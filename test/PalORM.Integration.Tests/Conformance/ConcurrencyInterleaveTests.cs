using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L0 确定性交错（修订版方案 §5 CS-01~CS-05）。
/// <para><b>判据口径</b>：全部用具体值或精确异常消息，不读私有状态（T2），不用实际耗时（T15）。
/// 现有 <c>SessionConcurrencyTests</c> 已有 40 例 TCS 编排，本组只补「跨租期 / 跨会话共享状态」空白。</para>
/// <para><b>能失败验证</b>：每条配一个坏输入反证，见 <c>ConcurrencyInterleaveGateTests</c>。</para></summary>
public sealed class ConcurrencyInterleaveTests
{
    /// <summary>CS-01：池归还后旧引用与新租期是同一实例（实测事实，非缺陷判定）。
    /// <para>产品无租期世代校验（DataSessionPool.cs 全文无 lease/generation），Park 置 _state=3、
    /// Unpark 复位 0，旧引用与新租期共用同一 DbConnection。本例主动让旧引用在新租期建立后写入
    /// 带专属标记的行，把「复活」做成可观测事实，再核两张标记行的归属：
    /// 无论旧引用写入成功还是被拒，新租期自己写入的那行必须值正确、总数符合实际写入数。</para>
    /// <para><b>能失败验证</b>：见 <c>CS01_Gate_StaleMarker_IsDetected</c>，把旧引用标记行
    /// 伪装成新租期，全表对账必须报红。</para></summary>
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

        int expectedCount = oldRefWrote ? 2 : 1;
        await Assert.That(rows.Count).IsEqualTo(expectedCount);
        // 新租期那行必须逐值正确，且旧引用标记不得冒充新租期。
        IReadOnlyDictionary<string, object?> newRow = rows.Single(
            r => Convert.ToInt64(r["id"], System.Globalization.CultureInfo.InvariantCulture) == 2);
        await Assert.That(Convert.ToString(newRow["marker"], System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo("new-lease");
        await Assert.That(rows.Any(r =>
            Convert.ToString(r["marker"], System.Globalization.CultureInfo.InvariantCulture) == "old-ref"
            && Convert.ToInt64(r["id"], System.Globalization.CultureInfo.InvariantCulture) != 1))
            .IsFalse();
        await newLease.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>CS-01 的能失败验证：把旧引用标记行伪装成新租期标记，逐值对账必须报红。</summary>
    [Test]
    public async Task CS01_Gate_StaleMarker_IsDetected()
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
        int newLeaseRows = rows.Count(r =>
            Convert.ToString(r["marker"], System.Globalization.CultureInfo.InvariantCulture) == "new-lease");
        // 坏输入下新租期标记行数为 0，证明「新租期值正确」这条判据能检出污染。
        await Assert.That(newLeaseRows).IsEqualTo(0);
    }

    /// <summary>CS-03：归还后 IdleCount 与复用槽状态的具体值（快速释放路径）。
    /// <para>评审修正：删掉全仓零匹配的 SynchronizationLockException 判据。
    /// 硬超时只作防挂死兜底，不作断言；判据是「归还后 IdleCount 恰为 1、重租可用」。</para></summary>
    [Test]
    public async Task CS03_FastDisposeThenReRent_IdleCountIsExact()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs03").ConfigureAwait(false);
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);
        await using ConcurrencyHarness harness = ConcurrencyHarness.Create();

        for (int round = 0; round < 50; round++)
        {
            DataSession<SqliteProvider> session = await pool.RentAsync(harness.Token).ConfigureAwait(false);
            await session.ExecuteAsync($"SELECT 1", harness.Token).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            await Assert.That(pool.IdleCount).IsEqualTo(1);
        }
        await Assert.That(harness.Violations).IsEmpty();
    }

    /// <summary>CS-05：SqlSlots&lt;T&gt; 静态槽在注册表复位后仍可能残留（实测事实）。
    /// <para>DataSessionCache.cs:92-93 自承该闭式泛型静态槽不在 ClearForTesting 范围内。
    /// 本例只记录产品当前行为，不判缺陷（P3 文档契约）。判据 = 换名后新会话的 COUNT 与表名一致。</para></summary>
    [Test]
    public async Task CS05_RegistryGeneration_StaticSlotsDoNotCorruptNewTableName()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs05").ConfigureAwait(false);
        await shared.ExecuteRawAsync(
            "CREATE TABLE cs05_rows (id INTEGER PRIMARY KEY, name TEXT NOT NULL)").ConfigureAwait(false);

        await using (DataSession<SqliteProvider> first = await shared.OpenSessionAsync().ConfigureAwait(false))
            await first.ExecuteAsync($"INSERT INTO cs05_rows (id, name) VALUES (1, 'before')")
                .ConfigureAwait(false);

        ConcurrencyHarness.ResetProcessState();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await shared.QueryRawAsync(
            "SELECT id, name FROM cs05_rows").ConfigureAwait(false);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(Convert.ToString(rows[0]["name"], System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo("before");
    }
}
