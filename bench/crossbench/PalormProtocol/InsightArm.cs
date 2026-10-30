using System.Data.Common;
using Insight.Database;

namespace PalORM.PerfHub;

/// <summary>Insight.Database 臂（清单条目 insight-database，8.0.6）。
/// <para>形态决策：
/// <list type="bullet">
/// <item><b>API 面</b>：Insight 围绕存储过程设计，文本 SQL 走 <c>*Sql</c> 后缀家族
///（QuerySqlAsync/ExecuteSqlAsync/ExecuteScalarSqlAsync——CommandType.Text 的官方通道，
/// AotProbe 适配器降级教训的 JIT 侧正解）。SQL 文本与参数对象直接复用 Dapper 臂同源
///（Dataset/BulkSql 真源 + 匿名对象参数）。</item>
/// <item><b>连接</b>：直接复用 Runner 传入的连接（Insight 是 IDbConnection 扩展层，
/// 与 Dapper 同形态）；MySQL 经 Insight.Database.Providers.MySqlConnector 注册 provider。</item>
/// <item><b>批量</b>：Insight 的 bulk copy 仅 SQL Server——联网方言多值分批
///（Dictionary&lt;string,object&gt; 参数，运行时不支持时回退事务内逐行），
/// SQLite 事务内逐行（与 Dapper 臂同口径）。</item>
/// <item><b>UpsertBatch</b>：BulkSql 多值 UPSERT 同源（raw SQL 通道——Insight 的文本
/// SQL 即其显式 SQL 模型，无额外 upsert 封装）。</item>
/// </list></para></summary>
internal sealed class InsightImpl : IPerfImplementation
{
    private readonly Dialect _d;
    private bool _dictionaryParamsOk = true;

    public string Name => "Insight";

    /// <summary>三方言边界（2026-10-30 冒烟实证，N/A 定案）：
    /// SQLite——无 provider 包，批量物化对 TEXT 列 decimal 失败；
    /// MySQL/PG——Insight 的身份列约定（SQL Server 血统）把名为 Id 的属性从写参数中排除，
    /// 显式主键 INSERT/UPDATE 绑定值为 null（MySQL "Column 'Id' cannot be null" /
    /// PG "Parameter 'p0' must have ... Value set"），读路径正常但协议要求全操作面。
    /// 全方言 N/A，恢复路径：臂内 DTO 避开 Id 命名或 XML 映射覆盖（后续批次可探）。</summary>
    public InsightImpl(DialectInfo dialect)
    {
        _d = dialect.Dialect;
        throw new NotSupportedException(
            "Insight.Database 三方言 N/A：SQLite 物化崩；MySQL/PG 身份列约定吞显式主键绑定（2026-10-30 实证）");
    }

    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct) => Task.CompletedTask;

    private string T => Dataset.Table(_d);
    private string Cols => Dataset.SelectColumns(_d);
    private string C(string c) => Dataset.Q(_d, c);

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
        {
            System.Collections.Generic.IList<S1Row?> rows = await conn.QuerySqlAsync<S1Row?>(
                $"SELECT {Cols} FROM {T} WHERE {C("Id")} = @id",
                new { id },
                cancellationToken: ct).ConfigureAwait(false);
            return rows.FirstOrDefault();
        }

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        System.Collections.Generic.IList<S1Row?> rows = await conn.QuerySqlAsync<S1Row?>(
            $"SELECT {Cols} FROM {T}",
            parameters: null,
            cancellationToken: ct).ConfigureAwait(false);
        return rows.Where(static r => r is not null).ToList()!;
    }

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteSqlAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
            row,
            cancellationToken: ct).ConfigureAwait(false);

    /// <summary>批量插入——联网方言多值分批（Dictionary 参数）；SQLite 事务内逐行集合执行
    ///（与 Dapper 臂同口径：单方言最快真实形态）。</summary>
    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_d == Dialect.Sqlite)
        {
            await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            long inserted = 0;
            foreach (S1Row row in rows)
            {
                inserted += await conn.ExecuteSqlAsync(
                    $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
                    row,
                    transaction: tran,
                    cancellationToken: ct).ConfigureAwait(false);
            }

            await tran.CommitAsync(ct).ConfigureAwait(false);
            return inserted;
        }

        try
        {
            long total = 0;
            int batch = BulkSql.EffectiveBatchRows(_d);
            for (int start = 0; start < rows.Count; start += batch)
            {
                int end = Math.Min(start + batch, rows.Count);
                total += await conn.ExecuteSqlAsync(
                    $"INSERT INTO {T} ({Cols}) VALUES {BulkSql.ValuesRows(end - start)}",
                    InsertDictionary(rows, start, end),
                    cancellationToken: ct).ConfigureAwait(false);
            }

            return total;
        }
        catch (Exception ex) when (_dictionaryParamsOk && ex is not OutOfMemoryException)
        {
            // Dictionary 参数形态不被当前 provider 接受——一次性回退事务内逐行
            _dictionaryParamsOk = false;
            return await BulkInsertRowByRowAsync(conn, rows, ct).ConfigureAwait(false);
        }
    }

    private async Task<long> BulkInsertRowByRowAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        long inserted = 0;
        foreach (S1Row row in rows)
        {
            inserted += await conn.ExecuteSqlAsync(
                $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
                row,
                transaction: tran,
                cancellationToken: ct).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
        return inserted;
    }

    private static Dictionary<string, object> InsertDictionary(IReadOnlyList<S1Row> rows, int start, int end)
    {
        var dp = new Dictionary<string, object>((end - start) * BulkSql.ParamsPerRow);
        int p = 0;
        for (int r = start; r < end; r++)
        {
            S1Row row = rows[r];
            dp[Dataset.P(p++)] = row.Id;
            dp[Dataset.P(p++)] = row.Name;
            dp[Dataset.P(p++)] = row.Qty;
            dp[Dataset.P(p++)] = row.Price;
            dp[Dataset.P(p++)] = row.Marker;
        }

        return dp;
    }

    private static Dictionary<string, object> UpdateDictionary(IReadOnlyList<S1Row> rows, int start, int end)
    {
        var dp = new Dictionary<string, object>((end - start) * BulkSql.ParamsPerRow);
        int p = 0;
        for (int r = start; r < end; r++)
        {
            S1Row row = rows[r];
            dp[Dataset.P(p++)] = row.Name;
            dp[Dataset.P(p++)] = row.Qty;
            dp[Dataset.P(p++)] = row.Price;
            dp[Dataset.P(p++)] = row.Marker;
            dp[Dataset.P(p++)] = row.Id;
        }

        return dp;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteSqlAsync(
            $"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id",
            row,
            cancellationToken: ct).ConfigureAwait(false);

    /// <summary>批量更新——联网方言集合式（PG VALUES / MySQL CASE WHEN，BulkSql 真源）；
    /// SQLite 或字典参数不被 provider 接受时事务内逐行（与 Dapper 臂 SQLite 路径同口径）。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_d == Dialect.Sqlite || !_dictionaryParamsOk)
        {
            return await RowByRowUpdateAsync(conn, rows, ct).ConfigureAwait(false);
        }

        try
        {
            long total = 0;
            int batch = BulkSql.EffectiveBatchRows(_d);
            for (int start = 0; start < rows.Count; start += batch)
            {
                int end = Math.Min(start + batch, rows.Count);
                total += await conn.ExecuteSqlAsync(
                    BulkSql.UpdateBatch(_d, end - start),
                    UpdateDictionary(rows, start, end),
                    cancellationToken: ct).ConfigureAwait(false);
            }

            return total;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dictionaryParamsOk = false;
            return await RowByRowUpdateAsync(conn, rows, ct).ConfigureAwait(false);
        }
    }

    private async Task<long> RowByRowUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        long rowTotal = 0;
        foreach (S1Row row in rows)
        {
            rowTotal += await conn.ExecuteSqlAsync(
                $"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id",
                row,
                transaction: tran,
                cancellationToken: ct).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
        return rowTotal;
    }

    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        long total = 0;
        const int batch = BulkSql.BatchRows;
        for (int start = 0; start < keys.Count; start += batch)
        {
            int end = Math.Min(start + batch, keys.Count);
            int len = end - start;
            var dp = new Dictionary<string, object>(len);
            for (int i = start; i < end; i++)
            {
                dp[Dataset.P(i - start)] = Convert.ToInt64(keys[i], System.Globalization.CultureInfo.InvariantCulture);
            }

            string placeholders = string.Join(
                ", ",
                Enumerable.Range(0, len).Select(static i => Dataset.P(i)));
            total += await conn.ExecuteSqlAsync(
                $"DELETE FROM {T} WHERE {C("Id")} IN ({placeholders})",
                dp,
                transaction: tran,
                cancellationToken: ct).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
        return total;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => $"SELECT {Cols} FROM {T} WHERE {C("Id")} = @id";

    public string BuildComplexQuerySql(DbConnection conn)
        => $"SELECT {Cols} FROM {T} WHERE ({C("Qty")} > @qty AND {C("Marker")} < @marker) OR {C("Name")} LIKE @name ORDER BY {C("Marker")} DESC LIMIT 100";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        System.Collections.Generic.IList<S1Row?> rows = await conn.QuerySqlAsync<S1Row?>(
            $"SELECT {Cols} FROM {T} WHERE {C("Id")} > @lastId ORDER BY {C("Id")} LIMIT @pageSize",
            new { lastId, pageSize },
            cancellationToken: ct).ConfigureAwait(false);
        return rows.Where(static r => r is not null).ToList()!;
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        var dp = new Dictionary<string, object>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            dp[Dataset.P(i)] = ids[i];
        }

        string placeholders = string.Join(", ", Enumerable.Range(0, ids.Length).Select(static i => Dataset.P(i)));
        System.Collections.Generic.IList<S1Row?> rows = await conn.QuerySqlAsync<S1Row?>(
            $"SELECT {Cols} FROM {T} WHERE {C("Id")} IN ({placeholders})",
            dp,
            cancellationToken: ct).ConfigureAwait(false);
        return rows.Where(static r => r is not null).ToList()!;
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => await conn.ExecuteScalarSqlAsync<long>(
            $"SELECT COUNT(*) FROM {T}",
            parameters: null,
            cancellationToken: ct).ConfigureAwait(false);

    /// <summary>批量 UPSERT——多值 UPSERT（BulkSql 真源）裹事务；SQLite 或字典参数
    /// 不被接受时事务内逐行单语句（与 ADO 地板行预编译形态同语句面）。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_d == Dialect.Sqlite || !_dictionaryParamsOk)
        {
            return await RowByRowUpsertAsync(conn, rows, ct).ConfigureAwait(false);
        }

        try
        {
            await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            long total = 0;
            int batch = BulkSql.EffectiveBatchRows(_d);
            for (int start = 0; start < rows.Count; start += batch)
            {
                int end = Math.Min(start + batch, rows.Count);
                total += await conn.ExecuteSqlAsync(
                    BulkSql.UpsertBatch(_d, end - start),
                    InsertDictionary(rows, start, end),
                    transaction: tran,
                    cancellationToken: ct).ConfigureAwait(false);
            }

            await tran.CommitAsync(ct).ConfigureAwait(false);
            return total;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dictionaryParamsOk = false;
            return await RowByRowUpsertAsync(conn, rows, ct).ConfigureAwait(false);
        }
    }

    private async Task<long> RowByRowUpsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        long total = 0;
        foreach (S1Row row in rows)
        {
            total += await conn.ExecuteSqlAsync(
                BulkSql.UpsertBatch(_d, 1),
                InsertDictionary([row], 0, 1),
                transaction: tran,
                cancellationToken: ct).ConfigureAwait(false);
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
        _ = await conn.ExecuteSqlAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
            row,
            transaction: tran,
            cancellationToken: ct).ConfigureAwait(false);
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (S1Row row in rows)
        {
            _ = await conn.ExecuteSqlAsync(
                $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
                row,
                transaction: tran,
                cancellationToken: ct).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (_d == Dialect.Sqlite)
        {
            foreach (S1Row row in rows)
            {
                _ = await conn.ExecuteSqlAsync(
                    $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
                    row,
                    transaction: tran,
                    cancellationToken: ct).ConfigureAwait(false);
            }

            await tran.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        int batch = BulkSql.EffectiveBatchRows(_d);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            _ = await conn.ExecuteSqlAsync(
                $"INSERT INTO {T} ({Cols}) VALUES {BulkSql.ValuesRows(end - start)}",
                InsertDictionary(rows, start, end),
                transaction: tran,
                cancellationToken: ct).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (S1Row row in rows)
        {
            _ = await conn.ExecuteSqlAsync(
                $"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id",
                row,
                transaction: tran,
                cancellationToken: ct).ConfigureAwait(false);
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

    /// <summary>枚举列事务内逐条插入——匿名对象显式 ToString 转换
    ///（与 ADO/Dapper 地板形态同构）。</summary>
    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        string enumTable = Dataset.EnumTable(_d);
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (EnumRow row in rows)
        {
            _ = await conn.ExecuteSqlAsync(
                $"INSERT INTO {enumTable} ({C("Id")}, {C("Name")}, {C("Status")}) VALUES (@Id,@Name,@Status)",
                new { row.Id, row.Name, Status = row.Status.ToString() },
                transaction: tran,
                cancellationToken: ct).ConfigureAwait(false);
        }

        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct) => QueryAllAsync(conn, ct);
}
