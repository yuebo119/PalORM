namespace PalORM.Integration.Tests;

/// <summary>有序拦截器——按 Priority 排序后记录调用顺序到 List。</summary>
internal sealed class OrderedInterceptor(int priority, List<int> order) : IQueryInterceptor
{
    public int Priority => priority;
    public void OnBefore(QueryContext context) => order.Add(priority);
    public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) { }
    public void OnError(QueryContext context, Exception exception) { }
}
