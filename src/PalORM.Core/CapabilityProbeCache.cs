namespace PalORM;

/// <summary>字符串键控的能力探测缓存（EPSL 2026-10-11 审计 B 批 #2 底座）——local_infile
/// 能力探测（MySqlProvider）与 MySQL 版本形态探测（<c>ResolveMySqlUpdateFormAsync</c>）共用。
/// <para><b>为什么键是连接串而不是连接实例</b>：探测结果是「同一连接串所指服务端 + 账号」的
/// 事实（服务端变量/版本），不是单个连接包装实例的事实——每操作自建会话形态
/// （<c>DataSession.CreateAsync</c> per op）下每次都是新包装实例，按实例键控的
/// ConditionalWeakTable 永不命中，每次批量操作重付一次探测往返（local_infile 的
/// SHOW VARIABLES 跨网段 0.3~2ms，短事务场景可占总延迟三成，量测见 MySqlProvider L1）。
/// 键取改写后连接串（<c>CreateConnection</c> 的完整输出，与
/// RewrittenConnectionStringCache 的键控先例同族语义）。</para>
/// <para><b>TTL 与失效</b>：TTL 由调用方传入（既有两处均 60s——服务端变量可运行时变更，
/// 短 TTL 让配置刷新在一分钟内生效）；<see cref="Invalidate"/> 供失败后强制重探测路径
/// （ITM-892 族）。探测委托抛异常 = 本次不写缓存、异常原样上抛（探测故障不固化为能力
/// 结论，调用方自行降级）。键空间 = 连接串变体数（应用内通常个位数）；过期条目残留至
/// 同键覆盖（与 RewrittenConnectionStringCache 的无淘汰口径一致，不做持锁清理）。</para>
/// <para><b>泛型静态类按 T 分字典</b>：两类探测共用底座而不互撞（local_infile 与版本形态
/// 的键同为连接串），且与泛型会话的闭合类型绝缘（MySqlUpdateFormCacheHolder 注释的
/// S2743 同因）。internal：消费方为 Core 与 Provider 程序集探测路径（IVT 见 csproj）。</para></summary>
internal static class CapabilityProbeCache<T>
    where T : class
{
    private sealed record ProbeEntry(T Value, long ExpiresAtTicks);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S2743:StaticFieldInGenericType",
        Justification = "按 T 分字典正是本底座的设计语义（local_infile 与版本形态两类探测互不共享、"
        + "互不互撞，类文档第三段）；消费面封闭于本程序集与 Provider 程序集的两处探测点，"
        + "不存在需要跨闭合类型共享字典的场景。")]
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProbeEntry> Cache = new();

    /// <summary>命中未过期条目直接返回；否则执行 <paramref name="probe"/> 并写入缓存后返回。
    /// 并发同键未命中时可能各探一次（幂等探测，与原 ConditionalWeakTable.AddOrUpdate 同
    /// 竞态面），最后写入者胜。</summary>
    public static async ValueTask<T> GetOrProbeAsync(
        string key,
        int ttlMilliseconds,
        Func<CancellationToken, ValueTask<T>> probe,
        CancellationToken ct)
    {
        if (Cache.TryGetValue(key, out ProbeEntry? cached)
            && Environment.TickCount64 < cached.ExpiresAtTicks)
        {
            return cached.Value;
        }

        T value = await probe(ct).ConfigureAwait(false);
        Cache[key] = new ProbeEntry(value, Environment.TickCount64 + ttlMilliseconds);
        NegativeUntil.TryRemove(key, out _);   // R3：探测成功清除负结果标记
        return value;
    }

    /// <summary>强制失效单键（ITM-892 族：TTL 窗口内服务端状态变化，失败后重探测）。</summary>
    public static void Invalidate(string key) => Cache.TryRemove(key, out _);

    /// <summary>R3（2026-10-12）：负结果短 TTL 标记表——探测持续失败形态（受限账号/网络分区）
    /// 下，原每次调用重付探测往返 + 超时（local_infile 探测 CommandTimeout=3s 是最坏延迟洞）。
    /// 窗口内调用方经 <see cref="IsNegativeFresh"/> 直接走降级路径；探测成功时由
    /// <see cref="GetOrProbeAsync"/> 自动清除标记。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S2743:StaticFieldInGenericType",
        Justification = "与 Cache 字段同因：按 T 分表是设计语义（类文档第三段）。")]
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> NegativeUntil = new();

    /// <summary>标记探测失败（负结果窗口开始）。</summary>
    public static void MarkNegative(string key, int negativeTtlMilliseconds)
        => NegativeUntil[key] = Environment.TickCount64 + negativeTtlMilliseconds;

    /// <summary>负结果窗口是否仍在生效（窗口内调用方应跳过探测直接降级）。</summary>
    public static bool IsNegativeFresh(string key)
        => NegativeUntil.TryGetValue(key, out long until) && Environment.TickCount64 < until;
}
