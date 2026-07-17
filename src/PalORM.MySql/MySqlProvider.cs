using System.Data.Common;
using MySqlConnector;

namespace PalORM.MySql;

/// <summary>MySQL Provider —— MySqlConnector 适配。</summary>
public sealed class MySqlProvider : IDbProvider
{
    public static string Name => "MySql";
    public static char ParameterPrefix => '@';
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

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.CommandText = $"SHOW COLUMNS FROM {QuoteQualifiedIdentifier(schema, tableName)}";
        return 0;
    }

    public static string GetParameterPlaceholder(int index) => $"@p{index}";

    public static DbParameter CreateParameter(string name, object? value)
        => new MySqlParameter(name, value ?? DBNull.Value);

    /// <summary>批量插入——按源生成 InsertColumns 与 BindInsert 构造多值 INSERT。
    /// <para>列数与参数数在执行前确定性校验，整个输入复用同一事务。</para>
    /// <para>命令、回滚或事务释放失败附加到主异常，不替换原始执行失败。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062", Justification = "entities 由 DataSession 保证非 null")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "表名/列名来自源生成器")]
    public static async Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, CancellationToken ct)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (entities.Count == 0) return 0;
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName)
            || metadata.InsertColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated insert metadata.");

        Action<DbCommand, object> binder = metadata.BindInsert;
        int columnCount = metadata.InsertColumns.Count;
        DbCommand probeCommand = conn.CreateCommand();
        Exception? probeException = null;
        try
        {
            binder(probeCommand, entities[0]);
            if (probeCommand.Parameters.Count != columnCount)
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                    $"{probeCommand.Parameters.Count} parameters.");
        }
        catch (Exception exception)
        {
            probeException = exception;
            throw;
        }
        finally
        {
            await DisposeCommandPreservingAsync(probeCommand, probeException,
                "PalORM.ProbeCommandCleanupException").ConfigureAwait(false);
        }

        string quotedTable = QuoteIdentifier(tableName);
        string quotedColumns = string.Join(", ",
            metadata.InsertColumns.Select(QuoteIdentifier));
        long total = 0;

        DbTransaction tran = transaction
            ?? await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        bool ownsTransaction = transaction is null;
        Exception? primaryException = null;
        try
        {
            for (int start = 0; start < entities.Count; start += batchSize)
            {
                int end = Math.Min(start + batchSize, entities.Count);
                int batchLength = end - start;
                var rowPlaceholders = new string[batchLength];
                for (int row = 0; row < batchLength; row++)
                {
                    int parameterOffset = row * columnCount;
                    rowPlaceholders[row] = "(" + string.Join(", ",
                        Enumerable.Range(0, columnCount)
                            .Select(column => $"@p{parameterOffset + column}")) + ")";
                }

                DbCommand cmd = conn.CreateCommand();
                Exception? commandException = null;
                try
                {
                    cmd.Transaction = tran;
                    cmd.CommandText =
                        $"INSERT INTO {quotedTable} ({quotedColumns}) VALUES " +
                        string.Join(", ", rowPlaceholders);

                    for (int row = 0; row < batchLength; row++)
                    {
                        int parameterOffset = row * columnCount;
                        DbCommand rowCommand = conn.CreateCommand();
                        Exception? rowCommandException = null;
                        try
                        {
                            binder(rowCommand, entities[start + row]);
                            if (rowCommand.Parameters.Count != columnCount)
                                throw new InvalidOperationException(
                                    $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                                    $"{rowCommand.Parameters.Count} parameters.");

                            for (int column = 0; column < columnCount; column++)
                            {
                                DbParameter source = rowCommand.Parameters[column];
                                cmd.Parameters.Add(CreateParameter(
                                    $"@p{parameterOffset + column}", source.Value));
                            }
                        }
                        catch (Exception exception)
                        {
                            rowCommandException = exception;
                            throw;
                        }
                        finally
                        {
                            await DisposeCommandPreservingAsync(rowCommand, rowCommandException,
                                "PalORM.RowCommandCleanupException").ConfigureAwait(false);
                        }
                    }

                    total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    commandException = exception;
                    throw;
                }
                finally
                {
                    await DisposeCommandPreservingAsync(cmd, commandException,
                        "PalORM.CommandCleanupException").ConfigureAwait(false);
                }
            }
            if (ownsTransaction)
                await tran.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await RollbackPreservingAsync(tran, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (ownsTransaction)
                await DisposeTransactionPreservingAsync(tran, primaryException,
                    "PalORM.TransactionCleanupException").ConfigureAwait(false);
        }
        return total;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "回滚是清理路径；异常附加到主异常，不能替换原始执行失败。")]
    private static async ValueTask RollbackPreservingAsync(DbTransaction transaction, Exception primaryException)
    {
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception rollbackException) { primaryException.Data["PalORM.RollbackException"] = rollbackException; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "释放是清理路径；异常附加到主异常，不能替换原始批量写失败。")]
    private static async ValueTask DisposeCommandPreservingAsync(
        DbCommand command,
        Exception? primaryException,
        string exceptionDataKey)
    {
        try { await command.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data[exceptionDataKey] = cleanupException;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "释放是清理路径；异常附加到主异常，不能替换原始批量写失败。")]
    private static async ValueTask DisposeTransactionPreservingAsync(
        DbTransaction transaction,
        Exception? primaryException,
        string exceptionDataKey)
    {
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data[exceptionDataKey] = cleanupException;
        }
    }
}
