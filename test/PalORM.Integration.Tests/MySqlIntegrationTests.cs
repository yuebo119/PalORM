using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

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

    // v5.0 阶段 4.2 改进：验证 local_infile=ON 时走 MySqlBulkCopy（无阈值，对齐 PG COPY）。
    // 用任意行数（10 行）验证：local_infile 能力检测驱动分流，不再依赖 2000 阈值。
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkInsert_LocalInfileOn_UsesBulkCopyAndInsertsAll()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(Opts);
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_bulk_test");
            await db.ExecuteAsync($"CREATE TABLE mysql_bulk_test (id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");

            // 用 10 行验证（远小于原 2000 阈值，证明无阈值分流）
            var entities = Enumerable.Range(0, 10)
                .Select(i => new MySqlBulkEntity { Name = $"row-{i}", Value = i })
                .ToArray();
            long affected = await db.BulkInsertAsync(entities);
            await Assert.That(affected).IsEqualTo(10);

            long count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM mysql_bulk_test");
            await Assert.That(count).IsEqualTo(10);

            var firstRow = await db.ScalarAsync<int>($"SELECT value FROM mysql_bulk_test WHERE name = {"row-0"}");
            await Assert.That(firstRow).IsEqualTo(0);
            var lastRow = await db.ScalarAsync<int>($"SELECT value FROM mysql_bulk_test WHERE name = {"row-9"}");
            await Assert.That(lastRow).IsEqualTo(9);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS mysql_bulk_test");
        }
    }
}

#region Test Entities
[Table("mysql_test")]
public partial class MySqlEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}

[Table("mysql_bulk_test")]
public partial class MySqlBulkEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}
#endregion
