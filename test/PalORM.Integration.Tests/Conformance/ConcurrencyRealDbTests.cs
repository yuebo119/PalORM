using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using MySqlConnector;
using Npgsql;
using PalORM.Integration.Tests.FaultInjection;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L2 真库时序（修订版方案 §5 CS-04/CS-08/CS-09/CS-24）。
/// <para><b>为什么必须真库</b>：咨询锁、SERIALIZABLE 的 SSI 中止、InnoDB 死锁检测、并发 DDL
/// 都是服务端语义，SQLite/内存形态复现不出来（<c>SqlState</c> 与错误码在客户端无对应物）。</para>
/// <para><b>并发边界</b>：CS-04/CS-09 用多条真实连接而非多进程。PG 咨询锁与 InnoDB 行锁都是
/// <b>服务端会话级</b>资源，服务端只看到会话，客户端是否同进程不参与判定；因此多连接与多进程
/// 在锁语义上等价。真正需要多进程的只剩 SQLite 文件库（CS-23），其文件锁在进程内多连接同样生效。</para>
/// <para><b>判据口径</b>：全部是具体错误码、行数或值，不用实际耗时；硬超时只作防挂死兜底（T15）。
/// 每个用例 <c>try/finally</c> 清表（T6），统一挂 <c>ExtBulkTable</c> 串行键（T17/B63）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class ConcurrencyRealDbTests
{
    private const long BaseRowId = 9_300_000;

    /// <summary>固定锁序模式（真=反向、假=同向）。用编译期常量而非 <c>Random</c>：本项只需要
    /// 「反向轮必然死锁、同向轮必然不死锁」的可复现对照，随机数既不增加判别力，又会触发
    /// CA5394/S2245（非安全随机）——种子固定时它与常量表等价，但多一层分析器噪音。</summary>
    private static readonly bool[] LockOrderPattern = [true, false, true, true, false, true];

    private static DbOptions PgOpts
        => new() { ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString() };

    private static DbOptions MySqlOpts
        => new() { ConnectionString = TestEnvironment.ResolveMySqlConnectionString() };

    private static async Task PrepareRowsAsync<TProvider>(DbOptions options, params long[] ids)
        where TProvider : IDbProvider
    {
        await using DataSession<TProvider> db = await DataSession<TProvider>.CreateAsync(options);
        await db.MigrateAsync();
        await db.ExecuteAsync($"DELETE FROM palorm_fault_row");
        var rows = new List<FaultInjectionRow>(ids.Length);
        foreach (long id in ids)
            rows.Add(new FaultInjectionRow { Id = id, Name = "seed-" + id, Amount = 50 });
        await db.BulkInsertAsync(rows);
    }

    private static async Task CleanupAsync<TProvider>(DbOptions options) where TProvider : IDbProvider
    {
        await using DataSession<TProvider> db = await DataSession<TProvider>.CreateAsync(options);
        await db.ExecuteAsync($"DELETE FROM palorm_fault_row");
    }

    // ── CS-04：PG 咨询锁反向加锁（服务端会话级锁） ─────────────────────────

    /// <summary>CS-04：两个真实连接按相反顺序请求 <c>pg_advisory_xact_lock</c>，至少一方收 40P01；
    /// 同向锁序对照组零死锁。判据是错误码计数与提交结果，不靠耗时。
    /// <para>为什么用 xact 锁而不是 session 锁：xact 锁随事务结束自动释放，失败路径不需要
    /// 额外 RELEASE 清理；测试结束时连接关闭也会释放。</para></summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task CS04_PostgreSql_AdvisoryLockReverseOrder_Deadlocks_SameOrderSucceeds()
    {
        const long LockA = 9_304_001;
        const long LockB = 9_304_002;
        DbOptions options = PgOpts;

        (int deadlocks, int successes) = await RunAdvisoryReverseAsync(options, LockA, LockB);
        await Assert.That(deadlocks).IsEqualTo(1);
        await Assert.That(successes).IsEqualTo(1);

        (int sameDeadlocks, int sameSuccesses) = await RunAdvisorySameOrderAsync(options, LockA, LockB);
        await Assert.That(sameDeadlocks).IsEqualTo(0);
        await Assert.That(sameSuccesses).IsEqualTo(2);
    }

    private static async Task<(int Deadlocks, int Successes)> RunAdvisoryReverseAsync(
        DbOptions options, long lockA, long lockB)
    {
        await using DataSession<PostgreSqlProvider> first =
            await DataSession<PostgreSqlProvider>.CreateAsync(options);
        await using DataSession<PostgreSqlProvider> second =
            await DataSession<PostgreSqlProvider>.CreateAsync(options);

        var bothLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int locked = 0;

        async Task<Exception?> HoldAsync(DataSession<PostgreSqlProvider> session, long firstKey, long secondKey)
        {
            try
            {
                await using DbTransaction transaction = await session.BeginTransactionAsync();
                await session.ExecuteAsync($"SELECT pg_advisory_xact_lock({firstKey})");
                if (Interlocked.Increment(ref locked) == 2) bothLocked.SetResult();
                await bothLocked.Task;
                await session.ExecuteAsync($"SELECT pg_advisory_xact_lock({secondKey})");
                await transaction.CommitAsync();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        Task<Exception?> firstTask = Task.Run(() => HoldAsync(first, lockA, lockB));
        Task<Exception?> secondTask = Task.Run(() => HoldAsync(second, lockB, lockA));
        Exception?[] results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(60));

        Exception? unexpected = results.FirstOrDefault(
            result => result is not null && !IsPgDeadlock(result));
        if (unexpected is not null) throw unexpected;
        return (results.Count(IsPgDeadlock), results.Count(result => result is null));
    }

    private static async Task<(int Deadlocks, int Successes)> RunAdvisorySameOrderAsync(
        DbOptions options, long lockA, long lockB)
    {
        await using DataSession<PostgreSqlProvider> first =
            await DataSession<PostgreSqlProvider>.CreateAsync(options);
        await using DataSession<PostgreSqlProvider> second =
            await DataSession<PostgreSqlProvider>.CreateAsync(options);

        var firstLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> FirstAsync()
        {
            try
            {
                await using DbTransaction transaction = await first.BeginTransactionAsync();
                await first.ExecuteAsync($"SELECT pg_advisory_xact_lock({lockA})");
                firstLocked.SetResult();
                await first.ExecuteAsync($"SELECT pg_advisory_xact_lock({lockB})");
                await transaction.CommitAsync();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        async Task<Exception?> SecondAsync()
        {
            try
            {
                await firstLocked.Task;
                await using DbTransaction transaction = await second.BeginTransactionAsync();
                // 同向：也先请求 A，必须等第一会话提交后拿到
                await second.ExecuteAsync($"SELECT pg_advisory_xact_lock({lockA})");
                await second.ExecuteAsync($"SELECT pg_advisory_xact_lock({lockB})");
                await transaction.CommitAsync();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        Task<Exception?> firstTask = Task.Run(FirstAsync);
        Task<Exception?> secondTask = Task.Run(SecondAsync);
        Exception?[] results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(60));

        Exception? unexpected = results.FirstOrDefault(result => result is not null);
        if (unexpected is not null) throw unexpected;
        return (results.Count(IsPgDeadlock), results.Count(result => result is null));
    }

    private static bool IsPgDeadlock(Exception? exception)
        => exception is PostgresException { SqlState: "40P01" }
           || exception?.InnerException is PostgresException { SqlState: "40P01" };

    // ── CS-08：幻读 / 写偏斜 ────────────────────────────────────────────

    /// <summary>CS-08（PG）：SERIALIZABLE 下两个事务各读同一范围再各插入一行（写偏斜），
    /// SSI 必须中止一方（40001），重放后行数符合业务约束。判据是错误码计数与最终行数。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task CS08_PostgreSql_Serializable_WriteSkew_AbortsOneAndReplaySucceeds()
    {
        DbOptions options = PgOpts;
        await PrepareRowsAsync<PostgreSqlProvider>(options, BaseRowId + 1);
        try
        {
            await using DataSession<PostgreSqlProvider> first =
                (await DataSession<PostgreSqlProvider>.CreateAsync(options))
                .WithIsolationLevel(IsolationLevel.Serializable);
            await using DataSession<PostgreSqlProvider> second =
                (await DataSession<PostgreSqlProvider>.CreateAsync(options))
                .WithIsolationLevel(IsolationLevel.Serializable);

            var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Exception?> RunAsync(
                DataSession<PostgreSqlProvider> session, long newId, TaskCompletionSource own, TaskCompletionSource other)
            {
                try
                {
                    await using DbTransaction transaction = await session.BeginTransactionAsync();
                    // 两事务读同一谓词（amount 0..100），随后各写一行进该范围
                    long count = await session.ScalarAsync<long>(
                        $"SELECT COUNT(*) FROM palorm_fault_row WHERE amount BETWEEN {0L} AND {100L}");
                    if (count != 1L)
                        return new InvalidOperationException($"种子行数 {count}，期望 1");
                    own.SetResult();
                    await other.Task;
                    await session.InsertAsync(new FaultInjectionRow { Id = newId, Name = "skew", Amount = 60 });
                    await transaction.CommitAsync();
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }

            Task<Exception?> firstTask = Task.Run(() => RunAsync(first, BaseRowId + 10, firstRead, secondRead));
            Task<Exception?> secondTask = Task.Run(() => RunAsync(second, BaseRowId + 11, secondRead, firstRead));
            Exception?[] results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(60));

            int serializationFailures = results.Count(IsPgSerializationFailure);
            await Assert.That(serializationFailures).IsEqualTo(1);

            Exception? unexpected = results.FirstOrDefault(
                result => result is not null && !IsPgSerializationFailure(result));
            await Assert.That(unexpected).IsNull();

            // 重放被中止的那一方：业务约束是「范围内恰好 3 行」（种子 1 + 两笔各 1）
            await using DataSession<PostgreSqlProvider> replay =
                await DataSession<PostgreSqlProvider>.CreateAsync(options);
            await replay.InsertAsync(new FaultInjectionRow
            {
                Id = results[0] is null ? BaseRowId + 11 : BaseRowId + 10,
                Name = "skew-replay",
                Amount = 60,
            });
            long total = await replay.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM palorm_fault_row WHERE amount BETWEEN {0L} AND {100L}");
            await Assert.That(total).IsEqualTo(3L);
        }
        finally
        {
            await CleanupAsync<PostgreSqlProvider>(options);
        }
    }

    private static bool IsPgSerializationFailure(Exception? exception)
        => exception is PostgresException { SqlState: "40001" }
           || exception?.InnerException is PostgresException { SqlState: "40001" };

    /// <summary>主键范围谓词：列名是 SQL 结构位置，必须先拼纯文本再交给
    /// <see cref="System.Runtime.CompilerServices.FormattableStringFactory"/>，否则列名会被参数化；
    /// 且 PG 对未加引号的标识符折叠为小写，表列是 <c>"Id"</c>，必须带引号（同 TransactionIsolationTests）。</summary>
    private static FormattableString IdIn(SqlDialect dialect, long first, long second)
        => System.Runtime.CompilerServices.FormattableStringFactory.Create(
            dialect == SqlDialect.MySql ? "`Id` IN ({0}, {1})" : "\"Id\" IN ({0}, {1})", first, second);

    private static FormattableString IdAtLeast(SqlDialect dialect, long minimum)
        => System.Runtime.CompilerServices.FormattableStringFactory.Create(
            dialect == SqlDialect.MySql ? "`Id` >= {0}" : "\"Id\" >= {0}", minimum);

    /// <summary>CS-08（MySQL）：REPEATABLE READ 的幻读基线——事务内两次读同一范围，中间另一会话
    /// 插入并提交；RR 的一致性快照应使第二次读看不到新行。这是方言语义基线（P2），不是缺陷断言。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task CS08_MySql_RepeatableRead_DoesNotSeeConcurrentInsert()
    {
        DbOptions options = MySqlOpts;
        await PrepareRowsAsync<MySqlProvider>(options, BaseRowId + 21);
        try
        {
            await using DataSession<MySqlProvider> reader =
                (await DataSession<MySqlProvider>.CreateAsync(options))
                .WithIsolationLevel(IsolationLevel.RepeatableRead);

            var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<long> readTask = Task.Run(async () =>
            {
                await using DbTransaction transaction = await reader.BeginTransactionAsync();
                long before = await reader.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM palorm_fault_row WHERE amount BETWEEN {0L} AND {100L}");
                firstRead.SetResult();
                await writerDone.Task;
                long after = await reader.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM palorm_fault_row WHERE amount BETWEEN {0L} AND {100L}");
                await transaction.CommitAsync();
                return after - before;
            });

            await firstRead.Task;
            await using (DataSession<MySqlProvider> writer =
                await DataSession<MySqlProvider>.CreateAsync(options))
            {
                await writer.InsertAsync(new FaultInjectionRow
                {
                    Id = BaseRowId + 22, Name = "phantom", Amount = 60,
                });
            }
            writerDone.SetResult();

            long delta = await readTask.WaitAsync(TimeSpan.FromSeconds(60));
            // RR 快照：第二次读不应看到并发提交的新行
            await Assert.That(delta).IsEqualTo(0L);
        }
        finally
        {
            await CleanupAsync<MySqlProvider>(options);
        }
    }

    // ── CS-09：死锁压力 / 随机锁序 ─────────────────────────────────────

    /// <summary>CS-09（PG）：按种子随机决定每轮两行的加锁顺序，反向轮必须产生 40P01，
    /// 同向轮必须零死锁；每轮结束后行值可逐值核对。判据是「死锁数 == 反向轮数」，
    /// 不是耗时或比率。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task CS09_PostgreSql_RandomLockOrder_DeadlocksOnlyOnReverseRounds()
    {
        DbOptions options = PgOpts;
        long rowA = BaseRowId + 31;
        long rowB = BaseRowId + 32;
        await PrepareRowsAsync<PostgreSqlProvider>(options, rowA, rowB);
        try
        {
            int reverseRounds = 0;
            int deadlocks = 0;
            for (int round = 0; round < LockOrderPattern.Length; round++)
            {
                bool reverse = LockOrderPattern[round];
                if (reverse) reverseRounds++;
                (int roundDeadlocks, int roundSuccesses) = await RunRowLockPairAsync<PostgreSqlProvider>(
                    options, rowA, rowB, reverse, round + 1, IsPgDeadlock);
                deadlocks += roundDeadlocks;
                await Assert.That(roundSuccesses + roundDeadlocks).IsEqualTo(2);
            }

            await Assert.That(deadlocks).IsEqualTo(reverseRounds);

            await using DataSession<PostgreSqlProvider> verify =
                await DataSession<PostgreSqlProvider>.CreateAsync(options);
            List<FaultInjectionRow> rows = await verify
                .From<FaultInjectionRow>()
                .Where(IdIn(SqlDialect.PostgreSql, rowA, rowB))
                .ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(2);
            // 死锁回滚不得留下半写：每行最终值必是某一轮成功方写入的 1..6
            await Assert.That(rows.Select(row => row.Amount).All(amount => amount is >= 1L and <= 6L)).IsTrue();
        }
        finally
        {
            await CleanupAsync<PostgreSqlProvider>(options);
        }
    }

    /// <summary>CS-09（MySQL）：与 PG 同构，死锁错误码换成 1213。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task CS09_MySql_RandomLockOrder_DeadlocksOnlyOnReverseRounds()
    {
        DbOptions options = MySqlOpts;
        long rowA = BaseRowId + 41;
        long rowB = BaseRowId + 42;
        await PrepareRowsAsync<MySqlProvider>(options, rowA, rowB);
        try
        {
            int reverseRounds = 0;
            int deadlocks = 0;
            for (int round = 0; round < LockOrderPattern.Length; round++)
            {
                bool reverse = LockOrderPattern[round];
                if (reverse) reverseRounds++;
                (int roundDeadlocks, int roundSuccesses) = await RunRowLockPairAsync<MySqlProvider>(
                    options, rowA, rowB, reverse, round + 1, IsMySqlDeadlock);
                deadlocks += roundDeadlocks;
                await Assert.That(roundSuccesses + roundDeadlocks).IsEqualTo(2);
            }

            await Assert.That(deadlocks).IsEqualTo(reverseRounds);

            await using DataSession<MySqlProvider> verify =
                await DataSession<MySqlProvider>.CreateAsync(options);
            List<FaultInjectionRow> rows = await verify
                .From<FaultInjectionRow>()
                .Where(IdIn(SqlDialect.MySql, rowA, rowB))
                .ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(2);
            await Assert.That(rows.Select(row => row.Amount).All(amount => amount is >= 1L and <= 6L)).IsTrue();
        }
        finally
        {
            await CleanupAsync<MySqlProvider>(options);
        }
    }

    private static bool IsMySqlDeadlock(Exception? exception)
        => exception is MySqlException { Number: 1213 }
           || exception?.InnerException is MySqlException { Number: 1213 };

    /// <summary>两会话各持一行再请求对方行。反向 = 死锁，同向 = 串行等待后都成功。
    /// 返回（死锁数，成功数）；非目标异常直接抛给用例（不吞）。</summary>
    private static async Task<(int Deadlocks, int Successes)> RunRowLockPairAsync<TProvider>(
        DbOptions options, long rowA, long rowB, bool reverse, int amount, Func<Exception?, bool> isDeadlock)
        where TProvider : IDbProvider
    {
        await using DataSession<TProvider> first = await DataSession<TProvider>.CreateAsync(options);
        await using DataSession<TProvider> second = await DataSession<TProvider>.CreateAsync(options);

        var firstLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> RunAsync(
            DataSession<TProvider> session, long firstRow, long secondRow,
            TaskCompletionSource own, TaskCompletionSource other)
        {
            try
            {
                await using DbTransaction transaction = await session.BeginTransactionAsync();
                await session.UpdateAsync(new FaultInjectionRow { Id = firstRow, Name = "locked", Amount = amount });
                // 只有反向锁序需要「两边都持锁后再交叉请求」的同步；同向锁序的第二会话会直接
                // 阻塞在第一行的锁上，此时若还等对方的信号，两侧互等会把测试自己拖死
                // （首轮实测：同向轮 30 秒命令超时）。
                if (reverse)
                {
                    own.SetResult();
                    await other.Task;
                }
                await session.UpdateAsync(new FaultInjectionRow { Id = secondRow, Name = "locked", Amount = amount });
                await transaction.CommitAsync();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        // 反向：A→B 与 B→A 形成环；同向：两边都 A→B，第二会话串行等待第一会话提交
        Task<Exception?> firstTask = Task.Run(() => RunAsync(first, rowA, rowB, firstLocked, secondLocked));
        Task<Exception?> secondTask = Task.Run(() => RunAsync(
            second, reverse ? rowB : rowA, reverse ? rowA : rowB, secondLocked, firstLocked));
        Exception?[] results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(60));

        Exception? unexpected = results.FirstOrDefault(
            result => result is not null && !isDeadlock(result));
        if (unexpected is not null) throw unexpected;

        return (results.Count(isDeadlock), results.Count(result => result is null));
    }

    // ── CS-24：DDL × DML 并发 ──────────────────────────────────────────

    /// <summary>CS-24：一边并发重复 <c>MigrateAsync</c>（幂等 DDL），一边并发 CRUD。
    /// 补既有 <c>MigrationRaceHarnessTests</c> 的「零异常」弱断言：本例核对表结构、行数
    /// 与每行值。用独立数据库隔离，<c>finally</c> 强制 DROP（T6/T17）。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task CS24_PostgreSql_DdlRacingDml_StructureAndRowsAreCorrect()
    {
        const string raceDb = "palorm_cs24_race";
        string baseCs = TestEnvironment.ResolvePostgreSqlConnectionString();
        string raceCs = ReplaceDatabase(baseCs, raceDb);
        var raceOptions = new DbOptions { ConnectionString = raceCs };

        await using (var admin = await DataSession<PostgreSqlProvider>.CreateAsync(
            new DbOptions { ConnectionString = baseCs }))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS palorm_cs24_race WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE palorm_cs24_race");
        }

        try
        {
            // 先建一次表，让 DML 侧有目标表；随后 DDL 与 DML 才构成真并发
            await using (var setup = await DataSession<PostgreSqlProvider>.CreateAsync(raceOptions))
            {
                await setup.MigrateAsync();
                await setup.ExecuteAsync($"DELETE FROM palorm_fault_row");
                await setup.InsertAsync(new FaultInjectionRow
                {
                    Id = BaseRowId + 51, Name = "seed", Amount = 1,
                });
            }

            var errors = new ConcurrentBag<string>();
            var writes = new ConcurrentBag<long>();
            var tasks = new List<Task>();

            for (int worker = 0; worker < 3; worker++)
            {
                int captured = worker;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(raceOptions);
                        await db.MigrateAsync();
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"migrate-{captured}: {exception.GetType().Name}: {exception.Message.Split('\n')[0]}");
                    }
                }));
            }

            for (int worker = 0; worker < 3; worker++)
            {
                int captured = worker;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(raceOptions);
                        // 一次批量写 10 行（逐行 INSERT 在循环里会被 PALORM005 判为 N+1；本项要的是
                        // 「DDL 与 DML 并发」，批量写同样是 DML，且更接近真实负载）
                        var batch = new List<FaultInjectionRow>(10);
                        for (int i = 0; i < 10; i++)
                        {
                            long id = BaseRowId + 100 + captured * 10 + i;
                            batch.Add(new FaultInjectionRow
                            {
                                Id = id, Name = $"dml-{captured}-{i}", Amount = captured * 10 + i,
                            });
                            writes.Add(id);
                        }
                        await db.BulkInsertAsync(batch);
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"dml-{captured}: {exception.GetType().Name}: {exception.Message.Split('\n')[0]}");
                    }
                }));
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(120));
            await Assert.That(string.Join(" | ", errors.Take(5))).IsEmpty();

            await using var verify = await DataSession<PostgreSqlProvider>.CreateAsync(raceOptions);
            long count = await verify.ScalarAsync<long>($"SELECT COUNT(*) FROM palorm_fault_row");
            await Assert.That(count).IsEqualTo(1 + writes.Count);

            // 表结构：三列都在（DDL 并发不得产生半建表）。走裸 ADO 读系统目录，
            // 不复用产品元数据——两侧同错时判据会失去判别力。
            DbConnection raw = verify.GetRawConnection();
            if (raw.State != ConnectionState.Open) await raw.OpenAsync();
            var columnSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (DbCommand command = raw.CreateCommand())
            {
                command.CommandText =
                    "SELECT column_name FROM information_schema.columns WHERE table_name = 'palorm_fault_row'";
                await using DbDataReader reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    columnSet.Add(reader.GetString(0));
            }
            await Assert.That(columnSet.Contains("Id")).IsTrue();
            await Assert.That(columnSet.Contains("name")).IsTrue();
            await Assert.That(columnSet.Contains("amount")).IsTrue();

            // 逐值核对：每笔 DML 写入的行必须存在且 name/amount 对得上
            List<FaultInjectionRow> rows = await verify
                .From<FaultInjectionRow>()
                .Where(IdAtLeast(SqlDialect.PostgreSql, BaseRowId + 100L))
                .ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(writes.Count);
            List<FaultInjectionRow> mismatches = rows.Where(row =>
            {
                long offset = row.Id - BaseRowId - 100;
                return row.Name != $"dml-{offset / 10}-{offset % 10}" || row.Amount != offset;
            }).ToList();
            await Assert.That(mismatches.Count).IsEqualTo(0);
        }
        finally
        {
            await using var cleanup = await DataSession<PostgreSqlProvider>.CreateAsync(
                new DbOptions { ConnectionString = baseCs });
            await cleanup.ExecuteAsync($"DROP DATABASE IF EXISTS palorm_cs24_race WITH (FORCE)");
        }
    }

    /// <summary>替换连接串的 Database 键（不存在则追加）——装置用库与主库同实例。</summary>
    private static string ReplaceDatabase(string connectionString, string databaseName)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(
            connectionString, @"Database=[^;]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return System.Text.RegularExpressions.Regex.Replace(
                connectionString, @"Database=[^;]+", $"Database={databaseName}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return $"{connectionString};Database={databaseName}";
    }
}
