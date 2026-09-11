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

    /// <summary>ITM-773(r21)：instrumentation version 与包版本对齐（OTel 语义约定建议
    /// instrumentation_scope.version = 库版本）——此前硬编码 "1.0.0" 与 5.5.0 漂移，
    /// 观测端按版本过滤/关联失配。由 D11 同口径守护（生成物侧为 GeneratedCodeMetadata）。</summary>
    internal const string InstrumentationVersion = "5.5.0";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, InstrumentationVersion);

    /// <summary>共享 Meter——供其他模块（如 <see cref="BoundedQueryCache"/>）注册指标。
    /// 通过 <c>PalORM</c> 名称统一导出到 OpenTelemetry。</summary>
    internal static Meter Meter { get; } = new(MeterName, InstrumentationVersion);

    private static readonly Counter<long> _queryCounter = Meter.CreateCounter<long>(
        "palorm.query.executions", description: "Number of database commands executed");
    private static readonly Histogram<double> _queryDuration = Meter.CreateHistogram<double>(
        "palorm.query.duration", "s", "Database command duration in seconds");

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
