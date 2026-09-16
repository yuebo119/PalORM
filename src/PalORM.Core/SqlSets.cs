namespace PalORM;

/// <summary>编译期生成的单方言 CRUD SQL 集。
/// <para><b>哪些字段有值取决于方言族（v5.6 起）</b>：运行时按
/// <c>TProvider.SupportsReturningClause</c> 分发——PostgreSQL/SQLite 读
/// <see cref="InsertReturning"/> 与 <see cref="UpsertReturning"/>，MySQL 读
/// <see cref="InsertWithLastInsertId"/> 与 <see cref="UpsertMySql"/>。
/// 生成器**只发射本族会被读的载荷**，另一族字段为 <c>""</c>（实测此举省下注册文件 27% 的字符，
/// SQL 载荷的 49%）——跨族字段本就不可达，故这不影响任何运行路径。</para>
/// <para>读这两个族之外的字段请先确认方言：拿到 <c>""</c> 表示"该字段与本方言无关"，
/// 而不是"SQL 生成失败"。<see cref="Update"/>/<see cref="Delete"/> 两族皆有值。</para></summary>
/// <param name="Insert">INSERT 语句。<b>v5.6 起恒为 <c>""</c></b>：全方言无消费者（运行时一律走
/// <see cref="InsertReturning"/> 或 <see cref="InsertWithLastInsertId"/>）。字段保留是为了不破坏
/// 既有构造点；计划在下个主版本随其它破坏性项一并移除。</param>
/// <param name="Update">按主键 UPDATE 语句。两族皆有值。</param>
/// <param name="Delete">按主键 DELETE 语句。两族皆有值。</param>
/// <param name="InsertReturning">带主键回填的 INSERT 语句（RETURNING）。仅 PostgreSQL/SQLite 有值。</param>
/// <param name="UpsertReturning">UPSERT + RETURNING（ON CONFLICT ... DO UPDATE/NOTHING RETURNING）。仅 PostgreSQL/SQLite 有值。</param>
/// <param name="UpsertMySql">MySQL UPSERT 语句（ON DUPLICATE KEY UPDATE）。仅 MySQL 有值。</param>
/// <param name="InsertWithLastInsertId">MySQL INSERT + SELECT LAST_INSERT_ID()。仅 MySQL 有值。</param>
public readonly record struct CommandSqlSet(
    string Insert,
    string Update,
    string Delete,
    string InsertReturning,
    string UpsertReturning,
    string UpsertMySql,
    string InsertWithLastInsertId);

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
