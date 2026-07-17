using System.Data.Common;

namespace PalORM;

/// <summary>协调单个 DataSession 的数据库操作、事务逻辑流与释放生命周期。</summary>
internal sealed class SessionOperationState
{
    private readonly object _sync = new();
    private readonly AsyncLocal<object?> _currentOperationOwner = new();
    private readonly AsyncLocal<object?> _currentTransactionOwner = new();
    private TaskCompletionSource? _activeOperation;
    private TaskCompletionSource? _activeTransaction;
    private object? _activeOperationOwner;
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
            if (_activeOperation is not null)
            {
                if (owner is not null
                    && ReferenceEquals(owner, _activeOperationOwner))
                {
                    return default;
                }
                throw new InvalidOperationException(
                    "DataSession already has an active database operation.");
            }

            _activeOperationOwner = new object();
            _currentOperationOwner.Value = _activeOperationOwner;
            _activeOperation = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new SessionOperationLease(
                this, _activeOperation, _activeOperationOwner);
        }
    }

    internal SessionOperationLease EnterTransactionOperation()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                _state == 2
                || _transactionOwner is null
                || !ReferenceEquals(
                    _transactionOwner, _currentTransactionOwner.Value),
                this);
            if (_activeOperation is not null)
            {
                throw new InvalidOperationException(
                    "DataSession already has an active database operation.");
            }

            _activeOperationOwner = new object();
            _currentOperationOwner.Value = _activeOperationOwner;
            _activeOperation = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new SessionOperationLease(
                this, _activeOperation, _activeOperationOwner);
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
        {
            foreach (IAsyncDisposable resource in resources)
            {
                try { await resource.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupException ??= exception; }
            }
        }

        Task activeOperation;
        lock (_sync)
            activeOperation = _activeOperation?.Task ?? Task.CompletedTask;
        await activeOperation.ConfigureAwait(false);
        if (cleanupException is null) return;
        if (primaryException is null) throw cleanupException;
        primaryException.Data["PalORM.TransactionResourceCleanupException"] =
            cleanupException;
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
        if (ReferenceEquals(_currentTransactionOwner.Value, owner))
            _currentTransactionOwner.Value = null;
        completion?.TrySetResult();
    }

    internal bool IsCurrentOperationScope
    {
        get
        {
            lock (_sync)
            {
                return _activeOperationOwner is not null
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
            if (_activeOperation is not null || _activeTransaction is not null)
            {
                throw new InvalidOperationException(
                    "DataSession already has an active database operation or transaction flow.");
            }
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
            await activeOperation.ConfigureAwait(false);
            await activeTransaction.ConfigureAwait(false);
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

    private void Exit(TaskCompletionSource operation, object owner)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation = null;
                _activeOperationOwner = null;
            }
        }
        if (ReferenceEquals(_currentOperationOwner.Value, owner))
            _currentOperationOwner.Value = null;
        operation.TrySetResult();
    }

    internal readonly struct SessionOperationLease : IAsyncDisposable, IDisposable
    {
        private readonly SessionOperationState? _state;
        private readonly TaskCompletionSource? _operation;

        internal SessionOperationLease(
            SessionOperationState state,
            TaskCompletionSource operation,
            object owner)
        {
            _state = state;
            _operation = operation;
            Owner = owner;
        }

        internal object? Owner { get; }

        public void Dispose()
        {
            if (_state is not null
                && _operation is not null
                && Owner is not null)
            {
                _state.Exit(_operation, Owner);
            }
        }
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
