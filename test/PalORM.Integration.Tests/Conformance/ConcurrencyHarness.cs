using System.Collections.Concurrent;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>高并发压测共用夹具（修订版方案 §4 基建 1）。
/// <para><b>职责边界</b>：只提供编排、种子与进程级状态复位，不写产品断言。硬超时是防挂死兜底，
/// 不是判据（.ai/test/prompt.md T15 禁止实际耗时类断言）；命中超时只记 violation，
/// 用例仍须用具体值对账证明数据正确。</para>
/// <para><b>进程级状态复位与其代价</b>：<c>CacheStore.Clear()</c> 是产品公开入口，清的是
/// <b>进程级</b>默认查询缓存。产品侧对静态缓存有「非线程安全、仅测试单线程调用」的纪律，
/// 故<b>凡调用本夹具的测试类必须标 <c>[NotInParallel]</c></b>，否则会与并行用例的缓存命中时序互踩
/// （独立验收 2026-11-07 指出：本条不能声称「从构造上消除共享」）。
/// 静态 SQL 槽与注册表代际的清理入口是 internal，Core 的 IVT 只授权 PalORM.Core.Tests，
/// 不为测试改产品配置；涉缓存的用例改用 <c>DbOptions.QueryCache</c> 注入会话级实例（ADR-C 推荐形态），
/// 让状态由用例自己持有。</para></summary>
internal sealed class ConcurrencyHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _hardTimeout;
    private readonly ConcurrentBag<Violation> _violations = [];

    private ConcurrencyHarness(int seed, TimeSpan hardTimeout, CancellationTokenSource timeoutSource)
    {
        Seed = seed;
        HardTimeout = hardTimeout;
        _hardTimeout = timeoutSource;
        ResetProcessState();
    }

    /// <summary>确定性调度种子。环境变量 <c>PALORM_CONCURRENCY_SEED</c> 可覆盖，失败可单种子重放。</summary>
    internal int Seed { get; }

    /// <summary>硬超时长度（仅记入报告，不参与断言）。</summary>
    internal TimeSpan HardTimeout { get; }

    /// <summary>传给所有 worker 的取消令牌；超时或外部取消都会触发。</summary>
    internal CancellationToken Token => _hardTimeout.Token;

    internal IReadOnlyCollection<Violation> Violations => _violations;

    internal static ConcurrencyHarness Create(TimeSpan? hardTimeout = null)
    {
        TimeSpan timeout = hardTimeout ?? TimeSpan.FromSeconds(30);
        int seed = int.TryParse(
            Environment.GetEnvironmentVariable("PALORM_CONCURRENCY_SEED"), out int configured)
            ? configured
            : 20261107;
        return new ConcurrencyHarness(seed, timeout, new CancellationTokenSource(timeout));
    }

    /// <summary>按种子构造确定性调度器。同一 seed 得到同一交错序列。</summary>
    internal Random NewRandom(int salt = 0) => new(unchecked(Seed + salt));

    /// <summary>并行跑 N 个 worker，全部在同一起跑线放行，避免「先启动的先写完」掩盖交错。
    /// 用 TCS 闸门而非 Barrier：Barrier 是同步阻塞，且需要在全部任务结束后才能释放。
    /// 硬超时命中会记 violation 并取消，用例仍须用具体值对账证明数据正确。</summary>
    internal async Task RunAsync(int workers, Func<int, CancellationToken, Task> body)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] tasks = Enumerable.Range(0, workers)
            .Select(worker => Task.Run(async () =>
            {
                await gate.Task.WaitAsync(Token).ConfigureAwait(false);
                await body(worker, Token).ConfigureAwait(false);
            }, Token))
            .ToArray();
        gate.SetResult();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_hardTimeout.IsCancellationRequested)
        {
            Record(-1, 0, "全部 worker 在硬超时内完成", $"硬超时 {HardTimeout} 命中");
        }
    }

    internal void Record(int worker, int round, string expected, string actual)
        => _violations.Add(new Violation(worker, round, expected, actual));

    /// <summary>进程级状态复位。调用方在用例起止各调一次，保证默认查询缓存无共享残留（T5/T22）。</summary>
    internal static void ResetProcessState() => CacheStore.Clear();

    public ValueTask DisposeAsync()
    {
        ResetProcessState();
        _hardTimeout.Dispose();
        return ValueTask.CompletedTask;
    }

    internal sealed record Violation(int Worker, int Round, string Expected, string Actual);
}
