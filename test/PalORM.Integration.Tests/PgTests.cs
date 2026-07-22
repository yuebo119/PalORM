using PalORM.PostgreSql;
using PalORM.Testing;

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
        ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
    };

    [Test]
    public async Task WhereJson_GeneratesQuotedColumnWithBoundPathAndValue()
    {
        // SQL 生成不依赖 PG 连接：扩展方法只操作 builder，DryRun 即可验证。
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>()
            .WhereJson("payload", "name", "Alice")
            .AsDryRun();

        await Assert.That(dry.Sql).Contains("\"payload\"->>@p0 = @p1");
        await Assert.That(dry.Parameters.Count).IsEqualTo(2);
        await Assert.That(dry.Parameters[0].Value).IsEqualTo("name");
        await Assert.That(dry.Parameters[1].Value).IsEqualTo("Alice");
    }

    [Test]
    public async Task WhereJson_QuotesColumnIdentifier_DoubleQuoteEscaped()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>()
            .WhereJson("payload\"x", "k", 1)
            .AsDryRun();

        // 双写转义：嵌入的 " 不能提前闭合标识符
        await Assert.That(dry.Sql).Contains("\"payload\"\"x\"->>@p0");
    }

    [Test]
    public async Task WhereJson_NonStringValue_NormalizedToInvariantString()
    {
        await using var db = await TestDb.SqliteAsync();
        var dry = db.From<Product>()
            .WhereJson("payload", "count", 42)
            .AsDryRun();

        // ->> 返回 text：int 值归一为字符串绑定，避免 PG 端 text = integer 类型错误
        await Assert.That(dry.Parameters[1].Value).IsEqualTo("42");
    }

    [Test]
    public async Task WhereJson_NulInColumn_ThrowsArgumentException()
    {
        await using var db = await TestDb.SqliteAsync();

        await Assert.That(() => db.From<Product>().WhereJson("pay\0load", "k", "v"))
            .Throws<ArgumentException>();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_HealthCheck_Succeeds()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(Opts);
        await Assert.That((await db.HealthCheckAsync()).IsHealthy).IsTrue();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_DDL_Insert_Query_RoundTripsData()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(Opts);
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS pg_test");
            await db.ExecuteAsync($"CREATE TABLE pg_test (id BIGSERIAL PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL)");
            await db.ExecuteAsync($"INSERT INTO pg_test (name, value) VALUES ({"PG"}, {42})");
            var c = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM pg_test WHERE name = {"PG"}");
            await Assert.That(c).IsEqualTo(1);
        }
        finally
        {
            // T6：断言失败也清理表
            await db.ExecuteAsync($"DROP TABLE IF EXISTS pg_test");
        }
    }
}
