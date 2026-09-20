using System.Data.Common;

namespace PalORM;

/// <summary>查询拦截器接口——在查询执行前后注入自定义行为。
/// <para><b>覆盖面（ITM-513/547；R3/v5.7 扩展）</b>: 作用于实体 SELECT 执行管线（ToListAsync/FirstOrDefault 族）、
/// QueryBuilder UPDATE（ExecuteNonQueryAsync）与 <see cref="DataSession{TProvider}.ExecuteAsync"/>
/// （原始 DDL/DML，R3 接入）——三者均三段式 OnBefore/OnAfter/OnError。
/// INSERT/DELETE/Bulk/存储过程/QueryMultiple（流式多结果集）<b>不经过</b>拦截器——
/// 完整审计请用数据库层审计或 OpenTelemetry（WithTracing）。</para></summary>
public interface IQueryInterceptor
{
    /// <summary>拦截器优先级——数值越小越先执行（默认 100）。</summary>
    int Priority => 100;

    /// <summary>查询执行前调用。<paramref name="context"/> 携带即将执行的 SQL 与参数，可用于日志/审计。
    /// <para><b>异常契约</b>：本方法抛出的异常会传播并使该查询失败（审计 ERR-010 文档化）。</para></summary>
    void OnBefore(QueryContext context);

    /// <summary>查询成功完成后调用。<paramref name="elapsed"/> 为执行耗时，<paramref name="rowCount"/> 为返回行数。
    /// <para><b>异常契约</b>：同 <see cref="OnBefore"/>——抛出的异常传播并使查询调用方失败。</para></summary>
    void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount);

    /// <summary>查询抛出异常时调用（此时不调用 OnAfter）。<paramref name="exception"/> 为原始执行异常，方法返回后照常向调用方抛出。
    /// <para><b>异常契约（与 OnBefore/OnAfter 不对称）</b>：本方法处于异常处理通路上，实现抛出的异常
    /// 会被<b>吞掉</b>——以保护原始执行异常不被覆盖、后续拦截器与资源清理不被阻断；吞掉的同时
    /// 计入 <see cref="PalORMMetrics.InterceptorOnErrorFailures"/> 诊断计数（审计 ERR-010）。
    /// 实现方应自行 try-catch 并记录自身失败，不要依赖本契约静默。</para></summary>
    void OnError(QueryContext context, Exception exception);
}

/// <summary>查询上下文——传递给拦截器的只读信息。</summary>
/// <param name="Sql">完整 SQL 文本。</param>
/// <param name="Parameters">执行参数列表。
/// <para><b>ITM-535 警告</b>: 这是即将执行的实际参数的引用，非防御性副本。拦截器<b>不得</b>修改任何
/// <see cref="DbParameter.Value"/>——修改会直接改变随后执行的 SQL 语义。仅可读取用于日志/审计。</para></param>
/// <param name="SensitiveParameterMasks">ITM-763(r21)：参数名 → 脱敏掩码。[SensitiveData] 列经
/// <c>Set(member, value)</c> 写入的参数在此登记——记录参数值时以掩码替代真实值（可经
/// <see cref="AuditInterceptor.GetLoggableValue"/> 统一处理）。<b>边界</b>：<c>Where(FormattableString)</c>
/// 等用户手写 SQL 片段的洞值与列无映射关系，不在本表内（文档化为脱敏边界）；null = 无已知敏感参数。</param>
public readonly record struct QueryContext(
    string Sql,
    IReadOnlyList<DbParameter> Parameters,
    IReadOnlyDictionary<string, string>? SensitiveParameterMasks = null);

/// <summary>健康检查结果。</summary>
/// <param name="IsHealthy">DB 连接是否正常。</param>
/// <param name="Latency">SELECT 1 延迟。</param>
/// <param name="Error">错误信息（正常时为 null）。</param>
public readonly record struct HealthResult(bool IsHealthy, TimeSpan Latency, string? Error);

/// <summary>DryRun 预览结果——调试辅助，不执行数据库查询。</summary>
/// <param name="Sql">生成的 SQL 文本。</param>
/// <param name="Parameters">绑定参数列表。</param>
public readonly record struct DryRunResult(string Sql, IReadOnlyList<DbParameter> Parameters);
