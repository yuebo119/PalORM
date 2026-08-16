using PalORM.Sqlite;

namespace PalORM.Core.Tests;

[NotInParallel("CacheStore")]
public sealed class QueryCacheInjectionTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync(IQueryCache? cache = null)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:",
            QueryCache = cache
        });
        await session.ExecuteAsync(
            $"CREATE TABLE qcache_items (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)");
        return session;
    }

    [Test]
    public async Task InjectedCache_IsUsedInsteadOfProcessDefault()
    {
        // r19/T-P3-18：原断言依赖进程级默认 CacheStore 状态（跨测试耦合）——
        // 改为计数缓存：注入实例收到 TryGet+Set 即证明未落到默认实例
        var cache = new CountingCache();
        await using DataSession<SqliteProvider> session = await CreateSessionAsync(cache);
        await session.InsertAsync(new QueryCacheEntity { Name = "A" });

        await session.From<QueryCacheEntity>()
            .WithCache("inject-key", TimeSpan.FromMinutes(1)).ToListAsync();

        await Assert.That(cache.TryGetCalls).IsEqualTo(1);
        await Assert.That(cache.SetCalls).IsEqualTo(1);
    }

    [Test]
    public async Task TwoSessions_WithSeparateCaches_AreIsolated()
    {
        var cacheA = new BoundedQueryCache();
        var cacheB = new BoundedQueryCache();
        await using DataSession<SqliteProvider> sessionA = await CreateSessionAsync(cacheA);
        await using DataSession<SqliteProvider> sessionB = await CreateSessionAsync(cacheB);
        await sessionA.InsertAsync(new QueryCacheEntity { Name = "A" });

        var fromA = await sessionA.From<QueryCacheEntity>()
            .WithCache("same-key", TimeSpan.FromMinutes(1)).ToListAsync();
        var fromB = await sessionB.From<QueryCacheEntity>()
            .WithCache("same-key", TimeSpan.FromMinutes(1)).ToListAsync();

        // B 库为空表：同 key 不同缓存实例互不串数据
        await Assert.That(fromA.Count).IsEqualTo(1);
        await Assert.That(fromB.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BoundedCache_AtCapacity_RejectsNewEntriesInsteadOfGrowing()
    {
        var cache = new BoundedQueryCache(maxEntries: 2);
        cache.Set("k1", new List<string> { "a" }, TimeSpan.FromMinutes(5));
        cache.Set("k2", new List<string> { "b" }, TimeSpan.FromMinutes(5));

        cache.Set("k3", new List<string> { "c" }, TimeSpan.FromMinutes(5));

        await Assert.That(cache.TryGet("k1", out List<string>? _)).IsTrue();
        await Assert.That(cache.TryGet("k2", out List<string>? _)).IsTrue();
        // 容量满且无过期条目：拒绝写入（未命中是正确性中性的）
        await Assert.That(cache.TryGet("k3", out List<string>? _)).IsFalse();
    }

    [Test]
    public async Task BoundedCache_AtCapacity_EvictsExpiredThenAccepts()
    {
        var cache = new BoundedQueryCache(maxEntries: 2);
        cache.Set("expired", new List<string> { "x" }, TimeSpan.FromMilliseconds(1));
        cache.Set("live", new List<string> { "y" }, TimeSpan.FromMinutes(5));
        await Task.Delay(30);

        cache.Set("fresh", new List<string> { "z" }, TimeSpan.FromMinutes(5));

        await Assert.That(cache.TryGet("fresh", out List<string>? _)).IsTrue();
        await Assert.That(cache.TryGet("live", out List<string>? _)).IsTrue();
    }

    [Test]
    public async Task BoundedCache_ExistingKeyUpdate_BypassesCapacityCheck()
    {
        var cache = new BoundedQueryCache(maxEntries: 1);
        cache.Set("k", new List<string> { "v1" }, TimeSpan.FromMinutes(5));

        cache.Set("k", new List<string> { "v2", "v3" }, TimeSpan.FromMinutes(5));

        await Assert.That(cache.TryGet("k", out List<string>? value)).IsTrue();
        await Assert.That(value!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ToPageAsync_DoesNotWriteUserCacheKey()
    {
        // r9-SA/r10-N3（r11.5 片 B 揭穿后真交付——"声称未交付"第三例）：
        // 页截断结果不得写入用户缓存键——同键 ToListAsync 将静默命中单页子集（ITM-406 族）
        var cache = new BoundedQueryCache();
        await using DataSession<SqliteProvider> session = await CreateSessionAsync(cache);
        await session.InsertAsync(new QueryCacheEntity { Name = "A" });
        await session.InsertAsync(new QueryCacheEntity { Name = "B" });

        _ = await session.From<QueryCacheEntity>()
            .WithCache("page-key", TimeSpan.FromMinutes(1))
            .OrderBy(x => x.Id)
            .ToPageAsync(1, x => x.Id);

        await Assert.That(cache.TryGet("page-key", out List<QueryCacheEntity>? _)).IsFalse();
    }

    /// <summary>r19/T-P3-18 计数缓存——记录 TryGet/Set 调用次数，锁定注入实例被真实使用。</summary>
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
[Table("qcache_items")]
internal sealed partial class QueryCacheEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = "";
}
#endregion
