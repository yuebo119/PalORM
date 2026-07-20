namespace PalORM.Integration.Tests;

/// <summary>计数型测试拦截器——记录 OnBefore/OnAfter 调用次数。
/// 用于验证查询执行管线是否正确触发拦截器回调。</summary>
internal sealed class CountingTestInterceptor : IQueryInterceptor
{
    internal int BeforeCount;
    internal int AfterCount;
    public void OnBefore(QueryContext context) => BeforeCount++;
    public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) => AfterCount++;
    public void OnError(QueryContext context, Exception exception) => _ = (context, exception);
}

/// <summary>回调型测试拦截器——OnBefore/OnAfter 触发传入的 Action。</summary>
internal sealed class CallbackTestInterceptor(Action onBefore, Action onAfter) : IQueryInterceptor
{
    public void OnBefore(QueryContext context) => onBefore();
    public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) => onAfter();
    public void OnError(QueryContext context, Exception exception) { /* S108: 测试 interceptor 不关心错误路径 */ }
}

/// <summary>有序拦截器——按 Priority 排序后记录调用顺序到 List。</summary>
internal sealed class OrderedInterceptor(int priority, List<int> order) : IQueryInterceptor
{
    public int Priority => priority;
    public void OnBefore(QueryContext context) => order.Add(priority);
    public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) { }
    public void OnError(QueryContext context, Exception exception) { }
}
