using System.Collections.Concurrent;

namespace PalORM;

/// <summary>非链接形态 CancellationTokenSource 的进程级租借池(R3-CTS,2026-10-13 用户点名轮)。
/// <para><b>为什么可以池化</b>:.NET 6+ 的 <see cref="CancellationTokenSource.TryReset"/> 官方
/// 文档明示其设计动机正是热路径上每操作新建 CTS 的分配消除(ASP.NET Core 请求管道场景);
/// 未被取消的实例 TryReset 成功后语义与新建逐位一致(未取消状态 + 可重新 CancelAfter)。
/// 已取消(超时触发/调用方取消联动)时 TryReset 返回 false——按不可复用丢弃,语义零变化。</para>
/// <para><b>只池化非链接形态</b>:<c>CreateLinkedTokenSource</c> 在源 token 上挂注册,池化复用
/// 会让跨次注册累积(泄漏 + 误触发),链接形态恒走新建+Dispose。</para>
/// <para><b>下游注册安全性</b>:租出的 token 只在单次尝试的 await 生命周期内传递给操作与驱动;
/// 驱动(Npgsql 等)在执行完成前注销自己的取消注册,Return 时 token 上无存活注册。</para>
/// <para><b>上限</b>:空闲上限 64(并发同时in-flight的弹性尝试数远低于此;超出直接丢弃,
/// 池自然回落)。<see cref="ConcurrentQueue{T}"/> 出入队并发安全,无需锁。</para>
/// <para><b>与 step10 PoC 的关系</b>:step10 证伪的是"移除 CTS 依赖驱动超时"(丢失连接获取+读取
/// 阶段的取消覆盖与 InfrastructureTimeout 标记);本池化保持超时语义逐位不变,只消除分配与构造
/// 成本——是不同干预。台账依据:默认弹性配置下每读 168B 的 CTS+定时器是 per-op 分配的
/// 最大单一成分(文档化 272B 包装中的主项)。</para></summary>
internal static class PooledCts
{
    private const int MaxIdle = 64;
    private static readonly ConcurrentQueue<CancellationTokenSource> Idle = new();

    /// <summary>租一个未取消的 CTS——优先复用,池空时新建。</summary>
    internal static CancellationTokenSource Rent()
        => Idle.TryDequeue(out CancellationTokenSource? reused) ? reused : new CancellationTokenSource();

    /// <summary>归还:未取消且 TryReset 成功则入池,否则 Dispose(已取消的不可复用)。
    /// 池满亦 Dispose(空闲上限纪律,不放大驻留)。
    /// <para><b>双检硬化</b>:TryReset 与迟发 CancelAfter 回调存在文档化竞态(极罕见时
    /// TryReset 返回 true 但取消已在途)——TryReset 后二次 IsCancellationRequested 复核,
    /// 命中即丢弃;此时 TryReset 已解除定时器,复核之后不可能再出现新的取消来源。</para></summary>
    internal static void Return(CancellationTokenSource cts)
    {
        if (!cts.IsCancellationRequested
            && cts.TryReset()
            && !cts.IsCancellationRequested
            && Idle.Count < MaxIdle)
            Idle.Enqueue(cts);
        else
            cts.Dispose();
    }
}
