using System.Data.Common;

namespace PalORM;

/// <summary>协调单个 DataSession 的数据库操作、事务逻辑流与释放生命周期。
/// <para><b>v4.5 极致优化</b>：TCS 延迟创建（-88B/操作），Exit 不写 AsyncLocal（-300B EC 拷贝）。
/// 合计 -388B/操作（55%）。owner 保持 object? + ReferenceEquals 语义不变。</para></summary>
internal sealed class SessionOperationState
{
    /// <summary>Dispose 等待活动操作的上限。正常操作受 CommandTimeout 约束远早于此完成；
    /// 触发即说明存在永不完成的租约（如被放弃的枚举器）。internal 可写供测试缩短。</summary>
    internal static TimeSpan DisposeWaitTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private readonly Lock _sync = new();
    private readonly AsyncLocal<object?> _currentOperationOwner = new();
    private readonly AsyncLocal<object?> _currentTransactionOwner = new();
    // v4.5：TCS 延迟创建 -- 仅 Dispose/WaitForActive 需要等待时才创建
    private TaskCompletionSource? _activeOperation;
    private TaskCompletionSource? _activeTransaction;
    private object? _activeOperationOwner;
    // v4.5：bool 标志 -- 替代 _activeOperation is not null 做活动状态判断
    // Enter 设 true，Exit 设 false；TCS 仅在需要等待时才从 null 创建
    private bool _isActive;
    private Task? _disposeTask;
    private DbTransaction? _transaction;
    private object? _transactionOperationOwner;
    private object? _transactionOwner;
    private List<IAsyncDisposable>? _transactionResources;
    private bool _transactionCompleting;
    private int _state;

    internal SessionOperationLease Enter(object? owner = null)
    {
        lock (_sync)
        {
            bool ownedOperation = owner is not null
                && ReferenceEquals(owner, _activeOperationOwner);
            bool currentTransaction = _transactionOwner is not null
                && ReferenceEquals(
                    _transactionOwner, _currentTransactionOwner.Value);
            ObjectDisposedException.ThrowIf(
                _state == 2
                || (_state == 1 && !ownedOperation && !currentTransaction),
                this);
            if (_transactionCompleting && currentTransaction && !ownedOperation)
            {
                throw new InvalidOperationException(
                    "The active transaction flow is completing.");
            }
            if (_transactionOwner is not null
                && !ReferenceEquals(
                    _transactionOwner, _currentTransactionOwner.Value))
            {
                throw new InvalidOperationException(
                    "The active transaction belongs to another asynchronous flow.");
            }
            if (_isActive)
            {
                if (ownedOperation)
                {
                    return default;
                }
                throw new InvalidOperationException(
                    "DataSession already has an active database operation.");
            }

            // v4.6：用 this 替代 new object() -- 常驻引用，第2次 AsyncLocal.Value=this 与旧值相等时 EC 短路不 COW
            _activeOperationOwner = this;
            _currentOperationOwner.Value = _activeOperationOwner;
            _isActive = true;
            // v4.5：不创建 TCS -- 仅在 Dispose/WaitForActive 需要等待时才延迟创建
            _activeOperation = null;
            return new SessionOperationLease(
                this, _activeOperationOwner);
        }
    }

    internal SessionOperationLease EnterTransactionOperation()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state == 2, this);
            // 无活动事务流/事务归属其他异步流是调用序错误，不是释放后使用（ITM-431）
            if (_transactionOwner is null
                || !ReferenceEquals(
                    _transactionOwner, _currentTransactionOwner.Value))
            {
                throw new InvalidOperationException(
                    "EnterTransactionOperation requires an active transaction flow owned by the current asynchronous flow.");
            }
            if (_isActive)
            {
                throw new InvalidOperationException(
                    "DataSession already has an active database operation.");
            }

            // v4.6：用 this 替代 new object() -- 常驻引用，第2次 AsyncLocal.Value=this 与旧值相等时 EC 短路不 COW
            _activeOperationOwner = this;
            _currentOperationOwner.Value = _activeOperationOwner;
            _isActive = true;
            _activeOperation = null;
            return new SessionOperationLease(
                this, _activeOperationOwner);
        }
    }

    internal object EnterTransactionFlow()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state != 0, this);
            if (_transactionOwner is not null)
            {
                throw new InvalidOperationException(
                    ReferenceEquals(
                        _transactionOwner, _currentTransactionOwner.Value)
                        ? "DataSession does not support nested transactions."
                        : "The active transaction belongs to another asynchronous flow.");
            }

            _transactionOwner = new object();
            _currentTransactionOwner.Value = _transactionOwner;
            _activeTransaction = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _transactionResources = [];
            _transactionCompleting = false;
            return _transactionOwner;
        }
    }

    internal void RegisterTransactionResource(IAsyncDisposable resource)
    {
        lock (_sync)
        {
            if (_transactionOwner is not null
                && ReferenceEquals(
                    _transactionOwner, _currentTransactionOwner.Value))
            {
                if (_transactionCompleting
                    || _transactionResources is null)
                {
                    throw new InvalidOperationException(
                        "The active transaction flow is completing.");
                }
                _transactionResources.Add(resource);
            }
        }
    }

    internal async ValueTask DisposeTransactionResourcesAsync(
        Exception? primaryException)
    {
        List<IAsyncDisposable>? resources;
        lock (_sync)
        {
            _transactionCompleting = true;
            resources = _transactionResources;
            _transactionResources = null;
        }

        Exception? cleanupException = null;
        if (resources is not null)
            cleanupException = await DisposeAllPreservingAsync(resources).ConfigureAwait(false);

        cleanupException = await WaitForActiveOperationPreservingAsync(cleanupException).ConfigureAwait(false);

        if (cleanupException is null) return;
        if (primaryException is null) throw cleanupException;
        primaryException.Data["PalORM.TransactionResourceCleanupException"] =
            cleanupException;
    }

    /// <summary>顺序释放全部事务资源，首异常保留为主异常，后续异常挂 Data 不丢弃
    /// （与 GridReader 清理约定一致）。</summary>
    private static async ValueTask<Exception?> DisposeAllPreservingAsync(
        List<IAsyncDisposable> resources)
    {
        Exception? cleanupException = null;
        foreach (IAsyncDisposable resource in resources)
        {
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                if (cleanupException is null) cleanupException = exception;
                else cleanupException.Data[$"PalORM.CleanupException{cleanupException.Data.Count}"] = exception;
            }
        }
        return cleanupException;
    }

    /// <summary>等待活动操作完成的有界等待--ITM-570：与 DisposeAsync 对称--
    /// WithTransaction 回调内被放弃的枚举器租约（只走 EnterOperation，不注册为事务资源）
    /// 会让此等待永久挂起，事务收口死锁。</summary>
    private async ValueTask<Exception?> WaitForActiveOperationPreservingAsync(Exception? cleanupException)
    {
        // v4.5：TCS 延迟创建 -- 如果操作仍活动，创建 TCS 并等待 Exit 唤醒
        Task activeOperation;
        lock (_sync)
        {
            if (_isActive && _activeOperation is null)
            {
                _activeOperation = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            activeOperation = _activeOperation?.Task ?? Task.CompletedTask;
        }

        // ITM-629 同型面（修复侧纪律卡第三问实证——210/215 双读，411 行已单读此处漏改）
        TimeSpan waitTimeout = DisposeWaitTimeout;
        try
        {
            await activeOperation.WaitAsync(waitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException timeoutException)
        {
            var hangException = new InvalidOperationException(
                $"Transaction completion timed out after {waitTimeout} waiting for an active operation. " +
                "A likely cause is an abandoned QueryAsyncEnumerable enumerator inside the transaction callback; " +
                "always consume it with 'await foreach' or dispose the enumerator explicitly.",
                timeoutException);
            if (cleanupException is null) cleanupException = hangException;
            else cleanupException.Data[$"PalORM.CleanupException{cleanupException.Data.Count}"] = hangException;
        }
        return cleanupException;
    }

    internal void ExitTransactionFlow(object owner)
    {
        TaskCompletionSource? completion = null;
        lock (_sync)
        {
            if (ReferenceEquals(_transactionOwner, owner))
            {
                _transactionOwner = null;
                _transactionResources = null;
                _transactionCompleting = false;
                completion = _activeTransaction;
                _activeTransaction = null;
            }
        }
        // v4.6：不清 _currentTransactionOwner.Value -- _transactionOwner=null 已让所有读取方判定 false
        // stale AsyncLocal 值永不被咨询（下次 EnterTransactionFlow 先查 _transactionOwner is not null）
        // 省一次 EC 拷贝 ~300B/事务收口
        completion?.TrySetResult();
    }

    internal bool IsCurrentOperationScope
    {
        get
        {
            lock (_sync)
            {
                // v4.5：_isActive 替代 _activeOperationOwner is not null 做第一道判断
                return _isActive
                    && _activeOperationOwner is not null
                    && ReferenceEquals(
                        _activeOperationOwner, _currentOperationOwner.Value);
            }
        }
    }

    internal bool IsCurrentTransactionFlow
    {
        get
        {
            lock (_sync)
            {
                return _transactionOwner is not null
                    && ReferenceEquals(
                        _transactionOwner, _currentTransactionOwner.Value);
            }
        }
    }

    internal void EnsureAvailable()
    {
        lock (_sync)
        {
            bool currentTransaction = _transactionOwner is not null
                && ReferenceEquals(
                    _transactionOwner, _currentTransactionOwner.Value);
            ObjectDisposedException.ThrowIf(
                _state == 2 || (_state == 1 && !currentTransaction), this);
        }
    }

    internal DbTransaction? GetActiveTransaction()
    {
        lock (_sync)
        {
            if (_transaction?.Connection is not null)
                return _transaction;
            _transaction = null;
            _transactionOperationOwner = null;
            return null;
        }
    }

    internal void UseTransaction(DbTransaction? transaction)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state != 0, this);
            // v4.5：_isActive 替代 _activeOperation is not null
            if (_isActive || _activeTransaction is not null)
            {
                throw new InvalidOperationException(
                    "DataSession already has an active database operation or transaction flow.");
            }
            // ITM-596: 传入已 dispose 的事务（Connection == null）会让 GetActiveTransaction
            // 静默清空 _transaction--调用方"我设了事务"的期望与实际"命令不带事务执行"不符，
            // 数据可能在非事务上下文写入。明确拒绝并提示正确用法。
            if (transaction is not null && transaction.Connection is null)
                throw new ArgumentException(
                    "Cannot use a disposed transaction (its Connection is null). " +
                    "Pass a transaction from an open DbConnection, or null to clear.",
                    nameof(transaction));
            _transaction = transaction;
            _transactionOperationOwner = null;
        }
    }

    internal void PublishTransaction(
        DbTransaction transaction,
        object? operationOwner)
    {
        lock (_sync)
        {
            bool ownedOperation = operationOwner is not null
                && ReferenceEquals(
                    operationOwner, _activeOperationOwner);
            bool currentTransaction = _transactionOwner is not null
                && ReferenceEquals(
                    _transactionOwner, _currentTransactionOwner.Value);
            ObjectDisposedException.ThrowIf(
                _state == 2
                || (_state == 1 && !ownedOperation && !currentTransaction),
                this);
            _transaction = transaction;
            _transactionOperationOwner = ownedOperation
                ? operationOwner
                : null;
        }
    }

    internal void RestoreTransaction(
        DbTransaction transaction,
        DbTransaction? previousTransaction)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_transaction, transaction))
            {
                _transaction = previousTransaction?.Connection is not null
                    ? previousTransaction
                    : null;
                _transactionOperationOwner = null;
            }
        }
    }

    internal ValueTask DisposeAsync(Func<Task> disposeCore)
    {
        TaskCompletionSource? completion;
        Task activeOperation;
        Task activeTransaction;
        lock (_sync)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            bool operationOwnsTransaction =
                _transactionOperationOwner is not null
                && ReferenceEquals(
                    _transactionOperationOwner, _activeOperationOwner);
            if (_transaction?.Connection is not null
                && _activeTransaction is null
                && !operationOwnsTransaction)
            {
                throw new InvalidOperationException(
                    "Complete or dispose the active transaction before disposing DataSession.");
            }

            _state = 1;
            // v4.5：TCS 延迟创建 -- 如果操作仍活动，创建 TCS 供等待
            if (_isActive && _activeOperation is null)
            {
                _activeOperation = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            activeOperation = _activeOperation?.Task ?? Task.CompletedTask;
            activeTransaction = _activeTransaction?.Task ?? Task.CompletedTask;
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = DisposeAndCompleteAsync(
            activeOperation, activeTransaction, disposeCore, completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeAndCompleteAsync(
        Task activeOperation,
        Task activeTransaction,
        Func<Task> disposeCore,
        TaskCompletionSource completion)
    {
        try
        {
            // 有界等待：被放弃的 QueryAsyncEnumerable 枚举器（未 DisposeAsync）会让操作租约
            // 永不完成--无诊断的无限挂起改为明确失败，指向泄漏原因。
            // ITM-581: 读一次入局部--测试并发修改该可变静态时，等待值与诊断消息保持一致
            TimeSpan disposeWaitTimeout = DisposeWaitTimeout;
            try
            {
                await Task.WhenAll(activeOperation, activeTransaction)
                    .WaitAsync(disposeWaitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException timeoutException)
            {
                throw new InvalidOperationException(
                    $"DataSession dispose timed out after {disposeWaitTimeout} waiting for an active operation. " +
                    "A likely cause is an abandoned QueryAsyncEnumerable enumerator that was never disposed; " +
                    "always consume it with 'await foreach' or dispose the enumerator explicitly.",
                    timeoutException);
            }
            await disposeCore().ConfigureAwait(false);
            lock (_sync) _state = 2;
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            lock (_sync) _state = 2;
            completion.TrySetException(exception);
        }
    }

    /// <summary>v4.5：Exit 不再写 AsyncLocal（省一次 EC 拷贝 ~300B）。
    /// _isActive = false 已让 IsCurrentOperationScope 返回 false。
    /// 如果 Dispose/WaitForActive 已延迟创建了 TCS，则 TrySetResult 唤醒等待者。</summary>
    private void Exit(object owner)
    {
        TaskCompletionSource? tcs;
        lock (_sync)
        {
            if (ReferenceEquals(_activeOperationOwner, owner))
            {
                _activeOperationOwner = null;
                _isActive = false;
                tcs = _activeOperation;
            }
            else
            {
                tcs = null;
            }
        }
        // v4.5：不再写 _currentOperationOwner.Value = null（省 EC 拷贝）
        // _isActive = false 已足够让 IsCurrentOperationScope 返回 false
        tcs?.TrySetResult();
    }

    internal readonly struct SessionOperationLease : IAsyncDisposable, IDisposable
    {
        // v4.5：删除 _operation(TCS) 字段 -- TCS 延迟创建在 SessionOperationState 内管理
        private readonly SessionOperationState? _state;

        internal SessionOperationLease(
            SessionOperationState state,
            object owner)
        {
            _state = state;
            Owner = owner;
        }

        internal object? Owner { get; }

        public void Dispose()
        {
            if (_state is not null && Owner is not null)
            {
                _state.Exit(Owner);
            }
        }
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
