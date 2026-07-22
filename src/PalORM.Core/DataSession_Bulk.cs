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
        return await TProvider.BulkInsertAsync(_conn, GetActiveTransaction(), entities, batchSize,
            _options.CommandTimeoutSeconds, ct).ConfigureAwait(false);
    }

    /// <summary>批量删除——每 500 个生成主键 IN 批次；软删除实体更新 deleted_at，其他实体物理删除。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "批量删除的双路径（软删 UPDATE / 物理 DELETE）+ 事务包装+批次循环是必然复杂度。"
            + "拆分会引入跨方法状态传递，损害可读性。")]
    public async ValueTask<long> BulkDeleteAsync<T>(IReadOnlyList<object> keys, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return 0;
        // v4.0 优化 B：CurrentState 单次快照——替代 3 次独立 Volatile.Read。
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._tableNames.TryGetValue(typeof(T), out string? tableName)
            || !state._pkColumns.TryGetValue(typeof(T), out string? pkCol)
            || !state._bindDelete.TryGetValue(
                typeof(T), out Action<DbCommand, object>? bindKey))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");

        bool isSoftDelete =
            (GetEntityFeatures<T>() & EntityFeatures.SoftDelete) != 0;
        string quotedTable = TProvider.QuoteIdentifier(tableName);
        string quotedPrimaryKey = TProvider.QuoteIdentifier(pkCol);
        // 租户过滤与单条 DeleteAsync 对齐（ITM-404）：跨租户主键命中 0 行
        string tenantFilter = HasTenantFilter<T>()
            ? $" AND {TProvider.QuoteIdentifier("tenant_id")} = {_tenantParameterName}"
            : "";
        const int batchSize = 500;
        long total = 0;
        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction tran = previousTransaction
            ?? await BeginTransactionCoreAsync(
                null, operation.Owner, ct).ConfigureAwait(false);
        bool ownsTransaction = previousTransaction is null;
        Exception? primaryException = null;
        // R10 修复：scratch 命令跨批次复用——替代每批 CreateCommand（对齐 MultiValueBulkInsert rowCommand 模式）
        DbCommand scratch = CreateCommand();
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
                      $"{TProvider.QuoteIdentifier("deleted_at")} IS NULL{tenantFilter}"
                    : $"DELETE FROM {quotedTable} WHERE {predicate}{tenantFilter}";

                // binder 固定产出 @p0——不能直接绑到 cmd 再改名：MySqlConnector 在 Add 时
                // 即拒绝集合内重名（SQLite 容忍瞬时重名掩盖了这点，真库 AOT 实测暴露）。
                // 经暂存命令中转取值，按批内序号重建参数。
                for (int index = 0; index < batchLen; index++)
                {
                    scratch.Parameters.Clear();
                    bindKey(scratch, keys[start + index]);
                    if (scratch.Parameters.Count != 1)
                        throw new InvalidOperationException(
                            $"Type '{typeof(T).Name}' generated an invalid primary-key binder.");

                    cmd.Parameters.Add(TProvider.CreateParameter(
                        placeholders[index], scratch.Parameters[0].Value));
                }
                BindDefaultFilterParameters<T>(cmd);

                total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
            await scratch.DisposeAsync().ConfigureAwait(false);
            _operationState.RestoreTransaction(
                tran, previousTransaction);
            if (ownsTransaction)
                await TransactionCleanup.DisposeTransactionPreservingAsync(tran, primaryException).ConfigureAwait(false);
        }
        return total;
    }

    /// <summary>批量更新。复用源生成 UPDATE 与并发语义，整个输入在同一事务内执行。
    /// <para>ITM-556: [ConcurrencyCheck] 实体的内存 version 回填延迟到事务提交成功后统一执行——
    /// 中途冲突整批回滚时，已成功条目的内存状态与 DB 保持一致，重试不产生假冲突。
    /// 复用外部事务时回填发生在本方法返回前；若调用方随后回滚该外部事务，
    /// 内存 version 需重新查询同步（与单条 UpdateAsync 在外部事务中回滚的既有语义一致）。</para></summary>
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
        List<Action> deferredVersionIncrements = [];
        try
        {
            foreach (T entity in entities)
            {
                total += await UpdateCoreAsync(
                    entity, operation.Owner, ct, deferredVersionIncrements).ConfigureAwait(false);
            }
            if (ownsTransaction)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            foreach (Action increment in deferredVersionIncrements) increment();
            return total;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await TransactionCleanup.RollbackPreservingAsync(transaction, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operationState.RestoreTransaction(
                transaction, previousTransaction);
            if (ownsTransaction)
                await TransactionCleanup.DisposeTransactionPreservingAsync(transaction, primaryException).ConfigureAwait(false);
        }
    }

    /// <summary>批量 Upsert。整个输入在同一事务内执行，复用源生成写入元数据。
    /// <para>ITM-556 注记: 自增 ID 回填随每条 UPSERT 立即发生；中途失败整批回滚时，
    /// 已回填的内存 ID 对应的行不存在于 DB——异常路径下不要继续使用输入实体的 ID，
    /// 重试应重新走 BulkMergeAsync（UPSERT 幂等）。</para></summary>
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
            long affected = 0;
            foreach (T entity in entities)
            {
                await SaveCoreAsync(
                    entity, operation.Owner, ct).ConfigureAwait(false);
                affected++;
            }
            if (ownsTransaction)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            return affected;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await TransactionCleanup.RollbackPreservingAsync(transaction, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operationState.RestoreTransaction(
                transaction, previousTransaction);
            if (ownsTransaction)
                await TransactionCleanup.DisposeTransactionPreservingAsync(transaction, primaryException).ConfigureAwait(false);
        }
    }

    /// <summary>种子数据。要求每个实体具有非默认稳定主键，重复执行按主键更新。</summary>
    public async ValueTask SeedAsync<T>(IEnumerable<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(entities);
        var items = entities.ToList();
        if (items.Count == 0) return;
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        if (items.Any(entity => metadata.HasDefaultKey(entity)))
            throw new InvalidOperationException($"Seed entity '{typeof(T).Name}' requires a non-default stable primary key.");
        await BulkMergeAsync(items, ct).ConfigureAwait(false);
    }
}
