using System.Data.Common;
using MySqlConnector;

namespace PalORM.MySql;

/// <summary>MySQL Provider —— MySqlConnector 适配。</summary>
public sealed class MySqlProvider : IDbProvider
{
    /// <summary>Provider 名称:MySql。</summary>
    public static string Name => "MySql";

    /// <summary>SQL 方言标识:<see cref="SqlDialect.MySql"/>。</summary>
    public static SqlDialect Dialect => SqlDialect.MySql;

    /// <summary>创建 MySqlConnection,连接池配置沿用连接串默认值。</summary>
    public static DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    /// <summary>创建连接并把 <see cref="DbOptions"/> 池配置映射到 MySqlConnector 连接串:
    /// MaximumPoolSize / ConnectionIdleTimeout(秒)/ ConnectionLifeTime(分钟换算为秒);
    /// MySqlConnector 池参数为 uint,checked 转换防负值/溢出静默截断。</summary>
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            MaximumPoolSize = checked((uint)options.MaxPoolSize),
            ConnectionIdleTimeout = checked((uint)options.PoolIdleTimeoutSeconds),
            ConnectionLifeTime = checked((uint)(options.PoolLifetimeMinutes * 60))
        };
        return new MySqlConnection(builder.ConnectionString);
    }

    /// <summary>反引号引用标识符(MySQL 方言,非 SQL 标准双引号),内部反引号以 `` 转义。</summary>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    }

    /// <summary>schema 与表名分别反引号引用后以点连接;MySQL 中 schema 即数据库名。</summary>
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    /// <summary>MySQL 的 LIMIT offset, count 语法;无 limit 时用 uint64 极大值哨兵(LIMIT offset, -1 非法)。</summary>
    [Obsolete("零调用点的死接口成员；LIMIT 构建统一在 QueryBuilder.BuildLimitClause。3.0 随接口成员一并移除。")]
    public static string GetLimitOffsetClause(int? limit, int? offset)
    {
        // 无 limit 时 MySQL 需极大值哨兵（LIMIT offset, -1 是非法语法）——与 BuildLimitClause 对齐
        string take = limit?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? "18446744073709551615";
        return $"LIMIT {offset ?? 0}, {take}";
    }

    /// <summary>MySQL 不支持 RETURNING 子句(自增主键回读走 LAST_INSERT_ID 路径)。</summary>
    public static bool SupportsReturningClause => false;

    /// <summary>CURRENT_TIMESTAMP——注意 MySQL 返回会话时区时间(与 SQLite 的恒 UTC 语义不同,ITM-326)。</summary>
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    /// <summary>MySQL 无 CREATE INDEX IF NOT EXISTS：迁移幂等靠识别 1061 重名索引错误。
    /// <para><b>ITM-528 已知限制</b>：错误码 1061 只表示"索引名已存在"，无法区分
    /// "同名同构"（真正幂等，应跳过）与"同名异构"（旧索引列集不同，实为冲突）。
    /// 因此修改 [Index]/[Unique] 的列集但保留索引名后再迁移，旧索引会被当作幂等静默保留，
    /// 新列集不生效。规避方法：变更被索引列后需手动 DROP INDEX 旧索引再迁移，
    /// 或直接改用新的索引名。运行时无法安全区分两者，故此处不改判定逻辑。</para></summary>
    public static bool IsDuplicateSchemaObject(Exception exception)
        => exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyName };

    /// <summary>1062 Duplicate entry——唯一约束冲突。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };

    /// <summary>用 SHOW COLUMNS 查询列信息(表名/库名经反引号引用内联),列名位于结果集序号 0。</summary>
    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.CommandText = $"SHOW COLUMNS FROM {QuoteQualifiedIdentifier(schema, tableName)}";
        return 0;
    }

    /// <summary>参数占位符,形如 @p0。</summary>
    public static string GetParameterPlaceholder(int index) => $"@p{index}";

    /// <summary>创建 MySqlParameter;value 为 null 时转为 <see cref="DBNull.Value"/>(ADO.NET 中 null 参数值不会被发送)。</summary>
    public static DbParameter CreateParameter(string name, object? value)
        => new MySqlParameter(name, value ?? DBNull.Value);

    /// <summary>批量插入——委托共享多值 INSERT 骨架。
    /// <para>MySQL 协议单语句占位符上限 65535（2 字节计数）；宽表大批次经骨架按列数钳制，
    /// 避免超出 max_allowed_packet/预处理参数上限时的晚期运行时错误（ITM-304）。</para></summary>
    public static Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds, CancellationToken ct)
        where T : class, new()
        => MultiValueBulkInsert.ExecuteAsync(
            conn, transaction, entities, batchSize,
            maxParametersPerStatement: 65535,
            QuoteIdentifier, CreateParameter, commandTimeoutSeconds, ct);
}
