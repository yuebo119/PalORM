using PalORM.Sqlite;

namespace PalORM.Integration.Tests;

/// <summary>迁移漂移检测（<see cref="DbOptions.ValidateMigrationDrift"/>）。
///
/// <para><b>背景</b>：<c>MigrateAsync</c> 只建不改（幂等新建哲学），实体加列后迁移仍返回成功，
/// 错误推迟到第一次写入才以驱动错误暴露。开启本开关后，漂移在迁移返回时就写入
/// <see cref="MigrationResult.SchemaDrift"/>。</para></summary>
[NotInParallel("MigrationDrift")]
public sealed class MigrationDriftTests
{
    private static Task<DataSession<SqliteProvider>> CreateAsync(bool validateDrift)
        => DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:",
            ValidateMigrationDrift = validateDrift,
        });

    [Test]
    public async Task Migrate_DriftCheckOn_CleanSchema_ReportsNoDrift()
    {
        await using DataSession<SqliteProvider> db = await CreateAsync(validateDrift: true);

        await db.MigrateAsync();

        MigrationResult? result = db.LastMigrationResult;
        await Assert.That(result).IsNotNull();
        await Assert.That(string.Join(" | ", result!.SchemaDrift)).IsEmpty()
            .Because("迁移刚建的库不应有漂移，非空说明 ColumnNames 与建表 DDL 的列集不同源");
    }

    [Test]
    public async Task Migrate_DriftCheckOn_MissingColumn_IsReported()
    {
        await using DataSession<SqliteProvider> db = await CreateAsync(validateDrift: true);
        // 手工建一张缺列的表：MigrateAsync 的 IF NOT EXISTS 会跳过它，正是漂移场景
        await db.ExecuteAsync(
            $"CREATE TABLE products (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)");

        await db.MigrateAsync();

        MigrationResult? result = db.LastMigrationResult;
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.SchemaDrift.Count).IsGreaterThan(0);
        await Assert.That(result.SchemaDrift.Any(entry => entry.Contains("price", StringComparison.Ordinal)))
            .IsTrue().Because("products 表缺 price/stock 两列，漂移清单必须点名");
    }

    [Test]
    public async Task Migrate_DriftCheckOff_LeavesSchemaDriftEmpty()
    {
        await using DataSession<SqliteProvider> db = await CreateAsync(validateDrift: false);
        await db.ExecuteAsync(
            $"CREATE TABLE products (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)");

        await db.MigrateAsync();

        MigrationResult? result = db.LastMigrationResult;
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.SchemaDrift.Count).IsEqualTo(0)
            .Because("开关关闭时不付逐表列查询的代价，SchemaDrift 保持空集合");
    }
}
