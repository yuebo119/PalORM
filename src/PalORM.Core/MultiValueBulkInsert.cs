using System.Data.Common;

namespace PalORM;

/// <summary>多值 INSERT 批量骨架——SQLite/MySQL Provider 共享（ITM-304：两份逐字复制已发生
/// 参数钳制漂移，收敛为单一实现）。PG 的 Binary COPY 是真实方言差异，不并入此骨架。
/// <para>职责：binder 参数数 probe 校验 → 按单语句参数上限钳制批次 →
/// 多值 INSERT 分批执行 → 事务生命周期与异常保留清理。</para>
/// <para>命令、回滚或事务释放失败附加到主异常，不替换原始执行失败。</para></summary>
public static class MultiValueBulkInsert
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "表名/列名来自源生成器")]
    public static async Task<long> ExecuteAsync<T>(
        DbConnection conn,
        DbTransaction? transaction,
        IReadOnlyList<T> entities,
        int batchSize,
        int maxParametersPerStatement,
        Func<string, string> quoteIdentifier,
        Func<string, object?, DbParameter> createParameter,
        CancellationToken ct)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(quoteIdentifier);
        ArgumentNullException.ThrowIfNull(createParameter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParametersPerStatement);
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

        if (columnCount > maxParametersPerStatement)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has {columnCount} insert columns, exceeding the " +
                $"{maxParametersPerStatement}-parameter statement limit.");

        int effectiveBatchSize = Math.Min(batchSize, maxParametersPerStatement / columnCount);
        string quotedTable = quoteIdentifier(tableName);
        string quotedColumns = string.Join(", ",
            metadata.InsertColumns.Select(quoteIdentifier));
        long total = 0;

        DbTransaction tran = transaction
            ?? await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        bool ownsTransaction = transaction is null;
        Exception? primaryException = null;
        try
        {
            // 行参数暂存命令在批间复用（原实现每行新建一个 DbCommand，1 万行即 1 万次分配）
            DbCommand rowCommand = conn.CreateCommand();
            Exception? rowCommandException = null;
            try
            {
                for (int start = 0; start < entities.Count; start += effectiveBatchSize)
                {
                    int end = Math.Min(start + effectiveBatchSize, entities.Count);
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
                            rowCommand.Parameters.Clear();
                            binder(rowCommand, entities[start + row]);
                            if (rowCommand.Parameters.Count != columnCount)
                                throw new InvalidOperationException(
                                    $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                                    $"{rowCommand.Parameters.Count} parameters.");

                            for (int column = 0; column < columnCount; column++)
                            {
                                DbParameter source = rowCommand.Parameters[column];
                                cmd.Parameters.Add(createParameter(
                                    $"@p{parameterOffset + column}", source.Value));
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
            if (ownsTransaction)
                await tran.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await TransactionCleanup.RollbackPreservingAsync(tran, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (ownsTransaction)
                await TransactionCleanup.DisposeTransactionPreservingAsync(tran, primaryException,
                    "PalORM.TransactionCleanupException").ConfigureAwait(false);
        }
        return total;
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
}
