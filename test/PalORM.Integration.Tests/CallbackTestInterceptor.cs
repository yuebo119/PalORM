namespace PalORM.Integration.Tests;

/// <summary>回调型测试拦截器——OnBefore/OnAfter 触发传入的 Action。</summary>
internal sealed class CallbackTestInterceptor(Action onBefore, Action onAfter) : IQueryInterceptor
{
    public void OnBefore(QueryContext context) => onBefore();
    public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) => onAfter();
    public void OnError(QueryContext context, Exception exception) { /* S108: 测试 interceptor 不关心错误路径 */ }
}
