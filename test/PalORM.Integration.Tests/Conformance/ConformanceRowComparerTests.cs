namespace PalORM.Integration.Tests.Conformance;

/// <summary>规范化比较器的反例测试（方案 §4.7）。
/// <para>每条放宽规则都必须有一个"保持报红"的反例：反例不红说明规则过宽，会把真缺陷洗成相等。
/// 这是 L0 元判据的一部分，改比较器必须同步改这里。</para></summary>
public sealed class ConformanceRowComparerTests
{
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object? value) in cells) row[key] = value;
        return row;
    }

    private static Dictionary<string, object?>[] Rows(
        params Dictionary<string, object?>[] rows) => rows;

    [Test]
    public async Task RowOrder_IsIgnored()
    {
        var expected = Rows(Row(("id", 1L)), Row(("id", 2L)));
        var actual = Rows(Row(("id", 2L)), Row(("id", 1L)));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ColumnOrder_IsIgnored()
    {
        var expected = Rows(Row(("id", 1L), ("name", "a")));
        var actual = Rows(Row(("name", "a"), ("id", 1L)));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(0);
    }

    [Test]
    public async Task RowCountMismatch_IsReported()
    {
        var expected = Rows(Row(("id", 1L)));
        var actual = Rows(Row(("id", 1L)), Row(("id", 2L)));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsGreaterThan(0);
    }

    [Test]
    public async Task SameColumnDifferentValue_IsReported()
    {
        // 列序放宽不能吞掉值错位：RowFactory 序号错会让同一列名下出现错值
        var expected = Rows(Row(("id", 1L), ("name", "expected")));
        var actual = Rows(Row(("id", 1L), ("name", "actual")));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(1);
    }

    [Test]
    public async Task DecimalValueMismatch_IsReported()
    {
        var expected = Rows(Row(("amount", 12.30m)));
        var actual = Rows(Row(("amount", 12.31m)));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(1);
    }

    [Test]
    public async Task DateTimeOffsetDifference_IsReported()
    {
        // 同一时钟读数、不同偏移：实际时刻不同，必须报
        var expected = Rows(Row(("at", new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc))));
        var actual = Rows(Row(("at", new DateTime(2026, 7, 18, 18, 30, 0, DateTimeKind.Utc))));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(1);
    }

    [Test]
    public async Task DateTimeKindDifference_IsReported()
    {
        // 同一读数、Kind 被静默改写：统一转 UTC 会洗掉这类缺陷
        var expected = Rows(Row(("at", new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc))));
        var actual = Rows(Row(("at", new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Unspecified))));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(1);
    }

    [Test]
    public async Task DoubleBitDifference_IsReported()
    {
        // 相差 1 ULP：容差比较会放过
        var expected = Rows(Row(("value", 0.1)));
        var actual = Rows(Row(("value", Math.BitIncrement(0.1))));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NaN_VersusNaN_IsNotEqual()
    {
        var expected = Rows(Row(("value", double.NaN)));
        var actual = Rows(Row(("value", double.NaN)));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(1);
    }

    [Test]
    public async Task ByteArrayMismatch_IsReported()
    {
        var expected = Rows(Row(("payload", new byte[] { 1, 2, 3 })));
        var actual = Rows(Row(("payload", new byte[] { 1, 2, 4 })));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NullVersusEmptyString_IsReported()
    {
        var expected = Rows(Row(("name", null)));
        var actual = Rows(Row(("name", "")));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(1);
    }

    [Test]
    public async Task CaseDifference_IsReported()
    {
        // 大小写敏感列上的差异不得折叠
        var expected = Rows(Row(("name", "Alpha")));
        var actual = Rows(Row(("name", "alpha")));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(1);
    }

    [Test]
    public async Task DbNull_AndNull_AreEqual()
    {
        // 裸 ADO 侧未归一化时 DBNull 与 null 表示同一个 SQL NULL
        var expected = Rows(Row(("name", DBNull.Value)));
        var actual = Rows(Row(("name", null)));

        await Assert.That(RowComparer.Compare(expected, actual).Count).IsEqualTo(0);
    }
}