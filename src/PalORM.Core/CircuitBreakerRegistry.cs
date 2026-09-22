using System.Collections.Concurrent;

namespace PalORM;

/// <summary>进程级熔断器登记表（RES-002，2026-09-23）——<see cref="CircuitBreakerScope.Process"/>
/// 下按 (Provider, 连接串, 阈值, 冷却) 共享同一熔断器实例。
/// <para><b>为什么需要</b>：会话级熔断器的失败计数随会话销毁，"一个请求一个会话"的推荐用法下
/// 默认阈值 5 几乎不可能触发（数据库真挂时每个请求各自重试再失败），熔断器形同虚设。</para>
/// <para><b>键含配置</b>：阈值/冷却不同视为不同熔断器——否则 WithCircuitBreaker 改配置后会复用
/// 旧阈值的实例，配置静默失效。旧配置实例留在表中，键空间受配置组合数约束。</para>
/// <para><b>敏感信息</b>：连接串仅作字典键参与相等比较，不写日志、不进异常消息、不参与
/// ToString 输出。</para></summary>
internal static class CircuitBreakerRegistry
{
    private static readonly ConcurrentDictionary<Key, CircuitBreaker> Breakers = new();

    internal static CircuitBreaker GetOrAdd(Type providerType, DbOptions options)
        => Breakers.GetOrAdd(
            new Key(
                providerType,
                options.ResolveConnectionString(),
                options.CircuitBreakerThreshold,
                options.CircuitBreakerResetAfter),
            static key => new CircuitBreaker(key.Threshold, key.ResetAfter));

    private readonly record struct Key(
        Type Provider,
        string ConnectionString,
        int Threshold,
        TimeSpan ResetAfter);
}
