using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>MigrateAsync SQLite 收尾（2026-09-26）：索引 DDL 后必须跑一次 <c>PRAGMA optimize</c>
/// （SQLite 官方对 schema 变更的建议）。实效断言：表有数据 + 索引 → migrate → optimize 应产出
/// sqlite_stat1 统计行（复刻探针实证该形态 memory 库 stat1=1；空表场景 ANALYZE 无对象不写，
/// 属 SQLite 语义，不作断言面）。锁死"optimize 步骤不得缺席/丢失"。</summary>
public sealed class MigrateOptimizeTests
{
    [Test]
    public async Task MigrateAsync_Sqlite_OptimizePopulatesStat1AfterData()
    {
        await using var db = await TestDb.SqliteAsync();

        // 先建索引表与数据（不走 registry）——migrate 建 registry 索引 + 末尾 optimize
        await db.ExecuteAsync(
            $"CREATE TABLE mig_opt_probe (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL)");
        await db.ExecuteAsync($"CREATE INDEX ix_mig_opt_probe_name ON mig_opt_probe(Name)");
        await db.ExecuteAsync($"INSERT INTO mig_opt_probe(Name) VALUES ('a'), ('b')");

        await db.MigrateAsync();

        long stat1Rows = await db.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM sqlite_stat1");
        await Assert.That(stat1Rows).IsGreaterThanOrEqualTo(1);
    }
}
