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
                result.Append(current);
                continue;
            }

            int close = format.IndexOf('}', index + 1);
            ReadOnlySpan<char> item = format.AsSpan(index + 1, close - index - 1);
            int separator = item.IndexOfAny(',', ':');
            ReadOnlySpan<char> argumentIndex = separator < 0 ? item : item[..separator];
            if (!int.TryParse(argumentIndex, out int parsedIndex)
                || parsedIndex < 0
                || parsedIndex >= sql.ArgumentCount)
            {
                throw new FormatException("Formattable SQL contains an invalid argument index.");
            }

            result.Append("@p");
            result.Append(baseIndex + parsedIndex);
            index = close;
        }

        return result.ToString();
    }
}
