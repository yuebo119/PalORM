namespace PalORM.Core.Tests;

/// <summary>时间边界注入（2026-10-31）：缓存 TTL、熔断冷却、探测负窗口用假时钟确定性验证。</summary>
[NotInParallel]
public sealed class PalORMClockTests
{
    [Test]
    public async Task BoundedQueryCache_TtlExpires_WithInjectedClock()
    {
        long now = 1_000;
        using var clock = PalORMClock.OverrideForTests(() => now);
        var cache = new BoundedQueryCache(8);
        cache.Set("k", new object(), TimeSpan.FromSeconds(1));

        await Assert.That(cache.TryGet<object>("k", out _)).IsTrue();

        now += 1_001;
        await Assert.That(cache.TryGet<object>("k", out _)).IsFalse();
    }

    [Test]
    public async Task CircuitBreaker_CooldownExpires_WithInjectedClock()
    {
        long now = 10_000;
        using var clock = PalORMClock.OverrideForTests(() => now);
        var breaker = new CircuitBreaker(1, TimeSpan.FromSeconds(10));
        (bool probe, long generation, long token) = breaker.Enter();
        await Assert.That(probe).IsFalse();

        breaker.RecordFinalFailure(
            isHalfOpenProbe: false, countsTowardCircuit: true, generation, token);
        await Assert.That(() => breaker.Enter()).Throws<CircuitBreakerOpenException>();

        now += 10_001;
        (bool halfOpen, _, _) = breaker.Enter();
        await Assert.That(halfOpen).IsTrue();
    }

    [Test]
    public async Task CapabilityProbeCache_NegativeWindowExpires_WithInjectedClock()
    {
        long now = 20_000;
        using var clock = PalORMClock.OverrideForTests(() => now);
        string key = $"clock-{Guid.NewGuid():N}";
        _ = new ClockProbe();

        CapabilityProbeCache<ClockProbe>.MarkNegative(key, 10_000);
        await Assert.That(CapabilityProbeCache<ClockProbe>.IsNegativeFresh(key)).IsTrue();

        now += 10_001;
        await Assert.That(CapabilityProbeCache<ClockProbe>.IsNegativeFresh(key)).IsFalse();
    }

    private sealed class ClockProbe;
}
