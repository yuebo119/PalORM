using System.Data.Common;
using MySqlConnector;
using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.FaultInjection;

/// <summary>FLT-9：MySQL 死锁（真库）。
/// <para>反向加锁制造 1213：会话一锁 A 等 B，会话二锁 B 等 A。断言三件：错误码是 1213、
/// 该异常被 Provider 判为可重试、死锁之后新事务里同样的更新能成功（调用方拿到重试语义，
/// 不需要自己分辨错误码）。</para>
/// <para>只在全量通道跑（<c>ExternalDatabase</c>）：本用例依赖 InnoDB 的死锁检测时序，
/// SQLite/内存形态复现不出来。</para></summary>
[Property("Category", "ExternalDatabase")]
[Property("Category", "FaultInjection")]
[NotInParallel("FaultInjectionMySqlDeadlock")]
public sealed class MySqlDeadlockFaultTests
{
    private const long RowA = 9_000_001;
    private const long RowB = 9_000_002;

    [Test]
    public async Task ReverseLockOrder_Produces1213_AndRetrySucceeds()
    {
        var options = new DbOptions { ConnectionString = TestEnvironment.ResolveMySqlConnectionString() };
        await using var first = await DataSession<MySqlProvider>.CreateAsync(options);
        await using var second = await DataSession<MySqlProvider>.CreateAsync(options);
        await first.MigrateAsync();
        // 上一次运行可能留下同样的行（真库是持久化的），先清掉再插
        await first.ExecuteAsync($"DELETE FROM palorm_fault_row WHERE Id IN ({RowA}, {RowB})");
        await first.BulkInsertAsync(
        [
            new FaultInjectionRow { Id = RowA, Name = "a", Amount = 1 },
            new FaultInjectionRow { Id = RowB, Name = "b", Amount = 2 },
        ]);

        using var aLocked = new ManualResetEventSlim(false);
        using var bLocked = new ManualResetEventSlim(false);

        Task firstWork = Task.Run(async () =>
        {
            await using DbTransaction transaction = await first.BeginTransactionAsync();
            await first.UpdateAsync(new FaultInjectionRow { Id = RowA, Name = "a", Amount = 11 });
            aLocked.Set();
            await Task.Run(() => bLocked.Wait(TimeSpan.FromSeconds(30)));
            await first.UpdateAsync(new FaultInjectionRow { Id = RowB, Name = "b", Amount = 22 });
            await transaction.CommitAsync();
        });

        MySqlException? deadlock = await Assert.ThrowsAsync<MySqlException>(async () =>
        {
            await using DbTransaction transaction = await second.BeginTransactionAsync();
            try
            {
                await second.UpdateAsync(new FaultInjectionRow { Id = RowB, Name = "b", Amount = 22 });
                bLocked.Set();
                // 等 first 真的停在「等 B」上，再由本会话请求 A 来闭合环路：
                // 死锁由 InnoDB 判给闭合环路的那次请求，加这段延迟避免时序竞争让 first 变成受害者。
                await Task.Delay(400);
                await second.UpdateAsync(new FaultInjectionRow { Id = RowA, Name = "a", Amount = 11 });
                await transaction.CommitAsync();
            }
            catch
            {
                // 死锁由 InnoDB 回滚整个事务，这里再显式回滚一次：会话要求事务在会话释放前
                // 处于已完成状态，否则 Dispose 会抛「Complete or dispose the active transaction」
                await transaction.RollbackAsync();
                throw;
            }
        });

        await Assert.That(deadlock).IsNotNull();
        await Assert.That(deadlock!.Number).IsEqualTo(1213);
        // MySQL Provider 未覆写 IsTransient，走 IDbProvider 默认：DbException.IsTransient
        await Assert.That(((System.Data.Common.DbException)deadlock!).IsTransient).IsTrue();

        await firstWork;   // 死锁回滚释放锁后，第一条链路完成

        // 重试语义：新会话 + WithRetry，死锁后同样的两条更新顺序执行即可成功
        await using var retrying = (await DataSession<MySqlProvider>.CreateAsync(options))
            .WithRetry(maxRetries: 3, attempt => TimeSpan.FromMilliseconds(100 * (attempt + 1)));
        await retrying.UpdateAsync(new FaultInjectionRow { Id = RowA, Name = "a", Amount = 33 });
        await retrying.UpdateAsync(new FaultInjectionRow { Id = RowB, Name = "b", Amount = 44 });
        await Assert.That(await retrying.ScalarAsync<long>($"SELECT amount FROM palorm_fault_row WHERE Id = {RowA}"))
            .IsEqualTo(33);
    }
}
