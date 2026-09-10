using System.Data.Common;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PalORM;

/// <summary>v5.0 阶段 5.4：审计拦截器——记录每条查询的开始/结束/耗时/错误。
/// <para><b>⚠ 覆盖面限制</b>（重要）：本拦截器仅覆盖实体 SELECT 执行管线
/// （ToListAsync/FirstOrDefault）与 QueryBuilder UPDATE。<b>INSERT/DELETE/Bulk/存储过程不经过拦截器</b>——
/// 如需全量审计（含写入操作），请用数据库层审计（PG log_statement / MySQL general_log）或 OpenTelemetry。
/// 这是 <see cref="IQueryInterceptor"/> 接口的既有限制，非 AuditInterceptor 独有。</para>
/// <para><b>设计</b>：实现 <see cref="IQueryInterceptor"/>，把审计事件转发给 <see cref="ILogger"/>。
/// 默认 Priority=200（让用户业务拦截器优先于审计执行，避免审计日志污染业务逻辑顺序）。</para>
/// <para><b>敏感数据脱敏</b>（ITM-713/763）：参数值默认不写入日志（避免凭据/PII 泄露）。
/// 调用方显式传 <c>logParameters: true</c> 时，<c>Set(member, value)</c> 写入
/// <c>[SensitiveData]</c> 列的参数值以该列掩码替代（见 <see cref="GetLoggableValue"/>）。
/// <b>脱敏边界</b>：<c>Where(FormattableString)</c> 等手写 SQL 片段的洞值与列无映射关系，
/// 无法自动脱敏——在 SQL 文本中内联敏感值的场景由调用方自行负责。
/// 异常消息（OnError）在 logParameters=false 时也仅记录异常类型名，不记录可能含参数值的 Message。</para>
/// <para><b>性能影响</b>：每次查询多一次 OnBefore + OnAfter 调用（含 Stopwatch.StartNew/Stop）。
/// 无日志订阅者时（ILogger.IsEnabled=false），仍构造 QueryContext 字符串——不适用于超高频场景。
/// 超高频场景请用 OpenTelemetry WithTracing（采样控制）。</para></summary>
public sealed class AuditInterceptor : IQueryInterceptor
{
    private readonly ILogger _logger;
    private readonly bool _logParameters;

    /// <summary>构造审计拦截器。</summary>
    /// <param name="logger">日志接收方（必填）。审计事件以 LogLevel.Information 写入。</param>
    /// <param name="logParameters">是否记录 SQL 参数值（默认 false——参数值含敏感数据，开启需评估合规风险）。</param>
    public AuditInterceptor(ILogger logger, bool logParameters = false)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _logParameters = logParameters;
    }

    /// <summary>优先级 200——让用户业务拦截器（默认 Priority=100）先执行。</summary>
    public int Priority => 200;

    /// <summary>查询开始：记录 SQL（可选参数）。</summary>
    public void OnBefore(QueryContext context)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        if (_logParameters)
            _logger.LogInformation("PalORM Audit [Before]: {Sql} | Params: {Params}",
                context.Sql, FormatParameters(context.Parameters, context.SensitiveParameterMasks));
        else
            _logger.LogInformation("PalORM Audit [Before]: {Sql}", context.Sql);
    }

    /// <summary>查询成功完成：记录耗时与行数。</summary>
    public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        _logger.LogInformation("PalORM Audit [After]: {RowCount} rows in {ElapsedMs:F2}ms | {Sql}",
            rowCount, elapsed.TotalMilliseconds, context.Sql);
    }

    /// <summary>查询失败：记录异常类型（异常实例照常向调用方抛出）。
    /// <para><b>脱敏说明</b>：异常消息（exception.Message）经常含数据库返回的参数值
    /// （如 PG DETAIL: Key (email)=(a@b.com)）。logParameters=false 时仅记录异常类型名 + SQL，
    /// 不记录 exception.Message，与参数脱敏承诺一致。</para></summary>
    public void OnError(QueryContext context, Exception exception)
    {
        if (!_logger.IsEnabled(LogLevel.Error)) return;
        if (_logParameters)
            _logger.LogError(exception, "PalORM Audit [Error]: {ExceptionType}: {Message} | {Sql}",
                exception.GetType().Name, exception.Message, context.Sql);
        else
            // ITM-611：不传 exception 实例——LogError(exception,...) 会让标准 provider 渲染
            // exception.ToString()（含 Message 内的 PG DETAIL 参数值），击穿 logParameters=false 的脱敏承诺。
            _logger.LogError("PalORM Audit [Error]: {ExceptionType} | {Sql}",
                exception.GetType().Name, context.Sql);
    }

    /// <summary>参数格式化（仅 logParameters=true 时调用）。
    /// 用 StringBuilder 避免大参数列表的多重字符串分配。
    /// <para><b>ITM-763(r21) 脱敏</b>：经 <see cref="QueryContext.SensitiveParameterMasks"/> 登记
    /// 的参数（<c>Set(member, value)</c> 写入 [SensitiveData] 列）以掩码替代真实值；
    /// 未登记的列照常输出。边界（Where 手写 SQL 洞值无列映射）见 QueryContext 文档。</para></summary>
    private static string FormatParameters(
        IReadOnlyList<DbParameter> parameters,
        IReadOnlyDictionary<string, string>? sensitiveMasks)
    {
        if (parameters.Count == 0) return "(none)";
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(parameters[i].ParameterName).Append('=').Append(GetLoggableValue(parameters[i], sensitiveMasks));
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>取参数的可记录值：命中掩码表时以掩码替代真实值（ITM-763）。
    /// 公开以便自定义拦截器复用同一脱敏策略——传入执行时的
    /// <see cref="QueryContext.SensitiveParameterMasks"/> 即获得与 AuditInterceptor 一致的行为。</summary>
    public static object? GetLoggableValue(
        DbParameter parameter,
        IReadOnlyDictionary<string, string>? sensitiveParameterMasks = null)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return sensitiveParameterMasks is not null
            && sensitiveParameterMasks.TryGetValue(parameter.ParameterName, out string? mask)
            ? mask
            : parameter.Value;
    }
}
