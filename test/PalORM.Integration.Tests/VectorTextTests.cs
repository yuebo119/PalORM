using PalORM.PostgreSql;

namespace PalORM.Integration.Tests;

/// <summary>VectorText 纯逻辑单测（ADR 向量门 Raw 路线配套设施，无真库依赖）。
/// 端到端行为（::vector 写入/::text 回读/KNN）见 docs/向量搜索Raw配方.md §五核对清单，
/// 由 scripts/probe-pgvector.cs 在具备 pgvector 的库上执行。</summary>
public sealed class VectorTextTests
{
    [Test]
    public async Task Of_ProducesPgVectorLiteral()
    {
        string text = VectorText.Of([0.1f, 0.2f, 0.3f], expectedDim: 3);
        // InvariantCulture 恒定小数点；R 格式往返精度
        await Assert.That(text).IsEqualTo("[0.1,0.2,0.3]");
    }

    [Test]
    public async Task Of_WrongDimension_ThrowsWithBothNumbers()
    {
        await Assert.That(() => VectorText.Of([1f, 2f], expectedDim: 3))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Of_ZeroDimension_Throws()
    {
        await Assert.That(() => VectorText.Of([], expectedDim: 0))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Of_NegativeValuesAndExponents_RoundTrip()
    {
        string text = VectorText.Of([-1.5f, 1e-8f, 123456f], expectedDim: 3);
        // R 格式允许指数形态（1E-08）；pgvector 输入函数接受科学计数法——回读验证
        bool ok = VectorText.TryParse(text, expectedDim: 3, out float[] values);
        await Assert.That(ok).IsTrue();
        await Assert.That(values[0]).IsEqualTo(-1.5f);
        await Assert.That(values[1]).IsEqualTo(1e-8f);
        await Assert.That(values[2]).IsEqualTo(123456f);
    }

    [Test]
    public async Task TryParse_RejectsMalformedForms()
    {
        await Assert.That(VectorText.TryParse("0.1,0.2", 2, out _)).IsFalse();      // 缺括号
        await Assert.That(VectorText.TryParse("[0.1,0.2,x]", 3, out _)).IsFalse();  // 非数值
        await Assert.That(VectorText.TryParse("[0.1,0.2]", 3, out _)).IsFalse();    // 维度不符
        await Assert.That(VectorText.TryParse("[]", 0, out _)).IsFalse();           // 非法维度
        await Assert.That(VectorText.TryParse("[,]", 2, out _)).IsFalse();          // 空分量
    }

    [Test]
    public async Task Of_TryParse_RoundTripsLongVector()
    {
        float[] source = [.. Enumerable.Range(0, 1536).Select(i => i * 0.001f)];
        string text = VectorText.Of(source, expectedDim: 1536);

        bool ok = VectorText.TryParse(text, expectedDim: 1536, out float[] parsed);

        await Assert.That(ok).IsTrue();
        await Assert.That(parsed.Length).IsEqualTo(1536);
        await Assert.That(parsed[1535]).IsEqualTo(source[1535]);
    }
}
