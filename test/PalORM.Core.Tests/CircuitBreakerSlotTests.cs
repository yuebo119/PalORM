namespace PalORM.Core.Tests;

/// <summary>
/// 熔断器半开探针槽位归属的并发契约（ITM-895 锁定测试，r24 待办收口）。
/// 不变式：探针 P 卡死超 stale 窗口被 Enter 回收、槽位转给新探针 Q 后，P 苏醒的任何
/// 终结记录（取消/成功/失败）都不得释放 Q 的槽位或改写 Q 周期的窗口——修复前仅凭
/// generation 校验（回收不开闸、gen 不推进），P 与 Q 同 gen，P 的取消会释放 Q 的槽位
/// 并把 _openUntil 改写为"现在"（半开单探针不变式破坏、熔断保护弱化一拍）。
/// 时序依赖 halfOpenSlotStaleAfter 构造注入（亚毫秒窗口模拟卡死；默认 5 分钟不可测）。
/// </summary>
internal sealed class CircuitBreakerSlotTests
{
    private static CircuitBreaker OpenedBreaker()
    {
        // threshold=1：一次失败即开；resetAfter 极短：开闸后立即可半开；
        // staleAfter=500ms：P 拿到后 Sleep 600ms 即越窗可回收，Q 拿到后到第三次 Enter 的
        // 毫秒级间隔仍在窗内（若 staleAfter 太小，Q 也立即 stale，第三次 Enter 会合法回收
        // 而非被拒——时序分层是本测试的成立前提）
        var cb = new CircuitBreaker(threshold: 1, resetAfter: TimeSpan.FromTicks(1),
            halfOpenSlotStaleAfter: TimeSpan.FromMilliseconds(500));
        cb.RecordFinalFailure(isHalfOpenProbe: false, countsTowardCircuit: true, generation: 0, probeToken: 0);
        return cb;
    }

    [Test]
    public async Task StaleProbe_Cancel_DoesNotReleaseNewProbeSlot()
    {
        CircuitBreaker cb = OpenedBreaker();
        var (isProbeP, genP, tokenP) = cb.Enter();
        await Assert.That(isProbeP).IsTrue();

        await Task.Delay(600);  // 推进时钟使 P 越过 stale 窗口
        var (isProbeQ, _, _) = cb.Enter();
        await Assert.That(isProbeQ).IsTrue();  // 槽位 stale 回收后 Q 成为新探针

        // P 苏醒取消：修复前（无 token 校验）会释放 Q 的槽位并把 _openUntil 改写为现在
        cb.ReleaseCancelledProbe(isProbeP, genP, tokenP);

        // Q 的保护必须仍在：第三次 Enter 被"探针占用"拒绝（修复前槽位已被 P 释放，
        // 第三次调用会穿过并成为新探针——断言红）
        await Assert.That(cb.Enter).Throws<CircuitBreakerOpenException>();
    }

    [Test]
    public async Task StaleProbe_Success_DoesNotCloseNewProbeCircuit()
    {
        CircuitBreaker cb = OpenedBreaker();
        var (isProbeP, genP, tokenP) = cb.Enter();
        await Assert.That(isProbeP).IsTrue();

        await Task.Delay(600);
        var (isProbeQ, _, _) = cb.Enter();
        await Assert.That(isProbeQ).IsTrue();

        // P 苏醒报告成功：修复前（gen 匹配即放行）会关闭 Q 正在验证的闸（熔断保护失效一拍）
        cb.RecordSuccess(isProbeP, genP, tokenP);

        // 闸必须仍开：Q 占用中，第三次 Enter 仍被拒绝（修复前闸已被 P 关闭，
        // Enter 返回探针而非抛异常——断言红）
        await Assert.That(cb.Enter).Throws<CircuitBreakerOpenException>();
    }
}
