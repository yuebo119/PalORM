using System.Data.Common;
using AotProbe.Adapters.DapperAot;
using Dapper;
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

// 官方 AOT 形态：程序集级开关强制全量拦截（无此标注时仅部分调用被源生成接管，
// 未拦截的回退 ReflectionEmit 在 AOT 下 PlatformNotSupportedException——IL3053 同证，2026-10-28 实测）
[assembly: DapperAot]

namespace AotProbe.Adapters;

// Dapper.AOT 适配器（官方 AOT 路径 = Dapper.AOT 包，插值参数化由源生成器编译期展开）。
// 读走 FormattableString 插值（Dapper.AOT 核心场景）；事务内写走匿名对象参数（官方双形态）。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return RunAsync(probe, dbPath).GetAwaiter().GetResult();
    }

    private static async Task<ProbeObservation> RunAsync(ProbeId probe, string dbPath)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        return probe switch
        {
            ProbeId.P0Ddl => await DdlAsync(conn, dbPath),
            ProbeId.P0InsertExplicitPk => await InsertExplicitPkAsync(conn, dbPath),
            ProbeId.P0InsertAutoPk => await InsertAutoPkAsync(conn, dbPath),
            ProbeId.P0GetByKey => await GetByKeyAsync(conn, dbPath),
            ProbeId.P0UpdateDelete => await UpdateDeleteAsync(conn),
            ProbeId.P0QueryAll => await QueryAllAsync(conn, dbPath),
            ProbeId.P0Aggregate => await AggregateAsync(conn, dbPath),
            ProbeId.P0Transaction => await TransactionAsync(conn, dbPath),
            _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
        };
    }

    private static async Task EnsureTablesAsync(DbConnection conn)
    {
        await conn.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS ap_order (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, amount TEXT NOT NULL, category TEXT NOT NULL)");
        await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS ap_catalog (Id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
    }

    private static async Task<ProbeObservation> DdlAsync(DbConnection conn, string dbPath)
    {
        await EnsureTablesAsync(conn);
        long tables = await RawChannel.TableCountAsync(dbPath);
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static async Task<ProbeObservation> InsertExplicitPkAsync(DbConnection conn, string dbPath)
    {
        await EnsureTablesAsync(conn);
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        int affected = await conn.ExecuteAsync(
            "INSERT INTO ap_catalog (Id, label) VALUES (@id, @label)", new { id = CatalogIdValue, label = CatalogLabelValue });
        CatalogRow? readBack = await conn.QuerySingleOrDefaultAsync<CatalogRow>(
            $"SELECT Id, label AS Label FROM ap_catalog WHERE Id = {CatalogIdValue}");
        string raw = await RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue));
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", affected == 1 ? CatalogIdValue : 0L),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> InsertAutoPkAsync(DbConnection conn, string dbPath)
    {
        await EnsureTablesAsync(conn);
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            new { n = Name1, a = Amount, c = Cat1 });
        long id = await conn.ExecuteScalarAsync<long>("SELECT last_insert_rowid()");
        OrderRow? readBack = await conn.QuerySingleOrDefaultAsync<OrderRow>(
            $"SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order WHERE Id = {id}");
        string raw = await RawChannel.ScalarStringAsync(dbPath, "SELECT name FROM ap_order WHERE Id = @p0",
            ("@p0", id));
        return Ok(ProbeId.P0InsertAutoPk,
            F("insert.id", id),
            F("orm.readback.name", readBack?.Name ?? ""),
            F("raw.stored.name", raw),
            F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            FT("orm.readback.amount.type", readBack?.Amount.GetType()),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> GetByKeyAsync(DbConnection conn, string dbPath)
    {
#if AOTPROBE_B
        await RawChannel.EnsureOrderTableAsync(dbPath);
        await RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1);
        const long keyId = 1;
#else
        await EnsureTablesAsync(conn);
        await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            new { n = Name1, a = Amount, c = Cat1 });
        long keyId = await conn.ExecuteScalarAsync<long>("SELECT last_insert_rowid()");
#endif
        OrderRow? found = await conn.QuerySingleOrDefaultAsync<OrderRow>(
            $"SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order WHERE Id = {keyId}");
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static async Task<ProbeObservation> UpdateDeleteAsync(DbConnection conn)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        await EnsureTablesAsync(conn);
        await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            new { n = Name1, a = Amount, c = Cat1 });
        long id = await conn.ExecuteScalarAsync<long>("SELECT last_insert_rowid()");
        int updated = await conn.ExecuteAsync("UPDATE ap_order SET amount = @a WHERE Id = @id",
            new { a = AmountUpdated, id });
        OrderRow? afterUpdate = await conn.QuerySingleOrDefaultAsync<OrderRow>(
            $"SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order WHERE Id = {id}");
        int deleted = await conn.ExecuteAsync("DELETE FROM ap_order WHERE Id = @id", new { id });
        OrderRow? afterDelete = await conn.QuerySingleOrDefaultAsync<OrderRow>(
            $"SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order WHERE Id = {id}");
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterUpdate is null
                ? "" : afterUpdate.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDelete is not null));
#endif
    }

    private static async Task<ProbeObservation> QueryAllAsync(DbConnection conn, string dbPath)
    {
#if AOTPROBE_B
        await RawChannel.EnsureOrderTableAsync(dbPath);
        await RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2);
#else
        await EnsureTablesAsync(conn);
        await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            new { n = Name1, a = Amount, c = Cat1 });
        await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            new { n = Name2, a = Amount, c = Cat1 });
        await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            new { n = Name3, a = Amount, c = Cat2 });
#endif
        List<OrderRow> rows = (await conn.QueryAsync<OrderRow>(
            "SELECT Id, name AS Name, amount AS Amount, category AS Category FROM ap_order ORDER BY Id")).ToList();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static async Task<ProbeObservation> AggregateAsync(DbConnection conn, string dbPath)
    {
#if AOTPROBE_B
        await RawChannel.EnsureOrderTableAsync(dbPath);
        await RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2);
#else
        await EnsureTablesAsync(conn);
        await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            new { n = Name1, a = Amount, c = Cat1 });
        await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            new { n = Name2, a = Amount, c = Cat1 });
        await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
            new { n = Name3, a = Amount, c = Cat2 });
#endif
        long total = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ap_order");
        // 已知缺口登记候选：元组形态 Query<(string, long)> 在 Dapper.AOT 1.1.0 失败（DAP012，2026-10-28 实测），
        // 官方可用的名映射 DTO 形态为本探针路径
        List<CategoryCount> grouped = (await conn.QueryAsync<CategoryCount>(
            "SELECT category AS Category, COUNT(*) AS \"Count\" FROM ap_order GROUP BY category")).ToList();
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static async Task<ProbeObservation> TransactionAsync(DbConnection conn, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        await EnsureTablesAsync(conn);
        await using (DbTransaction tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
                new { n = TxKept, a = Amount, c = Cat1 }, tx);
            await tx.CommitAsync();
        }
        long kept = await RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept);

        await using (DbTransaction tx2 = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("INSERT INTO ap_order (name, amount, category) VALUES (@n, @a, @c)",
                new { n = TxDropped, a = Amount, c = Cat2 }, tx2);
            await tx2.RollbackAsync();
        }
        long dropped = await RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped);

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }
}
