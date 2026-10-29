using AotProbe.Adapters.SqliteNet;
using PalORM.AotProbe;
using SQLite;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// sqlite-net 适配器（官方路径 = 默认形态，无任何 AOT 工作——"碰巧保留"机制活样本）。
// sqlite-net 为同步 API 库；事务用 RunInTransaction（提交）与显式 BeginTransaction（回滚臂）。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        using var db = new SQLiteConnection(dbPath);
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

    private static void EnsureTables(SQLiteConnection db)
    {
        _ = db.CreateTable<OrderRow>();
        _ = db.CreateTable<CatalogRow>();
    }

    private static ProbeObservation Ddl(SQLiteConnection db, string dbPath)
    {
        EnsureTables(db);
        long tables = RawChannel.TableCountAsync(dbPath).GetAwaiter().GetResult();
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static ProbeObservation InsertExplicitPk(SQLiteConnection db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        EnsureTables(db);
        _ = db.Insert(new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue });
        CatalogRow? readBack = db.Find<CatalogRow>(CatalogIdValue);
        string raw = RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue)).GetAwaiter().GetResult();
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", CatalogIdValue),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static ProbeObservation InsertAutoPk(SQLiteConnection db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        EnsureTables(db);
        var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
        _ = db.Insert(row);
        OrderRow? readBack = db.Find<OrderRow>(row.Id);
        string raw = RawChannel.ScalarStringAsync(dbPath, "SELECT name FROM ap_order WHERE Id = @p0",
            ("@p0", (object)row.Id)).GetAwaiter().GetResult();
        return Ok(ProbeId.P0InsertAutoPk,
            F("insert.id", row.Id),
            F("orm.readback.name", readBack?.Name ?? ""),
            F("raw.stored.name", raw),
            F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            FT("orm.readback.amount.type", readBack?.Amount.GetType()),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static ProbeObservation GetByKey(SQLiteConnection db, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        const long keyId = 1;
#else
        EnsureTables(db);
        var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
        _ = db.Insert(row);
        long keyId = row.Id;
#endif
        OrderRow? found = db.Find<OrderRow>(keyId);
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static ProbeObservation UpdateDelete(SQLiteConnection db)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        EnsureTables(db);
        var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
        _ = db.Insert(row);
        row.Amount = AmountUpdated;
        int updated = db.Update(row);
        OrderRow? afterUpdate = db.Find<OrderRow>(row.Id);
        int deleted = db.Delete<OrderRow>(row.Id);
        OrderRow? afterDelete = db.Find<OrderRow>(row.Id);
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterUpdate is null
                ? "" : afterUpdate.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDelete is not null));
#endif
    }

    private static ProbeObservation QueryAll(SQLiteConnection db, string dbPath)
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
        List<OrderRow> rows = db.Table<OrderRow>().OrderBy(x => x.Id).ToList();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static ProbeObservation Aggregate(SQLiteConnection db, string dbPath)
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
        long total = db.ExecuteScalar<long>("SELECT COUNT(*) FROM ap_order");
        List<CategoryCount> grouped = db.Query<CategoryCount>(
            "SELECT category AS Category, COUNT(*) AS \"Count\" FROM ap_order GROUP BY category");
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static ProbeObservation Transaction(SQLiteConnection db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        EnsureTables(db);
        db.RunInTransaction(() => _ = db.Insert(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 }));
        long kept = RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept).GetAwaiter().GetResult();

        // sqlite-net 事务是连接级模型：BeginTransaction() 返回 void，Commit/Rollback 都在连接上
        db.BeginTransaction();
        _ = db.Execute("INSERT INTO ap_order (name, amount, category) VALUES (?, ?, ?)", TxDropped, "0", Cat1);
        db.Rollback();
        long dropped = RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped).GetAwaiter().GetResult();

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }
}
