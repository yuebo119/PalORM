using PalORM.PostgreSql;

namespace PalORM.Integration.Tests;

[Table("pg_test")]
public partial class PgEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
}

public sealed class PostgreSqlIntegrationTests
{
    private static DbOptions Opts => new()
    {
        ConnectionString = Environment.GetEnvironmentVariable("PALORM_PG_CONNECTION")
            ?? "Host=localhost;Username=postgres;Password=;Database=postgres"
    };

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_HealthCheck_Succeeds() { await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(Opts); await Assert.That((await db.HealthCheckAsync()).IsHealthy).IsTrue(); }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_DDL_Insert_Query_Works()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(Opts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS pg_test");
        await db.ExecuteAsync($"CREATE TABLE pg_test (id BIGSERIAL PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");
        await db.ExecuteAsync($"INSERT INTO pg_test (name, value) VALUES ({"PG"}, {42})");
        var c = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM pg_test WHERE name = {"PG"}");
        await Assert.That(c).IsEqualTo(1);
        await db.ExecuteAsync($"DROP TABLE pg_test");
    }
}
