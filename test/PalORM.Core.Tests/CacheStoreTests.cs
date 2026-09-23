namespace PalORM.Core.Tests;

[NotInParallel("CacheStore")]
public sealed class CacheStoreTests
{
    [Test]
    public async Task TryGet_ExpiredEntry_IsRemovedFromStore()
    {
        const string key = "cache-store-expired";
        CacheStore.Set(key, new List<string> { "a" }, TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);

        bool hit = CacheStore.TryGet(key, out List<string>? value);

        await Assert.That(hit).IsFalse();
        await Assert.That(value).IsNull();
        // 二次读取仍未命中且不抛（条目已被移除，非仅判定过期）
        await Assert.That(CacheStore.TryGet(key, out List<string>? _)).IsFalse();
    }

    [Test]
    public async Task Set_ThenTryGet_WithinTtl_ReturnsValue()
    {
        const string key = "cache-store-live";
        var stored = new List<string> { "a", "b" };
        CacheStore.Set(key, stored, TimeSpan.FromMinutes(5));

        bool hit = CacheStore.TryGet(key, out List<string>? value);

        await Assert.That(hit).IsTrue();
        await Assert.That(value!.Count).IsEqualTo(2);
        CacheStore.Clear();
    }

    [Test]
    public async Task Set_BeyondCapacity_StopsAtLimit()
    {
        // CACHE-001（2026-09-23）：容量判定从 ConcurrentDictionary.Count（全分段锁扫描）改为
        // Interlocked 近似计数——上限仍必须硬性成立，且满员时既有键的更新不被拒。
        var cache = new BoundedQueryCache(maxEntries: 8);
        for (int i = 0; i < 50; i++)
            cache.Set($"cap-{i}", new List<string> { "v" });

        int hits = 0;
        for (int i = 0; i < 50; i++)
        {
            if (cache.TryGet($"cap-{i}", out List<string>? _)) hits++;
        }

        await Assert.That(hits).IsEqualTo(8);   // 前 8 个入表，其余拒写（无过期条目可淘汰）

        // 满员时更新既有键仍允许（原语义：只拒新键）
        cache.Set("cap-0", new List<string> { "updated" });
        await Assert.That(cache.TryGet("cap-0", out List<string>? value)).IsTrue();
        await Assert.That(value![0]).IsEqualTo("updated");
    }

    // ITM-558 下沉：同 key 不同类型按 miss 处理并移除旧条目——不抛 InvalidCastException
    [Test]
    public async Task TryGet_TypeMismatch_TreatedAsMissAndEvicts()
    {
        var cache = new BoundedQueryCache();
        cache.Set("shared-key", new List<string> { "a" });

        bool hit = cache.TryGet("shared-key", out List<int>? wrongType);

        await Assert.That(hit).IsFalse();
        await Assert.That(wrongType).IsNull();
        // 旧条目已被移除：原类型再读也 miss（后写者胜语义）
        bool originalHit = cache.TryGet("shared-key", out List<string>? original);
        await Assert.That(originalHit).IsFalse();
        await Assert.That(original).IsNull();
    }
}
