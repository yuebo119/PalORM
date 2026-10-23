using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>2026-10-22 终审 P1-8：池化会话跨租期的连接级会话态复位。
/// PG 用 DISCARD ALL、MySQL 用 RESET CONNECTION，随后重放池基准 SessionSetupSql；
/// 上一租期的 SET search_path / SET SESSION sql_mode 不得进入下一租期。</summary>
[NotInParallel("ExtBulkTable")]
public sealed class PoolSessionStateResetTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_DirtyRent_ResetsSearchPath()
    {
        var pool = DataSessionPool<PostgreSqlProvider>.Create(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
        }, maxIdleSize: 1);
        try
        {
            await using (var session = await pool.RentAsync())
            {
                await session.ExecuteAsync($"SET search_path TO pg_catalog");
            }
            await using (var session = await pool.RentAsync())
            {
                string? path = await session.ScalarAsync<string>(
                    $"SELECT current_setting('search_path')");
                await Assert.That(path).IsNotEqualTo("pg_catalog");
            }
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_DirtyRent_ReappliesSessionSetupSql()
    {
        var pool = DataSessionPool<PostgreSqlProvider>.Create(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString(),
            SessionSetupSql = "SET search_path TO public"
        }, maxIdleSize: 1);
        try
        {
            await using (var session = await pool.RentAsync())
            {
                await session.ExecuteAsync($"SET search_path TO pg_catalog");
            }
            await using (var session = await pool.RentAsync())
            {
                string? path = await session.ScalarAsync<string>(
                    $"SELECT current_setting('search_path')");
                await Assert.That(path).IsEqualTo("public");
            }
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_DirtyRent_ResetsSqlMode()
    {
        var pool = DataSessionPool<MySqlProvider>.Create(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
        }, maxIdleSize: 1);
        try
        {
            await using (var session = await pool.RentAsync())
            {
                await session.ExecuteAsync($"SET SESSION sql_mode = 'ANSI'");
            }
            await using (var session = await pool.RentAsync())
            {
                string? mode = await session.ScalarAsync<string>($"SELECT @@SESSION.sql_mode");
                await Assert.That(mode ?? "").DoesNotContain("ANSI");
            }
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
}
