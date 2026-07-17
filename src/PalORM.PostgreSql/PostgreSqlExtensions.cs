using System.Runtime.CompilerServices;

namespace PalORM.PostgreSql;

/// <summary>PostgreSQL 专有扩展方法。</summary>
public static class PostgreSqlExtensions
{
    /// <summary>JSONB 路径查询——生成 WHERE "column"->&gt;@path = @value。
    /// 列名经 QuoteIdentifier 白名单转义；path 与 value 均为绑定参数，零 SQL 注入。</summary>
    /// <param name="builder">查询构建器。</param>
    /// <param name="column">JSONB 列名（标识符，经引号转义后进入 SQL 文本）。</param>
    /// <param name="path">JSON 键路径（绑定参数）。</param>
    /// <param name="value">比较值（绑定参数，按 text 比较）。</param>
    public static QueryBuilder<T> WhereJson<T>(this QueryBuilder<T> builder, string column, string path, object? value) where T : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // 花括号转义：列名进入复合格式串文本段，未转义的 {/} 会被格式解析器误读。
        string quoted = PostgreSqlProvider.QuoteIdentifier(column)
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);
        return builder.Where(FormattableStringFactory.Create(quoted + "->>{0} = {1}", path, value));
    }
}
