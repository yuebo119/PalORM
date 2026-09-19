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
        // v4.0 优化 B：CurrentState 单次快照——替代 3 次独立 Volatile.Read。
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._tableNames.TryGetValue(typeof(T), out string? tableName)
            || !state._pkColumns.TryGetValue(typeof(T), out string? pkCol)
            || !state._bindDelete.TryGetValue(
                typeof(T), out Action<DbCommand, object>? bindKey))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        // r13-S1（r12 声称未交付第四例——本批真交付）：空短路后置同族口径
        if (keys.Count == 0) return 0;

        bool isSoftDelete =
            (GetEntityFeatures<T>() & EntityFeatures.SoftDelete) != 0;
        string quotedTable = TProvider.QuoteIdentifier(tableName);
        string quotedPrimaryKey = TProvider.QuoteIdentifier(pkCol);
        // 租户过滤与单条 DeleteAsync 对齐（ITM-404）：跨租户主键命中 0 行
        string tenantFilter = HasTenantFilter<T>()
            ? $" AND {TProvider.QuoteIdentifier("tenant_id")} = {_tenantParameterName}"
            : "";
        const int batchSize = 500;
        // v5.4 精炼 L1：事务骨架（复用/自开→commit/rollback→Restore→释放）收敛至
        // RunInTransactionScopeAsync 单点。
        return await RunInTransactionScopeAsync(
            operation.Owner,
            async (tran, token) =>
            {
                // ITM-676 等价保持：scratch 在作用域内创建——创建失败时内核 finally
                // 仍执行 Restore+事务释放；await using 覆盖批间清理。
                // R10：scratch 跨批次复用（对齐 MultiValueBulkInsert rowCommand 模式）。
                await using DbCommand scratch = CreateCommand();
                long total = 0;
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

                    total += await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                return total;
            },
            ct).ConfigureAwait(false);
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
        // r15-DB1（D3 族第五侧一致性收口——同四侧口径）
        if (!PalORM_Runtime.CurrentState._crudMetadatas.TryGetValue(typeof(T), out _))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated CRUD.");
        if (entities.Count == 0) return 0;
        return await ExecuteBulkUpdateRowByRowAsync<T>(entities, operation.Owner, ct).ConfigureAwait(false);
    }

    /// <summary>BulkUpdate 逐条核心逻辑（不含 EnterOperation）——供 BulkUpdateAsync 和 BulkUpdateBatchAsync SQLite 回退复用。
    /// v5.4 精炼 L1：事务骨架收敛至 RunInTransactionScopeAsync。</summary>
    private async ValueTask<long> ExecuteBulkUpdateRowByRowAsync<T>(
        IReadOnlyList<T> entities, object? operationOwner, CancellationToken ct)
        where T : class, new()
    {
        var (total, deferredVersionIncrements) = await RunInTransactionScopeAsync(
            operationOwner,
            async (transaction, token) =>
            {
                long total = 0;
                List<Action> increments = [];
                foreach (T entity in entities)
                {
                    total += await UpdateCoreAsync(
                        entity, operationOwner, token, increments).ConfigureAwait(false);
                }
                return (total, increments);
            },
            ct).ConfigureAwait(false);

        // ITM-556：内存 version 回填在提交成功后统一执行——中途冲突整批回滚时，
        // 已成功条目的内存状态与 DB 保持一致，重试不产生假冲突。置于内核之外：
        // 仅成功提交路径可达此处（复用外部事务时回填发生在本方法返回前，
        // 调用方随后回滚该外部事务的既有语义不变）。
        foreach (Action increment in deferredVersionIncrements) increment();
        return total;
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
    /// <para><b>参数上限</b>：按驱动上限分批执行（PG/MySQL 65535；SQLite 走上文逐条回退路径、不分批）。物理约束非性能阈值。</para></summary>
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

        // v5.4 精炼 L1：事务骨架收敛至 RunInTransactionScopeAsync。
        return await RunInTransactionScopeAsync(
            operation.Owner,
            async (tran, token) =>
            {
                long totalAffected = 0;
                for (int batchStart = 0; batchStart < entities.Count; batchStart += rowsPerBatch)
                {
                    int batchEnd = Math.Min(batchStart + rowsPerBatch, entities.Count);
                    totalAffected += await ExecuteBatchUpdateAsync(
                        entities, batchStart, batchEnd, metadata, ctx, tran, token).ConfigureAwait(false);
                }
                return totalAffected;
            },
            ct).ConfigureAwait(false);
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
        // ITM-792(r21) 订正：本方法为同步（BindUpdate 无 IO）——await using 不适用，
        // 用 try/finally 显式 Dispose 保证异常路径释放（与 peer 的异步释放语义等价）。
        DbCommand probe = CreateCommand();
        try
        {
            metadata.BindUpdate(probe, firstEntity);
            int totalParams = probe.Parameters.Count;
            if (totalParams != setColumnCount + 1)
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' BindUpdate produced {totalParams} parameters but metadata " +
                    $"declares {setColumnCount} update columns (+1 primary key). Recompile the model assembly.");
            string[] setColumns = metadata.UpdateColumns
                .Select(TProvider.QuoteIdentifier).ToArray();
            string quotedTable = TProvider.QuoteIdentifier(tableName);
            // r19/ITM-684：缺 PkColumns 不再静默回退主键 "id"——错列更新比明确失败更危险。
            // Register 的必填键校验使此分支经公共 API 不可达（防御纵深），但与 GetPkColumn/
            // ITM-672 缺键拒绝族保持同口径：旧生成器/手工片段必须显式失败。
            string quotedPk = state._pkColumns.TryGetValue(typeof(T), out string? pkCol)
                ? TProvider.QuoteIdentifier(pkCol)
                : throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' has no generated primary key metadata; " +
                    "recompile the model assembly against the current PalORM source generator.");
            return new BatchUpdateContext(setColumns, quotedTable, quotedPk, HasTenantFilter<T>());
        }
        finally
        {
            probe.Dispose();
        }
    }

    /// <summary>执行单批 UPDATE（构造 SQL + 绑定参数 + 执行）。
    /// <para><b>v5.6 参数池</b>：目标命令的参数对象一次建好（<c>@p0…@p{n-1}</c> 按行递增，
    /// SET 列在前、主键在每行末尾，租户参数固定名追加末尾），逐行只写 <c>Value</c>。
    /// 取值优先走生成器新发射的 <see cref="CrudMetadata.BindUpdateValues"/>（零 CreateParameter）
    /// ——原先经 probe 命令逐行 <c>BindUpdate</c> 建参数，40000 行 × 4 列即 16 万次创建，
    /// 是真库实测 2671 B/行（PG）的主项。旧版模型程序集该绑定器为 null 时回退 probe 路径，
    /// 参数创建量回到改前水平但语义不变。</para>
    /// <para>参数名与顺序与 <see cref="BatchUpdateSqlBuilder"/> 的占位符逐位对应，由
    /// <c>BatchUpdateParameterContractTests</c> 锁定跨 Provider 契约。</para></summary>
    private async ValueTask<long> ExecuteBatchUpdateAsync<T>(
        IReadOnlyList<T> entities, int batchStart, int batchEnd,
        CrudMetadata metadata, BatchUpdateContext ctx,
        DbTransaction tran, CancellationToken ct)
        where T : class, new()
    {
        int batchLen = batchEnd - batchStart;
        int paramsPerRow = ctx.SetColumnCount + 1;
        int rowParamCount = batchLen * paramsPerRow;

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        cmd.CommandText = BatchUpdateSqlBuilder.Build(
            TProvider.Dialect, ctx.QuotedTable, ctx.QuotedPk, ctx.SetColumns,
            batchLen, ctx.HasTenantFilter, _tenantParameterName);

        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterPool(
            cmd, rowParamCount, ctx.HasTenantFilter, _tenantParameterName, _tenantId,
            TProvider.CreateParameter);

        Action<DbParameter[], object, int>? valuesBinder = metadata.BindUpdateValues;
        if (valuesBinder is not null)
        {
            for (int i = batchStart; i < batchEnd; i++)
                valuesBinder(pool, entities[i], (i - batchStart) * paramsPerRow);
        }
        else
        {
            // 旧版生成器模型程序集：无零分配绑定器，退回 probe 取值再写进池
            await using DbCommand probe = CreateCommand();
            for (int i = batchStart; i < batchEnd; i++)
            {
                probe.Parameters.Clear();
                metadata.BindUpdate(probe, entities[i]);
                int baseIndex = (i - batchStart) * paramsPerRow;
                for (int c = 0; c < paramsPerRow; c++)
                    pool[baseIndex + c].Value = probe.Parameters[c].Value;
            }
        }

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
        // r15-DB2（第六侧：公共 API 直调路径——Seed 侧已有自身入口保护）
        if (!PalORM_Runtime.CurrentState._crudMetadatas.TryGetValue(typeof(T), out _))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated CRUD.");
        if (entities.Count == 0) return 0;

        // v5.7 集合化：按键状态分区——默认键行走逐条 INSERT（保留 ID 回填契约，
        // InsertCoreAsync 的物化/回填无法在多值形态下按行还原）；非默认键行
        // （bulk-merge 的主流场景：既有键的重复执行更新）改为**多行 UPSERT**，
        // 每批一条语句。原实现 N 行 = N 次往返（每行一次 SaveCoreAsync），
        // 10K 行在远程库（RTT ~1ms）约 10s，集合化后约 12 条语句（900 参数/批上限）。
        // 单行语义保持：ON CONFLICT/ON DUPLICATE KEY 与 SaveCoreAsync 的单行
        // upsert 用同一谓词列集（UpsertColumns，含 PK）；[ConcurrencyCheck] 实体
        // 维持逐条路径——SaveCoreAsync 会以同消息拒绝（UPSERT 无法尊重乐观锁，ITM-503）。
        PalORM_Runtime.RuntimeRegistryState mergeState = PalORM_Runtime.CurrentState;
        if (!mergeState._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata mergeMetadata))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        bool rowByRow = mergeMetadata.IncrementVersion is not null
            || mergeMetadata.UpsertColumns.Count == 0;

        return await RunInTransactionScopeAsync(
            operation.Owner,
            async (transaction, token) =>
            {
                long affected = 0;
                if (rowByRow)
                {
                    foreach (T entity in entities)
                    {
                        await SaveCoreAsync(entity, operation.Owner, token).ConfigureAwait(false);
                        affected++;
                    }
                    return affected;
                }

                List<T> upsertBatch = new(entities.Count);
                foreach (T entity in entities)
                {
                    if (mergeMetadata.HasDefaultKey(entity))
                    {
                        await SaveCoreAsync(entity, operation.Owner, token).ConfigureAwait(false);
                        affected++;
                    }
                    else
                    {
                        upsertBatch.Add(entity);
                    }
                }

                affected += await BatchUpsertAsync(
                    transaction, upsertBatch, mergeMetadata, token).ConfigureAwait(false);
                return affected;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>多行 UPSERT 分批执行——PG/SQLite 走 ON CONFLICT (...) DO UPDATE SET c=excluded.c，
    /// MySQL 走 ON DUPLICATE KEY UPDATE c=VALUES(c)（与单行 upsert 的既有 SQL 形态一致）。
    /// <para><b>批次上限</b>：每语句参数总数钳制在 900（SQLite 默认变量上限 999 的安全余量；
    /// PG/MySQL 上限更高但不依赖方言探测——统一保守值，批数已足够少）。</para>
    /// <para><b>批内重复主键的语义边界（如实登记）</b>：单行逐条形态下后行静默覆盖前行
    /// （last-wins）。集合化后 MySQL 保持 last-wins（ON DUPLICATE KEY 天然如此）；
    /// PG/SQLite 对同一语句内影响同一行会报错（PG: "cannot affect row a second time"）。
    /// 旧行为的 last-wins 依赖执行顺序，属于未定义边界的巧合而非契约——集合化把它
    /// 变成明确失败。需要确定性 last-wins 时请先按主键去重再调用。</para>
    /// <para><b>返回值</b>：与原实现一致，返回处理的行数（不依赖驱动的 affectedRows——
    /// MySQL ON DUPLICATE KEY 的 affectedRows 对 insert/update 取值不同，不可比）。</para></summary>
    private async Task<long> BatchUpsertAsync<T>(
        DbTransaction transaction,
        List<T> entities,
        CrudMetadata metadata,
        CancellationToken ct) where T : class, new()
    {
        if (entities.Count == 0) return 0;

        int columnCount = metadata.UpsertColumns.Count;
        const int maxParametersPerStatement = 900;
        int batchSize = Math.Max(1, maxParametersPerStatement / columnCount);

        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        string tableName = state._tableNames[typeof(T)];
        if (!state._pkColumns.TryGetValue(typeof(T), out string? pkColumn) || pkColumn is null)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no primary key column; set-based upsert requires one.");
        UpsertSqlShape shape = BuildUpsertSqlShape(tableName, metadata, pkColumn);

        // 绑定策略：BindUpsert 每行从 @p0 起命名且 MySQL 参数集合在 Add 时校验重名——
        // 不能直接往批命令里逐行 Append。改为 scratch 命令绑定 → 值拷贝进预建参数池
        // （池参数以批内连续下标命名，创建一次逐行只写 Value）。
        await using DbCommand scratch = CreateCommand();

        long processed = 0;
        for (int start = 0; start < entities.Count; start += batchSize)
        {
            int end = Math.Min(start + batchSize, entities.Count);
            int rowCount = end - start;
            await using DbCommand cmd = CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;

            var pool = new System.Data.Common.DbParameter[rowCount * columnCount];
            for (int i = 0; i < pool.Length; i++)
            {
                System.Data.Common.DbParameter parameter = cmd.CreateParameter();
                parameter.ParameterName = QueryBuilder<T>.GetParameterName(i);
                pool[i] = parameter;
                _ = cmd.Parameters.Add(parameter);
            }

            for (int row = start; row < end; row++)
            {
                scratch.Parameters.Clear();
                metadata.BindUpsert(scratch, entities[row]);
                if (scratch.Parameters.Count != columnCount)
                    throw new InvalidOperationException(
                        $"Type '{typeof(T).Name}' upsert binder produced {scratch.Parameters.Count} " +
                        $"parameters for {columnCount} upsert columns.");
                int rowBase = (row - start) * columnCount;
                for (int c = 0; c < columnCount; c++)
                    pool[rowBase + c].Value = scratch.Parameters[c].Value;
            }

            cmd.CommandText = BuildUpsertBatchSql(rowCount, columnCount, shape);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            processed += rowCount;
        }
        return processed;
    }

    /// <summary>UPSERT 语句的可复用片段（方言分派一次，逐批复用）。</summary>
    private readonly record struct UpsertSqlShape(string QuotedTable, string QuotedColumns, string ConflictClause);

    /// <summary>构建方言分派的 UPSERT 骨架片段。UPDATE 集 = UpsertColumns 去掉主键
    /// （ON CONFLICT 的 DO UPDATE 不能更新冲突键本身）；MySQL 用 VALUES(c) 形态
    /// （与单行 upsert 的既有生成 SQL 一致）。</summary>
    private UpsertSqlShape BuildUpsertSqlShape(string tableName, CrudMetadata metadata, string pkColumn)
    {
        string quote(string identifier) => TProvider.QuoteIdentifier(identifier);
        List<string> updateColumns =
        [
            .. metadata.UpsertColumns.Where(c => !string.Equals(c, pkColumn, StringComparison.Ordinal))
        ];
        if (updateColumns.Count == 0)
            throw new NotSupportedException(
                $"Set-based upsert on '{tableName}' has no updatable columns " +
                "(upsert columns minus primary key is empty); use BulkInsertAsync instead.");
        string conflictClause = TProvider.Dialect == SqlDialect.MySql
            ? " ON DUPLICATE KEY UPDATE " + string.Join(", ",
                updateColumns.Select(c => $"{quote(c)} = VALUES({quote(c)})"))
            : $" ON CONFLICT ({quote(pkColumn)}) DO UPDATE SET " + string.Join(", ",
                updateColumns.Select(c => $"{quote(c)} = excluded.{quote(c)}"));
        return new UpsertSqlShape(
            quote(tableName),
            string.Join(", ", metadata.UpsertColumns.Select(quote)),
            conflictClause);
    }

    /// <summary>构建单批的多值 UPSERT SQL——参数已由调用方按批内连续下标预建绑定，
    /// 此处只生成与之一一对应的 VALUES 占位符。</summary>
    private static string BuildUpsertBatchSql(int rowCount, int columnCount, UpsertSqlShape shape)
    {
        var sql = new System.Text.StringBuilder(64 + rowCount * (columnCount * 8 + 4));
        sql.Append("INSERT INTO ").Append(shape.QuotedTable).Append(" (")
            .Append(shape.QuotedColumns).Append(") VALUES ");
        for (int row = 0; row < rowCount; row++)
        {
            sql.Append(row > 0 ? ", (" : "(");
            for (int c = 0; c < columnCount; c++)
            {
                if (c > 0) sql.Append(", ");
                sql.Append(ParameterNameCache.GetName(row * columnCount + c));
            }
            sql.Append(')');
        }
        sql.Append(shape.ConflictClause);
        return sql.ToString();
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
