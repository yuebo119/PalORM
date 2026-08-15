using System.Globalization;
using System.Runtime.CompilerServices;

namespace PalORM.PostgreSql;

/// <summary>PostgreSQL 专有扩展方法。</summary>
public static class PostgreSqlExtensions
{
    /// <summary>JSONB 路径查询——生成 WHERE "column"->&gt;@path = @value。
    /// 列名经 QuoteIdentifier 白名单转义；path 与 value 均为绑定参数，零 SQL 注入。
    /// <c>-&gt;&gt;</c> 返回 text，非字符串 value 以 InvariantCulture 归一为字符串绑定，
    /// 避免 PG 端 <c>text = integer</c> 无操作符错误。</summary>
    /// <param name="builder">查询构建器。</param>
    /// <param name="column">JSONB 列名（标识符，经引号转义后进入 SQL 文本）。</param>
    /// <param name="path">JSON 键路径（绑定参数）。</param>
    /// <param name="value">比较值（绑定参数，按 text 比较；null 生成 IS NULL 语义请改用显式 SQL）。</param>
    public static QueryBuilder<T> WhereJson<T>(this QueryBuilder<T> builder, string column, string path, object? value) where T : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // NUL 显式拒绝（ITM-212）：PG 线协议不允许字符串含 0x00。column 进 SQL 文本
        // 格式串段（与 ValidateSqlComment 同侧防御）；path 是绑定参数——参数化已隔离
        // 注入面，但驱动层对 NUL 的错误形态不可控（ITM-644），库内统一明确失败。
        if (column.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("JSONB 列名不能包含 NUL 字符。", nameof(column));
        if (path.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("JSONB 路径不能包含 NUL 字符。", nameof(path));
        // 花括号转义：列名进入复合格式串文本段，未转义的 {/} 会被格式解析器误读。
        string quoted = PostgreSqlProvider.QuoteIdentifier(column)
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);
        // ->> 结果恒为 text：非字符串 value 归一为不变文化字符串，绑定参数类型对齐。
        // ITM-610：bool 必须特判小写——Convert.ToString(bool) 产 "True"（首字母大写），
        // jsonb 提取 text 恒为 "true"，text 相等比较大小写敏感 → 恒不匹配静默空结果。
        // ITM-641(r4)：DateTime/DateTimeOffset 显式拒绝——Convert.ToString 产区域格式
        // （06/15/2026 ...），jsonb ->> 提取 ISO text 恒不相等（同型静默空结果）。格式
        // 对齐需 PG 真库实证提取形态——实现前响亮拒绝优于静默错；调用方请先 ToString
        // 为与存储一致的 ISO 形态再传 string。
        object? normalized = value switch
        {
            null or string => value,
            bool b => b ? "true" : "false",
            DateTime or DateTimeOffset => throw new NotSupportedException(
                "WhereJson does not accept DateTime/DateTimeOffset values: the culture-formatted text "
                + "never matches the jsonb ISO text extracted by '->>'. Serialize to the stored ISO string form first."),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
        return builder.Where(FormattableStringFactory.Create(quoted + "->>{0} = {1}", path, normalized));
    }
}
