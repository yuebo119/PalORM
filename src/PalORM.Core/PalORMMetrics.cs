using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PalORM;

/// <summary>PalORM 查询 Activity 与 Meter。标签仅包含有界的 Provider、操作和结果。</summary>
public static class PalORMMetrics
{
    /// <summary>ActivitySource 名称。</summary>
    public const string ActivitySourceName = "PalORM";

    /// <summary>Meter 名称。</summary>
    public const string MeterName = "PalORM";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    private static readonly Meter _meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> _queryCounter = _meter.CreateCounter<long>(
        "palorm.query.executions", description: "Number of database commands executed");
    private static readonly Histogram<double> _queryDuration = _meter.CreateHistogram<double>(
        "palorm.query.duration", "s", "Database command duration in seconds");

    /// <summary>兼容旧版手动计数入口。参数不会写入指标标签。</summary>
    [Obsolete("请使用 QueryBuilder.WithMetrics() 由执行管线自动记录结果和耗时。")]
    public static void LogQuery(string entityType)
    {
        // ITM-531: 不再 RecordCount——兼容期的手动调用无真实执行结果，计入 outcome=success 会
        // 污染 palorm.query.executions 指标（伪造成功样本）。保留 Obsolete 引导迁移，方法体 no-op。
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
    }

    /// <summary>兼容旧版手动开始入口。参数不会写入指标标签。</summary>
    [Obsolete("请使用 QueryBuilder.WithMetrics() 由执行管线自动记录结果和耗时。")]
    public static void RecordQueryStart(string queryName)
    {
        // ITM-531: 同上 no-op，避免污染指标。
        ArgumentException.ThrowIfNullOrWhiteSpace(queryName);
    }

    /// <summary>兼容旧版手动耗时入口。参数不会写入指标标签。</summary>
    [Obsolete("请使用 QueryBuilder.WithMetrics() 由执行管线自动记录结果和耗时。")]
    public static void RecordQueryDuration(string queryName, TimeSpan duration)
    {
        // ITM-531: 同上 no-op，避免污染指标。
        ArgumentException.ThrowIfNullOrWhiteSpace(queryName);
    }

    internal static Activity? StartActivity(string operation, string provider)
    {
        Activity? activity = ActivitySource.StartActivity("PalORM.Query", ActivityKind.Client);
        if (activity is null)
            return null;

        activity.SetTag("db.system.name", provider);
        activity.SetTag("db.operation.name", operation);
        return activity;
    }

    internal static void CompleteActivity(Activity? activity, string outcome)
    {
        if (activity is null)
            return;

        activity.SetTag("palorm.outcome", outcome);
        activity.SetStatus(outcome == "success" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        activity.Dispose();
    }

    internal static void Record(string operation, string provider, string outcome, TimeSpan duration)
    {
        RecordCount(operation, provider, outcome);
        RecordDuration(operation, provider, outcome, duration);
    }

    private static void RecordCount(string operation, string provider, string outcome)
    {
        TagList tags = CreateTags(operation, provider, outcome);
        _queryCounter.Add(1, tags);
    }

    private static void RecordDuration(string operation, string provider, string outcome, TimeSpan duration)
    {
        TagList tags = CreateTags(operation, provider, outcome);
        _queryDuration.Record(duration.TotalSeconds, tags);
    }

    private static TagList CreateTags(string operation, string provider, string outcome)
    {
        TagList tags = default;
        tags.Add("db.system.name", provider);
        tags.Add("db.operation.name", operation);
        tags.Add("palorm.outcome", outcome);
        return tags;
    }
}

internal sealed class QueryObservation
{
    private readonly Activity? _activity;
    private readonly bool _metricsEnabled;
    private readonly string _operation;
    private readonly string _provider;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _completed;

    internal QueryObservation(bool tracingEnabled, bool metricsEnabled, string operation, string provider)
    {
        _activity = tracingEnabled ? PalORMMetrics.StartActivity(operation, provider) : null;
        _metricsEnabled = metricsEnabled;
        _operation = operation;
        _provider = provider;
    }

    internal void Complete(string outcome)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _stopwatch.Stop();
        PalORMMetrics.CompleteActivity(_activity, outcome);
        if (_metricsEnabled)
            PalORMMetrics.Record(_operation, _provider, outcome, _stopwatch.Elapsed);
    }
}
