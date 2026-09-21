using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Dapper;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.PerfHub;

/// <summary>三种实现的统一操作面——PerfHub 对每个 (方言 × 实现) 组合调同一组方法。
/// 三实现测同一份数据、同一组 SQL 语义，只有"谁来生成 SQL/绑定参数/物化行"不同。</summary>
internal interface IPerfImplementation
{
    /// <summary>实现标识：ADO_NET（地板基线）/ Dapper / PalORM。</summary>
    string Name { get; }

    Task SetupAsync(DbConnection conn, int rows, CancellationToken ct);
    Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct);
    Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct);
    Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct);
    Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct);
    Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct);
    Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct);

    // ── SQL 构建（不执行，纯 ORM 开销）──
    string BuildGetByKeySql(DbConnection conn, long id);
    string BuildComplexQuerySql(DbConnection conn);

    // ── 查询类 ──
    Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct);
    Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct);
    Task<long> CountAsync(DbConnection conn, CancellationToken ct);

    // ── 事务场景（真实业务形态）──
    Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct);
    Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
    Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// ADO.NET 基线——性能地板。所有比值（Ratio）以它为分母。
// 手写参数化 SQL + 手工物化，是"不靠任何 ORM 能做到的最好情况"。
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class AdoNetImpl(DialectInfo dialect) : IPerfImplementation
{
    public string Name => "ADO_NET";

    private string T => Dataset.Table(dialect.Dialect);
    private string Q(string c) => Dataset.Q(dialect.Dialect, c);
    private string Cols => Dataset.SelectColumns(dialect.Dialect);
    private Dialect D => dialect.Dialect;

    public async Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
    {
        await ExecAsync(conn, Dataset.DropTableSql(D), ct).ConfigureAwait(false);
        await ExecAsync(conn, Dataset.CreateTableSql(D), ct).ConfigureAwait(false);
        const int batch = 500;
        for (int start = 0; start < rows; start += batch)
        {
            int end = Math.Min(start + batch, rows);
            var sb = new StringBuilder();
            sb.Append("INSERT INTO ").Append(T).Append(" (").Append(Cols).Append(") VALUES ");
            await using DbCommand cmd = conn.CreateCommand();
            for (int r = start; r < end; r++)
            {
                if (r > start)
                {
                    sb.Append(", ");
                }

                sb.Append('(');
                for (int c = 0; c < 5; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(Dataset.P(((r - start) * 5) + c));
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();
            int p = 0;
            for (int r = start; r < end; r++)
            {
                S1Row row = Dataset.Seed(r);
                AddP(cmd, p++, row.Id);
                AddP(cmd, p++, row.Name);
                AddP(cmd, p++, row.Qty);
                AddP(cmd, p++, row.Price);
                AddP(cmd, p++, row.Marker);
            }
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T} WHERE {Q("Id")} = {Dataset.P(0)}";
        AddP(cmd, 0, id);
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Map(r) : null;
    }

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T}";
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<S1Row>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Map(r));
        }

        return list;
    }

    public async Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T}";
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        long n = 0;
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            await onRow(Map(r)).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await InsertAsync(conn, row, null, ct).ConfigureAwait(false);

    /// <summary>插入单行；<paramref name="tran"/> 非 null 时命令显式挂到该事务
    /// （MySqlConnector 不接受「命令的事务不是连接的活动事务」）。</summary>
    public async Task InsertAsync(DbConnection conn, S1Row row, DbTransaction? tran, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"INSERT INTO {T} ({Cols}) VALUES ({Dataset.P(0)}, {Dataset.P(1)}, {Dataset.P(2)}, {Dataset.P(3)}, {Dataset.P(4)})";
        AddP(cmd, 0, row.Id);
        AddP(cmd, 1, row.Name);
        AddP(cmd, 2, row.Qty);
        AddP(cmd, 3, row.Price);
        AddP(cmd, 4, row.Marker);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => await BulkInsertAsync(conn, rows, null, ct).ConfigureAwait(false);

    /// <summary>多值 INSERT；<paramref name="tran"/> 非 null 时每个命令显式挂到该事务。</summary>
    public async Task<long> BulkInsertAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, DbTransaction? tran, CancellationToken ct)
    {
        long total = 0;
        const int batch = 500;
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            var sb = new StringBuilder();
            sb.Append("INSERT INTO ").Append(T).Append(" (").Append(Cols).Append(") VALUES ");
            await using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            for (int r = start; r < end; r++)
            {
                if (r > start)
                {
                    sb.Append(", ");
                }

                sb.Append('(');
                for (int c = 0; c < 5; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(Dataset.P(((r - start) * 5) + c));
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();
            int p = 0;
            for (int r = start; r < end; r++)
            {
                S1Row row = rows[r];
                AddP(cmd, p++, row.Id);
                AddP(cmd, p++, row.Name);
                AddP(cmd, p++, row.Qty);
                AddP(cmd, p++, row.Price);
                AddP(cmd, p++, row.Marker);
            }
            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await UpdateAsync(conn, row, null, ct).ConfigureAwait(false);

    /// <summary>更新单行；<paramref name="tran"/> 非 null 时命令显式挂到该事务。</summary>
    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, DbTransaction? tran, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"UPDATE {T} SET {Q("Name")} = {Dataset.P(0)}, {Q("Qty")} = {Dataset.P(1)}, "
            + $"{Q("Price")} = {Dataset.P(2)}, {Q("Marker")} = {Dataset.P(3)} WHERE {Q("Id")} = {Dataset.P(4)}";
        AddP(cmd, 0, row.Name);
        AddP(cmd, 1, row.Qty);
        AddP(cmd, 2, row.Price);
        AddP(cmd, 3, row.Marker);
        AddP(cmd, 4, row.Id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => await BulkUpdateAsync(conn, rows, null, ct).ConfigureAwait(false);

    /// <summary>逐条 UPDATE；<paramref name="tran"/> 非 null 时每条命令显式挂到该事务。</summary>
    public async Task<long> BulkUpdateAsync(
        DbConnection conn, IReadOnlyList<S1Row> rows, DbTransaction? tran, CancellationToken ct)
    {
        // ADO.NET 无批量 UPDATE 抽象——逐条执行（这正是 ORM 批量路径要对比的地板）
        long total = 0;
        foreach (S1Row row in rows)
        {
            total += await UpdateAsync(conn, row, tran, ct).ConfigureAwait(false);
        }

        return total;
    }

    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
        => await BulkDeleteAsync(conn, keys, null, ct).ConfigureAwait(false);

    /// <summary>IN 分批删除；<paramref name="tran"/> 非 null 时每个命令显式挂到该事务。</summary>
    public async Task<long> BulkDeleteAsync(
        DbConnection conn, IReadOnlyList<object> keys, DbTransaction? tran, CancellationToken ct)
    {
        long total = 0;
        const int batch = 500;
        for (int start = 0; start < keys.Count; start += batch)
        {
            int end = Math.Min(start + batch, keys.Count);
            var sb = new StringBuilder();
            sb.Append("DELETE FROM ").Append(T).Append(" WHERE ").Append(Q("Id")).Append(" IN (");
            await using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            for (int i = start; i < end; i++)
            {
                if (i > start)
                {
                    sb.Append(", ");
                }

                sb.Append(Dataset.P(i - start));
            }
            sb.Append(')');
            cmd.CommandText = sb.ToString();
            for (int i = start; i < end; i++)
            {
                AddP(cmd, i - start, keys[i]);
            }

            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => $"SELECT {Cols} FROM {T} WHERE {Q("Id")} = {Dataset.P(0)}";

    public string BuildComplexQuerySql(DbConnection conn)
        => $"SELECT {Cols} FROM {T} WHERE ({Q("Qty")} > {Dataset.P(0)} AND {Q("Marker")} < {Dataset.P(1)}) "
         + $"OR {Q("Name")} LIKE {Dataset.P(2)} ORDER BY {Q("Marker")} DESC LIMIT 100";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T} WHERE {Q("Id")} > {Dataset.P(0)} ORDER BY {Q("Id")} LIMIT {pageSize}";
        AddP(cmd, 0, lastId);
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<S1Row>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Map(r));
        }

        return list;
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"SELECT {Cols} FROM {T} WHERE {Q("Id")} IN (");
        await using DbCommand cmd = conn.CreateCommand();
        for (int i = 0; i < ids.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(Dataset.P(i));
        }
        sb.Append(')');
        cmd.CommandText = sb.ToString();
        for (int i = 0; i < ids.Length; i++)
        {
            AddP(cmd, i, ids[i]);
        }

        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<S1Row>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Map(r));
        }

        return list;
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {T}";
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── 事务场景 ──
    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await InsertAsync(conn, row, tran, ct).ConfigureAwait(false);
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"INSERT INTO {T} ({Cols}) VALUES ({Dataset.P(0)}, {Dataset.P(1)}, {Dataset.P(2)}, {Dataset.P(3)}, {Dataset.P(4)})";
        foreach (S1Row row in rows)
        {
            cmd.Parameters.Clear();
            AddP(cmd, 0, row.Id);
            AddP(cmd, 1, row.Name);
            AddP(cmd, 2, row.Qty);
            AddP(cmd, 3, row.Price);
            AddP(cmd, 4, row.Marker);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>事务内 100 条逐条插入——与 <see cref="TxTenInsertsAsync"/> 同形态不同规模，
    /// 用独立实现以便在报告里分开登记（10 条 vs 100 条的每语句摊销差异正是要测的）。</summary>
    public Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => TxTenInsertsAsync(conn, rows, ct);

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await BulkInsertAsync(conn, rows, tran, ct).ConfigureAwait(false);
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"UPDATE {T} SET {Q("Qty")} = {Dataset.P(0)} WHERE {Q("Id")} = {Dataset.P(1)}";
        foreach (S1Row row in rows)
        {
            cmd.Parameters.Clear();
            AddP(cmd, 0, row.Qty + 1);
            AddP(cmd, 1, row.Id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tran.RollbackAsync(ct).ConfigureAwait(false);
    }

    private static void AddP(DbCommand cmd, int index, object? value)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = Dataset.P(index);
        p.Value = value ?? DBNull.Value;
        // 显式 DbType：Npgsql 对 object 装箱的 long/int 可能推断为 text
        switch (value)
        {
            case long: p.DbType = System.Data.DbType.Int64; break;
            case int: p.DbType = System.Data.DbType.Int32; break;
            case decimal: p.DbType = System.Data.DbType.Decimal; break;
            case bool: p.DbType = System.Data.DbType.Boolean; break;
            case string: p.DbType = System.Data.DbType.String; break;
            default: break;
        }
        cmd.Parameters.Add(p);
    }

    internal static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static S1Row Map(DbDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Qty = r.GetInt32(2),
        Price = r.GetDecimal(3),
        Marker = r.GetInt64(4)
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Dapper——主流 micro-ORM 对照臂
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class DapperImpl(DialectInfo dialect) : IPerfImplementation
{
    public string Name => "Dapper";

    private string T => Dataset.Table(dialect.Dialect);
    private string C(string c) => Dataset.Q(dialect.Dialect, c);
    private string Cols => Dataset.SelectColumns(dialect.Dialect);

    // 灌数走 ADO.NET 臂的同一路径——保证三实现的库内容逐位相同
    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
        => new AdoNetImpl(dialect).SetupAsync(conn, rows, ct);

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<S1Row>(
            $"SELECT {Cols} FROM {T} WHERE {C("Id")} = @id", new { id }).ConfigureAwait(false);

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        IEnumerable<S1Row> rows = await conn.QueryAsync<S1Row>($"SELECT {Cols} FROM {T}").ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
    {
        long n = 0;
        await foreach (S1Row? row in conn.QueryUnbufferedAsync<S1Row>($"SELECT {Cols} FROM {T}").ConfigureAwait(false))
        {
            await onRow(row).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", row).ConfigureAwait(false);

    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => await conn.ExecuteAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", rows).ConfigureAwait(false);

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            $"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id",
            row).ConfigureAwait(false);

    public Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        long total = 0;
        foreach (S1Row row in rows)
        {
            total += conn.Execute(
                $"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id",
                row);
        }

        return Task.FromResult(total);
    }

    public async Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
    {
        long total = 0;
        const int batch = 500;
        for (int start = 0; start < keys.Count; start += batch)
        {
            int end = Math.Min(start + batch, keys.Count);
            long[] ids = new long[end - start];
            for (int i = start; i < end; i++)
            {
                ids[i - start] = Convert.ToInt64(keys[i], System.Globalization.CultureInfo.InvariantCulture);
            }

            total += await conn.ExecuteAsync(
                $"DELETE FROM {T} WHERE {C("Id")} IN ({Placeholders(ids.Length)})", Parameters(ids))
                .ConfigureAwait(false);
        }
        return total;
    }

    /// <summary>生成 N 个参数占位符（@p0, @p1, ...）——三方言统一用 @pN，驱动各自接受。</summary>
    private static string Placeholders(int count)
        => string.Join(", ", Enumerable.Range(0, count).Select(static i => Dataset.P(i)));

    /// <summary>把 long[] 装进 DynamicParameters——按占位符名逐个绑定。</summary>
    private static DynamicParameters Parameters(long[] values)
    {
        DynamicParameters dp = new();
        for (int i = 0; i < values.Length; i++)
        {
            dp.Add(Dataset.P(i), values[i]);
        }

        return dp;
    }

    public string BuildGetByKeySql(DbConnection conn, long id)
        => $"SELECT {Cols} FROM {T} WHERE {C("Id")} = @id";

    public string BuildComplexQuerySql(DbConnection conn)
        => $"SELECT {Cols} FROM {T} WHERE ({C("Qty")} > @qty AND {C("Marker")} < @marker) OR {C("Name")} LIKE @name ORDER BY {C("Marker")} DESC LIMIT 100";

    public async Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
    {
        IEnumerable<S1Row> rows = await conn.QueryAsync<S1Row>(
            $"SELECT {Cols} FROM {T} WHERE {C("Id")} > @lastId ORDER BY {C("Id")} LIMIT @pageSize",
            new { lastId, pageSize }).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
    {
        // 不用 Dapper 的 IN @ids 列表展开：它靠改写具名参数实现，Npgsql 是位置参数（$1），
        // 展开后 PG 报 42601 syntax error at or near "$1"。改为显式占位符 + DynamicParameters。
        IEnumerable<S1Row> rows = await conn.QueryAsync<S1Row>(
            $"SELECT {Cols} FROM {T} WHERE {C("Id")} IN ({Placeholders(ids.Length)})", Parameters(ids))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {T}").ConfigureAwait(false);

    public async Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", row, tran).ConfigureAwait(false);
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>事务内 10 条插入。Dapper 对 IEnumerable 参数走 multi-exec（一次调用 = N 次
    /// ExecuteNonQuery），10/100/批量三档共用同一路径，规模差异由调用方传入的 rows 决定。
    /// 每个入口用自己的契约守卫声明适用规模，避免三档在报告里混为一谈。</summary>
    public async Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows.Count, 10, nameof(rows));
        await TxInsertManyAsync(conn, rows, ct).ConfigureAwait(false);
    }

    /// <summary>事务内 100 条插入——与 <see cref="TxTenInsertsAsync"/> 同一路径不同规模。</summary>
    public async Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows.Count, 11, nameof(rows));
        await TxInsertManyAsync(conn, rows, ct).ConfigureAwait(false);
    }

    private async Task TxInsertManyAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)", rows, tran).ConfigureAwait(false);
        await tran.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await TxInsertManyAsync(conn, rows, ct).ConfigureAwait(false);
    }

    public async Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        await using DbTransaction tran = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (S1Row row in rows)
        {
            await conn.ExecuteAsync(
                $"UPDATE {T} SET {C("Qty")}=@Qty WHERE {C("Id")}=@Id", row, tran).ConfigureAwait(false);
        }

        await tran.RollbackAsync(ct).ConfigureAwait(false);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PalORM——被测对象
//
// 结构说明：PalORM 的 API 是 DataSession<TSession> 的实例方法，而 TSession 在三个 Provider 上
// 不同。泛型 helper 无法表达"同一 lambda 体套三种会话"（C# 泛型约束不支持
// where TSession : DataSession<TSession>），故按方言写三个具体分支。三者的 lambda 体逐位
// 相同——SQL 由 From<T>() 按方言自动生成，调用方无感。
// ─────────────────────────────────────────────────────────────────────────────

// DataSession.DisposeAsync 会关闭并释放构造时传入的 DbConnection
// （见 PalORM.Core DataSession.DisposeCoreAsync：先 CloseAsync 再 DisposeAsync）。
// 而 PerfHub 的三实现共用同一条已打开连接（B76：建连口径一致，否则比值被污染），
// 故本类的会话一律不释放：无活动事务/GridReader 时会话本身没有待释放资源，
// 连接由 RunAsync 的 await using 统一管理。按操作新建会话是与 Dapper 无状态
// 扩展方法对等的用法形态，测的正是"用户写完这行代码要付多少代价"。
[SuppressMessage("Reliability", "CA2000", Justification =
    "DataSession.DisposeAsync 会关闭共享连接；连接由外层统一管理，会话按操作新建是对等用法形态。")]
internal sealed class PalormImpl(DialectInfo dialect) : IPerfImplementation
{
    public string Name => "PalORM";

    private Dialect D => dialect.Dialect;

    /// <summary>方言分派的穷举兜底——正常不可达（<see cref="Dialect"/> 只有三个值）。
    /// 不带 paramName：<c>D</c> 是属性不是形参，用 <c>nameof(D)</c> 会触发
    /// CA2208/S3928（异常参数名必须对应真实形参）。</summary>
    private static NotSupportedException UnsupportedDialect(Dialect dialect)
        => new($"方言 '{dialect}' 不在 PerfHub 覆盖范围内（仅 SQLite / MySQL / PostgreSQL）。");

    /// <summary>按操作新建会话——与 Dapper 无状态扩展方法对等的用法形态。</summary>
    private static DataSession<TProvider> Session<TProvider>(DbConnection conn)
        where TProvider : IDbProvider, new()
        => new(conn, Opts(conn), [], null);

    private static DbOptions Opts(DbConnection conn)
        => new() { ConnectionString = conn.ConnectionString ?? "" };

    // ── 播种：DDL 走 PerfHub 自己的方言枚举，灌数走 PalORM 批量路径 ──
    public Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => SetupCoreAsync<SqliteProvider>(conn, rows, D, ct),
            Dialect.MySql => SetupCoreAsync<MySqlProvider>(conn, rows, D, ct),
            Dialect.PostgreSql => SetupCoreAsync<PostgreSqlProvider>(conn, rows, D, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static async Task SetupCoreAsync<TProvider>(
        DbConnection conn, int rows, Dialect dialect, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        await AdoNetImpl.ExecAsync(conn, Dataset.DropTableSql(dialect), ct).ConfigureAwait(false);
        await AdoNetImpl.ExecAsync(conn, Dataset.CreateTableSql(dialect), ct).ConfigureAwait(false);
        List<S1Row> seed = Dataset.SeedRows(rows);
        await Session<TProvider>(conn).BulkInsertAsync(seed, 1000, ct).ConfigureAwait(false);
    }

    public Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => GetByKeyCoreAsync<SqliteProvider>(conn, id, ct),
            Dialect.MySql => GetByKeyCoreAsync<MySqlProvider>(conn, id, ct),
            Dialect.PostgreSql => GetByKeyCoreAsync<PostgreSqlProvider>(conn, id, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<S1Row?> GetByKeyCoreAsync<TProvider>(DbConnection conn, long id, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>()
            .Where(Dataset.WhereId(TProvider.Dialect, id))
            .FirstOrDefaultAsync(ct).AsTask();

    public Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => QueryAllCoreAsync<SqliteProvider>(conn, ct),
            Dialect.MySql => QueryAllCoreAsync<MySqlProvider>(conn, ct),
            Dialect.PostgreSql => QueryAllCoreAsync<PostgreSqlProvider>(conn, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<List<S1Row>> QueryAllCoreAsync<TProvider>(DbConnection conn, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>().ToListAsync(ct).AsTask();

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => StreamAllCoreAsync<SqliteProvider>(conn, onRow, D, ct),
            Dialect.MySql => StreamAllCoreAsync<MySqlProvider>(conn, onRow, D, ct),
            Dialect.PostgreSql => StreamAllCoreAsync<PostgreSqlProvider>(conn, onRow, D, ct),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>流式全表——与 Dapper 的 <c>QueryUnbufferedAsync</c>、ADO.NET 的
    /// <c>ReadAsync</c> 循环同级对照。
    /// <para><b>为什么用 QueryAsyncEnumerable 而不是 ForEachAsync</b>：<c>ForEachAsync</c>
    /// 是 v5.8 才加的流式终结器（commit ee3e731），v5.5.1 基线没有它——用它就没法和基线对照。
    /// <c>QueryAsyncEnumerable</c> 两版都有，是流式对比的共同口径。</para>
    /// <para><b>列名/表名必须走文本段</b>（B78）：<c>QueryAsyncEnumerable</c> 收 FormattableString，
    /// 写成 <c>$"SELECT {列} FROM {表}"</c> 会把标识符当插值项参数化，拼出
    /// <c>SELECT @p0 FROM @p1</c>——SQLite 直接语法错误。故用 0 插值项的
    /// <c>FormattableStringFactory.Create</c> 承载已拼好的 SQL 文本。</para></summary>
    private static async Task<long> StreamAllCoreAsync<TProvider>(
        DbConnection conn, Func<S1Row, ValueTask> onRow, Dialect dialect, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        long n = 0;
        FormattableString sql = FormattableStringFactory.Create(
            "SELECT " + Dataset.SelectColumns(dialect) + " FROM " + Dataset.Table(dialect));
        await foreach (S1Row? row in Session<TProvider>(conn)
            .QueryAsyncEnumerable<S1Row>(sql, ct).ConfigureAwait(false))
        {
            await onRow(row).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    public Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => InsertCoreAsync<SqliteProvider>(conn, row, ct),
            Dialect.MySql => InsertCoreAsync<MySqlProvider>(conn, row, ct),
            Dialect.PostgreSql => InsertCoreAsync<PostgreSqlProvider>(conn, row, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static async Task InsertCoreAsync<TProvider>(DbConnection conn, S1Row row, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        await Session<TProvider>(conn).InsertAsync(row, ct).ConfigureAwait(false);
    }

    public Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => BulkInsertCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => BulkInsertCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => BulkInsertCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<long> BulkInsertCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).BulkInsertAsync(rows, 1000, ct).AsTask();

    public Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => UpdateCoreAsync<SqliteProvider>(conn, row, ct),
            Dialect.MySql => UpdateCoreAsync<MySqlProvider>(conn, row, ct),
            Dialect.PostgreSql => UpdateCoreAsync<PostgreSqlProvider>(conn, row, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<int> UpdateCoreAsync<TProvider>(DbConnection conn, S1Row row, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).UpdateAsync(row, ct).AsTask();

    public Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => BulkUpdateCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => BulkUpdateCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => BulkUpdateCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<long> BulkUpdateCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).BulkUpdateAsync(rows, ct).AsTask();

    public Task<long> BulkDeleteAsync(DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => BulkDeleteCoreAsync<SqliteProvider>(conn, keys, ct),
            Dialect.MySql => BulkDeleteCoreAsync<MySqlProvider>(conn, keys, ct),
            Dialect.PostgreSql => BulkDeleteCoreAsync<PostgreSqlProvider>(conn, keys, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<long> BulkDeleteCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<object> keys, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).BulkDeleteAsync<S1Row>(keys, ct).AsTask();

    // ── SQL 构建（纯 ORM 开销，不执行）──
    public string BuildGetByKeySql(DbConnection conn, long id)
        => D switch
        {
            Dialect.Sqlite => BuildGetByKeyCore<SqliteProvider>(conn, id),
            Dialect.MySql => BuildGetByKeyCore<MySqlProvider>(conn, id),
            Dialect.PostgreSql => BuildGetByKeyCore<PostgreSqlProvider>(conn, id),
            _ => throw UnsupportedDialect(D)
        };

    private static string BuildGetByKeyCore<TProvider>(DbConnection conn, long id)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>()
            .Where(Dataset.WhereId(TProvider.Dialect, id))
            .AsDryRun().Sql;

    public string BuildComplexQuerySql(DbConnection conn)
        => D switch
        {
            Dialect.Sqlite => BuildComplexCore<SqliteProvider>(conn, D),
            Dialect.MySql => BuildComplexCore<MySqlProvider>(conn, D),
            Dialect.PostgreSql => BuildComplexCore<PostgreSqlProvider>(conn, D),
            _ => throw UnsupportedDialect(D)
        };

    private static string BuildComplexCore<TProvider>(DbConnection conn, Dialect dialect)
        where TProvider : IDbProvider, new()
    {
        // 列名必须走文本段（B78）：写成插值项会被参数化成字符串，PG 报 text = bigint
        FormattableString where = FormattableStringFactory.Create(
            $"{Dataset.Q(dialect, "Qty")} > {{0}} AND {Dataset.Q(dialect, "Marker")} < {{1}}", 100, 900000L);
        FormattableString like = FormattableStringFactory.Create(
            $"{Dataset.Q(dialect, "Name")} LIKE {{0}}", "row-%");
        return Session<TProvider>(conn).From<S1Row>()
            .Where(where)
            .OrWhere(like)
            // 用 OrderBy(descending: true) 而不是 OrderByDescending——后者是 v5.8 才补的
            // 便捷重载，v5.5.1 基线没有它，用了就没法和基线同口径对照。
            .OrderBy(static x => x.Marker, descending: true)
            .Take(100)
            .AsDryRun().Sql;
    }

    public Task<List<S1Row>> KeysetPageAsync(DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => KeysetPageCoreAsync<SqliteProvider>(conn, lastId, pageSize, ct),
            Dialect.MySql => KeysetPageCoreAsync<MySqlProvider>(conn, lastId, pageSize, ct),
            Dialect.PostgreSql => KeysetPageCoreAsync<PostgreSqlProvider>(conn, lastId, pageSize, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<List<S1Row>> KeysetPageCoreAsync<TProvider>(
        DbConnection conn, long lastId, int pageSize, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>()
            .Where(Dataset.WhereIdGt(TProvider.Dialect, lastId))
            .OrderBy(static x => x.Id)
            .Take(pageSize)
            .ToListAsync(ct).AsTask();

    public Task<List<S1Row>> WhereInAsync(DbConnection conn, long[] ids, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => WhereInCoreAsync<SqliteProvider>(conn, ids, ct),
            Dialect.MySql => WhereInCoreAsync<MySqlProvider>(conn, ids, ct),
            Dialect.PostgreSql => WhereInCoreAsync<PostgreSqlProvider>(conn, ids, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<List<S1Row>> WhereInCoreAsync<TProvider>(
        DbConnection conn, long[] ids, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).From<S1Row>().WhereIn(static x => x.Id, ids).ToListAsync(ct).AsTask();

    public Task<long> CountAsync(DbConnection conn, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => CountCoreAsync<SqliteProvider>(conn, ct),
            Dialect.MySql => CountCoreAsync<MySqlProvider>(conn, ct),
            Dialect.PostgreSql => CountCoreAsync<PostgreSqlProvider>(conn, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static Task<long> CountCoreAsync<TProvider>(DbConnection conn, CancellationToken ct)
        where TProvider : IDbProvider, new()
        => Session<TProvider>(conn).CountAsync<S1Row>(ct: ct).AsTask();

    // ── 事务场景：真实业务形态（开启事务 → N 条写 → 提交/回滚）──
    public Task TxSingleInsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TxSingleCoreAsync<SqliteProvider>(conn, row, ct),
            Dialect.MySql => TxSingleCoreAsync<MySqlProvider>(conn, row, ct),
            Dialect.PostgreSql => TxSingleCoreAsync<PostgreSqlProvider>(conn, row, ct),
            _ => throw UnsupportedDialect(D)
        };

    private static async Task TxSingleCoreAsync<TProvider>(DbConnection conn, S1Row row, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        DataSession<TProvider> session = Session<TProvider>(conn);
        await session.WithTransaction(
            async _ => await session.InsertAsync(row, ct).ConfigureAwait(false), ct: ct).ConfigureAwait(false);
    }

    public Task TxTenInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TxInsertManyCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => TxInsertManyCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => TxInsertManyCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    public Task TxHundredInsertsAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TxInsertManyCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => TxInsertManyCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => TxInsertManyCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    public Task TxBulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TxInsertManyCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => TxInsertManyCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => TxInsertManyCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>事务内 N 条插入——10 / 100 / 批量三档共用同一路径，规模差异由调用方传入的
    /// <paramref name="rows"/> 决定（PalORM 没有独立的 bulk-in-transaction 入口，
    /// 这正是要与 Dapper multi-exec 对照的点）。</summary>
    private static async Task TxInsertManyCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        DataSession<TProvider> session = Session<TProvider>(conn);
        await session.WithTransaction(async _ =>
        {
            foreach (S1Row row in rows)
            {
                await session.InsertAsync(row, ct).ConfigureAwait(false);
            }
        }, ct: ct).ConfigureAwait(false);
    }

    public Task TxRollbackAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => D switch
        {
            Dialect.Sqlite => TxRollbackCoreAsync<SqliteProvider>(conn, rows, ct),
            Dialect.MySql => TxRollbackCoreAsync<MySqlProvider>(conn, rows, ct),
            Dialect.PostgreSql => TxRollbackCoreAsync<PostgreSqlProvider>(conn, rows, ct),
            _ => throw UnsupportedDialect(D)
        };

    /// <summary>事务内更新 N 行后主动抛异常触发回滚——测的是"撤销量"而非"提交量"。</summary>
    private static async Task TxRollbackCoreAsync<TProvider>(
        DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        where TProvider : IDbProvider, new()
    {
        DataSession<TProvider> session = Session<TProvider>(conn);
        try
        {
            await session.WithTransaction(async _ =>
            {
                foreach (S1Row row in rows)
                {
                    await session.UpdateAsync(row, ct).ConfigureAwait(false);
                }
                throw new InvalidOperationException("intentional rollback");
            }, ct: ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // 预期的回滚路径——WithTransaction 把 callback 异常转成 rollback 后重新抛出
        }
    }
}
