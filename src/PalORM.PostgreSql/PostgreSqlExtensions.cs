using System.Globalization;
using System.Runtime.CompilerServices;

namespace PalORM.PostgreSql;

/// <summary>PostgreSQL 专有扩展方法。</summary>
public static class PostgreSqlExtensions
{
    /// <summary>C13（2026-10-01 全 API 逐项轮）：WhereJson 格式串缓存容量上限——
    /// 键空间 = 应用内 JSONB 列名集（通常为代码常量）；超限不写仅退化重建，防动态列名无界增长。</summary>
    private const int MaxJsonFormatCacheEntries = 256;

    /// <summary>C13（2026-10-01）：(列名 → 转义后格式串) 缓存（见 <see cref="WhereJson{T}"/> 内注释）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> JsonFormatCache = new();
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
        // ITM-770(r21)：方言守卫——本扩展硬编码 PG 的 ->> 与双引号标识符；MySQL 默认
        // sql_mode 下 "col" 是字符串字面量，误用会生成静默错误结果（G31 方言感知要求）。
        // 扩展方法无法按接收者类型约束 Provider，运行时按构建器方言明确拒绝。
        if (builder.Dialect != SqlDialect.PostgreSql)
            throw new NotSupportedException(
                $"WhereJson requires the PostgreSQL dialect (builder dialect: {builder.Dialect}). " +
                "The '->>' operator and double-quoted identifiers are PostgreSQL-specific.");
        // NUL 显式拒绝（ITM-212）：PG 线协议不允许字符串含 0x00。column 进 SQL 文本
        // 格式串段（与 ValidateSqlComment 同侧防御）；path 是绑定参数——参数化已隔离
        // 注入面，但驱动层对 NUL 的错误形态不可控（ITM-644），库内统一明确失败。
        if (column.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("JSONB column name must not contain NUL characters.", nameof(column));
        if (path.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("JSONB path must not contain NUL characters.", nameof(path));
        // C13（2026-10-01 全 API 逐项轮）：格式串（花括号转义列名 + "->>{0} = {1}"）按列名缓存——
        // 原实现每次调用 2 次 Replace 扫描 + 1 次拼接。花括号转义：列名进入复合格式串文本段，
        // 未转义的 {/} 会被格式解析器误读。
        if (!JsonFormatCache.TryGetValue(column, out string? format))
        {
            string quoted = PostgreSqlProvider.QuoteIdentifier(column)
                .Replace("{", "{{", StringComparison.Ordinal)
                .Replace("}", "}}", StringComparison.Ordinal);
            format = quoted + "->>{0} = {1}";
            if (JsonFormatCache.Count < MaxJsonFormatCacheEntries)
                JsonFormatCache.TryAdd(column, format);
        }
        // ITM-701：value 与 column/path 同口径 NUL 显式拒绝——绑定参数虽已隔离注入面，
        // 但 Npgsql 线协议对 NUL 的错误形态不可控，库内统一明确失败（ITM-644 族）。
        object? normalized = NormalizeJsonValue(value);
        if (normalized is string normalizedString && normalizedString.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("JSONB comparison value must not contain NUL characters.", nameof(value));
        return builder.Where(FormattableStringFactory.Create(format, path, normalized));
    }

    /// <summary>value → text 比较值归一。<c>->></c> 结果恒为 text：非字符串 value 归一为
    /// 不变文化字符串，绑定参数类型对齐。格式恒不相等族（bool/DateTime/DateOnly/enum/char/
    /// TimeSpan/byte[]/double/float）显式拒绝——详见各分支 ITM 注释。</summary>
    private static object? NormalizeJsonValue(object? value) => value switch
    {
        null or string => value,
        bool b => b ? "true" : "false",
        // ITM-610：bool 特判小写——Convert.ToString(bool) 产 "True"，jsonb 恒 "true"，大小写敏感恒不匹配。
        // ITM-641(r4)：DateTime/DateTimeOffset 显式拒绝——Convert.ToString 产区域格式，jsonb ->>
        // 提取 ISO text 恒不相等。格式对齐需 PG 真库实证提取形态——实现前响亮拒绝优于静默错；
        // 调用方请先 ToString 为与存储一致的 ISO 形态再传 string。
        DateTime or DateTimeOffset => throw new NotSupportedException(
            "WhereJson does not accept DateTime/DateTimeOffset values: the culture-formatted text "
            + "never matches the jsonb ISO text extracted by '->>'. Serialize to the stored ISO string form first."),
        // r19/ITM-683：DateOnly/TimeOnly 与 DateTime 同族——Convert.ToString 的 invariant
        // 输出（MM/dd/yyyy / H:mm）与 jsonb ->> 提取的 ISO text（yyyy-MM-dd / HH:mm:ss）
        // 恒不相等 → 静默空结果。显式拒绝，调用方请 ToString("yyyy-MM-dd")/("HH:mm:ss")。
        DateOnly or TimeOnly => throw new NotSupportedException(
            "WhereJson does not accept DateOnly/TimeOnly values: the invariant-formatted text "
            + "never matches the jsonb ISO text extracted by '->>'. Serialize to the stored ISO string form first "
            + "(e.g. value.ToString(\"yyyy-MM-dd\") or value.ToString(\"HH:mm:ss\"))."),
        // ITM-771(r21)：enum/char/TimeSpan/byte[] 与 DateOnly 同族——Convert.ToString 的
        // 输出与 jsonb ->> 提取 text 恒不相等（enum 产符号名而 jsonb 存数字/字符串、
        // byte[] 产类型名）→ 静默空结果（ITM-610/683 根因类）。显式拒绝。
        Enum or char or TimeSpan => throw new NotSupportedException(
            "WhereJson does not accept enum/char/TimeSpan values: the invariant-formatted text "
            + "never matches the jsonb text extracted by '->>'. Serialize to the stored string form first."),
        byte[] => throw new NotSupportedException(
            "WhereJson does not accept byte[] values; serialize to the stored string form first."),
        // ITM-890（r24，PG 18.6 真库实证）：double/float 与上族同型——InvariantCulture 输出
        // 大值走 E 记法（1e21 → "1E+21"）且去尾零（1.10 → "1.1"），而 jsonb ->> 的 numeric
        // 文本恒为展开定点形（"1000000000000000000000"）且保留输入 scale 尾零（"1.10"）→
        // 大值/尾零形态恒不相等 → 静默空结果（实测 1e21/1.10 均 False、0.1 简形相等）。
        // decimal 不拒绝：其 ToString 保留 scale 尾零，与 jsonb numeric 的 scale 形态一致
        //（实测 1.10m == '1.10' 相等）。
        double or float => throw new NotSupportedException(
            "WhereJson does not accept double/float values: the invariant text (E-notation for "
            + "large values, trailing zeros stripped) never matches the jsonb numeric text extracted "
            + "by '->>' (always expanded decimal with input scale preserved). "
            + "Serialize to the stored string form first."),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
