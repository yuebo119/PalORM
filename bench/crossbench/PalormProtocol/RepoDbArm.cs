using System.Data.Common;
using MySqlConnector;
using Npgsql;
using RepoDb;
using RepoDb.Interfaces;
using RepoDb.Options;

namespace PalORM.PerfHub;

/// <summary>RepoDB 臂——按 RepoDB 1.17.0 官方最佳实践与真实用户写法实现（2026-10-26）：
/// <list type="bullet">
/// <item><b>映射</b>：<c>FluentMapper.Entity&lt;T&gt;()</c> 零特性配置（表名 + 主键 + 枚举 PropertyHandler）。
/// S1Row/EnumRow 上的 PalORM 特性 RepoDB 不可见，与 FreeSql 臂的 <c>ConfigEntity</c> 同法同目标。</item>
/// <item><b>读路径</b>：<c>QueryAsync&lt;T&gt;</c> / <c>QueryAllAsync&lt;T&gt;</c> 走 RepoDB 的表达式解释器
/// （编译期构建行物化委托），传原生实体而非 DTO 投影。</item>
/// <item><b>批量</b>：Insert/Update/Merge 走官方 <c>*.BulkOperations</c> 扩展包（MySqlBulkCopy = LOAD DATA /
/// NpgsqlBinaryImporter = COPY BINARY，均经 pseudo 暂存表）；Delete 走 Core 的 <c>DeleteAll</c>（主键 IN）——
/// 同批实测 pseudo 表形态在 2000 行档慢 3.7~5.9×（异步 API 26.6ms 对 156.8ms；同步探针 42.6 对 156.2，
/// MySQL），故按操作取优而非统一用扩展包。
/// SQLite 无 BulkOperations 包，全部走 Core（InsertAll 多值 / UpdateAll / MergeAll→Upsert / DeleteAll），
/// 批宽按 <c>SqLiteDbSetting.MaxParameterCount=999</c> ÷ 5 列 = 199。</item>
/// <item><b>UPSERT</b>：MySQL/PG 用 <c>BulkMerge</c>；SQLite 用 <c>MergeAll</c>——其 DbSetting
/// <c>IsUseUpsert=true</c>，内部自动切 Upsert 路径（SQLite 的 CreateMerge 本身抛 NotSupported，
/// 由 MergeAll 的 IsUseUpsert 分支绕过）。</item>
/// <item><b>事务</b>：扩展方法直接接 <c>IDbTransaction</c>（BulkOperations 要求 provider 具体事务类型），
/// 与 ADO 地板同形态——无需 FreeSql 那样的 WithTransaction 绑定。</item>
/// <item><b>连接</b>：用传入的共享连接（Dapper 臂口径）。入口的 <see cref="CountingConnection.Unwrap"/>
/// 是对 PerfHub 并行测量路径（会包装 CountingConnection）的防御——CrossBench 的 Measure 传的是裸驱动
/// 连接，此处实为直通；BulkOperations 的 CAST 前必须先解包，故保留。</item>
/// <item><b>读路径分配</b>（2026-10-26 审计修正）：主键直查用 <c>QueryFirstAsync&lt;T, TWhat&gt;</c>
/// （QueryAsync + FirstOrDefault 会先聚成 List）；<c>QueryAllAsync</c> 返回的已是 List，
/// 直接复用不复制——两条都对齐 Dapper 臂的 <c>QueryFirstOrDefaultAsync</c> / <c>AsList()</c> 口径。</item>
/// </list></summary>
internal sealed class RepoDbArm : IPerfImplementation
{
    private const string S1Table = "perf_s1";
    private const string EnumTable = "perf_enum";

    /// <summary>SQLite 单语句参数上限 999（<c>SqLiteDbSetting.MaxParameterCount</c>）× 5 列 = 199 行/批，
    /// 与 ADO 地板的 <c>BulkSql.EffectiveBatchRows</c> 同口径。</summary>
    private const int SqliteBatchRows = 199;

    /// <summary>MySQL/PG 的 Core 批宽。PG 的 <c>MaxParameterCount=2098</c>（5 列 → 419 行/批），
    /// 传 419 避免 RepoDB 内部再按上限拆；MySQL 的 65535 上限下 1000 行/批与 ADO 地板同档。</summary>
    private static int RemoteBatchRows(Dialect d) => d == Dialect.PostgreSql ? 419 : 1000;

    private readonly Dialect _dialect;

    /// <summary>进程级注册：provider 的 DbSetting/DbHelper/StatementBuilder 映射 + 实体 Fluent 映射。
    /// 静态构造只跑一次，避免逐实例重复注册（RepoDB 的 Add 默认 overwrite=false）。</summary>
    static RepoDbArm()
    {
        GlobalConfiguration.Setup()
            .UseSqlite()
            .UseMySqlConnector()
            .UsePostgreSql();

        FluentMapper.Entity<S1Row>()
            .Table(S1Table)
            .Primary(e => e.Id);

        // 枚举列：perf_enum.Status 三方言均为 TEXT，用 PropertyHandler 显式做 enum↔string
        // （RepoDB 默认 EnumDefaultDatabaseType=String 只给了 DbType，不给值转换）
        FluentMapper.Entity<EnumRow>()
            .Table(EnumTable)
            .Primary(e => e.Id)
            .PropertyHandler<OrderStatusStringHandler>(e => e.Status);
    }

    public RepoDbArm(DialectInfo dialect)
    {
        _dialect = dialect.Dialect;

        // RepoDB 的全局缓存假定"一进程一方言"，键里不含 connection / provider：
        // ① CommandTextCache 的键只组合 QueryRequest 类型 + 表名 + 字段 + 条件 + 排序 + Top + Hints
        //    ——同一 (表名, 条件) 会命中先到方言的 SQL 文本（实测 SQLite 先跑后 MySQL 的 GetByKey
        //    拿到 `SELECT [Id] ... FROM [perf_s1]` 的方括号转义并报 MySQL 语法错）；
        // ② 六个 ExecutionContextCache（Insert / InsertAll / Update / UpdateAll / Merge / MergeAll）
        //    缓存编译好的参数绑定委托，其中嵌入了 provider 的 DbHelper.DynamicHandler——MySQL 复用
        //    了 SQLite 编好的委托，`MySqlParameter` 被强转 `SqliteParameter` 抛 InvalidCastException
        //    （实测全量批 MySQL 的 Update 处炸）。六个必须全清，漏一个就漏一条路径。
        // Runner 每方言只创建一个臂实例，正是方言边界，故在此清空全部七个缓存。
        CommandTextCache.Flush();
        RepoDb.Contexts.Cachers.InsertExecutionContextCache.Flush();
        RepoDb.Contexts.Cachers.InsertAllExecutionContextCache.Flush();
        RepoDb.Contexts.Cachers.UpdateExecutionContextCache.Flush();
        RepoDb.Contexts.Cachers.UpdateAllExecutionContextCache.Flush();
        RepoDb.Contexts.Cachers.MergeExecutionContextCache.Flush();
        RepoDb.Contexts.Cachers.MergeAllExecutionContextCache.Flush();
    }

    public string Name => "RepoDb";

    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct) => Task.CompletedTask;

    // ── 读路径 ──

    /// <summary>主键直查——用显式 <c>QueryField</c>，不用 key 直传 <c>QueryFirstAsync&lt;T, TWhat&gt;(id)</c>。
    /// <para><b>为什么</b>：key 直传（RepoDB 文档的主键查询示例）内部先
    /// <c>GetAndGuardPrimaryKeyOrIdentityKey</c> 解析主键字段名（PrimaryCache 与 PropertyCache 各一次
    /// 查找 + 主键守卫）；显式 QueryField 直接给定 <c>WHERE Id = @Id</c>，跳过这一步。同表同连接
    /// 600 次中位实测：SQLite 22.4µs 对 27.1µs（快 17%），MySQL 296 对 305µs，PG 297 对 298µs
    /// （联网方言被往返掩盖）。语义等价，均为参数化主键等式。</para>
    /// <para>未命中时 QueryFirst 抛 EmptyException，catch 后返回 null 与其他臂语义对齐。</para></summary>
    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        try
        {
            return await Db(conn).QueryFirstAsync<S1Row>(new QueryField("Id", id), cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (RepoDb.Exceptions.EmptyException)
        {
            return null;
        }
    }

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
        => AsList(await Db(conn).QueryAllAsync<S1Row>(cancellationToken: ct).ConfigureAwait(false));

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    // ── 单行写 ──

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        _ = await Db(conn).InsertAsync(row, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        int affected = await Db(conn).UpdateAsync(row, cancellationToken: ct).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException($"RepoDB Update affected {affected} for Id={row.Id}");
        }

        return affected;
    }

    // ── 批量（方言分流 + 按操作取优路径，见类注释）──

    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        DbConnection db = Db(conn);
        switch (_dialect)
        {
            case Dialect.Sqlite:
                return await db.InsertAllAsync(rows, batchSize: SqliteBatchRows, cancellationToken: ct)
                    .ConfigureAwait(false);
            case Dialect.MySql:
                return await ((MySqlConnection)db).BulkInsertAsync(rows, cancellationToken: ct).ConfigureAwait(false);
            case Dialect.PostgreSql:
                return await ((NpgsqlConnection)db).BulkInsertAsync(rows, cancellationToken: ct).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(_dialect), _dialect, null);
        }
    }

    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        DbConnection db = Db(conn);
        switch (_dialect)
        {
            case Dialect.Sqlite:
                // SQLite 无 provider 批量更新扩展；UpdateAll 按主键逐条是 RepoDB 在该方言的唯一路径
                return await db.UpdateAllAsync(rows, cancellationToken: ct).ConfigureAwait(false);
            case Dialect.MySql:
                return await ((MySqlConnection)db).BulkUpdateAsync(rows, cancellationToken: ct).ConfigureAwait(false);
            case Dialect.PostgreSql:
                // PG 的 BulkUpdate 在 1.17.0 有上游缺陷：perf_s1 的 Id 非 identity（identityField=null），
                // GetUpdateCommandText 内 identityField.GetHashCode() 未判空 → NRE。映射层无法规避
                //（标 Identity 会让 INSERT 跳过 Id 列），故走 Core 的 UpdateAll。
                return await db.UpdateAllAsync(rows, cancellationToken: ct).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(_dialect), _dialect, null);
        }
    }

    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        // 全方言走 Core 的 DeleteAll（主键 IN）——MySQL 同批实测比 BulkDeleteByKey 的 pseudo 表形态
        // 快 5.9×（26.6ms vs 156.8ms @2000 行），删除无需暂存表的行级匹配
        return await Db(conn).DeleteAllAsync<S1Row>(keys, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        DbConnection db = Db(conn);
        switch (_dialect)
        {
            case Dialect.Sqlite:
                // MergeAll + SqLiteDbSetting.IsUseUpsert=true → 内部走 UpsertAll（INSERT ... ON CONFLICT 形态）
                return await db.MergeAllAsync(rows, batchSize: SqliteBatchRows, cancellationToken: ct)
                    .ConfigureAwait(false);
            case Dialect.MySql:
                return await ((MySqlConnection)db).BulkMergeAsync(rows, cancellationToken: ct).ConfigureAwait(false);
            case Dialect.PostgreSql:
                // 同 BulkUpdate：BulkMerge 的 GetMergeCommandText 也在 identityField=null 时 NRE（1.17.0 上游缺陷）
                return await db.MergeAllAsync(rows, batchSize: RemoteBatchRows(_dialect), cancellationToken: ct)
                    .ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(_dialect), _dialect, null);
        }
    }

    // ── SQL 构建（不执行；CrossBench 操作集不含 Build 组，仅满足接口）──

    /// <summary>CrossBench 操作集不含 Build 组（纯 SQL 构建开销单项），与 FreeSql 臂的
    /// 未覆盖项同口径显式抛——返回空串会让未来接入的 Build 项静默拿到空结果。</summary>
    public string BuildGetByKeySql(DbConnection conn, long id)
        => throw new NotSupportedException("Build 组不在 CrossBench 子集内");

    public string BuildComplexQuerySql(DbConnection conn)
        => throw new NotSupportedException("Build 组不在 CrossBench 子集内");

    // ── 查询族 ──

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        IEnumerable<S1Row> rows = await Db(conn).QueryAsync<S1Row>(
            where: e => e.Id > lastId,
            orderBy: [new OrderField("Id", RepoDb.Enumerations.Order.Ascending)],
            top: pageSize,
            cancellationToken: ct).ConfigureAwait(false);
        return [.. rows];
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        IEnumerable<S1Row> rows = await Db(conn).QueryAsync<S1Row>(
            where: e => ids.Contains(e.Id),
            cancellationToken: ct).ConfigureAwait(false);
        return [.. rows];
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => await Db(conn).CountAllAsync<S1Row>(cancellationToken: ct).ConfigureAwait(false);

    public Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
        => throw new NotSupportedException("InsertReturningId 不在本次子集内");

    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("WideQueryAll 不在本次子集内");

    public Task<(int Parents, int Children)> IncludeJoinAsync(
        DbConnection conn, int parentCount, CancellationToken ct)
        => throw new NotSupportedException("IncludeJoin 不在本次子集内");

    // ── 事务族 ──

    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        DbConnection db = Db(conn);
        await using DbTransaction tran = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            _ = await db.InsertAsync(row, transaction: tran, cancellationToken: ct).ConfigureAwait(false);
            await tran.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tran.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        DbConnection db = Db(conn);
        await using DbTransaction tran = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (S1Row row in rows)
            {
                _ = await db.InsertAsync(row, transaction: tran, cancellationToken: ct).ConfigureAwait(false);
            }

            await tran.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tran.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        DbConnection db = Db(conn);
        await using DbTransaction tran = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            switch (_dialect)
            {
                case Dialect.Sqlite:
                    _ = await db.InsertAllAsync(
                        rows, batchSize: SqliteBatchRows, transaction: tran, cancellationToken: ct)
                        .ConfigureAwait(false);
                    break;
                case Dialect.MySql:
                    _ = await ((MySqlConnection)db).BulkInsertAsync(
                        rows, transaction: (MySqlTransaction)tran, cancellationToken: ct).ConfigureAwait(false);
                    break;
                case Dialect.PostgreSql:
                    _ = await ((NpgsqlConnection)db).BulkInsertAsync(
                        rows, transaction: (NpgsqlTransaction)tran, cancellationToken: ct).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_dialect), _dialect, null);
            }

            await tran.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tran.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        DbConnection db = Db(conn);
        await using DbTransaction tran = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (S1Row row in rows)
            {
                _ = await db.UpdateAsync(row, transaction: tran, cancellationToken: ct).ConfigureAwait(false);
            }

            await tran.RollbackAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tran.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        DbConnection db = Db(conn);
        await using DbTransaction tran = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (EnumRow row in rows)
            {
                _ = await db.InsertAsync(row, transaction: tran, cancellationToken: ct).ConfigureAwait(false);
            }

            await tran.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tran.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct)
        => AsList(await Db(conn).QueryAllAsync<S1Row>(cancellationToken: ct).ConfigureAwait(false));

    // ── 覆盖范围外（与 FreeSql 臂同口径）──

    public Task<long> TenantCountAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("租户族不在本次子集内");

    public Task<long> TenantCountWhereAsync(DbConnection conn, int rows, CancellationToken ct)
        => throw new NotSupportedException("租户族不在本次子集内");

    public Task<List<BenchTenantPost>> TenantGetAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("租户族不在本次子集内");

    public Task<List<BenchTenantPost>> OwnedJsonQueryAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("OwnedJson 不在本次子集内");

    public Task<int> SessionBatchInsertsAsync(DbConnection conn, int offset, CancellationToken ct)
        => throw new NotSupportedException("SessionBatch 不在本次子集内");

    /// <summary>脱掉计数装饰器（防御性直通）。真正必须解包的是 BulkOperations 的 CAST：
    /// 它们要求驱动具体连接类型。</summary>
    private static DbConnection Db(DbConnection conn) => CountingConnection.Unwrap(conn);

    /// <summary>RepoDB 的 <c>QueryAllAsync</c> 内部已聚成 List——直接复用，避免 <c>[.. rows]</c>
    /// 再复制一遍（20k 行约 160KB 额外分配）。Dapper 臂用 <c>AsList()</c> 同口径。</summary>
    private static List<S1Row> AsList(IEnumerable<S1Row> rows) => rows as List<S1Row> ?? [.. rows];
}

/// <summary>枚举 ↔ TEXT 的 PropertyHandler——perf_enum.Status 三方言均为 TEXT NOT NULL；
/// RepoDB 的 <c>EnumDefaultDatabaseType</c> 只影响 DbType 不转换值，故显式挂 handler。</summary>
internal sealed class OrderStatusStringHandler : IPropertyHandler<string, OrderStatus>
{
    public OrderStatus Get(string input, PropertyHandlerGetOptions options)
        => Enum.Parse<OrderStatus>(input);

    public string Set(OrderStatus input, PropertyHandlerSetOptions options)
        => input.ToString();
}
