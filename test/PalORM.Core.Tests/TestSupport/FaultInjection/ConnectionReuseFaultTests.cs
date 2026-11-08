#pragma warning disable PALORM005 // 3 次同类 Update 是养出 PL-2 复用槽的必要载体，逐条是有意的
using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>连接判死自愈（FLT-4）与 PL-2 已晋升 CRUD 命令复用槽的交错。
/// <para><b>已修复的缺口（C1，2026-11-08）</b>：原 <c>RebuildConnectionIfNeeded</c> 只挂在
/// <c>CreateCommand</c> 上；<c>ReusableCrudCommand</c> 记录无 Connection 字段，命中分支
/// 直接返回旧 <c>DbCommand</c> 而不新建命令。因此「读路径判死 → 复用槽绕过重建 →
/// 后续新建命令路径重建并 Dispose 旧连接 → 复用槽仍绑在已释放连接上」这条链路会让
/// 同类 CRUD 持续失败。修复：重建成功后一并清空 6 个复用槽与直查槽，命中路径失效。</para>
/// <para><b>本用例断言修复后的恢复行为</b>：判死 → 重建清空槽 → 同类 Update 新建命令并成功。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class ConnectionReuseFaultTests
{
    /// <summary>SQLite Provider 认定的连接判死异常（与驱动真实消息同形态，2026-11-09 实测修正：
    /// 原 "Connection is not open" 是死代码消息，真实形态为 "…can only be called when the connection is open."）。</summary>
    private static InvalidOperationException ConnectionFailure()
        => new("ExecuteNonQuery can only be called when the connection is open.");

    /// <summary>晋升阈值 3（DataSession.Crud.cs:245）：前 3 次同类 Update 养出复用槽。</summary>
    private const int PromotionThreshold = 3;

    [Test]
    public async Task PromotedReusableCrudSlot_RecoversAfterConnectionRebuild()
    {
        var boom = ConnectionFailure();
        // 序号 5 = Insert(1) + Update×3(2/3/4) 之后的第一条 Scalar；一次性注入模拟
        // 「那一次执行时连接真的死了」，后续执行还原底层真实连接。
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnceOnCommand(5, boom));
        await using DataSession<SqliteProvider> session = created.Session;

        var row = FaultInjectionFixtures.Row("seed", 1);
        await session.InsertAsync(row);
        await Assert.That(row.Id).IsGreaterThan(0L);

        // 养出 _reusableUpdate 槽：前 2 次新建命令，第 3 次晋升并写入槽。
        for (int i = 1; i <= PromotionThreshold; i++)
        {
            int affected = await session.UpdateAsync(new FaultRow { Id = row.Id, Name = $"u{i}", Qty = 10 + i });
            await Assert.That(affected).IsEqualTo(1);
        }
        int commandsAfterPromotion = created.Wrapper.CommandsCreated;
        await Assert.That(commandsAfterPromotion).IsEqualTo(4)
            .Because("Insert(1) + Update#1(2) + Update#2(3) + Update#3 晋升(4)");

        // 读路径注入判死异常：异常原样传播，判死标记置位。
        InvalidOperationException? thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.ScalarAsync<long>($"SELECT Qty FROM fault_row WHERE Id = {row.Id}"));
        await Assert.That(thrown).IsSameReferenceAs(boom);
        await Assert.That(created.Wrapper.CommandsCreated).IsEqualTo(5);

        // 关键：第 4 次 Update 命中复用槽 → 不新建命令 → 不触发重建。
        // 此处成功恰是缺陷的一半：判死标记被挂起而未消费。
        int affectedAfterPoison = await session.UpdateAsync(
            new FaultRow { Id = row.Id, Name = "u4", Qty = 14 });
        await Assert.That(affectedAfterPoison).IsEqualTo(1);
        await Assert.That(created.Wrapper.CommandsCreated).IsEqualTo(5)
            .Because("复用槽命中时不得新建命令，否则会意外触发 RebuildConnectionIfNeeded");

        // 新建命令路径（Scalar）触发重建：_conn 换新连接、旧连接 Dispose。
        // 注：重建走 TProvider.CreateConnection，脱离 wrapper，故 CommandsCreated 不再增长。
        long qty = await session.ScalarAsync<long>($"SELECT Qty FROM fault_row WHERE Id = {row.Id}");
        await Assert.That(qty).IsEqualTo(14L)
            .Because("重建后仍能读到第 4 次 Update 写入的值，数据未丢失");
        await Assert.That(created.Wrapper.CommandsCreated).IsEqualTo(5)
            .Because("重建出的新连接不经 wrapper");

        // C1 修复后：重建时已清空复用槽，第 5 次 Update 新建命令（绑定新连接），成功。
        int affectedAfterRebuild = await session.UpdateAsync(
            new FaultRow { Id = row.Id, Name = "u5", Qty = 15 });
        await Assert.That(affectedAfterRebuild).IsEqualTo(1);
        await Assert.That(await session.ScalarAsync<long>($"SELECT Qty FROM fault_row WHERE Id = {row.Id}"))
            .IsEqualTo(15L)
            .Because("重建清空复用槽后，同类 Update 应新建命令并成功写入");
    }

    /// <summary>对照臂（能失败验证）：不注入判死异常时，同一条复用槽链路全程可用。
    /// 证明上面那条用例的失败源于「判死 + 重建」，不是复用槽本身或夹具重复注入。</summary>
    [Test]
    public async Task WithoutPoisoning_PromotedReusableCrudSlot_KeepsWorking()
    {
        var created = await FaultInjectionFixtures.CreateAsync();
        await using DataSession<SqliteProvider> session = created.Session;

        var row = FaultInjectionFixtures.Row("seed", 1);
        await session.InsertAsync(row);

        for (int i = 1; i <= PromotionThreshold; i++)
        {
            int affected = await session.UpdateAsync(new FaultRow { Id = row.Id, Name = $"u{i}", Qty = 10 + i });
            await Assert.That(affected).IsEqualTo(1);
        }

        long qtyAfterPromotion = await session.ScalarAsync<long>(
            $"SELECT Qty FROM fault_row WHERE Id = {row.Id}");
        await Assert.That(qtyAfterPromotion).IsEqualTo(10L + PromotionThreshold);

        int affectedAfterScalar = await session.UpdateAsync(
            new FaultRow { Id = row.Id, Name = "u4", Qty = 14 });
        await Assert.That(affectedAfterScalar).IsEqualTo(1);

        long qty = await session.ScalarAsync<long>($"SELECT Qty FROM fault_row WHERE Id = {row.Id}");
        await Assert.That(qty).IsEqualTo(14L);

        int affectedAgain = await session.UpdateAsync(
            new FaultRow { Id = row.Id, Name = "u5", Qty = 15 });
        await Assert.That(affectedAgain).IsEqualTo(1);
        await Assert.That(await session.ScalarAsync<long>($"SELECT Qty FROM fault_row WHERE Id = {row.Id}"))
            .IsEqualTo(15L);
    }
}
