using System.Globalization;
using AotProbe.Adapters.PalORM;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.AotProbe;
using PalORM.Sqlite;

namespace AotProbe.Adapters;

// [Projection] DTO：顶层声明（PalORM 拒绝嵌套类型，PALORM045；AotTest 同例），
// 随适配器编译进宿主（A/B 两侧一致），物化类型断言的载体
[Projection]
internal sealed class CategoryCount
{
    public string Category { get; set; } = "";
    public long Count { get; set; }
}

// PalORM 适配器（S0 试点，官方 AOT 路径 = 默认形态，AOT-first 设计）。
// 纪律一：只交观测值，绝不判定成败（断言反转；期望值在 Runner 侧 Fixtures）。
// 纪律二：下列值常量镜像 Fixtures——漂移只会假失败（ loudly fail），永远不会假通过。
// 纪律三：B 组（AOTPROBE_B）下禁止出现实体的 new 调用点，数据引导走原生 ADO——
//         这是"库能否在无静态构造调用点下物化"实验的前提，#if 块必须保持干净。
internal static class LibAdapter
{
    private const string Name1 = "订单\"A\"-🚀-日本語";
    private const string Name2 = "row-two-平安京";
    private const string Name3 = "row'three \"quoted\"";
    private const string AmountText = "12345.67";
    private const string AmountUpdatedText = "999.99";
    private const string Cat1 = "cat-1";
    private const string Cat2 = "cat-2";
    private const string CatalogLabelValue = "明示主键-42";
    private const long CatalogIdValue = 42;

    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return RunAsync(probe, dbPath).GetAwaiter().GetResult();
    }

    private static async Task<ProbeObservation> RunAsync(ProbeId probe, string dbPath)
    {
        var options = new DbOptions { ConnectionString = $"Data Source={dbPath}" };
        DataSession<SqliteProvider> db = await DataSession<SqliteProvider>.CreateAsync(options);
        try
        {
            return probe switch
            {
                ProbeId.P0Ddl => await DdlAsync(db, dbPath),
                ProbeId.P0InsertExplicitPk => await InsertExplicitPkAsync(db, dbPath),
                ProbeId.P0InsertAutoPk => await InsertAutoPkAsync(db, dbPath),
                ProbeId.P0GetByKey => await GetByKeyAsync(db, dbPath),
                ProbeId.P0UpdateDelete => await UpdateDeleteAsync(db),
                ProbeId.P0QueryAll => await QueryAllAsync(db, dbPath),
                ProbeId.P0Aggregate => await AggregateAsync(db, dbPath),
                ProbeId.P0Transaction => await TransactionAsync(db),
                _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
            };
        }
        finally
        {
            await db.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<ProbeObservation> DdlAsync(DataSession<SqliteProvider> db, string dbPath)
    {
        await db.MigrateAsync();
        long tables = await RawScalarLongAsync(dbPath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('ap_order', 'ap_catalog')");
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static async Task<ProbeObservation> InsertExplicitPkAsync(DataSession<SqliteProvider> db, string dbPath)
    {
        await db.MigrateAsync();
#if AOTPROBE_B
        // B 组不适用（显式主键插入需要构造实体）——Runner 不会派发到本分支
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        CatalogRow inserted = await db.InsertAsync(new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue });
        CatalogRow? readBack = await db.GetAsync<CatalogRow>(CatalogIdValue);
        string raw = await RawScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue));
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", inserted.Id),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> InsertAutoPkAsync(DataSession<SqliteProvider> db, string dbPath)
    {
        await db.MigrateAsync();
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        OrderRow inserted = await db.InsertAsync(new OrderRow
        {
            Name = Name1,
            Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture),
            Category = Cat1,
        });
        OrderRow? readBack = await db.GetAsync<OrderRow>(inserted.Id);
        string raw = await RawScalarStringAsync(dbPath, "SELECT name FROM ap_order WHERE Id = @p0",
            ("@p0", inserted.Id));
        return Ok(ProbeId.P0InsertAutoPk,
            F("insert.id", inserted.Id),
            F("orm.readback.name", readBack?.Name ?? ""),
            F("raw.stored.name", raw),
            F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(CultureInfo.InvariantCulture)),
            FT("orm.readback.amount.type", readBack?.Amount.GetType()),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> GetByKeyAsync(DataSession<SqliteProvider> db, string dbPath)
    {
        await db.MigrateAsync();
#if AOTPROBE_B
        await RawExecAsync(dbPath,
            "INSERT INTO ap_order (name, amount, category) VALUES (@p0, @p1, @p2)",
            ("@p0", Name1), ("@p1", AmountText), ("@p2", Cat1));
        OrderRow? found = await db.GetAsync<OrderRow>(1L);
#else
        OrderRow inserted = await db.InsertAsync(new OrderRow
        {
            Name = Name1,
            Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture),
            Category = Cat1,
        });
        OrderRow? found = await db.GetAsync<OrderRow>(inserted.Id);
#endif
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static async Task<ProbeObservation> UpdateDeleteAsync(DataSession<SqliteProvider> db)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        await db.MigrateAsync();
        OrderRow inserted = await db.InsertAsync(new OrderRow
        {
            Name = Name1,
            Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture),
            Category = Cat1,
        });
        inserted.Amount = decimal.Parse(AmountUpdatedText, CultureInfo.InvariantCulture);
        int updated = await db.UpdateAsync(inserted);
        OrderRow? afterUpdate = await db.GetAsync<OrderRow>(inserted.Id);
        int deleted = await db.DeleteAsync<OrderRow>(inserted.Id);
        OrderRow? afterDelete = await db.GetAsync<OrderRow>(inserted.Id);
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterUpdate is null
                ? "" : afterUpdate.Amount.ToString(CultureInfo.InvariantCulture)),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDelete is not null));
#endif
    }

    private static async Task<ProbeObservation> QueryAllAsync(DataSession<SqliteProvider> db, string dbPath)
    {
        await db.MigrateAsync();
#if AOTPROBE_B
        await RawExecAsync(dbPath, "INSERT INTO ap_order (name, amount, category) VALUES (@p0, @p1, @p2)",
            ("@p0", Name1), ("@p1", AmountText), ("@p2", Cat1));
        await RawExecAsync(dbPath, "INSERT INTO ap_order (name, amount, category) VALUES (@p0, @p1, @p2)",
            ("@p0", Name2), ("@p1", AmountText), ("@p2", Cat1));
        await RawExecAsync(dbPath, "INSERT INTO ap_order (name, amount, category) VALUES (@p0, @p1, @p2)",
            ("@p0", Name3), ("@p1", AmountText), ("@p2", Cat2));
#else
        // 展开插入而非循环：PALORM005（N+1 检测）对循环内 DB 调用报警，AotTest 同例
        await db.InsertAsync(new OrderRow
        {
            Name = Name1, Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture), Category = Cat1,
        });
        await db.InsertAsync(new OrderRow
        {
            Name = Name2, Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture), Category = Cat1,
        });
        await db.InsertAsync(new OrderRow
        {
            Name = Name3, Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture), Category = Cat2,
        });
#endif
        List<OrderRow> rows = await db.From<OrderRow>().OrderBy(x => x.Id).ToListAsync();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static async Task<ProbeObservation> AggregateAsync(DataSession<SqliteProvider> db, string dbPath)
    {
        await db.MigrateAsync();
#if AOTPROBE_B
        await RawExecAsync(dbPath, "INSERT INTO ap_order (name, amount, category) VALUES (@p0, @p1, @p2)",
            ("@p0", Name1), ("@p1", AmountText), ("@p2", Cat1));
        await RawExecAsync(dbPath, "INSERT INTO ap_order (name, amount, category) VALUES (@p0, @p1, @p2)",
            ("@p0", Name2), ("@p1", AmountText), ("@p2", Cat1));
        await RawExecAsync(dbPath, "INSERT INTO ap_order (name, amount, category) VALUES (@p0, @p1, @p2)",
            ("@p0", Name3), ("@p1", AmountText), ("@p2", Cat2));
#else
        await db.InsertAsync(new OrderRow
        {
            Name = Name1, Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture), Category = Cat1,
        });
        await db.InsertAsync(new OrderRow
        {
            Name = Name2, Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture), Category = Cat1,
        });
        await db.InsertAsync(new OrderRow
        {
            Name = Name3, Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture), Category = Cat2,
        });
#endif
        long total = await db.CountAsync<OrderRow>();
        List<CategoryCount> grouped = await db.QueryAsync<CategoryCount>(
            $"SELECT category AS \"Category\", COUNT(*) AS \"Count\" FROM ap_order GROUP BY category");
        // 观测的是各分组的 COUNT 聚合值（组行本身每组恰一行，Count(g=>...) 数的是组数）
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static async Task<ProbeObservation> TransactionAsync(DataSession<SqliteProvider> db)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        await db.MigrateAsync();

        System.Data.Common.DbTransaction commitTx = await db.BeginTransactionAsync();
        try
        {
            await db.InsertAsync(new OrderRow
            {
                Name = "tx-kept", Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture), Category = Cat1,
            });
            await commitTx.CommitAsync();
        }
        finally
        {
            await commitTx.DisposeAsync();
        }
        long kept = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM ap_order WHERE name = {"tx-kept"}");

        System.Data.Common.DbTransaction rollbackTx = await db.BeginTransactionAsync();
        try
        {
            await db.InsertAsync(new OrderRow
            {
                Name = "tx-dropped", Amount = decimal.Parse(AmountText, CultureInfo.InvariantCulture), Category = Cat2,
            });
            await rollbackTx.RollbackAsync();
        }
        finally
        {
            await rollbackTx.DisposeAsync();
        }
        long dropped = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM ap_order WHERE name = {"tx-dropped"}");

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }

    private static ProbeObservation Ok(ProbeId probe, params ObsField[] fields)
        => new(probe.ToString(), ProbeStatus.Ok, null, null, fields);

    private static ObsField F(string key, object value) =>
        new(key, value.GetType().FullName ?? "", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

    /// <summary>类型观测字段的构造约定：TypeName 与 Value 都承载类型全名
    /// （Runner 的 TypeSuffix 断言查 TypeName；F() 会把类型名退化成 System.String）。</summary>
    private static ObsField FT(string key, Type? type) =>
        new(key, type?.FullName ?? "<null>", type?.FullName ?? "<null>");

    // ===== 原生 ADO 旁路（B 组数据引导 + 存储形态观测通道；值一律参数化）=====

    private static async Task RawExecAsync(string dbPath, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> RawScalarStringAsync(string dbPath, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
    }

    private static async Task<long> RawScalarLongAsync(string dbPath, string sql, params (string Name, object Value)[] args)
    {
        string s = await RawScalarStringAsync(dbPath, sql, args);
        return long.Parse(s, CultureInfo.InvariantCulture);
    }
}
