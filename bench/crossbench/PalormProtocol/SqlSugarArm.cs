using System.Data.Common;
using SqlSugar;

namespace PalORM.PerfHub;

/// <summary>独立物理表声明：共享表的列名约定与该组驱动不兼容时，用一组专属表隔离。
/// <para>目前只有 SqlSugar + PG 使用（该组硬编码小写引号标识符，与 CamelCase 引号 DDL 冲突）；
/// 其余组返回 null，走共享表，快照与复位逻辑不变。</para></summary>
internal interface ISeparateTableArm
{
    /// <summary>该方言下 S1 表的物理名；null = 用共享 perf_s1。</summary>
    string? S1TableFor(Dialect dialect);

    /// <summary>该方言下枚举表的物理名；null = 用共享 perf_enum。</summary>
    string? EnumTableFor(Dialect dialect);
}

/// <summary>SqlSugar 臂（清单条目 sqlsugar，SqlSugarCore 5.1.4.221）。
/// <para>形态决策：
/// <list type="bullet">
/// <item><b>映射</b>：ConfigureExternalServices 的 EntityNameService/EntityService 零特性
/// Fluent 配置（SqlSugar 文档对"无法加特性的 POCO"的标准做法）——S1Row 固定映射共享名
/// perf_s1（属性名即列名，Id 主键非自增）；枚举行用臂内 SsEnumRow（Status 为 string 列，
/// 每行 ToString 转换——SqlSugar 无官方枚举→字符串列开关，ADO/Dapper 地板同形）。
/// <b>映射名必须固定</b>：SqlSugar 实体映射按类型做进程级静态缓存（探针实证：第二个
/// client 复用第一个 client 的表名），映射随方言变化会把前一方言的名字泄漏给后一方言。
/// 表名差异用查询/写入期的 <c>.AS()</c>/<c>.As()</c> 覆写表达（五条写路径 + 查询全支持，
/// 逐路径探针实证）。</item>
/// <item><b>PG 独立小写表</b>（2026-11-01，探针 16/16 实证）：SqlSugar 的 PG 通道把标识符
/// 折叠小写后再加引号，共享表的 CamelCase 引号列名必然 42703；该方言全部操作经
/// <see cref="TableSuffix"/> 指向 perf_s1_lc / perf_enum_lc（列名不加引号的小写 DDL，
/// 由夹具同步维护快照与复位），MySQL/SQLite 列名大小写不敏感，后缀为空走共享表。</item>
/// <item><b>MySQL 驱动</b>：DbType.MySqlConnector（SqlSugar 原生 MySqlConnector 通道，
/// 与其他臂驱动对齐；连接串附加 AllowLoadLocalInfile 供 Fastest 的 LOAD DATA 路径）。</item>
/// <item><b>批量</b>：db.Fastest&lt;T&gt;()（官方最快路径——MySQL LOAD DATA / PG COPY /
/// SQLite 批量）；Fastest 运行时不支持的场景一次性回退官方集合 API（Insertable/Updateable
/// 列表重载）并保持该方言后续走回退路径。</item>
/// <item><b>UpsertBatch</b>：db.Storageable（SqlSugar 官方 upsert API）。</item>
/// <item><b>SQLite PRAGMA</b>：SqlSugar 自管连接无打开钩子——SetupAsync 一次性注入，
/// 文件级 WAL 持久；逐连接 PRAGMA 差异在报告环境节注记。</item>
/// </list></para></summary>
internal sealed class SqlSugarImpl : IPerfImplementation, IDisposable, ISeparateTableArm
{
    private readonly Dialect _d;
    private readonly SqlSugarClient _db;
    private readonly string _tableSuffix;
    private bool _fastestInsertOk = true;
    private bool _fastestUpdateOk = true;

    public string Name => "SqlSugar";

    public SqlSugarImpl(DialectInfo dialect)
    {
        _d = dialect.Dialect;
        _tableSuffix = _d == Dialect.PostgreSql ? "_lc" : "";
        string cs = Connections.Resolve(dialect);
        if (_d == Dialect.MySql)
        {
            // Fastest 的 MySQL BulkCopy 走 LOAD DATA LOCAL INFILE（SqlSugar 官方实现），
            // 驱动侧需显式允许（与 FreeSql/RepoDb 臂同要求）
            cs += cs.Contains("AllowLoadLocalInfile", StringComparison.OrdinalIgnoreCase)
                ? ""
                : ";AllowLoadLocalInfile=true";
        }

        // 映射固定共享名（静态缓存钉名，探针实证跨 client 泄漏）；目标表经 AS 覆写指向
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = cs,
            DbType = MapDbType(_d),
            IsAutoCloseConnection = true,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (type, entity) =>
                {
                    if (type == typeof(S1Row))
                    {
                        entity.DbTableName = "perf_s1";
                    }
                    else if (type == typeof(SsEnumRow))
                    {
                        entity.DbTableName = "perf_enum";
                    }
                },
                EntityService = (property, column) =>
                {
                    if (property.Name == "Id")
                    {
                        // 显式主键非自增（S1Row/SsEnumRow 同形态）
                        column.IsPrimarykey = true;
                        column.IsIdentity = false;
                    }

                    if (_d == Dialect.PostgreSql)
                    {
                        // 小写列不加引号：PG 未加引号标识符折叠小写（perf_s1_lc 的列名约定）
                        column.DbColumnName = property.Name.ToLowerInvariant();
                    }
                },
            },
        });
    }

    /// <summary>本方言的 S1 表物理名（PG=perf_s1_lc，其余=共享 perf_s1）——AS 覆写目标。</summary>
    private string S1Table => _d == Dialect.PostgreSql ? Dataset.S1LowercaseTableName : "perf_s1";

    /// <summary>本方言的枚举表物理名。</summary>
    private string EnumTable => _d == Dialect.PostgreSql ? Dataset.EnumLowercaseTableName : "perf_enum";

    /// <summary>PG 用独立小写表：夹具据此把该组的快照/复位落到 perf_s1_lc，其余组仍走 perf_s1。</summary>
    public string? S1TableFor(Dialect dialect)
        => dialect == Dialect.PostgreSql ? Dataset.S1LowercaseTableName : null;

    public string? EnumTableFor(Dialect dialect)
        => dialect == Dialect.PostgreSql ? Dataset.EnumLowercaseTableName : null;

    private static DbType MapDbType(Dialect d) => d switch
    {
        Dialect.Sqlite => DbType.Sqlite,
        Dialect.MySql => DbType.MySqlConnector,
        Dialect.PostgreSql => DbType.PostgreSQL,
        _ => throw new ArgumentOutOfRangeException(nameof(d), d, null)
    };

    /// <summary>枚举行臂内实体——Status 落 TEXT（字符串存储），转换成本显式计入
    ///（与 ADO 地板逐行 ToString 同形）。</summary>
    internal sealed class SsEnumRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public string Status { get; set; } = "";

        internal static SsEnumRow From(EnumRow row) =>
            new() { Id = row.Id, Name = row.Name, Status = row.Status.ToString() };
    }

    public void Dispose() => _db.Dispose();

    public async Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
    {
        // SQLite：一次性注入与 ADO/PalORM 臂同款 PRAGMA（文件级 WAL 持久；
        // 逐连接项在 SqlSugar 自管连接上的覆盖度见类注释与报告环境节）
        if (_d == Dialect.Sqlite)
        {
            _ = await _db.Ado.ExecuteCommandAsync(Program.Pragma).ConfigureAwait(false);
        }
    }

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
        => await _db.Queryable<S1Row>().AS(S1Table).FirstAsync(x => x.Id == id).ConfigureAwait(false);

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
        => await _db.Queryable<S1Row>().AS(S1Table).ToListAsync().ConfigureAwait(false);

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        int affected = await _db.Insertable(row).AS(S1Table).ExecuteCommandAsync().ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException($"SqlSugar Insert affected {affected}");
        }
    }

    /// <summary>批量插入——Fastest BulkCopy（官方最快路径）；不支持时回退 Insertable 列表重载。</summary>
    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_fastestInsertOk)
        {
            try
            {
                _ = await _db.Fastest<S1Row>().AS(S1Table).BulkCopyAsync(rows.ToList()).ConfigureAwait(false);
                return rows.Count;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _fastestInsertOk = false;
            }
        }

        return await _db.Insertable(rows.ToList()).AS(S1Table).ExecuteCommandAsync().ConfigureAwait(false);
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        int affected = await _db.Updateable(row).AS(S1Table).ExecuteCommandAsync().ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException($"SqlSugar Update affected {affected} for Id={row.Id}");
        }

        return affected;
    }

    /// <summary>批量更新——Fastest BulkUpdate（官方最快路径，MySQL/PG 支持；SQLite 不支持时
    /// 回退 Updateable 列表重载=逐行裹批）。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_fastestUpdateOk)
        {
            try
            {
                _ = await _db.Fastest<S1Row>().AS(S1Table).BulkUpdateAsync(rows.ToList()).ConfigureAwait(false);
                return rows.Count;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _fastestUpdateOk = false;
            }
        }

        return await _db.Updateable(rows.ToList()).AS(S1Table).ExecuteCommandAsync().ConfigureAwait(false);
    }

    /// <summary>批量删除——Deleteable.In（官方集合式 IN 删除，SqlSugar 自动按参数上限分批）。</summary>
    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        long[] ids = new long[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            ids[i] = Convert.ToInt64(keys[i], System.Globalization.CultureInfo.InvariantCulture);
        }

        return await _db.Deleteable<S1Row>().AS(S1Table).In(ids).ExecuteCommandAsync().ConfigureAwait(false);
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => "SqlSugar Queryable 翻译（First(Id == key)）";

    public string BuildComplexQuerySql(DbConnection conn)
        => "SqlSugar Queryable 翻译（Where/OrderBy/Take 组合）";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        => await _db.Queryable<S1Row>().AS(S1Table)
            .Where(x => x.Id > lastId)
            .OrderBy(x => x.Id, OrderByType.Asc)
            .Take(pageSize)
            .ToListAsync()
            .ConfigureAwait(false);

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
        => await _db.Queryable<S1Row>().AS(S1Table).Where(x => ids.Contains(x.Id)).ToListAsync().ConfigureAwait(false);

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => await _db.Queryable<S1Row>().AS(S1Table).CountAsync().ConfigureAwait(false);

    /// <summary>批量 UPSERT——Storageable（SqlSugar 官方 upsert API：按主键分派 insert/update）。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => await _db.Storageable(rows.ToList()).As(S1Table).ExecuteCommandAsync().ConfigureAwait(false);

    public Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
        => throw new NotSupportedException("InsertReturningId 不在本次子集内");

    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("WideQueryAll 不在本次子集内");

    public Task<(int Parents, int Children)> IncludeJoinAsync(DbConnection conn, int parentCount, CancellationToken ct)
        => throw new NotSupportedException("IncludeJoin 不在本次子集内");

    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        try
        {
            await _db.Ado.BeginTranAsync().ConfigureAwait(false);
            _ = await _db.Insertable(row).AS(S1Table).ExecuteCommandAsync().ConfigureAwait(false);
            await _db.Ado.CommitTranAsync().ConfigureAwait(false);
        }
        catch
        {
            await _db.Ado.RollbackTranAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        try
        {
            await _db.Ado.BeginTranAsync().ConfigureAwait(false);
            foreach (S1Row row in rows)
            {
                _ = await _db.Insertable(row).AS(S1Table).ExecuteCommandAsync().ConfigureAwait(false);
            }

            await _db.Ado.CommitTranAsync().ConfigureAwait(false);
        }
        catch
        {
            await _db.Ado.RollbackTranAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        try
        {
            await _db.Ado.BeginTranAsync().ConfigureAwait(false);
            _ = await _db.Insertable(rows.ToList()).AS(S1Table).ExecuteCommandAsync().ConfigureAwait(false);
            await _db.Ado.CommitTranAsync().ConfigureAwait(false);
        }
        catch
        {
            await _db.Ado.RollbackTranAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        try
        {
            await _db.Ado.BeginTranAsync().ConfigureAwait(false);
            foreach (S1Row row in rows)
            {
                _ = await _db.Updateable(row).AS(S1Table).ExecuteCommandAsync().ConfigureAwait(false);
            }

            await _db.Ado.RollbackTranAsync().ConfigureAwait(false);
        }
        catch
        {
            await _db.Ado.RollbackTranAsync().ConfigureAwait(false);
            throw;
        }
    }

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

    /// <summary>枚举列事务内逐条插入——SsEnumRow 每行 ToString 转换 + Insertable 单行
    ///（与 ADO/Dapper 地板形态同构）。</summary>
    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        try
        {
            await _db.Ado.BeginTranAsync().ConfigureAwait(false);
            foreach (EnumRow row in rows)
            {
                _ = await _db.Insertable(SsEnumRow.From(row)).AS(EnumTable).ExecuteCommandAsync().ConfigureAwait(false);
            }

            await _db.Ado.CommitTranAsync().ConfigureAwait(false);
        }
        catch
        {
            await _db.Ado.RollbackTranAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct) => QueryAllAsync(conn, ct);
}
