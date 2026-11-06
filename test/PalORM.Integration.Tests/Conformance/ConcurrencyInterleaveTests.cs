using System.Globalization;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L0 确定性交错（修订版方案 §5 CS-01/CS-03/CS-05）。
/// <para><b>判据口径</b>：全部用具体值或精确异常消息，不读私有状态（T2），不用实际耗时（T15）。
/// 现有 <c>SessionConcurrencyTests</c> 已有 40 例 TCS 编排，本组只补「跨租期 / 跨会话共享状态」空白。</para>
/// <para><b>能失败验证</b>：有反证的用例在同文件以 <c>_Gate</c> 后缀成对出现，反证复用主用例的
/// 判据函数并喂坏输入，而不是另写一条必然通过的断言。</para>
/// <para><b>并行纪律</b>：CS-03/CS-05 调 <c>ConcurrencyHarness.ResetProcessState()</c>，清的是进程级
/// 查询缓存，故本类标 <c>[NotInParallel]</c>（详见 <see cref="ConcurrencyHarness"/> 类注释）。</para></summary>
[NotInParallel]
public sealed class ConcurrencyInterleaveTests
{
    /// <summary>CS-01 判据（主用例与反证共用同一实现，保证反证证明的是主判据能失败）。
    /// <para>返回差异清单，空表示通过。判据三条：总行数与实际写入数一致；id=2 那行的 marker
    /// 必须是新租期值；任何 id≠1 的行都不得带旧引用标记。</para></summary>
    private static List<string> CheckLeaseIsolation(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, bool oldRefWrote)
    {
        var differences = new List<string>();
        int expectedCount = oldRefWrote ? 2 : 1;
        if (rows.Count != expectedCount)
            differences.Add($"行数 {rows.Count}，期望 {expectedCount}");

        string MarkerOf(IReadOnlyDictionary<string, object?> row)
            => Convert.ToString(row["marker"], CultureInfo.InvariantCulture) ?? "";
        long IdOf(IReadOnlyDictionary<string, object?> row)
            => Convert.ToInt64(row["id"], CultureInfo.InvariantCulture);

        IReadOnlyDictionary<string, object?>? newRow = rows.FirstOrDefault(r => IdOf(r) == 2);
        if (newRow is null)
            differences.Add("缺少 id=2 的新租期行");
        else if (MarkerOf(newRow) != "new-lease")
            differences.Add($"id=2 的 marker={MarkerOf(newRow)}，期望 new-lease");

        differences.AddRange(rows
            .Where(row => MarkerOf(row) == "old-ref" && IdOf(row) != 1)
            .Select(row => $"id={IdOf(row)} 带旧引用标记，旧引用污染了新租期行"));
        return differences;
    }

    /// <summary>CS-01：池归还后旧引用与新租期是同一实例（实测事实，非缺陷判定）。
    /// <para>产品无租期世代校验（DataSessionPool.cs 全文无 lease/generation），Park 置驻留态、
    /// Unpark 复位，旧引用与新租期共用同一 DbConnection。本例主动让旧引用在新租期建立后写入
    /// 带专属标记的行，把「复活」做成可观测事实，再用 <see cref="CheckLeaseIsolation"/> 对账。</para></summary>
    [Test]
    public async Task CS01_PoolReturnedOldReference_NewLeaseDataIsIsolated()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs01").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "CREATE TABLE cs01_rows (id INTEGER PRIMARY KEY, marker TEXT NOT NULL)").ConfigureAwait(false);
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);

        DataSession<SqliteProvider> oldLease = await pool.RentAsync().ConfigureAwait(false);
        await oldLease.DisposeAsync().ConfigureAwait(false);

        DataSession<SqliteProvider> newLease = await pool.RentAsync().ConfigureAwait(false);
        try
        {
            // 实测事实：池容量 1 时重租的是同一实例，两租期共用同一会话对象。
            await Assert.That(ReferenceEquals(newLease, oldLease)).IsTrue();

            bool oldRefWrote;
            try
            {
                await oldLease.ExecuteAsync($"INSERT INTO cs01_rows (id, marker) VALUES (1, 'old-ref')")
                    .ConfigureAwait(false);
                oldRefWrote = true;
            }
            catch (ObjectDisposedException)
            {
                oldRefWrote = false;
            }

            await newLease.ExecuteAsync($"INSERT INTO cs01_rows (id, marker) VALUES (2, 'new-lease')")
                .ConfigureAwait(false);
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await shared.QueryRawAsync(
                "SELECT id, marker FROM cs01_rows ORDER BY id").ConfigureAwait(false);

            await Assert.That(string.Join("\n", CheckLeaseIsolation(rows, oldRefWrote))).IsEmpty();
        }
        finally
        {
            await newLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>CS-01 的能失败验证：把 id=2 也写成旧引用标记（模拟旧引用污染新租期行），
    /// 复用主用例判据必须报红。断言的是差异清单的具体文本，不是「清单非空」这类弱形态。</summary>
    [Test]
    public async Task CS01_Gate_StaleMarker_IsDetectedBySameCriterion()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs01_gate").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "CREATE TABLE cs01_rows (id INTEGER PRIMARY KEY, marker TEXT NOT NULL)").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "INSERT INTO cs01_rows (id, marker) VALUES (1, 'old-ref')").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "INSERT INTO cs01_rows (id, marker) VALUES (2, 'old-ref')").ConfigureAwait(false);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await shared.QueryRawAsync(
            "SELECT id, marker FROM cs01_rows ORDER BY id").ConfigureAwait(false);
        IReadOnlyList<string> differences = CheckLeaseIsolation(rows, oldRefWrote: true);
        string joined = string.Join("\n", differences);
        await Assert.That(joined).Contains("id=2 的 marker=old-ref，期望 new-lease");
        await Assert.That(joined).Contains("id=2 带旧引用标记");
    }

    /// <summary>CS-03：归还后 IdleCount 与复用槽状态的具体值（快速释放路径）。
    /// <para>评审修正：删掉全仓零匹配的 SynchronizationLockException 判据。硬超时只作防挂死兜底，
    /// 不作断言；判据是「归还后 IdleCount 恰为 1」。</para></summary>
    [Test]
    public async Task CS03_FastDisposeThenReRent_IdleCountIsExact()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs03").ConfigureAwait(false);
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);
        await using ConcurrencyHarness harness = ConcurrencyHarness.Create();

        for (int round = 0; round < 50; round++)
        {
            DataSession<SqliteProvider> session = await pool.RentAsync(harness.Token).ConfigureAwait(false);
            try
            {
                await session.ExecuteAsync($"SELECT 1", harness.Token).ConfigureAwait(false);
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            await Assert.That(pool.IdleCount).IsEqualTo(1);
        }
    }

    /// <summary>CS-05：静态 SQL 槽在缓存/槽清理后仍能返回正确行（可做的部分）。
    /// <para><b>能做什么、不能做什么要分清</b>：本工程（Integration.Tests）没有 Core 的
    /// InternalsVisibleTo，拿不到 <c>DataSessionCache.ClearForTesting</c> 与
    /// <c>PalORM_Runtime.RestoreStateForTesting</c>，因此「注册表换表名后旧静态槽残留」这一子场景
    /// 无法在此通道构造，已在修订版方案 §5 标为 deferred（触发条件：给 Integration 开 IVT 或
    /// 在 Core.Tests 通道补用例）。
    /// 本例做的是可达部分：清进程级查询缓存后，新建会话走产品查询路径仍读到正确行与正确值，
    /// 证明槽/缓存清理不会把后续查询带偏。</para></summary>
    [Test]
    public async Task CS05_AfterProcessStateReset_ProductQueryStillSeesCorrectRows()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs05").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "INSERT INTO cf_tenant (tenant_id, value) VALUES (1, 'before')").ConfigureAwait(false);

        await using (DataSession<SqliteProvider> first = await shared.OpenSessionAsync().ConfigureAwait(false))
            await Assert.That(await first.CountAsync<CfTenantEntity>().ConfigureAwait(false)).IsEqualTo(1L);

        // 清进程级查询缓存（公开入口；注册表代际复位是 internal，本工程不可达）。
        ConcurrencyHarness.ResetProcessState();

        await using DataSession<SqliteProvider> afterReset = await shared.OpenSessionAsync().ConfigureAwait(false);
        List<CfTenantEntity> rows = await afterReset.From<CfTenantEntity>().ToListAsync().ConfigureAwait(false);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Value).IsEqualTo("before");
        await Assert.That(rows[0].TenantId).IsEqualTo(1L);
    }
}
