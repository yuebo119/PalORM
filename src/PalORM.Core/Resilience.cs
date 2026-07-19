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
    // ITM-538: 熔断恢复时点用 DateTime.UtcNow 墙钟，已知取舍——对系统时钟回拨/NTP 校时敏感
    // （回拨可能延长或缩短熔断窗口）。改用 Environment.TickCount64/Stopwatch 单调时钟可根治，
    // 但与已提交的熔断逻辑耦合，留待确有需要时统一改造，当前不动逻辑。
    private DateTime _circuitOpenUntil;
    private bool _circuitOpen;
    private bool _halfOpenProbeActive;
    private long _circuitGeneration;
    private readonly Lock _lock = new();

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
        catch (Exception exception)
        {
            // 仅瞬时故障与超时计入熔断（ITM-506）：唯一约束冲突/SQL 语法错误等确定性
            // 失败是应用层问题，不应熔断整个执行器。TimeoutException 是内部命令超时的包装，
            // 属基础设施信号，计入。
            bool countsTowardCircuit = exception is TimeoutException || _isTransient(exception);
            RecordFinalFailure(entry.IsHalfOpenProbe, countsTowardCircuit);
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

    private void RecordFinalFailure(bool isHalfOpenProbe, bool countsTowardCircuit)
    {
        lock (_lock)
        {
            // 仅探针自身失败释放探针占用（无论是否计数）：旧操作失败不得清掉在飞探针标志。
            if (isHalfOpenProbe)
                _halfOpenProbeActive = false;

            // 确定性异常不推进熔断状态（ITM-506）。
            if (!countsTowardCircuit)
                return;

            // 半开探针失败：重新武装熔断（顺延恢复时间点并推进 generation）——这是探针的职责。
            if (isHalfOpenProbe)
            {
                _failureCount = _circuitBreakerThreshold;
                _circuitOpen = true;
                _circuitGeneration++;
                _circuitOpenUntil = DateTime.UtcNow.Add(_circuitBreakerResetAfter);
                return;
            }

            // 熔断已打开期间，在飞旧的非探针失败不再重复顺延恢复时间点（ITM-507）——否则多个
            // 慢操作先后失败可无限延长熔断窗口。仅从关闭态首次跨阈值时开启熔断。
            if (_circuitOpen)
                return;

            _failureCount += 1;

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

    // ITM-598: 调用方取消（ct.IsCancellationRequested）不算数据库失败，不应推进熔断 failureCount。
    // 把 _circuitOpenUntil 重置为当前时间让熔断窗口"立即到期"——下次请求进入半开探针裁决，
    // 由真实请求结果决定熔断是否真正关闭。频繁取消时熔断器确实会多次进入探针路径，
    // 但每次探针是"尝试建立真实数据库连接"的轻量操作，且仅在 isHalfOpenProbe=true 时进入此分支。
    // 与"连续失败 N 次后开闸 resetAfter 秒"的契约一致：取消不是失败，不延长熔断时间。
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
    /// <summary>以熔断状态描述（含恢复时间点）创建异常。</summary>
    public CircuitBreakerOpenException(string message) : base(message) { }
}

/// <summary>PalORM 基础异常。</summary>
public class PalORMException : Exception
{
    /// <summary>以错误描述创建异常。</summary>
    public PalORMException(string message) : base(message) { }

    /// <summary>以错误描述和原始异常创建异常——保留底层失败原因供调用方追溯。</summary>
    public PalORMException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>并发冲突异常（乐观锁）。</summary>
public sealed class ConcurrencyConflictException : PalORMException
{
    /// <summary>以冲突描述（实体/版本信息）创建异常。</summary>
    public ConcurrencyConflictException(string message) : base(message) { }
}
