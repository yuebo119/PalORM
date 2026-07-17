using System.Collections.Concurrent;

namespace PalORM;

/// <summary>查询缓存存储——ConcurrentDictionary + TTL 自动失效。</summary>
public static class CacheStore
{
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    /// <summary>尝试获取缓存值。</summary>
    public static bool TryGet<T>(string key, out T? value) where T : class
    {
        if (_cache.TryGetValue(key, out CacheEntry? entry) && !entry.IsExpired())
        {
            value = (T)entry.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>设置缓存值。</summary>
    public static void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class
    {
        _cache[key] = new CacheEntry(value, ttl ?? TimeSpan.FromMinutes(5));
    }

    /// <summary>清除所有缓存。</summary>
    public static void Clear() => _cache.Clear();

    private sealed class CacheEntry(object value, TimeSpan ttl)
    {
        public object Value { get; } = value;
        private DateTime ExpiresAt { get; } = DateTime.UtcNow.Add(ttl);
        public bool IsExpired() => DateTime.UtcNow > ExpiresAt;
    }
}
