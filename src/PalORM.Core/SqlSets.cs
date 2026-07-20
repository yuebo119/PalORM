namespace PalORM;

/// <summary>编译期生成的单方言 CRUD SQL 集。</summary>
/// <param name="Insert">INSERT 语句（不含主键回填）。</param>
/// <param name="Update">按主键 UPDATE 语句。</param>
/// <param name="Delete">按主键 DELETE 语句。</param>
/// <param name="InsertReturning">带主键回填的 INSERT 语句（如 RETURNING/LAST_INSERT_ID）。</param>
public readonly record struct CommandSqlSet(string Insert, string Update, string Delete, string InsertReturning);

/// <summary>编译期生成的三数据库方言 CRUD SQL。</summary>
/// <param name="Sqlite">SQLite 方言 SQL 集。</param>
/// <param name="PostgreSql">PostgreSQL 方言 SQL 集。</param>
/// <param name="MySql">MySQL 方言 SQL 集。</param>
public readonly record struct CommandSqlByDialect(
    CommandSqlSet Sqlite,
    CommandSqlSet PostgreSql,
    CommandSqlSet MySql)
{
    /// <summary>按 Provider 方言选择对应 SQL。</summary>
    public CommandSqlSet Get(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.Sqlite => Sqlite,
            SqlDialect.PostgreSql => PostgreSql,
            SqlDialect.MySql => MySql,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
        };
}

/// <summary>编译期生成的三数据库方言建表 DDL。</summary>
/// <param name="Sqlite">SQLite 方言 CREATE TABLE。</param>
/// <param name="PostgreSql">PostgreSQL 方言 CREATE TABLE。</param>
/// <param name="MySql">MySQL 方言 CREATE TABLE。</param>
public readonly record struct CreateTableSqlSet(
    string Sqlite,
    string PostgreSql,
    string MySql)
{
    /// <summary>按 Provider 方言选择对应 DDL。</summary>
    public string Get(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.Sqlite => Sqlite,
            SqlDialect.PostgreSql => PostgreSql,
            SqlDialect.MySql => MySql,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
        };
}

/// <summary>编译期生成的三数据库方言索引 DDL（ADR-B：[Index]/[Unique] 注解产物）。</summary>
/// <param name="Sqlite">SQLite 方言 CREATE INDEX 语句组。</param>
/// <param name="PostgreSql">PostgreSQL 方言 CREATE INDEX 语句组。</param>
/// <param name="MySql">MySQL 方言 CREATE INDEX 语句组。</param>
public readonly record struct CreateIndexSqlSet(
    IReadOnlyList<string> Sqlite,
    IReadOnlyList<string> PostgreSql,
    IReadOnlyList<string> MySql)
{
    /// <summary>按 Provider 方言选择对应索引 DDL 组。</summary>
    public IReadOnlyList<string> Get(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.Sqlite => Sqlite,
            SqlDialect.PostgreSql => PostgreSql,
            SqlDialect.MySql => MySql,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
        };
}
