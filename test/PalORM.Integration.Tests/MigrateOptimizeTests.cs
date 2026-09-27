using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>MigrateAsync 收尾的计划器统计刷新（2026-09-26，step5 §九/§九-E）：
/// <para>① SQLite：索引 DDL 后跑 <c>PRAGMA optimize</c>（SQLite 官方对 schema 变更的建议；
/// 引擎无 STAT4，sqlite_stat1 是计划器唯一统计来源）。实效断言：表有数据 + 索引 → migrate →
/// optimize 应产出 sqlite_stat1 统计行（空表场景 ANALYZE 无对象不写，属 SQLite 语义）。</para>
/// <para>② PostgreSQL：同构平移 <c>ANALYZE</c>。实效断言：migrate 后 pg_statistic 系统目录
/// 出现被迁移表的统计行——计划器统计已刷新（SQLite 优化平移 PG 的对应物）。</para></summary>
/// <para>③ <b>B63 守卫（2026-09-27 复现）</b>：MigrateAsync 在 PG/MySQL 上是 registry 全量建表，
/// 多用例并行执行会在系统目录撞 23505（pg_type_typname_nsp_index）——本地并行复现、CI 同因
/// 失败卡过发布。本类挂 ExtBulkTable 组与其余 PG 建表用例串行。</para>
[NotInParallel("ExtBulkTable")]
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

    [Test]
    public async Task MigrateAsync_PostgreSql_AnalyzePopulatesPgStatistic()
    {
        await using var db = await TestDb.PostgreSqlAsync();

        // 手工建带索引探针表 + 数据（全库 ANALYZE 覆盖它，含非 registry 表）
        await db.ExecuteAsync(
            $"DROP TABLE IF EXISTS mig_opt_probe_pg");
        await db.ExecuteAsync(
            $"CREATE TABLE mig_opt_probe_pg (Id BIGSERIAL PRIMARY KEY, Name TEXT NOT NULL)");
        await db.ExecuteAsync(
            $"CREATE INDEX ix_mig_opt_probe_pg_name ON mig_opt_probe_pg(Name)");
        await db.ExecuteAsync($"INSERT INTO mig_opt_probe_pg(Name) VALUES ('a'), ('b')");

        await db.MigrateAsync();

        long statRows = await db.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM pg_statistic s JOIN pg_class c ON c.oid = s.starelid WHERE c.relname = 'mig_opt_probe_pg'");
        await Assert.That(statRows).IsGreaterThanOrEqualTo(1);
    }
}
