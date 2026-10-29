using System.Data;
using AotProbe.Adapters.ServiceStackOrmLite;
using PalORM.AotProbe;
using ServiceStack.OrmLite;
using ServiceStack.OrmLite.Sqlite; // 包 id 是 .Sqlite.Data，命名空间/程序集实名是 .Sqlite（10.1.4 程序集扫描）
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// ServiceStack.OrmLite 适配器（官方路径 = 默认形态；v10 无官方 OrmLite AOT 声明——本矩阵的验证价值点）。
// 载体：ServiceStack.OrmLite.Sqlite.Data（Microsoft.Data.Sqlite 载体形态）。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return Run(probe, dbPath);
    }

    private static ProbeObservation Run(ProbeId probe, string dbPath)
    {
        // OrmLite Sqlite 方言的"连接串"位实参是裸文件路径（其内部自行拼 Data Source；实测整串传入会被当相对路径）
        var factory = new OrmLiteConnectionFactory(dbPath, SqliteDialect.Provider);
        using IDbConnection db = factory.Open();
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

    private static void EnsureTables(IDbConnection db)
    {
        db.CreateTable<OrderRow>(false);
        db.CreateTable<CatalogRow>(false);
    }

    private static ProbeObservation Ddl(IDbConnection db, string dbPath)
    {
        EnsureTables(db);
        long tables = RawChannel.TableCountAsync(dbPath).GetAwaiter().GetResult();
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static ProbeObservation InsertExplicitPk(IDbConnection db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        EnsureTables(db);
        db.Insert(new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue });
        CatalogRow? readBack = db.SingleById<CatalogRow>(CatalogIdValue);
        string raw = RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue)).GetAwaiter().GetResult();
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", CatalogIdValue),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static ProbeObservation InsertAutoPk(IDbConnection db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        EnsureTables(db);
        long id = db.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }, selectIdentity: true);
        OrderRow? readBack = db.SingleById<OrderRow>(id);
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

    private static ProbeObservation GetByKey(IDbConnection db, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        const long keyId = 1;
#else
        EnsureTables(db);
        long keyId = db.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }, selectIdentity: true);
#endif
        OrderRow? found = db.SingleById<OrderRow>(keyId);
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static ProbeObservation UpdateDelete(IDbConnection db)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        EnsureTables(db);
        long id = db.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }, selectIdentity: true);
        int updated = db.Update(new OrderRow { Id = id, Name = Name1, Amount = AmountUpdated, Category = Cat1 });
        OrderRow? afterUpdate = db.SingleById<OrderRow>(id);
        int deleted = db.DeleteById<OrderRow>(id);
        OrderRow? afterDelete = db.SingleById<OrderRow>(id);
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterUpdate is null
                ? "" : afterUpdate.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDelete is not null));
#endif
    }

    private static ProbeObservation QueryAll(IDbConnection db, string dbPath)
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
        List<OrderRow> rows = db.Select(db.From<OrderRow>().OrderBy(x => x.Id));
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static ProbeObservation Aggregate(IDbConnection db, string dbPath)
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
        long total = db.Count<OrderRow>();
        // 分组聚合：SqlList 裸 SQL（结果探针口径，路径自由）
        List<CategoryCount> grouped = db.SqlList<CategoryCount>(
            "SELECT Category AS Category, COUNT(*) AS \"Count\" FROM ap_order GROUP BY Category");
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static ProbeObservation Transaction(IDbConnection db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        EnsureTables(db);
        using (IDbTransaction tx = db.OpenTransaction())
        {
            db.Insert(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 });
            tx.Commit();
        }
        long kept = RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept).GetAwaiter().GetResult();

        using (IDbTransaction tx2 = db.OpenTransaction())
        {
            db.Insert(new OrderRow { Name = TxDropped, Amount = Amount, Category = Cat2 });
            tx2.Rollback();
        }
        long dropped = RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped).GetAwaiter().GetResult();

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }
}
