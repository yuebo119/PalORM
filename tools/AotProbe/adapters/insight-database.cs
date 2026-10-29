using System.Data.Common;
using AotProbe.Adapters.InsightDatabase;
using Microsoft.Data.Sqlite;
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// Insight.Database 适配器（③类反射 + 运行时代理）。
// Insight API 围绕存储过程设计（默认 CommandType.StoredProcedure），与 SQLite 不兼容；
// 降级为原生 ADO 物化验证（反射物化器在 AOT 下即死，与 SmartSql 同策略）。
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
        await RawChannel.EnsureOrderTableAsync(dbPath);
        await RawChannel.EnsureCatalogTableAsync(dbPath);
        return conn;
    }

    private static async Task<ProbeObservation> RunAsync(ProbeId probe, string dbPath)
    {
        await using DbConnection conn = await OpenAsync(dbPath);
        return probe switch
        {
            ProbeId.P0Ddl => await DdlAsync(conn, dbPath),
            ProbeId.P0InsertExplicitPk => await InsertExplicitPkAsync(conn, dbPath),
            ProbeId.P0InsertAutoPk => await InsertAutoPkAsync(conn, dbPath),
            ProbeId.P0GetByKey => await GetByKeyAsync(conn),
            ProbeId.P0UpdateDelete => await UpdateDeleteAsync(conn),
            ProbeId.P0QueryAll => await QueryAllAsync(conn),
            ProbeId.P0Aggregate => await AggregateAsync(conn),
            ProbeId.P0Transaction => await TransactionAsync(conn, dbPath),
            _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
        };
    }

    private static async Task<ProbeObservation> DdlAsync(DbConnection conn, string dbPath)
    {
        long tables = await RawChannel.TableCountAsync(dbPath);
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static async Task<ProbeObservation> InsertExplicitPkAsync(DbConnection conn, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("A-group only");
#else
        await RawChannel.SeedCatalogAsync(dbPath, CatalogIdValue, CatalogLabelValue);
        CatalogRow? readBack = await QueryOneAsync<CatalogRow>(conn,
            "SELECT Id, label AS Label FROM ap_catalog WHERE Id = @Id", ("@Id", CatalogIdValue));
        string raw = await RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue));
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", CatalogIdValue),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> InsertAutoPkAsync(DbConnection conn, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("A-group only");
#else
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name1), ("@a", AmountText), ("@c", Cat1));
        long id = await ScalarLongAsync(conn, "SELECT last_insert_rowid()");
        OrderRow? readBack = await QueryOneAsync<OrderRow>(conn,
            "SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order WHERE Id = @Id",
            ("@Id", (object)id));
        string raw = await RawChannel.ScalarStringAsync(dbPath, "SELECT name FROM ap_order WHERE Id = @p0",
            ("@p0", (object)id));
        return Ok(ProbeId.P0InsertAutoPk,
            F("insert.id", id),
            F("orm.readback.name", readBack?.Name ?? ""),
            F("raw.stored.name", raw),
            F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            FT("orm.readback.amount.type", readBack?.Amount.GetType()),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> GetByKeyAsync(DbConnection conn)
    {
#if AOTPROBE_B
        throw new NotSupportedException("B-group: seed not wired");
#else
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name1), ("@a", AmountText), ("@c", Cat1));
        long id = await ScalarLongAsync(conn, "SELECT last_insert_rowid()");
        OrderRow? found = await QueryOneAsync<OrderRow>(conn,
            "SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order WHERE Id = @Id",
            ("@Id", (object)id));
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> UpdateDeleteAsync(DbConnection conn)
    {
#if AOTPROBE_B
        throw new NotSupportedException("A-group only");
#else
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name1), ("@a", AmountText), ("@c", Cat1));
        long id = await ScalarLongAsync(conn, "SELECT last_insert_rowid()");
        int updated = await ExecAsync(conn, "UPDATE ap_order SET amount = @a WHERE Id = @Id",
            ("@a", AmountUpdatedText), ("@Id", (object)id));
        OrderRow? afterUpdate = await QueryOneAsync<OrderRow>(conn,
            "SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order WHERE Id = @Id",
            ("@Id", (object)id));
        int deleted = await ExecAsync(conn, "DELETE FROM ap_order WHERE Id = @Id",
            ("@Id", (object)id));
        OrderRow? afterDelete = await QueryOneAsync<OrderRow>(conn,
            "SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order WHERE Id = @Id",
            ("@Id", (object)id));
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterUpdate is null ? "" : afterUpdate.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDelete is not null));
#endif
    }

    private static async Task<ProbeObservation> QueryAllAsync(DbConnection conn)
    {
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name1), ("@a", AmountText), ("@c", Cat1));
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name2), ("@a", AmountText), ("@c", Cat1));
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name3), ("@a", AmountText), ("@c", Cat2));
        List<OrderRow> rows = await QueryListAsync<OrderRow>(conn,
            "SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order ORDER BY Id");
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static async Task<ProbeObservation> AggregateAsync(DbConnection conn)
    {
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name1), ("@a", AmountText), ("@c", Cat1));
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name2), ("@a", AmountText), ("@c", Cat1));
        await ExecAsync(conn, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            ("@n", Name3), ("@a", AmountText), ("@c", Cat2));
        long total = await ScalarLongAsync(conn, "SELECT COUNT(*) FROM ap_order");
        List<CategoryCount> grouped = await QueryListAsync<CategoryCount>(conn,
            "SELECT category AS Category, COUNT(*) AS Count FROM ap_order GROUP BY category");
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static async Task<ProbeObservation> TransactionAsync(DbConnection conn, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("A-group only");
#else
        await using (DbTransaction tx = await conn.BeginTransactionAsync())
        {
            await ExecInTxAsync(conn, tx, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
                ("@n", TxKept), ("@a", AmountText), ("@c", Cat1));
            await tx.CommitAsync();
        }
        long kept = await RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept);

        await using (DbTransaction tx2 = await conn.BeginTransactionAsync())
        {
            await ExecInTxAsync(conn, tx2, "INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
                ("@n", TxDropped), ("@a", AmountText), ("@c", Cat2));
            await tx2.RollbackAsync();
        }
        long dropped = await RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped);

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }

    // ===== 简易 ADO 助手（Insight API 围绕存储过程，与 SQLite 不兼容；反射物化器在 AOT 下即死） =====

    private static async Task<int> ExecAsync(DbConnection conn, string sql, params (string, object)[] args)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
            _ = cmd.Parameters.Add(new SqliteParameter(n, v));
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecInTxAsync(DbConnection conn, DbTransaction tx, string sql, params (string, object)[] args)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (n, v) in args)
            _ = cmd.Parameters.Add(new SqliteParameter(n, v));
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(DbConnection conn, string sql)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<T?> QueryOneAsync<T>(DbConnection conn, string sql, params (string, object)[] args)
        where T : class, new()
    {
        List<T> list = await QueryListAsync<T>(conn, sql, args);
        return list.FirstOrDefault();
    }

    private static async Task<List<T>> QueryListAsync<T>(DbConnection conn, string sql, params (string, object)[] args)
        where T : class, new()
    {
        var results = new List<T>();
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
            _ = cmd.Parameters.Add(new SqliteParameter(n, v));
        await using DbDataReader reader = await cmd.ExecuteReaderAsync();
        var props = typeof(T).GetProperties();
        while (await reader.ReadAsync())
        {
            var item = new T();
            foreach (var prop in props)
            {
                int ordinal = reader.GetOrdinal(prop.Name);
                if (!reader.IsDBNull(ordinal))
                {
                    var value = reader.GetValue(ordinal);
                    if (prop.PropertyType == typeof(decimal) && value is string str)
                        prop.SetValue(item, decimal.Parse(str, System.Globalization.CultureInfo.InvariantCulture));
                    else if (prop.PropertyType == typeof(decimal) && value is long l)
                        prop.SetValue(item, (decimal)l);
                    else if (prop.PropertyType == typeof(long) && value is int i)
                        prop.SetValue(item, (long)i);
                    else
                        prop.SetValue(item, value);
                }
            }
            results.Add(item);
        }
        return results;
    }
}
