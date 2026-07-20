using System.Diagnostics;
using System.Diagnostics.Metrics;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

[NotInParallel]
public sealed class FinalTests
{
    [Test] public async Task WindowOver_Execution_ReturnsRows() { await using var db = await TestDb.SqliteAsync(); await db.MigrateAsync(); await db.InsertAsync(new Product{Name="W1",Price=10m,Stock=0}); var r=await db.From<Product>().UnsafeWindowOver("ROW_NUMBER()","ORDER BY price DESC").ToListAsync(); await Assert.That(r.Count).IsEqualTo(1); }
    [Test] public async Task WithCommandTimeout_ExecutesSuccessfully() { await using var db = await TestDb.SqliteAsync(); await db.MigrateAsync(); var r=await db.From<Product>().WithCommandTimeout(30).ToListAsync(); await Assert.That(r).IsNotNull(); }

    [Test]
    public async Task WithTracing_EmitsSanitizedActivity()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PalORMMetrics.ActivitySourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        await db.From<Product>().Where($"name = {"secret-value"}").WithTracing().ToListAsync();

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.OperationName).IsEqualTo("PalORM.Query");
        await Assert.That(captured.GetTagItem("db.operation.name")).IsEqualTo("select");
        await Assert.That(captured.GetTagItem("palorm.outcome")).IsEqualTo("success");
        await Assert.That(string.Join('|', captured.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")))
            .DoesNotContain("secret-value");
    }

    [Test]
    public async Task WithMetrics_EmitsLowCardinalityOutcomeTags()
    {
        var tags = new List<KeyValuePair<string, object?>>();
        long executions = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PalORMMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, measurementTags, _) =>
        {
            if (instrument.Name != "palorm.query.executions") return;
            executions += measurement;
            foreach (KeyValuePair<string, object?> tag in measurementTags)
                tags.Add(tag);
        });
        listener.Start();
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        await db.From<Product>().WithMetrics("user-supplied-name").ToListAsync();

        await Assert.That(executions).IsEqualTo(1);
        await Assert.That(tags).Contains(new KeyValuePair<string, object?>("db.operation.name", "select"));
        await Assert.That(tags).Contains(new KeyValuePair<string, object?>("palorm.outcome", "success"));
        await Assert.That(string.Join('|', tags.Select(tag => $"{tag.Key}={tag.Value}")))
            .DoesNotContain("user-supplied-name");
    }

    [Test]
    public async Task ToPageWithMetrics_RecordsCountAndSelectCommands()
    {
        var operations = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PalORMMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, measurementTags, _) =>
        {
            if (instrument.Name != "palorm.query.executions") return;
            foreach (KeyValuePair<string, object?> tag in measurementTags)
            {
                if (tag.Key == "db.operation.name" && tag.Value is string operation)
                    operations.Add(operation);
            }
        });
        listener.Start();
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "page", Price = 1m, Stock = 1 });

        await db.From<Product>().WithMetrics("page").ToPageAsync(10, product => product.Id);

        await Assert.That(operations).Contains("count");
        await Assert.That(operations).Contains("select");
        await Assert.That(operations.Count).IsEqualTo(2);
    }

    [Test]
    public async Task QueryMultipleMetrics_CompletesWhenGridReaderIsDisposed()
    {
        long successCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PalORMMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, measurementTags, _) =>
        {
            if (instrument.Name != "palorm.query.executions") return;
            bool isSuccess = false;
            bool isMultiple = false;
            foreach (KeyValuePair<string, object?> tag in measurementTags)
            {
                isSuccess |= tag.Key == "palorm.outcome" && Equals(tag.Value, "success");
                isMultiple |= tag.Key == "db.operation.name" && Equals(tag.Value, "query_multiple");
            }
            if (isSuccess && isMultiple) successCount += measurement;
        });
        listener.Start();
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        GridReader reader = await db.From<Product>()
            .WithMetrics("multiple")
            .QueryMultipleAsync($"SELECT * FROM products");
        await Assert.That(successCount).IsEqualTo(0);
        await reader.DisposeAsync();

        await Assert.That(successCount).IsEqualTo(1);
    }

    [Test]
    public async Task QueryMultiple_UnregisteredType_RecordsErrorWithoutSuccess()
    {
        var outcomes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PalORMMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, measurementTags, _) =>
        {
            if (instrument.Name != "palorm.query.executions") return;
            bool isMultiple = false;
            string? outcome = null;
            foreach (KeyValuePair<string, object?> tag in measurementTags)
            {
                isMultiple |= tag.Key == "db.operation.name" && Equals(tag.Value, "query_multiple");
                if (tag.Key == "palorm.outcome") outcome = tag.Value as string;
            }
            if (isMultiple && outcome is not null) outcomes.Add(outcome);
        });
        listener.Start();
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        GridReader reader = await db.From<Product>()
            .WithMetrics("multiple-error")
            .QueryMultipleAsync($"SELECT 1");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadAsync<UnregisteredResult>());
        await reader.DisposeAsync();

        await Assert.That(outcomes).IsEquivalentTo(["error"]);
    }

    [Test]
    public async Task WithMetrics_RecordsErrorAndCancellationOutcomes()
    {
        var outcomes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PalORMMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, measurementTags, _) =>
        {
            if (instrument.Name != "palorm.query.executions") return;
            foreach (KeyValuePair<string, object?> tag in measurementTags)
            {
                if (tag.Key == "palorm.outcome" && tag.Value is string outcome)
                    outcomes.Add(outcome);
            }
        });
        listener.Start();
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        try
        {
            await db.From<Product>().Raw("INVALID SQL").WithMetrics("error-case").ToListAsync();
        }
        catch (Exception)
        {
            // 这里只验证执行管线的结果分类，数据库异常类型由 Provider 决定。
        }

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        try
        {
            await db.From<Product>().WithMetrics("cancel-case").ToListAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // S108: 测试期望取消——WithMetrics 必须把取消路径记为 cancelled/error。
        }

        await Assert.That(outcomes).Contains("error");
        await Assert.That(outcomes).Contains("cancelled");
    }

    [Test] public async Task CTE_SimpleQuery_ReturnsRows() { await using var db = await TestDb.SqliteAsync(); await db.MigrateAsync(); await db.InsertAsync(new Product{Name="C",Price=10m,Stock=0}); var r=await db.From<Product>().With("c",$"SELECT * FROM products WHERE price > {5m}").ToListAsync(); await Assert.That(r.Count).IsEqualTo(1); }
    [Test] public async Task AsSplitQuery_ExecutesWithoutJoin() { await using var db = await TestDb.SqliteAsync(); await db.MigrateAsync(); await db.InsertAsync(new Product{Name="S",Price=1m,Stock=0}); var r=await db.From<Product>().AsSplitQuery().ToListAsync(); await Assert.That(r.Count).IsEqualTo(1); }

    private sealed class UnregisteredResult;
}
