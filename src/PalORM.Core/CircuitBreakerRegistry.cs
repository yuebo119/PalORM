using System.Collections.Concurrent;

namespace PalORM;

/// <summary>进程级熔断器登记表（RES-002，2026-09-23）——<see cref="CircuitBreakerScope.Process"/>
/// 下按 (Provider, 连接串指纹, 阈值, 冷却) 共享同一熔断器实例。
/// <para><b>为什么需要</b>：会话级熔断器的失败计数随会话销毁，"一个请求一个会话"的推荐用法下
/// 默认阈值 5 几乎不可能触发（数据库真挂时每个请求各自重试再失败），熔断器形同虚设。</para>
/// <para><b>键含配置</b>：阈值/冷却不同视为不同熔断器——否则 WithCircuitBreaker 改配置后会复用
/// 旧阈值的实例，配置静默失效。旧配置实例留在表中，键空间受配置组合数约束。</para>
/// <para><b>敏感信息</b>：P3-8 起键只存连接串的 64 位指纹（FNV-1a），不再驻留连接串原文；
/// 指纹不写日志、不进异常消息、不参与 ToString 输出。相等语义与原字符串键等价到 2^-64
/// 碰撞概率（碰撞仅表现为两个不同连接串共享熔断状态）。</para>
/// <para><b>容量</b>：P3-8 起有界（<see cref="MaxEntries"/>）——多租户按会话连接串轮换时
/// 原静态字典永不淘汰；满员后插入新键前淘汰一个任意条目。被淘汰的熔断器若仍被在飞执行器
/// 引用则继续工作，但后续会话会创建新实例（开闸状态重置 = 故障期短暂放行），这是有界
/// 内存换取的已知取舍；键空间正常情况下远小于上限。</para></summary>
internal static class CircuitBreakerRegistry
{
    private const int MaxEntries = 4096;

    private static readonly ConcurrentDictionary<Key, CircuitBreaker> Breakers = new();

    internal static CircuitBreaker GetOrAdd(Type providerType, DbOptions options)
    {
        var key = new Key(
            providerType,
            Fingerprint(options.ResolveConnectionString()),
            options.CircuitBreakerThreshold,
            options.CircuitBreakerResetAfter);
        if (Breakers.TryGetValue(key, out CircuitBreaker? existing))
            return existing;
        // 有界：只在新键路径上做容量检查（命中路径零额外开销）。并发下可能短暂超限，
        // 下一次插入继续修剪，上界是近似值。
        if (Breakers.Count >= MaxEntries)
        {
            using System.Collections.Generic.IEnumerator<Key> keys = Breakers.Keys.GetEnumerator();
            if (keys.MoveNext())
                Breakers.TryRemove(keys.Current, out _);
        }
        return Breakers.GetOrAdd(
            key,
            static k => new CircuitBreaker(k.Threshold, k.ResetAfter));
    }

    /// <summary>连接串指纹（FNV-1a 64）——确定性、零分配，进程内外一致。
    /// 不作为安全哈希使用（连接串本身仍是凭据，指纹不可逆更不用于验证）。</summary>
    private static ulong Fingerprint(string connectionString)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in connectionString)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private readonly record struct Key(
        Type Provider,
        ulong ConnectionStringFingerprint,
        int Threshold,
        TimeSpan ResetAfter)
    {
        // ITM-816（r23 实修）：显式覆写排除键载荷——编译器合成 ToString 输出全部字段，
        // 连接串明文（含凭据）会进日志/异常消息/diagnostic dump，与类注释"不参与 ToString
        // 输出"的承诺相反。相等语义不受影响（仍按全部字段）。
        public override string ToString()
            => $"{Provider.Name}, Threshold={Threshold}, ResetAfter={ResetAfter}";
    }
}
