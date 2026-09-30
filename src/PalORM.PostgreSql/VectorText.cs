using System.Globalization;
using System.Text;

namespace PalORM.PostgreSql;

/// <summary>pgvector 向量文本的生成与校验（ADR 向量门结论 B 分支：全文本边界 Raw 路线的
/// 配套设施，实现规格见 <c>docs/向量搜索Raw配方.md</c>）。
/// <para><b>设计定位</b>：pgvector 列经 <c>[Column(TypeName = "vector(n)")]</c> 之外的 Raw
/// 路线管理时，向量以 pgvector 文本字面量（<c>"[0.1,0.2,0.3]"</c>）经显式 <c>::vector</c>
/// cast 写入（PG 对 string→用户类型的自动 cast 是 explicit-only，裸 text 参数必败——
/// PG `CREATE CAST` 文档）。本类型把"维度不匹配/非法格式运行期才在数据库报错"前移到
/// 赋值行；InvariantCulture 恒定小数点，防区域设置产出逗号小数污染字面量。</para>
/// <para><b>读侧</b>：向量列必须以 <c>::text</c> 转出（Npgsql 无 vector 映射且
/// <c>EnableUnmappedTypes</c> 与 AOT 不兼容），回读的文本可用 <see cref="TryParse"/> 解析。</para></summary>
public static class VectorText
{
    /// <summary>把向量值格式化为 pgvector 文本字面量（<c>"[v1,v2,...]"</c>）。
    /// 维度与 <paramref name="expectedDim"/> 不符即抛（列定义 vector(n) 的 n 在调用点可知，
    /// 错误前移到生成行而非数据库端）。</summary>
    /// <param name="values">向量分量。</param>
    /// <param name="expectedDim">目标列的声明维度（vector(n) 的 n）。</param>
    public static string Of(ReadOnlySpan<float> values, int expectedDim)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedDim);
        if (values.Length != expectedDim)
            throw new ArgumentException(
                $"向量维度 {values.Length} 与列声明 vector({expectedDim}) 不符。", nameof(values));

        var sb = new StringBuilder(values.Length * 8).Append('[');
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }

    /// <summary>解析 pgvector 文本字面量为 float 数组（<c>::text</c> 回读列的配套解析）。
    /// 非法格式（缺括号/非数值/空分量）返回 <c>false</c>，维度不匹配同理——由调用方决定
    /// 抛错或降级。</summary>
    public static bool TryParse(string text, int expectedDim, out float[] values)
    {
        values = [];
        ArgumentNullException.ThrowIfNull(text);
        if (expectedDim <= 0 || text.Length < 2 || text[0] != '[' || text[^1] != ']')
            return false;

        string[] parts = text[1..^1].Split(',');
        if (parts.Length != expectedDim)
            return false;

        var result = new float[expectedDim];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                return false;
        }
        values = result;
        return true;
    }
}
