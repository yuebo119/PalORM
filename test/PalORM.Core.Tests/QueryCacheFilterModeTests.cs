using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>2026-10-22 终审 P1-6：缓存 key 必须含 IgnoreFilters/软删维度。
/// 非租户会话此前 scope=null，普通查询与 IgnoreFilters 查询用同一 WithCache key 时互撞：
/// 正向隐藏软删行，反向把软删行返回给普通查询。</summary>
public sealed class QueryCacheFilterModeTests
{
    [Test]
    public async Task NormalThenIgnoreFilters_DoesNotReuseFilteredEntries()
    {
        var cache = new BoundedQueryCache();
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:",
            QueryCache = cache
        });
        await db.MigrateAsync();
        await db.InsertAsync(new CacheSoftDeleteProbe { Id = 1, Name = "live" });
        await db.InsertAsync(new CacheSoftDeleteProbe { Id = 2, Name = "dead", DeletedAt = DateTime.UtcNow });

        var normal = await db.From<CacheSoftDeleteProbe>().WithCache("docs").ToListAsync();
        db.IgnoreFilters();
        var full = await db.From<CacheSoftDeleteProbe>().WithCache("docs").ToListAsync();

        await Assert.That(normal.Count).IsEqualTo(1);
        await Assert.That(normal[0].Id).IsEqualTo(1L);
        await Assert.That(full.Count).IsEqualTo(2);
    }

    [Test]
    public async Task IgnoreFiltersThenNormal_DoesNotReturnSoftDeletedRows()
    {
        var cache = new BoundedQueryCache();
        string cs = $"Data Source=cache_filter_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keeper = new SqliteConnection(cs);
        await keeper.OpenAsync();

        await using (var setup = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = cs, QueryCache = cache }))
        {
            await setup.MigrateAsync();
            await setup.InsertAsync(new CacheSoftDeleteProbe { Id = 1, Name = "live" });
            await setup.InsertAsync(new CacheSoftDeleteProbe { Id = 2, Name = "dead", DeletedAt = DateTime.UtcNow });
        }

        await using (var fullSession = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = cs, QueryCache = cache }))
        {
            fullSession.IgnoreFilters();
            var full = await fullSession.From<CacheSoftDeleteProbe>().WithCache("docs").ToListAsync();
            await Assert.That(full.Count).IsEqualTo(2);
        }

        await using (var normalSession = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = cs, QueryCache = cache }))
        {
            var normal = await normalSession.From<CacheSoftDeleteProbe>().WithCache("docs").ToListAsync();
            await Assert.That(normal.Count).IsEqualTo(1);
            await Assert.That(normal[0].Id).IsEqualTo(1L);
        }
    }
}

[Table("cache_soft_delete_probe")]
[SoftDelete]
public sealed class CacheSoftDeleteProbe
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("deleted_at")] public DateTime? DeletedAt { get; set; }
}
