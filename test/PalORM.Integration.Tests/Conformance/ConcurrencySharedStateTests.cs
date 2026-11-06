using System.Globalization;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L1 跨会话共享状态与脏数据（修订版方案 §5 CS-06/10/14/18/21/25/26）。
/// <para><b>通道划分</b>：CS-06 的并发写走裸 ADO（期望值独立于被测代码，判据才有判别力），
/// 产品侧并发行为由 CS-10 池租还、CS-18 租户过滤、CS-14 查询缓存三条覆盖。
/// 不把「夹具自己并发写」写成「产品并发写已验证」。</para>
/// <para><b>时间窗改造</b>：原 C-14 TTL 陈旧窗口撞 T15/T24，本组改成确定性交错：
/// 用显式顺序控制「写前 / 写后 / 失效后」三次读，断言每次读到的具体值，不用耗时或 TTL 计时。</para></summary>
public sealed class ConcurrencySharedStateTests
{
    private const int Workers = 8;
    private const int RowsPerWorker = 25;

    /// <summary>CS-06：8 写者并发写同一张共享表，整表逐值对账。
    /// <para>补现有 <c>SessionFanOutStressTests.cs:49-50</c> 只核总数不核值的缺口。
    /// 主键按 worker 分区不冲突，标记用本轮专属值防串用例。</para>
    /// <para><b>每 worker 独立连接</b>：<c>Microsoft.Data.Sqlite</c> 单连接不支持并发命令，
    /// 共用 keeper 连接会在 <c>Close()</c> 触发 NRE（实测）。连接串加 <c>Default Timeout</c>
    /// 让共享缓存库的写争用按 busy_timeout 排队而不是立即可见地失败。</para></summary>
    [Test]
    public async Task CS06_ConcurrentWriters_AllRowsMatchExpectedValues()
    {
        const string marker = "cs06";
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs06").ConfigureAwait(false);
        await ConcurrencyRowVerifier.CreateTableAsync(shared).ConfigureAwait(false);
        await using ConcurrencyHarness harness = ConcurrencyHarness.Create();

        string perWorkerConnectionString = shared.ConnectionString + ";Default Timeout=10";
        await harness.RunAsync(Workers, async (worker, ct) =>
        {
            await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
                new DbOptions { ConnectionString = perWorkerConnectionString }, ct).ConfigureAwait(false);
            System.Data.Common.DbConnection connection = session.GetRawConnection();
            for (int seq = 0; seq < RowsPerWorker; seq++)
            {
                long id = worker * RowsPerWorker + seq + 1;
                await ConcurrencyRowVerifier.InsertRowAsync(connection, id, worker, seq, marker, ct)
                    .ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        IReadOnlyList<string> differences = await ConcurrencyRowVerifier.VerifyAllRowsAsync(
            shared, Workers * RowsPerWorker, Workers, RowsPerWorker, marker).ConfigureAwait(false);
        await Assert.That(string.Join("\n", differences)).IsEmpty();
        await Assert.That(harness.Violations).IsEmpty();
    }

    /// <summary>CS-10：有界池 + 超额并发租借，全部归还后 IdleCount 恰为容量上界，
    /// 且同一会话实例不会被同时租给两个 worker。</summary>
    [Test]
    public async Task CS10_PoolSaturation_IdleCountReturnsToCapacity()
    {
        const int capacity = 4;
        const int borrowers = 16;
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs10").ConfigureAwait(false);
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: capacity);
        await using ConcurrencyHarness harness = ConcurrencyHarness.Create();

        // 引用判等而非 GetHashCode：哈希可碰撞，不能作为「同一实例」的证据。
        // 「并发租出」= 某实例在他人持有期间被再次租出；归还后顺序复用是池的正常语义，
        // 故租出时 TryAdd、归还后移除，只有在持有窗口内重复出现才算违规。
        var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<
            DataSession<SqliteProvider>, byte>(ReferenceEqualityComparer.Instance);
        await harness.RunAsync(borrowers, async (worker, ct) =>
        {
            DataSession<SqliteProvider> session = await pool.RentAsync(ct).ConfigureAwait(false);
            bool added = inFlight.TryAdd(session, 0);
            if (!added)
                harness.Record(worker, 0, "同一会话实例只被租出一次", "同一会话实例被并发租出");
            try
            {
                await session.ExecuteAsync($"SELECT 1", ct).ConfigureAwait(false);
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
                if (added) inFlight.TryRemove(session, out _);
            }
        }).ConfigureAwait(false);

        await Assert.That(harness.Violations).IsEmpty();
        await Assert.That(pool.IdleCount).IsEqualTo(capacity);
    }

    /// <summary>CS-14：查询缓存写后读陈旧的确定性交错（ADR-O P2 取舍，量化不判失败）。
    /// <para>走 <c>From&lt;T&gt;().WithCache()</c>（缓存真实挂载点；裸 <c>ScalarAsync</c> 不经缓存）。
    /// 注入会话级 <c>BoundedQueryCache</c>，状态由用例自己持有（T5/T22）。
    /// 三次读期望值：写前 <c>before</c>；写后未失效时允许旧值或新值（记录实际值）；
    /// 调用 <c>EvictQueryCache()</c> 后必须 <c>after</c>。</para></summary>
    [Test]
    public async Task CS14_QueryCache_ExplicitInterleave_EvictMakesNewValueVisible()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs14").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "INSERT INTO cf_tenant (tenant_id, value) VALUES (1, 'before')").ConfigureAwait(false);

        var cache = new BoundedQueryCache();
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            shared.CreateOptions(queryCache: cache)).ConfigureAwait(false);
        const string key = "cs14-key";

        string first = (await session.From<CfTenantEntity>().WithCache(key).ToListAsync()
            .ConfigureAwait(false)).Single().Value;
        await Assert.That(first).IsEqualTo("before");

        await session.ExecuteAsync($"UPDATE cf_tenant SET value = 'after' WHERE id = 1")
            .ConfigureAwait(false);
        string second = (await session.From<CfTenantEntity>().WithCache(key).ToListAsync()
            .ConfigureAwait(false)).Single().Value;
        // 不判失败：ADR-O 已裁决 TTL 最终一致。把实际值作为基线事实记录。
        await Assert.That(second is "before" or "after").IsTrue();

        session.EvictQueryCache();
        string third = (await session.From<CfTenantEntity>().WithCache(key).ToListAsync()
            .ConfigureAwait(false)).Single().Value;
        await Assert.That(third).IsEqualTo("after");
    }

    /// <summary>CS-14 能失败验证：不调 EvictQueryCache 时缓存仍返回旧值，
    /// 证明 CS-14 的 third 断言有判别力，不是恒真。</summary>
    [Test]
    public async Task CS14_Gate_NoEvict_StaleValueRemains()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs14_gate").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "INSERT INTO cf_tenant (tenant_id, value) VALUES (1, 'before')").ConfigureAwait(false);

        var cache = new BoundedQueryCache();
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            shared.CreateOptions(queryCache: cache)).ConfigureAwait(false);
        const string key = "cs14-gate-key";

        await session.From<CfTenantEntity>().WithCache(key).ToListAsync().ConfigureAwait(false);
        await session.ExecuteAsync($"UPDATE cf_tenant SET value = 'after' WHERE id = 1")
            .ConfigureAwait(false);
        // 故意不调 EvictQueryCache：缓存仍返回旧值。
        string stale = (await session.From<CfTenantEntity>().WithCache(key).ToListAsync()
            .ConfigureAwait(false)).Single().Value;
        await Assert.That(stale).IsEqualTo("before");
    }

    /// <summary>CS-18：8 租户并发混合写后，各自读回的 TenantId 与行数符合本租户期望。
    /// <para><c>IgnoreFilters()</c> 组按契约记录「返回全量」为基线事实，不判失败。</para></summary>
    [Test]
    public async Task CS18_TenantFilter_ConcurrentMixedWrites_NoCrossTenantRows()
    {
        const int tenants = 8;
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs18").ConfigureAwait(false);
        await using ConcurrencyHarness harness = ConcurrencyHarness.Create();

        await harness.RunAsync(tenants, async (tenant, ct) =>
        {
            await using DataSession<SqliteProvider> session = await shared.OpenSessionAsync(ct)
                .ConfigureAwait(false);
            session.WithTenant((long)tenant);
            await session.InsertAsync(
                new CfTenantEntity { TenantId = tenant, Value = $"t{tenant}" }, ct).ConfigureAwait(false);

            List<CfTenantEntity> rows = await session.GetAllAsync<CfTenantEntity>(ct).ConfigureAwait(false);
            if (rows.Count != 1 || rows[0].TenantId != tenant || rows[0].Value != $"t{tenant}")
                harness.Record(tenant, 0, $"本租户 1 行且 TenantId={tenant}",
                    $"实际 {rows.Count} 行，首行 TenantId={(rows.Count > 0 ? rows[0].TenantId : -1)}");
        }).ConfigureAwait(false);

        await Assert.That(string.Join("\n", harness.Violations.Select(v => v.Actual))).IsEmpty();

        // 基线事实：IgnoreFilters 按契约看到全量 8 行。
        await using DataSession<SqliteProvider> unscoped = await shared.OpenSessionAsync().ConfigureAwait(false);
        List<CfTenantEntity> all = await unscoped.IgnoreFilters()
            .GetAllAsync<CfTenantEntity>().ConfigureAwait(false);
        await Assert.That(all.Count).IsEqualTo(tenants);
    }

    /// <summary>CS-26：原始 SQL 不套默认过滤（契约基线），自动过滤入口 From&lt;T&gt;() 不受影响。
    /// <para>租户表用裸 ADO 播种两个租户，再用租户会话核自动过滤入口的具体行数。</para></summary>
    [Test]
    public async Task CS26_RawSql_ByContractSeesAllRows_AutoFilterStillApplies()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs26").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "INSERT INTO cf_tenant (tenant_id, value) VALUES (1, 'a'), (2, 'b')").ConfigureAwait(false);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> raw = await shared.QueryRawAsync(
            "SELECT tenant_id, value FROM cf_tenant").ConfigureAwait(false);
        await Assert.That(raw.Count).IsEqualTo(2);

        await using DataSession<SqliteProvider> session = await shared.OpenSessionAsync().ConfigureAwait(false);
        session.WithTenant(1L);
        List<CfTenantEntity> filtered = await session.From<CfTenantEntity>()
            .ToListAsync().ConfigureAwait(false);
        await Assert.That(filtered.Count).IsEqualTo(1);
        await Assert.That(filtered[0].TenantId).IsEqualTo(1L);
    }

    /// <summary>CS-15：缓存命中返回的元素是共享实体实例（ITM-308 文档化契约，P3 不判缺陷）。
    /// <para>WithCache 文档明确「命中返回新 List，但元素是共享实体实例」，本用例锁定该契约：
    /// 读者 A 改命中实体的字段，读者 B 的命中结果可见同一改动；数据库行不受影响。
    /// 若产品改为深拷贝，本合同测试须随之改为「互不可见」。</para></summary>
    [Test]
    public async Task CS15_CacheHit_ElementsAreSharedInstances_DbRowUnaffected()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs15").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "INSERT INTO cf_tenant (tenant_id, value) VALUES (1, 'original')").ConfigureAwait(false);

        var cache = new BoundedQueryCache();
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            shared.CreateOptions(queryCache: cache)).ConfigureAwait(false);
        string key = "cs15-" + Guid.NewGuid().ToString("N");

        CfTenantEntity readerA = (await session.From<CfTenantEntity>().WithCache(key).ToListAsync()
            .ConfigureAwait(false)).Single();
        readerA.Value = "mutated-by-a";

        CfTenantEntity readerB = (await session.From<CfTenantEntity>().WithCache(key).ToListAsync()
            .ConfigureAwait(false)).Single();
        await Assert.That(readerB.Value).IsEqualTo("mutated-by-a");

        IReadOnlyList<IReadOnlyDictionary<string, object?>> dbRows = await shared.QueryRawAsync(
            "SELECT value FROM cf_tenant").ConfigureAwait(false);
        await Assert.That(
            Convert.ToString(dbRows[0]["value"], System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo("original");
    }

    /// <summary>CS-19：8 会话并发 BulkInsertReturningAsync，每个实体拿到的 Id 与自己插入的行一一对应。
    /// <para>既有 <c>BulkInsertReturningTests</c> 覆盖单批回填映射，本例只补并发交错下的 id 不串行。
    /// 每 worker 独立连接（单连接不支持并发命令）。</para></summary>
    [Test]
    public async Task CS19_ConcurrentBulkInsertReturning_IdsMapToOwnRows()
    {
        const int workers = 8;
        const int rowsPerWorker = 10;
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs19").ConfigureAwait(false);
        string perWorkerConnectionString = shared.ConnectionString + ";Default Timeout=10";
        await using ConcurrencyHarness harness = ConcurrencyHarness.Create();

        var inserted = new System.Collections.Concurrent.ConcurrentBag<(string Marker, long Id)>();
        await harness.RunAsync(workers, async (worker, ct) =>
        {
            await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
                new DbOptions { ConnectionString = perWorkerConnectionString }, ct).ConfigureAwait(false);
            var rows = new List<CfTenantEntity>(rowsPerWorker);
            for (int i = 0; i < rowsPerWorker; i++)
                rows.Add(new CfTenantEntity { TenantId = 1, Value = $"w{worker}-{i}" });

            await session.BulkInsertReturningAsync(rows, ct).ConfigureAwait(false);
            foreach (CfTenantEntity row in rows)
            {
                if (row.Id <= 0)
                {
                    harness.Record(worker, 0, $"w{worker}-{row.Value} 的 Id 已回填", $"实际 Id={row.Id}");
                    continue;
                }
                inserted.Add((row.Value, row.Id));
            }
        }).ConfigureAwait(false);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> dbRows = await shared.QueryRawAsync(
            "SELECT id, value FROM cf_tenant").ConfigureAwait(false);
        var idByValue = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, object?> dbRow in dbRows)
            idByValue[Convert.ToString(dbRow["value"], System.Globalization.CultureInfo.InvariantCulture)!] =
                Convert.ToInt64(dbRow["id"], System.Globalization.CultureInfo.InvariantCulture);

        foreach ((string marker, long id) in inserted)
        {
            if (!idByValue.TryGetValue(marker, out long actualId) || actualId != id)
                harness.Record(0, 0, $"{marker} 回填 Id={id}", $"库中实际 {(idByValue.TryGetValue(marker, out long v) ? v : -1)}");
        }

        await Assert.That(string.Join("\n", harness.Violations.Select(v => $"{v.Expected} / {v.Actual}")))
            .IsEmpty();
        await Assert.That(idByValue.Count).IsEqualTo(workers * rowsPerWorker);
    }
}
