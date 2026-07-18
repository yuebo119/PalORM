namespace PalORM;

/// <summary>SQL 方言枚举。定义各数据库的语法差异标识，配合 IDbProvider 方言方法使用。</summary>
public enum SqlDialect
{
    /// <summary>PostgreSQL——标识符双引号、Binary COPY 批量写入。</summary>
    PostgreSql,

    /// <summary>MySQL——标识符反引号、多值 INSERT 批量写入。</summary>
    MySql,

    /// <summary>SQLite——标识符双引号、单文件库、不支持连接池配置。</summary>
    Sqlite
}

/// <summary>方言辅助——热路径上替代 Enum.ToString()（每次调用分配新串）。</summary>
internal static class SqlDialectExtensions
{
    /// <summary>方言名常量。查询执行管线每次观测（tracing/metrics 标签）都取名——
    /// switch 到 interned 常量为零分配。</summary>
    internal static string GetName(this SqlDialect dialect) => dialect switch
    {
        SqlDialect.PostgreSql => nameof(SqlDialect.PostgreSql),
        SqlDialect.MySql => nameof(SqlDialect.MySql),
        SqlDialect.Sqlite => nameof(SqlDialect.Sqlite),
        _ => dialect.ToString()
    };
}
