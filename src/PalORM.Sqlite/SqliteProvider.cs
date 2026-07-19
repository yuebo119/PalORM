using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace PalORM.Sqlite;

/// <summary>SQLite Provider —— Microsoft.Data.Sqlite 适配。</summary>
public sealed class SqliteProvider : IDbProvider
{
    static SqliteProvider()
    {
        // SQLite3MC.PCLRaw.bundle 要求显式初始化（其 README 明示）；Microsoft.Data.Sqlite.Core
        // 不含自动 bundle 探测，NativeAOT/裁剪下更不能依赖反射发现（ITM-317）。
        // Init 幂等，静态构造保证首次使用前恰好执行一次。
        SQLitePCL.Batteries_V2.Init();
    }

    /// <summary>Provider 名称:SQLite。</summary>
    public static string Name => "SQLite";

    /// <summary>SQL 方言标识:<see cref="SqlDialect.Sqlite"/>。</summary>
    public static SqlDialect Dialect => SqlDialect.Sqlite;

    /// <summary>创建 SqliteConnection。静态构造已保证 SQLitePCL bundle 在首次使用前初始化。</summary>
    public static DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    /// <summary>创建连接。SQLite 为进程内嵌入式库,无服务端连接池——
    /// 显式配置池参数时抛 <see cref="NotSupportedException"/>,而非静默忽略。</summary>
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // 以显式标记位判定，而非与 DbOptions 默认值比对——魔法数字随默认值漂移（ITM-315）
        if (options.PoolExplicitlyConfigured)
            throw new NotSupportedException("SQLite Provider 不支持连接池大小、空闲超时或生命周期配置。");
        return new SqliteConnection(connectionString);
    }

    /// <summary>双引号引用标识符(SQL 标准风格),内部双引号以 "" 转义。</summary>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        // ITM-584: NUL 会在驱动/服务端 C 层截断语句——三方言对称拒绝（引用符转义不覆盖 NUL）
        if (identifier.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("标识符不能包含 NUL 字符。", nameof(identifier));
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>schema 与表名分别引用后以点连接;SQLite 中 schema 对应 ATTACH 数据库别名(main/temp/自定义)。</summary>
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    /// <summary>SQLite 3.35+ 支持 RETURNING 子句。</summary>
    public static bool SupportsReturningClause => true;

    /// <summary>SQLite 的 CURRENT_TIMESTAMP 恒为 UTC；MySQL/PG 为会话时区——
    /// 软删除 deleted_at 跨库混用时语义不同（ITM-326）。</summary>
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    /// <summary>SQLite 连接初始化：开启 FK 约束 + WAL 模式。数据库文件被其他进程锁定时受调用方取消/超时约束。</summary>
    public static async Task InitializeConnectionAsync(DbConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>用 PRAGMA table_info 查询列信息,列名位于结果集序号 1。
    /// SQLite 不支持实体 Schema——schema 非空时抛 <see cref="NotSupportedException"/>。</summary>
    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.IsNullOrWhiteSpace(schema))
            throw new NotSupportedException("SQLite Provider 不支持实体 Schema 配置。");
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        return 1;
    }

    /// <summary>参数占位符,形如 @p0。</summary>
    public static string GetParameterPlaceholder(int index) => $"@p{index}";

    /// <summary>创建 SqliteParameter;value 为 null 时转为 <see cref="DBNull.Value"/>(ADO.NET 中 null 参数值不会被发送)。</summary>
    public static DbParameter CreateParameter(string name, object? value)
        => new SqliteParameter(name, value ?? DBNull.Value);

    /// <summary>SQLITE_BUSY (5) / SQLITE_LOCKED (6)——数据库或表被其他连接锁定,属可重试瞬时故障。</summary>
    public static bool IsTransient(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 5 or 6 };

    /// <summary>SQLITE_CONSTRAINT_UNIQUE (2067) / SQLITE_CONSTRAINT_PRIMARYKEY (1555)——
    /// 仅唯一/主键冲突。主码 19 涵盖 NOT NULL(1299)/FK(787) 等全部约束违规，
    /// 按主码判定会把数据完整性错误误报为"记录已存在"（ITM-403）。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 or 1555 };

    /// <summary>批量插入——委托共享多值 INSERT 骨架；SQLite 单语句参数上限 999。</summary>
    public static Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds, CancellationToken ct)
        where T : class, new()
        => MultiValueBulkInsert.ExecuteAsync(
            conn, transaction, entities, batchSize,
            maxParametersPerStatement: 999,
            QuoteIdentifier, CreateParameter, commandTimeoutSeconds, ct);
}
