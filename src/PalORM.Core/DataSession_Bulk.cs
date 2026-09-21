using System.Data.Common;
using System.Text;

namespace PalORM;

// Bulk operations (partial class — 从 DataSession.cs 拆分)
public partial class DataSession<TProvider>
{

    /// <summary>批量插入——委托 Provider 使用源生成 InsertColumns 与 binder，并复用会话事务。</summary>
    /// <returns><b>数据库受影响行数</b>（驱动 ExecuteNonQuery 口径）——与本家族
    /// BulkUpdate/BulkDelete 同口径；BulkMerge 例外（返回处理实体数，跨方言可预测，见其
    /// returns 说明）。</returns>
    public async ValueTask<long> BulkInsertAsync<T>(IReadOnlyList<T> entities, int batchSize = 1000, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // r11.5-D3（ITM-637 同型第六处）：元数据检查先于空列表短路——会话层短路使
        // Provider 层（r4 批次已修）的三方言一致性检查对空列表不可达
        // R13：复用 BulkOperationFramework 的单一实现点（PG/MySQL/多值骨架共用同一守卫）
        _ = BulkOperationFramework.EnsureInsertMetadata(typeof(T));
        if (entities.Count == 0) return 0;
        return await TProvider.BulkInsertAsync(_conn, GetActiveTransaction(), entities, batchSize,
            _options.CommandTimeoutSeconds, ct, _isolationLevel).ConfigureAwait(false);  // r6-N2
    }

    /// <summary>批量删除——每 500 个生成主键 IN 批次；软删除实体更新 deleted_at，其他实体物理删除。</summary>
    /// <returns><b>数据库受影响行数</b>（各批次 ExecuteNonQuery 之和，驱动口径）。
    /// 软删除路径返回被标记删除的行数（UPDATE 计数），与物理删除的 DELETE 计数口径一致。</returns>
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
        // M1（v5.7）：后缀 per-Dialect 缓存（语句随批次占位符变化，仅后缀可缓存）
        string tenantFilter = HasTenantFilter<T>() ? GetTenantAppendFragment() : "";
        // L5：标识符与时间表达式集合一次算好——原实现每批重算
        // （QuoteIdentifier("deleted_at") × 2 + CurrentTimestampExpression 取值）
        DeleteIdentifiers identifiers = new(
            quotedTable, quotedPrimaryKey, TProvider.QuoteIdentifier("deleted_at"),
            TProvider.CurrentTimestampExpression, tenantFilter);
        const int batchSize = SqlLimits.InClauseBatchSize;
        // 满批占位符名在批大小不变时逐位相同——预建一次，末批另建
        string[] fullBatchPlaceholders = BuildPlaceholderNames(TProvider.GetParameterPlaceholder, batchSize);
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
                // 语句文本在批大小不变时逐位相同，末批不同——只在变化时重建
                string? lastBatchSql = null;
                int lastBatchLength = -1;
                long total = 0;
                for (int start = 0; start < keys.Count; start += batchSize)
                {
                    int end = Math.Min(start + batchSize, keys.Count);
                    int batchLen = end - start;
                    string[] placeholders = batchLen == batchSize
                        ? fullBatchPlaceholders
                        : BuildPlaceholderNames(TProvider.GetParameterPlaceholder, batchLen);

                    if (batchLen != lastBatchLength)
                    {
                        lastBatchSql = BuildBulkDeleteSql(
                            batchLen, placeholders, identifiers, isSoftDelete);
                        lastBatchLength = batchLen;
                    }

                    await using DbCommand cmd = CreateCommand();
                    cmd.Transaction = tran;
                    cmd.CommandText = lastBatchSql!;

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

    /// <summary>批内参数名（<see cref="IDbProvider.GetParameterPlaceholder"/> 形态，三方言一致）。
    /// 驱动在 Add 时校验集合内重名，故名字必须真实存在而非仅占位。</summary>
    private static string[] BuildPlaceholderNames(Func<int, string> placeholderFactory, int batchLen)
    {
        var placeholders = new string[batchLen];
        for (int index = 0; index < batchLen; index++)
            placeholders[index] = placeholderFactory(index);
        return placeholders;
    }

    /// <summary>删除语句的标识符与表达式集合——L5 提取到方法外，避免每批重算
    /// （QuoteIdentifier("deleted_at") × 2 + 时间表达式取值）。</summary>
    private readonly record struct DeleteIdentifiers(
        string QuotedTable, string QuotedPrimaryKey, string QuotedDeletedAt,
        string TimestampExpression, string TenantFilter);

    /// <summary>L5：单批删除/软删语句——单个 <see cref="ValueStringBuilder"/> 顺序写出，
    /// 替代原「string[] + string.Join + 多重插值」的中间串。SQL 文本与旧实现逐字节一致。</summary>
    private static string BuildBulkDeleteSql(
        int batchLen, string[] placeholders, in DeleteIdentifiers identifiers, bool isSoftDelete)
    {
        var sb = new ValueStringBuilder(stackalloc char[256]);
        try
        {
            if (isSoftDelete)
            {
                sb.Append("UPDATE ");
                sb.Append(identifiers.QuotedTable);
                sb.Append(" SET ");
                sb.Append(identifiers.QuotedDeletedAt);
                sb.Append(" = ");
                sb.Append(identifiers.TimestampExpression);
                sb.Append(" WHERE ");
            }
            else
            {
                sb.Append("DELETE FROM ");
                sb.Append(identifiers.QuotedTable);
                sb.Append(" WHERE ");
            }
            sb.Append(identifiers.QuotedPrimaryKey);
            sb.Append(" IN (");
            for (int index = 0; index < batchLen; index++)
            {
                if (index > 0) sb.Append(", ");
                sb.Append(placeholders[index]);
            }
            sb.Append(')');
            if (isSoftDelete)
            {
                sb.Append(" AND ");
                sb.Append(identifiers.QuotedDeletedAt);
                sb.Append(" IS NULL");
            }
            sb.Append(identifiers.TenantFilter);
            sb.TrimEnd();
            return sb.ToString();
        }
        finally { sb.Dispose(); }
    }

    /// <summary>批量更新。复用源生成 UPDATE 与并发语义，整个输入在同一事务内执行。
    /// <para>ITM-556: [ConcurrencyCheck] 实体的内存 version 回填延迟到事务提交成功后统一执行——
    /// 中途冲突整批回滚时，已成功条目的内存状态与 DB 保持一致，重试不产生假冲突。
    /// 复用外部事务时回填发生在本方法返回前；若调用方随后回滚该外部事务，
    /// 内存 version 需重新查询同步（与单条 UpdateAsync 在外部事务中回滚的既有语义一致）。</para></summary>
    /// <returns><b>数据库受影响行数</b>（驱动 ExecuteNonQuery 口径；单语句多行 UPDATE 形态
    /// 或逐条批次，均按驱动计数累加）。BulkMerge 的返回口径例外，见其 returns 说明。</returns>
    public async ValueTask<long> BulkUpdateAsync<T>(IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        // R8：单次注册表快照贯穿路由判定与后续取值——原实现入口三次独立读
        // CurrentState（三次 Volatile.Read），Register/热重载窗口内可能跨版本混用元数据
        // （与 Insert/Update/Delete 的 r19/ITM-703 单快照纪律对齐）。
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata routeMetadata))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated CRUD.");
        if (entities.Count == 0) return 0;

        // v5.7 自动路由（L1）：满足全部条件时走单语句批量（远程 N 行 N 次 RTT → 1 次，
        // 与 BulkMerge 集合化同构收益）。条件不满足时保持逐条（乐观锁语义 / 软删 / 租户 / SQLite）。
        // 每个条件不自动路由的理由：
        //   乐观锁（IncrementVersion）→ 批量无法表达"每行 version 匹配"；
        //   软删/租户 → 逐条路径追加默认过滤，批量路径的过滤追加需另做（当前不支持）；
        //   SQLite → CASE WHEN 在 SQLite 实测慢 6.4×（BulkUpdateBatchAsync 已有同判）。
        if (TProvider.Dialect != SqlDialect.Sqlite
            && entities.Count > 1
            && routeMetadata.IncrementVersion is null
            && !IsSoftDeletable<T>()
            && !HasTenantFilter<T>())
        {
            // 直接复用 BulkUpdateBatchAsync 的核心逻辑（此处条件已排除其拒绝项）
            string tableName = state._tableNames[typeof(T)];
            BatchUpdateContext ctx = PrepareBatchUpdateContext<T>(state, routeMetadata, tableName, entities[0]);
            int rowsPerBatch = Math.Max(1, SqlLimits.MaxBindParameters / (ctx.SetColumnCount + 1));
            return await RunInTransactionScopeAsync(
                operation.Owner,
                async (tran, token) =>
                    await ExecuteBulkUpdateBatchesAsync(entities, routeMetadata, ctx, tran, rowsPerBatch, token)
                        .ConfigureAwait(false),
                ct).ConfigureAwait(false);
        }

        return await ExecuteBulkUpdateRowByRowAsync<T>(entities, operation.Owner, ct).ConfigureAwait(false);
    }

    /// <summary>BulkUpdate 逐条核心逻辑（不含 EnterOperation）——供 BulkUpdateAsync 和 BulkUpdateBatchAsync SQLite 回退复用。
    /// v5.4 精炼 L1：事务骨架收敛至 RunInTransactionScopeAsync。
    /// <para><b>P0-1（2026-09-21）参数池化路径</b>：原实现每行走 <c>UpdateCoreAsync</c>，
    /// 每行新建 DbCommand + 重建全部参数 + 新建 async 委托。三方言实测每行
    /// 1457~1625 B，是 BulkInsert（148~736 B）的 2.2~11 倍——同一文件的
    /// <see cref="MultiValueBulkInsert"/> 早有「命令跨批复用 + 参数池 + 只写 Value」范式，
    /// 逐条 UPDATE 是漏项。现按生成器是否发射 <see cref="CrudMetadata.BindUpdateValues"/>
    /// 分派：有则走池化路径（零 CreateParameter），无则回退原逐条路径（旧模型程序集）。</para>
    /// <para><b>乐观锁语义保持不变</b>：affectedRows 的 0 行 / 多行检查与 version 内存回填
    /// 的时机（提交成功后统一执行）都按原契约保留——池化只改「参数怎么来」，不改判定。</para></summary>
    private async ValueTask<long> ExecuteBulkUpdateRowByRowAsync<T>(
        IReadOnlyList<T> entities, object? operationOwner, CancellationToken ct)
        where T : class, new()
    {
        // P0-1：单次注册表快照（与 BulkUpdateAsync 的 R8 同口径）
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated CRUD.");

        var (total, deferredVersionIncrements) = await RunInTransactionScopeAsync(
            operationOwner,
            async (transaction, token) => metadata.BindUpdateValues is { } valuesBinder
                ? await ExecuteBulkUpdatePooledAsync(
                    entities, metadata, valuesBinder, transaction, token).ConfigureAwait(false)
                : await ExecuteBulkUpdateLegacyAsync(
                    entities, operationOwner, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        // ITM-556：内存 version 回填在提交成功后统一执行——中途冲突整批回滚时，
        // 已成功条目的内存状态与 DB 保持一致，重试不产生假冲突。置于内核之外：
        // 仅成功提交路径可达此处（复用外部事务时回填发生在本方法返回前，
        // 调用方随后回滚该外部事务的既有语义不变）。
        foreach (Action increment in deferredVersionIncrements) increment();
        return total;
    }

    /// <summary>P0-1：池化逐条 UPDATE——命令与参数池建一次，逐行只写 Value。
    /// <para><b>为什么命令能跨行复用</b>：所有行的 UPDATE 语句逐位相同（同表同 SET 列集），
    /// 变的只是参数值；生成的 UPDATE 恒以 <c>WHERE pk = @pN [AND version = @pM]</c> 结尾，
    /// 参数序固定。因此 CommandText 设一次、参数对象建一次，逐行只写 Value。</para>
    /// <para><b>与 <see cref="ExecuteBulkUpdateBatchesAsync{T}"/> 的关系</b>：后者是「一条 SQL 更新 N 行」，
    /// 本方法是「N 条 SQL 各更新 1 行」——参数布局相同（每行 paramsPerRow 个），故直接复用
    /// <see cref="BatchUpdateSqlBuilder.CreateParameterArray"/> 的命名契约。</para></summary>
    private async ValueTask<(long Total, List<Action> Increments)> ExecuteBulkUpdatePooledAsync<T>(
        IReadOnlyList<T> entities, CrudMetadata metadata,
        Action<DbParameter[], object, int> valuesBinder,
        DbTransaction tran, CancellationToken ct)
        where T : class, new()
    {
        List<Action> increments = [];
        if (entities.Count == 0) return (0, increments);

        // P0-1：参数数从 probe 提取而非硬算——BindUpdateValues 的参数序是
        // [SET 列…, 主键…, 并发令牌?]（见 CommandFactoryEmitter.GenerateBindUpdateValuesBody），
        // 带 [ConcurrencyCheck] 的实体比 setColumnCount+1 多一个。硬算会让乐观锁实体
        // 的 version 参数拿不到值，静默写错数据。probe 同时是生成器三处
        // （SQL/Bind/元数据）漂移的运行时哨兵（与 PrepareBatchUpdateContext 同范式）。
        DbCommand probe = CreateCommand();
        int paramsPerRow;
        try
        {
            metadata.BindUpdate(probe, entities[0]);
            paramsPerRow = probe.Parameters.Count;
        }
        finally
        {
            await probe.DisposeAsync().ConfigureAwait(false);
        }
        if (paramsPerRow <= 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' BindUpdate produced {paramsPerRow} parameters; recompile the model assembly.");

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        // 生成的 UPDATE 语句：复用 GetCommandSqls 的单一真源（含租户后缀的两形态由缓存提供）
        string updateSql = GetCommandSqls<T>(PalORM_Runtime.CurrentState).Update;
        if (updateSql.Length == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no updatable columns.");
        if (HasTenantFilter<T>())
            updateSql = GetTenantWrappedSql<T>(
                DataSessionCache.UpdateWithTenantSqlCache, updateSql);
        cmd.CommandText = updateSql;

        // 参数池：一行 paramsPerRow 个 + 可选租户参数（固定名追加末尾）
        bool hasTenant = HasTenantFilter<T>();
        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterArray(
            paramsPerRow, hasTenant, _tenantParameterName, _tenantId,
            TProvider.CreateParameter);
        AttachParameters(cmd, pool, paramsPerRow, paramsPerRow, hasTenant ? 1 : 0);

        long total = 0;
        for (int i = 0; i < entities.Count; i++)
        {
            // 只写 Value——零 CreateParameter（P0-1 的核心）
            valuesBinder(pool, entities[i], 0);

            int affectedRows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (metadata.IncrementVersion is { } incrementVersion)
            {
                if (affectedRows == 0)
                    throw new ConcurrencyConflictException(
                        $"Entity '{typeof(T).Name}' was modified by another transaction.");
                if (affectedRows != 1)
                    throw new InvalidOperationException(
                        $"Concurrency update for '{typeof(T).Name}' affected {affectedRows} rows.");
                // 闭包必须捕获本次迭代的实体而非循环变量 i——lambda 在提交成功后统一执行，
                // 届时 i 已越界（ITM-556 的延迟回放语义）
                T entity = entities[i];
                increments.Add(() => incrementVersion(entity));
            }
            total += affectedRows;
        }
        return (total, increments);
    }

    /// <summary>P0-1 的旧版模型程序集回退路径：无 <see cref="CrudMetadata.BindUpdateValues"/>
    /// 时逐行 <c>UpdateCoreAsync</c>（每行新建命令与参数）。语义与池化路径逐位一致，
    /// 参数创建量回到改前水平——这是旧程序集的既有成本，不是回归。</summary>
    private async ValueTask<(long Total, List<Action> Increments)> ExecuteBulkUpdateLegacyAsync<T>(
        IReadOnlyList<T> entities, object? operationOwner, CancellationToken token)
        where T : class, new()
    {
        long total = 0;
        List<Action> increments = [];
        foreach (T entity in entities)
        {
            total += await UpdateCoreAsync(
                entity, operationOwner, token, increments).ConfigureAwait(false);
        }
        return (total, increments);
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
        // R8：单次注册表快照——原实现入口先读 CurrentState 检查、随后又独立读一次取元数据
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated CRUD.");
        // r12-B1（D3 残留族）：空短路后置（三方言/两路径统一口径——SQLite 回退路径同样
        // 不应使未注册类型+空列表静默成功）
        if (entities.Count == 0) return 0;

        // v5.0 SQLite 方言感知回退：CASE WHEN 在 SQLite 上比逐条慢 6.4x（实测验证）。
        // 直接调内部逐条路径（不复用 BulkUpdateAsync 的 EnterOperation——会双重锁定）。
        if (TProvider.Dialect == SqlDialect.Sqlite)
            return await ExecuteBulkUpdateRowByRowAsync(entities, operation.Owner, ct).ConfigureAwait(false);

        if (!state._tableNames.TryGetValue(typeof(T), out string? tableName))
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
        const int driverLimit = SqlLimits.MaxBindParameters;
        int tenantParams = ctx.HasTenantFilter ? 1 : 0;
        int rowsPerBatch = Math.Max(1, (driverLimit - tenantParams) / (ctx.SetColumnCount + 1));

        // v5.4 精炼 L1：事务骨架收敛至 RunInTransactionScopeAsync。
        return await RunInTransactionScopeAsync(
            operation.Owner,
            async (tran, token) =>
                await ExecuteBulkUpdateBatchesAsync(entities, metadata, ctx, tran, rowsPerBatch, token)
                    .ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    /// <summary>实体是否标注 [SoftDelete] 且未被 IgnoreFilters——按注册表 EntityFeatures
    /// 判定（与 BulkDeleteAsync 的软删分派同源）。</summary>
    private bool IsSoftDeletable<T>() where T : class, new()
        => !_ignoreFilters
            && (GetEntityFeatures<T>() & EntityFeatures.SoftDelete) != 0;

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

    /// <summary><b>M1/L2</b>：批量 UPDATE 的分批执行内核——拥有批次循环，命令、参数池与
    /// SQL 文本在批间复用。
    /// <para><b>为什么收敛成单方法</b>：原实现是「调用方循环 + 每批一个 ExecuteBatchUpdateAsync」，
    /// 每批新建 DbCommand、每批重建 rowParamCount 个参数、每批重建一份逐位相同的 SQL 文本。
    /// INSERT 路径自 v4.6 起就有「命令跨批复用 + 满批参数池 + CommandText 仅批大小变化时重建」
    /// 三件套（<see cref="MultiValueBulkInsert.ExecuteBatchesAsync"/>），UPDATE 路径是同类未收敛点。
    /// 本方法把整套范式搬过来：命令与参数池在进入循环前建一次，逐批只写 <c>Value</c>；
    /// SQL 文本仅在批大小变化时重建（满批恒等，仅末批可能不同）。</para>
    /// <para><b>取值路径</b>：优先走生成器发射的 <see cref="CrudMetadata.BindUpdateValues"/>
    /// （零 CreateParameter）。旧版模型程序集该绑定器为 null 时回退 probe 取值，语义不变、
    /// 参数创建量回到改前水平。</para>
    /// <para>参数名与顺序与 <see cref="BatchUpdateSqlBuilder"/> 的占位符逐位对应，由
    /// <c>BatchUpdateParameterContractTests</c> 锁定跨 Provider 契约。</para></summary>
    private async ValueTask<long> ExecuteBulkUpdateBatchesAsync<T>(
        IReadOnlyList<T> entities, CrudMetadata metadata, BatchUpdateContext ctx,
        DbTransaction tran, int rowsPerBatch, CancellationToken ct)
        where T : class, new()
    {
        int setColumnCount = ctx.SetColumnCount;
        int paramsPerRow = setColumnCount + 1;
        int tenantParams = ctx.HasTenantFilter ? 1 : 0;
        // 池按"本调用实际出现的最大批"建——输入不足一个满批时不为不存在的行预留
        int poolRows = Math.Min(rowsPerBatch, entities.Count);
        int poolRowParamCount = poolRows * paramsPerRow;

        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;

        // 参数池与命令参数集合一次建好；末批缩短时只收敛集合、不新建参数对象
        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterArray(
            poolRowParamCount, ctx.HasTenantFilter, _tenantParameterName, _tenantId,
            TProvider.CreateParameter);
        AttachParameters(cmd, pool, poolRowParamCount, poolRowParamCount, tenantParams);

        Action<DbParameter[], object, int>? valuesBinder = metadata.BindUpdateValues;
        string? lastBatchSql = null;
        int lastBatchLength = -1;
        long totalAffected = 0;

        for (int start = 0; start < entities.Count; start += rowsPerBatch)
        {
            int end = Math.Min(start + rowsPerBatch, entities.Count);
            int batchLen = end - start;
            int rowParamCount = batchLen * paramsPerRow;

            if (batchLen != lastBatchLength)
            {
                lastBatchSql = BatchUpdateSqlBuilder.Build(
                    TProvider.Dialect, ctx.QuotedTable, ctx.QuotedPk, ctx.SetColumns,
                    batchLen, ctx.HasTenantFilter, _tenantParameterName);
                lastBatchLength = batchLen;
                AttachParameters(cmd, pool, rowParamCount, poolRowParamCount, tenantParams);
            }
            cmd.CommandText = lastBatchSql!;

            if (valuesBinder is not null)
            {
                // 零分配路径：只写 Value
                for (int i = start; i < end; i++)
                    valuesBinder(pool, entities[i], (i - start) * paramsPerRow);
            }
            else
            {
                await BindRowValuesViaProbeAsync(
                    metadata, pool, entities, start, end, paramsPerRow).ConfigureAwait(false);
            }

            totalAffected += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return totalAffected;
    }

    /// <summary>旧版生成器模型程序集的回退取值：probe 命令逐行 <c>BindUpdate</c> 建参数，
    /// 再把值写进池。语义与 <see cref="CrudMetadata.BindUpdateValues"/> 一致，参数创建量回到
    /// 改前水平（这是旧程序集的既有成本，不是本路径的回归）。
    /// probe 命令由会话连接创建（<see cref="CreateCommand"/> 带事务/超时口径）。</summary>
    private async ValueTask BindRowValuesViaProbeAsync<T>(
        CrudMetadata metadata, DbParameter[] pool,
        IReadOnlyList<T> entities, int start, int end, int paramsPerRow)
        where T : class, new()
    {
        await using DbCommand probe = CreateCommand();
        for (int i = start; i < end; i++)
        {
            probe.Parameters.Clear();
            metadata.BindUpdate(probe, entities[i]);
            int baseIndex = (i - start) * paramsPerRow;
            for (int c = 0; c < paramsPerRow; c++)
                pool[baseIndex + c].Value = probe.Parameters[c].Value;
        }
    }

    /// <summary>把参数池的前 <paramref name="rowParamCount"/> 个（+ 末尾租户参数）挂到命令集合。
    /// 批大小未变时集合元素数一致，直接返回；末批缩短时清空后按池内前缀重挂——
    /// 参数对象全部来自池，无新分配（参数集合本身的重建是驱动固有成本）。
    /// <para><b>internal for testing</b>：本方法是 M1 跨批复用的契约点（池按满批建、命令集合按
    /// 实际批收敛），PG/MySQL 批量路径本地跑不到，靠单测锁定。</para></summary>
    internal static void AttachParameters(
        DbCommand cmd, DbParameter[] pool, int rowParamCount, int poolRowParamCount, int tenantParams)
    {
        int required = rowParamCount + tenantParams;
        if (cmd.Parameters.Count == required) return;
        cmd.Parameters.Clear();
        for (int i = 0; i < rowParamCount; i++)
            cmd.Parameters.Add(pool[i]);
        if (tenantParams > 0)
            cmd.Parameters.Add(pool[poolRowParamCount]);
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
    /// <returns><b>成功处理的实体数</b>（按输入计数），<b>非</b>数据库受影响行数——与
    /// BulkInsert/Update/Delete 返回驱动行数的语义不同（审计 API-010 文档化）。
    /// 选择该语义的原因（2026-09-19 论证后裁决维持）：① MySQL ON DUPLICATE KEY 的
    /// affectedRows 对更新行计 2、插入行计 1——返回值将依赖数据历史（同输入重放返回不同值），
    /// 调用方的对账逻辑会静默错；② PG/SQLite 的 ON CONFLICT 每行计 1、恰等于实体数，
    /// 处理实体数是唯一跨方言可预测口径；③ 混合路径（默认键逐条 INSERT + 批量 UPSERT）
    /// 下两种驱动口径不可加总。BulkMergeSetBasedTests 已锁定本口径。</returns>
    public async ValueTask<long> BulkMergeAsync<T>(IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(entities);
        // R8：单次注册表快照——原实现入口与路由各读一次 CurrentState（两次 Volatile.Read）
        PalORM_Runtime.RuntimeRegistryState mergeState = PalORM_Runtime.CurrentState;
        if (!mergeState._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata mergeMetadata))
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

