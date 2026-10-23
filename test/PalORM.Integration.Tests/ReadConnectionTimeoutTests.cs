using Npgsql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>2026-10-22 终审 P2-3：读连接 Open/初始化超时必须以 TimeoutException 呈现
/// （与主连接 CreateAsync 同口径），而不是调用方未取消时的 OperationCanceledException。</summary>
[NotInParallel("ExtBulkTable")]
public sealed class ReadConnectionTimeoutTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task ReadConnectionOpenTimeout_ThrowsTimeoutException()
    {
        string main = TestEnvironment.ResolvePostgreSqlConnectionString();
#pragma warning disable S1313 // 故意的非可路由地址：用于连接超时探针，不指向任何真实主机
        string badHost = "10.255.255.1";
#pragma warning restore S1313
        var badBuilder = new NpgsqlConnectionStringBuilder(main)
        {
            Host = badHost,
            Timeout = 30,
        };

        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = main,
            ReadConnectionString = badBuilder.ConnectionString,
            ConnectionTimeout = TimeSpan.FromSeconds(1),
        });

        TimeoutException? exception = await Assert.ThrowsAsync<TimeoutException>(
            async () => await db.From<Product>().ForRead().ToListAsync());

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Data["PalORM.InfrastructureTimeout"] is true).IsTrue();
    }
}
