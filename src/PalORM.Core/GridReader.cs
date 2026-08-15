using System.Data.Common;
using System.Runtime.ExceptionServices;

namespace PalORM;

/// <summary>多结果集读取器——QueryMultipleAsync 返回。
/// <para><b>为什么自己管理生命周期</b>: DbDataReader 在使用期间必须保持连接打开。
/// GridReader 同时拥有 DbCommand 和 DbDataReader——DisposeAsync 时先关 reader 再关 command。</para>
/// <para>同一实例仅允许一个活动读取；释放会等待活动读取完成，并拒绝后续读取。</para></summary>
public sealed class GridReader : IAsyncDisposable
{
    private readonly DbDataReader _reader;
    private readonly DbCommand _command;
    private readonly ConnectionLease _lease;
    private readonly SessionOperationState.SessionOperationLease _operation;
    private readonly QueryObservation? _observation;
    private readonly Lock _sync = new();
    private TaskCompletionSource? _activeRead;
    private Task? _disposeTask;
    private int _state;
    private readonly bool _validateColumnOrder;

    internal GridReader(DbDataReader reader, DbCommand command, ConnectionLease lease,
        QueryObservation? observation = null,
        SessionOperationState.SessionOperationLease operation = default,
        bool validateColumnOrder = false)
    {
        _validateColumnOrder = validateColumnOrder;
        _reader = reader;
        _command = command;
        _lease = lease;
        _operation = operation;
        _observation = observation;
    }

    /// <summary>读取当前结果集。</summary>
    public async ValueTask<List<T>> ReadAsync<T>(CancellationToken ct = default) where T : class, new()
    {
        EnterRead();
        try
        {
            if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory))
                throw new InvalidOperationException($"Type '{typeof(T).Name}' not registered.");

            ColumnOrderValidator.Validate<T>(_reader, _validateColumnOrder);
            // v4.4：对齐 ExecuteQueryAsync/QueryAsync 的 16 起步容量
            List<T> list = new(16);
            var typedFactory = (Func<DbDataReader, T>)factory;
            while (await _reader.ReadAsync(ct).ConfigureAwait(false))
                list.Add(typedFactory(_reader));

            await _reader.NextResultAsync(ct).ConfigureAwait(false);
            return list;
        }
        catch (Exception exception)
        {
            _observation?.Complete(exception is OperationCanceledException && ct.IsCancellationRequested
                ? "cancelled"
                : "error");
            throw;
        }
        finally
        {
            ExitRead();
        }
    }

    /// <summary>读取当前结果集第一行（不物化全量）。</summary>
    public async ValueTask<T?> ReadFirstAsync<T>(CancellationToken ct = default) where T : class, new()
    {
        EnterRead();
        try
        {
            if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory))
                throw new InvalidOperationException($"Type '{typeof(T).Name}' not registered.");

            ColumnOrderValidator.Validate<T>(_reader, _validateColumnOrder);
            var typedFactory = (Func<DbDataReader, T>)factory;
            if (await _reader.ReadAsync(ct).ConfigureAwait(false))
            {
                T result = typedFactory(_reader);
                await _reader.NextResultAsync(ct).ConfigureAwait(false);
                return result;
            }
            await _reader.NextResultAsync(ct).ConfigureAwait(false);
            return default;
        }
        catch (Exception exception)
        {
            _observation?.Complete(exception is OperationCanceledException && ct.IsCancellationRequested
                ? "cancelled"
                : "error");
            throw;
        }
        finally
        {
            ExitRead();
        }
    }

    /// <summary>等待活动读取完成后逐级释放 reader/command/connection/session 租约。
    /// <para><b>调用方契约</b>: 必须释放（推荐 await using）。非事务流中忘记释放时，
    /// 操作租约永不退出——会话后续操作明确失败，读路由自有连接依赖 GC 终结器兜底回收。</para>
    /// <para>等待活动读取时不传递取消：活动 ReadAsync 因网络阻塞时以其自身 ct 为准（文档化设计取舍）。</para></summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task activeRead;
        lock (_sync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _state = 1;
            activeRead = _activeRead?.Task ?? Task.CompletedTask;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = DisposeAndCompleteAsync(activeRead, completion);
        return new ValueTask(completion.Task);
    }

    private void EnterRead()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state != 0, this);
            if (_activeRead is not null)
                throw new InvalidOperationException("GridReader already has an active read operation.");
            _activeRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ExitRead()
    {
        TaskCompletionSource? completion;
        lock (_sync)
        {
            completion = _activeRead;
            _activeRead = null;
        }
        completion?.TrySetResult();
    }

    private async Task DisposeAndCompleteAsync(
        Task activeRead,
        TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync(activeRead).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync(Task activeRead)
    {
        // R2 修复：对齐 SessionOperationState 的超时保护——活动 ReadAsync 网络阻塞时
        // 无超时的 await 会导致 DisposeAsync 永久挂起，await using 卡死。
        // 使用 SessionOperationState.DisposeWaitTimeout（5 分钟）+ 超时诊断异常。
        // ITM-629：静态可变值读一次入局部（对齐 ITM-581 的 SessionOperationState 修法）——
        // 双读在等待期间被改写时，实际等待时长与诊断消息不一致。
        // ITM-647(r4)：超时不再前置 throw——原路径 reader/command/lease/operation 四级
        // 均未释放且观测挂起。改为记录挂起异常，先走完统一清理链再抛。
        Exception? hangException = await WaitForActiveReadAsync(activeRead);
        Exception? cleanupException = null;
        try { await _reader.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupException = exception; }

        try { await _command.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (cleanupException is null) cleanupException = exception;
            else cleanupException.Data["PalORM.CommandCleanupException"] = exception;
        }

        try { await _lease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (cleanupException is null) cleanupException = exception;
            else cleanupException.Data["PalORM.ConnectionCleanupException"] = exception;
        }

        try { await _operation.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            if (cleanupException is null) cleanupException = exception;
            else cleanupException.Data["PalORM.OperationCleanupException"] = exception;
        }

        lock (_sync) _state = 2;
        // ITM-647(r4)：挂起（等待超时）优先于清理异常抛出——它是根因
        Exception? fatal = hangException ?? cleanupException;
        if (fatal is not null)
        {
            _observation?.Complete("error");
            ExceptionDispatchInfo.Capture(fatal).Throw();
        }

        _observation?.Complete("success");
    }

    /// <summary>有界等待活动 ReadAsync（ITM-629 单读 + ITM-647 超时转挂起异常不前置抛）。
    /// r5-A1：单读局部在 647 提取重构时意外丢失——本处恢复（与 SessionOperationState 两处同型）。</summary>
    private static async Task<Exception?> WaitForActiveReadAsync(Task activeRead)
    {
        TimeSpan waitTimeout = SessionOperationState.DisposeWaitTimeout;
        try
        {
            await activeRead.WaitAsync(waitTimeout).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException)
        {
            return new InvalidOperationException(
                $"GridReader Dispose timed out after {waitTimeout} "
                + "waiting for an active ReadAsync to complete. "
                + "Ensure all ReadAsync calls complete before disposing the GridReader.");
        }
    }
}
