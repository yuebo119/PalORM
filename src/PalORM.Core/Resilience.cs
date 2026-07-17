namespace PalORM;

/// <summary>弹性执行器——重试+退避+超时+熔断。
/// <para><b>重试策略</b>: 默认3次，指数退避(100ms→200ms→400ms)。仅重试 Provider 判定的瞬时异常和内部命令超时；调用方取消与确定性异常不重试。</para>
/// <para><b>熔断器</b>: 连续失败≥阈值→快速失败(抛 CircuitBreakerOpenException)→resetAfter 后恢复。</para>
/// <para><b>为什么不是 Polly</b>: 零外部依赖。核心弹性逻辑~100行, 引入 Polly(500K+)过度。</para></summary>
public sealed class ResilienceExecutor
{
    private readonly int _maxRetries;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly TimeSpan _timeout;
    private readonly Func<Exception, bool> _isTransient;
    private readonly int _circuitBreakerThreshold;
    private readonly TimeSpan _circuitBreakerResetAfter;

    private int _failureCount;
    private DateTime _circuitOpenUntil;
    private bool _circuitOpen;
    private bool _halfOpenProbeActive;
    private long _circuitGeneration;
    private readonly object _lock = new();

    public ResilienceExecutor(DbOptions options)
        : this(options, static exception => exception is System.Data.Common.DbException { IsTransient: true })
    {
    }

    internal ResilienceExecutor(DbOptions options, Func<Exception, bool> isTransient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isTransient);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetries);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CircuitBreakerThreshold);

        _maxRetries = options.MaxRetries;
        _backoff = options.RetryBackoff ?? GetDefaultBackoff;
        _timeout = options.CommandTimeout;
        _isTransient = isTransient;
        _circuitBreakerThreshold = options.CircuitBreakerThreshold;
        _circuitBreakerResetAfter = options.CircuitBreakerResetAfter;
    }

    /// <summary>执行带重试和熔断的异步操作。</summary>
    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CircuitEntry entry = EnterCircuit();

        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(_timeout);
                    T result = await operation(timeout.Token).ConfigureAwait(false);
                    RecordSuccess(entry);
                    return result;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (attempt < _maxRetries && IsRetryable(exception, ct))
                {
                    await Task.Delay(_backoff(attempt), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReleaseCancelledProbe(entry.IsHalfOpenProbe);
            throw;
        }
        catch
        {
            RecordFinalFailure(entry.IsHalfOpenProbe);
            throw;
        }
    }

    /// <summary>执行带重试和熔断的异步操作（无返回值）。</summary>
    public async ValueTask ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync(async c => { await operation(c).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
    }

    internal static TimeSpan GetDefaultBackoff(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        int shift = Math.Min(attempt, 18);
        long milliseconds = Math.Min(100L << shift, 30_000L);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private bool IsRetryable(Exception exception, CancellationToken callerToken)
        => exception is OperationCanceledException
            ? !callerToken.IsCancellationRequested
            : _isTransient(exception);

    private CircuitEntry EnterCircuit()
    {
        lock (_lock)
        {
            if (_circuitBreakerThreshold <= 0 || !_circuitOpen)
                return new CircuitEntry(false, _circuitGeneration);

            if (DateTime.UtcNow < _circuitOpenUntil || _halfOpenProbeActive)
                throw new CircuitBreakerOpenException($"Circuit breaker open until {_circuitOpenUntil:O}");

            _halfOpenProbeActive = true;
            return new CircuitEntry(true, _circuitGeneration);
        }
    }

    private void RecordFinalFailure(bool isHalfOpenProbe)
    {
        lock (_lock)
        {
            _failureCount = isHalfOpenProbe
                ? _circuitBreakerThreshold
                : _failureCount + 1;
            _halfOpenProbeActive = false;

            if (_circuitBreakerThreshold > 0 && _failureCount >= _circuitBreakerThreshold)
            {
                _circuitOpen = true;
                _circuitGeneration++;
                _circuitOpenUntil = DateTime.UtcNow.Add(_circuitBreakerResetAfter);
            }
        }
    }

    private void RecordSuccess(CircuitEntry entry)
    {
        lock (_lock)
        {
            if (!entry.IsHalfOpenProbe && entry.Generation != _circuitGeneration)
                return;

            _failureCount = 0;
            _circuitOpen = false;
            _halfOpenProbeActive = false;
            _circuitOpenUntil = default;
        }
    }

    private void ReleaseCancelledProbe(bool isHalfOpenProbe)
    {
        if (!isHalfOpenProbe)
            return;

        lock (_lock)
        {
            _halfOpenProbeActive = false;
            _circuitOpenUntil = DateTime.UtcNow;
        }
    }

    private readonly record struct CircuitEntry(bool IsHalfOpenProbe, long Generation);
}

/// <summary>熔断器打开异常。</summary>
public sealed class CircuitBreakerOpenException : PalORMException
{
    public CircuitBreakerOpenException(string message) : base(message) { }
}

/// <summary>PalORM 基础异常。</summary>
public class PalORMException : Exception
{
    public PalORMException(string message) : base(message) { }
    public PalORMException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>并发冲突异常（乐观锁）。</summary>
public sealed class ConcurrencyConflictException : PalORMException
{
    public ConcurrencyConflictException(string message) : base(message) { }
}
