using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

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
/// 若未来成为热点可改 Interlocked 近似计数。</para>
/// <para><b>OpenTelemetry 指标</b>（对齐 .NET 11 MemoryCache 标准口径，ITM-P3A）：
/// 复用 <see cref="PalORMMetrics.Meter"/> 暴露 5 个指标（无 cache key 标签——ITM-539 教训）：
/// <c>palorm.cache.requests{outcome=hit|miss}</c>、<c>palorm.cache.evictions</c>、
/// <c>palorm.cache.entries</c>（ObservableGauge）、<c>palorm.cache.estimated_size</c>（ObservableGauge，未实现精确字节估算故恒为 Count）。
/// 通过 <c>PalORMMetrics.Meter</c> 上游 OTLP 导出即可观测。</para></summary>
public sealed class BoundedQueryCache : IQueryCache
{
    private static readonly Counter<long> _requests = PalORMMetrics.Meter.CreateCounter<long>(
        "palorm.cache.requests", description: "Cache lookup requests (outcome=hit|miss)");
    private static readonly Counter<long> _evictions = PalORMMetrics.Meter.CreateCounter<long>(
        "palorm.cache.evictions", description: "Cache entries evicted (expired or type mismatch)");

    // v4.4：预构造 hit/miss KVP，消除每次 TryGet 的 KeyValuePair 构造开销
    private static readonly KeyValuePair<string, object?> HitTag = new("outcome", "hit");
    private static readonly KeyValuePair<string, object?> MissTag = new("outcome", "miss");

    // ObservableGauge 在 Pull 模式下被 OTel 主动轮询——缓存读取路径零开销（不每次 Set/TryGet 计数）。
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly int _maxEntries;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    // 评审 2026-09-02 结构化：gauge 注册从"每实例"改为"进程级单次 + 弱引用实例表"。
    // 原实现每实例在静态 Meter 上注册 2 个 ObservableGauge 且回调闭包持有缓存字典——
    // Meter 永久存活导致 instrument 与字典均不可 GC（review R8 登记的泄漏，靠
    // "推荐复用单例"缓解）。现在所有实例进入弱引用表，gauge 回调逐个读取 Count
    // 并顺带剪枝死引用——实例可正常 GC，指标名与 instance 标签形状保持不变。
    private static readonly Lock _gaugeRegistryLock = new();
    private static readonly List<WeakReference<BoundedQueryCache>> _liveInstances = [];
    private static bool _gaugesRegistered;

    /// <summary>创建有界缓存。
    /// <para><b>生命周期契约（review R8 + 评审 2026-09-02 结构化）</b>：ObservableGauge 进程内
    /// 单次注册（首个实例触发），回调经弱引用表读取全部存活实例并剪枝死引用——
    /// 实例可正常 GC，无 instrument 泄漏。高频创建场景仍推荐复用 <c>CacheStore.Default</c>
    /// 单例（避免 instance 标签基数随实例增长）。</para></summary>
    /// <param name="maxEntries">容量上限（默认 1024 条）。</param>
    public BoundedQueryCache(int maxEntries = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        _maxEntries = maxEntries;
        RegisterGaugesAndTrackInstance(this);
    }

    private static void RegisterGaugesAndTrackInstance(BoundedQueryCache instance)
    {
        lock (_gaugeRegistryLock)
        {
            _liveInstances.RemoveAll(static weakRef => !weakRef.TryGetTarget(out _));
            _liveInstances.Add(new WeakReference<BoundedQueryCache>(instance));
            if (_gaugesRegistered) return;
            _gaugesRegistered = true;
            PalORMMetrics.Meter.CreateObservableGauge(
                "palorm.cache.entries",
                CollectCacheMeasurements,
                unit: "{entries}",
                description: "Current number of cache entries");
            PalORMMetrics.Meter.CreateObservableGauge(
                "palorm.cache.estimated_size",
                CollectCacheMeasurements,
                unit: "{entries}",
                description: "Estimated cache size (approximated as entry count; no per-entry byte accounting)");
        }
    }

    private static List<Measurement<int>> CollectCacheMeasurements()
    {
        // 先在锁内物化快照再返回——Lock.Scope 不得跨 yield 边界（CS4007）。
        var measurements = new List<Measurement<int>>();
        lock (_gaugeRegistryLock)
        {
            for (int i = _liveInstances.Count - 1; i >= 0; i--)
            {
                if (!_liveInstances[i].TryGetTarget(out BoundedQueryCache? instance))
                {
                    _liveInstances.RemoveAt(i);
                    continue;
                }
                measurements.Add(new Measurement<int>(
                    instance._cache.Count,
                    new KeyValuePair<string, object?>("instance", instance._instanceId)));
            }
        }
        return measurements;
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
                    _requests.Add(1, HitTag);
                    value = typed;
                    return true;
                }
                _evictions.Add(1);  // 类型不匹配淘汰
            }
#pragma warning restore S1066
            else
            {
                _evictions.Add(1);  // 过期淘汰
            }
            _cache.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
        }
        _requests.Add(1, MissTag);
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
        // 物化到 List 后再 TryRemove——避免迭代中修改 ConcurrentDictionary 引发枚举器失效。
        var expired = _cache.Where(static p => p.Value.IsExpired()).ToList();
        if (expired.Count > 0)
        {
            _evictions.Add(expired.Count);
            foreach (var pair in expired)
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
