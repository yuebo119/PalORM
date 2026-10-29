using System.Globalization;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>两侧结果集的规范化比较器。
/// <para><b>判定口径</b>（方案 §4.7）：行序按全列元组稳定排序后忽略，列序按列名排序后忽略，
/// 其余逐值比较。所有放宽都必须配一个保持报红的反例，见 <c>ConformanceRowComparerTests</c>。</para>
/// <para><b>浮点</b>：往返比较要求 bit-exact；NaN 与任何值（含 NaN）判不等。容差只允许出现在
/// Sum/Avg 这类计算聚合上，由调用方另写，不在本比较器内放宽。</para>
/// <para><b>时间</b>：Utc 直接比，Local 转 UTC 后比，Unspecified 只与 Unspecified 比。
/// 方言与列类型带来的 Kind 差异由期望矩阵（方案 §4.7）负责，不在这里归一化，
/// 否则"Kind 被静默改写"这类缺陷会被洗成相等。</para>
/// <para><b>decimal</b>：按数值比较。声明了 Precision/Scale 的列是否丢尾零由方言专项断言负责，
/// 因为比较器拿不到列的精度声明。</para></summary>
internal static class RowComparer
{
    internal sealed record Difference(string Kind, string Column, string Expected, string Actual);

    /// <summary>比较两个结果集，返回差异清单。空清单表示两侧等价。</summary>
    public static IReadOnlyList<Difference> Compare(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> expected,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> actual)
    {
        var differences = new List<Difference>();
        if (expected.Count != actual.Count)
        {
            differences.Add(new Difference(
                "RowCount", "-",
                expected.Count.ToString(CultureInfo.InvariantCulture),
                actual.Count.ToString(CultureInfo.InvariantCulture)));
        }

        string[] columns = CollectColumns(expected, actual);
        List<IReadOnlyDictionary<string, object?>> expectedRows = SortRows(expected, columns);
        List<IReadOnlyDictionary<string, object?>> actualRows = SortRows(actual, columns);

        int paired = Math.Min(expectedRows.Count, actualRows.Count);
        for (int i = 0; i < paired; i++)
            CompareRow(expectedRows[i], actualRows[i], columns, differences);
        return differences;
    }

    /// <summary>列名取并集并按序数排序：列序不参与判定（方案 §4.7 行"列序"）。</summary>
    private static string[] CollectColumns(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> expected,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> actual)
    {
        var columnSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, object?> row in expected)
            foreach (string key in row.Keys) columnSet.Add(key);
        foreach (IReadOnlyDictionary<string, object?> row in actual)
            foreach (string key in row.Keys) columnSet.Add(key);
        return [.. columnSet];
    }

    private static void CompareRow(
        IReadOnlyDictionary<string, object?> expectedRow,
        IReadOnlyDictionary<string, object?> actualRow,
        string[] columns,
        List<Difference> differences)
    {
        foreach (string column in columns)
        {
            bool hasExpected = expectedRow.TryGetValue(column, out object? expectedValue);
            bool hasActual = actualRow.TryGetValue(column, out object? actualValue);
            if (!hasExpected || !hasActual)
            {
                differences.Add(new Difference(
                    "MissingColumn", column,
                    hasExpected ? "present" : "absent",
                    hasActual ? "present" : "absent"));
                continue;
            }
            if (!AreEqual(expectedValue, actualValue))
            {
                differences.Add(new Difference(
                    "Value", column, Format(expectedValue), Format(actualValue)));
            }
        }
    }

    private static List<IReadOnlyDictionary<string, object?>> SortRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string[] columns)
        => [.. rows.OrderBy(row => RowKey(row, columns), StringComparer.Ordinal)];

    private static string RowKey(IReadOnlyDictionary<string, object?> row, string[] columns)
    {
        var parts = new string[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            row.TryGetValue(columns[i], out object? value);
            parts[i] = Format(value);
        }
        return string.Join("\u001F", parts);
    }

    /// <summary>值相等判定。NaN 与任何值判不等，浮点按位比较。</summary>
    internal static bool AreEqual(object? left, object? right)
    {
        left = NormalizeDbNull(left);
        right = NormalizeDbNull(right);
        if (left is null || right is null) return left is null && right is null;

        if (left is byte[] || right is byte[]) return BytesEqual(left, right);
        if (left is double || right is double) return DoubleEqual(left, right);
        if (left is float || right is float) return FloatEqual(left, right);
        if (left is DateTime || right is DateTime) return DateTimeEqual(left, right);
        if (left is DateTimeOffset || right is DateTimeOffset) return OffsetEqual(left, right);
        if (left is decimal || right is decimal) return DecimalEqual(left, right);

        return Equals(left, right);
    }

    private static bool OffsetEqual(object? left, object? right)
        => left is DateTimeOffset leftOffset && right is DateTimeOffset rightOffset
            && leftOffset == rightOffset;

    private static bool DecimalEqual(object? left, object? right)
        => left is decimal leftDecimal && right is decimal rightDecimal
            && leftDecimal == rightDecimal;

    private static bool BytesEqual(object? left, object? right)
        => left is byte[] leftBytes && right is byte[] rightBytes
            && leftBytes.AsSpan().SequenceEqual(rightBytes);

    private static bool DoubleEqual(object? left, object? right)
        => left is double leftDouble && right is double rightDouble
            && !double.IsNaN(leftDouble) && !double.IsNaN(rightDouble)
            && BitConverter.DoubleToInt64Bits(leftDouble) == BitConverter.DoubleToInt64Bits(rightDouble);

    private static bool FloatEqual(object? left, object? right)
        => left is float leftFloat && right is float rightFloat
            && !float.IsNaN(leftFloat) && !float.IsNaN(rightFloat)
            && BitConverter.SingleToInt32Bits(leftFloat) == BitConverter.SingleToInt32Bits(rightFloat);

    private static bool DateTimeEqual(object? left, object? right)
        => left is DateTime leftTime && right is DateTime rightTime
            && NormalizeDateTime(leftTime) == NormalizeDateTime(rightTime);

    internal static object? NormalizeDbNull(object? value) => value is DBNull ? null : value;

    /// <summary>规范化后的可读表示。仅用于排序键与失败消息，不参与相等判定。</summary>
    internal static string Format(object? value)
    {
        object? normalized = NormalizeDbNull(value);
        return normalized switch
        {
            null => "null",
            string text => "str:" + text,
            byte[] bytes => "bytes:" + Convert.ToBase64String(bytes),
            bool flag => flag ? "bool:true" : "bool:false",
            decimal number => "dec:" + number.ToString(CultureInfo.InvariantCulture),
            double number => double.IsNaN(number) ? "dbl:NaN" : "dbl:" + number.ToString("R", CultureInfo.InvariantCulture),
            float number => float.IsNaN(number) ? "flt:NaN" : "flt:" + number.ToString("R", CultureInfo.InvariantCulture),
            DateTime time => NormalizeDateTime(time),
            DateTimeOffset offset => "dto:" + offset.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            DateOnly date => "date:" + date.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly time => "time:" + time.ToString("O", CultureInfo.InvariantCulture),
            Guid guid => "guid:" + guid.ToString("D"),
            IFormattable formattable => formattable.GetType().Name + ":" + formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => normalized!.GetType().Name + ":" + normalized.ToString()
        };
    }

    /// <summary>时间规范化：Utc 原样、Local 转 UTC、Unspecified 保留独立前缀。</summary>
    internal static string NormalizeDateTime(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => "utc:" + value.ToString("O", CultureInfo.InvariantCulture),
        DateTimeKind.Local => "utc:" + value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        _ => "unspecified:" + value.ToString("O", CultureInfo.InvariantCulture)
    };
}