using System.Data;
using System.Data.Common;
using MySqlConnector;
using Npgsql;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

// P1-A（方案 §6.4 / TX-1..4）：事务隔离、死锁与重放、锁竞争、aborted transaction 恢复。
// 全部用 TaskCompletionSource 与显式顺序控制两会话，不用 sleep 猜时序；无实际耗时断言。
// SERIALIZABLE 在 MySQL 的语义与 PG 不同（共享锁阻塞而非 SSI 中止），按方言分别断言并登记。

[NotInParallel("ExtBulkTable")]
public sealed class TransactionIsolationTests
{
    /// <summary>主键谓词：列名是 SQL 结构位置，必须先拼纯文本再交给 FormattableStringFactory，
    /// 否则列名会被参数化成 @p0 = 1（ITM 注释同源）。</summary>
    private static FormattableString KeyEquals(SqlDialect dialect, long id)
        => System.Runtime.CompilerServices.FormattableStringFactory.Create(
            dialect == SqlDialect.MySql ? "`Id` = {0}" : "\"Id\" = {0}", id);

    private static SqlDialect DialectOf<TProvider>() where TProvider : IDbProvider => TProvider.Dialect;

    private static async ValueTask PrepareAsync<TProvider>(Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        await using DataSession<TProvider> db = await factory();
        await db.MigrateAsync();
        await db.ExecuteAsync($"DELETE FROM ti_lock_probe");
        await db.InsertAsync(new TiLockEntity { Id = 1, Name = "v0", Amount = 0 });
        await db.InsertAsync(new TiLockEntity { Id = 2, Name = "v0", Amount = 0 });
    }

    // ── TX-1：隔离语义 ──────────────────────────────────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_ReadCommitted_SeesConcurrentCommit()
    {
        await PrepareAsync(static () => TestDb.PostgreSqlAsync());
        await ReadCommittedAsync(static () => TestDb.PostgreSqlAsync());
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_ReadCommitted_SeesConcurrentCommit()
    {
        await PrepareAsync(static () => TestDb.MySqlAsync());
        await ReadCommittedAsync(static () => TestDb.MySqlAsync());
    }

    private static async Task ReadCommittedAsync<TProvider>(Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        await using DataSession<TProvider> reader = await factory();
        reader.WithIsolationLevel(IsolationLevel.ReadCommitted);

        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task readTask = reader.WithTransaction(async ct =>
        {
            TiLockEntity before = (await reader.GetAsync<TiLockEntity>(1L, ct))!;
            await Assert.That(before.Name).IsEqualTo("v0");
            written.SetResult();
            await committed.Task.WaitAsync(ct);
            TiLockEntity after = (await reader.GetAsync<TiLockEntity>(1L, ct))!;
            // READ COMMITTED：同一事务内第二次读看到 B 已提交的新值
            await Assert.That(after.Name).IsEqualTo("v1");
        }).AsTask();

        await written.Task;
        await using (DataSession<TProvider> writer = await factory())
        {
            await writer.UpdateAsync(new TiLockEntity { Id = 1, Name = "v1", Amount = 0 });
        }
        committed.SetResult();
        await readTask;
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_RepeatableRead_DoesNotSeeConcurrentCommit()
        => await RepeatableReadAsync(static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_RepeatableRead_DoesNotSeeConcurrentCommit()
        => await RepeatableReadAsync(static () => TestDb.MySqlAsync());

    private static async Task RepeatableReadAsync<TProvider>(Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        await PrepareAsync(factory);
        await using DataSession<TProvider> reader = await factory();
        reader.WithIsolationLevel(IsolationLevel.RepeatableRead);

        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task readTask = reader.WithTransaction(async ct =>
        {
            await Assert.That((await reader.GetAsync<TiLockEntity>(1L, ct))!.Name).IsEqualTo("v0");
            read.SetResult();
            await committed.Task.WaitAsync(ct);
            // REPEATABLE READ：同一事务内第二次读看不到 B 已提交的新值
            await Assert.That((await reader.GetAsync<TiLockEntity>(1L, ct))!.Name).IsEqualTo("v0");
        }).AsTask();

        await read.Task;
        await using (DataSession<TProvider> writer = await factory())
        {
            await writer.UpdateAsync(new TiLockEntity { Id = 2, Name = "v2", Amount = 0 });
        }
        committed.SetResult();
        await readTask;
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_Serializable_ConflictsAndRetryReplays()
        => await SerializablePgAsync();

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_Serializable_BlocksWriterThenRetrySucceeds()
        => await SerializableMySqlAsync();

    /// <summary>PG SERIALIZABLE（SSI）：两边读后写，一方被中止（40001），重放成功。</summary>
    private static async Task SerializablePgAsync()
    {
        await PrepareAsync(static () => TestDb.PostgreSqlAsync());

        await using DataSession<PostgreSqlProvider> left = await TestDb.PostgreSqlAsync();
        left.WithIsolationLevel(IsolationLevel.Serializable);

        var leftRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? abort = null;

        Task leftTask = Task.Run(async () =>
        {
            try
            {
                await left.WithTransaction(async ct =>
                {
                    await left.GetAsync<TiLockEntity>(2L, ct);
                    leftRead.SetResult();
                    await rightCommitted.Task.WaitAsync(ct);
                    await left.UpdateAsync(new TiLockEntity { Id = 1, Name = "left", Amount = 0 }, ct);
                });
            }
            catch (PostgresException ex)
            {
                abort = ex;
            }
        });

        await leftRead.Task;
        Exception? rightAbort = null;
        await using (DataSession<PostgreSqlProvider> right = await TestDb.PostgreSqlAsync())
        {
            right.WithIsolationLevel(IsolationLevel.Serializable);
            try
            {
                await right.WithTransaction(async ct =>
                {
                    await right.GetAsync<TiLockEntity>(1L, ct);
                    rightCommitted.SetResult();
                    await right.UpdateAsync(new TiLockEntity { Id = 2, Name = "right", Amount = 0 }, ct);
                });
            }
            catch (PostgresException ex)
            {
                rightAbort = ex;
            }
        }
        await leftTask;

        // SSI 可能中止一方或双方：断言"至少一方以 40001 中止"，两方形态都合法
        bool leftAborted = abort is PostgresException { SqlState: "40001" };
        bool rightAborted = rightAbort is PostgresException { SqlState: "40001" };
        await Assert.That(leftAborted || rightAborted).IsTrue()
            .Because($"SSI 冲突应至少一方 40001；left={abort?.Message ?? "正常"} right={rightAbort?.Message ?? "正常"}");

        // 重放被中止的事务：串行执行后不再冲突
        if (leftAborted)
        {
            await left.WithTransactionRetry(async ct =>
            {
                await left.GetAsync<TiLockEntity>(2L, ct);
                await left.UpdateAsync(new TiLockEntity { Id = 1, Name = "left", Amount = 0 }, ct);
            });
        }
        // 左侧未中止时它自己写 "left"；中止时重放也写 "left"——两种形态下行 1 恒为 "left"
        await Assert.That((await left.GetAsync<TiLockEntity>(1L))!.Name).IsEqualTo("left");

        if (rightAborted)
        {
            await using DataSession<PostgreSqlProvider> replay = await TestDb.PostgreSqlAsync();
            replay.WithIsolationLevel(IsolationLevel.Serializable);
            await replay.WithTransactionRetry(async ct =>
            {
                await replay.GetAsync<TiLockEntity>(1L, ct);
                await replay.UpdateAsync(new TiLockEntity { Id = 2, Name = "right", Amount = 0 }, ct);
            });
            await Assert.That((await replay.GetAsync<TiLockEntity>(2L))!.Name).IsEqualTo("right");
        }
    }

    /// <summary>MySQL SERIALIZABLE：读加共享锁，B 的写被阻塞到锁等待超时（1205），A 提交后重放成功。</summary>
    private static async Task SerializableMySqlAsync()
    {
        await PrepareAsync(static () => TestDb.MySqlAsync());

        await using DataSession<MySqlProvider> left = await TestDb.MySqlAsync();
        left.WithIsolationLevel(IsolationLevel.Serializable);

        var leftRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? blocked = null;

        Task leftTask = Task.Run(async () =>
        {
            await left.WithTransaction(async ct =>
            {
                await left.GetAsync<TiLockEntity>(1L, ct);
                leftRead.SetResult();
                await writerFailed.Task.WaitAsync(ct);
            });
        });

        await leftRead.Task;
        await using (DataSession<MySqlProvider> right = await TestDb.MySqlAsync())
        {
            // 锁等待 1 秒：保证"被阻塞"可观察，同时不让用例无限挂起
            await right.ExecuteAsync($"SET SESSION innodb_lock_wait_timeout = 1");
            try
            {
                await right.UpdateAsync(new TiLockEntity { Id = 1, Name = "right", Amount = 0 });
            }
            catch (MySqlException ex)
            {
                blocked = ex;
            }
        }
        writerFailed.SetResult();
        await leftTask;

        await Assert.That(blocked).IsNotNull();
        await Assert.That(blocked is MySqlException { Number: 1205 }).IsTrue();

        // A 已提交；重放 B 的写成功
        await using (DataSession<MySqlProvider> retry = await TestDb.MySqlAsync())
        {
            await retry.UpdateAsync(new TiLockEntity { Id = 1, Name = "right", Amount = 0 });
            await Assert.That((await retry.GetAsync<TiLockEntity>(1L))!.Name).IsEqualTo("right");
        }
    }

    // ── TX-2：死锁与重放 ───────────────────────────────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_Deadlock_ClassifiedAndReplayedOnce()
        => await DeadlockAsync("postgresql", static () => TestDb.PostgreSqlAsync(), "40P01");

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_Deadlock_ClassifiedAndReplayedOnce()
        => await DeadlockAsync("mysql", static () => TestDb.MySqlAsync(), "1213");

    private static async Task DeadlockAsync<TProvider>(
        string name, Func<Task<DataSession<TProvider>>> factory, string expectedCode)
        where TProvider : IDbProvider
    {
        await PrepareAsync(factory);
        await using DataSession<TProvider> left = await factory();
        await using DataSession<TProvider> right = await factory();
        left.WithTimeout(TimeSpan.FromSeconds(5));
        right.WithTimeout(TimeSpan.FromSeconds(5));

        var leftLockedRowOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightLockedRowTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? deadlock = null;

        // 反向加锁：A 锁 1 再锁 2；B 锁 2 再锁 1
        Task leftTask = Task.Run(async () =>
        {
            try
            {
                await left.WithTransaction(async ct =>
                {
                    await left.From<TiLockEntity>().Where(KeyEquals(DialectOf<TProvider>(), 1)).ForUpdate().ToListAsync(ct);
                    leftLockedRowOne.SetResult();
                    await rightLockedRowTwo.Task.WaitAsync(ct);
                    await left.From<TiLockEntity>().Where(KeyEquals(DialectOf<TProvider>(), 2)).ForUpdate().ToListAsync(ct);
                });
            }
            catch (Exception ex)
            {
                deadlock = ex;
            }
        });

        Task rightTask = Task.Run(async () =>
        {
            try
            {
                await right.WithTransaction(async ct =>
                {
                    await right.From<TiLockEntity>().Where(KeyEquals(DialectOf<TProvider>(), 2)).ForUpdate().ToListAsync(ct);
                    rightLockedRowTwo.SetResult();
                    await leftLockedRowOne.Task.WaitAsync(ct);
                    // 这一步与 A 的第二步互为死锁：一方被选为牺牲者
                    await right.From<TiLockEntity>().Where(KeyEquals(DialectOf<TProvider>(), 1)).ForUpdate().ToListAsync(ct);
                });
            }
            catch (Exception ex)
            {
                deadlock ??= ex;
            }
        });

        await Task.WhenAll(leftTask, rightTask);

        await Assert.That(deadlock).IsNotNull();
        bool matched = deadlock switch
        {
            PostgresException pg => pg.SqlState == "40P01",
            MySqlException my => my.Number == 1213,
            _ => false
        };
        await Assert.That(matched).IsTrue()
            .Because($"{name} 死锁分类应为 {expectedCode}，实际 {deadlock!.GetType().Name}: {deadlock.Message}");

        // 重放：牺牲者事务整体重跑（对手已回滚），副作用只发生一次
        await left.WithTransactionRetry(async ct =>
        {
            await left.From<TiLockEntity>().Where(KeyEquals(DialectOf<TProvider>(), 1)).ForUpdate().ToListAsync(ct);
            await left.From<TiLockEntity>().Where(KeyEquals(DialectOf<TProvider>(), 2)).ForUpdate().ToListAsync(ct);
            await left.InsertAsync(new TiLockEntity { Id = 100, Name = "side-effect", Amount = 1 }, ct);
        });
        await Assert.That(await left.CountAsync<TiLockEntity>(ct: CancellationToken.None)).IsEqualTo(3L);
    }

    // ── TX-3：锁竞争 ───────────────────────────────────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_ForUpdate_BlocksThenTimesOutAndSkipLockedWorks()
        => await LockCompetitionAsync("postgresql", static () => TestDb.PostgreSqlAsync(),
            static async db => await db.ExecuteAsync($"SET LOCAL lock_timeout = '300ms'"), "55P03");

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_ForUpdate_BlocksThenTimesOutAndSkipLockedWorks()
        => await LockCompetitionAsync("mysql", static () => TestDb.MySqlAsync(),
            static async db => await db.ExecuteAsync($"SET SESSION innodb_lock_wait_timeout = 1"), "1205");

    private static async Task LockCompetitionAsync<TProvider>(
        string name,
        Func<Task<DataSession<TProvider>>> factory,
        Func<DataSession<TProvider>, Task> setLockTimeout,
        string expectedCode)
        where TProvider : IDbProvider
    {
        await PrepareAsync(factory);
        await using DataSession<TProvider> holder = await factory();
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task holdTask = Task.Run(async () =>
        {
            await holder.WithTransaction(async ct =>
            {
                await holder.From<TiLockEntity>().Where(KeyEquals(DialectOf<TProvider>(), 1)).ForUpdate().ToListAsync(ct);
                locked.SetResult();
                await release.Task.WaitAsync(ct);
            });
        });

        await locked.Task;

        // ① 第二会话被阻塞：命令超时归一为 TimeoutException
        await using (DataSession<TProvider> blocked = await factory())
        {
            blocked.WithTimeout(TimeSpan.FromMilliseconds(300));
            try
            {
                await blocked.From<TiLockEntity>().Where(KeyEquals(DialectOf<TProvider>(), 1)).ForUpdate().ToListAsync();
            }
            catch (TimeoutException)
            {
                // 预期：PalORM 把驱动超时归一为 TimeoutException
            }
        }

        // ② 方言原生锁等待超时：报错形态（PG 55P03 / MySQL 1205）
        await using (DataSession<TProvider> nativeTimeout = await factory())
        {
            DbTransaction transaction = await nativeTimeout.BeginTransactionAsync();
            await setLockTimeout(nativeTimeout);
            Exception? error = null;
            try
            {
                await nativeTimeout.From<TiLockEntity>()
                    .Where(KeyEquals(DialectOf<TProvider>(), 1)).ForUpdate().ToListAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex;
            }
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();

            bool matched = error switch
            {
                PostgresException pg => pg.SqlState == "55P03",
                MySqlException my => my.Number == 1205,
                _ => false
            };
            await Assert.That(matched).IsTrue()
                .Because($"{name} 锁等待超时应报 {expectedCode}，实际 {error!.GetType().Name}: {error.Message}");
        }

        // ③ skipLocked：跳过已锁行，只返回未锁的 Id=2
        await using (DataSession<TProvider> skipper = await factory())
        {
            await skipper.WithTransaction(async ct =>
            {
                List<TiLockEntity> rows = await skipper.From<TiLockEntity>()
                    .ForUpdate(skipLocked: true).ToListAsync(ct);
                await Assert.That(rows.Count).IsEqualTo(1);
                await Assert.That(rows[0].Id).IsEqualTo(2L);
            });
        }

        release.SetResult();
        await holdTask;
    }

    // ── TX-4：aborted transaction 恢复（PG 特有） ────────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_AbortedTransaction_RecoversAfterRollback()
    {
        await PrepareAsync(static () => TestDb.PostgreSqlAsync());
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.ExecuteAsync($"DELETE FROM ti_lock_probe");

        DbTransaction transaction = await db.BeginTransactionAsync();
        await Assert.That(db.IsInTransaction).IsTrue();

        // 语句失败 → PG 事务进入 aborted
        Exception? firstError = null;
        try
        {
            await db.ExecuteAsync($"SELECT * FROM no_such_table");
        }
        catch (PostgresException ex)
        {
            firstError = ex;
        }
        await Assert.That(firstError).IsNotNull();

        // aborted 状态下后续命令一律 25P02
        Exception? aborted = null;
        try
        {
            await db.ExecuteAsync($"SELECT 1");
        }
        catch (PostgresException ex)
        {
            aborted = ex;
        }
        await Assert.That(aborted is PostgresException { SqlState: "25P02" }).IsTrue();

        await transaction.RollbackAsync();
        await transaction.DisposeAsync();

        // 回滚后会话可用：后续写入正常
        await db.InsertAsync(new TiLockEntity { Id = 50, Name = "after-abort", Amount = 1 });
        await Assert.That((await db.GetAsync<TiLockEntity>(50L))!.Name).IsEqualTo("after-abort");
    }
}

/// <summary>事务与锁用例专用表：不与其他套件共享 `cf_*` 表，避免跨 NotInParallel 键的并发 DDL/清表干扰。</summary>
[Table("ti_lock_probe")]
public partial class TiLockEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("amount")] public long Amount { get; set; }
}
