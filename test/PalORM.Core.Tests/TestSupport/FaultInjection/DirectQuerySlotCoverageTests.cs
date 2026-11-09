using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>直查命令槽（<see cref="ReusableQuerySlot"/>）与判死收口的覆盖补齐（2026-11-09）。
/// <para>背景：PR #19 的 T1-diff 门禁把 <c>QueryStructsEntryAsync</c> 的复用分支（652-664）、
/// 判死 catch（680-685）与重建清槽（1468-1470）列为未覆盖。原因是既有 QueryStructs 用例
/// 每个测试各建会话，同形状直查从未达到晋升阈值 3；判死 catch 则缺故障注入入口。
/// 本文件用「同形状连查 → 换形状注入判死 → 验证重建」一条链路把三处串起来。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class DirectQuerySlotCoverageTests
{
    /// <summary>同形状直查第 3 次触发槽晋升（<see cref="ReusableQuerySlot.TryAcquire"/> 返回
    /// 非 null），命中复用分支；随后换形状注入判死，验证 catch 置位与重建清槽。</summary>
    [Test]
    public async Task QueryStructs_SameShapePromotes_ThenPoisoningRebuilds()
    {
        var boom = new InvalidOperationException("ExecuteReader can only be called when the connection is open.");
        // 命令序号：形状 A 三次（1/2/3，第 3 次晋升）→ 形状 B 新建（4）注入判死
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnceOnCommand(4, boom));
        await using DataSession<SqliteProvider> session = created.Session;

        for (int i = 0; i < 3; i++)
        {
            List<PlainSummary> rows = await session.QueryStructsAsync<PlainSummary>(
                $"SELECT Id, Name FROM fault_row");
            await Assert.That(rows.Count).IsEqualTo(0);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.QueryStructsAsync<PlainSummary>(
                $"SELECT Id, Name FROM fault_row WHERE Qty = {1}"));

        // 判死标记置位 → 下次建命令前重建，重建时清空已晋升的直查槽（InvalidateReusableCommands）
        List<PlainSummary> after = await session.QueryStructsAsync<PlainSummary>(
            $"SELECT Id, Name FROM fault_row WHERE Qty = {1}");
        await Assert.That(after.Count).IsEqualTo(0);
        await Assert.That(created.Wrapper.CommandsCreated).IsEqualTo(4)
            .Because("判死标记应触发重建，重建出的新连接不经 wrapper");
    }

    /// <summary>GetByKeyMany 读路径判死：异常落进外层 catch 置位判死标记（C2）。</summary>
    [Test]
    public async Task GetByKeyMany_ConnectionPoisoning_MarksPoisoned()
    {
        var boom = new InvalidOperationException("ExecuteReader can only be called when the connection is open.");
        // 命令序号：Insert(1) → GetByKeyMany(2) 注入判死
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnceOnCommand(2, boom));
        await using DataSession<SqliteProvider> session = created.Session;
        var row = FaultInjectionFixtures.Row("seed", 1);
        await session.InsertAsync(row);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.GetByKeyManyAsync<FaultRow>(new object[] { row.Id }));

        List<FaultRow> after = await session.GetByKeyManyAsync<FaultRow>(new object[] { row.Id });
        await Assert.That(after.Count).IsEqualTo(1);
        await Assert.That(created.Wrapper.CommandsCreated).IsEqualTo(2)
            .Because("判死标记应触发重建，重建出的新连接不经 wrapper");
    }
}
