using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace PalORM;

public sealed partial class DataSession<TProvider>
    where TProvider : IDbProvider
{
    /// <summary>见 DataSession 主文档。</summary>
    public async ValueTask SavepointAsync(DbTransaction tran, string name, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(tran);
        // ITM-575: 与 UseTransaction 对称——异连接事务在驱动层的错误形态不可控，库内明确失败
        if (!ReferenceEquals(tran.Connection, _conn))
            throw new ArgumentException("事务必须属于当前 DataSession 的主连接。", nameof(tran));
        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        cmd.CommandText = $"SAVEPOINT {TProvider.QuoteIdentifier(name)}";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>回滚到保存点。</summary>
    public async ValueTask RollbackToAsync(DbTransaction tran, string name, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(tran);
        if (!ReferenceEquals(tran.Connection, _conn))
            throw new ArgumentException("事务必须属于当前 DataSession 的主连接。", nameof(tran));
        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        cmd.CommandText = $"ROLLBACK TO SAVEPOINT {TProvider.QuoteIdentifier(name)}";
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
    public async ValueTask WithTransaction(Func<CancellationToken, Task> action,
        IsolationLevel? level = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        object owner = _operationState.EnterTransactionFlow();
        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction? transaction = null;
        Exception? primaryException = null;
        try
        {
            transaction = await BeginTransactionAsync(level, ct).ConfigureAwait(false);
            try
            {
                await action(ct).ConfigureAwait(false);
                await _operationState.DisposeTransactionResourcesAsync(null)
                    .ConfigureAwait(false);
                using SessionOperationState.SessionOperationLease operation =
                    _operationState.EnterTransactionOperation();
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                primaryException = exception;
                await _operationState.DisposeTransactionResourcesAsync(exception)
                    .ConfigureAwait(false);
                await RollbackTransactionPreservingAsync(transaction, exception)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (transaction is not null)
            {
                _operationState.RestoreTransaction(
                    transaction, previousTransaction);
            }
            try
            {
                if (transaction is not null)
                {
                    await TransactionCleanup.DisposeTransactionPreservingAsync(
                        transaction, primaryException).ConfigureAwait(false);
                }
            }
            finally
            {
                _operationState.ExitTransactionFlow(owner);
            }
        }
    }

    /// <summary>事务包裹执行（带返回值）。callback 内仅支持顺序数据库操作，不支持嵌套事务。</summary>
    public async ValueTask<T> WithTransaction<T>(Func<CancellationToken, Task<T>> action,
        IsolationLevel? level = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        object owner = _operationState.EnterTransactionFlow();
        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction? transaction = null;
        Exception? primaryException = null;
        try
        {
            transaction = await BeginTransactionAsync(level, ct).ConfigureAwait(false);
            try
            {
                T result = await action(ct).ConfigureAwait(false);
                await _operationState.DisposeTransactionResourcesAsync(null)
                    .ConfigureAwait(false);
                using SessionOperationState.SessionOperationLease operation =
                    _operationState.EnterTransactionOperation();
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (Exception exception)
            {
                primaryException = exception;
                await _operationState.DisposeTransactionResourcesAsync(exception)
                    .ConfigureAwait(false);
                await RollbackTransactionPreservingAsync(transaction, exception)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (transaction is not null)
            {
                _operationState.RestoreTransaction(
                    transaction, previousTransaction);
            }
            try
            {
                if (transaction is not null)
                {
                    await TransactionCleanup.DisposeTransactionPreservingAsync(
                        transaction, primaryException).ConfigureAwait(false);
                }
            }
            finally
            {
                _operationState.ExitTransactionFlow(owner);
            }
        }
    }

    private async ValueTask RollbackTransactionPreservingAsync(
        DbTransaction transaction,
        Exception primaryException)
    {
        try
        {
            using SessionOperationState.SessionOperationLease operation =
                _operationState.EnterTransactionOperation();
            await transaction.RollbackAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception rollbackException)
        {
            primaryException.Data["PalORM.RollbackException"] =
                rollbackException;
        }
    }
}
