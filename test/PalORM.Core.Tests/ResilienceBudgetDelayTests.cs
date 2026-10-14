using System.Diagnostics;

namespace PalORM.Core.Tests;

/// <summary>A3（2026-10-12 审读修复）：总预算（RES-003）必须约束退避等待本身。
/// <para>到期即停的契约：退避期间总预算到期应立即中断等待，包装为超时抛出，
/// 不得睡满退避后用已取消的 token 再发起一次操作。</para></summary>
public sealed class ResilienceBudgetDelayTests
{
    [Test]
    public async Task ExecuteAsync_OverallDeadline_InterruptsRetryBackoffWait()
    {
        var opts = new DbOptions
        {
            ConnectionString = "dummy",
            CommandTimeout = TimeSpan.FromSeconds(30),
            MaxRetries = 3,
            RetryBackoff = _ => TimeSpan.FromSeconds(3),
            CircuitBreakerThreshold = 0,
            OverallDeadline = TimeSpan.FromMilliseconds(200)
        };
        var executor = new ResilienceExecutor(opts, static _ => true);
        var elapsed = Stopwatch.StartNew();

        Exception? thrown = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await executor.ExecuteAsync<int>(_ => Task.FromException<int>(
                new InvalidOperationException("transient"))));

        await Assert.That(thrown!.Message).Contains("overall deadline");
        await Assert.That((bool)thrown!.Data["PalORM.InfrastructureTimeout"]!).IsTrue();
        await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }
}
