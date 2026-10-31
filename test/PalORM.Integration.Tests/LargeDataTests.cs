using PalORM.Integration.Tests.Conformance;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

// P2（方案 §6.8 / META-4）：大数据与内存上界。
// 10 万行批量写入、百万行流式读取；断言正确性与"活内存增长上界"，不断言实际耗时。
// 夜间随机种子跑 D 层由 DialectRandomDiffTests（PALORM_NIGHTLY_DIALECT=1）承担。

[NotInParallel("ExtBulkTable")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
    "S1215:Refactor the code to remove this use of 'GC.Collect'",
    Justification = "活内存上界是本用例的判据：强制回收后取基线，比较批量/流式路径的存活对象增长")]
public sealed class LargeDataTests
{
    private const int BulkRows = 100_000;
    private const int StreamRows = 1_000_000;

    private static CfBulkPlainEntity[] Rows(int count)
        => [.. Enumerable.Range(1, count).Select(i =>
            new CfBulkPlainEntity { Id = i, Name = $"n{i}", Amount = i })];

    [Test]
    public async Task Sqlite_BulkInsert_100kRows_CorrectAndMemoryBounded()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("large_data_sqlite");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        try
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
            long before = GC.GetTotalMemory(forceFullCollection: true);
            CfBulkPlainEntity[] rows = Rows(BulkRows);
            long affected = await db.BulkInsertAsync(rows, batchSize: 5_000);
            long after = GC.GetTotalMemory(forceFullCollection: true);

            await Assert.That(affected).IsEqualTo((long)BulkRows);
            await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo((long)BulkRows);
            await Assert.That((await db.GetAsync<CfBulkPlainEntity>(1L))!.Name).IsEqualTo("n1");
            await Assert.That((await db.GetAsync<CfBulkPlainEntity>((long)BulkRows))!.Name)
                .IsEqualTo($"n{BulkRows}");
            // 活内存上界：10 万实体数组 + 批量缓冲；超过 200MB 说明存在按行溢出的累积
            await Assert.That(after - before).IsLessThan(200L * 1024 * 1024);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
        }
    }

    [Test]
    public async Task Sqlite_Streaming_MillionRows_CountsAllAndMemoryBounded()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("large_stream_sqlite");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        try
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
            // 用递归 CTE 直接生成百万行（不在托管侧先建数组，避免把"输入规模"混进内存基线）
            await db.ExecuteAsync(
                System.Runtime.CompilerServices.FormattableStringFactory.Create(
                    "INSERT INTO cf_bulk_plain (\"Id\", \"name\", \"amount\") "
                    + "SELECT x, 'n' || x, x FROM ("
                    + "WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 1000000) "
                    + "SELECT x FROM seq)"));
            await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo((long)StreamRows);

            long before = GC.GetTotalMemory(forceFullCollection: true);
            long count = 0;
            await foreach (CfBulkPlainEntity row in db.QueryAsyncEnumerable<CfBulkPlainEntity>(
                $"SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain ORDER BY \"Id\""))
            {
                count++;
                if (row.Id == 1) await Assert.That(row.Name).IsEqualTo("n1");
            }
            long after = GC.GetTotalMemory(forceFullCollection: true);

            await Assert.That(count).IsEqualTo((long)StreamRows);
            // 流式读取不得把结果集整体物化：活内存增长远小于"每行一个对象"的全量物化
            await Assert.That(after - before).IsLessThan(64L * 1024 * 1024);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkInsert_100kRows_CorrectAndMemoryBounded()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        try
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
            long before = GC.GetTotalMemory(forceFullCollection: true);
            CfBulkPlainEntity[] rows = Rows(BulkRows);
            long affected = await db.BulkInsertAsync(rows, batchSize: 5_000);
            long after = GC.GetTotalMemory(forceFullCollection: true);

            await Assert.That(affected).IsEqualTo((long)BulkRows);
            await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo((long)BulkRows);
            // 校验和锁定"全部行真的写进去了"，不只数行数
            await Assert.That(await db.SumAsync<CfBulkPlainEntity>($"amount"))
                .IsEqualTo((decimal)BulkRows * (BulkRows + 1) / 2);
            await Assert.That(after - before).IsLessThan(200L * 1024 * 1024);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
        }
    }
}