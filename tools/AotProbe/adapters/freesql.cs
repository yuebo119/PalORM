using AotProbe.Adapters.FreeSql;
using FreeSql;
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// FreeSql 适配器（官方路径 = 默认形态；2026-10-28 手工实测 2/9、Root 后 9/9——运行时反射路线）。
internal static class LibAdapter
{
    private static IFreeSql Create(string dbPath) => new FreeSqlBuilder()
        .UseConnectionString(DataType.Sqlite, $"Data Source={dbPath}")
        .Build();

    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return RunAsync(probe, dbPath).GetAwaiter().GetResult();
    }

    private static async Task<ProbeObservation> RunAsync(ProbeId probe, string dbPath)
    {
        using IFreeSql fsql = Create(dbPath);
        return probe switch
        {
            ProbeId.P0Ddl => Ddl(fsql, dbPath),
            ProbeId.P0InsertExplicitPk => InsertExplicitPk(fsql, dbPath),
            ProbeId.P0InsertAutoPk => InsertAutoPk(fsql, dbPath),
            ProbeId.P0GetByKey => GetByKey(fsql, dbPath),
            ProbeId.P0UpdateDelete => UpdateDelete(fsql),
            ProbeId.P0QueryAll => QueryAll(fsql, dbPath),
            ProbeId.P0Aggregate => Aggregate(fsql, dbPath),
            ProbeId.P0Transaction => Transaction(fsql, dbPath),
            _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
        };
    }

    private static void EnsureTables(IFreeSql fsql) =>
        fsql.CodeFirst.SyncStructure(typeof(OrderRow), typeof(CatalogRow));

    private static ProbeObservation Ddl(IFreeSql fsql, string dbPath)
    {
        EnsureTables(fsql);
        long tables = RawChannel.TableCountAsync(dbPath).GetAwaiter().GetResult();
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static ProbeObservation InsertExplicitPk(IFreeSql fsql, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        EnsureTables(fsql);
        _ = fsql.Insert<CatalogRow>()
            .AppendData(new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue })
            .ExecuteAffrows();
        CatalogRow? readBack = fsql.Select<CatalogRow>().Where(a => a.Id == CatalogIdValue).First();
        string raw = RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue)).GetAwaiter().GetResult();
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", CatalogIdValue),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static ProbeObservation InsertAutoPk(IFreeSql fsql, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        EnsureTables(fsql);
        _ = fsql.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteAffrows();
        long id = fsql.Select<OrderRow>().Max(a => a.Id);
        OrderRow? readBack = fsql.Select<OrderRow>().Where(a => a.Id == id).First();
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

    private static ProbeObservation GetByKey(IFreeSql fsql, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        const long keyId = 1;
#else
        EnsureTables(fsql);
        _ = fsql.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteAffrows();
        long keyId = fsql.Select<OrderRow>().Max(a => a.Id);
#endif
        OrderRow? found = fsql.Select<OrderRow>().Where(a => a.Id == keyId).First();
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static ProbeObservation UpdateDelete(IFreeSql fsql)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        EnsureTables(fsql);
        _ = fsql.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteAffrows();
        long id = fsql.Select<OrderRow>().Max(a => a.Id);
        var row = new OrderRow { Id = id, Name = Name1, Amount = AmountUpdated, Category = Cat1 };
        int updated = fsql.Update<OrderRow>().SetSource(row).ExecuteAffrows();
        OrderRow? afterUpdate = fsql.Select<OrderRow>().Where(a => a.Id == id).First();
        int deleted = fsql.Delete<OrderRow>().Where(a => a.Id == id).ExecuteAffrows();
        OrderRow? afterDelete = fsql.Select<OrderRow>().Where(a => a.Id == id).First();
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterUpdate is null
                ? "" : afterUpdate.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDelete is not null));
#endif
    }

    private static ProbeObservation QueryAll(IFreeSql fsql, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2).GetAwaiter().GetResult();
#else
        EnsureTables(fsql);
        _ = fsql.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteAffrows();
        _ = fsql.Insert(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 }).ExecuteAffrows();
        _ = fsql.Insert(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 }).ExecuteAffrows();
#endif
        List<OrderRow> rows = fsql.Select<OrderRow>().OrderBy(a => a.Id).ToList();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static ProbeObservation Aggregate(IFreeSql fsql, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2).GetAwaiter().GetResult();
#else
        EnsureTables(fsql);
        _ = fsql.Insert(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 }).ExecuteAffrows();
        _ = fsql.Insert(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 }).ExecuteAffrows();
        _ = fsql.Insert(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 }).ExecuteAffrows();
#endif
        long total = fsql.Select<OrderRow>().Count();
        List<CategoryCount> grouped = fsql.Select<OrderRow>()
            .GroupBy(a => a.Category)
            .ToList(a => new CategoryCount { Category = a.Key, Count = a.Count() });
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static ProbeObservation Transaction(IFreeSql fsql, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        EnsureTables(fsql);
        // FreeSql 事务文档化形态：fsql.Transaction(委托)，委托抛异常即自动回滚
        fsql.Transaction(() =>
        {
            _ = fsql.Insert(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 }).ExecuteAffrows();
        });
        long kept = RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept).GetAwaiter().GetResult();

        try
        {
            fsql.Transaction(() =>
            {
                _ = fsql.Insert(new OrderRow { Name = TxDropped, Amount = Amount, Category = Cat2 }).ExecuteAffrows();
                throw new InvalidOperationException("probe-intended rollback");
            });
        }
        catch (InvalidOperationException ex) when (ex.Message == "probe-intended rollback")
        {
            // 预期信号：FreeSql 捕获后已回滚
        }
        long dropped = RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped).GetAwaiter().GetResult();

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }
}
