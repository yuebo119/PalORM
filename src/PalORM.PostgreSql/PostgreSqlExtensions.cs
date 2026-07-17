namespace PalORM.PostgreSql;

/// <summary>PostgreSQL 专有扩展方法。</summary>
public static class PostgreSqlExtensions
{
    /// <summary>JSONB 路径查询——WHERE column->>'path' = value。</summary>
    public static QueryBuilder<T> WhereJson<T>(this QueryBuilder<T> builder, string jsonPath, object value) where T : class, new()
    {
        return builder.Where($"{(FormattableString)$"\"{jsonPath}\" = {value}"}");
    }
}
