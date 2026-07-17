namespace PalORM;

/// <summary>SQL 方言枚举。定义各数据库的语法差异标识，配合 IDbProvider 方言方法使用。</summary>
public enum SqlDialect
{
    PostgreSql,
    MySql,
    Sqlite
}
