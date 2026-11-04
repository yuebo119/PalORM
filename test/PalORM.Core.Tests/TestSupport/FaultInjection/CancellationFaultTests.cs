using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>FLT-6：取消。
/// <para>取消后的三件事：抛的是 <see cref="OperationCanceledException"/>（不是被包装成
/// 别的异常）、连接仍 Open（没被取消打脏）、下一条命令成功（会话回到可用态）。
/// 第三种形态在事务内：取消命令后回滚，行数归零。</para></summary>
public sealed class CancellationFaultTests
{
    [Test]
    public async Task AlreadyCancelledToken_ThrowsAndSessionStaysUsable()
    {
        var created = await FaultInjectionFixtures.CreateAsync();
        await using DataSession<SqliteProvider> session = created.Session;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync($"SELECT 1", cts.Token));

        await Assert.That(created.Wrapper.State).IsEqualTo(System.Data.ConnectionState.Open);
        await session.ExecuteAsync($"SELECT 2");   // 后续命令成功
        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(0);
    }

    [Test]
    public async Task InjectedCancelMidCommand_ThrowsOperationCanceled_AndConnectionIsReusable()
    {
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).CancelOnCommand(1));
        await using DataSession<SqliteProvider> session = created.Session;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync($"SELECT 1"));

        await Assert.That(created.Wrapper.State).IsEqualTo(System.Data.ConnectionState.Open);
        await session.ExecuteAsync($"SELECT 2");
        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(0);
    }

    [Test]
    public async Task InjectedCancelInsideTransaction_RollsBackAndSessionReusable()
    {
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).CancelOnCommand(1));
        await using DataSession<SqliteProvider> session = created.Session;

        await using System.Data.Common.DbTransaction tran = await session.BeginTransactionAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await session.InsertAsync(FaultInjectionFixtures.Row("a", 1)));
        await tran.RollbackAsync();

        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(0);

        await session.InsertAsync(FaultInjectionFixtures.Row("b", 2));   // 取消之后的新事务命令成功
        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(1);
    }
}
