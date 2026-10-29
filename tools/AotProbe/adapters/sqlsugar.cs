using AotProbe.Adapters.SqlSugar;
using PalORM.AotProbe;
using SqlSugar;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// SqlSugar 适配器（官方路径 = 默认形态；SqlSugarCore 捆绑全套驱动 → SQLite 应用闭包内含 Npgsql，
// 2026-10-28 手工实测 ILC 编译期即死，EnableAot 开关无效）。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return Run(probe, dbPath);
    }

    private static SqlSugarClient Create(string dbPath) => new(new ConnectionConfig
    {
        ConnectionString = $"DataSource={dbPath}",
        DbType = DbType.Sqlite,
        IsAutoCloseConnection = true,
    });

    private static ProbeObservation Run(ProbeId probe, string dbPath)
    {
        using SqlSugarClient db = Create(dbPath);
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

    private static void EnsureTables(SqlSugarClient db) =>
        db.CodeFirst.InitTables<OrderRow, CatalogRow>();

    private static ProbeObservation Ddl(SqlSugarClient db, string dbPath)
    {
        EnsureTables(db);
        long tables = RawChannel.TableCountAsync(dbPath).GetAwaiter().GetResult();
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static ProbeObservation InsertExplicitPk(SqlSugarClient db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        EnsureTables(db);
        _ = db.Insertable(new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue }).ExecuteCommand();
        CatalogRow? readBack = db.Queryable<CatalogRow>().Single(t => t.Id == CatalogIdValue);
        string raw = RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue)).GetAwaiter().GetResult();
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", CatalogIdValue),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static ProbeObservation InsertAutoPk(SqlSugarClient db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        EnsureTables(db);
        long id = db.Insertable(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteReturnIdentity();
        OrderRow? readBack = db.Queryable<OrderRow>().Single(t => t.Id == id);
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

    private static ProbeObservation GetByKey(SqlSugarClient db, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        const long keyId = 1;
#else
        EnsureTables(db);
        long keyId = db.Insertable(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteReturnIdentity();
#endif
        OrderRow? found = db.Queryable<OrderRow>().Single(t => t.Id == keyId);
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static ProbeObservation UpdateDelete(SqlSugarClient db)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        EnsureTables(db);
        long id = db.Insertable(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteReturnIdentity();
        var row = new OrderRow { Id = id, Name = Name1, Amount = AmountUpdated, Category = Cat1 };
        int updated = db.Updateable(row).ExecuteCommand();
        OrderRow? afterUpdate = db.Queryable<OrderRow>().Single(t => t.Id == id);
        int deleted = db.Deleteable<OrderRow>().Where(t => t.Id == id).ExecuteCommand();
        OrderRow? afterDelete = db.Queryable<OrderRow>().Single(t => t.Id == id);
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterUpdate is null
                ? "" : afterUpdate.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDelete is not null));
#endif
    }

    private static ProbeObservation QueryAll(SqlSugarClient db, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2).GetAwaiter().GetResult();
#else
        EnsureTables(db);
        _ = db.Insertable(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteReturnIdentity();
        _ = db.Insertable(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 }).ExecuteReturnIdentity();
        _ = db.Insertable(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 }).ExecuteReturnIdentity();
#endif
        List<OrderRow> rows = db.Queryable<OrderRow>().OrderBy(t => t.Id).ToList();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static ProbeObservation Aggregate(SqlSugarClient db, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2).GetAwaiter().GetResult();
#else
        EnsureTables(db);
        _ = db.Insertable(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteReturnIdentity();
        _ = db.Insertable(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 }).ExecuteReturnIdentity();
        _ = db.Insertable(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 }).ExecuteReturnIdentity();
#endif
        int total = db.Queryable<OrderRow>().Count();
        List<CategoryCount> grouped = db.Queryable<OrderRow>()
            .GroupBy(t => t.Category)
            .Select(t => new CategoryCount { Category = t.Category, Count = SqlFunc.AggregateCount(t.Id) })
            .ToList();
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static ProbeObservation Transaction(SqlSugarClient db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        EnsureTables(db);
        db.Ado.BeginTran();
        _ = db.Insertable(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 }).ExecuteReturnIdentity();
        db.Ado.CommitTran();
        long kept = RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept).GetAwaiter().GetResult();

        db.Ado.BeginTran();
        _ = db.Insertable(new OrderRow { Name = TxDropped, Amount = Amount, Category = Cat2 }).ExecuteReturnIdentity();
        db.Ado.RollbackTran();
        long dropped = RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped).GetAwaiter().GetResult();

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }
}
