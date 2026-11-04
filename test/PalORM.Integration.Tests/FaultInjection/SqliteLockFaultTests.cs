using PalORM.Integration.Tests.Conformance;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.FaultInjection;

/// <summary>FLT-7：SQLite 写锁冲突（会话级，不需要真库）。
/// <para>两个会话共用同一内存库：持有方开写事务并插入（占住 RESERVED 锁），争用方再写。
/// 断言三件：错误码是 SQLITE_BUSY(5)、该异常被判为可重试（Provider 的 IsTransient）、
/// 锁释放后同一条插入成功。只断言「抛异常」会漏掉分类与重试语义，而这两条正是
/// <c>WithRetry()</c> 是否救得回的前提。</para></summary>
[Property("Category", "FaultInjection")]
[NotInParallel("FaultInjectionSqliteLock")]
public sealed class SqliteLockFaultTests
{
    [Test]
    public async Task ContendedWrite_IsTransient_AndSucceedsAfterLockReleased()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("fault_sqlite_lock");
        await using DataSession<SqliteProvider> holder = await shared.OpenSessionAsync();
        await holder.MigrateAsync();
        await using DataSession<SqliteProvider> contender = await shared.OpenSessionAsync();
        // 引擎默认 busy_timeout 约 30 秒，等下去会把用例拖成半分钟；显式压到 500ms。
        await contender.ExecuteAsync($"PRAGMA busy_timeout = 500");

        await using System.Data.Common.DbTransaction transaction = await holder.BeginTransactionAsync();
        await holder.InsertAsync(new FaultInjectionRow { Id = 1, Name = "a", Amount = 1 });

        SqliteException? thrown = await Assert.ThrowsAsync<SqliteException>(async () =>
            await contender.InsertAsync(new FaultInjectionRow { Id = 2, Name = "b", Amount = 2 }));
        await Assert.That(thrown).IsNotNull();
        // 共享缓存（Cache=Shared）下另一条连接的写锁表现为 SQLITE_LOCKED(6)，
        // 独立库文件下是 SQLITE_BUSY(5)；两者都在 Provider 的 IsTransient 名单里。
        await Assert.That(thrown!.SqliteErrorCode == 5 || thrown.SqliteErrorCode == 6).IsTrue()
            .Because($"实际 code={thrown.SqliteErrorCode} msg={thrown.Message}");
        await Assert.That(SqliteProvider.IsTransient(thrown)).IsTrue();

        await transaction.CommitAsync();   // 锁释放

        await contender.InsertAsync(new FaultInjectionRow { Id = 2, Name = "b", Amount = 2 });
        await Assert.That(await contender.ScalarAsync<long>($"SELECT COUNT(*) FROM palorm_fault_row")).IsEqualTo(2);
    }

    [Test]
    public async Task ContendedWrite_WithRetry_RecoversWithoutCallerRetry()
    {
        // 同一争用场景，持有方事务保持打开；争用方开 WithRetry。若 Provider 的 IsTransient
        // 判定或重试退避有问题，这里会超时失败，而不是靠调用方手工重试。
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("fault_sqlite_lock_retry");
        await using DataSession<SqliteProvider> holder = await shared.OpenSessionAsync();
        await holder.MigrateAsync();
        await using DataSession<SqliteProvider> contender = await shared.OpenSessionAsync();

        await using System.Data.Common.DbTransaction transaction = await holder.BeginTransactionAsync();
        await holder.InsertAsync(new FaultInjectionRow { Id = 1, Name = "a", Amount = 1 });

        DataSession<SqliteProvider> retrying = contender.WithRetry(
            maxRetries: 6, attempt => TimeSpan.FromMilliseconds(150 * (attempt + 1)));

        // 持有方在后台延迟提交，给重试留出窗口：前几次尝试会撞 BUSY，重试后才成功。
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            await transaction.CommitAsync();
        });

        await retrying.InsertAsync(new FaultInjectionRow { Id = 2, Name = "b", Amount = 2 });
        await Assert.That(await contender.ScalarAsync<long>($"SELECT COUNT(*) FROM palorm_fault_row")).IsEqualTo(2);
    }
}
