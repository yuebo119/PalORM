using PalORM.Testing;

namespace PalORM.Integration.Tests;

// ITM-302 回归：TenantAware 过滤必须覆盖全部直连入口（GetAsync/GetAllAsync/
// UpdateAsync/DeleteAsync/CountAsync/聚合），而不仅是 From<T>()。
public sealed class TenantIsolationTests
{
    private static async Task<(DataSession<PalORM.Sqlite.SqliteProvider> db, long otherTenantRowId)> SetupTwoTenantsAsync()
    {
        var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var mine = await db.InsertAsync(new TenantEntity { TenantId = 1, Value = "mine" });
        var theirs = await db.InsertAsync(new TenantEntity { TenantId = 2, Value = "theirs" });
        _ = mine;
        db.WithTenant(1L);
        return (db, theirs.Id);
    }

    [Test]
    public async Task GetAsync_CrossTenantKey_ReturnsNull()
    {
        var (db, otherId) = await SetupTwoTenantsAsync();
        await using var _ = db;
        await Assert.That(await db.GetAsync<TenantEntity>(otherId)).IsNull();
    }

    [Test]
    public async Task GetAllAsync_OnlyReturnsCurrentTenant()
    {
        var (db, _) = await SetupTwoTenantsAsync();
        await using var __ = db;
        var all = await db.GetAllAsync<TenantEntity>();
        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0].Value).IsEqualTo("mine");
    }

    [Test]
    public async Task CountAsync_OnlyCountsCurrentTenant()
    {
        var (db, _) = await SetupTwoTenantsAsync();
        await using var __ = db;
        await Assert.That(await db.CountAsync<TenantEntity>()).IsEqualTo(1);
        // 含 OR 的用户条件不得穿透租户过滤（ITM-307 括号纪律）
        long withOr = await db.CountAsync<TenantEntity>($"value = {"mine"} OR value = {"theirs"}");
        await Assert.That(withOr).IsEqualTo(1);
    }

    [Test]
    public async Task UpdateAsync_CrossTenantEntity_AffectsZeroRows()
    {
        var (db, otherId) = await SetupTwoTenantsAsync();
        await using var _ = db;
        int rows = await db.UpdateAsync(new TenantEntity { Id = otherId, TenantId = 2, Value = "hacked" });
        await Assert.That(rows).IsEqualTo(0);

        db.IgnoreFilters();
        var untouched = await db.GetAsync<TenantEntity>(otherId);
        await Assert.That(untouched!.Value).IsEqualTo("theirs");
    }

    [Test]
    public async Task DeleteAsync_CrossTenantKey_AffectsZeroRows()
    {
        var (db, otherId) = await SetupTwoTenantsAsync();
        await using var _ = db;
        int rows = await db.DeleteAsync<TenantEntity>(otherId);
        await Assert.That(rows).IsEqualTo(0);

        db.IgnoreFilters();
        await Assert.That(await db.GetAsync<TenantEntity>(otherId)).IsNotNull();
    }

    [Test]
    public async Task IgnoreFilters_RevealsAllTenants()
    {
        var (db, _) = await SetupTwoTenantsAsync();
        await using var __ = db;
        db.IgnoreFilters();
        await Assert.That((await db.GetAllAsync<TenantEntity>()).Count).IsEqualTo(2);
    }
}

// ITM-324 回归：单条软删除与 BulkDelete 幂等语义一致——重复删除返回 0 且不刷新时间戳。
public sealed class SoftDeleteIdempotencyTests
{
    [Test]
    public async Task DeleteAsync_Twice_SecondReturnsZeroAndKeepsTimestamp()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var e = await db.InsertAsync(new SoftDeletableEntity { Name = "SD" });

        await Assert.That(await db.DeleteAsync<SoftDeletableEntity>(e.Id)).IsEqualTo(1);
        db.IgnoreFilters();
        var afterFirst = await db.GetAsync<SoftDeletableEntity>(e.Id);
        string? firstDeletedAt = afterFirst!.DeletedAt;

        await Assert.That(await db.DeleteAsync<SoftDeletableEntity>(e.Id)).IsEqualTo(0);
        var afterSecond = await db.GetAsync<SoftDeletableEntity>(e.Id);
        await Assert.That(afterSecond!.DeletedAt).IsEqualTo(firstDeletedAt);
    }
}
