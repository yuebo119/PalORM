using System.Data.Common;
using NHibernate;
using NHibernate.Cfg;
using NHibernate.Dialect;
using NHibernate.Driver;
using NHibernate.Linq;
using NHibernate.Mapping.ByCode;

namespace PalORM.PerfHub;

/// <summary>NHibernate 臂（清单条目 nhibernate，NHibernate 5.6.0 + mapping-by-code）。
/// <para>形态决策：
/// <list type="bullet">
/// <item><b>实体</b>：臂内 NhS1Row/NhEnumRow（非 sealed + virtual 成员——NHibernate 官方
/// POCO 契约，InvalidProxyTypeException 实测 2026-10-28）；共享 S1Row↔NhS1Row 每操作转换，
/// 转换成本 = 该协议下使用 NHibernate 的真实代价（报告注记）。</item>
/// <item><b>方言配对</b>：SQLite=SQLiteDialect+SQLite20Driver（System.Data.SQLite，5.6 无
/// Microsoft.Data.Sqlite 驱动，AotProbe 同配对）；MySQL=MySQLDialect+MySqlDataDriver
///（5.6 核心<MySqlConnectorDriver，MySql.Data 驱动——报告注记驱动差异）；PG=PostgreSQLDialect+NpgsqlDriver。</item>
/// <item><b>SQLite 连接串</b>：System.Data.SQLite 原生键（Data Source/Synchronous/Cache Size），
/// synchronous=NORMAL 与其他臂 PRAGMA 对齐（WAL 为文件级持久）。</item>
/// <item><b>会话形态</b>：读/单行写 = session-per-op（NH 官方 web 用法）；批量写 =
/// IStatelessSession（NH 官方 bulk 路径）+ BatchSize=100（adonet.batch_size 官方调优）。</item>
/// <item><b>UpsertBatch</b>：NH 无官方 upsert API——CreateSQLQuery 多值 UPSERT
///（BulkSql 真源，@pN→:pN 改写，raw SQL 通道注记）。</item>
/// <item><b>枚举列</b>：NhEnumRow.Status 为 string 列 + 每行 ToString（ADO/Dapper 地板同形）。</item>
/// </list></para></summary>
internal sealed class NHibernateImpl : IPerfImplementation, IDisposable
{
    private readonly Dialect _d;
    private readonly ISessionFactory _factory;

    public string Name => "NHibernate";

    /// <summary>NHibernate 官方 POCO 形态的 perf_s1 实体（virtual 成员 + 非 sealed）。</summary>
    public class NhS1Row
    {
        public virtual long Id { get; set; }

        public virtual string Name { get; set; } = "";

        public virtual int Qty { get; set; }

        public virtual decimal Price { get; set; }

        public virtual long Marker { get; set; }

    }

    /// <summary>perf_enum 实体——Status 落 TEXT（字符串存储，每行 ToString 转换）。</summary>
    public class NhEnumRow
    {
        public virtual long Id { get; set; }

        public virtual string Name { get; set; } = "";

        public virtual string Status { get; set; } = "";

    }

    private static S1Row ToShared(NhS1Row r) =>
        new() { Id = r.Id, Name = r.Name, Qty = r.Qty, Price = r.Price, Marker = r.Marker };

    private static NhS1Row FromNh(S1Row row) =>
        new() { Id = row.Id, Name = row.Name, Qty = row.Qty, Price = row.Price, Marker = row.Marker };

    private static NhEnumRow FromNhEnum(EnumRow row) =>
        new() { Id = row.Id, Name = row.Name, Status = row.Status.ToString() };

    public NHibernateImpl(DialectInfo dialect)
    {
        _d = dialect.Dialect;
        string cs = Connections.Resolve(dialect);
        var cfg = new Configuration();
        cfg.DataBaseIntegration(db =>
        {
            // adonet.batch_size 官方调优（方言支持时逐行 INSERT/UPDATE 合批；SQLite 驱动
            // 不支持时 NHibernate 自动退回逐行——形态由方言能力决定，不额外配置）
            db.BatchSize = 100;
            switch (_d)
            {
                case Dialect.Sqlite:
                    db.Dialect<SQLiteDialect>();
                    db.Driver<SQLite20Driver>();
                    db.ConnectionString = SqliteConnString(cs);
                    break;
                case Dialect.MySql:
                    db.Dialect<MySQLDialect>();
                    db.Driver<MySqlDataDriver>();
                    db.ConnectionString = cs;
                    break;
                case Dialect.PostgreSql:
                    db.Dialect<PostgreSQLDialect>();
                    db.Driver<NpgsqlDriver>();
                    db.ConnectionString = cs;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_d), _d, null);
            }
        });

        var mapper = new ModelMapper();
        mapper.Class<NhS1Row>(m =>
        {
            m.Table("`perf_s1`");
            m.Id(x => x.Id, id =>
            {
                id.Generator(Generators.Assigned);
                id.Column("`Id`");
            });
            m.Property(x => x.Name, p => p.Column("`Name`"));
            m.Property(x => x.Qty, p => p.Column("`Qty`"));
            m.Property(x => x.Price, p => p.Column("`Price`"));
            m.Property(x => x.Marker, p => p.Column("`Marker`"));
        });
        mapper.Class<NhEnumRow>(m =>
        {
            m.Table("`perf_enum`");
            m.Id(x => x.Id, id =>
            {
                id.Generator(Generators.Assigned);
                id.Column("`Id`");
            });
            m.Property(x => x.Name, p => p.Column("`Name`"));
            m.Property(x => x.Status, p => p.Column("`Status`"));
        });
        cfg.AddMapping(mapper.CompileMappingFor([typeof(NhS1Row), typeof(NhEnumRow)]));
        _factory = cfg.BuildSessionFactory();
    }

    /// <summary>从 Resolve 的 Microsoft 形态提取 Data Source 路径，重建 System.Data.SQLite
    /// 键集（Synchronous=Normal 与其他臂 PRAGMA 对齐；未知键会被其 ConnStringBuilder 拒绝）。</summary>
    private static string SqliteConnString(string cs)
    {
        foreach (string part in cs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase))
            {
                string path = part[(part.IndexOf('=') + 1)..];
                return $"Data Source={path};Synchronous=Normal;Cache Size=-65536;Default Timeout=30;Pooling=true";
            }
        }

        throw new InvalidOperationException($"SQLite 连接串不含 Data Source：{cs}");
    }

    public void Dispose() => _factory.Dispose();

    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct) => Task.CompletedTask;

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        NhS1Row? found = await session.GetAsync<NhS1Row>(id, ct).ConfigureAwait(false);
        return ToShared(found);
    }

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        List<NhS1Row> rows = await session.Query<NhS1Row>().ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(static r => ToShared(r)).ToList();
    }

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        using ITransaction tx = session.BeginTransaction();
        _ = await session.InsertAsync(FromNh(row), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量插入——StatelessSession 逐行 Insert + BatchSize=100
    ///（NH 官方 bulk 路径与调优；方言不支持批处理时驱动自动逐行）。</summary>
    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        using ITransaction tx = session.BeginTransaction();
        foreach (S1Row row in rows)
        {
            _ = await session.InsertAsync(FromNh(row), ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        using ITransaction tx = session.BeginTransaction();
        await session.UpdateAsync(FromNh(row), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return 1;
    }

    /// <summary>批量更新——StatelessSession 逐行 Update + BatchSize（官方 bulk 路径）。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        using ITransaction tx = session.BeginTransaction();
        foreach (S1Row row in rows)
        {
            await session.UpdateAsync(FromNh(row), ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary>批量删除——CreateSQLQuery 集合式 DELETE IN（1000/批对齐 BulkSql.BatchRows）。
    /// HQL 对嵌套类实体名解析不可用（"NhS1Row is not mapped"，2026-10-30 MySQL 冒烟实证），
    /// 原生 SQL 通道与 UpsertBatch 同路（raw SQL 注记）。</summary>
    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        string table = Dataset.Table(_d);
        string idCol = Dataset.Q(_d, "Id");
        long total = 0;
        const int batch = BulkSql.BatchRows;
        for (int start = 0; start < keys.Count; start += batch)
        {
            int end = Math.Min(start + batch, keys.Count);
            var ids = new List<long>(end - start);
            for (int i = start; i < end; i++)
            {
                ids.Add(Convert.ToInt64(keys[i], System.Globalization.CultureInfo.InvariantCulture));
            }

            using IStatelessSession session = _factory.OpenStatelessSession();
            using ITransaction tx = session.BeginTransaction();
            total += await session.CreateSQLQuery($"DELETE FROM {table} WHERE {idCol} IN (:ids)")
                .SetParameterList("ids", ids)
                .ExecuteUpdateAsync(ct)
                .ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return total;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => "NHibernate GetAsync<T>(id)（主键直查）";

    public string BuildComplexQuerySql(DbConnection conn)
        => "NHibernate LINQ（Query<T> Where/OrderBy/Take）";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        List<NhS1Row> rows = await session.Query<NhS1Row>()
            .Where(r => r.Id > lastId)
            .OrderBy(r => r.Id)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(static r => ToShared(r)).ToList();
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        // long[].Contains 的 LINQ 翻译失败（Evaluation failure on op_Implicit(Int64[])，
        // 2026-10-30 全量批实证）——List<long> 是 NHibernate LINQ 的受支持集合形态
        List<long> idList = [.. ids];
        using IStatelessSession session = _factory.OpenStatelessSession();
        List<NhS1Row> rows = await session.Query<NhS1Row>()
            .Where(r => idList.Contains(r.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(static r => ToShared(r)).ToList();
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        return await session.Query<NhS1Row>().LongCountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量 UPSERT——NH 无官方 API，CreateSQLQuery 多值 UPSERT（BulkSql 真源，
    /// @pN→:pN 改写；raw SQL 通道注记）。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        long total = 0;
        int batch = BulkSql.EffectiveBatchRows(_d);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            int len = end - start;
            string sql = BulkSql.UpsertBatch(_d, len).Replace("@p", ":p", StringComparison.Ordinal);
            using IStatelessSession session = _factory.OpenStatelessSession();
            using ITransaction tx = session.BeginTransaction();
            var query = session.CreateSQLQuery(sql);
            int p = 0;
            for (int r = start; r < end; r++)
            {
                S1Row row = rows[r];
                query.SetParameter($"p{p++}", row.Id);
                query.SetParameter($"p{p++}", row.Name);
                query.SetParameter($"p{p++}", row.Qty);
                query.SetParameter($"p{p++}", row.Price);
                query.SetParameter($"p{p++}", row.Marker);
            }

            _ = await query.ExecuteUpdateAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            total += len;
        }

        return total;
    }

    public Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
        => throw new NotSupportedException("InsertReturningId 不在本次子集内");

    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("WideQueryAll 不在本次子集内");

    public Task<(int Parents, int Children)> IncludeJoinAsync(DbConnection conn, int parentCount, CancellationToken ct)
        => throw new NotSupportedException("IncludeJoin 不在本次子集内");

    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        using ITransaction tx = session.BeginTransaction();
        _ = await session.InsertAsync(FromNh(row), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        using IStatelessSession session = _factory.OpenStatelessSession();
        using ITransaction tx = session.BeginTransaction();
        foreach (S1Row row in rows)
        {
            _ = await session.InsertAsync(FromNh(row), ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        using ITransaction tx = session.BeginTransaction();
        foreach (S1Row row in rows)
        {
            _ = await session.InsertAsync(FromNh(row), ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        using ITransaction tx = session.BeginTransaction();
        foreach (S1Row row in rows)
        {
            await session.UpdateAsync(FromNh(row), ct).ConfigureAwait(false);
        }

        await tx.RollbackAsync(ct).ConfigureAwait(false);
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

    /// <summary>枚举列事务内逐条插入——NhEnumRow 每行 ToString 转换 + StatelessSession
    /// 逐行 Insert（与 ADO/Dapper 地板形态同构）。</summary>
    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        using IStatelessSession session = _factory.OpenStatelessSession();
        using ITransaction tx = session.BeginTransaction();
        foreach (EnumRow row in rows)
        {
            _ = await session.InsertAsync(FromNhEnum(row), ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct) => QueryAllAsync(conn, ct);
}
