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

    // ─── ADR-L：多租户缓存结构性隔离 ─────────────────────────────

    private static async Task<DataSession<SqliteProvider>> CreateTenantSessionAsync(IQueryCache cache, string tenant)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:",
            QueryCache = cache
        });
        await session.ExecuteAsync(
            $"CREATE TABLE tenant_cache_probe (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, tenant_id TEXT NOT NULL)");
        session.WithTenant(tenant);
        return session;
    }

    [Test]
    public async Task TenantScope_SharedCache_SameUserKey_NoCrossTenantLeak()
    {
        // ADR-L 核心防线：两个租户会话共享同一缓存实例、同一 userKey——修复前 B 命中
        // A 的条目拿到 A 的数据（静默串租），结构性前缀后 B 必 miss、只见到自己库的数据。
        // SQLite :memory: 每会话独立库：B 库为空，命中 A 缓存即铁证。
        var shared = new BoundedQueryCache();
        await using DataSession<SqliteProvider> sessionA = await CreateTenantSessionAsync(shared, "a");
        await using DataSession<SqliteProvider> sessionB = await CreateTenantSessionAsync(shared, "b");
        await sessionA.InsertAsync(new TenantCacheEntity { Name = "alpha", TenantId = "a" });

        var fromA = await sessionA.From<TenantCacheEntity>()
            .WithCache("shared-key", TimeSpan.FromMinutes(1)).ToListAsync();
        var fromB = await sessionB.From<TenantCacheEntity>()
            .WithCache("shared-key", TimeSpan.FromMinutes(1)).ToListAsync();

        await Assert.That(fromA.Count).IsEqualTo(1);
        await Assert.That(fromA[0].Name).IsEqualTo("alpha");
        await Assert.That(fromB.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TenantScope_IgnoreFilters_UsesAllNamespace()
    {
        // ADR-L：多租户会话 IgnoreFilters 的全量查询走 __all__ 命名空间——与租户命名空间
        // 互不可见（修复前同 userKey 直接命中租户条目，全量调用方拿到过滤子集）。
        var shared = new BoundedQueryCache();
        await using DataSession<SqliteProvider> sessionA = await CreateTenantSessionAsync(shared, "a");
        await using DataSession<SqliteProvider> sessionAll = await CreateTenantSessionAsync(shared, "c");
        await sessionA.InsertAsync(new TenantCacheEntity { Name = "alpha", TenantId = "a" });
        sessionAll.IgnoreFilters();
        await sessionAll.InsertAsync(new TenantCacheEntity { Name = "gamma", TenantId = "c" });

        var fromA = await sessionA.From<TenantCacheEntity>()
            .WithCache("shared-key", TimeSpan.FromMinutes(1)).ToListAsync();
        var fromAll = await sessionAll.From<TenantCacheEntity>()
            .WithCache("shared-key", TimeSpan.FromMinutes(1)).ToListAsync();

        await Assert.That(fromA.Count).IsEqualTo(1);
        await Assert.That(fromA[0].Name).IsEqualTo("alpha");
        await Assert.That(fromAll.Count).IsEqualTo(1);
        await Assert.That(fromAll[0].Name).IsEqualTo("gamma");
    }

    [Test]
    public async Task TenantScope_SingleTenant_KeyRemainsUnprefixed()
    {
        // ADR-L：单租户会话（未 WithTenant）行为零变化——key 原样进出缓存实例
        var cache = new RecordingCache();
        await using DataSession<SqliteProvider> session = await CreateSessionAsync(cache);
        await session.InsertAsync(new QueryCacheEntity { Name = "solo" });

        await session.From<QueryCacheEntity>()
            .WithCache("plain-key", TimeSpan.FromMinutes(1)).ToListAsync();

        await Assert.That(cache.SetKeys).Contains("plain-key");
        await Assert.That(cache.SetKeys.Count).IsEqualTo(1);
    }

    /// <summary>记录实际收到的 key（ADR-L 行为断言用——前缀是否编入、是否原样）。</summary>
    private sealed class RecordingCache : IQueryCache
    {
        internal List<string> SetKeys { get; } = [];
        private readonly Dictionary<string, object> _store = [];

        public bool TryGet<T>(string key, out T? value) where T : class
        {
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
            SetKeys.Add(key);
            _store[key] = value;
        }

        public void Clear() => _store.Clear();
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

[TenantAware]
[Table("tenant_cache_probe")]
internal sealed partial class TenantCacheEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = "";
    [Column("tenant_id")]
    public string TenantId { get; set; } = "";
}
#endregion
