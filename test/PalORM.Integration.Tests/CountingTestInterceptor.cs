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
