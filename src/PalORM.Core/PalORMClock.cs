namespace PalORM;

/// <summary>时间边界注入点（2026-10-31）：缓存 TTL、熔断冷却、能力探测负窗口共用。
/// <para>默认直接读 <see cref="Environment.TickCount64"/>，与改造前逐位一致；测试可经
/// <see cref="OverrideForTests"/> 注入假时钟，使时间敏感用例在毫秒内完成。
/// 只在时间边界调用，不在逐行/逐命令热路径上使用。</para>
/// <para><b>测试并发</b>：覆盖是进程级静态状态，使用它的测试须标 <c>[NotInParallel]</c>。</para></summary>
internal static class PalORMClock
{
    private static Func<long> _now = static () => Environment.TickCount64;

    /// <summary>当前单调毫秒。</summary>
    internal static long Now
    {
        get
        {
            Func<long> now = Volatile.Read(ref _now);
            return now();
        }
    }

    /// <summary>测试用：替换时钟并返回恢复句柄（幂等 Dispose）。</summary>
    internal static IDisposable OverrideForTests(Func<long> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        Func<long> previous = Interlocked.Exchange(ref _now, now);
        return new Restore(previous);
    }

    private sealed class Restore(Func<long> previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Exchange(ref _now, previous);
        }
    }
}
