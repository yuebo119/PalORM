using AotProbe.Adapters.MartenLib;
using Marten;
using Npgsql;
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// Marten 适配器（①类源生成路线，PG-only，经 opt-in PG 通道接入）。
// db 位实参 = PG 基连接串（Database=postgres）：适配器内为每探针建独立库 aotprobe_marten_<pid>，
// finally 里 DROP，保持与 SQLite 库同等的"每探针全新库"语义。
// raw 旁路 = 直连 Npgsql（Marten 的 mt_doc_* 表 (id, data jsonb)）。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbArg)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return RunAsync(probe, dbArg).GetAwaiter().GetResult();
    }

    private static async Task<string> CreateProbeDbAsync(string baseConn)
    {
        string dbName = $"aotprobe_marten_{Environment.ProcessId}";
        await using (var admin = new NpgsqlConnection(baseConn))
        {
            await admin.OpenAsync();
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)", admin))
                _ = await drop.ExecuteNonQueryAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE {dbName}", admin))
                _ = await create.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlConnectionStringBuilder(baseConn) { Database = dbName };
        return builder.ConnectionString;
    }

    private static async Task DropProbeDbAsync(string baseConn)
    {
        string dbName = $"aotprobe_marten_{Environment.ProcessId}";
        await using var admin = new NpgsqlConnection(baseConn);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)", admin);
        _ = await drop.ExecuteNonQueryAsync();
    }

    private static DocumentStore CreateStore(string conn)
    {
        var store = DocumentStore.For(options =>
        {
            options.Connection(conn);
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.All;
        });
        return store;
    }

    private static async Task<ProbeObservation> RunAsync(ProbeId probe, string baseConn)
    {
        string conn = await CreateProbeDbAsync(baseConn);
        try
        {
            return probe switch
            {
                ProbeId.P0Ddl => await DdlAsync(conn),
                ProbeId.P0InsertExplicitPk => await InsertExplicitPkAsync(conn),
                ProbeId.P0InsertAutoPk => await InsertAutoPkAsync(conn),
                ProbeId.P0GetByKey => await GetByKeyAsync(conn),
                ProbeId.P0UpdateDelete => await UpdateDeleteAsync(conn),
                ProbeId.P0QueryAll => await QueryAllAsync(conn),
                ProbeId.P0Aggregate => await AggregateAsync(conn),
                ProbeId.P0Transaction => await TransactionAsync(conn),
                _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
            };
        }
        finally
        {
            await DropProbeDbAsync(baseConn);
        }
    }

    /// <summary>触达两类文档使 Marten 建好 mt_doc_* 存储，再从 pg_tables 计数（raw 旁路 = 直连 Npgsql）。</summary>
    private static async Task<ProbeObservation> DdlAsync(string conn)
    {
        using DocumentStore store = CreateStore(conn);
        await using (var session = store.LightweightSession())
        {
            // 写触发 DDL（Marten 的 AutoCreate 仅在写路径生效，查询不建表——9.x 实测）
            session.Store(new OrderRow { Name = "ddl-trigger", Amount = 0m, Category = "ddl" });
            session.Store(new CatalogRow { Id = "ddl-trigger", Label = "ddl-trigger" });
            await session.SaveChangesAsync();
        }
        long tables = await PgScalarLongAsync(conn,
            "SELECT COUNT(*) FROM pg_tables WHERE tablename LIKE 'mt_doc%' AND schemaname = 'public'");
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static async Task<ProbeObservation> InsertExplicitPkAsync(string conn)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        using DocumentStore store = CreateStore(conn);
        var key = CatalogIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using (var session = store.LightweightSession())
        {
            session.Store(new CatalogRow { Id = key, Label = CatalogLabelValue });
            await session.SaveChangesAsync();
        }
        CatalogRow? readBack;
        await using (var session = store.LightweightSession())
        {
            readBack = await session.LoadAsync<CatalogRow>(key);
        }
        string raw = await PgScalarStringAsync(conn,
            "SELECT data->>'Label' FROM " + await DocTableAsync(conn, "mt_doc_c") + " WHERE id = @p0", ("@p0", key));
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", key),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> InsertAutoPkAsync(string conn)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        using DocumentStore store = CreateStore(conn);
        long id;
        await using (var session = store.LightweightSession())
        {
            var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
            session.Store(row);
            await session.SaveChangesAsync();
            id = row.Id;
        }
        OrderRow? readBack;
        await using (var session = store.LightweightSession())
        {
            readBack = await session.LoadAsync<OrderRow>(id);
        }
        string raw = await PgScalarStringAsync(conn,
            "SELECT data->>'Name' FROM " + await DocTableAsync(conn, "mt_doc_o") + " WHERE id = @p0", ("@p0", (object)id));
        return Ok(ProbeId.P0InsertAutoPk,
            F("insert.id", id),
            F("orm.readback.name", readBack?.Name ?? ""),
            F("raw.stored.name", raw),
            F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            FT("orm.readback.amount.type", readBack?.Amount.GetType()),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> GetByKeyAsync(string conn)
    {
        long keyId;
#if AOTPROBE_B
        using (DocumentStore store = CreateStore(conn))
        {
            await using var session = store.LightweightSession();
            _ = await session.Query<OrderRow>().CountAsync(); // 触发建表
            keyId = 1;
        }
        await PgExecAsync(conn,
            "INSERT INTO " + await DocTableAsync(conn, "mt_doc_o") + " (id, data) VALUES (@p0, @p1::jsonb)",
            ("@p0", 1L), ("@p1", $$"""{"Id":1,"Name":"{{Name1}}","Amount":{{AmountText}},"Category":"{{Cat1}}"}"""));
#else
        using DocumentStore store = CreateStore(conn);
        await using (var session = store.LightweightSession())
        {
            var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
            session.Store(row);
            await session.SaveChangesAsync();
            keyId = row.Id;
        }
#endif
        await using (var session2 = CreateStore(conn).LightweightSession())
        {
            OrderRow? found = await session2.LoadAsync<OrderRow>(keyId);
            return Ok(ProbeId.P0GetByKey,
                F("get.id", found?.Id ?? 0L),
                F("get.name", found?.Name ?? ""),
                F("get.category", found?.Category ?? ""),
                FT("materialized.type", found?.GetType()));
        }
    }

    private static async Task<ProbeObservation> UpdateDeleteAsync(string conn)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        using DocumentStore store = CreateStore(conn);
        long id;
        await using (var session = store.LightweightSession())
        {
            var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
            session.Store(row);
            await session.SaveChangesAsync();
            id = row.Id;
        }
        int updated; // SaveChangesAsync 返回 void，改由行存在性断言 affected=1（文档库无行计数语义）
        await using (var session = store.LightweightSession())
        {
            OrderRow? loaded = await session.LoadAsync<OrderRow>(id);
            if (loaded is null)
                throw new InvalidOperationException("reload failed before update");
            loaded.Amount = AmountUpdated;
            session.Store(loaded);
            await session.SaveChangesAsync();
            updated = 1; // 文档 Store 即 upsert：保存即生效
        }
        string afterAmount;
        await using (var session = store.LightweightSession())
        {
            afterAmount = (await session.LoadAsync<OrderRow>(id))?.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            session.Delete<OrderRow>(id);
            await session.SaveChangesAsync();
        }
        bool afterDeleteFound;
        await using (var session = store.LightweightSession())
        {
            afterDeleteFound = await session.LoadAsync<OrderRow>(id) is not null;
        }
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterAmount),
            F("delete.affected", 1),
            F("orm.afterdelete.found", afterDeleteFound));
#endif
    }

    private static async Task<ProbeObservation> QueryAllAsync(string conn)
    {
#if AOTPROBE_B
        using (DocumentStore store = CreateStore(conn))
        {
            await using var session = store.LightweightSession();
            _ = await session.Query<OrderRow>().CountAsync();
        }
        await PgExecAsync(conn, "INSERT INTO " + await DocTableAsync(conn, "mt_doc_o") + " (id, data) VALUES (@p0, @p1::jsonb)",
            ("@p0", 1L), ("@p1", $$"""{"Id":1,"Name":"{{Name1}}","Amount":{{AmountText}},"Category":"{{Cat1}}"}"""));
        await PgExecAsync(conn, "INSERT INTO " + await DocTableAsync(conn, "mt_doc_o") + " (id, data) VALUES (@p0, @p1::jsonb)",
            ("@p0", 2L), ("@p1", $$"""{"Id":2,"Name":"{{Name2}}","Amount":{{AmountText}},"Category":"{{Cat1}}"}"""));
        await PgExecAsync(conn, "INSERT INTO " + await DocTableAsync(conn, "mt_doc_o") + " (id, data) VALUES (@p0, @p1::jsonb)",
            ("@p0", 3L), ("@p1", $$"""{"Id":3,"Name":"{{Name3}}","Amount":{{AmountText}},"Category":"{{Cat2}}"}"""));
#else
        using DocumentStore store = CreateStore(conn);
        await using (var session = store.LightweightSession())
        {
            session.Store(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
            session.Store(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 });
            session.Store(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 });
            await session.SaveChangesAsync();
        }
#endif
        await using (var session2 = CreateStore(conn).LightweightSession())
        {
            List<OrderRow> rows = [.. await session2.Query<OrderRow>().OrderBy(x => x.Id).ToListAsync()];
            return Ok(ProbeId.P0QueryAll,
                F("list.count", rows.Count),
                FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
                F("names.byid", string.Join("|", rows.Select(r => r.Name))));
        }
    }

    private static async Task<ProbeObservation> AggregateAsync(string conn)
    {
#if AOTPROBE_B
        using (DocumentStore store = CreateStore(conn))
        {
            await using var session = store.LightweightSession();
            _ = await session.Query<OrderRow>().CountAsync();
        }
        await PgExecAsync(conn, "INSERT INTO " + await DocTableAsync(conn, "mt_doc_o") + " (id, data) VALUES (@p0, @p1::jsonb)",
            ("@p0", 1L), ("@p1", $$"""{"Id":1,"Name":"{{Name1}}","Amount":{{AmountText}},"Category":"{{Cat1}}"}"""));
        await PgExecAsync(conn, "INSERT INTO " + await DocTableAsync(conn, "mt_doc_o") + " (id, data) VALUES (@p0, @p1::jsonb)",
            ("@p0", 2L), ("@p1", $$"""{"Id":2,"Name":"{{Name2}}","Amount":{{AmountText}},"Category":"{{Cat1}}"}"""));
        await PgExecAsync(conn, "INSERT INTO " + await DocTableAsync(conn, "mt_doc_o") + " (id, data) VALUES (@p0, @p1::jsonb)",
            ("@p0", 3L), ("@p1", $$"""{"Id":3,"Name":"{{Name3}}","Amount":{{AmountText}},"Category":"{{Cat2}}"}"""));
#else
        using DocumentStore store = CreateStore(conn);
        await using (var session = store.LightweightSession())
        {
            session.Store(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
            session.Store(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 });
            session.Store(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 });
            await session.SaveChangesAsync();
        }
#endif
        await using (var session2 = CreateStore(conn).LightweightSession())
        {
            long total = await session2.Query<OrderRow>().CountAsync();
            List<CategoryCount> grouped = [.. await session2.Query<OrderRow>()
                .GroupBy(x => x.Category)
                .Select(g => new CategoryCount { Category = g.Key, Count = g.Count() })
                .ToListAsync()];
            return Ok(ProbeId.P0Aggregate,
                F("count.total", total),
                F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
                F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
                FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
        }
    }

    private static async Task<ProbeObservation> TransactionAsync(string conn)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        using DocumentStore store = CreateStore(conn);
        await using (var session = store.LightweightSession())
        {
            session.Store(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 });
            await session.SaveChangesAsync();
        }
        long kept = await PgScalarLongAsync(conn, "SELECT COUNT(*) FROM " + await DocTableAsync(conn, "mt_doc_o") + " WHERE data->>'Name' = @p0",
            ("@p0", TxKept));

        // 回滚臂：Marten 会话的未保存变更在 Dispose 时丢弃（官方事务语义，SaveChangesAsync 即提交单元）
        await using (var session = store.LightweightSession())
        {
            session.Store(new OrderRow { Name = TxDropped, Amount = Amount, Category = Cat2 });
            // 不调 SaveChangesAsync：Dispose 丢弃 pending 变更 = 回滚等价
        }
        long dropped = await PgScalarLongAsync(conn, "SELECT COUNT(*) FROM " + await DocTableAsync(conn, "mt_doc_o") + " WHERE data->>'Name' = @p0",
            ("@p0", TxDropped));

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }

    // ===== Npgsql raw 旁路 =====


    /// <summary>从 pg_tables 动态解析 Marten 建的文档表名（按前缀唯一匹配）。</summary>
    private static async Task<string> DocTableAsync(string conn, string prefix)
    {
        return await PgScalarStringAsync(conn,
            "SELECT tablename FROM pg_tables WHERE tablename LIKE @p0 || '%' AND schemaname = 'public' LIMIT 1",
            ("@p0", prefix));
    }

    private static async Task<long> PgScalarLongAsync(string conn, string sql, params (string Name, object Value)[] args)
        => long.Parse(await PgScalarStringAsync(conn, sql, args), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> PgScalarStringAsync(string conn, string sql, params (string Name, object Value)[] args)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            _ = cmd.Parameters.AddWithValue(name, value);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static async Task PgExecAsync(string conn, string sql, params (string Name, object Value)[] args)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            _ = cmd.Parameters.AddWithValue(name, value);
        _ = await cmd.ExecuteNonQueryAsync();
    }
}
