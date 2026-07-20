namespace PalORM;

/// <summary>熔断器状态机——独立于重试循环。
/// <para><b>状态</b>: Closed（正常）/ Open（快速失败）/ HalfOpen（单探针验证）。</para>
/// <para><b>线程安全</b>: 所有方法在 Lock 内调用。</para>
/// <para><b>generation 机制</b>: 每次开闸 +1，防止陈旧探针/旧操作误关新周期熔断。</para></summary>
internal sealed class CircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _resetAfter;

    private int _failureCount;
    private DateTime _openUntil;
    private bool _isOpen;
    private bool _halfOpenProbeActive;
    private long _generation;
    private readonly Lock _lock = new();

    internal CircuitBreaker(int threshold, TimeSpan resetAfter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        _threshold = threshold;
        _resetAfter = resetAfter;
    }

    internal bool IsEnabled => _threshold > 0;

    /// <summary>尝试进入电路——Open 态非探针请求抛 CircuitBreakerOpenException。
    /// 返回 (isHalfOpenProbe, generation) 记录，供后续 RecordSuccess/RecordFailure 判定。</summary>
    internal (bool IsHalfOpenProbe, long Generation) Enter()
    {
        lock (_lock)
        {
            if (!IsEnabled || !_isOpen)
                return (false, _generation);

            if (DateTime.UtcNow < _openUntil || _halfOpenProbeActive)
                throw new CircuitBreakerOpenException($"Circuit breaker open until {_openUntil:O}");

            _halfOpenProbeActive = true;
            return (true, _generation);
        }
    }

    /// <summary>记录成功——探针成功且 generation 匹配时关闭熔断。</summary>
    internal void RecordSuccess(bool isHalfOpenProbe, long generation)
    {
        lock (_lock)
        {
            if (isHalfOpenProbe)
                _halfOpenProbeActive = false;

            // generation 防陈旧：gen N 的探针成功不得关闭 gen N+1 的熔断。
            if (generation != _generation)
                return;

            _failureCount = 0;
            _isOpen = false;
            _openUntil = default;
        }
    }

    /// <summary>记录最终失败——探针失败重开熔断；非探针失败从 Closed 态首次跨阈值时开启。</summary>
    internal void RecordFinalFailure(bool isHalfOpenProbe, bool countsTowardCircuit)
    {
        lock (_lock)
        {
            if (isHalfOpenProbe)
                _halfOpenProbeActive = false;

            if (!countsTowardCircuit)
                return;

            if (isHalfOpenProbe)
            {
                _failureCount = _threshold;
                _isOpen = true;
                _generation++;
                _openUntil = DateTime.UtcNow.Add(_resetAfter);
                return;
            }

            // 熔断已打开期间，在飞旧的非探针失败不再重复顺延恢复时间点（ITM-507）。
            if (_isOpen)
                return;

            _failureCount += 1;

            if (IsEnabled && _failureCount >= _threshold)
            {
                _isOpen = true;
                _generation++;
                _openUntil = DateTime.UtcNow.Add(_resetAfter);
            }
        }
    }

    /// <summary>调用方取消（非数据库失败）——释放探针占用，让熔断窗口立即到期进入半开态。</summary>
    internal void ReleaseCancelledProbe(bool isHalfOpenProbe)
    {
        if (!isHalfOpenProbe)
            return;

        lock (_lock)
        {
            _halfOpenProbeActive = false;
            _openUntil = DateTime.UtcNow;
        }
    }
}
