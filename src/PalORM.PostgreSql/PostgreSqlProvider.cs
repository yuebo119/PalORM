using System.Data.Common;
using Npgsql;

namespace PalORM.PostgreSql;

/// <summary>PostgreSQL Provider —— Npgsql 适配 + JSONB/NOTIFY/Binary COPY。</summary>
public sealed class PostgreSqlProvider : IDbProvider
{
    public static string Name => "PostgreSql";
    public static SqlDialect Dialect => SqlDialect.PostgreSql;

    public static DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = options.MaxPoolSize,
            ConnectionIdleLifetime = options.PoolIdleTimeoutSeconds,
            ConnectionLifetime = checked(options.PoolLifetimeMinutes * 60)
        };
        return new NpgsqlConnection(builder.ConnectionString);
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
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    /// <summary>SQLSTATE 23505 unique_violation——唯一约束冲突。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is PostgresException { SqlState: "23505" };

    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_name = @table_name AND table_schema = COALESCE(@table_schema, current_schema())";
        command.Parameters.Add(CreateParameter("@table_name", tableName));
        command.Parameters.Add(CreateParameter("@table_schema", schema));
        return 0;
    }

    public static string GetParameterPlaceholder(int index) => $"@p{index}";

    public static DbParameter CreateParameter(string name, object? value)
        => new NpgsqlParameter(name, value ?? DBNull.Value);

    /// <summary>批量插入——按源生成 InsertColumns 与 BindInsert 执行 Npgsql Binary COPY。
    /// <para>BeginBinaryImportAsync → StartRowAsync → WriteAsync(value, NpgsqlDbType) → CompleteAsync。</para>
    /// <para>列数与参数数在开始 COPY 前校验，无需运行时类型映射。</para>
    /// <para>命令、Importer、回滚或事务释放失败附加到主异常，不替换原始 COPY 失败。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062", Justification = "conn 在外层已有验证")]
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

        if (conn is not NpgsqlConnection npgsqlConnection)
            throw new ArgumentException(
                "PostgreSqlProvider.BulkInsertAsync requires an NpgsqlConnection.", nameof(conn));

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
            await DisposePreservingAsync(probeCommand, probeException,
                "PalORM.ProbeCommandCleanupException").ConfigureAwait(false);
        }

        string quotedColumns = string.Join(", ",
            metadata.InsertColumns.Select(QuoteIdentifier));
        string quotedTable = QuoteIdentifier(tableName);
        long total = 0;
        DbTransaction bulkTransaction = transaction
            ?? await npgsqlConnection.BeginTransactionAsync(ct).ConfigureAwait(false);
        bool ownsTransaction = transaction is null;
        Exception? primaryException = null;

        try
        {
            for (int start = 0; start < entities.Count; start += batchSize)
            {
                int end = Math.Min(start + batchSize, entities.Count);
                NpgsqlBinaryImporter importer = await npgsqlConnection.BeginBinaryImportAsync(
                    $"COPY {quotedTable} ({quotedColumns}) FROM STDIN (FORMAT BINARY)", ct)
                    .ConfigureAwait(false);
                Exception? importerException = null;
                try
                {
                    DbCommand rowCommand = conn.CreateCommand();
                    Exception? rowCommandException = null;
                    try
                    {
                        for (int index = start; index < end; index++)
                        {
                            rowCommand.Parameters.Clear();
                            binder(rowCommand, entities[index]);
                            if (rowCommand.Parameters.Count != columnCount)
                                throw new InvalidOperationException(
                                    $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                                    $"{rowCommand.Parameters.Count} parameters.");

                            await importer.StartRowAsync(ct).ConfigureAwait(false);
                            for (int parameterIndex = 0;
                                 parameterIndex < columnCount;
                                 parameterIndex++)
                            {
                                var parameter = (NpgsqlParameter)
                                    rowCommand.Parameters[parameterIndex];
                                object? value = parameter.Value is DBNull
                                    ? null
                                    : parameter.Value;
                                await importer.WriteAsync(
                                    value, parameter.NpgsqlDbType, ct).ConfigureAwait(false);
                            }
                            total++;
                        }
                        await importer.CompleteAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        rowCommandException = exception;
                        throw;
                    }
                    finally
                    {
                        await DisposePreservingAsync(rowCommand, rowCommandException,
                            "PalORM.RowCommandCleanupException").ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    importerException = exception;
                    throw;
                }
                finally
                {
                    await DisposePreservingAsync(importer, importerException,
                        "PalORM.ImporterCleanupException").ConfigureAwait(false);
                }
            }

            if (ownsTransaction)
                await bulkTransaction.CommitAsync(ct).ConfigureAwait(false);
            return total;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await RollbackPreservingAsync(bulkTransaction, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (ownsTransaction)
                await DisposePreservingAsync(bulkTransaction, primaryException,
                    "PalORM.TransactionCleanupException").ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "回滚是清理路径；异常附加到主异常，不能替换原始 COPY 失败。")]
    private static async ValueTask RollbackPreservingAsync(DbTransaction transaction, Exception primaryException)
    {
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception rollbackException) { primaryException.Data["PalORM.RollbackException"] = rollbackException; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "释放是清理路径；异常附加到主异常，不能替换原始 COPY 失败。")]
    private static async ValueTask DisposePreservingAsync(
        IAsyncDisposable resource,
        Exception? primaryException,
        string exceptionDataKey)
    {
        try { await resource.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data[exceptionDataKey] = cleanupException;
        }
    }
}
