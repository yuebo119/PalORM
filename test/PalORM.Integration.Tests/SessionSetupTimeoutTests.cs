using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>2026-10-22 终审 P2-4：Open 同步完成时旧实现不武装 CancelAfter，Provider 初始化与
/// SessionSetupSql 可无限阻塞。主连接（池暖后 Open 常同步）后的会话 SQL 必须受 ConnectionTimeout 约束。</summary>
[NotInParallel("ExtBulkTable")]
public sealed class SessionSetupTimeoutTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task SessionSetupSql_ExceedingConnectionTimeout_ThrowsTimeout()
    {
        var options = new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString(),
            ConnectionTimeout = TimeSpan.FromSeconds(1),
            SessionSetupSql = "SELECT pg_sleep(10)",
            MaxRetries = 0,
        };

        TimeoutException? exception = await Assert.ThrowsAsync<TimeoutException>(
            async () => await DataSession<PostgreSqlProvider>.CreateAsync(options));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Data["PalORM.InfrastructureTimeout"] is true).IsTrue();
    }
}
