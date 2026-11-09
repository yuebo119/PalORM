using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>写路径连接判死锁定（FLT4-2，2026-11-09）。
/// <para>背景：原 <c>ExecuteWriteRowsAsync</c> 的 try 只包 ValueTask 构造不 await，异步异常
/// （连接判死）落在返回的 ValueTask 里进不了 catch——写路径的判死标记从未置位，后续命令
/// 不会触发重建。本用例注入写路径判死并断言重建发生：撤掉 await 修复即红
/// （不重建 → 第二次写仍走 wrapper 旧连接 → CommandsCreated 增长）。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class WritePathConnectionFailureTests
{
    [Test]
    public async Task WritePathPoisoning_RebuildsConnection()
    {
        var boom = new InvalidOperationException("ExecuteNonQuery can only be called when the connection is open.");
        // 序号 1 = 第一条 Insert 的命令（同 ConnectionReuseFaultTests 的命令序号口径）
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnceOnCommand(1, boom));
        await using DataSession<SqliteProvider> session = created.Session;

        // 写路径注入判死 → 异常原样传播，判死标记置位
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InsertAsync(FaultInjectionFixtures.Row("seed", 1)));

        // 判死标记置位 → 下次建命令前重建（新连接不经 wrapper）
        var row = FaultInjectionFixtures.Row("seed2", 2);
        await session.InsertAsync(row);
        await Assert.That(row.Id).IsGreaterThan(0L);
        await Assert.That(created.Wrapper.CommandsCreated).IsEqualTo(1)
            .Because("判死标记应触发重建，重建出的新连接不经 wrapper；未置位则第二次写仍走 wrapper 旧连接");
    }
}
