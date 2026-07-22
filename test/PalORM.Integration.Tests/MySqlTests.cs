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
}
