using System.Diagnostics;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>Tracing 脱敏契约——Activity tag 不含 SQL 参数值（原 FinalTests 拆分，审计 TEST-012）。
/// ActivityListener 是进程级广播，命名组串行防止并行测试互抢 captured 断言。</summary>
[NotInParallel("PalORMActivity")]
public sealed class TracingSanitizationTests
{
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
}
