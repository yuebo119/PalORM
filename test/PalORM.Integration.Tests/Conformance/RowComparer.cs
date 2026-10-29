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
        if (left is bool || right is bool) return BoolEqual(left, right);
        if (left is double || right is double || left is float || right is float
            || left is decimal || right is decimal)
            return NumericEqual(left, right);
        if (left is DateTime || right is DateTime) return DateTimeEqual(left, right);
        if (left is DateTimeOffset || right is DateTimeOffset) return OffsetEqual(left, right);

        return Equals(left, right);
    }

    /// <summary>布尔与整数判等：SQLite 的 BOOLEAN 是 INTEGER 亲和，读回 1/0 是方言事实。
    /// 只接受 0 与 1，其余整数与布尔不判等（避免把类型映射错误洗成相等）。</summary>
    private static bool BoolEqual(object? left, object? right)
    {
        if (left is bool leftBool && right is bool rightBool) return leftBool == rightBool;
        if (left is bool flag && right is long or int or short or byte)
            return (flag ? 1L : 0L) == Convert.ToInt64(right, CultureInfo.InvariantCulture);
        if (right is bool rightFlag && left is long or int or short or byte)
            return (rightFlag ? 1L : 0L) == Convert.ToInt64(left, CultureInfo.InvariantCulture);
        return false;
    }

    /// <summary>数值判等。同类保持精确（浮点按位、decimal 按值）；跨表示只允许
    /// decimal 与浮点之间按 decimal 换算比较：SQLite 无 DECIMAL 真实类型，读回 REAL 是方言事实。</summary>
    private static bool NumericEqual(object? left, object? right)
    {
        if (left is double leftDouble && right is double rightDouble)
        {
            if (double.IsNaN(leftDouble) || double.IsNaN(rightDouble)) return false;
            return BitConverter.DoubleToInt64Bits(leftDouble) == BitConverter.DoubleToInt64Bits(rightDouble);
        }
        if (left is float leftFloat && right is float rightFloat)
        {
            if (float.IsNaN(leftFloat) || float.IsNaN(rightFloat)) return false;
            return BitConverter.SingleToInt32Bits(leftFloat) == BitConverter.SingleToInt32Bits(rightFloat);
        }
        if (left is decimal leftDecimal && right is decimal rightDecimal)
            return leftDecimal == rightDecimal;

        return TryAsDecimal(left, out decimal convertedLeft)
            && TryAsDecimal(right, out decimal convertedRight)
            && convertedLeft == convertedRight;
    }

    private static bool TryAsDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case decimal number:
                result = number;
                return true;
            case double doubleNumber when !double.IsNaN(doubleNumber) && !double.IsInfinity(doubleNumber):
                // (decimal) 强转会完整搬入 double 的二进制误差（(decimal)12.34d = 12.339999999999999858），
                // 必须按最短往返十进制表示解析才能得到 12.34m；超出 decimal 范围时 TryParse 返回 false
                return decimal.TryParse(
                    doubleNumber.ToString("R", CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            case float floatNumber when !float.IsNaN(floatNumber) && !float.IsInfinity(floatNumber):
                return decimal.TryParse(
                    floatNumber.ToString("R", CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            case long or int or short or byte:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0m;
                return false;
        }
    }

    private static bool OffsetEqual(object? left, object? right)
        => left is DateTimeOffset leftOffset && right is DateTimeOffset rightOffset
            && leftOffset == rightOffset;

    private static bool BytesEqual(object? left, object? right)
        => left is byte[] leftBytes && right is byte[] rightBytes
            && leftBytes.AsSpan().SequenceEqual(rightBytes);

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
            // 数值与布尔统一 num: 前缀：SQLite 的 BOOLEAN/DECIMAL 读回 INTEGER/REAL，
            // 排序键必须让 true 与 1、12.34m 与 12.34d 落在同一键位，否则行配对会错位
            bool flag => flag ? "num:1" : "num:0",
            long or int or short or byte => "num:" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            decimal number => "num:" + number.ToString(CultureInfo.InvariantCulture),
            double number => double.IsNaN(number) ? "nan" : "num:" + number.ToString("R", CultureInfo.InvariantCulture),
            float number => float.IsNaN(number) ? "nan" : "num:" + number.ToString("R", CultureInfo.InvariantCulture),
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