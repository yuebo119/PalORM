using System.Diagnostics;

namespace PalORM.Core.Tests;

/// <summary>D4/D7 归一的语义边界（2026-11-01）。集成用例 E4 覆盖真库路径；这里锁住四条判据：
/// 成功返回也要判耗时、内层超时解包、未越线的异常原样上抛、起点为 0 时判定整体关闭。
/// 背景：E4_MySql 偶发失败（驱动中断返回部分结果，耗时判定落在截止线之前），根因是调用点
/// 把 Begin 放在实参第二位，窗口被进入方法的同步段截短；修复后本组与 E4 一起防回归。</summary>
public sealed class CommandTimeoutNormalizerTests
{
    private static long TimestampAgo(TimeSpan ago)
        => Stopwatch.GetTimestamp() - (long)(ago.TotalSeconds * Stopwatch.Frequency);

    [Test]
    public async Task SuccessWithinDeadline_ReturnsValue()
    {
        long start = Stopwatch.GetTimestamp();
        int value = await CommandTimeoutNormalizer.RunAsync(new ValueTask<int>(42), start, 5);
        await Assert.That(value).IsEqualTo(42);
    }

    [Test]
    public async Task SuccessPastDeadline_ThrowsTimeout()
    {
        long start = TimestampAgo(TimeSpan.FromSeconds(2));
        await Assert.That(async () => await CommandTimeoutNormalizer.RunAsync(new ValueTask<int>(42), start, 1))
            .Throws<TimeoutException>();
    }

    [Test]
    public async Task InnerTimeoutException_IsUnwrapped()
    {
        long start = Stopwatch.GetTimestamp();
        Task<int> wrapped = Task.FromException<int>(
            new InvalidOperationException("outer", new TimeoutException("inner")));
        await Assert.That(async () => await CommandTimeoutNormalizer.RunAsync(wrapped, start, 5))
            .Throws<TimeoutException>();
    }

    [Test]
    public async Task NonTimeoutExceptionBeforeDeadline_PropagatesUnchanged()
    {
        long start = Stopwatch.GetTimestamp();
        Task<int> failed = Task.FromException<int>(new InvalidOperationException("boom"));
        await Assert.That(async () => await CommandTimeoutNormalizer.RunAsync(failed, start, 5))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task NonTimeoutExceptionPastDeadline_IsNormalizedToTimeout()
    {
        long start = TimestampAgo(TimeSpan.FromSeconds(2));
        Task<int> failed = Task.FromException<int>(new InvalidOperationException("driver interrupt"));
        await Assert.That(async () => await CommandTimeoutNormalizer.RunAsync(failed, start, 1))
            .Throws<TimeoutException>();
    }

    /// <summary>驱动提前量容差（2026-11-07 全量实测）：MySqlConnector 在 1 秒超时下于
    /// 0.988~0.993 秒抛 1317，比调用方观测的截止线早 7~12 ms；严格 <c>&gt;=</c> 会把中断
    /// 当成真实 SQL 错误。容差内（约 0.96 s）必须归一时超。</summary>
    [Test]
    public async Task NonTimeoutException_JustBeforeDeadline_WithinTolerance_IsNormalized()
    {
        long start = TimestampAgo(TimeSpan.FromMilliseconds(960));
        Task<int> failed = Task.FromException<int>(new InvalidOperationException("driver interrupt"));
        await Assert.That(async () => await CommandTimeoutNormalizer.RunAsync(failed, start, 1))
            .Throws<TimeoutException>();
    }

    /// <summary>容差外（约 0.90 s，距截止线 100 ms）仍是真实错误语义：原样上抛，
    /// 防止容差把「早于超时返回的 SQL 错误」吞成超时。</summary>
    [Test]
    public async Task NonTimeoutException_ClearlyBeforeDeadline_PropagatesUnchanged()
    {
        long start = TimestampAgo(TimeSpan.FromMilliseconds(900));
        Task<int> failed = Task.FromException<int>(new InvalidOperationException("boom"));
        await Assert.That(async () => await CommandTimeoutNormalizer.RunAsync(failed, start, 1))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ZeroTimestamp_DisablesElapsedCheck()
    {
        Task<int> failed = Task.FromException<int>(new InvalidOperationException("boom"));
        await Assert.That(async () => await CommandTimeoutNormalizer.RunAsync(failed, 0L, 1))
            .Throws<InvalidOperationException>();
    }
}
