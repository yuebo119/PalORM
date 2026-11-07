using MySqlConnector;
using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>CS-17 真实网络路径：服务器已执行、响应在链路上丢失（修订版方案 §5 残余子场景）。
/// <para><b>与 <c>TimeoutRetryDuplicateWriteTests</c> 的分工</b>：那条在驱动包装层注入
/// 「执行后抛异常」，是进程内约定；本条用 <see cref="DropResponseProxy"/> 在 TCP 边界
/// 丢弃服务器已返回的 OK 包，走真实 socket，覆盖「链路丢响应」这条路径。</para>
/// <para><b>判据</b>：客户端必须拿到失败；直连核对服务器已提交（COUNT=1）；调用方重试后
/// 无唯一键出现第二行（COUNT=2，ITM-310 基线）；有唯一键时重试被拒（COUNT=1）。</para>
/// <para><b>触发确定性</b>：代理在「服务器响应已到达」时切断，而不是固定延迟，因此被丢弃的
/// 一定是执行后的响应；用例用 <c>DropTriggered</c> 等它完成再断言，不靠 sleep 猜时序（T15）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class RealNetworkLossTests
{
    private static MySqlConnectionStringBuilder DirectBuilder()
        => new(TestEnvironment.ResolveMySqlConnectionString());

    private static async Task<DataSession<MySqlProvider>> OpenDirectAsync(MySqlConnectionStringBuilder builder)
        => await DataSession<MySqlProvider>.CreateAsync(
            new DbOptions { ConnectionString = builder.ConnectionString });

    private static async Task PrepareAsync(MySqlConnectionStringBuilder builder, bool uniqueIndex)
    {
        await using DataSession<MySqlProvider> db = await OpenDirectAsync(builder);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS cs17_loss_rows");
        await db.ExecuteAsync(
            $"CREATE TABLE cs17_loss_rows (Id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(64) NOT NULL)");
        if (uniqueIndex)
            await db.ExecuteAsync($"CREATE UNIQUE INDEX ux_cs17_name ON cs17_loss_rows (name)");
    }

    private static async Task CleanupAsync(MySqlConnectionStringBuilder builder)
    {
        await using DataSession<MySqlProvider> db = await OpenDirectAsync(builder);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS cs17_loss_rows");
    }

    /// <summary>走代理发一条会被丢响应的 INSERT。返回客户端看到的异常（应为非 null）。</summary>
    private static async Task<Exception?> InsertThroughLossyProxyAsync(
        MySqlConnectionStringBuilder builder, DropResponseProxy proxy, string name)
    {
        var proxied = new MySqlConnectionStringBuilder(builder.ConnectionString)
        {
            Server = "127.0.0.1",
            Port = (uint)proxy.Port,
            Pooling = false,
        };
        DataSession<MySqlProvider> session = await DataSession<MySqlProvider>.CreateAsync(
            new DbOptions { ConnectionString = proxied.ConnectionString });
        Exception? observed = null;
        try
        {
            await session.ExecuteAsync($"INSERT INTO cs17_loss_rows (name) VALUES ({name})");
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception cleanupException)
        {
            _ = cleanupException;
        }
        try
        {
            await proxy.DropTriggered.WaitAsync(TimeSpan.FromSeconds(30));
            return observed;
        }
        catch (TimeoutException)
        {
            return new TimeoutException($"代理未观察到标记 SQL；客户端流量片段：{proxy.LastClientTraffic}");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task CS17_RealTcp_ResponseLostAfterExecution_CallerRetryDuplicates()
    {
        MySqlConnectionStringBuilder builder = DirectBuilder();
        await PrepareAsync(builder, uniqueIndex: false);
        try
        {
            await using var proxy = DropResponseProxy.Start(
                builder.Server, (int)builder.Port, "INSERT INTO cs17_loss_rows");
            Exception? lost = await InsertThroughLossyProxyAsync(builder, proxy, "real-loss");
            await Assert.That(lost).IsNotNull();

            await using DataSession<MySqlProvider> verify = await OpenDirectAsync(builder);
            long afterLoss = await verify.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM cs17_loss_rows WHERE name = {"real-loss"}");
            // 服务器已提交：响应丢了，行在
            await Assert.That(afterLoss).IsEqualTo(1L);

            // 调用方不知道已提交，重试同一业务写入 → 第二行（ITM-310 基线，P2）
            await verify.ExecuteAsync($"INSERT INTO cs17_loss_rows (name) VALUES ({"real-loss"})");
            long afterRetry = await verify.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM cs17_loss_rows WHERE name = {"real-loss"}");
            await Assert.That(afterRetry).IsEqualTo(2L);
        }
        finally
        {
            await CleanupAsync(builder);
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task CS17_RealTcp_UniqueKey_RejectsTheRetry()
    {
        MySqlConnectionStringBuilder builder = DirectBuilder();
        await PrepareAsync(builder, uniqueIndex: true);
        try
        {
            await using var proxy = DropResponseProxy.Start(
                builder.Server, (int)builder.Port, "INSERT INTO cs17_loss_rows");
            Exception? lost = await InsertThroughLossyProxyAsync(builder, proxy, "uniq-loss");
            await Assert.That(lost).IsNotNull();

            await using DataSession<MySqlProvider> verify = await OpenDirectAsync(builder);
            await Assert.That(await verify.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM cs17_loss_rows WHERE name = {"uniq-loss"}")).IsEqualTo(1L);

            // 唯一键把重复写挡在库外：调用方重试拿到 1062，行数仍为 1
            Exception? duplicate = null;
            try
            {
                await verify.ExecuteAsync($"INSERT INTO cs17_loss_rows (name) VALUES ({"uniq-loss"})");
            }
            catch (Exception exception)
            {
                duplicate = exception;
            }
            await Assert.That(duplicate).IsTypeOf<MySqlException>();
            await Assert.That(await verify.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM cs17_loss_rows WHERE name = {"uniq-loss"}")).IsEqualTo(1L);
        }
        finally
        {
            await CleanupAsync(builder);
        }
    }
}
