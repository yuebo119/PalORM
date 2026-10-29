using AotProbe.Adapters.Linq2Db;
using LinqToDB;
using LinqToDB.Data;
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// linq2db 适配器（官方路径 = 默认形态；provider 程序集经 Assembly.Load 动态解析，
// 2026-10-28 手工实测 AOT 下启动即崩 "Cannot load assembly Microsoft.Data.Sqlite"，Root 无效）。
// SQLite 驱动形态：ProviderName.SQLiteMS（Microsoft.Data.Sqlite 载体）。
internal static class LibAdapter
{
    private static DataConnection Create(string dbPath) =>
        new(ProviderName.SQLiteMS, $"Data Source={dbPath}");

    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return Run(probe, dbPath);
    }

    private static ProbeObservation Run(ProbeId probe, string dbPath)
    {
        using DataConnection db = Create(dbPath);
        return probe switch
        {
            ProbeId.P0Ddl => Ddl(db, dbPath),
            ProbeId.P0InsertExplicitPk => InsertExplicitPk(db, dbPath),
            ProbeId.P0InsertAutoPk => InsertAutoPk(db, dbPath),
            ProbeId.P0GetByKey => GetByKey(db, dbPath),
            ProbeId.P0UpdateDelete => UpdateDelete(db),
            ProbeId.P0QueryAll => QueryAll(db, dbPath),
            ProbeId.P0Aggregate => Aggregate(db, dbPath),
            ProbeId.P0Transaction => Transaction(db, dbPath),
            _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
        };
    }

    private static void EnsureTables(DataConnection db)
    {
        _ = db.CreateTable<OrderRow>(tableOptions: TableOptions.CheckExistence);
        _ = db.CreateTable<CatalogRow>(tableOptions: TableOptions.CheckExistence);
    }

    private static ProbeObservation Ddl(DataConnection db, string dbPath)
    {
        EnsureTables(db);
        long tables = RawChannel.TableCountAsync(dbPath).GetAwaiter().GetResult();
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static ProbeObservation InsertExplicitPk(DataConnection db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        EnsureTables(db);
        _ = db.Insert(new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue });
        CatalogRow? readBack = db.GetTable<CatalogRow>().FirstOrDefault(t => t.Id == CatalogIdValue);
        string raw = RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue)).GetAwaiter().GetResult();
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", CatalogIdValue),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static ProbeObservation InsertAutoPk(DataConnection db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        EnsureTables(db);
        long id = db.InsertWithInt64Identity(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
        OrderRow? readBack = db.GetTable<OrderRow>().FirstOrDefault(t => t.Id == id);
        string raw = RawChannel.ScalarStringAsync(dbPath, "SELECT name FROM ap_order WHERE Id = @p0",
            ("@p0", (object)id)).GetAwaiter().GetResult();
        return Ok(ProbeId.P0InsertAutoPk,
            F("insert.id", id),
            F("orm.readback.name", readBack?.Name ?? ""),
            F("raw.stored.name", raw),
            F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            FT("orm.readback.amount.type", readBack?.Amount.GetType()),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static ProbeObservation GetByKey(DataConnection db, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        const long keyId = 1;
#else
        EnsureTables(db);
        long keyId = db.InsertWithInt64Identity(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
#endif
        OrderRow? found = db.GetTable<OrderRow>().FirstOrDefault(t => t.Id == keyId);
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static ProbeObservation UpdateDelete(DataConnection db)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        EnsureTables(db);
        long id = db.InsertWithInt64Identity(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
        int updated = db.Update(new OrderRow { Id = id, Name = Name1, Amount = AmountUpdated, Category = Cat1 });
        OrderRow? afterUpdate = db.GetTable<OrderRow>().FirstOrDefault(t => t.Id == id);
        int deleted = db.GetTable<OrderRow>().Where(t => t.Id == id).Delete();
        OrderRow? afterDelete = db.GetTable<OrderRow>().FirstOrDefault(t => t.Id == id);
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterUpdate is null
                ? "" : afterUpdate.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDelete is not null));
#endif
    }

    private static ProbeObservation QueryAll(DataConnection db, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2).GetAwaiter().GetResult();
#else
        EnsureTables(db);
        _ = db.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
        _ = db.Insert(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 });
        _ = db.Insert(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 });
#endif
        List<OrderRow> rows = db.GetTable<OrderRow>().OrderBy(t => t.Id).ToList();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static ProbeObservation Aggregate(DataConnection db, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2).GetAwaiter().GetResult();
#else
        EnsureTables(db);
        _ = db.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
        _ = db.Insert(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 });
        _ = db.Insert(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 });
#endif
        long total = db.GetTable<OrderRow>().LongCount();
        List<CategoryCount> grouped = db.GetTable<OrderRow>()
            .GroupBy(t => t.Category)
            .Select(g => new CategoryCount { Category = g.Key, Count = g.LongCount() })
            .ToList();
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static ProbeObservation Transaction(DataConnection db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        EnsureTables(db);
        using (var tx = db.BeginTransaction())
        {
            _ = db.Insert(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 });
            tx.Commit();
        }
        long kept = RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept).GetAwaiter().GetResult();

        using (var tx2 = db.BeginTransaction())
        {
            _ = db.Insert(new OrderRow { Name = TxDropped, Amount = Amount, Category = Cat2 });
            tx2.Rollback();
        }
        long dropped = RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped).GetAwaiter().GetResult();

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }
}
