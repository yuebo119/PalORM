using System.Diagnostics.Metrics;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// R5（v6.0）：WithMetrics(name) 转正——name 透传为 metrics tag palorm.query.name。
/// 锚点：撤掉 CreateTags 的 metricName 分支 → tag 断言红（tag 永不出现）。
/// 通过 MeterListener 全局监听（PalORM Meter 为 internal，无需直访）。
/// </summary>
public sealed class WithMetricsTagTests
{
    [Test]
    public async Task WithMetrics_Name_FlowsToCounterAndHistogramTags()
    {
        var observedNames = new List<string>();
        var instrumentNames = new HashSet<string>();
        using var listener = new MeterListener();
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            lock (instrumentNames)
            {
                instrumentNames.Add(instrument.Name);
                foreach (KeyValuePair<string, object?> tag in tags)
                    if (tag.Key == "palorm.query.name" && tag.Value is string value)
                        lock (observedNames) observedNames.Add(value);
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            lock (instrumentNames)
            {
                instrumentNames.Add(instrument.Name);
                foreach (KeyValuePair<string, object?> tag in tags)
                    if (tag.Key == "palorm.query.name" && tag.Value is string value)
                        lock (observedNames) observedNames.Add(value);
            }
        });
        // 标准监听模式：instrument 发布时启用（PalORM 的 Meter 为 internal，无需直访）
        listener.InstrumentPublished = static (instrument, l) => l.EnableMeasurementEvents(instrument);
        listener.Start();

        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new MetricsProbeEntity { Value = "x" });
        _ = await db.From<MetricsProbeEntity>().WithMetrics("订单查询").ToListAsync();

        await Assert.That(observedNames.Contains("订单查询")).IsTrue();
        // 计数与时长两个 instrument 都携带（RecordCount + RecordDuration 同 tag）
        await Assert.That(instrumentNames.Contains("palorm.query.executions")).IsTrue();
        await Assert.That(instrumentNames.Contains("palorm.query.duration")).IsTrue();
    }
}

#region Test Entities
[Table("metrics_probe")]
public partial class MetricsProbeEntity
{
    [Key] public long Id { get; set; }
    [Column("value")] public string Value { get; set; } = "";
}
#endregion
