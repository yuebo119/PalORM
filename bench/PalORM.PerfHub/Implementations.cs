using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using Dapper;
using PalORM;
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
    Task<int> DeleteAsync(DbConnection conn, long id, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// ADO.NET 基线——性能地板。所有比值（Ratio）以它为分母。
// 手写参数化 SQL + 手工物化，是"不靠任何 ORM 能做到的最好情况"。
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class AdoNetImpl(DialectInfo dialect) : IPerfImplementation
{
    public string Name => "ADO_NET";

    private string T => dialect.Dialect == Dialect.MySql ? "`perf_s1`" : "\"perf_s1\"";
    private string Q(string c) => Dataset.Q(dialect.Dialect, c);
    private string Cols => Dataset.SelectColumns(dialect.Dialect);

    public async Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
    {
        await ExecAsync(conn, Dataset.DropTableSql(dialect.Dialect), ct).ConfigureAwait(false);
        await ExecAsync(conn, Dataset.CreateTableSql(dialect.Dialect), ct).ConfigureAwait(false);
        // 批量灌入：500 行一条多值 INSERT（三方言参数上限内的保守值）
        const int batch = 500;
        for (int start = 0; start < rows; start += batch)
        {
            int end = Math.Min(start + batch, rows);
            var sb = new StringBuilder();
            sb.Append("INSERT INTO ").Append(T).Append(" (").Append(Cols).Append(") VALUES ");
            await using var cmd = conn.CreateCommand();
            for (int r = start; r < end; r++)
            {
                if (r > start) sb.Append(", ");
                sb.Append('(');
                for (int c = 0; c < 5; c++)
                {
                    if (c > 0) sb.Append(", ");
                    sb.Append(Dataset.P((r - start) * 5 + c));
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();
            for (int r = start, p = 0; r < end; r++)
            {
                var row = Dataset.Seed(r);
                AddParam(cmd, p++, row.Id);
                AddParam(cmd, p++, row.Name);
                AddParam(cmd, p++, row.Qty);
                AddParam(cmd, p++, row.Price);
                AddParam(cmd, p++, row.Marker);
            }
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T} WHERE {Q("Id")} = {Dataset.P(0)}";
        AddParam(cmd, 0, id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Map(r) : null;
    }

    public async Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T}";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<S1Row>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(Map(r));
        return list;
    }

    public async Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {T}";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        long n = 0;
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            await onRow(Map(r)).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO {T} ({Cols}) VALUES ({Dataset.P(0)}, {Dataset.P(1)}, {Dataset.P(2)}, {Dataset.P(3)}, {Dataset.P(4)})";
        AddParam(cmd, 0, row.Id);
        AddParam(cmd, 1, row.Name);
        AddParam(cmd, 2, row.Qty);
        AddParam(cmd, 3, row.Price);
        AddParam(cmd, 4, row.Marker);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        long total = 0;
        const int batch = 500;
        for (int start = 0; start < rows.Count; start += batch)
        {
            int end = Math.Min(start + batch, rows.Count);
            var sb = new StringBuilder();
            sb.Append("INSERT INTO ").Append(T).Append(" (").Append(Cols).Append(") VALUES ");
            await using var cmd = conn.CreateCommand();
            for (int r = start; r < end; r++)
            {
                if (r > start) sb.Append(", ");
                sb.Append('(');
                for (int c = 0; c < 5; c++)
                {
                    if (c > 0) sb.Append(", ");
                    sb.Append(Dataset.P((r - start) * 5 + c));
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();
            for (int r = start, p = 0; r < end; r++)
            {
                var row = rows[r];
                AddParam(cmd, p++, row.Id);
                AddParam(cmd, p++, row.Name);
                AddParam(cmd, p++, row.Qty);
                AddParam(cmd, p++, row.Price);
                AddParam(cmd, p++, row.Marker);
            }
            total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {T} SET {Q("Name")} = {Dataset.P(0)}, {Q("Qty")} = {Dataset.P(1)}, "
            + $"{Q("Price")} = {Dataset.P(2)}, {Q("Marker")} = {Dataset.P(3)} WHERE {Q("Id")} = {Dataset.P(4)}";
        AddParam(cmd, 0, row.Name);
        AddParam(cmd, 1, row.Qty);
        AddParam(cmd, 2, row.Price);
        AddParam(cmd, 3, row.Marker);
        AddParam(cmd, 4, row.Id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        // ADO.NET 无批量 UPDATE 抽象——逐条执行（这正是 ORM 批量路径要对比的地板）
        long total = 0;
        foreach (var row in rows)
            total += await UpdateAsync(conn, row, ct).ConfigureAwait(false);
        return total;
    }

    public async Task<int> DeleteAsync(DbConnection conn, long id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T} WHERE {Q("Id")} = {Dataset.P(0)}";
        AddParam(cmd, 0, id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddParam(DbCommand cmd, int index, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = Dataset.P(index);
        p.Value = value ?? DBNull.Value;
        // 显式 DbType：Npgsql 对 object 装箱的 long/int 可能推断为 text，导致
        // "operator does not exist: text = bigint"。ADO.NET 基线的参数化必须显式，
        // 否则测的是驱动推断差异而非 ORM 差异。
        switch (value)
        {
            case long: p.DbType = System.Data.DbType.Int64; break;
            case int: p.DbType = System.Data.DbType.Int32; break;
            case decimal: p.DbType = System.Data.DbType.Decimal; break;
            case bool: p.DbType = System.Data.DbType.Boolean; break;
            case string: p.DbType = System.Data.DbType.String; break;
        }
        cmd.Parameters.Add(p);
    }

    internal static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
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

    private string T => Dataset.Q(dialect.Dialect, "perf_s1");
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
        var rows = await conn.QueryAsync<S1Row>($"SELECT {Cols} FROM {T}").ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
    {
        long n = 0;
        await foreach (var row in conn.QueryUnbufferedAsync<S1Row>($"SELECT {Cols} FROM {T}").ConfigureAwait(false))
        {
            await onRow(row).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    public async Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
            row).ConfigureAwait(false);

    public async Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
        => await conn.ExecuteAsync(
            $"INSERT INTO {T} ({Cols}) VALUES (@Id,@Name,@Qty,@Price,@Marker)",
            rows).ConfigureAwait(false);

    public async Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
        => await conn.ExecuteAsync(
            $"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id",
            row).ConfigureAwait(false);

    public Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        // Dapper 无批量 UPDATE——逐条执行（与 ADO.NET 地板同构）
        long total = 0;
        foreach (var row in rows)
            total += conn.Execute(
                $"UPDATE {T} SET {C("Name")}=@Name, {C("Qty")}=@Qty, {C("Price")}=@Price, {C("Marker")}=@Marker WHERE {C("Id")}=@Id",
                row);
        return Task.FromResult(total);
    }

    public async Task<int> DeleteAsync(DbConnection conn, long id, CancellationToken ct)
        => await conn.ExecuteAsync($"DELETE FROM {T} WHERE {C("Id")} = @id", new { id }).ConfigureAwait(false);
}

// ─────────────────────────────────────────────────────────────────────────────
// PalORM——被测对象
//
// 结构说明：PalORM 的 From<T>()/InsertAsync 等是 DataSession<TSession> 的实例方法，
/// 而 TSession 在三个 Provider 上不同。泛型 helper 无法表达"同一 lambda 体套三种会话"
/// （C# 泛型约束不支持 where TSession : DataSession<TSession>），故按方言写三个薄
/// helper。三者的 lambda 体逐位相同——SQL 由 From<T>() 按方言自动生成，调用方无感。
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class PalormImpl(DialectInfo dialect) : IPerfImplementation
{
    public string Name => "PalORM";

    /// <summary>会话缓存——PerfHub 对同一连接复用同一会话。
    /// <para>为什么复用：每次操作新建会话会让 SessionOperationState/AsyncLocal 反复初始化，
    /// 且掩盖了真实使用形态（用户不会每查询建一次会话）。连接由 PerfHub 持有并释放。</para></summary>
    private DataSession<SqliteProvider>? _sqlite;
    private DataSession<MySqlProvider>? _mysql;
    private DataSession<PostgreSqlProvider>? _pg;

    private object SessionLock => this;

    public async Task SetupAsync(DbConnection conn, int rows, CancellationToken ct)
    {
        await AdoNetImpl.ExecAsync(conn, Dataset.DropTableSql(dialect.Dialect), ct).ConfigureAwait(false);
        await AdoNetImpl.ExecAsync(conn, Dataset.CreateTableSql(dialect.Dialect), ct).ConfigureAwait(false);
        var seed = Dataset.SeedRows(rows);
        switch (dialect.Dialect)
        {
            case Dialect.Sqlite:
                await Using(new DataSession<SqliteProvider>(conn, Opts(conn), []),
                    s => s.BulkInsertAsync(seed, 1000, CancellationToken.None).AsTask()).ConfigureAwait(false);
                break;
            case Dialect.MySql:
                await Using(new DataSession<MySqlProvider>(conn, Opts(conn), []),
                    s => s.BulkInsertAsync(seed, 1000, CancellationToken.None).AsTask()).ConfigureAwait(false);
                break;
            default:
                await Using(new DataSession<PostgreSqlProvider>(conn, Opts(conn), []),
                    s => s.BulkInsertAsync(seed, 1000, CancellationToken.None).AsTask()).ConfigureAwait(false);
                break;
        }
    }

    public Task<S1Row?> GetByKeyAsync(DbConnection conn, long id, CancellationToken ct)
    {
        var o = Opts(conn);
        return dialect.Dialect switch
        {
            Dialect.Sqlite => Using(new DataSession<SqliteProvider>(conn, o, []),
                s => s.From<S1Row>().Where(WhereId(Dialect.Sqlite, id)).FirstOrDefaultAsync(ct).AsTask()),
            Dialect.MySql => Using(new DataSession<MySqlProvider>(conn, o, []),
                s => s.From<S1Row>().Where(WhereId(Dialect.MySql, id)).FirstOrDefaultAsync(ct).AsTask()),
            _ => Using(new DataSession<PostgreSqlProvider>(conn, o, []),
                s => s.From<S1Row>().Where(WhereId(Dialect.PostgreSql, id)).FirstOrDefaultAsync(ct).AsTask())
        };
    }

    public Task<List<S1Row>> QueryAllAsync(DbConnection conn, CancellationToken ct)
    {
        var o = Opts(conn);
        return dialect.Dialect switch
        {
            Dialect.Sqlite => Using(new DataSession<SqliteProvider>(conn, o, []),
                s => s.From<S1Row>().ToListAsync(ct).AsTask()),
            Dialect.MySql => Using(new DataSession<MySqlProvider>(conn, o, []),
                s => s.From<S1Row>().ToListAsync(ct).AsTask()),
            _ => Using(new DataSession<PostgreSqlProvider>(conn, o, []),
                s => s.From<S1Row>().ToListAsync(ct).AsTask())
        };
    }

    public Task<long> StreamAllAsync(DbConnection conn, Func<S1Row, ValueTask> onRow, CancellationToken ct)
    {
        var o = Opts(conn);
        return dialect.Dialect switch
        {
            Dialect.Sqlite => Using(new DataSession<SqliteProvider>(conn, o, []),
                s => s.From<S1Row>().ForEachAsync((row, _) => onRow(row), ct).AsTask()),
            Dialect.MySql => Using(new DataSession<MySqlProvider>(conn, o, []),
                s => s.From<S1Row>().ForEachAsync((row, _) => onRow(row), ct).AsTask()),
            _ => Using(new DataSession<PostgreSqlProvider>(conn, o, []),
                s => s.From<S1Row>().ForEachAsync((row, _) => onRow(row), ct).AsTask())
        };
    }

    public Task InsertAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        var o = Opts(conn);
        return dialect.Dialect switch
        {
            Dialect.Sqlite => Using(new DataSession<SqliteProvider>(conn, o, []),
                s => s.InsertAsync(row, ct).AsTask()),
            Dialect.MySql => Using(new DataSession<MySqlProvider>(conn, o, []),
                s => s.InsertAsync(row, ct).AsTask()),
            _ => Using(new DataSession<PostgreSqlProvider>(conn, o, []),
                s => s.InsertAsync(row, ct).AsTask())
        };
    }

    public Task<long> BulkInsertAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        var o = Opts(conn);
        return dialect.Dialect switch
        {
            Dialect.Sqlite => Using(new DataSession<SqliteProvider>(conn, o, []),
                s => s.BulkInsertAsync(rows, 1000, ct).AsTask()),
            Dialect.MySql => Using(new DataSession<MySqlProvider>(conn, o, []),
                s => s.BulkInsertAsync(rows, 1000, ct).AsTask()),
            _ => Using(new DataSession<PostgreSqlProvider>(conn, o, []),
                s => s.BulkInsertAsync(rows, 1000, ct).AsTask())
        };
    }

    public Task<int> UpdateAsync(DbConnection conn, S1Row row, CancellationToken ct)
    {
        var o = Opts(conn);
        return dialect.Dialect switch
        {
            Dialect.Sqlite => Using(new DataSession<SqliteProvider>(conn, o, []),
                s => s.UpdateAsync(row, ct).AsTask()),
            Dialect.MySql => Using(new DataSession<MySqlProvider>(conn, o, []),
                s => s.UpdateAsync(row, ct).AsTask()),
            _ => Using(new DataSession<PostgreSqlProvider>(conn, o, []),
                s => s.UpdateAsync(row, ct).AsTask())
        };
    }

    public Task<long> BulkUpdateAsync(DbConnection conn, IReadOnlyList<S1Row> rows, CancellationToken ct)
    {
        var o = Opts(conn);
        return dialect.Dialect switch
        {
            Dialect.Sqlite => Using(new DataSession<SqliteProvider>(conn, o, []),
                s => s.BulkUpdateAsync(rows, ct).AsTask()),
            Dialect.MySql => Using(new DataSession<MySqlProvider>(conn, o, []),
                s => s.BulkUpdateAsync(rows, ct).AsTask()),
            _ => Using(new DataSession<PostgreSqlProvider>(conn, o, []),
                s => s.BulkUpdateAsync(rows, ct).AsTask())
        };
    }

    public Task<int> DeleteAsync(DbConnection conn, long id, CancellationToken ct)
    {
        var o = Opts(conn);
        return dialect.Dialect switch
        {
            Dialect.Sqlite => Using(new DataSession<SqliteProvider>(conn, o, []),
                s => s.DeleteAsync<S1Row>(id, ct).AsTask()),
            Dialect.MySql => Using(new DataSession<MySqlProvider>(conn, o, []),
                s => s.DeleteAsync<S1Row>(id, ct).AsTask()),
            _ => Using(new DataSession<PostgreSqlProvider>(conn, o, []),
                s => s.DeleteAsync<S1Row>(id, ct).AsTask())
        };
    }

    /// <summary>点查条件——列名拼进文本段（经 QuoteIdentifier 转义），只有 id 是插值项。
    /// <para>踩坑记录：若写成 <c>$"{Q("Id")} = {id}"</c>，列名也会变成插值项并被参数化，
    /// 生成 <c>WHERE (@p0 = @p1)</c> 且 @p0 是字符串——PG 报 "operator does not exist:
    /// text = bigint"。列名是标识符面，必须走文本段。</para></summary>
    private static FormattableString WhereId(Dialect dialect, long id)
    {
        string quoted = Dataset.Q(dialect, "Id");
        return FormattableStringFactory.Create(quoted + " = {0}", id);
    }

    private static DbOptions Opts(DbConnection conn)
        => new() { ConnectionString = conn.ConnectionString ?? "" };

    /// <summary>在会话上执行操作。
    /// <para><b>为什么不 Dispose 会话</b>：PerfHub 的会话复用调用方已打开的连接
    /// （三实现必须同连接，否则"建连口径不一致"会污染 ORM/地板比值，见 lessons B76），
    /// 而 <c>DataSession.DisposeAsync</c> 的契约是「会话持有连接」——它会 Close +
    /// Dispose 主连接。这里只借会话执行一次操作，操作完成后会话自身无待释放资源
    /// （无后台任务、无持有连接），连接生命周期归 PerfHub 的 using 管。</para>
    /// <para><b>这不是资源泄漏</b>：每次操作新建的会话在方法返回后即不可达，
    /// 其 SessionOperationState 的租约已随操作完成归还（Enter/Exit 配对）。</para></summary>
    private static Task<T> Using<TSession, T>(
        TSession session, Func<TSession, Task<T>> body)
        => body(session);

    private static Task Using<TSession>(
        TSession session, Func<TSession, Task> body)
        => body(session);
}
