using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>2026-10-22 终审 P2-2：池化空闲会话被 NAT/LB 静默掐断后 State 仍可能为 Open，
/// 旧实现反复租出死连接。空闲超过 PoolConnectionValidationInterval 时租出前 SELECT 1 探活，
/// 失败由池丢弃并换新，调用方不收到过期连接。</summary>
[NotInParallel("ExtBulkTable")]
public sealed class PoolHealthTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task IdleBrokenConnection_IsReplacedOnRent()
    {
        string cs = TestEnvironment.ResolvePostgreSqlConnectionString();
        var pool = DataSessionPool<PostgreSqlProvider>.Create(new DbOptions
        {
            ConnectionString = cs,
            PoolConnectionValidationInterval = TimeSpan.FromMilliseconds(1),
        }, maxIdleSize: 1);
        try
        {
            DataSession<PostgreSqlProvider> first = await pool.RentAsync();
            int backendPid = await first.ScalarAsync<int>($"SELECT pg_backend_pid()");
            await first.DisposeAsync();
            await Task.Delay(20);

            await using (var killer = await DataSession<PostgreSqlProvider>.CreateAsync(
                new DbOptions { ConnectionString = cs }))
            {
                bool terminated = await killer.ScalarAsync<bool>(
                    $"SELECT pg_terminate_backend({backendPid})");
                await Assert.That(terminated).IsTrue();
            }

            DataSession<PostgreSqlProvider> second = await pool.RentAsync();
            await Assert.That(await second.ScalarAsync<int>($"SELECT 1")).IsEqualTo(1);
            await second.DisposeAsync();
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
}
