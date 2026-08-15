using PalORM.Testing;

namespace PalORM.Integration.Tests;

// ITM-324 回归：单条软删除与 BulkDelete 幂等语义一致——重复删除返回 0 且不刷新时间戳。
// T-DEF-6：从 TenantIsolationTests.cs 拆分独立文件（文件名 = 主类名）。
public sealed class SoftDeleteIdempotencyTests
{
    [Test]
    public async Task DeleteAsync_Twice_SecondReturnsZeroAndKeepsTimestamp()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var e = await db.InsertAsync(new SoftDeletableEntity { Name = "SD" });

        await Assert.That(await db.DeleteAsync<SoftDeletableEntity>(e.Id)).IsEqualTo(1);
        db.IgnoreFilters();
        var afterFirst = await db.GetAsync<SoftDeletableEntity>(e.Id);
        string? firstDeletedAt = afterFirst!.DeletedAt;

        await Assert.That(await db.DeleteAsync<SoftDeletableEntity>(e.Id)).IsEqualTo(0);
        var afterSecond = await db.GetAsync<SoftDeletableEntity>(e.Id);
        await Assert.That(afterSecond!.DeletedAt).IsEqualTo(firstDeletedAt);
    }

    // ITM-401 回归：OrWhere 不得复活软删行。
    [Test]
    public async Task OrWhere_CannotResurrectSoftDeletedRow()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var alive = await db.InsertAsync(new SoftDeletableEntity { Name = "alive" });
        var dead = await db.InsertAsync(new SoftDeletableEntity { Name = "dead" });
        await db.DeleteAsync<SoftDeletableEntity>(dead.Id);
        _ = alive;

        var rows = await db.From<SoftDeletableEntity>()
            .Where($"name = {"alive"}").OrWhere($"name = {"dead"}").ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("alive");
    }
}
