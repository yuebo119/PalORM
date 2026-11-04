using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>FLT-2：事务中命令失败。
/// <para>每条用例都压三件事：行没有残留（回滚生效）、连接仍然 Open（会话没被失败打脏）、
/// 失败之后下一条命令能成功（状态机走回可用态）。只断言「抛异常」的测试证明不了这三件；
/// 2026-11-03 修掉的串行键丢失（1/4 偶发）属于第四问：失败后的下一次自增键不能错位。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class TransactionFaultTests
{
    [Test]
    public async Task FailingSecondCommand_RollsBackEverything_AndSessionStaysUsable()
    {
        var boom = new InvalidOperationException("注入：事务内第二条命令失败");
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnCommand(2, boom));
        await using DataSession<SqliteProvider> session = created.Session;
        FaultyConnection wrapper = created.Wrapper;

        await using System.Data.Common.DbTransaction tran = await session.BeginTransactionAsync();
        await session.InsertAsync(FaultInjectionFixtures.Row("a", 1));   // 命令 1

        InvalidOperationException? thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InsertAsync(FaultInjectionFixtures.Row("b", 2)));   // 命令 2 注入
        await Assert.That(thrown).IsSameReferenceAs(boom);

        await tran.RollbackAsync();

        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(0);
        await Assert.That(wrapper.State).IsEqualTo(System.Data.ConnectionState.Open);

        await session.InsertAsync(FaultInjectionFixtures.Row("c", 3));   // 失败之后新命令成功
        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(1);
    }

    [Test]
    public async Task FailingFirstCommand_LeavesNoRowAndNextTransactionStartsFromFreshKey()
    {
        // 自增键连续性：第一次事务整体回滚后，下一次插入的自增键不得被失败的那次吃掉。
        var boom = new InvalidOperationException("注入：事务内第一条命令失败");
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnCommand(1, boom));
        await using DataSession<SqliteProvider> session = created.Session;

        await using System.Data.Common.DbTransaction tran = await session.BeginTransactionAsync();
        InvalidOperationException? thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InsertAsync(FaultInjectionFixtures.Row("a", 1)));
        await Assert.That(thrown).IsSameReferenceAs(boom);
        await tran.RollbackAsync();

        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(0);

        await using System.Data.Common.DbTransaction secondTran = await session.BeginTransactionAsync();
        FaultRow after = await session.InsertAsync(FaultInjectionFixtures.Row("b", 2));
        await secondTran.CommitAsync();

        await Assert.That(after.Id).IsEqualTo(1);
        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(1);
    }

    [Test]
    public async Task FailureOnBatchCommandInsideTransaction_RollsBackAllBatches()
    {
        // 批量入口在事务内：第 1 条命令成功写入后，第 2 条命令失败，已写入部分必须一起回滚。
        var boom = new InvalidOperationException("注入：批量第二条命令失败");
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnCommand(2, boom));
        await using DataSession<SqliteProvider> session = created.Session;

        await using System.Data.Common.DbTransaction tran = await session.BeginTransactionAsync();
        await session.InsertAsync(FaultInjectionFixtures.Row("a", 1));   // 命令 1
        InvalidOperationException? thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.BulkInsertAsync(FaultInjectionFixtures.Rows(5)));   // 命令 2 注入
        await Assert.That(thrown).IsSameReferenceAs(boom);
        await tran.RollbackAsync();

        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(0);
        await Assert.That(created.Wrapper.State).IsEqualTo(System.Data.ConnectionState.Open);
    }
}
