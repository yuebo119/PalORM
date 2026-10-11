namespace PalORM.Core.Tests;

/// <summary><see cref="CapabilityProbeCache{T}"/>（EPSL 2026-10-11 审计 B 批 #2 底座）的
/// 缓存语义单测：同键命中 / 异键各探 / TTL 过期重探 / 探测失败不缓存 / 强制失效。
/// <para><b>与端到端证据的分工</b>：本套件锁定缓存底座语义；「per-op 会话探测次数 40→1」
/// 的连接级红绿证据由审计期一次性探针承载（SHOW GLOBAL STATUS Com_show_variables 计数，
/// 见审计报告）——进程级计数器断言受 B57/B74 并行污染禁令约束不入库（Integration 套件
/// 并行执行 MySQL 批量测试会污染全局计数）。</para>
/// <para>静态缓存跨测试共享：每用例用 Guid 键隔离；Ttl=0 的过期判定确定性成立
/// （<c>now &lt; now + 0</c> 恒假），无需注入时钟。</para></summary>
internal sealed class CapabilityProbeCacheTests
{
    private sealed record ProbeValue(int N);

    private static string NewKey() => $"probe-cache-test/{Guid.NewGuid():N}";

    [Test]
    public async Task SameKey_SecondCall_HitsCache_WithoutReprobe()
    {
        string key = NewKey();
        int probes = 0;
        ValueTask<ProbeValue> Probe(CancellationToken _)
        {
            probes++;
            return ValueTask.FromResult(new ProbeValue(probes));
        }

        ProbeValue first = await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(key, 60_000, Probe, default);
        ProbeValue second = await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(key, 60_000, Probe, default);

        await Assert.That(probes).IsEqualTo(1);
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task DifferentKeys_EachProbes()
    {
        int probes = 0;
        ValueTask<ProbeValue> Probe(CancellationToken _)
        {
            probes++;
            return ValueTask.FromResult(new ProbeValue(probes));
        }

        _ = await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(NewKey(), 60_000, Probe, default);
        _ = await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(NewKey(), 60_000, Probe, default);

        await Assert.That(probes).IsEqualTo(2);
    }

    [Test]
    public async Task ZeroTtl_EntryImmediatelyExpires_EveryCallProbes()
    {
        string key = NewKey();
        int probes = 0;
        ValueTask<ProbeValue> Probe(CancellationToken _)
        {
            probes++;
            return ValueTask.FromResult(new ProbeValue(probes));
        }

        _ = await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(key, 0, Probe, default);
        _ = await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(key, 0, Probe, default);

        await Assert.That(probes).IsEqualTo(2);
    }

    [Test]
    public async Task ProbeThrows_NotCached_NextCallProbesAgain()
    {
        string key = NewKey();
        int probes = 0;
        ValueTask<ProbeValue> Probe(CancellationToken _)
        {
            probes++;
            return probes == 1
                ? ValueTask.FromException<ProbeValue>(new InvalidOperationException("probe failed"))
                : ValueTask.FromResult(new ProbeValue(probes));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(key, 60_000, Probe, default));
        ProbeValue recovered = await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(key, 60_000, Probe, default);

        await Assert.That(probes).IsEqualTo(2);  // 失败结果未固化为缓存
        await Assert.That(recovered.N).IsEqualTo(2);
    }

    [Test]
    public async Task Invalidate_ForcesReprobe_OnNextCall()
    {
        string key = NewKey();
        int probes = 0;
        ValueTask<ProbeValue> Probe(CancellationToken _)
        {
            probes++;
            return ValueTask.FromResult(new ProbeValue(probes));
        }

        _ = await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(key, 60_000, Probe, default);
        CapabilityProbeCache<ProbeValue>.Invalidate(key);
        ProbeValue reprobed = await CapabilityProbeCache<ProbeValue>.GetOrProbeAsync(key, 60_000, Probe, default);

        await Assert.That(probes).IsEqualTo(2);
        await Assert.That(reprobed.N).IsEqualTo(2);
    }
}
