using System.Data.Common;
using AotProbe.Adapters.MapDataReaderLib;
using MapDataReader;
using Microsoft.Data.Sqlite;
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// MapDataReader 适配器（①类源生成映射器）。无 ORM 面（DDL/事务/聚合/写全 N/A），只测物化路径。
// 写入用原生 ADO（映射器不负责写）；读取用 MapDataReader 源生成映射。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return RunAsync(probe, dbPath).GetAwaiter().GetResult();
    }

    private static async Task<DbConnection> OpenAsync(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await ExecAsync(conn, "CREATE TABLE IF NOT EXISTS ap_order (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, amount TEXT NOT NULL, category TEXT NOT NULL)");
        return conn;
    }

    private static async Task ExecAsync(DbConnection conn, string sql, params (string, object)[] args)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
            _ = cmd.Parameters.Add(new SqliteParameter(n, v));
        _ = await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(DbConnection conn, string sql)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ProbeObservation> RunAsync(ProbeId probe, string dbPath)
    {
        await using DbConnection conn = await OpenAsync(dbPath);
        return probe switch
        {
            ProbeId.P0GetByKey => await GetByKeyAsync(conn),
            ProbeId.P0QueryAll => await QueryAllAsync(conn),
            _ => throw new NotSupportedException($"{probe} is N/A for mapdatareader（纯映射器，无 ORM 写面）"),
        };
    }

    private static async Task<ProbeObservation> GetByKeyAsync(DbConnection conn)
    {
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name1), ("@a", AmountText), ("@c", Cat1));
        long id = await ScalarLongAsync(conn, "SELECT last_insert_rowid()");
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order WHERE Id = @id";
        _ = cmd.Parameters.Add(new SqliteParameter("@id", id));
        await using DbDataReader reader = await cmd.ExecuteReaderAsync();
        OrderRow? found = reader.GetOrderRowFromDataReader();
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static async Task<ProbeObservation> QueryAllAsync(DbConnection conn)
    {
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name1), ("@a", AmountText), ("@c", Cat1));
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name2), ("@a", AmountText), ("@c", Cat1));
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name3), ("@a", AmountText), ("@c", Cat2));
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order ORDER BY Id";
        await using DbDataReader reader = await cmd.ExecuteReaderAsync();
        List<OrderRow> rows = OrderRowMapper.Map(reader).ToList();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }
}
