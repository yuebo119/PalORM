using System.Collections.Concurrent;

namespace PalORM;

/// <summary>查询缓存抽象（ADR-C）。经 <see cref="DbOptions.QueryCache"/> 注入到会话，
/// 替代进程级静态状态；未注入时各会话使用进程级共享的 <see cref="BoundedQueryCache"/> 默认实例。</summary>
public interface IQueryCache
{
    /// <summary>尝试获取缓存值。实现应在读取时清理过期条目。</summary>
    bool TryGet<T>(string key, out T? value) where T : class;

    /// <summary>设置缓存值。</summary>
    void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class;

    /// <summary>清除所有缓存。</summary>
    void Clear();
}

/// <summary>默认查询缓存——ConcurrentDictionary + TTL + 容量上限（默认 1024 条）。
/// 超出容量时先剔除全部过期条目；仍满则拒绝新条目（缓存未命中是正确性中性的，
/// 拒绝写入优于无界增长或引入 LRU 锁开销）。
/// <para><b>软上限</b>：容量检查与写入非原子（check-then-act），并发 Set 可短暂超出上限
/// （幅度 ≤ 并发写入者数，有界且随 TTL 回落）——刻意不加锁换取写入路径无阻塞。
/// 另注：ConcurrentDictionary.Count 为全分段锁操作，每次 Set 付一次；查询缓存写频率低可接受，
/// 若未来成为热点可改 Interlocked 近似计数。</para></summary>
public sealed class BoundedQueryCache : IQueryCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly int _maxEntries;

    /// <summary>创建有界缓存。</summary>
    /// <param name="maxEntries">容量上限（默认 1024 条）。</param>
    public BoundedQueryCache(int maxEntries = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        _maxEntries = maxEntries;
    }

    /// <inheritdoc />
    public bool TryGet<T>(string key, out T? value) where T : class
    {
        if (_cache.TryGetValue(key, out CacheEntry? entry))
        {
            // S1066: 内层 if 不能与外层合并——TryRemove 必须在"条目存在但过期/类型不符"时执行，
            // 合并到外层条件会跳过 TryRemove。
#pragma warning disable S1066
            if (!entry.IsExpired())
            {
                // ITM-558：两个实体类型误用同一 WithCache key 时，硬强转会抛不含 key 的
                // InvalidCastException。类型不匹配按 miss 处理并移除旧条目——后写者胜，
                // 与"缓存未命中是正确性中性的"设计一致。
                if (entry.Value is T typed)
                {
                    value = typed;
                    return true;
                }
            }
#pragma warning restore S1066
            _cache.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
        }
        value = default;
        return false;
    }

    /// <inheritdoc />
    public void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class
    {
        if (_cache.Count >= _maxEntries && !_cache.ContainsKey(key))
        {
            EvictExpired();
            if (_cache.Count >= _maxEntries) return;
        }
        _cache[key] = new CacheEntry(value, ttl ?? TimeSpan.FromMinutes(5));
    }

    /// <inheritdoc />
    public void Clear() => _cache.Clear();

    private void EvictExpired()
    {
        foreach (var pair in _cache)
        {
            if (pair.Value.IsExpired())
                _cache.TryRemove(pair);
        }
    }

    private sealed class CacheEntry(object value, TimeSpan ttl)
    {
        public object Value { get; } = value;
        // ITM-586：与 Resilience（ITM-538）同款已知取舍——UtcNow 墙钟受 NTP 回拨影响，
        // 回拨仅延长条目存活（正确性中性：缓存多活≠错误数据，写路径会覆盖）。
        // 换 Environment.TickCount64 需处理 49.7 天回绕，不值得为缓存 TTL 引入。
        private DateTime ExpiresAt { get; } = DateTime.UtcNow.Add(ttl);
        public bool IsExpired() => DateTime.UtcNow > ExpiresAt;
    }
}

/// <summary>进程级静态缓存外观——兼容既有 <c>CacheStore.Clear()</c> 调用方。
/// 内部委托给默认 <see cref="BoundedQueryCache"/> 实例；新代码请使用
/// <see cref="DbOptions.QueryCache"/> 注入会话级缓存（ADR-C）。</summary>
public static class CacheStore
{
    /// <summary>未注入 QueryCache 的会话共享的默认实例（容量 1024）。</summary>
    internal static BoundedQueryCache Default { get; } = new();

    /// <summary>尝试获取缓存值。过期条目在读取时移除，避免无界驻留。</summary>
    public static bool TryGet<T>(string key, out T? value) where T : class
        => Default.TryGet(key, out value);

    /// <summary>设置缓存值。</summary>
    public static void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class
        => Default.Set(key, value, ttl);

    /// <summary>清除所有缓存。</summary>
    public static void Clear() => Default.Clear();
}
