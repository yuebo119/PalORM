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

    // ITM-404 回归：BulkDeleteAsync 与单条 DeleteAsync 同语义——跨租户主键命中 0 行。
    [Test]
    public async Task BulkDeleteAsync_CrossTenantKeys_AffectsZeroRows()
    {
        var (db, otherId) = await SetupTwoTenantsAsync();
        await using var _ = db;
        long rows = await db.BulkDeleteAsync<TenantEntity>([otherId]);
        await Assert.That(rows).IsEqualTo(0);

        db.IgnoreFilters();
        var survivor = await db.GetAsync<TenantEntity>(otherId);
        await Assert.That(survivor!.Value).IsEqualTo("theirs");
    }

    // ITM-401 回归：OrWhere 不得绕过租户过滤——首个用户子句 OrWhere 与 Where+OrWhere
    // 组合均生成 WHERE tenant AND ((...) OR (...))，OR 分支被括组隔离。
    [Test]
    public async Task OrWhere_AsFirstUserClause_CannotEscapeTenantFilter()
    {
        var (db, _) = await SetupTwoTenantsAsync();
        await using var __ = db;
        var rows = await db.From<TenantEntity>().OrWhere($"value = {"theirs"}").ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OrWhere_AfterWhere_CannotEscapeTenantFilter()
    {
        var (db, _) = await SetupTwoTenantsAsync();
        await using var __ = db;
        var rows = await db.From<TenantEntity>()
            .Where($"value = {"mine"}").OrWhere($"value = {"theirs"}").ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Value).IsEqualTo("mine");
    }
}

