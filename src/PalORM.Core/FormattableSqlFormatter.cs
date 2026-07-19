using System.Text;

namespace PalORM;

/// <summary>将复合格式项映射为数据库参数名，不格式化参数值。</summary>
internal static class FormattableSqlFormatter
{
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
                // 拒绝用户 SQL 文本中手写字面 @pN——与本方法为插值项生成的 @pN 占位符同名，
                // 会静默共享绑定值产生错误数据（ITM-509）。参数化查询请统一用插值 {n}。
                if (current == '@' && index + 1 < format.Length && format[index + 1] == 'p'
                    && index + 2 < format.Length && char.IsAsciiDigit(format[index + 2]))
                {
                    throw new FormatException(
                        "Formattable SQL contains a literal '@p<n>' parameter placeholder, which collides " +
                        "with generated placeholders. Use interpolation ({0}, {1}, ...) for all parameters " +
                        "instead of writing '@p0' by hand.");
                }
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
