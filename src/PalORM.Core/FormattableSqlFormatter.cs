using System.Text;

namespace PalORM;

/// <summary>将复合格式项映射为数据库参数名，不格式化参数值。</summary>
internal static class FormattableSqlFormatter
{
    // S1994/S127（for 循环体内修改 stop 变量）在此抑制：
    //   - `{{`/`}}` 转义符需跳过第二个字符
    //   - `{N}` 占位符解析后需把游标直接跳到结束花括号位置
    // 这些是单遍扫描复合格式串的标准实现（与 BCL StringBuilder.AppendFormat 内部模式一致），
    // 末尾的自增不会破坏正确性--每次循环内已先调整 index 到目标位置。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S127:DoNotUpdateLoopVariableInLoopBody",
        Justification = "Composite format scan requires cursor adjustment for escapes and placeholders.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S1994:ForLoopConditionChanged",
        Justification = "Same as S127 - composite format scan requires index manipulation.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "Single-pass composite format scan - three branches (escape/placeholder/literal) + validation.")]
    internal static string Format(FormattableString sql, int baseIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentOutOfRangeException.ThrowIfNegative(baseIndex);

        string format = sql.Format;
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
                ReadOnlySpan<char> argumentIndex = separator < 0 ? item : item[..separator];
                if (!int.TryParse(argumentIndex, out int parsedIndex)
                    || parsedIndex < 0
                    || parsedIndex >= sql.ArgumentCount)
                {
                    throw new FormatException(
                        $"Formattable SQL contains an invalid argument index '{argumentIndex}' " +
                        $"(argument count: {sql.ArgumentCount}).");
                }
                // v4.1：校验 alignment 部分（逗号后）--替代被删除的 CompositeFormat.Parse 的格式验证
                if (separator >= 0 && item[separator] == ',')
                {
                    ReadOnlySpan<char> rest = item[(separator + 1)..];
                    int formatColon = rest.IndexOf(':');
                    ReadOnlySpan<char> alignment = formatColon < 0 ? rest : rest[..formatColon];
                    if (!alignment.IsWhiteSpace() && !int.TryParse(alignment, out _))
                        throw new FormatException(
                            $"Formattable SQL contains an invalid alignment '{alignment}' in format item.");
                }

                sb.Append("@p");
                sb.Append((baseIndex + parsedIndex).ToString(System.Globalization.CultureInfo.InvariantCulture));
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
