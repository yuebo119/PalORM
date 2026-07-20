namespace PalORM;

/// <summary>弹性执行器——重试+退避+超时+熔断。
/// <para><b>重试策略</b>: 默认3次，指数退避(100ms→200ms→400ms)。仅重试 Provider 判定的瞬时异常和内部命令超时；调用方取消与确定性异常不重试。</para>
/// <para><b>熔断器</b>: 连续失败≥阈值→快速失败(抛 CircuitBreakerOpenException)→resetAfter 后恢复。熔断状态机抽到独立的 <see cref="CircuitBreaker"/> 类。</para>
/// <para><b>为什么不是 Polly</b>: 零外部依赖。核心弹性逻辑~100行, 引入 Polly(500K+)过度。</para></summary>
public sealed class ResilienceExecutor
{
    private readonly int _maxRetries;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly TimeSpan _timeout;
    private readonly Func<Exception, bool> _isTransient;
    private readonly CircuitBreaker _circuitBreaker;

    /// <summary>按配置创建执行器——重试/超时/熔断参数取自 <paramref name="options"/>，
    /// 瞬时异常判定默认为 <see cref="System.Data.Common.DbException.IsTransient"/>。</summary>
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
        // ITM-605: 包装 _backoff 统一覆盖两路径（DataSession.CreateAsync 连接重试 +
        // ResilienceExecutor.ExecuteAsync 命令重试）——此前 ITM-603 只在 CreateAsync 调用点
        // 加守卫，命令路径 Task.Delay(_backoff(attempt)) 抛 AOORE("delay") 不指向 RetryBackoff 配置。
        // 包装到构造函数后，所有走 ResilienceExecutor 的路径共享同一校验逻辑。
        Func<int, TimeSpan> sourceBackoff = options.RetryBackoff ?? GetDefaultBackoff;
        _backoff = attempt =>
        {
            TimeSpan delay = sourceBackoff(attempt);
            if (delay < TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"DbOptions.RetryBackoff(attempt={attempt}) returned a negative TimeSpan ({delay}). "
                    + "The delegate must return a non-negative delay.");
            return delay;
        };
        _timeout = options.CommandTimeout;
        _isTransient = isTransient;
        _circuitBreaker = new CircuitBreaker(options.CircuitBreakerThreshold, options.CircuitBreakerResetAfter);
    }

    /// <summary>执行带重试和熔断的异步操作。
    /// <para><b>幂等性约束（ITM-310）</b>: 命令超时被判为可重试，而超时不代表服务器未执行——
    /// INSERT 可能已提交，重试会重复写入。仅将幂等操作（查询/带唯一键的 upsert/条件更新）
    /// 交给本方法；非幂等写入请自行处理重试或依赖唯一约束去重。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S2189:LoopStopIncrementerNotTested",
        Justification = "for(;;) 退出靠 return（成功路径）和 throw（取消/超时/重试耗尽）；"
            + "attempt 仅作为 when 子句重试上限裁断与日志计数器，不进入 stop 条件是有意为之。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S1994:ForLoopConditionChanged",
        Justification = "同 S2189——for(;;) 退出靠 return/throw，attempt 不进入 stop 条件。")]
    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var (isHalfOpenProbe, generation) = _circuitBreaker.Enter();

        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(_timeout);
                    T result = await operation(timeout.Token).ConfigureAwait(false);
                    _circuitBreaker.RecordSuccess(isHalfOpenProbe, generation);
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
            _circuitBreaker.ReleaseCancelledProbe(isHalfOpenProbe);
            throw;
        }
        catch (Exception exception)
        {
            // 仅瞬时故障与超时计入熔断（ITM-506）：唯一约束冲突/SQL 语法错误等确定性
            // 失败是应用层问题，不应熔断整个执行器。TimeoutException 是内部命令超时的包装，
            // 属基础设施信号，计入。
            bool countsTowardCircuit = exception is TimeoutException || _isTransient(exception);
            _circuitBreaker.RecordFinalFailure(isHalfOpenProbe, countsTowardCircuit);
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
}
