using System.Data.Common;

namespace PalORM.Core.Tests;

/// <summary>
/// 审计 2026-09-19 ERR-010 防线——拦截器三回调异常契约的行为钉住。
/// 契约（IQueryInterceptor 文档化）：OnBefore/OnAfter 异常传播使查询失败；
/// OnError 异常被吞（保护原始异常与清理）但计入 InterceptorOnErrorFailures 诊断计数。
/// </summary>
[NotInParallel("InterceptorOnError")]
public sealed class InterceptorErrorContractTests
{
    private static QueryContext NewContext()
        => new("SELECT 1", []);

    private sealed class ThrowingOnErrorInterceptor : IQueryInterceptor
    {
        public int Calls { get; private set; }

        public void OnBefore(QueryContext context) { }

        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) { }

        public void OnError(QueryContext context, Exception exception)
        {
            Calls++;
            throw new InvalidOperationException("interceptor's own bug");
        }
    }

    private sealed class RecordingOnErrorInterceptor : IQueryInterceptor
    {
        public int Calls { get; private set; }

        public void OnBefore(QueryContext context) { }

        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) { }

        public void OnError(QueryContext context, Exception exception) => Calls++;
    }

    [Test]
    public async Task OnErrorThrowing_DoesNotPropagate_AndCountsFailure()
    {
        var thrower = new ThrowingOnErrorInterceptor();
        var interceptors = new List<IQueryInterceptor> { thrower };
        long before = PalORMMetrics.InterceptorOnErrorFailures;

        // 吞掉：不抛出（原始异常保护的契约），调用本身完成
        QueryBuilderExtensions.NotifyInterceptorsOnError(
            interceptors, NewContext(), new InvalidOperationException("original"));

        await Assert.That(thrower.Calls).IsEqualTo(1);
        // 计数为进程级共享（其它并行组的失败查询路径也可能计入）——断言方向性增长
        await Assert.That(PalORMMetrics.InterceptorOnErrorFailures - before).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task OnErrorThrowing_DoesNotBlockSubsequentInterceptors()
    {
        var thrower = new ThrowingOnErrorInterceptor();
        var recorder = new RecordingOnErrorInterceptor();
        var interceptors = new List<IQueryInterceptor> { thrower, recorder };

        QueryBuilderExtensions.NotifyInterceptorsOnError(
            interceptors, NewContext(), new InvalidOperationException("original"));

        await Assert.That(thrower.Calls).IsEqualTo(1);
        await Assert.That(recorder.Calls).IsEqualTo(1);
    }
}
