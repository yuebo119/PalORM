namespace PalORM.Core.Tests;

/// <summary>F30（2026-10-12 审读修复）：参数名表分块惰性——正确性锁定。
/// 块边界（1023/1024）、跨块（65535）、超上限 fallback、同块内多索引一致性。</summary>
public sealed class ParameterNameCacheChunkTests
{
    [Test]
    public async Task BoundaryIndices_ReturnCorrectNames()
    {
        await Assert.That(ParameterNameCache.GetName(0)).IsEqualTo("@p0");
        await Assert.That(ParameterNameCache.GetName(1)).IsEqualTo("@p1");
        await Assert.That(ParameterNameCache.GetName(1023)).IsEqualTo("@p1023");
        await Assert.That(ParameterNameCache.GetName(1024)).IsEqualTo("@p1024");
        await Assert.That(ParameterNameCache.GetName(1025)).IsEqualTo("@p1025");
        await Assert.That(ParameterNameCache.GetName(65534)).IsEqualTo("@p65534");
        await Assert.That(ParameterNameCache.GetName(65535)).IsEqualTo("@p65535");
    }

    [Test]
    public async Task IndexBeyondLimit_FallsBackToInterpolation()
    {
        await Assert.That(ParameterNameCache.GetName(65536)).IsEqualTo("@p65536");
        await Assert.That(ParameterNameCache.GetName(99999)).IsEqualTo("@p99999");
    }

    [Test]
    public async Task RepeatedAccess_ReturnsSameInstance()
    {
        // 分块惰性下重复取用必须稳定返回同一字符串实例（生成代码把它们当作可引用常量）
        await Assert.That(ReferenceEquals(ParameterNameCache.GetName(5000), ParameterNameCache.GetName(5000))).IsTrue();
        await Assert.That(ReferenceEquals(ParameterNameCache.GetName(0), ParameterNameCache.GetName(0))).IsTrue();
    }
}
