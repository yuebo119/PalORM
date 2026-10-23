using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>2026-10-22 终审 P0-1：租户缓存命名空间单射性。
/// 原实现 <c>__t:{tenantId}:{userKey}</c> 以 ':' 裸拼接，租户 "a" + key "b:c" 与
/// 租户 "a:b" + key "c" 同键，第二个租户会命中第一个租户的缓存行。</summary>
public sealed class QueryCacheTenantKeyTests
{
    [Test]
    public async Task TenantAndKeyWithColon_DoNotCollideAcrossTenants()
    {
        var cache = new BoundedQueryCache();
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:",
            QueryCache = cache
        });
        await db.MigrateAsync();

        db.WithTenant("a");
        await db.InsertAsync(new CacheTenantProbe { Id = 1, TenantId = "a", Name = "row-a" });
        db.WithTenant("a:b");
        await db.InsertAsync(new CacheTenantProbe { Id = 2, TenantId = "a:b", Name = "row-b" });

        db.WithTenant("a");
        var first = await db.From<CacheTenantProbe>().WithCache("b:c").ToListAsync();
        db.WithTenant("a:b");
        var second = await db.From<CacheTenantProbe>().WithCache("c").ToListAsync();

        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(first[0].Id).IsEqualTo(1L);
        await Assert.That(second.Count).IsEqualTo(1);
        await Assert.That(second[0].Id).IsEqualTo(2L);
        await Assert.That(second[0].TenantId).IsEqualTo("a:b");
    }
}

[Table("cache_tenant_probe")]
[TenantAware]
public sealed class CacheTenantProbe
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("tenant_id")] public string TenantId { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
}
