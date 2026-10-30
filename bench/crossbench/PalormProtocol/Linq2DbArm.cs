using System.Data.Common;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace PalORM.PerfHub;

/// <summary>linq2db 臂（清单条目 linq2db 6.5.1）。
/// <para>形态决策：
/// <list type="bullet">
/// <item><b>连接</b>：臂级持久 <c>DataConnection</c>（provider 名 + 连接串构造，首次使用时打开、
/// 臂 Dispose 时释放）——与其他臂"单连接/单池"的连接形态对齐。MySQL 走
/// <c>linq2db.Mysql</c>（MySql.Data 驱动）：nuget.org 对本区域 302 到 azure.cn 半量镜像，
/// linq2db.MySqlConnector 在镜像 BlobNotFound（2026-10-30 实测），驱动差异在报告注记。</item>
/// <item><b>映射</b>：FluentMappingBuilder 映射共享实体 S1Row/EnumRow（零特性侵入，
/// 属性名即列名，Id 主键非自增；Status 枚举列 HasConversion 字符串存储）。</item>
/// <item><b>异步 API 面</b>：linq2db 6 移除了 FirstOrDefaultAsync/LongCountAsync 等并行异步
/// 扩展（查询异步 = ToListAsync 家族）——单键直查用 Where+Take(1)+ToListAsync（LIMIT 1），
/// Count 用同步 LongCount()（COUNT(*) 翻译，Task.FromResult 包装，语句形态与三臂一致）。</item>
/// <item><b>批量</b>：BulkCopy 走 provider 原生（PG BinaryImport / SQLite 多值）；
/// MySQL 显式 MultipleRows（MySqlBulkLoader 需 server local_infile + 连接串开关，
/// 与其他臂的 LOAD DATA 路径不同源会引入服务器配置依赖，多值形态无配置依赖）。</item>
/// <item><b>BulkUpdate/UpsertBatch</b>：PG/MySQL 走官方 Merge API（OnTargetKey 主键匹配）；
/// SQLite 无 Merge 语义——BulkUpdate 逐行裹事务（与 Dapper 家族 SQLite 路径同形）、
/// UpsertBatch 逐行 InsertOrReplace 裹事务。</item>
/// </list></para></summary>
internal sealed class Linq2DbImpl(DialectInfo dialect) : IPerfImplementation, IDisposable
{
    private readonly Dialect _d = dialect.Dialect;
    private readonly MappingSchema _schema = BuildSchema();
    private DataConnection? _db;
    private System.Data.Common.DbConnection? _ownedConn;
    private readonly string _cs = Connections.Resolve(dialect);

    public string Name => "Linq2Db";

    private static MappingSchema BuildSchema()
    {
        var schema = new MappingSchema();
        var builder = new FluentMappingBuilder(schema);
        builder.Entity<S1Row>()
            .HasTableName("perf_s1")
            .Property(x => x.Id).IsPrimaryKey();
        builder.Entity<EnumRow>()
            .HasTableName("perf_enum")
            .Property(x => x.Id).IsPrimaryKey()
            .Property(x => x.Status).HasConversion(
                s => s.ToString(),
                v => Enum.Parse<OrderStatus>(v, true));
        builder.Build();
        return schema;
    }

    private DataConnection Db
    {
        get
        {
            if (_db is null)
            {
                string provider = _d switch
                {
                    Dialect.Sqlite => ProviderName.SQLiteMS,
                    Dialect.MySql => ProviderName.MySql,
                    Dialect.PostgreSql => ProviderName.PostgreSQL,
                    _ => throw new ArgumentOutOfRangeException(nameof(_d), _d, null)
                };

                // 连接串构造 + 臂级持久 DataConnection：连接在首次使用时打开、臂 Dispose 时释放
                //（查询路径实测与其他臂同量级=连接复用成立）；SQLite 单行 Insert 的独有慢路径
                //（1295µs/行 vs Dapper 28µs）在报告异常节单列归因，不在此臆改
                _db = new DataConnection(provider, _cs);
                _db.AddMappingSchema(_schema);
            }

            return _db;
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
        _ownedConn?.Dispose();
    }

    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct) => Task.CompletedTask;

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        List<S1Row> rows = await Db.GetTable<S1Row>()
            .Where(x => x.Id == id)
            .Take(1)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
        => await Db.GetTable<S1Row>().ToListAsync(ct).ConfigureAwait(false);

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await Db.InsertAsync(row, token: ct).ConfigureAwait(false);

    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        var options = new BulkCopyOptions
        {
            BulkCopyType = _d == Dialect.MySql ? BulkCopyType.MultipleRows : BulkCopyType.Default,
        };
        await Db.BulkCopyAsync(options, rows, ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await Db.UpdateAsync(row, token: ct).ConfigureAwait(false);

    /// <summary>批量更新——PG 官方 Merge；MySQL provider 无 MERGE 支持
    ///（"MySql80 provider doesn't support SQL MERGE"，2026-10-30 冒烟实证）改走
    /// BulkSql CASE WHEN 多值 raw SQL（DataParameter 绑定，库自身命令通道）；SQLite 逐行裹事务。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_d == Dialect.Sqlite)
        {
            using DataConnectionTransaction tx = await Db.BeginTransactionAsync(ct).ConfigureAwait(false);
            long total = 0;
            foreach (S1Row row in rows)
            {
                total += await Db.UpdateAsync(row, token: ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return total;
        }

        if (_d == Dialect.MySql)
        {
            long total = 0;
            int batch = BulkSql.EffectiveBatchRows(_d);
            for (int start = 0; start < rows.Count; start += batch)
            {
                int end = Math.Min(start + batch, rows.Count);
                total += await Db.ExecuteAsync(
                    BulkSql.UpdateBatch(_d, end - start), ct, UpdateParameters(rows, start, end))
                    .ConfigureAwait(false);
            }

            return total;
        }

        int merged = await Db.GetTable<S1Row>()
            .Merge()
            .Using(rows)
            .OnTargetKey()
            .UpdateWhenMatched()
            .MergeAsync(ct)
            .ConfigureAwait(false);
        return merged;
    }

    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        using DataConnectionTransaction tx = await Db.BeginTransactionAsync(ct).ConfigureAwait(false);
        long total = 0;
        const int batch = BulkSql.BatchRows;
        for (int start = 0; start < keys.Count; start += batch)
        {
            int end = Math.Min(start + batch, keys.Count);
            long[] ids = new long[end - start];
            for (int i = start; i < end; i++)
            {
                ids[i - start] = Convert.ToInt64(keys[i], System.Globalization.CultureInfo.InvariantCulture);
            }

            total += await Db.GetTable<S1Row>().Where(x => ids.Contains(x.Id)).DeleteAsync(ct)
                .ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return total;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => "linq2db LINQ 翻译（GetTable<S1Row>().Where(Id == key).Take(1)）";

    public string BuildComplexQuerySql(DbConnection conn)
        => "linq2db LINQ 翻译（Where/OrderBy/Take 组合）";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        => await Db.GetTable<S1Row>()
            .Where(x => x.Id > lastId)
            .OrderBy(x => x.Id)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
        => await Db.GetTable<S1Row>().Where(x => ids.Contains(x.Id)).ToListAsync(ct).ConfigureAwait(false);

    /// <summary>Count——linq2db 6 无异步 Count 扩展，同步 LongCount() 翻译 COUNT(*)
    /// （与三臂同语句形态；Task.FromResult 包装避免伪 async）。</summary>
    public Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => Task.FromResult(Db.GetTable<S1Row>().LongCount());

    /// <summary>批量 UPSERT——PG/MySQL 官方 Merge（UpdateWhenMatched + InsertWhenNotMatched）；
    /// SQLite 逐行 InsertOrReplace 裹事务（库无 SQLite Merge 语义）。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_d == Dialect.Sqlite)
        {
            using DataConnectionTransaction tx = await Db.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (S1Row row in rows)
            {
                await Db.InsertOrReplaceAsync(row, token: ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return rows.Count;
        }

        if (_d == Dialect.MySql)
        {
            // MySQL provider 无 MERGE（同 BulkUpdate 注记）——多值 UPSERT raw SQL 通道
            using DataConnectionTransaction tx = await Db.BeginTransactionAsync(ct).ConfigureAwait(false);
            long total = 0;
            int batch = BulkSql.EffectiveBatchRows(_d);
            for (int start = 0; start < rows.Count; start += batch)
            {
                int end = Math.Min(start + batch, rows.Count);
                total += await Db.ExecuteAsync(
                    BulkSql.UpsertBatch(_d, end - start), ct, InsertParameters(rows, start, end))
                    .ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return total;
        }

        int merged = await Db.GetTable<S1Row>()
            .Merge()
            .Using(rows)
            .OnTargetKey()
            .UpdateWhenMatched()
            .InsertWhenNotMatched()
            .MergeAsync(ct)
            .ConfigureAwait(false);
        return merged;
    }

    /// <summary>INSERT 序 DataParameter 装填（Id 在前，@pN 与 BulkSql 语句真源对齐）。</summary>
    private static LinqToDB.Data.DataParameter[] InsertParameters(IReadOnlyList<S1Row> rows, int start, int end)
    {
        var dp = new LinqToDB.Data.DataParameter[(end - start) * BulkSql.ParamsPerRow];
        int p = 0;
        for (int r = start; r < end; r++)
        {
            S1Row row = rows[r];
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Id);
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Name);
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Qty);
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Price);
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Marker);
        }

        return dp;
    }

    /// <summary>UPDATE 序 DataParameter 装填（Id 在尾）。</summary>
    private static LinqToDB.Data.DataParameter[] UpdateParameters(IReadOnlyList<S1Row> rows, int start, int end)
    {
        var dp = new LinqToDB.Data.DataParameter[(end - start) * BulkSql.ParamsPerRow];
        int p = 0;
        for (int r = start; r < end; r++)
        {
            S1Row row = rows[r];
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Name);
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Qty);
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Price);
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Marker);
            dp[p++] = new LinqToDB.Data.DataParameter(Dataset.P(p - 1), row.Id);
        }

        return dp;
    }

    public Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
        => throw new NotSupportedException("InsertReturningId 不在本次子集内");

    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("WideQueryAll 不在本次子集内");

    public Task<(int Parents, int Children)> IncludeJoinAsync(DbConnection conn, int parentCount, CancellationToken ct)
        => throw new NotSupportedException("IncludeJoin 不在本次子集内");

    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        using DataConnectionTransaction tx = await Db.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Db.InsertAsync(row, token: ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        using DataConnectionTransaction tx = await Db.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (S1Row row in rows)
        {
            await Db.InsertAsync(row, token: ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        using DataConnectionTransaction tx = await Db.BeginTransactionAsync(ct).ConfigureAwait(false);
        var options = new BulkCopyOptions
        {
            BulkCopyType = _d == Dialect.MySql ? BulkCopyType.MultipleRows : BulkCopyType.Default,
        };
        await Db.BulkCopyAsync(options, rows, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        using DataConnectionTransaction tx = await Db.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (S1Row row in rows)
        {
            await Db.UpdateAsync(row, token: ct).ConfigureAwait(false);
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

    /// <summary>枚举列事务内逐条插入——每行一次 InsertAsync（枚举→字符串由映射转换器完成），
    /// 事务形态与 ADO/Dapper 地板同构。</summary>
    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        using DataConnectionTransaction tx = await Db.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (EnumRow row in rows)
        {
            await Db.InsertAsync(row, token: ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct) => QueryAllAsync(conn, ct);
}
