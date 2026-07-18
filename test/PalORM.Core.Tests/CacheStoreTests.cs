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
}
