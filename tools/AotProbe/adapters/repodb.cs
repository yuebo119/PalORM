using System.Data.Common;
using AotProbe.Adapters.RepoDb;
using PalORM.AotProbe;
using RepoDb; // 扩展方法（InsertAsync/QueryAsync/UseSqlite/FluentMapper）所在命名空间
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// RepoDb 适配器（官方路径 = 默认形态；源内注解申报，AOT 下 LINQ 表达式解释执行）。
// 档位争议库：AOT 通过是否依赖应用引用保底，B 组归因重点。
// 连接策略：每次写操作独立短命连接——同连接第二次写会触发参数未绑定异常
// （RepoDb 1.17 + Microsoft.Data.Sqlite 命令缓存复用问题，实测 2026-10-28），独立连接为合法用法形态。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return RunAsync(probe, dbPath).GetAwaiter().GetResult();
    }

    private static bool _booted;

    private static void Boot()
    {
        // 进程内只引导一次：重复 FluentMapper.Entity 注册会破坏属性→参数映射缓存，
        // 第二次写入起报 "Must add values for parameters"（RepoDb 1.17 实测 2026-10-28）
        if (_booted)
            return;
        _booted = true;
        global::RepoDb.GlobalConfiguration.Setup().UseSqlite();
        global::RepoDb.FluentMapper.Entity<global::AotProbe.Adapters.RepoDb.OrderRow>().Table("ap_order").Primary(e => e.Id);
        global::RepoDb.FluentMapper.Entity<global::AotProbe.Adapters.RepoDb.CatalogRow>().Table("ap_catalog").Primary(e => e.Id);
    }

    private static async Task<DbConnection> OpenAsync(string dbPath)
    {
        Boot();
        var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<ProbeObservation> RunAsync(ProbeId probe, string dbPath)
    {
        // 属性同形列名（RepoDb 参数绑定要求属性名与列名精确同形，见 RawChannel 注释）
        await RawChannel.EnsureOrderTableCamelAsync(dbPath);
        await RawChannel.EnsureCatalogTableCamelAsync(dbPath);
        return probe switch
        {
            ProbeId.P0Ddl => throw new NotSupportedException(
                "P0Ddl is N/A for repodb（ProbeCatalog.NotApplicable 登记，Runner 不应派发）"),
            ProbeId.P0InsertExplicitPk => await InsertExplicitPkAsync(dbPath),
            ProbeId.P0InsertAutoPk => await InsertAutoPkAsync(dbPath),
            ProbeId.P0GetByKey => await GetByKeyAsync(dbPath),
            ProbeId.P0UpdateDelete => await UpdateDeleteAsync(dbPath),
            ProbeId.P0QueryAll => await QueryAllAsync(dbPath),
            ProbeId.P0Aggregate => await AggregateAsync(dbPath),
            ProbeId.P0Transaction => await TransactionAsync(dbPath),
            _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
        };
    }

    private static async Task<long> InsertOneAsync(string dbPath, string name, string amount, string category)
    {
        await using (DbConnection conn = await OpenAsync(dbPath))
        {
            object id = await conn.InsertAsync(new OrderRow
            {
                Name = name,
                Amount = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
                Category = category,
            });
            return Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static async Task<ProbeObservation> InsertExplicitPkAsync(string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        await using (DbConnection conn = await OpenAsync(dbPath))
        {
            _ = await conn.InsertAsync(new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue });
        }
        await using (DbConnection conn = await OpenAsync(dbPath))
        {
            CatalogRow? readBack = (await conn.QueryAsync<CatalogRow>(CatalogIdValue)).FirstOrDefault();
            string raw = await RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
                ("@p0", CatalogIdValue));
            return Ok(ProbeId.P0InsertExplicitPk,
                F("insert.id", CatalogIdValue),
                F("orm.readback.label", readBack?.Label ?? ""),
                F("raw.stored.label", raw),
                FT("materialized.type", readBack?.GetType()));
        }
#endif
    }

    private static async Task<ProbeObservation> InsertAutoPkAsync(string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        long keyId = await InsertOneAsync(dbPath, Name1, AmountText, Cat1);
        await using (DbConnection conn = await OpenAsync(dbPath))
        {
            OrderRow? readBack = (await conn.QueryAsync<OrderRow>(keyId)).FirstOrDefault();
            string raw = await RawChannel.ScalarStringAsync(dbPath, "SELECT name FROM ap_order WHERE Id = @p0",
                ("@p0", keyId));
            return Ok(ProbeId.P0InsertAutoPk,
                F("insert.id", keyId),
                F("orm.readback.name", readBack?.Name ?? ""),
                F("raw.stored.name", raw),
                F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                FT("orm.readback.amount.type", readBack?.Amount.GetType()),
                FT("materialized.type", readBack?.GetType()));
        }
#endif
    }

    private static async Task<ProbeObservation> GetByKeyAsync(string dbPath)
    {
#if AOTPROBE_B
        await RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1);
        const long keyId = 1;
#else
        long keyId = await InsertOneAsync(dbPath, Name1, AmountText, Cat1);
#endif
        await using DbConnection conn = await OpenAsync(dbPath);
        OrderRow? found = (await conn.QueryAsync<OrderRow>(keyId)).FirstOrDefault();
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static async Task<ProbeObservation> UpdateDeleteAsync(string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        long keyId = await InsertOneAsync(dbPath, Name1, AmountText, Cat1);
        int updated;
        await using (DbConnection conn = await OpenAsync(dbPath))
        {
            updated = await conn.UpdateAsync(new OrderRow { Id = keyId, Name = Name1, Amount = AmountUpdated, Category = Cat1 });
        }
        await using (DbConnection conn = await OpenAsync(dbPath))
        {
            OrderRow? afterUpdate = (await conn.QueryAsync<OrderRow>(keyId)).FirstOrDefault();
            int deleted = await conn.DeleteAsync<OrderRow>(keyId);
            OrderRow? afterDelete = (await conn.QueryAsync<OrderRow>(keyId)).FirstOrDefault();
            return Ok(ProbeId.P0UpdateDelete,
                F("update.affected", updated),
                F("orm.afterupdate.amount", afterUpdate is null
                    ? "" : afterUpdate.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                F("delete.affected", deleted),
                F("orm.afterdelete.found", afterDelete is not null));
        }
#endif
    }

    private static async Task<ProbeObservation> QueryAllAsync(string dbPath)
    {
#if AOTPROBE_B
        await RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2);
#else
        _ = await InsertOneAsync(dbPath, Name1, AmountText, Cat1);
        _ = await InsertOneAsync(dbPath, Name2, AmountText, Cat1);
        _ = await InsertOneAsync(dbPath, Name3, AmountText, Cat2);
#endif
        await using DbConnection conn = await OpenAsync(dbPath);
        List<OrderRow> rows = (await conn.QueryAllAsync<OrderRow>()).OrderBy(r => r.Id).ToList();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static async Task<ProbeObservation> AggregateAsync(string dbPath)
    {
#if AOTPROBE_B
        await RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2);
#else
        _ = await InsertOneAsync(dbPath, Name1, AmountText, Cat1);
        _ = await InsertOneAsync(dbPath, Name2, AmountText, Cat1);
        _ = await InsertOneAsync(dbPath, Name3, AmountText, Cat2);
#endif
        await using DbConnection conn = await OpenAsync(dbPath);
        long total = Convert.ToInt64(await conn.CountAllAsync<OrderRow>(), System.Globalization.CultureInfo.InvariantCulture);
        List<CategoryCount> grouped = (await conn.ExecuteQueryAsync<CategoryCount>(
            "SELECT category AS Category, COUNT(*) AS \"Count\" FROM ap_order GROUP BY category")).ToList();
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static async Task<ProbeObservation> TransactionAsync(string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        await using (DbConnection conn = await OpenAsync(dbPath))
        {
            await using (DbTransaction tx = await conn.BeginTransactionAsync())
            {
                await conn.InsertAsync(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 }, transaction: tx);
                await tx.CommitAsync();
            }
        }
        long kept = await RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept);

        await using (DbConnection conn = await OpenAsync(dbPath))
        {
            await using (DbTransaction tx = await conn.BeginTransactionAsync())
            {
                await conn.InsertAsync(new OrderRow { Name = TxDropped, Amount = Amount, Category = Cat2 }, transaction: tx);
                await tx.RollbackAsync();
            }
        }
        long dropped = await RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped);

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }
}
