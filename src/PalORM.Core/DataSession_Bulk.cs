using System.Data.Common;

namespace PalORM;

// Bulk operations (partial class — 从 DataSession.cs 拆分)
public partial class DataSession<TProvider>
{
    /// <summary>批量插入——委托 Provider 使用源生成 InsertColumns 与 binder，并复用会话事务。</summary>
    public async ValueTask<long> BulkInsertAsync<T>(IReadOnlyList<T> entities, int batchSize = 1000, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (entities.Count == 0) return 0;
        return await TProvider.BulkInsertAsync(_conn, GetActiveTransaction(), entities, batchSize, ct).ConfigureAwait(false);
    }

    /// <summary>批量删除——每 500 个生成主键 IN 批次；软删除实体更新 deleted_at，其他实体物理删除。</summary>
    public async ValueTask<long> BulkDeleteAsync<T>(IReadOnlyList<object> keys, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return 0;
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName)
            || !PalORM_Runtime.PkColumns.TryGetValue(typeof(T), out string? pkCol)
            || !PalORM_Runtime.BindDelete.TryGetValue(
                typeof(T), out Action<DbCommand, object>? bindKey))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");

        bool isSoftDelete =
            (GetEntityFeatures<T>() & EntityFeatures.SoftDelete) != 0;
        string quotedTable = TProvider.QuoteIdentifier(tableName);
        string quotedPrimaryKey = TProvider.QuoteIdentifier(pkCol);
        const int batchSize = 500;
        long total = 0;
        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction tran = previousTransaction
            ?? await BeginTransactionCoreAsync(
                null, operation.Owner, ct).ConfigureAwait(false);
        bool ownsTransaction = previousTransaction is null;
        Exception? primaryException = null;
        try
        {
            for (int start = 0; start < keys.Count; start += batchSize)
            {
                int end = Math.Min(start + batchSize, keys.Count);
                int batchLen = end - start;
                var placeholders = new string[batchLen];
                for (int index = 0; index < batchLen; index++)
                    placeholders[index] = TProvider.GetParameterPlaceholder(index);

                await using DbCommand cmd = CreateCommand();
                cmd.Transaction = tran;
                string predicate =
                    $"{quotedPrimaryKey} IN ({string.Join(", ", placeholders)})";
                cmd.CommandText = isSoftDelete
                    ? $"UPDATE {quotedTable} SET {TProvider.QuoteIdentifier("deleted_at")} = " +
                      $"{TProvider.CurrentTimestampExpression} WHERE {predicate} AND " +
                      $"{TProvider.QuoteIdentifier("deleted_at")} IS NULL"
                    : $"DELETE FROM {quotedTable} WHERE {predicate}";

                for (int index = 0; index < batchLen; index++)
                {
                    int parameterCount = cmd.Parameters.Count;
                    bindKey(cmd, keys[start + index]);
                    if (cmd.Parameters.Count != parameterCount + 1)
                        throw new InvalidOperationException(
                            $"Type '{typeof(T).Name}' generated an invalid primary-key binder.");

                    cmd.Parameters[parameterCount].ParameterName = placeholders[index];
                }

                total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
            _operationState.RestoreTransaction(
                tran, previousTransaction);
            if (ownsTransaction)
                await DisposeTransactionPreservingAsync(tran, primaryException).ConfigureAwait(false);
        }
        return total;
    }

    /// <summary>批量更新。复用源生成 UPDATE 与并发语义，整个输入在同一事务内执行。</summary>
    public async ValueTask<long> BulkUpdateAsync<T>(IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0) return 0;

        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction transaction = previousTransaction
            ?? await BeginTransactionCoreAsync(
                null, operation.Owner, ct).ConfigureAwait(false);
        bool ownsTransaction = previousTransaction is null;
        long total = 0;
        Exception? primaryException = null;
        try
        {
            foreach (T entity in entities)
            {
                total += await UpdateCoreAsync(
                    entity, operation.Owner, ct).ConfigureAwait(false);
            }
            if (ownsTransaction)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            return total;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await RollbackPreservingAsync(transaction, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operationState.RestoreTransaction(
                transaction, previousTransaction);
            if (ownsTransaction)
                await DisposeTransactionPreservingAsync(transaction, primaryException).ConfigureAwait(false);
        }
    }

    /// <summary>批量 Upsert。整个输入在同一事务内执行，复用源生成写入元数据。</summary>
    public async ValueTask<long> BulkMergeAsync<T>(IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0) return 0;

        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction transaction = previousTransaction
            ?? await BeginTransactionCoreAsync(
                null, operation.Owner, ct).ConfigureAwait(false);
        bool ownsTransaction = previousTransaction is null;
        Exception? primaryException = null;
        try
        {
            foreach (T entity in entities)
            {
                await SaveCoreAsync(
                    entity, operation.Owner, ct).ConfigureAwait(false);
            }
            if (ownsTransaction)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            return entities.Count;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await RollbackPreservingAsync(transaction, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operationState.RestoreTransaction(
                transaction, previousTransaction);
            if (ownsTransaction)
                await DisposeTransactionPreservingAsync(transaction, primaryException).ConfigureAwait(false);
        }
    }

    /// <summary>种子数据。要求每个实体具有非默认稳定主键，重复执行按主键更新。</summary>
    public async ValueTask SeedAsync<T>(IEnumerable<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entities);
        List<T> items = entities.ToList();
        if (items.Count == 0) return;
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        if (items.Any(entity => metadata.HasDefaultKey(entity)))
            throw new InvalidOperationException($"Seed entity '{typeof(T).Name}' requires a non-default stable primary key.");
        await BulkMergeAsync(items, ct).ConfigureAwait(false);
    }
}
