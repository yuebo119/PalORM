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

    /// <summary>执行带重试和熔断的异步操作。
    /// <para><b>幂等性约束（ITM-310）</b>: 命令超时被判为可重试，而超时不代表服务器未执行——
    /// INSERT 可能已提交，重试会重复写入。仅将幂等操作（查询/带唯一键的 upsert/条件更新）
    /// 交给本方法；非幂等写入请自行处理重试或依赖唯一约束去重。</para></summary>
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
                catch (OperationCanceledException timeoutException) when (!ct.IsCancellationRequested)
                {
                    // 内部命令超时且重试耗尽：包装为 TimeoutException，调用方可与"我被取消"区分。
                    throw new TimeoutException(
                        $"Command timed out after {_timeout} (attempt {attempt + 1}/{_maxRetries + 1}).",
                        timeoutException);
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
            // 仅探针自身失败释放探针占用：熔断前进入的旧操作失败不得清掉在飞探针的标志，
            // 否则 resetAfter 过后会放行第二个并发探针，打破半开单探针不变式。
            if (isHalfOpenProbe)
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
            // 探针无论新旧都先释放占用标志，避免陈旧探针提前返回导致半开态永久无探针可进。
            if (entry.IsHalfOpenProbe)
                _halfOpenProbeActive = false;

            // generation 防陈旧对探针同样生效：gen N 的探针成功不得关闭 gen N+1 的熔断
            //（其成功证明的是重开前的数据库状态）。新熔断周期由新探针验证。
            // 已知活性取舍（ITM-209）：探针在飞期间陈旧操作失败推进 generation，
            // 该探针随后的真实成功会因代不匹配被丢弃，熔断多保持一个周期——
            // 保守方向（宁多熔断不误关），下一轮探针会正确裁决。
            if (entry.Generation != _circuitGeneration)
                return;

            _failureCount = 0;
            _circuitOpen = false;
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
