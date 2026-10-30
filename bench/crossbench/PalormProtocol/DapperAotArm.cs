using System.Data.Common;
using Dapper;

namespace PalORM.PerfHub;

/// <summary>Dapper.AOT 臂（清单条目 Dapper.AOT，与 Dapper 主包臂并存为对照）。
/// <para>拦截形态：<b>方法级 <c>[DapperAot]</c></b>（AotProbe dapper-aot 适配器同款官方路径）。
/// 程序集级开关会波及同程序集的 DapperImpl 臂（全量拦截改变其测量语义），故禁用——
/// 12 库清单口径 = 官方 AOT 路径形态，即源生成器接管的 Dapper 调用。</para>
/// <para>SQL 形态分两层：
/// <list type="bullet">
/// <item>单语句操作（CRUD/Query/EnumInserts）：方言分支内<b>字面 SQL</b> + 匿名参数——
/// Dapper.AOT 源生成器可完整分析（参数绑定/物化编译期生成），是官方 AOT 形态的核心场景。
/// SQLite/PG 共享双引号字面，MySQL 单独反引号字面，保证每个调用点 SQL 常量可达。</item>
/// <item>批量操作（BulkInsert/BulkUpdate/UpsertBatch/WhereIn）：SQL 随批宽/占位符数动态构造，
/// 源生成器无法预分析，回落运行时 Dapper 路径——与 Dapper 臂同 SQL 真源（BulkSql/Dataset），
/// 批量本就是手拼 SQL 场景，Dapper.AOT 对此亦无官方批量 API。</item>
/// </list></para></summary>
internal sealed class DapperAotImpl(DialectInfo dialect) : IPerfImplementation
{
    public string Name => "DapperAot";

    private Dialect D => dialect.Dialect;
    private bool MySql => D == Dialect.MySql;

    // MySQL 反引号列集/表名与双引号族分离——每个调用点字面可达（见类注释）
    private const string ColsMy = "`Id`, `Name`, `Qty`, `Price`, `Marker`";
    private const string ColsQ = "\"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\"";
    private const string TMy = "`perf_s1`";
    private const string TQ = "\"perf_s1\"";

    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct) => Task.CompletedTask;

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        if (MySql)
        {
            return await GetMy(conn, id, ct).ConfigureAwait(false);
        }

        return await GetQ(conn, id, ct).ConfigureAwait(false);
    }

    [DapperAot]
    private static async Task<S1Row?> GetMy(DbConnection conn, long id, CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<S1Row>(
            "SELECT `Id`, `Name`, `Qty`, `Price`, `Marker` FROM `perf_s1` WHERE `Id` = @id", new { id })
            .ConfigureAwait(false);

    [DapperAot]
    private static async Task<S1Row?> GetQ(DbConnection conn, long id, CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<S1Row>(
            "SELECT \"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\" FROM \"perf_s1\" WHERE \"Id\" = @id", new { id })
            .ConfigureAwait(false);

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        if (MySql)
        {
            return (await QueryMy(conn, ct).ConfigureAwait(false)).AsList();
        }

        return (await QueryQ(conn, ct).ConfigureAwait(false)).AsList();
    }

    [DapperAot]
    private static async Task<IEnumerable<S1Row>> QueryMy(DbConnection conn, CancellationToken ct)
        => await conn.QueryAsync<S1Row>(
            "SELECT `Id`, `Name`, `Qty`, `Price`, `Marker` FROM `perf_s1`", commandTimeout: null)
            .ConfigureAwait(false);

    [DapperAot]
    private static async Task<IEnumerable<S1Row>> QueryQ(DbConnection conn, CancellationToken ct)
        => await conn.QueryAsync<S1Row>(
            "SELECT \"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\" FROM \"perf_s1\"", commandTimeout: null)
            .ConfigureAwait(false);

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        if (MySql)
        {
            await InsertMy(conn, row, ct).ConfigureAwait(false);
        }
        else
        {
            await InsertQ(conn, row, ct).ConfigureAwait(false);
        }
    }

    [DapperAot]
    private static async Task InsertMy(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            "INSERT INTO `perf_s1` (`Id`, `Name`, `Qty`, `Price`, `Marker`) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
            row).ConfigureAwait(false);

    [DapperAot]
    private static async Task InsertQ(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            "INSERT INTO \"perf_s1\" (\"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\") VALUES (@Id,@Name,@Qty,@Price,@Marker)",
            row).ConfigureAwait(false);

    /// <summary>批量插入——与 Dapper 臂同构（SQLite multi-exec / 联网方言多值分批，BulkSql 真源）。
    /// 动态 SQL 不经源生成器（见类注释），此操作属"回落运行时 Dapper"的批量场景。</summary>
    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (D == Dialect.Sqlite)
        {
            await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            long inserted = await conn.ExecuteAsync(
                MySql
                    ? "INSERT INTO `perf_s1` (`Id`, `Name`, `Qty`, `Price`, `Marker`) VALUES (@Id,@Name,@Qty,@Price,@Marker)"
                    : "INSERT INTO \"perf_s1\" (\"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\") VALUES (@Id,@Name,@Qty,@Price,@Marker)",
                rows, tran).ConfigureAwait(false);
            await tran.CommitAsync(ct).ConfigureAwait(false);
            return inserted;
        }

        long total = 0;
        int batch = BulkSql.EffectiveBatchRows(D);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            string cols = MySql ? ColsMy : ColsQ;
            string table = MySql ? TMy : TQ;
            total += await conn.ExecuteAsync(
                $"INSERT INTO {table} ({cols}) VALUES {BulkSql.ValuesRows(end - start)}",
                DapperImplInsertParameters(rows, start, end)).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>INSERT 序参数装填（Id 在前）——与 DapperImpl.InsertParameters 同构。
    /// 经 internal 同程序集复制而非公开，保持两臂代码形状逐位对齐。</summary>
    private static DynamicParameters DapperImplInsertParameters(IReadOnlyList<S1Row> rows, int start, int end)
    {
        DynamicParameters dp = new();
        int p = 0;
        for (int r = start; r < end; r++)
        {
            S1Row row = rows[r];
            dp.Add(Dataset.P(p++), row.Id);
            dp.Add(Dataset.P(p++), row.Name);
            dp.Add(Dataset.P(p++), row.Qty);
            dp.Add(Dataset.P(p++), row.Price);
            dp.Add(Dataset.P(p++), row.Marker);
        }

        return dp;
    }

    /// <summary>UPDATE 序参数装填（Id 在尾）——与 DapperImpl.UpdateParameters 同构。</summary>
    private static DynamicParameters DapperImplUpdateParameters(IReadOnlyList<S1Row> rows, int start, int end)
    {
        DynamicParameters dp = new();
        int p = 0;
        for (int r = start; r < end; r++)
        {
            S1Row row = rows[r];
            dp.Add(Dataset.P(p++), row.Name);
            dp.Add(Dataset.P(p++), row.Qty);
            dp.Add(Dataset.P(p++), row.Price);
            dp.Add(Dataset.P(p++), row.Marker);
            dp.Add(Dataset.P(p++), row.Id);
        }

        return dp;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        if (MySql)
        {
            return await UpdateMy(conn, row, ct).ConfigureAwait(false);
        }

        return await UpdateQ(conn, row, ct).ConfigureAwait(false);
    }

    [DapperAot]
    private static async Task<int> UpdateMy(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            "UPDATE `perf_s1` SET `Name`=@Name, `Qty`=@Qty, `Price`=@Price, `Marker`=@Marker WHERE `Id`=@Id",
            row).ConfigureAwait(false);

    [DapperAot]
    private static async Task<int> UpdateQ(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            "UPDATE \"perf_s1\" SET \"Name\"=@Name, \"Qty\"=@Qty, \"Price\"=@Price, \"Marker\"=@Marker WHERE \"Id\"=@Id",
            row).ConfigureAwait(false);

    /// <summary>批量更新——与 Dapper 臂同构（SQLite 逐条裹事务 / PG VALUES / MySQL CASE WHEN）。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        string table = MySql ? TMy : TQ;
        string cols = MySql ? ColsMy : ColsQ;
        string single = MySql
            ? "UPDATE `perf_s1` SET `Name`=@Name, `Qty`=@Qty, `Price`=@Price, `Marker`=@Marker WHERE `Id`=@Id"
            : "UPDATE \"perf_s1\" SET \"Name\"=@Name, \"Qty\"=@Qty, \"Price\"=@Price, \"Marker\"=@Marker WHERE \"Id\"=@Id";
        if (D == Dialect.Sqlite)
        {
            await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            long rowTotal = 0;
            foreach (S1Row row in rows)
            {
                rowTotal += await conn.ExecuteAsync(single, row, tran).ConfigureAwait(false);
            }

            await tran.CommitAsync(ct).ConfigureAwait(false);
            return rowTotal;
        }

        long total = 0;
        int batch = BulkSql.EffectiveBatchRows(D);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            total += await conn.ExecuteAsync(
                BulkSql.UpdateBatch(D, end - start),
                DapperImplUpdateParameters(rows, start, end)).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>批量删除——与 Dapper 臂同构（1000/批 IN 裹事务）。</summary>
    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        long total = 0;
        const int batch = BulkSql.BatchRows;
        string table = MySql ? TMy : TQ;
        string idCol = MySql ? "`Id`" : "\"Id\"";
        for (int start = 0; start < keys.Count; start += batch)
        {
            int end = Math.Min(start + batch, keys.Count);
            long[] ids = new long[end - start];
            for (int i = start; i < end; i++)
            {
                ids[i - start] = Convert.ToInt64(keys[i], System.Globalization.CultureInfo.InvariantCulture);
            }

            DynamicParameters dp = new();
            for (int i = 0; i < ids.Length; i++)
            {
                dp.Add(Dataset.P(i), ids[i]);
            }

            total += await conn.ExecuteAsync(
                $"DELETE FROM {table} WHERE {idCol} IN ({Placeholders(ids.Length)})", dp, tran)
                .ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
        return total;
    }

    private static string Placeholders(int count)
        => string.Join(", ", Enumerable.Range(0, count).Select(static i => Dataset.P(i)));

    public string BuildGetByKeySql(DbConnection conn, long id)
        => MySql
            ? "SELECT `Id`, `Name`, `Qty`, `Price`, `Marker` FROM `perf_s1` WHERE `Id` = @id"
            : "SELECT \"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\" FROM \"perf_s1\" WHERE \"Id\" = @id";

    public string BuildComplexQuerySql(DbConnection conn)
        => MySql
            ? "SELECT `Id`, `Name`, `Qty`, `Price`, `Marker` FROM `perf_s1` WHERE (`Qty` > @qty AND `Marker` < @marker) OR `Name` LIKE @name ORDER BY `Marker` DESC LIMIT 100"
            : "SELECT \"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\" FROM \"perf_s1\" WHERE (\"Qty\" > @qty AND \"Marker\" < @marker) OR \"Name\" LIKE @name ORDER BY \"Marker\" DESC LIMIT 100";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        if (MySql)
        {
            return (await PageMy(conn, lastId, pageSize, ct).ConfigureAwait(false)).AsList();
        }

        return (await PageQ(conn, lastId, pageSize, ct).ConfigureAwait(false)).AsList();
    }

    [DapperAot]
    private static async Task<IEnumerable<S1Row>> PageMy(
        DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        => await conn.QueryAsync<S1Row>(
            "SELECT `Id`, `Name`, `Qty`, `Price`, `Marker` FROM `perf_s1` WHERE `Id` > @lastId ORDER BY `Id` LIMIT @pageSize",
            new { lastId, pageSize }).ConfigureAwait(false);

    [DapperAot]
    private static async Task<IEnumerable<S1Row>> PageQ(
        DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        => await conn.QueryAsync<S1Row>(
            "SELECT \"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\" FROM \"perf_s1\" WHERE \"Id\" > @lastId ORDER BY \"Id\" LIMIT @pageSize",
            new { lastId, pageSize }).ConfigureAwait(false);

    /// <summary>WhereIn——占位符数随 ids.Length 动态构造（Npgsql 位置参数限制同 Dapper 臂注释），
    /// 回落运行时路径。</summary>
    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        string table = MySql ? TMy : TQ;
        string idCol = MySql ? "`Id`" : "\"Id\"";
        string cols = MySql ? ColsMy : ColsQ;
        DynamicParameters dp = new();
        for (int i = 0; i < ids.Length; i++)
        {
            dp.Add(Dataset.P(i), ids[i]);
        }

        IEnumerable<S1Row> rows = await conn.QueryAsync<S1Row>(
            $"SELECT {cols} FROM {table} WHERE {idCol} IN ({Placeholders(ids.Length)})", dp)
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
    {
        if (MySql)
        {
            return await CountMy(conn, ct).ConfigureAwait(false);
        }

        return await CountQ(conn, ct).ConfigureAwait(false);
    }

    [DapperAot]
    private static async Task<long> CountMy(DbConnection conn, CancellationToken ct)
        => await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM `perf_s1`").ConfigureAwait(false);

    [DapperAot]
    private static async Task<long> CountQ(DbConnection conn, CancellationToken ct)
        => await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM \"perf_s1\"").ConfigureAwait(false);

    /// <summary>批量 UPSERT——与 Dapper 臂同构（SQLite 行预编译形态不在 Dapper 生态，
    /// 统一走多值分批裹事务；SQL 真源 BulkSql.UpsertBatch）。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        long total = 0;
        int batch = BulkSql.EffectiveBatchRows(D);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            total += await conn.ExecuteAsync(
                BulkSql.UpsertBatch(D, end - start),
                DapperImplInsertParameters(rows, start, end), tran).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
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
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (MySql)
        {
            await TxInsertMy(conn, row, tran).ConfigureAwait(false);
        }
        else
        {
            await TxInsertQ(conn, row, tran).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    [DapperAot]
    private static async Task TxInsertMy(DbConnection conn, S1Row row, DbTransaction tran)
        => await conn.ExecuteAsync(
            "INSERT INTO `perf_s1` (`Id`, `Name`, `Qty`, `Price`, `Marker`) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
            row, tran).ConfigureAwait(false);

    [DapperAot]
    private static async Task TxInsertQ(DbConnection conn, S1Row row, DbTransaction tran)
        => await conn.ExecuteAsync(
            "INSERT INTO \"perf_s1\" (\"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\") VALUES (@Id,@Name,@Qty,@Price,@Marker)",
            row, tran).ConfigureAwait(false);

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    /// <summary>事务内 100 条——Dapper.AOT 的插值逐行形态（每行一次拦截调用）；
    /// 与 Dapper 臂 multi-exec 的差异是形态差异（无状态扩展 vs 集合 multi-exec）。</summary>
    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (S1Row row in rows)
        {
            if (MySql)
            {
                await TxInsertMy(conn, row, tran).ConfigureAwait(false);
            }
            else
            {
                await TxInsertQ(conn, row, tran).ConfigureAwait(false);
            }
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (D == Dialect.Sqlite)
        {
            await conn.ExecuteAsync(
                MySql
                    ? "INSERT INTO `perf_s1` (`Id`, `Name`, `Qty`, `Price`, `Marker`) VALUES (@Id,@Name,@Qty,@Price,@Marker)"
                    : "INSERT INTO \"perf_s1\" (\"Id\", \"Name\", \"Qty\", \"Price\", \"Marker\") VALUES (@Id,@Name,@Qty,@Price,@Marker)",
                rows, tran).ConfigureAwait(false);
            await tran.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        int batch = BulkSql.EffectiveBatchRows(D);
        string cols = MySql ? ColsMy : ColsQ;
        string table = MySql ? TMy : TQ;
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            await conn.ExecuteAsync(
                $"INSERT INTO {table} ({cols}) VALUES {BulkSql.ValuesRows(end - start)}",
                DapperImplInsertParameters(rows, start, end), tran).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        string single = MySql
            ? "UPDATE `perf_s1` SET `Name`=@Name, `Qty`=@Qty, `Price`=@Price, `Marker`=@Marker WHERE `Id`=@Id"
            : "UPDATE \"perf_s1\" SET \"Name\"=@Name, \"Qty\"=@Qty, \"Price\"=@Price, \"Marker\"=@Marker WHERE \"Id\"=@Id";
        foreach (S1Row row in rows)
        {
            await conn.ExecuteAsync(single, row, tran).ConfigureAwait(false);
        }

        await tran.RollbackAsync(ct).ConfigureAwait(false);
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

    /// <summary>枚举列事务内逐条插入——与 Dapper 臂同形（匿名对象显式 ToString 转换），
    /// 差异仅 [DapperAot] 拦截生成的参数绑定与物化代码。</summary>
    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (EnumRow row in rows)
        {
            if (MySql)
            {
                await EnumInsertMy(conn, row, tran).ConfigureAwait(false);
            }
            else
            {
                await EnumInsertQ(conn, row, tran).ConfigureAwait(false);
            }
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    [DapperAot]
    private static async Task EnumInsertMy(DbConnection conn, EnumRow row, DbTransaction tran)
        => await conn.ExecuteAsync(
            "INSERT INTO `perf_enum` (`Id`, `Name`, `Status`) VALUES (@Id,@Name,@Status)",
            new { row.Id, row.Name, Status = row.Status.ToString() }, tran).ConfigureAwait(false);

    [DapperAot]
    private static async Task EnumInsertQ(DbConnection conn, EnumRow row, DbTransaction tran)
        => await conn.ExecuteAsync(
            "INSERT INTO \"perf_enum\" (\"Id\", \"Name\", \"Status\") VALUES (@Id,@Name,@Status)",
            new { row.Id, row.Name, Status = row.Status.ToString() }, tran).ConfigureAwait(false);

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct) => QueryAllAsync(conn, ct);
}
