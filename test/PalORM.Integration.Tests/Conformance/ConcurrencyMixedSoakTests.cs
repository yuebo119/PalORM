using System.Collections.Concurrent;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L3 有界混合读写浸泡（修订版方案 §5 CS-22）。
/// <para><b>与既有 <c>ConcurrencySoakTests</c> 的区别</b>：那条是 8 线程 × 3 轮 × 30 秒纯读，
/// 只核租期隔离；本条是 16 worker × 20 轮 × 80/20 混合读写，每轮末做全表 checksum 与逐行值
/// 对账，覆盖长时序交错下的丢行/重复行/值错乱。</para>
/// <para><b>为什么不用墙钟窗口</b>：T15 禁止实际耗时类断言。本轮用<b>固定轮数</b>而非固定时长：
/// 每轮由屏障把 16 个 worker 对齐，checksum 只在屏障之间计算，因此期望值是确定值
/// （<c>workers × round</c>），与调度快慢无关。真实 10~30 分钟长稳仍需独立 nightly，
/// 本用例把它的<b>判据</b>先钉死，nightly 只换轮数。</para></summary>
[NotInParallel]
public sealed class ConcurrencyMixedSoakTests
{
    private const int Workers = 16;
    private const int Rounds = 20;
    private const int ReadsPerRound = 4;

    /// <summary>CS-22：16 worker 各持一行（id/tenant 均按 worker 分区），每轮做 4 次跨 worker 读
    /// + 1 次本行写（80/20），屏障对齐后核 checksum。判据四件：每轮 checksum 恰为
    /// <c>workers × round</c>；跨 worker 读只能看到本轮或上一轮的值；结束时每行值等于总轮数；
    /// 行数恒为 worker 数（无丢行/重复行）。</summary>
    [Test]
    public async Task CS22_BoundedMixedSoak_PerRoundChecksumAndFinalRowsAreExact()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("cs22").ConfigureAwait(false);
        await using (DataSession<SqliteProvider> seed = await shared.OpenSessionAsync().ConfigureAwait(false))
        {
            var rows = new List<CfTenantEntity>(Workers);
            for (int worker = 0; worker < Workers; worker++)
                rows.Add(new CfTenantEntity { Id = worker + 1, TenantId = worker + 1, Value = "0" });
            await seed.BulkInsertAsync(rows).ConfigureAwait(false);
        }

        await using ConcurrencyHarness harness = ConcurrencyHarness.Create(TimeSpan.FromMinutes(3));
        var violations = new ConcurrentBag<string>();
        var roundBarrier = new AsyncBarrier(Workers);
        var checksumBarrier = new AsyncBarrier(Workers);

        await harness.RunAsync(Workers, async (worker, ct) =>
        {
            await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
                new DbOptions { ConnectionString = shared.ConnectionString + ";Default Timeout=10" }, ct)
                .ConfigureAwait(false);
            for (int round = 1; round <= Rounds; round++)
            {
                await CheckNeighbourRowsAsync(session, worker, round, violations, ct).ConfigureAwait(false);
                await WriteOwnRowAsync(session, worker, round, ct).ConfigureAwait(false);

                await roundBarrier.SignalAndWaitAsync(ct).ConfigureAwait(false);
                // 屏障之间无写入：checksum 是确定值
                long sum = await session.ScalarAsync<long>($"SELECT SUM(value) FROM cf_tenant", ct: ct)
                    .ConfigureAwait(false);
                long expected = (long)Workers * round;
                if (sum != expected)
                    violations.Add($"round={round} checksum={sum}，期望 {expected}");
                await checksumBarrier.SignalAndWaitAsync(ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        await Assert.That(string.Join("\n", violations.Take(10))).IsEmpty();
        await Assert.That(harness.Violations).IsEmpty();

        // 终态逐行对账：每行值 == 总轮数，行数 == worker 数
        await using DataSession<SqliteProvider> verify = await shared.OpenSessionAsync().ConfigureAwait(false);
        List<CfTenantEntity> finalRows = await verify.From<CfTenantEntity>().ToListAsync().ConfigureAwait(false);
        await Assert.That(finalRows.Count).IsEqualTo(Workers);
        List<CfTenantEntity> wrong = finalRows
            .Where(row => row.Value != Rounds.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        await Assert.That(wrong.Count).IsEqualTo(0);
    }

    private static async Task WriteOwnRowAsync(
        DataSession<SqliteProvider> session, int worker, int round, CancellationToken ct)
    {
        long ownId = worker + 1;
        string value = round.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await session.ExecuteAsync($"UPDATE cf_tenant SET value = {value} WHERE id = {ownId}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>读 4 个相邻 worker 的行，值只能是本轮或上一轮（屏障保证没人跑到下一轮）。</summary>
    private static async Task CheckNeighbourRowsAsync(
        DataSession<SqliteProvider> session,
        int worker,
        int round,
        ConcurrentBag<string> violations,
        CancellationToken ct)
    {
        // PALORM005：4 次点查各自命中不同的 key，是「跨 worker 读」的被测行为本身，不是 N+1
#pragma warning disable PALORM005
        for (int offset = 1; offset <= ReadsPerRound; offset++)
        {
            long otherId = (worker + offset) % Workers + 1;
            CfTenantEntity? other = await session.GetAsync<CfTenantEntity>(otherId, ct).ConfigureAwait(false);
            if (other is null)
            {
                violations.Add($"worker={worker} round={round} 读 id={otherId} 为空");
                continue;
            }
            long value = long.Parse(other.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (value != round - 1 && value != round)
            {
                violations.Add(
                    $"worker={worker} round={round} 读到 id={otherId} 的值 {value}，期望 {round - 1} 或 {round}");
            }
        }
#pragma warning restore PALORM005
    }

    /// <summary>异步屏障：<c>System.Threading.Barrier</c> 是同步阻塞，在 async worker 里会占住
    /// 线程池线程；本类用 TCS 实现同语义的异步版本，最后到达者复位并放行全部等待者。</summary>
    private sealed class AsyncBarrier
    {
        private readonly Lock _lock = new();
        private readonly int _participants;
        private TaskCompletionSource _current = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining;

        internal AsyncBarrier(int participants)
        {
            _participants = participants;
            _remaining = participants;
        }

        internal async Task SignalAndWaitAsync(CancellationToken ct = default)
        {
            Task wait;
            lock (_lock)
            {
                if (--_remaining == 0)
                {
                    TaskCompletionSource released = _current;
                    _current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _remaining = _participants;
                    released.SetResult();
                    return;
                }
                wait = _current.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
