using System.Data.Common;
using System.Text;

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
        // r11.5-D3（ITM-637 同型第六处）：元数据检查先于空列表短路——会话层短路使
        // Provider 层（r4 批次已修）的三方言一致性检查对空列表不可达
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out _)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out _))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated insert metadata.");
        if (entities.Count == 0) return 0;
        return await TProvider.BulkInsertAsync(_conn, GetActiveTransaction(), entities, batchSize,
            _options.CommandTimeoutSeconds, ct, _isolationLevel).ConfigureAwait(false);  // r6-N2
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
        return await ExecuteBulkUpdateRowByRowAsync<T>(entities, operation.Owner, ct).ConfigureAwait(false);
    }

    /// <summary>BulkUpdate 逐条核心逻辑（不含 EnterOperation）——供 BulkUpdateAsync 和 BulkUpdateBatchAsync SQLite 回退复用。</summary>
    private async ValueTask<long> ExecuteBulkUpdateRowByRowAsync<T>(
        IReadOnlyList<T> entities, object? operationOwner, CancellationToken ct)
        where T : class, new()
    {
        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction transaction = previousTransaction
            ?? await BeginTransactionCoreAsync(null, operationOwner, ct).ConfigureAwait(false);
        bool ownsTransaction = previousTransaction is null;
        long total = 0;
        Exception? primaryException = null;
        List<Action> deferredVersionIncrements = [];
        try
        {
            foreach (T entity in entities)
            {
                total += await UpdateCoreAsync(
                    entity, operationOwner, ct, deferredVersionIncrements).ConfigureAwait(false);
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
            _operationState.RestoreTransaction(transaction, previousTransaction);
            if (ownsTransaction)
                await TransactionCleanup.DisposeTransactionPreservingAsync(transaction, primaryException).ConfigureAwait(false);
        }
    }

    /// <summary>v5.0 阶段 4.3b：批量更新（单语句批量 UPDATE，方案 Y 严格版）。
    /// <para><b>与 <see cref="BulkUpdateAsync{T}"/> 的差异</b>：本方法走批量 SQL
    /// （PG: UPDATE FROM VALUES；MySQL: CASE WHEN），单次 RTT 完成 N 行更新。</para>
    /// <para><b>SQLite 方言感知回退</b>：SQLite 的 CASE WHEN 大批量比逐条慢 6.4x（SQL 解析开销），
    /// 实测验证 SQLite 逐条 UPDATE 已是最优路径。SQLite 调用本方法自动回退到
    /// <see cref="BulkUpdateAsync{T}"/>（逐条 + 乐观锁语义保留），调用方无需感知。</para>
    /// <para><b>乐观锁不支持</b>：带 <c>[ConcurrencyCheck]</c> 的实体调用本方法抛
    /// <see cref="NotSupportedException"/>（PG/MySQL 路径）；SQLite 回退到 BulkUpdateAsync 则支持。</para>
    /// <para><b>输入不可变约束</b>：调用方在方法返回前不得修改 <paramref name="entities"/> 集合
    /// （与 BulkInsertAsync / BulkUpdateAsync 的 IReadOnlyList&lt;T&gt; 契约一致）。</para>
    /// <para><b>租户过滤</b>：自动追加 <c>AND tenant_id = @p</c>（与 BulkUpdateAsync 对齐）。</para>
    /// <para><b>参数上限</b>：按驱动上限分批执行（PG/MySQL 65535，SQLite 999），物理约束非性能阈值。</para></summary>
    public async ValueTask<long> BulkUpdateBatchAsync<T>(
        IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        if (!PalORM_Runtime.CurrentState._crudMetadatas.TryGetValue(typeof(T), out _))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated CRUD.");
        // r12-B1（D3 残留族）：空短路后置（三方言/两路径统一口径——SQLite 回退路径同样
        // 不应使未注册类型+空列表静默成功）
        if (entities.Count == 0) return 0;

        // v5.0 SQLite 方言感知回退：CASE WHEN 在 SQLite 上比逐条慢 6.4x（实测验证）。
        // 直接调内部逐条路径（不复用 BulkUpdateAsync 的 EnterOperation——会双重锁定）。
        if (TProvider.Dialect == SqlDialect.Sqlite)
            return await ExecuteBulkUpdateRowByRowAsync(entities, operation.Owner, ct).ConfigureAwait(false);

        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata)
            || !state._tableNames.TryGetValue(typeof(T), out string? tableName))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        // 乐观锁实体拒绝——批量 UPDATE 无法表达"每行 version 匹配"语义。
        if (metadata.IncrementVersion is not null)
            throw new NotSupportedException(
                $"BulkUpdateBatchAsync cannot honor [ConcurrencyCheck] on '{typeof(T).Name}'; " +
                "batch UPDATE cannot express per-row version matching. " +
                "Use BulkUpdateAsync (row-by-row with version check) instead.");

        // 准备批量上下文：SET 列集、引号包裹标识符、租户过滤标记。
        BatchUpdateContext ctx = PrepareBatchUpdateContext<T>(state, metadata, tableName, entities[0]);
        // ITM-640：SQLite 已在上方回退逐条路径，此处恒非 SQLite——原三元的 999 分支不可达。
        const int driverLimit = 65535;
        int tenantParams = ctx.HasTenantFilter ? 1 : 0;
        int rowsPerBatch = Math.Max(1, (driverLimit - tenantParams) / (ctx.SetColumnCount + 1));

        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction tran = previousTransaction
            ?? await BeginTransactionCoreAsync(null, operation.Owner, ct).ConfigureAwait(false);
        bool ownsTransaction = previousTransaction is null;
        Exception? primaryException = null;
        try
        {
            long totalAffected = 0;
            for (int batchStart = 0; batchStart < entities.Count; batchStart += rowsPerBatch)
            {
                int batchEnd = Math.Min(batchStart + rowsPerBatch, entities.Count);
                totalAffected += await ExecuteBatchUpdateAsync(
                    entities, batchStart, batchEnd, metadata, ctx, tran, ct).ConfigureAwait(false);
            }
            if (ownsTransaction)
                await tran.CommitAsync(ct).ConfigureAwait(false);
            return totalAffected;
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
            _operationState.RestoreTransaction(tran, previousTransaction);
            if (ownsTransaction)
                await TransactionCleanup.DisposeTransactionPreservingAsync(tran, primaryException).ConfigureAwait(false);
        }
    }

    /// <summary>准备批量 UPDATE 上下文：SET 列集取自 CrudMetadata 真源、引号包裹、租户过滤。
    /// probe 命令验证 BindUpdate 参数序与元数据列集一致（ITM-642——原实现解析生成 SQL 文本
    /// 反解列名，含逗号标识符被 Split(',') 错切、表名内含 " SET " 亦会误判）。</summary>
    private BatchUpdateContext PrepareBatchUpdateContext<T>(
        PalORM_Runtime.RuntimeRegistryState state, CrudMetadata metadata,
        string tableName, T firstEntity)
        where T : class, new()
    {
        // ITM-642：SET 列集直接消费生成器发射的 UpdateColumns（与 BuildUpdateSql/BindUpdate
        // 同源同序，ITM-552 单一谓词）——不再解析生成 SQL 文本反解列名。
        int setColumnCount = metadata.UpdateColumns.Count;
        if (setColumnCount <= 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no updatable columns.");
        // probe 提取参数总数，作为生成器三处（SQL/Bind/元数据）漂移的运行时哨兵。
        using DbCommand probe = CreateCommand();
        metadata.BindUpdate(probe, firstEntity);
        int totalParams = probe.Parameters.Count;
        if (totalParams != setColumnCount + 1)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' BindUpdate produced {totalParams} parameters but metadata " +
                $"declares {setColumnCount} update columns (+1 primary key). Recompile the model assembly.");
        string[] setColumns = metadata.UpdateColumns
            .Select(TProvider.QuoteIdentifier).ToArray();
        string quotedTable = TProvider.QuoteIdentifier(tableName);
        string quotedPk = state._pkColumns.TryGetValue(typeof(T), out string? pkCol)
            ? TProvider.QuoteIdentifier(pkCol) : "\"id\"";
        return new BatchUpdateContext(setColumns, quotedTable, quotedPk, HasTenantFilter<T>());
    }

    /// <summary>执行单批 UPDATE（构造 SQL + 绑定参数 + 执行）。</summary>
    private async ValueTask<long> ExecuteBatchUpdateAsync<T>(
        IReadOnlyList<T> entities, int batchStart, int batchEnd,
        CrudMetadata metadata, BatchUpdateContext ctx,
        DbTransaction tran, CancellationToken ct)
        where T : class, new()
    {
        int batchLen = batchEnd - batchStart;
        int paramsPerRow = ctx.SetColumnCount + 1;

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        cmd.CommandText = BatchUpdateSqlBuilder.Build(
            TProvider.Dialect, ctx.QuotedTable, ctx.QuotedPk, ctx.SetColumns,
            batchLen, ctx.HasTenantFilter, _tenantParameterName);

        // probe 复用：逐行绑定提取参数值
        await using DbCommand probe = CreateCommand();
        for (int i = batchStart; i < batchEnd; i++)
        {
            probe.Parameters.Clear();
            metadata.BindUpdate(probe, entities[i]);
            for (int c = 0; c <= ctx.SetColumnCount; c++)
            {
                int globalIdx = (i - batchStart) * paramsPerRow + c;
                cmd.Parameters.Add(TProvider.CreateParameter($"@p{globalIdx}", probe.Parameters[c].Value));
            }
        }
        if (ctx.HasTenantFilter)
            cmd.Parameters.Add(TProvider.CreateParameter(_tenantParameterName, _tenantId));

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>批量 UPDATE 上下文（避免方法参数过多 S107）。</summary>
    private sealed record BatchUpdateContext(
        string[] SetColumns, string QuotedTable, string QuotedPk, bool HasTenantFilter)
    {
        public int SetColumnCount => SetColumns.Length;
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
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        // r12-B1（D3 残留族）：空短路后置——同 BulkInsertAsync 口径
        if (items.Count == 0) return;
        if (items.Any(entity => metadata.HasDefaultKey(entity)))
            throw new InvalidOperationException($"Seed entity '{typeof(T).Name}' requires a non-default stable primary key.");
        await BulkMergeAsync(items, ct).ConfigureAwait(false);
    }
}
