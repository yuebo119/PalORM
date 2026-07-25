using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

[Table("mysql_test")]
public partial class MySqlEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}

public sealed class MySqlIntegrationTests
{
    private static DbOptions Opts => new()
    {
        ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
    };

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_HealthCheck_Succeeds()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(Opts);
        await Assert.That((await db.HealthCheckAsync()).IsHealthy).IsTrue();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_Execute_DDL_Works()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(Opts);
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_test");
            await db.ExecuteAsync($"CREATE TABLE mysql_test (id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");
            await db.ExecuteAsync($"INSERT INTO mysql_test (name, value) VALUES ({"MySQL"}, {42})");
            var c = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM mysql_test WHERE name = {"MySQL"}");
            await Assert.That(c).IsEqualTo(1);
        }
        finally
        {
            // P0 修复：确保断言失败也清理表——避免残留导致下次测试冲突
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_test");
        }
    }

    // v5.0 阶段 4.2：验证 MySqlBulkCopy 阈值路径（≥2000 行用二进制行协议）。
    // 用 2500 行（>BulkCopyThreshold=2000）触发 BulkCopy 路径而非多值 INSERT。
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkInsert_AboveThreshold_UsesBulkCopyAndInsertsAll()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(Opts);
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_bulk_test");
            await db.ExecuteAsync($"CREATE TABLE mysql_bulk_test (id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");

            var entities = Enumerable.Range(0, 2500)
                .Select(i => new MySqlBulkEntity { Name = $"row-{i}", Value = i })
                .ToArray();
            long affected = await db.BulkInsertAsync(entities);
            await Assert.That(affected).IsEqualTo(2500);

            // 全部行都落库（验证 BulkCopy 数据正确无丢行/错列）
            long count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM mysql_bulk_test");
            await Assert.That(count).IsEqualTo(2500);

            // 抽样首尾验证数据正确性（value 列等于原始序号）
            var firstRow = await db.ScalarAsync<int>($"SELECT value FROM mysql_bulk_test WHERE name = {"row-0"}");
            await Assert.That(firstRow).IsEqualTo(0);
            var lastRow = await db.ScalarAsync<int>($"SELECT value FROM mysql_bulk_test WHERE name = {"row-2499"}");
            await Assert.That(lastRow).IsEqualTo(2499);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_bulk_test");
        }
    }
}

[Table("mysql_bulk_test")]
public partial class MySqlBulkEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}
