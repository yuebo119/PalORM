using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L1 池生命周期、共享实体与读路由（修订版方案 §5 CS-12/CS-25/CS-16）。
/// <para><b>为什么可以确定性</b>：三条都用显式顺序或「租/还、读/写」的固定编排，不用随机调度；
/// 判据是具体值、异常类型与计数，不用实际耗时（T15）。</para>
/// <para><b>并行纪律</b>：CS-12 每个用例自建池与库，不触进程级缓存；CS-25/CS-16 用
/// <see cref="SharedSqliteDb"/> 的独立内存库。本类不调 <c>ConcurrencyHarness</c> 的进程级复位，
/// 但仍标 <c>[NotInParallel]</c>：池与共享库的时序断言依赖「本进程内没有别的用例同时建池」，
/// 与既有 <c>SessionConcurrencyTests</c> 同口径。</para></summary>
[NotInParallel]
public sealed class ConcurrencyPoolLifecycleTests
{
    /// <summary>CS-12：池释放与租出双向并发。判据三件：每个租借要么成功执行并归还、
    /// 要么被明确拒绝（<c>ObjectDisposedException</c>），不存在「已释放池仍租出」；
    /// 释放完成后 <c>IdleCount == 0</c>，再租必拒。</summary>
    [Test]
    public async Task CS12_PoolDisposeRacingRent_NeverRentsFromDisposedPool()
    {
        const int rounds = 20;
        const int borrowers = 8;
        int successes = 0;
        int rejections = 0;

        for (int round = 0; round < rounds; round++)
        {
            await using SharedSqliteDb shared =
                await SharedSqliteDb.CreateAsync($"cs12_{round}").ConfigureAwait(false);
            DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 4);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            var tasks = new List<Task>();

            tasks.Add(Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                try
                {
                    await pool.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add($"dispose: {exception.GetType().Name}: {exception.Message}");
                }
            }));

            for (int worker = 0; worker < borrowers; worker++)
            {
                int captured = worker;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.Task.ConfigureAwait(false);
                    await BorrowOnceAsync(
                        pool, captured, failures,
                        () => Interlocked.Increment(ref successes),
                        () => Interlocked.Increment(ref rejections)).ConfigureAwait(false);
                }));
            }

            gate.SetResult();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            await Assert.That(string.Join(" | ", failures.Take(3))).IsEmpty();
            await Assert.That(successes + rejections).IsEqualTo((round + 1) * borrowers);
            // 释放完成后不得残留空闲会话；再租必须被拒
            await Assert.That(pool.IdleCount).IsEqualTo(0);
            await Assert.That(async () => await pool.RentAsync()).Throws<ObjectDisposedException>();
        }
    }

    /// <summary>一次租借尝试：成功则执行 <c>SELECT 1</c> 并归还；池已释放视为合法拒绝；
    /// 其余异常进失败清单。抽成方法只为把用例主体的分支压到分析器阈值内。</summary>
    private static async Task BorrowOnceAsync(
        DataSessionPool<SqliteProvider> pool,
        int worker,
        System.Collections.Concurrent.ConcurrentBag<string> failures,
        Action onSuccess,
        Action onRejected)
    {
        DataSession<SqliteProvider>? session = null;
        try
        {
            session = await pool.RentAsync().ConfigureAwait(false);
            long value = await session.ScalarAsync<long>($"SELECT 1").ConfigureAwait(false);
            if (value != 1L) failures.Add($"worker-{worker}: SELECT 1 返回 {value}");
            onSuccess();
        }
        catch (ObjectDisposedException)
        {
            // 池已释放：明确拒绝是合法结果，不是失败
            onRejected();
        }
        catch (Exception exception)
        {
            failures.Add($"worker-{worker}: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>CS-25：同一实体实例被两个会话共享时，<c>[ConcurrencyCheck]</c> 的版本令牌会被
    /// 前一方的回填提前推进，第二方拿到的「旧版本」已不是旧版本 → 静默覆盖（P3 契约基线）。
    /// <para>判据分两侧：① 共享实例下不抛 <c>ConcurrencyConflictException</c>，最终版本为 2、
    /// 值为后写者——把「共享实体实例使乐观锁失效」做成可观测事实；② 不出现静默损坏：
    /// 主键仍指向同一行、行数恰为 1。对照组用两个独立实例，第二方必须收到冲突，证明判据能区分
    /// 「共享实例」与「正常乐观锁」两种形态。</para></summary>
    [Test]
    public async Task CS25_SharedEntityInstance_AdvancesVersionToken_ButNoCrossRowCorruption()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs25").ConfigureAwait(false);
        long id;
        await using (DataSession<SqliteProvider> seed = await shared.OpenSessionAsync().ConfigureAwait(false))
        {
            CfVersionedEntity inserted = await seed.InsertAsync(
                new CfVersionedEntity { Name = "v0", Version = 0 }).ConfigureAwait(false);
            id = inserted.Id;
        }

        await using DataSession<SqliteProvider> writerA = await shared.OpenSessionAsync().ConfigureAwait(false);
        await using DataSession<SqliteProvider> writerB = await shared.OpenSessionAsync().ConfigureAwait(false);

        // 两会话共享同一个实例（模拟调用方把同一对象交给两条并发链路）
        CfVersionedEntity sharedEntity = (await writerA.GetAsync<CfVersionedEntity>(id).ConfigureAwait(false))!;
        _ = await writerB.GetAsync<CfVersionedEntity>(id).ConfigureAwait(false);

        sharedEntity.Name = "writer-a";
        await writerA.UpdateAsync(sharedEntity).ConfigureAwait(false);
        await Assert.That(sharedEntity.Version).IsEqualTo(1L);

        // B 用的是同一个实例：Version 已被 A 的提交后回填推到 1，B 的「旧版本」不再是旧版本
        sharedEntity.Name = "writer-b";
        await writerB.UpdateAsync(sharedEntity).ConfigureAwait(false);
        await Assert.That(sharedEntity.Version).IsEqualTo(2L);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await shared.QueryRawAsync(
            "SELECT id, name, version FROM cf_versioned WHERE id = $id",
            [("id", id)]).ConfigureAwait(false);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(Convert.ToInt64(rows[0]["id"], System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo(id);
        await Assert.That(Convert.ToString(rows[0]["name"], System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo("writer-b");
        await Assert.That(Convert.ToInt64(rows[0]["version"], System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo(2L);

        // 对照组：两个独立实例（各自读到 version=2）→ 第二方必须收到冲突，证明判据有判别力
        CfVersionedEntity independentA = (await writerA.GetAsync<CfVersionedEntity>(id).ConfigureAwait(false))!;
        CfVersionedEntity independentB = (await writerB.GetAsync<CfVersionedEntity>(id).ConfigureAwait(false))!;
        independentA.Name = "control-a";
        await writerA.UpdateAsync(independentA).ConfigureAwait(false);
        independentB.Name = "control-b";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
            await writerB.UpdateAsync(independentB).ConfigureAwait(false));
    }

    /// <summary>CS-25（SetId 侧）：同一实例被两会话依次插入。自增主键实体的 INSERT <b>不含 Id 列</b>
    /// （生成 SQL 实测：<c>INSERT INTO cf_versioned (name, version) ...</c>），因此第二次插入仍由
    /// 数据库分配新 Id，<b>不会</b>变成显式主键插入、也不会静默覆盖。
    /// <para>判据：两行各有独立 Id；共享实例的 Id 最终等于后写者（回填是后写者胜，P3 契约基线）；
    /// 两行 name 都是 shared——不存在「Id 指向另一会话的行」这类静默损坏。</para></summary>
    [Test]
    public async Task CS25_SharedInstance_SecondInsertGetsOwnKey_LastWriterWinsWithoutCrossRowCorruption()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs25_setid").ConfigureAwait(false);
        await using DataSession<SqliteProvider> writerA = await shared.OpenSessionAsync().ConfigureAwait(false);
        await using DataSession<SqliteProvider> writerB = await shared.OpenSessionAsync().ConfigureAwait(false);

        var sharedEntity = new CfVersionedEntity { Name = "shared", Version = 0 };
        CfVersionedEntity insertedA = await writerA.InsertAsync(sharedEntity).ConfigureAwait(false);
        long idA = insertedA.Id;
        await Assert.That(idA).IsGreaterThan(0L);
        await Assert.That(sharedEntity.Id).IsEqualTo(idA);

        CfVersionedEntity insertedB = await writerB.InsertAsync(sharedEntity).ConfigureAwait(false);
        long idB = insertedB.Id;
        await Assert.That(idB).IsNotEqualTo(idA);
        // 后写者胜：共享实例的 Id 被 B 的回填覆盖
        await Assert.That(sharedEntity.Id).IsEqualTo(idB);

        List<IReadOnlyDictionary<string, object?>> rows = (await shared.QueryRawAsync(
            "SELECT id, name FROM cf_versioned").ConfigureAwait(false)).ToList();
        await Assert.That(rows.Count).IsEqualTo(2);
        var ids = rows
            .Select(row => Convert.ToInt64(row["id"], System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(id => id)
            .ToList();
        await Assert.That(ids).IsEquivalentTo(new[] { idA, idB }.OrderBy(id => id).ToList());
        await Assert.That(rows.All(row =>
            Convert.ToString(row["name"], System.Globalization.CultureInfo.InvariantCulture) == "shared"))
            .IsTrue();
    }

    /// <summary>CS-16：读路由在「副本落后」时的确定性形态。用第二个 SQLite 库模拟副本，
    /// 手工控制「复制」发生的时刻，因此不依赖真实时间窗（T15）。
    /// <para><b>判据</b>：主库已提交新值时，<c>ForRead()</c> 读到副本旧值；同一会话
    /// <c>ForWrite()</c> 读到主库新值；把变更「复制」到副本后 <c>ForRead()</c> 读到新值。
    /// 这同时把「产品无 read-your-writes 等待」变成可观测事实：它不做 LSN 等待，读路由看到什么
    /// 完全取决于副本现状。</para>
    /// <para><b>保真度声明</b>：本机无真实 PG/MySQL 从库，本例是「复制延迟」的确定性模拟，
    /// 真实网络复制路径零覆盖（[推断]）；升级为事实需要搭真从库后重跑同一判据。</para></summary>
    [Test]
    public async Task CS16_ReadRoute_WithLaggingReplica_ReturnsStaleValueUntilReplicaCatchesUp()
    {
        await using SharedSqliteDb primary = await SharedSqliteDb.CreateAsync("cs16_primary").ConfigureAwait(false);
        await using SharedSqliteDb replica = await SharedSqliteDb.CreateAsync("cs16_replica").ConfigureAwait(false);

        const long id = 1L;
        await primary.ExecuteRawAsync(
            "INSERT INTO cf_tenant (id, tenant_id, value) VALUES ($id, 1, 'v0')",
            [("id", id)]).ConfigureAwait(false);
        await replica.ExecuteRawAsync(
            "INSERT INTO cf_tenant (id, tenant_id, value) VALUES ($id, 1, 'v0')",
            [("id", id)]).ConfigureAwait(false);

        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            primary.CreateOptions(readConnectionString: replica.ConnectionString)).ConfigureAwait(false);

        // 主库提交新值，但「复制」尚未发生
        await session.ExecuteAsync($"UPDATE cf_tenant SET value = {"v1"} WHERE id = {id}").ConfigureAwait(false);

        string replicaValue = (await session.From<CfTenantEntity>()
            .Where($"id = {id}").ForRead().ToListAsync().ConfigureAwait(false)).Single().Value;
        await Assert.That(replicaValue).IsEqualTo("v0");

        string primaryValue = (await session.From<CfTenantEntity>()
            .Where($"id = {id}").ForWrite().ToListAsync().ConfigureAwait(false)).Single().Value;
        await Assert.That(primaryValue).IsEqualTo("v1");

        // 副本追上后，同一读路由看到新值
        await replica.ExecuteRawAsync(
            "UPDATE cf_tenant SET value = 'v1' WHERE id = $id", [("id", id)]).ConfigureAwait(false);
        string caughtUpValue = (await session.From<CfTenantEntity>()
            .Where($"id = {id}").ForRead().ToListAsync().ConfigureAwait(false)).Single().Value;
        await Assert.That(caughtUpValue).IsEqualTo("v1");
    }
}
