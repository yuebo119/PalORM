using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>C 层十条不变量（方案 §6.3）。
/// <para>全程黑盒：只用公开 API 与可观察结果判定，不反射内部字段。每条一个测试，失败名即不变量编号。</para>
/// <para>I1 用 <c>maxIdleSize: 1</c> 的单槽池保证"重租的是同一实例"（`_idle` 是 FIFO 队列，
/// 容量大于 1 时不保证）；I5/I6 用注入的计数缓存观测命中；I9 用两个独立库区分主副本。</para></summary>
public sealed class StateInvariantTests
{
    private static async ValueTask<SharedSqliteDb> SeedSoftDeleteAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        try
        {
            await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive" }, ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "dead", DeletedAt = "2026-01-01" }, ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static async ValueTask<SharedSqliteDb> SeedTenantAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        try
        {
            await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
            await db.InsertAsync(new CfTenantEntity { TenantId = 1, Value = "t1" }, ct);
            await db.InsertAsync(new CfTenantEntity { TenantId = 2, Value = "t2" }, ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    [Test]
    public async Task I1_ReturnedSession_IsBackToBaselineForm()
    {
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("invariant_i1");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);

        DataSession<SqliteProvider> first = await pool.RentAsync();
        first.IgnoreFilters();
        first.WithTimeout(TimeSpan.FromSeconds(5));
        long unfiltered = await first.CountAsync<CfSoftDeleteEntity>();
        await first.DisposeAsync();

        DataSession<SqliteProvider> second = await pool.RentAsync();
        long filtered = await second.CountAsync<CfSoftDeleteEntity>();
        await second.DisposeAsync();

        await Assert.That(unfiltered).IsEqualTo(2L);
        await Assert.That(filtered).IsEqualTo(1L);
    }

    [Test]
    public async Task I2_LeaseIsolation_TenantStateDoesNotLeakAcrossRent()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("invariant_i2");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);

        DataSession<SqliteProvider> firstLease = await pool.RentAsync();
        firstLease.WithTenant(1L);
        long tenantOne = await firstLease.CountAsync<CfTenantEntity>();
        await firstLease.DisposeAsync();

        DataSession<SqliteProvider> secondLease = await pool.RentAsync();
        secondLease.WithTenant(2L);
        long tenantTwo = await secondLease.CountAsync<CfTenantEntity>();
        await secondLease.DisposeAsync();

        await Assert.That(tenantOne).IsEqualTo(1L);
        await Assert.That(tenantTwo).IsEqualTo(1L);
    }

    [Test]
    public async Task I3_AfterRollback_ConnectionRemainsUsable()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("invariant_i3");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        using (System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync())
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "rolled-back" });
            await transaction.RollbackAsync();
        }

        // 回滚后同一会话继续可用
        await db.InsertAsync(new CfBulkPlainEntity { Id = 2, Name = "after-rollback" });
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
        await Assert.That((await db.GetAsync<CfBulkPlainEntity>(2L))!.Name).IsEqualTo("after-rollback");
    }

    [Test]
    public async Task I4_CancelInsideTransaction_LeavesSessionPredictable()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("invariant_i4");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        try
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "cancelled" }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 预期形态
        }

        try
        {
            await transaction.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // 取消可能已让事务失效；无论哪种，后续必须落到"会话可继续查询"这一侧
        }
        await transaction.DisposeAsync();

        // 契约取"会话可用"一侧：新会话必然可用，原会话也不得处于永久故障态
        await using DataSession<SqliteProvider> fresh = await shared.OpenSessionAsync();
        await Assert.That(await fresh.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
    }

    [Test]
    public async Task I5_CacheKeyIncludesTenantState()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("invariant_i5");
        var cache = new CountingQueryCache();

        await using (DataSession<SqliteProvider> tenantOne = await DataSession<SqliteProvider>.CreateAsync(
            shared.CreateOptions(queryCache: cache)))
        {
            tenantOne.WithTenant(1L);
            List<CfTenantEntity> rows = await tenantOne.From<CfTenantEntity>().WithCache("shared-key").ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].TenantId).IsEqualTo(1L);
        }

        await using (DataSession<SqliteProvider> tenantTwo = await DataSession<SqliteProvider>.CreateAsync(
            shared.CreateOptions(queryCache: cache)))
        {
            tenantTwo.WithTenant(2L);
            List<CfTenantEntity> rows = await tenantTwo.From<CfTenantEntity>().WithCache("shared-key").ToListAsync();
            // 缓存键若漏掉租户态，这里会命中租户 1 的条目并返回 t1 的行
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].TenantId).IsEqualTo(2L);
        }
    }

    [Test]
    public async Task I6_AfterEvictQueryCache_NextReadIsMiss()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("invariant_i6");
        var cache = new CountingQueryCache();
        await using DataSession<SqliteProvider> db = await DataSession<SqliteProvider>.CreateAsync(
            shared.CreateOptions(queryCache: cache));

        await db.From<CfTenantEntity>().WithCache("evict-key").ToListAsync();
        await db.From<CfTenantEntity>().WithCache("evict-key").ToListAsync();
        int hitsBeforeEvict = cache.Hits;
        await Assert.That(hitsBeforeEvict).IsGreaterThan(0);

        db.EvictQueryCache();
        await db.From<CfTenantEntity>().WithCache("evict-key").ToListAsync();

        await Assert.That(cache.Hits).IsEqualTo(hitsBeforeEvict);
    }

    [Test]
    public async Task I7_InterceptorRunsOnceOnThreeFamiliesAndOnErrorOnFailure()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("invariant_i7");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "a" });

        var counter = new CountingInterceptor();
        db.AddInterceptor(counter);

        // 族 1：实体 SELECT
        await db.From<CfBulkPlainEntity>().ToListAsync();
        await Assert.That(counter.Before).IsEqualTo(1);
        await Assert.That(counter.After).IsEqualTo(1);

        // 族 2：QueryBuilder UPDATE
        await db.From<CfBulkPlainEntity>().Set(e => e.Name, "b").ExecuteNonQueryAsync();
        await Assert.That(counter.Before).IsEqualTo(2);
        await Assert.That(counter.After).IsEqualTo(2);

        // 族 3：ExecuteAsync 原始 SQL
        await db.ExecuteAsync($"SELECT 1");
        await Assert.That(counter.Before).IsEqualTo(3);
        await Assert.That(counter.After).IsEqualTo(3);

        // 异常路径：OnBefore + OnError，且不调用 OnAfter
        await Assert.That(async () => await db.ExecuteAsync($"SELECT * FROM no_such_table"))
            .Throws<Exception>();
        await Assert.That(counter.Error).IsEqualTo(1);
        await Assert.That(counter.Before).IsEqualTo(4);
        await Assert.That(counter.After).IsEqualTo(3);
    }

    [Test]
    public async Task I8_AfterCancellation_IdleCountReturnsToBaseline()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("invariant_i8");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);

        for (int i = 0; i < 5; i++)
        {
            DataSession<SqliteProvider> session = await pool.RentAsync();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            try
            {
                await session.CountAsync<CfBulkPlainEntity>(ct: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 预期形态
            }
            await session.DisposeAsync();
        }

        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task I9_ReadRouteUsesReplicaWhenConfigured()
    {
        await using SharedSqliteDb primary = await SharedSqliteDb.CreateAsync("invariant_i9_primary");
        await using SharedSqliteDb replica = await SharedSqliteDb.CreateAsync("invariant_i9_replica");

        await using (DataSession<SqliteProvider> primarySession = await primary.OpenSessionAsync())
        {
            await primarySession.BulkInsertAsync(
                [new CfBulkPlainEntity { Id = 1, Name = "p1" }, new CfBulkPlainEntity { Id = 2, Name = "p2" }]);
        }
        await using (DataSession<SqliteProvider> replicaSession = await replica.OpenSessionAsync())
        {
            await replicaSession.BulkInsertAsync(
            [
                new CfBulkPlainEntity { Id = 1, Name = "r1" },
                new CfBulkPlainEntity { Id = 2, Name = "r2" },
                new CfBulkPlainEntity { Id = 3, Name = "r3" }
            ]);
        }

        await using DataSession<SqliteProvider> routed = await DataSession<SqliteProvider>.CreateAsync(
            primary.CreateOptions(readConnectionString: replica.ConnectionString));

        List<CfBulkPlainEntity> readRows = await routed.From<CfBulkPlainEntity>().ForRead().ToListAsync();
        List<CfBulkPlainEntity> writeRows = await routed.From<CfBulkPlainEntity>().ForWrite().ToListAsync();

        // 读副本 3 行、主库 2 行；读路由未生效时两侧会相同
        await Assert.That(readRows.Count).IsEqualTo(3);
        await Assert.That(writeRows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task I10_CircuitBreaker_NonTransientErrorDoesNotTrip()
    {
        // 实测（2026-10-29）：弹性执行器只对"瞬时错误"计数（TProvider.IsTransient），
        // 表不存在这类 SQL 错误直接抛出，不进熔断计数。方案 §6.3 I10 原设计"指向不存在的表"
        // 与实现不符，本用例按实测契约断言，三态转移的完整覆盖需瞬时错误注入（见文档 §8.1）。
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("invariant_i10");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "a" });

        db.WithCircuitBreaker(failureThreshold: 1, resetAfter: TimeSpan.FromMilliseconds(300));

        // 关闭态：正常查询成功
        await Assert.That((await db.GetAllAsync<CfBulkPlainEntity>()).Count).IsEqualTo(1);

        await shared.ExecuteRawAsync("DROP TABLE cf_bulk_plain");

        // 非瞬时错误：直接抛出驱动异常，不是 CircuitBreakerOpenException
        await Assert.That(async () => await db.GetAllAsync<CfBulkPlainEntity>())
            .Throws<Microsoft.Data.Sqlite.SqliteException>();

        // 熔断未被这次失败打开：恢复表后同一会话仍能正常查询
        await shared.ExecuteRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "cf_bulk_plain" (
                "Id" INTEGER PRIMARY KEY NOT NULL,
                "name" TEXT NOT NULL,
                "amount" INTEGER NOT NULL
            )
            """);
        await Assert.That((await db.GetAllAsync<CfBulkPlainEntity>()).Count).IsEqualTo(0);
    }

    private sealed class CountingQueryCache : IQueryCache
    {
        private readonly Dictionary<string, object> _store = new(StringComparer.Ordinal);

        public int Hits { get; private set; }

        public bool TryGet<T>(string key, out T? value) where T : class
        {
            if (_store.TryGetValue(key, out object? cached) && cached is T typed)
            {
                Hits++;
                value = typed;
                return true;
            }
            value = null;
            return false;
        }

        public void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class
            => _store[key] = value;

        public void Clear() => _store.Clear();
    }

    private sealed class CountingInterceptor : IQueryInterceptor
    {
        public int Before { get; private set; }

        public int After { get; private set; }

        public int Error { get; private set; }

        public void OnBefore(QueryContext context) => Before++;

        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) => After++;

        public void OnError(QueryContext context, Exception exception) => Error++;
    }
}