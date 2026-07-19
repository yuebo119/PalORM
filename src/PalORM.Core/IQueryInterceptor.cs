using System.Data.Common;

namespace PalORM;

/// <summary>查询拦截器接口——在查询执行前后注入自定义行为。
/// <para><b>覆盖面（ITM-309）</b>: 仅作用于实体 SELECT 执行管线（ToListAsync/FirstOrDefault 族）；
/// INSERT/UPDATE/DELETE/Bulk/存储过程/QueryMultiple 不经过拦截器——完整审计请用数据库层审计或
/// OpenTelemetry（WithTracing）。</para></summary>
public interface IQueryInterceptor
{
    /// <summary>拦截器优先级——数值越小越先执行（默认 100）。</summary>
    int Priority => 100;

    /// <summary>查询执行前调用。<paramref name="context"/> 携带即将执行的 SQL 与参数，可用于日志/审计。</summary>
    void OnBefore(QueryContext context);

    /// <summary>查询成功完成后调用。<paramref name="elapsed"/> 为执行耗时，<paramref name="rowCount"/> 为返回行数。</summary>
    void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount);

    /// <summary>查询抛出异常时调用（此时不调用 OnAfter）。<paramref name="exception"/> 为原始执行异常，方法返回后照常向调用方抛出。</summary>
    void OnError(QueryContext context, Exception exception);
}

/// <summary>查询上下文——传递给拦截器的只读信息。</summary>
/// <param name="Sql">完整 SQL 文本。</param>
/// <param name="Parameters">执行参数列表。
/// <para><b>ITM-535 警告</b>: 这是即将执行的实际参数的引用，非防御性副本。拦截器<b>不得</b>修改任何
/// <see cref="DbParameter.Value"/>——修改会直接改变随后执行的 SQL 语义。仅可读取用于日志/审计。</para>
/// <para><b>脱敏边界</b>: 参数值以明文携带。框架内置日志对 <c>[SensitiveData]</c> 标注列脱敏，但该脱敏
/// <b>不覆盖</b>自定义拦截器——若在拦截器中记录参数值，敏感数据脱敏由拦截器实现方自行负责。</para></param>
public readonly record struct QueryContext(string Sql, IReadOnlyList<DbParameter> Parameters);

/// <summary>健康检查结果。</summary>
/// <param name="IsHealthy">DB 连接是否正常。</param>
/// <param name="Latency">SELECT 1 延迟。</param>
/// <param name="Error">错误信息（正常时为 null）。</param>
public readonly record struct HealthResult(bool IsHealthy, TimeSpan Latency, string? Error);

/// <summary>DryRun 预览结果——调试辅助，不执行数据库查询。</summary>
/// <param name="Sql">生成的 SQL 文本。</param>
/// <param name="Parameters">绑定参数列表。</param>
public readonly record struct DryRunResult(string Sql, IReadOnlyList<DbParameter> Parameters);
