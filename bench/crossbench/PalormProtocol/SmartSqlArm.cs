using System.Data.Common;
using SmartSql;


namespace PalORM.PerfHub;

/// <summary>SmartSql 臂（清单条目 smartsql，SmartSql 4.2.0）。
/// <para>形态决策：
/// <list type="bullet">
/// <item><b>API 面</b>：SmartSql 的原生形态 = XML 语句映射 + DI 集成（AotProbe 适配器对 XML
/// 形态的评语"配置复杂度高"）；本臂走其官方 <c>RealSql</c> 显式 SQL 通道
///（RequestContext.RealSql + Request 参数对象，SqlMapper 直查）——SQL 文本与参数对象
/// 复用 Dapper 臂同源（Dataset/BulkSql 真源）。XML 语句模板引擎（ForEach 批量拼 SQL）
/// 属于另一测量面，不在此展开（报告注记）。</item>
/// <item><b>事务</b>：ISqlMapper 实现 ITransaction（BeginTransaction/CommitTransaction/
/// RollbackTransaction，会话级事务模型）。</item>
/// <item><b>批量</b>：多值分批 + Dictionary 参数（Request 对象）；运行时 Dictionary 形态
/// 不被接受时一次性回退事务内逐行（与 SQLite 家族同形）。</item>
/// <item><b>provider 名</b>：UseDatabase(dbProviderName, connString)——SmartSql DbProviderManager
/// 注册名（Sqlite/MySql/PostgreSql），冒烟批实证。</item>
/// </list></para></summary>
internal sealed class SmartSqlImpl : IPerfImplementation, IDisposable
{
    private readonly Dialect _d;
    private readonly ISqlMapper _mapper;
    private bool _dictionaryParamsOk = true;

    public string Name => "SmartSql";

    private static string ProviderName(Dialect d) => d switch
    {
        Dialect.Sqlite => "Sqlite",
        Dialect.MySql => "MySql",
        Dialect.PostgreSql => "PostgreSql",
        _ => throw new ArgumentOutOfRangeException(nameof(d), d, null)
    };

    public SmartSqlImpl(DialectInfo dialect)
    {
        throw new NotSupportedException(
            "SmartSql 4.2.0 代码级初始化未打通：UseDatabase 路径在 SmartSqlConfig.Database 未初始化即解引用（NRE，2026-10-30 探针实证），"
            + "XML 配置路径需 SmartSqlMapConfig 工程化接入（AotProbe 适配器当年同样降级）——本批 N/A，后续可按 XML 路径接入");
#pragma warning disable CS0162 // 不可到达的代码（保留形态供 XML 路径接入时恢复）
        _d = dialect.Dialect;
        _mapper = new SmartSqlBuilder()
            .UseDatabase(ProviderName(_d), Connections.Resolve(dialect))
            .Build()
            .GetSqlMapper();
#pragma warning restore CS0162
    }

    public void Dispose() => _mapper.Dispose();

    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct) => Task.CompletedTask;

    private string T => Dataset.Table(_d);
    private string Cols => Dataset.SelectColumns(_d);
    private string C(string c) => Dataset.Q(_d, c);

    private static AbstractRequestContext Sql(string sql, object? request) =>
        new SmartSql.RequestContext { RealSql = sql, Request = request };

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        System.Collections.Generic.IList<S1Row> rows = await _mapper.QueryAsync<S1Row>(
            Sql($"SELECT {Cols} FROM {T} WHERE {C("Id")} = @id", new { id })).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public Task<int> PooledGetByKeyAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("PooledGetByKey 不在本次子集内");

    public Task<int> GetByKeySessionAsync(DbConnection conn, IReadOnlyList<long> ids, CancellationToken ct)
        => throw new NotSupportedException("GetByKeySession 不在本次子集内");

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
        => [.. await _mapper.QueryAsync<S1Row>(Sql($"SELECT {Cols} FROM {T}", null)).ConfigureAwait(false)];

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => throw new NotSupportedException("StreamAll 不在本次子集内");

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await _mapper.ExecuteAsync(
            Sql($"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", row)).ConfigureAwait(false);

    /// <summary>批量插入——多值分批（Dictionary Request 参数）；SQLite/回退路径事务内逐行。</summary>
    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_d == Dialect.Sqlite || !_dictionaryParamsOk)
        {
            return await RowByRowInsertAsync(conn, rows, ct).ConfigureAwait(false);
        }

        try
        {
            long total = 0;
            int batch = BulkSql.EffectiveBatchRows(_d);
            for (int start = 0; start < rows.Count; start += batch)
            {
                int end = Math.Min(start + batch, rows.Count);
                total += await _mapper.ExecuteAsync(
                    Sql($"INSERT INTO {T} ({Cols}) VALUES {BulkSql.ValuesRows(end - start)}",
                        InsertDictionary(rows, start, end))).ConfigureAwait(false);
            }

            return total;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dictionaryParamsOk = false;
            return await RowByRowInsertAsync(conn, rows, ct).ConfigureAwait(false);
        }
    }

    private async Task<long> RowByRowInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        _mapper.BeginTransaction();
        try
        {
            long inserted = 0;
            foreach (S1Row row in rows)
            {
                inserted += await _mapper.ExecuteAsync(
                    Sql($"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", row))
                    .ConfigureAwait(false);
            }

            _mapper.CommitTransaction();
            return inserted;
        }
        catch
        {
            _mapper.RollbackTransaction();
            throw;
        }
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
        => await _mapper.ExecuteAsync(
            Sql($"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id", row))
            .ConfigureAwait(false);

    /// <summary>批量更新——联网方言集合式（BulkSql 真源）；SQLite 事务内逐行。</summary>
    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_d == Dialect.Sqlite || !_dictionaryParamsOk)
        {
            _mapper.BeginTransaction();
            try
            {
                long rowTotal = 0;
                foreach (S1Row row in rows)
                {
                    rowTotal += await _mapper.ExecuteAsync(
                        Sql($"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id", row))
                        .ConfigureAwait(false);
                }

                _mapper.CommitTransaction();
                return rowTotal;
            }
            catch
            {
                _mapper.RollbackTransaction();
                throw;
            }
        }

        long total = 0;
        int batch = BulkSql.EffectiveBatchRows(_d);
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            total += await _mapper.ExecuteAsync(
                Sql(BulkSql.UpdateBatch(_d, end - start), UpdateDictionary(rows, start, end)))
                .ConfigureAwait(false);
        }

        return total;
    }

    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        _mapper.BeginTransaction();
        try
        {
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

                string placeholders = string.Join(", ", Enumerable.Range(0, len).Select(static i => Dataset.P(i)));
                total += await _mapper.ExecuteAsync(
                    Sql($"DELETE FROM {T} WHERE {C("Id")} IN ({placeholders})", dp)).ConfigureAwait(false);
            }

            _mapper.CommitTransaction();
            return total;
        }
        catch
        {
            _mapper.RollbackTransaction();
            throw;
        }
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => $"SELECT {Cols} FROM {T} WHERE {C("Id")} = @id";

    public string BuildComplexQuerySql(DbConnection conn)
        => $"SELECT {Cols} FROM {T} WHERE ({C("Qty")} > @qty AND {C("Marker")} < @marker) OR {C("Name")} LIKE @name ORDER BY {C("Marker")} DESC LIMIT 100";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        => [.. await _mapper.QueryAsync<S1Row>(
            Sql($"SELECT {Cols} FROM {T} WHERE {C("Id")} > @lastId ORDER BY {C("Id")} LIMIT @pageSize",
                new { lastId, pageSize })).ConfigureAwait(false)];

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        var dp = new Dictionary<string, object>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            dp[Dataset.P(i)] = ids[i];
        }

        string placeholders = string.Join(", ", Enumerable.Range(0, ids.Length).Select(static i => Dataset.P(i)));
        return [.. await _mapper.QueryAsync<S1Row>(
            Sql($"SELECT {Cols} FROM {T} WHERE {C("Id")} IN ({placeholders})", dp)).ConfigureAwait(false)];
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => await _mapper.ExecuteScalarAsync<long>(Sql($"SELECT COUNT(*) FROM {T}", null)).ConfigureAwait(false);

    /// <summary>批量 UPSERT——多值 UPSERT（BulkSql 真源）裹事务；SQLite 单语句行预编译形态
    /// 不在 SmartSql 表达面，走事务内逐行 UPSERT 单语句。</summary>
    public async Task<long> UpsertBatchAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        _mapper.BeginTransaction();
        try
        {
            long total = 0;
            int batch = BulkSql.EffectiveBatchRows(_d);
            for (int start = 0; start < rows.Count; start += batch)
            {
                int end = Math.Min(start + batch, rows.Count);
                total += await _mapper.ExecuteAsync(
                    Sql(BulkSql.UpsertBatch(_d, end - start), InsertDictionary(rows, start, end)))
                    .ConfigureAwait(false);
            }

            _mapper.CommitTransaction();
            return total;
        }
        catch
        {
            _mapper.RollbackTransaction();
            throw;
        }
    }

    public Task<long> InsertReturningIdAsync(DbConnection conn, string name, int qty, CancellationToken ct)
        => throw new NotSupportedException("InsertReturningId 不在本次子集内");

    public Task<int> WideQueryAllAsync(DbConnection conn, CancellationToken ct)
        => throw new NotSupportedException("WideQueryAll 不在本次子集内");

    public Task<(int Parents, int Children)> IncludeJoinAsync(DbConnection conn, int parentCount, CancellationToken ct)
        => throw new NotSupportedException("IncludeJoin 不在本次子集内");

    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        _mapper.BeginTransaction();
        try
        {
            _ = await _mapper.ExecuteAsync(
                Sql($"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", row)).ConfigureAwait(false);
            _mapper.CommitTransaction();
        }
        catch
        {
            _mapper.RollbackTransaction();
            throw;
        }
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => throw new NotSupportedException("TxTenInserts 不在本次子集内");

    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        _mapper.BeginTransaction();
        try
        {
            foreach (S1Row row in rows)
            {
                _ = await _mapper.ExecuteAsync(
                    Sql($"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", row)).ConfigureAwait(false);
            }

            _mapper.CommitTransaction();
        }
        catch
        {
            _mapper.RollbackTransaction();
            throw;
        }
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        if (_d == Dialect.Sqlite || !_dictionaryParamsOk)
        {
            await RowByRowInsertAsync(conn, rows, ct).ConfigureAwait(false);
            return;
        }

        _mapper.BeginTransaction();
        try
        {
            int batch = BulkSql.EffectiveBatchRows(_d);
            for (int start = 0; start < rows.Count; start += batch)
            {
                int end = Math.Min(start + batch, rows.Count);
                _ = await _mapper.ExecuteAsync(
                    Sql($"INSERT INTO {T} ({Cols}) VALUES {BulkSql.ValuesRows(end - start)}",
                        InsertDictionary(rows, start, end))).ConfigureAwait(false);
            }

            _mapper.CommitTransaction();
        }
        catch
        {
            _mapper.RollbackTransaction();
            throw;
        }
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        _mapper.BeginTransaction();
        try
        {
            foreach (S1Row row in rows)
            {
                _ = await _mapper.ExecuteAsync(
                    Sql($"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id", row))
                    .ConfigureAwait(false);
            }

            _mapper.RollbackTransaction();
        }
        catch
        {
            _mapper.RollbackTransaction();
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

    /// <summary>枚举列事务内逐条插入——匿名对象显式 ToString 转换
    ///（与 ADO/Dapper 地板形态同构）。</summary>
    public async Task EnumInsertsAsync(DbConnection conn, IReadOnlyList<EnumRow> rows, CancellationToken ct)
    {
        string enumTable = Dataset.EnumTable(_d);
        _mapper.BeginTransaction();
        try
        {
            foreach (EnumRow row in rows)
            {
                _ = await _mapper.ExecuteAsync(
                    Sql($"INSERT INTO {enumTable} ({C("Id")}, {C("Name")}, {C("Status")}) VALUES (@Id,@Name,@Status)",
                        new { row.Id, row.Name, Status = row.Status.ToString() })).ConfigureAwait(false);
            }

            _mapper.CommitTransaction();
        }
        catch
        {
            _mapper.RollbackTransaction();
            throw;
        }
    }

    public Task<List<S1Row>> GetAllAsync(DbConnection conn, CancellationToken ct) => QueryAllAsync(conn, ct);
}
