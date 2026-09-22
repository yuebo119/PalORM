using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace PalORM;

public sealed partial class DataSession<TProvider>
    where TProvider : IDbProvider
{
    /// <summary>保存点名合法性校验（R4，v5.7）：空名/空白名/含 NUL 的名字经 QuoteIdentifier
    /// 转义后跨方言行为发散（PG 接受空引用标识符、MySQL 拒绝；NUL 截断命令文本）——
    /// 库内统一提前拒绝，错误消息指向参数而非驱动语法报错。</summary>
    private static void ValidateSavepointName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\0'))
            throw new ArgumentException("Savepoint name cannot contain NUL characters.", nameof(name));
    }

    /// <summary>见 DataSession 主文档。</summary>
    public async ValueTask SavepointAsync(DbTransaction tran, string name, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(tran);
        ValidateSavepointName(name);
        // ITM-637 同型面（复检发现）：已释放事务（Connection null）先于归属检查——
        // 原统一报"不属于主连接"误导排查方向（与 WithTransaction 同口径）。
        // R4/T1（v5.7）：探测走 IsTransactionAlive——Npgsql 已释放事务取 Connection 抛
        // ObjectDisposedException，会抢在本 ArgumentException 之前崩溃
        if (!SessionOperationState.IsTransactionAlive(tran))
            throw new ArgumentException(
                "Cannot create a savepoint: the transaction has been disposed (its Connection is null).", nameof(tran));
        // ITM-575: 与 UseTransaction 对称——异连接事务在驱动层的错误形态不可控，库内明确失败
        if (!ReferenceEquals(tran.Connection, _conn))
            throw new ArgumentException(
                "The transaction must belong to the DataSession's primary connection.", nameof(tran));
        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        // S2077 报备（M1-5）：savepoint 名经 Provider QuoteIdentifier 标识符转义（标识符面，无值拼接）
#pragma warning disable S2077
        cmd.CommandText = $"SAVEPOINT {TProvider.QuoteIdentifier(name)}";
#pragma warning restore S2077
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>回滚到保存点。</summary>
    public async ValueTask RollbackToAsync(DbTransaction tran, string name, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(tran);
        ValidateSavepointName(name);
        // ITM-637 同型面（复检发现，同 SavepointAsync）；R4/T1（v5.7）探测走
        // IsTransactionAlive，理由同 SavepointAsync 注释
        if (!SessionOperationState.IsTransactionAlive(tran))
            throw new ArgumentException(
                "Cannot roll back to a savepoint: the transaction has been disposed (its Connection is null).", nameof(tran));
        if (!ReferenceEquals(tran.Connection, _conn))
            throw new ArgumentException(
                "The transaction must belong to the DataSession's primary connection.", nameof(tran));
        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        // S2077 报备（M1-5）：同 SavepointAsync——标识符面，无值拼接
#pragma warning disable S2077
        cmd.CommandText = $"ROLLBACK TO SAVEPOINT {TProvider.QuoteIdentifier(name)}";
#pragma warning restore S2077
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>开始事务（使用会话默认隔离级别或显式指定）。</summary>
    public ValueTask<DbTransaction> BeginTransactionAsync(
        IsolationLevel? level = null, CancellationToken ct = default)
        => BeginTransactionCoreAsync(level, null, ct);

    private async ValueTask<DbTransaction> BeginTransactionCoreAsync(
        IsolationLevel? level,
        object? operationOwner,
        CancellationToken ct)
    {
        using SessionOperationState.SessionOperationLease operation =
            EnterOperation(operationOwner);
        if (GetActiveTransaction() is not null)
            throw new InvalidOperationException("DataSession does not support nested transactions.");

        DbTransaction transaction = await _conn.BeginTransactionAsync(
            level ?? _isolationLevel, ct).ConfigureAwait(false);
        try
        {
            _operationState.PublishTransaction(
                transaction, operationOwner);
            return transaction;
        }
        catch (Exception exception)
        {
            await TransactionCleanup.DisposeTransactionPreservingAsync(
                transaction, exception).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>事务包裹执行——自动 commit/rollback。callback 内仅支持顺序数据库操作，不支持嵌套事务。</summary>
    public ValueTask WithTransaction(Func<CancellationToken, Task> action,
        IsolationLevel? level = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        // v5.4 精炼（L2）：无返回值重载委托泛型核心——此前两份 ~60 行逐字复制，
        // 是"commit/rollback/清理链修一处漏一处"的温床。包装委托每次事务分配一个，
        // 非热路径，可忽略。ValueTask<T> 无隐式转换，经 new ValueTask(Task) 桥接。
        return new ValueTask(WithTransaction<object?>(
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return null;
            },
            level, ct).AsTask());
    }

    /// <summary>事务包裹执行（带返回值）。callback 内仅支持顺序数据库操作，不支持嵌套事务。</summary>
    public async ValueTask<T> WithTransaction<T>(Func<CancellationToken, Task<T>> action,
        IsolationLevel? level = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        // ITM-708(r20)：EnterTransactionFlow 与 GetActiveTransaction 必须都在 try 内——
        // GetActiveTransaction 在外部事务已被 Dispose 时会抛（ITM-640 响亮路径），
        // 若它（或 EnterTransactionFlow 之后的任何语句）在 try 之外抛出，finally 的
        // ExitTransactionFlow 永不执行 → _transactionOwner/_activeTransaction 永不清，
        // 会话后续操作永久失败且 DisposeAsync 挂到超时。owner 先置 null，仅在登记成功后赋值。
        object? owner = null;
        DbTransaction? previousTransaction = null;
        DbTransaction? transaction = null;
        Exception? primaryException = null;
        // T1（v5.7）：置于 CommitAsync 紧前——异常到达 catch 且此标志为 true 即"提交已尝试
        // 且失败"，与 action 失败可区分（提交成功不会进 catch）
        bool commitAttempted = false;
        try
        {
            owner = _operationState.EnterTransactionFlow();
            previousTransaction = GetActiveTransaction();
            transaction = await BeginTransactionAsync(level, ct).ConfigureAwait(false);
            // TX-004（2026-09-23）：本方法持有提交权——生成 ID 等回填延迟到提交成功后回放，
            // 否则 action 中途失败回滚会让实体持有已不存在行的 ID。
            _operationState.DefersPostCommitActions = true;
            try
            {
                T result = await action(ct).ConfigureAwait(false);
                await _operationState.DisposeTransactionResourcesAsync(null)
                    .ConfigureAwait(false);
                using SessionOperationState.SessionOperationLease operation =
                    _operationState.EnterTransactionOperation();
                commitAttempted = true;
                await TransactionCleanup.CommitWithTimeoutAsync(
                    transaction, _options.CommandTimeoutSeconds, ct).ConfigureAwait(false);
                ReplayPostCommitActions();
                return result;
            }
            catch (Exception exception)
            {
                primaryException = exception;
                _operationState.DiscardPostCommitActions();
                await _operationState.DisposeTransactionResourcesAsync(exception)
                    .ConfigureAwait(false);
                // T1（v5.7）：提交失败且非 SQLite（服务端已终结事务）时跳过回滚——
                // 裁决依据与 SQLite 例外见 TransactionCleanup.TrySkipRollbackAfterCommitFailure
                if (!commitAttempted
                    || !TransactionCleanup.TrySkipRollbackAfterCommitFailure(
                        TProvider.Dialect, exception))
                {
                    await RollbackTransactionPreservingAsync(transaction, exception)
                        .ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            // TX-004：无论提交/回滚/异常，收口后必须复位——否则会话后续的无事务写入也会被延迟回填。
            _operationState.DefersPostCommitActions = false;
            try
            {
                if (transaction is not null)
                {
                    // r19/ITM-704：RestoreTransaction 若抛异常（如 previousTransaction 状态访问失败），
                    // 事务释放仍必须执行——嵌套 finally 保证清理链不可跳步。
                    try
                    {
                        _operationState.RestoreTransaction(
                            transaction, previousTransaction);
                    }
                    finally
                    {
                        await TransactionCleanup.DisposeTransactionPreservingAsync(
                            transaction, primaryException).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (owner is not null)
                    _operationState.ExitTransactionFlow(owner);
            }
        }
    }

    /// <summary>提交成功后回放延迟的提交后动作（TX-004）——空清单零开销。</summary>
    private void ReplayPostCommitActions()
    {
        if (_operationState.TakePostCommitActions() is { } actions)
        {
            foreach (Action action in actions)
                action();
        }
    }

    /// <summary>整事务重放（API-003，2026-09-23）：把 <paramref name="action"/> 包在自开事务里执行，
    /// 遇到可重放的失败（PG 序列化失败 40001 / 死锁 40P01、MySQL 1213·1205、SQLITE_BUSY·LOCKED）时
    /// <b>整事务</b>重放——回滚后从头再来，等价于调用方自写 while 循环。
    /// <para><b>与 WithRetry 的区别</b>：弹性执行器与 WithRetry 重试的是<b>语句</b>，且事务内默认关闭
    /// 重试（PG aborted transaction 会以次生异常掩盖根因）；本方法重试的是<b>整个事务</b>。</para>
    /// <para><b>重放语义 = at-least-once</b>：action 必须可重复执行（含其中全部写入；唯一键 upsert 或
    /// 条件更新是安全形态）。提交结果未知（如 COMMIT 超时）同样会触发重放——非幂等副作用
    /// （发消息、调外部 API）请放在事务外。重放判据用 Provider 的瞬时判定
    /// （<c>TProvider.IsTransient</c>，上述错误码均已覆盖），因此连接级瞬时故障也触发重放。</para>
    /// <para>退避复用弹性执行器的默认策略（含抖动）；调用方取消原样传播，不重放。</para></summary>
    public async ValueTask WithTransactionRetry(Func<CancellationToken, Task> action,
        int maxRetries = 3, IsolationLevel? level = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        int attempt = 0;
        while (true)
        {
            try
            {
                await WithTransaction(action, level, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (attempt < maxRetries
                && exception is not OperationCanceledException
                && TProvider.IsTransient(exception))
            {
                await Task.Delay(ResilienceExecutor.GetDefaultBackoff(attempt), ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    /// <summary>整事务重放（带返回值）——语义见
    /// <see cref="WithTransactionRetry(Func{CancellationToken, Task}, int, IsolationLevel?, CancellationToken)"/>。</summary>
    public async ValueTask<T> WithTransactionRetry<T>(Func<CancellationToken, Task<T>> action,
        int maxRetries = 3, IsolationLevel? level = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        int attempt = 0;
        while (true)
        {
            try
            {
                return await WithTransaction(action, level, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < maxRetries
                && exception is not OperationCanceledException
                && TProvider.IsTransient(exception))
            {
                await Task.Delay(ResilienceExecutor.GetDefaultBackoff(attempt), ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    private async ValueTask RollbackTransactionPreservingAsync(
        DbTransaction transaction,
        Exception primaryException)
    {
        SessionOperationState.SessionOperationLease operation;
        try
        {
            operation = _operationState.EnterTransactionOperation();
        }
        catch (Exception gateException)
        {
            // r19/ITM-693：被弃的 QueryAsyncEnumerable 枚举器让 WaitForActiveOperationPreservingAsync
            // 超时后 _isActive 仍为 true——门禁拒绝回滚租约。此前直接放弃回滚（只靠 finally
            // Dispose 的驱动隐式回滚，无任何留痕）。此处绕过门禁直接尝试回滚：连接上有活跃
            // reader 时驱动可能再次拒绝，成败都以结构化 Data 记录，不静默跳过。
            try
            {
                await TransactionCleanup.RollbackPreservingAsync(
                    transaction, primaryException, RollbackTimeoutSeconds).ConfigureAwait(false);
            }
            catch (Exception directRollbackException)
            {
                directRollbackException.Data["PalORM.RollbackGateException"] = gateException;
                primaryException.Data["PalORM.RollbackException"] = directRollbackException;
                return;
            }
            primaryException.Data["PalORM.RollbackGateException"] = gateException;
            return;
        }

        using (operation)
        {
            await TransactionCleanup.RollbackPreservingAsync(
                transaction, primaryException, RollbackTimeoutSeconds).ConfigureAwait(false);
        }
    }

    /// <summary>R3：回滚的有界上限——沿用会话 CommandTimeout 口径；Zero（无限）透传为 0
    /// 表示调用方显式要求无界等待。</summary>
    private int RollbackTimeoutSeconds => _options.CommandTimeoutSeconds;

    /// <summary>事务作用域内核（Bulk 家族共享，v5.4 精炼 L1）——复用会话活动事务或自开事务，
    /// 统一承载"commit / rollback-preserving / RestoreTransaction / 释放"四段骨架。
    /// 此前该骨架在 BulkDelete/BulkUpdateRowByRow/BulkUpdateBatch/BulkMerge 四处逐字复制，
    /// 是清理链修一处漏一处的温床。
    /// <para><b>语义契约</b>: work 仅在成功路径返回；自开事务由本内核提交，异常路径回滚并
    /// 重抛主异常，finally 先还原登记再释放事务（与 WithTransaction 同序，ITM-704 嵌套保证）。
    /// 提交后的收尾动作（如 ITM-556 的 version 批量回填）应在内核返回后执行——仅成功路径可达。</para>
    /// <para><see cref="QueryBuilderExtensions.ToPageAsync"/> 有意不走本内核：其自开事务需
    /// honoring 会话隔离级别并经 PublishTransaction 登记（r9-S2/ITM-649），机制不同，
    /// 强行统一需策略参数化反而劣化可读性。</para></summary>
    private async ValueTask<T> RunInTransactionScopeAsync<T>(
        object? operationOwner,
        Func<DbTransaction, CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction transaction = previousTransaction
            ?? await BeginTransactionCoreAsync(
                null, operationOwner, ct).ConfigureAwait(false);
        bool ownsTransaction = previousTransaction is null;
        Exception? primaryException = null;
        // T1（v5.7）：同 WithTransaction——提交尝试标志区分提交失败与 work 失败
        bool commitAttempted = false;
        // TX-004（2026-09-23）：自开事务时持有提交权——生成 ID 等回填延迟到提交成功后回放。
        if (ownsTransaction)
            _operationState.DefersPostCommitActions = true;
        try
        {
            T result = await work(transaction, ct).ConfigureAwait(false);
            if (ownsTransaction)
            {
                commitAttempted = true;
                await TransactionCleanup.CommitWithTimeoutAsync(
                    transaction, _options.CommandTimeoutSeconds, ct).ConfigureAwait(false);
                ReplayPostCommitActions();
            }
            return result;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            _operationState.DiscardPostCommitActions();
            if (ownsTransaction
                && (!commitAttempted
                    || !TransactionCleanup.TrySkipRollbackAfterCommitFailure(
                        TProvider.Dialect, exception)))
            {
                await TransactionCleanup.RollbackPreservingAsync(
                    transaction, exception, RollbackTimeoutSeconds).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            // ITM-707(r20)：仅在自开事务时还原登记。非自开路径下 previousTransaction == transaction，
            // 无条件 RestoreTransaction 会让 ReferenceEquals 成立并把 _externalTransaction 复位为 false
            // ——外部 UseTransaction 设入的事务从此失效时不抛（ITM-640 保护被静默关闭）。
            // 与 QueryBuilderExtensions.ToPageAsync 的 ownsTransaction 守卫口径一致。
            if (ownsTransaction)
            {
                _operationState.DefersPostCommitActions = false;
                _operationState.RestoreTransaction(transaction, previousTransaction);
                await TransactionCleanup.DisposeTransactionPreservingAsync(transaction, primaryException).ConfigureAwait(false);
            }
        }
    }
}
