using AotProbe.Adapters.EfCoreLib;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// EF Core 适配器。口径 = 官方 AOT 路径（Compiled Model）：
// AOT/Root 形态在 EF_COMPILED 下 UseModel(编译模型)——模型代码由 Runner 编排
// `dotnet ef dbcontext optimize` 两阶段生成（先常规构建生成，再注入编译模型重建）；
// JIT 对照臂保持常规形态（每探针构建模型）。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return RunAsync(probe, dbPath).GetAwaiter().GetResult();
    }

    private static DbContextOptions<ApDbContext> BuildOptions(string dbPath)
    {
        var builder = new DbContextOptionsBuilder<ApDbContext>();
        _ = builder.UseSqlite($"Data Source={dbPath}");
#if EF_COMPILED
        builder.UseModel(AotProbe.Variant.CompiledModels.ApDbContextModel.Instance);
#endif
        return builder.Options;
    }

    internal static async Task<ProbeObservation> RunAsync(ProbeId probe, string dbPath)
    {
        using var db = new ApDbContext(BuildOptions(dbPath));
        return probe switch
        {
            ProbeId.P0Ddl => await DdlAsync(db, dbPath),
            ProbeId.P0InsertExplicitPk => await InsertExplicitPkAsync(db, dbPath),
            ProbeId.P0InsertAutoPk => await InsertAutoPkAsync(db, dbPath),
            ProbeId.P0GetByKey => await GetByKeyAsync(db, dbPath),
            ProbeId.P0UpdateDelete => await UpdateDeleteAsync(db),
            ProbeId.P0QueryAll => await QueryAllAsync(db, dbPath),
            ProbeId.P0Aggregate => await AggregateAsync(db, dbPath),
            ProbeId.P0Transaction => await TransactionAsync(db, dbPath),
            _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
        };
    }

    private static async Task<ProbeObservation> DdlAsync(ApDbContext db, string dbPath)
    {
        _ = await db.Database.EnsureCreatedAsync();
        long tables = await RawChannel.TableCountAsync(dbPath);
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static async Task<ProbeObservation> InsertExplicitPkAsync(ApDbContext db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        await RawChannel.EnsureCatalogTableAsync(dbPath);
        _ = db.Catalog.Add(new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue });
        _ = await db.SaveChangesAsync();
        CatalogRow? readBack = await db.Catalog.FindAsync(CatalogIdValue);
        string raw = await RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue));
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", CatalogIdValue),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> InsertAutoPkAsync(ApDbContext db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        await RawChannel.EnsureOrderTableAsync(dbPath);
        var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
        _ = db.Orders.Add(row);
        _ = await db.SaveChangesAsync();
        OrderRow? readBack = await db.Orders.FindAsync(row.Id);
        string raw = await RawChannel.ScalarStringAsync(dbPath, "SELECT name FROM ap_order WHERE Id = @p0",
            ("@p0", (object)row.Id));
        return Ok(ProbeId.P0InsertAutoPk,
            F("insert.id", row.Id),
            F("orm.readback.name", readBack?.Name ?? ""),
            F("raw.stored.name", raw),
            F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            FT("orm.readback.amount.type", readBack?.Amount.GetType()),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static async Task<ProbeObservation> GetByKeyAsync(ApDbContext db, string dbPath)
    {
#if AOTPROBE_B
        await RawChannel.EnsureOrderTableAsync(dbPath);
        await RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1);
        const long keyId = 1;
        OrderRow? found = await db.Orders.FindAsync(keyId);
#else
        await RawChannel.EnsureOrderTableAsync(dbPath);
        var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
        _ = db.Orders.Add(row);
        _ = await db.SaveChangesAsync();
        long keyId = row.Id;
        db.ChangeTracker.Clear();
        OrderRow? found = await db.Orders.FindAsync(keyId);
#endif
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static async Task<ProbeObservation> UpdateDeleteAsync(ApDbContext db)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        string dbPath = db.Database.GetDbConnection().DataSource;
        await RawChannel.EnsureOrderTableAsync(dbPath);
        var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
        _ = db.Orders.Add(row);
        _ = await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        OrderRow? loaded = await db.Orders.FindAsync(row.Id);
        if (loaded is null)
            throw new InvalidOperationException("reload failed before update");
        loaded.Amount = AmountUpdated;
        int updated = await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        OrderRow? afterUpdate = await db.Orders.FindAsync(row.Id);
        string afterAmount = afterUpdate?.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";

        OrderRow? victim = await db.Orders.FindAsync(row.Id);
        if (victim is null)
            throw new InvalidOperationException("reload failed before delete");
        _ = db.Orders.Remove(victim);
        int deleted = await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        bool afterDeleteFound = await db.Orders.FindAsync(row.Id) is not null;
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterAmount),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDeleteFound));
#endif
    }

    private static async Task<ProbeObservation> QueryAllAsync(ApDbContext db, string dbPath)
    {
#if AOTPROBE_B
        await RawChannel.EnsureOrderTableAsync(dbPath);
        await RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2);
#else
        await RawChannel.EnsureOrderTableAsync(dbPath);
        _ = db.Orders.Add(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
        _ = db.Orders.Add(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 });
        _ = db.Orders.Add(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 });
        _ = await db.SaveChangesAsync();
#endif
        List<OrderRow> rows = await db.Orders.OrderBy(x => x.Id).ToListAsync();
        return Ok(ProbeId.P0QueryAll,
            F("list.count", rows.Count),
            FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
            F("names.byid", string.Join("|", rows.Select(r => r.Name))));
    }

    private static async Task<ProbeObservation> AggregateAsync(ApDbContext db, string dbPath)
    {
#if AOTPROBE_B
        await RawChannel.EnsureOrderTableAsync(dbPath);
        await RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1);
        await RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2);
#else
        await RawChannel.EnsureOrderTableAsync(dbPath);
        _ = db.Orders.Add(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
        _ = db.Orders.Add(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 });
        _ = db.Orders.Add(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 });
        _ = await db.SaveChangesAsync();
#endif
        int total = await db.Orders.CountAsync();
        List<CategoryCount> grouped = await db.Orders
            .GroupBy(x => x.Category)
            .Select(g => new CategoryCount { Category = g.Key, Count = g.LongCount() })
            .ToListAsync();
        return Ok(ProbeId.P0Aggregate,
            F("count.total", total),
            F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
            F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
            FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
    }

    private static async Task<ProbeObservation> TransactionAsync(ApDbContext db, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        await RawChannel.EnsureOrderTableAsync(dbPath);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            _ = db.Orders.Add(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 });
            _ = await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        long kept = await RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept);

        await using (var tx2 = await db.Database.BeginTransactionAsync())
        {
            _ = db.Orders.Add(new OrderRow { Name = TxDropped, Amount = Amount, Category = Cat2 });
            _ = await db.SaveChangesAsync();
            await tx2.RollbackAsync();
        }
        long dropped = await RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped);

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }
}

/// <summary>设计时工厂（官方形态）：dotnet ef dbcontext optimize 实例化 DbContext 的通道。</summary>
public sealed class ApDbContextFactory : IDesignTimeDbContextFactory<ApDbContext>
{
    public ApDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ApDbContext>();
        _ = builder.UseSqlite("Data Source=design-time.db");
        return new ApDbContext(builder.Options);
    }
}

/// <summary>EF Core 官方形态 DbContext（fluent 映射；无 [Db] 特性——compiled model 生成同源）。</summary>
public sealed class ApDbContext : DbContext
{
    public ApDbContext(DbContextOptions<ApDbContext> options) : base(options) { }

    public DbSet<OrderRow> Orders => Set<OrderRow>();
    public DbSet<CatalogRow> Catalog => Set<CatalogRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderRow>(e =>
        {
            e.ToTable("ap_order");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Amount).HasColumnName("amount");
            e.Property(x => x.Category).HasColumnName("category");
        });
        modelBuilder.Entity<CatalogRow>(e =>
        {
            e.ToTable("ap_catalog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Label).HasColumnName("label");
        });
    }
}
