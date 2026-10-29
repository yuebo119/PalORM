using AotProbe.Adapters.LiteDb;
using LiteDB;
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// LiteDB 适配器（官方路径 = 默认形态；2026-10-28 手工实测 3/7：读取路径 Activator 构造被裁）。
// raw 旁路语义调整（第二次）：LiteDB v5 对数据文件持进程内独占锁，同文件第二连接不可开——
// 改为"ORM 相位 → 释放 → raw 相位"两段生命周期，存储校验仍不经被测物化路径。
// B 组数据引导走 BsonDocument（不经实体构造函数——B 组前提保持干净）。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return probe switch
        {
            ProbeId.P0InsertExplicitPk => InsertExplicitPk(dbPath),
            ProbeId.P0InsertAutoPk => InsertAutoPk(dbPath),
            ProbeId.P0GetByKey => GetByKey(dbPath),
            ProbeId.P0UpdateDelete => UpdateDelete(dbPath),
            ProbeId.P0QueryAll => QueryAll(dbPath),
            ProbeId.P0Transaction => Transaction(dbPath),
            ProbeId.P0Ddl or ProbeId.P0Aggregate => throw new NotSupportedException(
                $"{probe} is N/A for litedb（ProbeCatalog.NotApplicable 登记，Runner 不应派发）"),
            _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
        };
    }

    private static ProbeObservation InsertExplicitPk(string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        CatalogRow? readBack;
        using (var db = new LiteDatabase(dbPath))
        {
            ILiteCollection<CatalogRow> col = db.GetCollection<CatalogRow>("ap_catalog");
            col.Insert(new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue });
            readBack = col.FindById(CatalogIdValue);
        }
        using (var raw = new LiteDatabase(dbPath))
        {
            string rawLabel = raw.GetCollection<CatalogRow>("ap_catalog").FindById(CatalogIdValue)?.Label ?? "";
            return Ok(ProbeId.P0InsertExplicitPk,
                F("insert.id", CatalogIdValue),
                F("orm.readback.label", readBack?.Label ?? ""),
                F("raw.stored.label", rawLabel),
                FT("materialized.type", readBack?.GetType()));
        }
#endif
    }

    private static ProbeObservation InsertAutoPk(string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        long id;
        OrderRow? readBack;
        using (var db = new LiteDatabase(dbPath))
        {
            ILiteCollection<OrderRow> col = db.GetCollection<OrderRow>("ap_order");
            id = col.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).AsInt64;
            readBack = col.FindById(id);
        }
        using (var raw = new LiteDatabase(dbPath))
        {
            string rawName = raw.GetCollection<OrderRow>("ap_order").FindById(id)?.Name ?? "";
            return Ok(ProbeId.P0InsertAutoPk,
                F("insert.id", id),
                F("orm.readback.name", readBack?.Name ?? ""),
                F("raw.stored.name", rawName),
                F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                FT("orm.readback.amount.type", readBack?.Amount.GetType()),
                FT("materialized.type", readBack?.GetType()));
        }
#endif
    }

    private static ProbeObservation GetByKey(string dbPath)
    {
#if AOTPROBE_B
        SeedBson(dbPath, 1, Name1, Cat1);
        const long keyId = 1;
        OrderRow? found;
        using (var db = new LiteDatabase(dbPath))
        {
            found = db.GetCollection<OrderRow>("ap_order").FindById(keyId);
        }
#else
        long keyId;
        OrderRow? found;
        using (var db = new LiteDatabase(dbPath))
        {
            ILiteCollection<OrderRow> col = db.GetCollection<OrderRow>("ap_order");
            keyId = col.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).AsInt64;
            found = col.FindById(keyId);
        }
#endif
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static ProbeObservation UpdateDelete(string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        using var db = new LiteDatabase(dbPath);
        ILiteCollection<OrderRow> col = db.GetCollection<OrderRow>("ap_order");
        long id = col.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).AsInt64;
        OrderRow? row = col.FindById(id);
        if (row is null)
            return Ok(ProbeId.P0UpdateDelete, F("update.affected", 0));
        row.Amount = AmountUpdated;
        bool updated = col.Update(row);
        OrderRow? afterUpdate = col.FindById(id);
        bool deleted = col.Delete(id);
        OrderRow? afterDelete = col.FindById(id);
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated ? 1 : 0),
            F("orm.afterupdate.amount", afterUpdate is null
                ? "" : afterUpdate.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            F("delete.affected", deleted ? 1 : 0),
            F("orm.afterdelete.found", afterDelete is not null));
#endif
    }

    private static ProbeObservation QueryAll(string dbPath)
    {
#if AOTPROBE_B
        SeedBson(dbPath, 1, Name1, Cat1);
        SeedBson(dbPath, 2, Name2, Cat1);
        SeedBson(dbPath, 3, Name3, Cat2);
#else
        using (var db = new LiteDatabase(dbPath))
        {
            ILiteCollection<OrderRow> col = db.GetCollection<OrderRow>("ap_order");
            _ = col.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
            _ = col.Insert(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 });
            _ = col.Insert(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 });
        }
#endif
        using var db2 = new LiteDatabase(dbPath);
        List<OrderRow> rows = db2.GetCollection<OrderRow>("ap_order").FindAll().OrderBy(x => x.Id).ToList();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static ProbeObservation Transaction(string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        using var db = new LiteDatabase(dbPath);
        ILiteCollection<OrderRow> col = db.GetCollection<OrderRow>("ap_order");
        // LiteDB 5 事务 API：BeginTrans() 返回 bool（非事务对象），Commit/Rollback 在 Database 上
        _ = db.BeginTrans();
        _ = col.Insert(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 });
        _ = db.Commit();
        int kept = col.Count(x => x.Name == TxKept);

        _ = db.BeginTrans();
        _ = col.Insert(new OrderRow { Name = TxDropped, Amount = Amount, Category = Cat2 });
        _ = db.Rollback();
        int dropped = col.Count(x => x.Name == TxDropped);

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }

    /// <summary>B 组引导：BsonDocument 直插（无实体构造调用点）。只写断言涉及的字段。</summary>
    private static void SeedBson(string dbPath, long id, string name, string category)
    {
        using var raw = new LiteDatabase(dbPath);
        var doc = new BsonDocument
        {
            ["_id"] = id,
            ["Name"] = name,
            ["Category"] = category,
        };
        raw.GetCollection("ap_order").Insert(doc);
    }
}
