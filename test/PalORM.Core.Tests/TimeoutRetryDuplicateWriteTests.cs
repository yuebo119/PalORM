using Microsoft.Data.Sqlite;
using PalORM.Core.Tests.TestSupport.FaultInjection;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>CS-17 超时重试重复写（修订版方案 §5）。
/// <para><b>语义来源</b>：ITM-310——「超时不代表未执行」。本例用
/// <c>FailAfterExecutionOnCommand</c> 在命令<b>执行之后</b>抛 <c>SQLITE_BUSY</c>，精确模拟
/// 「服务器已写入、客户端没等到响应」。</para>
/// <para><b>产品边界（本轮实测确认）</b>：写路径走 <c>ExecuteWithTimeoutAsync</c>，<b>只包装超时
/// 不重试</b>（源码注释：写操作不能重试，INSERT 重试 = 重复写入）。因此重复写入的触发者是
/// <b>调用方重试</b>，不是产品内部重试——本例按调用方视角编排：第一次拿到异常，库里已有行；
/// 调用方再试一次即出现第二行。</para>
/// <para><b>为什么不用真库触发器 + pg_sleep</b>：PG 的语句超时会取消整条语句并回滚，
/// 客户端超时后库里没有行——复现不出「已提交但响应丢失」；那种形态需要断链/丢包。
/// 包装层在驱动边界注入是确定性的等价形态，且不依赖墙钟（T15）。真实网络丢包路径零覆盖，
/// 该声明写在报告里，不把模拟说成实测。</para>
/// <para><b>两条判据</b>：无唯一键时重复写可见（COUNT=2，P2 基线事实，不是缺陷）；有唯一键时
/// 重试被唯一约束拒绝（COUNT=1 且抛异常），证明「幂等键/唯一约束」是该风险的处置手段。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class TimeoutRetryDuplicateWriteTests
{
    /// <summary>模拟「命令已执行、响应丢失」：SQLITE_BUSY(5) 是瞬时故障的常见形态；
    /// <c>once: true</c> 保证只丢第一次的响应。</summary>
    private static SqliteException ResponseLostAfterExecution()
        => new("注入：命令已执行但响应丢失（SQLITE_BUSY 形态）", 5, 5);

    [Test]
    public async Task CS17_ResponseLostAfterExecution_CallerRetryDuplicatesWrite()
    {
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailAfterExecutionOnCommand(
                1, ResponseLostAfterExecution(), once: true));
        await using DataSession<SqliteProvider> session = created.Session;

        // 第一次执行：行已写入，随后抛 BUSY（产品写路径不重试，异常原样上抛）
        await Assert.ThrowsAsync<SqliteException>(async () =>
            await session.ExecuteAsync($"INSERT INTO fault_row (Name, Qty) VALUES ({"dup"}, {1})"));
        await Assert.That(await CountAsync(created.Keeper, "dup")).IsEqualTo(1L);

        // 调用方看到失败后重试（新命令序号，不再注入）：第二行落地
        await session.ExecuteAsync($"INSERT INTO fault_row (Name, Qty) VALUES ({"dup"}, {1})");
        await Assert.That(await CountAsync(created.Keeper, "dup")).IsEqualTo(2L);
    }

    [Test]
    public async Task CS17_UniqueKey_RejectsTheDuplicateWrite()
    {
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailAfterExecutionOnCommand(
                1, ResponseLostAfterExecution(), once: true));
        await using DataSession<SqliteProvider> session = created.Session;
        await using (SqliteCommand index = created.Keeper.CreateCommand())
        {
            index.CommandText = "CREATE UNIQUE INDEX ux_fault_row_name ON fault_row(Name)";
            await index.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await session.ExecuteAsync($"INSERT INTO fault_row (Name, Qty) VALUES ({"dup"}, {1})"));
        // 调用方重试命中唯一约束：库里只有第一次写入的那一行
        await Assert.ThrowsAsync<SqliteException>(async () =>
            await session.ExecuteAsync($"INSERT INTO fault_row (Name, Qty) VALUES ({"dup"}, {1})"));
        await Assert.That(await CountAsync(created.Keeper, "dup")).IsEqualTo(1L);
    }

    private static async Task<long> CountAsync(SqliteConnection keeper, string name)
    {
        await using SqliteCommand command = keeper.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fault_row WHERE Name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
