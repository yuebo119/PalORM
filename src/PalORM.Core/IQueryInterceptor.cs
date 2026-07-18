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

    void OnBefore(QueryContext context);
    void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount);
    void OnError(QueryContext context, Exception exception);
}

/// <summary>查询上下文——传递给拦截器的只读信息。</summary>
/// <param name="Sql">完整 SQL 文本。</param>
/// <param name="Parameters">参数列表。</param>
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
