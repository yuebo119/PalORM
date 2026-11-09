using System.Data.Common;
using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>FLT-3：批量中途失败。
/// <para>断言三件：行数、异常类型（调用方拿到的就是注入的那一个）、会话状态（失败后仍可用）。</para>
/// <para>两条工程细节决定注入点能不能命中，写进注释：①SQLite 参数上限把 2500 行的多值
/// INSERT 切成多条语句（每块 500 行），行号 600 落在第二条语句的 reader 上，所以断点放在
/// 第一条语句的中间行；②取键的 SELECT 若走会话会白占一个命令序号，因此直接走底层连接。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class BulkFaultTests
{
    private const int TotalRows = 2500;

    [Test]
    public async Task BulkInsertReturning_MidMaterializationFailure_LeavesNoPartialRows()
    {
        // 分块整体包事务（2026-11-04 修复，FLT-3 实测缺口）：读侧故障中断后前面若干块
        // 一并回滚，不留残留（修复前实测残留 499 行，总 2500）。
        var boom = new IOException("注入：结果集读到第一条语句的第 100 行断开");
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailReaderAtRow(commandOrdinal: 1, row: 100, boom));
        await using DataSession<SqliteProvider> session = created.Session;

        IOException? thrown = await Assert.ThrowsAsync<IOException>(async () =>
            await session.BulkInsertReturningAsync(FaultInjectionFixtures.Rows(TotalRows)));
        await Assert.That(thrown).IsSameReferenceAs(boom);

        long leftover = await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row");
        await Assert.That(leftover).IsEqualTo(0)
            .Because($"分块插入未整体回滚，残留 {leftover} 行");
        await Assert.That(created.Wrapper.State).IsEqualTo(System.Data.ConnectionState.Open);

        await session.InsertAsync(FaultInjectionFixtures.Row("after", 1));
        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(leftover + 1);
    }

    [Test]
    public async Task BulkInsert_FirstCommandFails_WritesNothing()
    {
        // 非返回路径在 SQLite 上只发一条命令（行预编译形态，batchSize 不决定语句形状），
        // 所以「第一条命令失败」等价于整批未执行：0 行残留，异常原样抛出。
        var boom = new InvalidOperationException("注入：批量第一条命令失败");
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnCommand(1, boom));
        await using DataSession<SqliteProvider> session = created.Session;

        InvalidOperationException? thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.BulkInsertAsync(FaultInjectionFixtures.Rows(10), batchSize: 3));
        await Assert.That(thrown).IsSameReferenceAs(boom);

        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(0);
        await Assert.That(created.Wrapper.State).IsEqualTo(System.Data.ConnectionState.Open);
    }

    [Test]
    public async Task BulkDelete_SecondCommandFails_KeepsAllRowsAfterRollback()
    {
        var boom = new InvalidOperationException("注入：批量删除第二条命令失败");
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnCommand(2, boom));
        await using DataSession<SqliteProvider> session = created.Session;
        await session.BulkInsertAsync(FaultInjectionFixtures.Rows(6));   // 命令 1

        List<long> keys = [];
        await using (DbCommand read = created.Keeper.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM fault_row ORDER BY Id";
            await using DbDataReader reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                keys.Add(reader.GetInt64(0));
            }
        }
        await Assert.That(keys.Count).IsEqualTo(6);

        InvalidOperationException? thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.BulkDeleteAsync<FaultRow, long>(keys));   // 命令 2 注入
        await Assert.That(thrown).IsSameReferenceAs(boom);

        // 批量删除整体在事务内：失败回滚，失败前那一批的删除效果一并撤销。
        await Assert.That(await session.ScalarAsync<long>($"SELECT COUNT(*) FROM fault_row")).IsEqualTo(6);
        await Assert.That(created.Wrapper.State).IsEqualTo(System.Data.ConnectionState.Open);
    }

    /// <summary>FLT4-3 锁定（2026-11-09）：批量执行点此前裸调 ExecuteNonQueryAsync，
    /// 异步判死异常不进任何 catch → 判死标记从不置位。本用例注入真实形态的判死异常
    /// （驱动消息），断言后续操作触发重建：撤掉包装方法即红（不重建 → 第二次写仍走 wrapper）。</summary>
    [Test]
    public async Task BulkInsert_ConnectionPoisoning_RebuildsConnection()
    {
        var boom = new InvalidOperationException("ExecuteNonQuery can only be called when the connection is open.");
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnCommand(1, boom));
        await using DataSession<SqliteProvider> session = created.Session;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.BulkInsertAsync(FaultInjectionFixtures.Rows(10), batchSize: 3));

        // 判死标记置位 → 下次建命令前重建（新连接不经 wrapper）
        var row = FaultInjectionFixtures.Row("after", 1);
        await session.InsertAsync(row);
        await Assert.That(row.Id).IsGreaterThan(0L);
        await Assert.That(created.Wrapper.CommandsCreated).IsEqualTo(1)
            .Because("判死标记应触发重建，重建出的新连接不经 wrapper；未置位则第二次写仍走 wrapper 旧连接");
    }
}
