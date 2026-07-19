using System.Text;

namespace PalORM;

/// <summary>将复合格式项映射为数据库参数名，不格式化参数值。</summary>
internal static class FormattableSqlFormatter
{
    // S1994/S127（for 循环体内修改 stop 变量）在此抑制：
    //   - `{{`/`}}` 转义符需跳过第二个字符
    //   - `{N}` 占位符解析后需把游标直接跳到结束花括号位置
    // 这些是单遍扫描复合格式串的标准实现（与 BCL StringBuilder.AppendFormat 内部模式一致），
    // 末尾的自增不会破坏正确性——每次循环内已先调整 index 到目标位置。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S127:DoNotUpdateLoopVariableInLoopBody",
        Justification = "Composite format scan requires cursor adjustment for escapes and placeholders.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S1994:ForLoopConditionChanged",
        Justification = "Same as S127 — composite format scan requires index manipulation.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "单遍扫描复合格式串——三种分支（{{}} 转义/{N} 占位符/普通字符）+ 校验逻辑，"
            + "结构紧凑；拆分会破坏单遍扫描的局部性。")]
    internal static string Format(FormattableString sql, int baseIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentOutOfRangeException.ThrowIfNegative(baseIndex);

        string format = sql.Format;
        _ = CompositeFormat.Parse(format);

        var result = new StringBuilder(format.Length + sql.ArgumentCount * 4);
        for (int index = 0; index < format.Length; index++)
        {
            char current = format[index];
            if (current == '{' && index + 1 < format.Length && format[index + 1] == '{')
            {
                result.Append('{');
                index++;
                continue;
            }

            if (current == '}' && index + 1 < format.Length && format[index + 1] == '}')
            {
                result.Append('}');
                index++;
                continue;
            }

            if (current != '{')
            {
                // ITM-546：不再拦截 SQL 文本中的字面 @p<n>。此前（ITM-509）为防"手写 @p0 与
                // 生成占位符同名"而纯文本扫描拒绝，但它不理解 SQL 字符串字面量，误拒了
                // 'a@p1.com'（邮箱）、LIKE '%@p2%' 等合法 SQL。手写 @pN 冲突极罕见，
                // 且组合查询 baseIndex>0 时生成号不从 0 起，真实碰撞概率极低——移除该检测。
                result.Append(current);
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

            result.Append("@p");
            result.Append(baseIndex + parsedIndex);
            index = close;
        }

        return result.ToString();
    }
}
