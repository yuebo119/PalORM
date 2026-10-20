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
    /// <summary>整次操作的总预算（RES-003，2026-09-23）；Zero = 不设（既有最坏墙钟语义不变）。</summary>
    private readonly TimeSpan _overallDeadline;
    private readonly Func<Exception, bool> _isTransient;
    private readonly CircuitBreaker _circuitBreaker;

    /// <summary>策略直通判定——零重试且熔断禁用时，内置只读管线跳过执行器
    /// 以保持热路径零额外开销（链接 CTS 分配/状态机锁）。
    /// <para><b>注意</b>：仅内部管线短路使用；公共 <see cref="ExecuteAsync{T}"/> 不短路——
    /// 显式调用方已声明要弹性语义（含命令超时包装），即使配置为零也保持行为一致。</para></summary>
    internal bool IsPassThrough => _maxRetries == 0 && !_circuitBreaker.IsEnabled;

    /// <summary>S3：是否还有重试预算（直发失败后决定续跑还是直接记账）。</summary>
    internal bool CanRetry => _maxRetries > 0;

    /// <summary>按配置创建执行器——重试/超时/熔断参数取自 <paramref name="options"/>，
    /// 瞬时异常判定默认为 <see cref="System.Data.Common.DbException.IsTransient"/>。</summary>
    public ResilienceExecutor(DbOptions options)
        : this(options, static exception => exception is System.Data.Common.DbException { IsTransient: true })
    {
    }

    internal ResilienceExecutor(DbOptions options, Func<Exception, bool> isTransient, Type? providerType = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isTransient);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetries);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CircuitBreakerThreshold);

        _maxRetries = options.MaxRetries;
        // RES-003（2026-09-23）：总预算默认 Zero = 不设（保持既有最坏墙钟语义不变）。
        _overallDeadline = options.OverallDeadline;
        // ITM-605: 包装 _backoff 统一覆盖两路径（DataSession.CreateAsync 连接重试 +
        // ResilienceExecutor.ExecuteAsync 命令重试）——此前 ITM-603 只在 CreateAsync 调用点
        // 加守卫，命令路径 Task.Delay(_backoff(attempt)) 抛 AOORE("delay") 不指向 RetryBackoff 配置。
        // 包装到构造函数后，所有走 ResilienceExecutor 的路径共享同一校验逻辑。
        // C11（2026-10-12 审读重开·安全子集）：默认退避恒非负（GetDefaultBackoff 数学上
        // ≥50ms），守卫只对自定义委托必要——默认路径跳过包装委托，省每会话一个闭包；
        // 自定义路径保持原守卫（ITM-603/605 双路径校验语义不变）。
        if (options.RetryBackoff is { } customBackoff)
        {
            _backoff = attempt =>
            {
                TimeSpan delay = customBackoff(attempt);
                if (delay < TimeSpan.Zero)
                    throw new InvalidOperationException(
                        $"DbOptions.RetryBackoff(attempt={attempt}) returned a negative TimeSpan ({delay}). "
                        + "The delegate must return a non-negative delay.");
                return delay;
            };
        }
        else
        {
            _backoff = GetDefaultBackoff;
        }
        // ITM-619：DbOptions 契约 TimeSpan.Zero = 无限等待（Validate 允许 Zero），但
        // CancelAfter(Zero) 是立即取消——归一为 InfiniteTimeSpan 保持契约语义。
        // r19/ITM-696：直接构造未 Validate 的 options 时负值会在执行期 CancelAfter(负)
        // 抛 AOORE 且消息不指向配置——构造期拒绝（与 RetryBackoff 守卫同族）。
        ArgumentOutOfRangeException.ThrowIfNegative(
            options.CommandTimeout.Ticks, nameof(options.CommandTimeout));
        _timeout = options.CommandTimeout == TimeSpan.Zero
            ? Timeout.InfiniteTimeSpan
            : options.CommandTimeout;
        _isTransient = isTransient;
        // RES-002（2026-09-23）：会话级（默认）每个执行器一个熔断器；进程级按
        // (Provider, 连接串, 阈值, 冷却) 共享——否则"一请求一会话"下阈值永远达不到。
        // providerType 为 null（如直接构造执行器的测试）时退化为会话级，避免无键可用。
        _circuitBreaker = options.CircuitBreakerScope == CircuitBreakerScope.Process && providerType is not null
            ? CircuitBreakerRegistry.GetOrAdd(providerType, options)
            : new CircuitBreaker(options.CircuitBreakerThreshold, options.CircuitBreakerResetAfter);
    }

    /// <summary>执行带重试和熔断的异步操作。
    /// <para><b>幂等性约束（ITM-310）</b>: 命令超时被判为可重试，而超时不代表服务器未执行——
    /// INSERT 可能已提交，重试会重复写入。仅将幂等操作（查询/带唯一键的 upsert/条件更新）
    /// 交给本方法；非幂等写入请自行处理重试或依赖唯一约束去重。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S4136",
        Justification = "结构体状态重载与 void 重载分置内核两侧(编排取舍,语义分组优先)。")]
    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await ExecuteAsyncCore<Func<CancellationToken, Task<T>>, T>(
            static (op, token) => op(token), operation, ct).ConfigureAwait(false);
    }

    /// <summary>R4-λ(2026-10-13 用户点名"继续"轮):结构体状态重载——operation 收
    /// (state, token) 两参,调用方经 static lambda + 值元组传参,热路径闭包分配归零
    /// (static lambda 不捕获,每个调用点一份由编译器缓存;TState 是值类型按值传递)。
    /// step10 证伪的是手写 struct 内核接口形态(收益 -61B 判"低于当时 200B 线");
    /// 本形态是同一收益的零架构代价版本:重试/超时/熔断语义逐位复用既有内核。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S4136",
        Justification = "结构体状态重载紧邻经典重载与内核放置(与 void 重载隔开是编排取舍,语义分组优先)。")]
    public async ValueTask<TResult> ExecuteAsync<TState, TResult>(
        Func<TState, CancellationToken, Task<TResult>> operation, TState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await ExecuteAsyncCore(operation, state, ct).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S2189:LoopStopIncrementerNotTested",
        Justification = "for(;;) 退出靠 return（成功路径）和 throw（取消/超时/重试耗尽）；"
            + "attempt 仅作为 when 子句重试上限裁断与日志计数器，不进入 stop 条件是有意为之。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S1994:ForLoopConditionChanged",
        Justification = "同 S2189——for(;;) 退出靠 return/throw，attempt 不进入 stop 条件。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "RES-003 起本方法有 6 条异常分支（调用方取消 / 总预算到期 / 超时重试 / "
            + "瞬时重试 / 超时耗尽包装 / 外层熔断记账），每条过滤条件都必须与异常形态严格对应；"
            + "拆分会把重试语义打散到多处、降低可审性（与同方法 S2189/S1994 同因）。")]
    private async ValueTask<TResult> ExecuteAsyncCore<TState, TResult>(
        Func<TState, CancellationToken, Task<TResult>> operation, TState state, CancellationToken ct,
        int startAttempt = 0)
    {
        var (isHalfOpenProbe, generation, probeToken) = _circuitBreaker.Enter();

        // RES-003（2026-09-23）：总预算（Zero = 不设）——比单次尝试超时更强的上界，到期即停重试。
        // 默认不设时本块零开销（overall 为 null，attemptScope 就是调用方 ct）。
        using CancellationTokenSource? overall = _overallDeadline > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        overall?.CancelAfter(_overallDeadline);
        CancellationToken attemptScope = overall?.Token ?? ct;

        try
        {
            for (int attempt = startAttempt; ; attempt++)
            {
                CancellationTokenSource? timeout = null;
                try
                {
                    // A3（2026-10-01 全 API 逐项轮）：CTS 单构化——attemptScope 不可取消时
                    // 用单构 CTS 替代 linked（省 registration 结构与两跳取消链）；无限超时
                    // 且不可取消时完全不建 CTS（本就不存在超时事件）。下游 catch 判据均为
                    // `timeout is not null`，null 形态下内部超时分支不可达，语义等价。
                    if (_timeout == Timeout.InfiniteTimeSpan && !attemptScope.CanBeCanceled)
                    {
                        TResult resultNoTimeout = await operation(state, attemptScope).ConfigureAwait(false);
                        _circuitBreaker.RecordSuccess(isHalfOpenProbe, generation, probeToken);
                        return resultNoTimeout;
                    }
                    // R3-CTS（2026-10-13 用户点名轮）：非链接形态经进程级租借池复用——
                    // step10 证伪的是"移除 CTS 依赖驱动超时"(丢语义)；池化保持超时语义
                    // 逐位不变只消分配(TryReset 官方热路径复用模式,见 PooledCts 文档)。
                    // 链接形态(源 token 注册不可跨次复用)恒新建+Dispose。
                    timeout = attemptScope.CanBeCanceled
                        ? CancellationTokenSource.CreateLinkedTokenSource(attemptScope)
                        : PooledCts.Rent();
                    if (_timeout != Timeout.InfiniteTimeSpan)
                        timeout.CancelAfter(_timeout);
                    TResult result = await operation(state, timeout.Token).ConfigureAwait(false);
                    _circuitBreaker.RecordSuccess(isHalfOpenProbe, generation, probeToken);
                    return result;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException overallException) when (overall is not null
                    && overall.IsCancellationRequested)
                {
                    // RES-003：总预算到期不再重试——最坏墙钟由 (MaxRetries+1)×CommandTimeout+Σ退避
                    // 收敛为 OverallDeadline。与单次命令超时同口径包装（Data 标记可识别）。
                    throw WrapOverallDeadline(attempt, overallException);
                }
                catch (OperationCanceledException) when (timeout is not null
                    && timeout.IsCancellationRequested && attempt < _maxRetries)
                {
                    // ITM-630：仅超时 token 触发的 OCE 可重试——operation 内部自取消
                    // （非调用方 ct、非超时）原样上抛：重试会放大已取消操作的副作用。
                    await DelayBeforeRetryAsync(attempt - startAttempt, overall, attemptScope).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException
                    && attempt < _maxRetries && _isTransient(exception))
                {
                    await DelayBeforeRetryAsync(attempt - startAttempt, overall, attemptScope).ConfigureAwait(false);
                }
                catch (OperationCanceledException timeoutException) when (timeout is not null
                    && timeout.IsCancellationRequested)
                {
                    // 内部命令超时且重试耗尽：包装为 TimeoutException，调用方可与"我被取消"区分。
                    // ITM-647：attempt 从 0 起，分母 = _maxRetries + 1 = 1 次初始尝试 + _maxRetries 次重试。
                    // ITM-667：Data 标记替代消息前缀识别——用户自抛同前缀 TimeoutException
                    // 不再被误判为基础设施超时计入熔断。
                    var wrappedTimeout = new TimeoutException(
                        $"Command timed out after {_timeout} (attempt {attempt + 1} of {_maxRetries + 1}; " +
                        $"1 initial attempt + {_maxRetries} retries).",
                        timeoutException);
                    wrappedTimeout.Data["PalORM.InfrastructureTimeout"] = true;
                    throw wrappedTimeout;
                }
                finally
                {
                    // R3-CTS:非链接形态归还池(TryReset 失败即已取消 → 池内 Dispose),
                    // 链接形态照旧 Dispose(源 token 注册不可复用)。
                    if (timeout is not null)
                    {
                        if (attemptScope.CanBeCanceled)
                            timeout.Dispose();
                        else
                            PooledCts.Return(timeout);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _circuitBreaker.ReleaseCancelledProbe(isHalfOpenProbe, generation, probeToken);
            throw;
        }
        catch (Exception exception)
        {
            _circuitBreaker.RecordFinalFailure(
                isHalfOpenProbe, CountsTowardCircuit(exception), generation, probeToken);
            throw;
        }
    }

    /// <summary>S3：首次尝试已在执行器外直接执行并失败后，从此处继续剩余重试（attempt 从 1 起）。
    /// 退避序号按"首次失败后的第 N 次重试"计算（attempt - startAttempt），与既有循环逐位一致；
    /// 首次失败本身不计入熔断，最终结果由本循环的 RecordSuccess/RecordFinalFailure 记账。</summary>
    internal ValueTask<TResult> ExecuteAfterFirstFailureAsync<TState, TResult>(
        Func<TState, CancellationToken, Task<TResult>> operation, TState state, CancellationToken ct = default)
        => ExecuteAsyncCore(operation, state, ct, startAttempt: 1);

    /// <summary>S3：首次直接尝试失败且不再重试（MaxRetries=0）时，把最终失败计入熔断器。
    /// 与 ExecuteAsyncCore 的尾段同口径：仅瞬时故障与基础设施超时计数。</summary>
    internal void RecordDirectFailure(Exception exception)
    {
        if (!_circuitBreaker.IsEnabled) return;
        _circuitBreaker.RecordFinalFailure(
            isHalfOpenProbe: false,
            CountsTowardCircuit(exception),
            _circuitBreaker.CurrentGeneration,
            probeToken: 0);
    }

    /// <summary>执行带重试和熔断的异步操作（无返回值）。</summary>
    /// <summary>使用会话级弹性策略执行操作（自动重试+熔断，无返回值重载）。</summary>
    public async ValueTask ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync(async c => { await operation(c).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
    }

    /// <summary>重试退避等待（A3）：等待与尝试范围共用 <paramref name="attemptScope"/>——总预算到期即中断等待，
    /// 包装为超时抛出；不再睡满退避后用已取消的 token 多发起一次操作（RES-003 的到期即停契约）。</summary>
    private async Task DelayBeforeRetryAsync(
        int attempt, CancellationTokenSource? overall, CancellationToken attemptScope)
    {
        try
        {
            await Task.Delay(_backoff(attempt), attemptScope).ConfigureAwait(false);
        }
        catch (OperationCanceledException delayException) when (overall is not null
            && overall.IsCancellationRequested)
        {
            throw WrapOverallDeadline(attempt, delayException);
        }
    }

    /// <summary>总预算到期的超时包装（RES-003）：与单次命令超时同口径，Data 标记供基础设施超时识别。</summary>
    private TimeoutException WrapOverallDeadline(int attempt, OperationCanceledException inner)
    {
        var wrapped = new TimeoutException(
            $"Operation exceeded the overall deadline of {_overallDeadline} "
            + $"(attempt {attempt + 1} of {_maxRetries + 1}).",
            inner);
        wrapped.Data["PalORM.InfrastructureTimeout"] = true;
        return wrapped;
    }

    internal static TimeSpan GetDefaultBackoff(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        int shift = Math.Min(attempt, 18);
        long milliseconds = Math.Min(100L << shift, 30_000L);
        // RES-003（2026-09-23）：默认退避加 50%~100% 抖动。纯指数退避在多实例同时恢复时让
        // 所有客户端对齐到达，把刚恢复的服务二次打垮；同仓 PgNotificationListener 的重连退避
        // 早已加抖动，理由相同。
        double jitter = 0.5 + (Random.Shared.NextDouble() * 0.5);
        return TimeSpan.FromMilliseconds(milliseconds * jitter);
    }

    /// <summary>瞬时故障判定（READ-001，2026-09-23）：暴露 Provider 传入的瞬时判定——读路径在
    /// 瞬时失败时据此丢弃缓存的读连接（确定性失败不该丢弃健康连接）。</summary>
    internal bool IsTransient(Exception exception) => _isTransient(exception);

    /// <summary>ITM-506/658(r4)：仅瞬时故障与本类型包装的基础设施超时（ITM-667 起用
    /// Data 标记识别，不再按消息前缀）计入熔断——用户 operation 自抛的 TimeoutException
    /// 是应用域确定性信号，不熔断；唯一约束冲突/SQL 语法错误等确定性失败同理。</summary>
    private bool CountsTowardCircuit(Exception exception)
        => (exception is TimeoutException timeoutException
                && timeoutException.Data.Contains("PalORM.InfrastructureTimeout"))
            || _isTransient(exception);
    /// <summary>仅超时包装（不重试不熔断）——非幂等写路径的超时语义统一入口。
    /// <para><b>与 ExecuteAsync 的区别</b>：ExecuteAsync 会重试瞬时故障（幂等性契约，ITM-310）——
    /// 写操作不能重试（INSERT 重试 = 重复写入）。但写路径此前<b>连超时包装都没有</b>：
    /// 命令设置了驱动级 CommandTimeout，但超时抛的是驱动原始异常（如 NpgsqlException 包装的
    /// TimeoutException），调用方无法用统一模式匹配超时。本方法提供与读路径一致的
    /// <c>TimeoutException</c> + <c>Data["PalORM.InfrastructureTimeout"]=true</c> 契约——只是不重试。</para>
    /// <para>事务内语句挂起时，整个事务的持锁时间由本超时限定上界（长事务风险）。</para></summary>
    public async ValueTask<T> ExecuteWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await ExecuteWithTimeoutAsync<Func<CancellationToken, Task<T>>, T>(
            static (op, token) => op(token), operation, ct).ConfigureAwait(false);
    }

    /// <summary>R4-λ:结构体状态重载(与 ExecuteAsync 的同型重载对称)——写路径
    /// ExecuteWriteRowsAsync 消闭包用;语义与单参版逐位一致。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S4136",
        Justification = "与 ExecuteAsync 族同因:编排取舍,语义分组优先。")]
    public async ValueTask<TResult> ExecuteWithTimeoutAsync<TState, TResult>(
        Func<TState, CancellationToken, Task<TResult>> operation, TState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        // A3（2026-10-01 全 API 逐项轮）：与 ExecuteAsync 同款——ct 不可取消时单构 CTS；
        // 无限超时且不可取消时完全不建 CTS（无超时事件可发生）。
        if (_timeout == Timeout.InfiniteTimeSpan && !ct.CanBeCanceled)
            return await operation(state, ct).ConfigureAwait(false);
        // R3-CTS:同 ExecuteAsync——非链接形态走租借池,链接形态新建。
        bool linked = ct.CanBeCanceled;
        CancellationTokenSource timeout = linked
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : PooledCts.Rent();
        try
        {
            if (_timeout != Timeout.InfiniteTimeSpan)
                timeout.CancelAfter(_timeout);
            return await operation(state, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException timeoutException) when (timeout.IsCancellationRequested)
        {
            var wrappedTimeout = new TimeoutException(
                $"Command timed out after {_timeout} (non-idempotent write; no retry attempted).",
                timeoutException);
            wrappedTimeout.Data["PalORM.InfrastructureTimeout"] = true;
            throw wrappedTimeout;
        }
        finally
        {
            // R3-CTS:同 ExecuteAsync 的归还分流。
            if (linked)
                timeout.Dispose();
            else
                PooledCts.Return(timeout);
        }
    }

}
