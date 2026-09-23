using System.Text;

namespace PalORM;

/// <summary>将复合格式项映射为数据库参数名，不格式化参数值。
/// <para><b>格式说明符契约（v7.2.1 显式化）</b>: 插值项的格式说明符（如 <c>{0:N2}</c> 的
/// <c>:N2</c>）被<b>有意忽略</b>——参数化路径下值恒以原始对象绑定（驱动负责类型转换），
/// 数据库侧不存在 .NET 格式化语义；alignment（<c>{0,5}</c> 逗号后）仍校验合法性但同样不参与
/// 输出。该契约由 QueryExecutionTests.FormatFormattableSql_ValidCompositeFormat 锁定。</para></summary>
internal static class FormattableSqlFormatter
{
    // S1994/S127（for 循环体内修改 stop 变量）在此抑制：
    //   - `{{`/`}}` 转义符需跳过第二个字符
    //   - `{N}` 占位符解析后需把游标直接跳到结束花括号位置
    // 这些是单遍扫描复合格式串的标准实现（与 BCL StringBuilder.AppendFormat 内部模式一致），
    // 末尾的自增不会破坏正确性--每次循环内已先调整 index 到目标位置。
    // T1 形状缓存：格式化输出是（Format 文本, baseIndex, ArgumentCount）的**纯函数**，
    // 而 Format 文本是编译期 ldstr 常量——同一调用点每次返回同一实例。以
    // （值相等文本, 槽位偏移, 参数个数）为键缓存输出，命中时复用同一 SQL 文本实例。
    // 实测收益为每次查询 −40 B（输出串实例复用）+ 省去复合格式扫描——远小于立项时
    // 的预估（−36%），因为格式扫描本身已走 ValueStringBuilder 栈分配、几乎零分配，
    // 可省的只有输出串。保留原因：正收益、零行为变化、缓存命中还省扫描时间；
    // 键不含参数值——编译期参数化保证值只进 @pN 占位，同形状 ⇒ 同 SQL 文本。
    // 容量以应用内"不同查询形状数"为界（有限且通常很小），与既有静态缓存同纪律；
    // 放在非泛型类避免按 T 分片（S2743）。
    // CACHE-001（2026-09-23）：键空间 = Format × BaseIndex × ArgumentCount。Format 由调用点决定
    // （有限），但 BaseIndex 是运行期参数偏移（QueryBuilder 按子句前累积参数数传入），动态集合大小
    // 会让同一格式串产生不同键——因此"有限键集"只对单看 Format 成立，需要显式上限兜底。
    private const int MaxEntries = 4096;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string Format, int BaseIndex, int ArgumentCount), string> ShapeCache = new();

    /// <summary>带形状缓存的格式化入口——QueryBuilder 热路径经此调用。</summary>
    internal static string FormatCached(string format, int baseIndex, int argumentCount)
    {
        (string Format, int BaseIndex, int ArgumentCount) key = (format, baseIndex, argumentCount);
        // 命中路径零额外开销（与原先的 GetOrAdd 同为一次查表）
        if (ShapeCache.TryGetValue(key, out string? cached)) return cached;
        // 未命中才做容量治理：超限整体清空（自愈式重置）。格式化是纯函数，重建成本远低于
        // "拒写导致缓存冻结在早期形状"的永久失效（与 SqlShapeCache 的拒写纪律不同，
        // 那是因为形状缓存条目构建更贵且可观测计数被外部依赖）。
        if (ShapeCache.Count >= MaxEntries) ShapeCache.Clear();
        return ShapeCache.GetOrAdd(key, static k => Format(k.Format, k.BaseIndex, k.ArgumentCount));
    }

    /// <summary>便捷重载——委托给纯字符串签名版本（保持既有调用点与契约测试不变）。</summary>
    internal static string Format(FormattableString sql, int baseIndex = 0)
        => Format(sql.Format, baseIndex, sql.ArgumentCount);

    /// <summary>把复合格式串格式化为参数化 SQL——<b>纯函数</b>：输出仅由
    /// （<paramref name="format"/>，<paramref name="baseIndex"/>，<paramref name="argumentCount"/>）决定，
    /// 与参数值无关（值只进 @pN 占位）。纯函数性是 QueryBuilder 形状缓存（T1）的正确性前提。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S127:DoNotUpdateLoopVariableInLoopBody",
        Justification = "Composite format scan requires cursor adjustment for escapes and placeholders.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S1994:ForLoopConditionChanged",
        Justification = "Same as S127 - composite format scan requires index manipulation.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "Single-pass composite format scan - three branches (escape/placeholder/literal) + validation.")]
    internal static string Format(string format, int baseIndex, int argumentCount)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegative(baseIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(argumentCount);

        // v4.1：删除丢弃的 CompositeFormat.Parse（纯浪费），改用 ValueStringBuilder（栈分配 + ArrayPool 兜底）
        var sb = new ValueStringBuilder(stackalloc char[256]);
        try
        {
            for (int index = 0; index < format.Length; index++)
            {
                char current = format[index];
                if (current == '{' && index + 1 < format.Length && format[index + 1] == '{')
                {
                    sb.Append('{');
                    index++;
                    continue;
                }

                if (current == '}' && index + 1 < format.Length && format[index + 1] == '}')
                {
                    sb.Append('}');
                    index++;
                    continue;
                }

                // v4.1：单独的 } 不是合法的转义（}} 才是）--替代被删除的 CompositeFormat.Parse 校验
                if (current == '}')
                    throw new FormatException("Formattable SQL has an unescaped '}' that is not part of a '}}' escape.");

                if (current != '{')
                {
                    sb.Append(current);
                    continue;
                }

                int close = format.IndexOf('}', index + 1);
                if (close < 0)
                    throw new FormatException("Formattable SQL has an unclosed '{' in its format string.");
                ReadOnlySpan<char> item = format.AsSpan(index + 1, close - index - 1);
                int separator = item.IndexOfAny(',', ':');
                // ':' 后的格式说明符在 item[..separator] 截断处被有意忽略——契约见类 XML doc
                ReadOnlySpan<char> argumentIndex = separator < 0 ? item : item[..separator];
                if (!int.TryParse(argumentIndex, out int parsedIndex)
                    || parsedIndex < 0
                    || parsedIndex >= argumentCount)
                {
                    throw new FormatException(
                        $"Formattable SQL contains an invalid argument index '{argumentIndex}' " +
                        $"(argument count: {argumentCount}).");
                }
                // v4.1：校验 alignment 部分（逗号后）——替代被删除的 CompositeFormat.Parse 的格式验证
                if (separator >= 0 && item[separator] == ',')
                {
                    ReadOnlySpan<char> rest = item[(separator + 1)..];
                    int formatColon = rest.IndexOf(':');
                    ReadOnlySpan<char> alignment = formatColon < 0 ? rest : rest[..formatColon];
                    if (!alignment.IsWhiteSpace() && !int.TryParse(alignment, out _))
                        throw new FormatException(
                            $"Formattable SQL contains an invalid alignment '{alignment}' in format item.");
                }

                // v4.6：用 ParameterNameCache.GetName 替代 @p + int.ToString（零分配索引取用）
                sb.Append(ParameterNameCache.GetName(baseIndex + parsedIndex));
                index = close;
            }

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }
}
