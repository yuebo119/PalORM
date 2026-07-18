using System.Data.Common;
using MySqlConnector;

namespace PalORM.MySql;

/// <summary>MySQL Provider —— MySqlConnector 适配。</summary>
public sealed class MySqlProvider : IDbProvider
{
    public static string Name => "MySql";
    public static SqlDialect Dialect => SqlDialect.MySql;

    public static DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

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

    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    }

    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    public static string GetLimitOffsetClause(int? limit, int? offset)
    {
        // 无 limit 时 MySQL 需极大值哨兵（LIMIT offset, -1 是非法语法）——与 BuildLimitClause 对齐
        string take = limit?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? "18446744073709551615";
        return $"LIMIT {offset ?? 0}, {take}";
    }

    public static bool SupportsReturningClause => false;
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    /// <summary>MySQL 无 CREATE INDEX IF NOT EXISTS：迁移幂等靠识别 1061 重名索引错误。</summary>
    public static bool IsDuplicateSchemaObject(Exception exception)
        => exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyName };

    /// <summary>1062 Duplicate entry——唯一约束冲突。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.CommandText = $"SHOW COLUMNS FROM {QuoteQualifiedIdentifier(schema, tableName)}";
        return 0;
    }

    public static string GetParameterPlaceholder(int index) => $"@p{index}";

    public static DbParameter CreateParameter(string name, object? value)
        => new MySqlParameter(name, value ?? DBNull.Value);

    /// <summary>批量插入——委托共享多值 INSERT 骨架。
    /// <para>MySQL 协议单语句占位符上限 65535（2 字节计数）；宽表大批次经骨架按列数钳制，
    /// 避免超出 max_allowed_packet/预处理参数上限时的晚期运行时错误（ITM-304）。</para></summary>
    public static Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, CancellationToken ct)
        where T : class, new()
        => MultiValueBulkInsert.ExecuteAsync(
            conn, transaction, entities, batchSize,
            maxParametersPerStatement: 65535,
            QuoteIdentifier, CreateParameter, ct);
}
