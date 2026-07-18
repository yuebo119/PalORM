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

    public static string Name => "SQLite";
    public static SqlDialect Dialect => SqlDialect.Sqlite;

    public static DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // 以显式标记位判定，而非与 DbOptions 默认值比对——魔法数字随默认值漂移（ITM-315）
        if (options.PoolExplicitlyConfigured)
            throw new NotSupportedException("SQLite Provider 不支持连接池大小、空闲超时或生命周期配置。");
        return new SqliteConnection(connectionString);
    }

    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

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

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.IsNullOrWhiteSpace(schema))
            throw new NotSupportedException("SQLite Provider 不支持实体 Schema 配置。");
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        return 1;
    }

    public static string GetParameterPlaceholder(int index) => $"@p{index}";

    public static DbParameter CreateParameter(string name, object? value)
        => new SqliteParameter(name, value ?? DBNull.Value);

    public static bool IsTransient(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 5 or 6 };

    /// <summary>SQLITE_CONSTRAINT (19)——含唯一约束冲突。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 19 };

    /// <summary>批量插入——委托共享多值 INSERT 骨架；SQLite 单语句参数上限 999。</summary>
    public static Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, CancellationToken ct)
        where T : class, new()
        => MultiValueBulkInsert.ExecuteAsync(
            conn, transaction, entities, batchSize,
            maxParametersPerStatement: 999,
            QuoteIdentifier, CreateParameter, ct);
}
