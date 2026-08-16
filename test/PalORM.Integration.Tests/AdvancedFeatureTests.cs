using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

public sealed class AdvancedFeatureTests
{
    [Test]
    public async Task ConcurrencyCheck_UpdateSucceeds()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var e = await db.InsertAsync(new VersionedEntity { Name = "V1", Version = 0 });
        e.Name = "V2";
        int rows = await db.UpdateAsync(e);
        await Assert.That(rows).IsEqualTo(1);
        await Assert.That(e.Version).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrencyCheck_StaleVersionThrows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var inserted = await db.InsertAsync(new VersionedEntity { Name = "V1", Version = 0 });
        var first = await db.GetAsync<VersionedEntity>(inserted.Id);
        var stale = await db.GetAsync<VersionedEntity>(inserted.Id);
        first!.Name = "First";
        await db.UpdateAsync(first);
        stale!.Name = "Stale";
        await Assert.That(async () => await db.UpdateAsync(stale)).Throws<ConcurrencyConflictException>();
        await Assert.That(stale.Version).IsEqualTo(0);
    }

    [Test]
    public async Task SoftDelete_DefaultQueriesHideRowAndIgnoreFiltersRevealsIt()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var e = await db.InsertAsync(new SoftDeletableEntity { Name = "SD" });
        await db.DeleteAsync<SoftDeletableEntity>(e.Id);
        await Assert.That(await db.GetAsync<SoftDeletableEntity>(e.Id)).IsNull();
        await Assert.That((await db.From<SoftDeletableEntity>().ToListAsync()).Count).IsEqualTo(0);
        await Assert.That(await db.CountAsync<SoftDeletableEntity>()).IsEqualTo(0);
        db.IgnoreFilters();
        var found = await db.GetAsync<SoftDeletableEntity>(e.Id);
        await Assert.That(found!.Name).IsEqualTo("SD");
        await Assert.That(found.DeletedAt).IsNotNull();
    }

    [Test]
    public async Task SoftDelete_DefaultAggregatesExcludeDeletedRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var kept = await db.InsertAsync(new SoftDeletableEntity { Name = "Kept" });
        var deleted = await db.InsertAsync(new SoftDeletableEntity { Name = "Deleted" });
        await db.DeleteAsync<SoftDeletableEntity>(deleted.Id);
        await Assert.That(await db.SumAsync<SoftDeletableEntity>($"Id")).IsEqualTo(kept.Id);
        await Assert.That(await db.MaxAsync<SoftDeletableEntity, long>($"Id")).IsEqualTo(kept.Id);
        await Assert.That(await db.MinAsync<SoftDeletableEntity, long>($"Id")).IsEqualTo(kept.Id);
        await Assert.That(await db.AvgAsync<SoftDeletableEntity>($"Id")).IsEqualTo(kept.Id);
    }

    [Test]
    public async Task TenantAware_Filters()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        db.WithTenant(1L);
        await db.InsertAsync(new TenantEntity { TenantId = 1, Value = "T1" });
        await Assert.That((await db.From<TenantEntity>().ToListAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task BulkInsertAsync_UsesAmbientTransaction()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await using var transaction = await db.BeginTransactionAsync();
        await db.BulkInsertAsync([new Product { Name = "Rollback", Price = 1m, Stock = 1 }]);
        await transaction.RollbackAsync();
        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(0);
    }

    [Test]
    public async Task BulkMergeAsync_RepeatedKeysUpdateWithoutAddingRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        Product[] items = [new Product { Id = 101, Name = "A", Price = 1m, Stock = 1 }, new Product { Id = 102, Name = "B", Price = 2m, Stock = 2 }];
        await db.BulkMergeAsync(items);
        items[0].Name = "Updated";
        await db.BulkMergeAsync(items);
        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(2);
        await Assert.That((await db.GetAsync<Product>(101L))!.Name).IsEqualTo("Updated");
    }

    // R1 回归覆盖：默认键实体（Id=0）走 InsertCoreAsync 路径
    [Test]
    public async Task BulkMergeAsync_DefaultKeys_InsertsNewRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        Product[] items = [
            new Product { Name = "Default1", Price = 1m, Stock = 1 },
            new Product { Name = "Default2", Price = 2m, Stock = 2 }];
        await db.BulkMergeAsync(items);
        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(2);
        // 验证自增 ID 被回填
        await Assert.That(items[0].Id).IsGreaterThan(0);
        await Assert.That(items[1].Id).IsGreaterThan(0);
    }

    [Test]
    public async Task SeedAsync_IsIdempotentAndRequiresStableKeys()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        Product[] items = [new Product { Id = 201, Name = "Seed", Price = 1m, Stock = 1 }];
        await db.SeedAsync(items);
        items[0].Name = "SeedUpdated";
        await db.SeedAsync(items);
        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(1);
        await Assert.That((await db.GetAsync<Product>(201L))!.Name).IsEqualTo("SeedUpdated");
        await Assert.That(async () => await db.SeedAsync([new Product { Name = "Invalid", Price = 1m }])).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Savepoint_Rollback_PreservesPriorInserts()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        using var t = await db.BeginTransactionAsync();
        await db.InsertAsync(new Product { Name = "SP1", Price = 1m, Stock = 0 });
        await db.SavepointAsync(t, "sp1");
        await db.InsertAsync(new Product { Name = "SP2", Price = 2m, Stock = 0 });
        await db.RollbackToAsync(t, "sp1");
        await t.CommitAsync();
        // SP2 已回滚，SP1 保留
        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(1);
        await Assert.That((await db.GetAsync<Product>(1L))!.Name).IsEqualTo("SP1");
    }

    [Test]
    public async Task StoredProc_Builder_ValidatesNameAndRejectsSecondExecution()
    {
        await using var db = await TestDb.SqliteAsync();
        // 过程名白名单：特殊字符明确拒绝（防注入纵深）
        await Assert.That(() => db.StoredProc("bad name; DROP")).Throws<ArgumentException>();
        // 未注册输出参数名明确失败（SqliteParameter 不支持 Output 方向，输出参数行为由 MySQL/PG CI 覆盖）
        var sp = db.StoredProc("proc_ok").WithParam("@p0", "hello");
        await Assert.That(() => sp.GetOutputValue<long>("@missing")).Throws<InvalidOperationException>();
        // S108: 用 ThrowsAsync 显式断言预期异常，避免空 catch
        await Assert.ThrowsAsync<ArgumentException>(async () => await sp.ExecuteAsync());
        await Assert.That(async () => await sp.ExecuteAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task GetRawConnection_Open()
    {
        await using var db = await TestDb.SqliteAsync();
        await Assert.That(db.GetRawConnection().State).IsEqualTo(System.Data.ConnectionState.Open);
    }

    [Test]
    public async Task QueryAsyncEnumerable_Streams()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "ST", Price = 1m, Stock = 0 });
        int c = 0;
        await foreach (var p in db.QueryAsyncEnumerable<Product>($"SELECT * FROM products")) { c++; }
        await Assert.That(c).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task WithCache_CachesResults()
    {
        // T-P2-02：注入计数缓存锁定真实命中——原断言只比较行数相等，不缓存也通过
        var cache = new CountingCache();
        var options = new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveSqliteConnectionString(),
            QueryCache = cache
        };
        await using var db = await DataSession<SqliteProvider>.CreateAsync(options);
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "C1", Price = 1m, Stock = 0 });
        var r1 = await db.From<Product>().WithCache("t1", TimeSpan.FromMinutes(1)).ToListAsync();
        var r2 = await db.From<Product>().WithCache("t1", TimeSpan.FromMinutes(1)).ToListAsync();
        // 两次查询都查缓存、首次写入后命中不再重写
        await Assert.That(cache.TryGetCalls).IsEqualTo(2);
        await Assert.That(cache.SetCalls).IsEqualTo(1);
        await Assert.That(r1.Count).IsEqualTo(1);
        await Assert.That(r2.Count).IsEqualTo(1);
        // 命中返回新 List（浅拷贝契约，见 WithCache 文档）
        await Assert.That(ReferenceEquals(r1, r2)).IsFalse();
    }

    [Test]
    public async Task WithCache_ReturnsSnapshotCopies_CallerMutationDoesNotPolluteCache()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "S1", Price = 1m, Stock = 0 });
        var r1 = await db.From<Product>().WithCache("snap1", TimeSpan.FromMinutes(1)).ToListAsync();
        r1.Clear();
        var r2 = await db.From<Product>().WithCache("snap1", TimeSpan.FromMinutes(1)).ToListAsync();
        r2.Add(new Product { Name = "X", Price = 9m, Stock = 0 });
        var r3 = await db.From<Product>().WithCache("snap1", TimeSpan.FromMinutes(1)).ToListAsync();
        await Assert.That(r2.Count).IsEqualTo(2);
        await Assert.That(r3.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(r2, r3)).IsFalse();
    }

    [Test]
    public async Task Raw_LiteralInjection()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "R1", Price = 1m, Stock = 0 });
        var r = await db.From<Product>().Where($"name = {"R1"}").Raw("LIMIT 1").ToListAsync();
        await Assert.That(r.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AsPrepared_ExecutesAndReturnsSameRowsAsUnprepared()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "P1", Price = 1m, Stock = 0 });
        var prepared = await db.From<Product>().AsPrepared().Where($"name = {"P1"}").ToListAsync();
        var plain = await db.From<Product>().Where($"name = {"P1"}").ToListAsync();
        await Assert.That(prepared.Count).IsEqualTo(1);
        await Assert.That(prepared.Count).IsEqualTo(plain.Count);
        await Assert.That(prepared[0].Name).IsEqualTo("P1");
    }

    [Test]
    public async Task Interceptor_FiresCallbacks()
    {
        int b = 0, a = 0;
        var opt = new DbOptions { ConnectionString = "Data Source=:memory:", Interceptors = [new CallbackTestInterceptor(() => b++, () => a++)] };
        await using var db = await DataSession<SqliteProvider>.CreateAsync(opt);
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "I1", Price = 1m, Stock = 0 });
        await db.From<Product>().ToListAsync();
        await Assert.That(b).IsEqualTo(1);
        await Assert.That(a).IsEqualTo(1);
    }

    [Test]
    public async Task AddInterceptor_OrdersDynamicInterceptorsByPriority()
    {
        var order = new List<int>();
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        db.AddInterceptor(new OrderedInterceptor(200, order)).AddInterceptor(new OrderedInterceptor(10, order));
        await db.From<Product>().ToListAsync();
        await Assert.That(order).IsEquivalentTo([10, 200]);
    }

    [Test]
    public async Task CountAsync_ReturnsCount()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "C1", Price = 1m, Stock = 0 });
        await db.InsertAsync(new Product { Name = "C2", Price = 2m, Stock = 0 });
        long c = await db.CountAsync<Product>();
        await Assert.That(c).IsEqualTo(2);
    }

    [Test]
    public async Task WindowOver_DryRun_GeneratesSQL()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>().UnsafeWindowOver("ROW_NUMBER()", "PARTITION BY stock ORDER BY price DESC").AsDryRun();
        await Assert.That(dry.Sql).Contains("ROW_NUMBER");
        await Assert.That(dry.Sql).Contains("OVER");
    }

    /// <summary>T-P2-02 计数缓存：记录 TryGet/Set 调用次数，锁定缓存真实命中。</summary>
    private sealed class CountingCache : IQueryCache
    {
        internal int TryGetCalls;
        internal int SetCalls;
        private readonly Dictionary<string, object> _store = [];

        public bool TryGet<T>(string key, out T? value) where T : class
        {
            TryGetCalls++;
            if (_store.TryGetValue(key, out object? cached) && cached is T typed)
            {
                value = typed;
                return true;
            }
            value = null;
            return false;
        }

        public void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class
        {
            SetCalls++;
            _store[key] = value;
        }

        public void Clear() => _store.Clear();
    }
}

#region Test Entities
[Table("versioned")]
public partial class VersionedEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("version")] [ConcurrencyCheck] public long Version { get; set; }
}

[SoftDelete]
[Table("soft_deletable")]
public partial class SoftDeletableEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}

[TenantAware]
[Table("tenant_data")]
public partial class TenantEntity
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("value")] public string Value { get; set; } = "";
}
#endregion
