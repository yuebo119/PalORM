namespace PalORM;

/// <summary>熔断器状态机——独立于重试循环。
/// <para><b>状态</b>: Closed（正常）/ Open（快速失败）/ HalfOpen（单探针验证）。</para>
/// <para><b>线程安全</b>: 所有写在 Lock 内；Closed 态快路径用 <c>_isOpenFlag</c>（volatile
/// 镜像）无锁读取——见 <see cref="Enter"/>。</para>
/// <para><b>generation 机制</b>: 每次开闸 +1，防止陈旧探针/旧操作误关新周期熔断。
/// 探针的所有终结路径（成功/失败/取消）都带 generation 校验。</para></summary>
internal sealed class CircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _resetAfter;

    private int _failureCount;
    private DateTime _openUntil;
    private bool _isOpen;
    /// <summary>C1：<see cref="_isOpen"/> 的 volatile 镜像，供 Closed 态无锁快路径读。
    /// 全部写点都在锁内，因此快路径读到的值要么是 Closed（走完整锁路径复核后仍返回 Closed，
    /// 无行为差异），要么是 Open（走完整锁路径拿探针/抛异常）——镜像只提前"要不要进锁"。</summary>
    private volatile bool _isOpenFlag;
    private bool _halfOpenProbeActive;
    private long _generation;
    /// <summary>R5：半开探针的获取时刻（UtcNow ticks）——探针占用超过 resetAfter 未终结时，
    /// <see cref="Enter"/> 回收槽位，防止"调用方忘了调 ReleaseCancelledProbe → 熔断器永久 Open"。
    /// 0 = 无占用探针。</summary>
    private long _halfOpenProbeAcquiredAt;
    private readonly Lock _lock = new();
    /// <summary>R5：探针重试前的消息缓存——开闸时生成一次，避免每次拒绝都格式化（M6）。</summary>
    private string? _openMessage;

    internal CircuitBreaker(int threshold, TimeSpan resetAfter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        _threshold = threshold;
        _resetAfter = resetAfter;
    }

    internal bool IsEnabled => _threshold > 0;

    /// <summary>当前开闸截止时刻——仅在 Open 态有意义，供诊断查询（不参与判定）。</summary>
    internal DateTime OpenUntil
    {
        get
        {
            lock (_lock) { return _openUntil; }
        }
    }

    /// <summary>尝试进入电路——Open 态非探针请求抛 CircuitBreakerOpenException。
    /// 返回 (isHalfOpenProbe, generation) 记录，供后续 RecordSuccess/RecordFailure 判定。
    /// <para><b>C1 无锁快路径</b>：Closed 态（绝大多数时间）只读 volatile 镜像 + generation，
    /// 不进入锁。会话级单活动操作契约下同一执行器极少被并发使用，收益是省掉锁本身的开销
    /// （每次 DB 操作两次）。</para>
    /// <para><b>读序契约（RES-001，2026-09-22）</b>：generation 必须先于 flag 读取，且与
    /// <see cref="Open"/> 的写序（先 flag 后 generation）配对。反序时存在窗口：本线程读到
    /// flag=false（闸未开）之后 Open 已推进 generation，于是携带"新 generation + 未开闸"
    /// 返回，操作成功后 RecordSuccess 会关闭刚开闸的熔断器，resetAfter 冷却静默失效。
    /// 先读 generation 则保证：只要后读的 flag 仍是 false，先读的 generation 必然早于本次
    /// Open 的自增（自增发生在 flag 写之后），陈旧记录会被 RecordSuccess 的 generation 核对拦下。</para></summary>
    internal (bool IsHalfOpenProbe, long Generation) Enter()
    {
        long generation = Volatile.Read(ref _generation);
        if (!IsEnabled || !_isOpenFlag)
            return (false, generation);

        lock (_lock)
        {
            if (!IsEnabled || !_isOpen)
                return (false, _generation);

            // R5：回收超时未终结的探针占用槽位——调用方在两次记录之间崩溃/取消且未调
            // ReleaseCancelledProbe 时，槽位会永久泄漏使熔断器永远 Open
            if (_halfOpenProbeActive && !IsHalfOpenSlotStale())
            {
                throw new CircuitBreakerOpenException(_openMessage ?? FormatOpenMessage(_openUntil));
            }

            if (_halfOpenProbeActive)
            {
                // 槽位超时：回收后本次请求立即成为新探针
                _halfOpenProbeActive = false;
            }
            else if (DateTime.UtcNow < _openUntil)
            {
                throw new CircuitBreakerOpenException(_openMessage ?? FormatOpenMessage(_openUntil));
            }

            _halfOpenProbeActive = true;
            _halfOpenProbeAcquiredAt = DateTime.UtcNow.Ticks;
            return (true, _generation);
        }
    }

    /// <summary>探针占用是否已超时可回收——R5 的槽位回收判据。
    /// <para><b>为什么有独立下限</b>：判据不能用 <see cref="_resetAfter"/>——<c>resetAfter=Zero</c>
    /// 是合法配置（"开闸后立即半开"），以它为界会让探针一获取就算过期，Enter 直接回收槽位，
    /// 半开单探针不变式被破坏（两个并发 Enter 都会拿到探针）。兜底下界只针对"调用方忘了调
    /// ReleaseCancelledProbe 导致槽位永久泄漏"这一种故障形态，与熔断冷却期是两件事。</para></summary>
    private bool IsHalfOpenSlotStale()
    {
        long acquiredAt = _halfOpenProbeAcquiredAt;
        if (acquiredAt == 0) return true;
        TimeSpan held = DateTime.UtcNow - new DateTime(acquiredAt, DateTimeKind.Utc);
        return held > HalfOpenSlotStaleAfter;
    }

    /// <summary>半开探针占用的可回收下限——触发即说明探针的终结路径没被调用
    /// （正常探针由数据库命令超时约束，远早于此完成）。</summary>
    private static readonly TimeSpan HalfOpenSlotStaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>记录成功——探针成功且 generation 匹配时关闭熔断。</summary>
    internal void RecordSuccess(bool isHalfOpenProbe, long generation)
    {
        lock (_lock)
        {
            if (isHalfOpenProbe)
                ReleaseProbeSlot();

            // generation 防陈旧：gen N 的探针成功不得关闭 gen N+1 的熔断。
            if (generation != _generation)
                return;

            _failureCount = 0;
            _isOpen = false;
            _isOpenFlag = false;
            _openUntil = default;
            _openMessage = null;
        }
    }

    /// <summary>记录最终失败——探针失败重开熔断；非探针失败从 Closed 态首次跨阈值时开启。</summary>
    internal void RecordFinalFailure(bool isHalfOpenProbe, bool countsTowardCircuit)
    {
        lock (_lock)
        {
            if (isHalfOpenProbe)
                ReleaseProbeSlot();

            if (!countsTowardCircuit)
            {
                // ITM-772(r21)：半开探针以"不计入熔断"的异常（确定性错误，如约束冲突/语法错）
                // 失败时，原样保留 _isOpen=true/_openUntil 已过期——此后 Enter() 每次都走探针
                // 分支放行，"Open"成为谎言状态（熔断语义失效）。确定性失败不是熔断的保护对象
                // （熔断防瞬时故障风暴），显式复位为 Closed：状态诚实，瞬时故障再现时会重新计数开启。
                if (isHalfOpenProbe)
                {
                    _isOpen = false;
                    _failureCount = 0;
                    _openUntil = default;
                }
                return;
            }

            if (isHalfOpenProbe)
            {
                _failureCount = _threshold;
                Open(DateTime.UtcNow.Add(_resetAfter));
                return;
            }

            // 熔断已打开期间，在飞旧的非探针失败不再重复顺延恢复时间点（ITM-507）。
            if (_isOpen)
                return;

            _failureCount += 1;

            if (IsEnabled && _failureCount >= _threshold)
                Open(DateTime.UtcNow.Add(_resetAfter));
        }
    }

    /// <summary>调用方取消（非数据库失败）——释放探针占用，让熔断窗口立即到期进入半开态。
    /// <para><b>C2：带 generation</b>——原实现不带 generation，序列「探针 P 进入(gen N) →
    /// 探针失败重开(gen N+1，新 _openUntil) → P 的调用方取消并调本方法」会把 _openUntil
    /// 改写为"现在"，新窗口瞬间到期，实际冷却期被旧探针的取消单方面抹掉。</para></summary>
    internal void ReleaseCancelledProbe(bool isHalfOpenProbe, long generation)
    {
        if (!isHalfOpenProbe) return;

        lock (_lock)
        {
            if (generation != _generation) return;
            ReleaseProbeSlot();
            _openUntil = DateTime.UtcNow;
        }
    }

    /// <summary>开闸——集中写 _isOpen / _isOpenFlag / 消息缓存，保证两者永不漂移。</summary>
    private void Open(DateTime openUntil)
    {
        _isOpen = true;
        _isOpenFlag = true;
        _openUntil = openUntil;
        _generation += 1;
        _openMessage = FormatOpenMessage(openUntil);
    }

    private void ReleaseProbeSlot()
    {
        _halfOpenProbeActive = false;
        _halfOpenProbeAcquiredAt = 0;
    }

    /// <summary>M6：开闸时生成一次消息——Open 态下每次拒绝都格式化 DateTime 是纯垃圾。</summary>
    private static string FormatOpenMessage(DateTime openUntil)
        => $"Circuit breaker open until {openUntil:O}";
}
