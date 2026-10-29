using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>C 层并发浸泡（方案 §6.4 / §6.5）。
/// <para>八线程各做"租出、操作、归还"，连续 3 轮、每轮窗口 30 秒（方案 §6.4 的 30 到 60 秒下沿）。
/// 每轮用独立库与会话池，轮间不共享状态；四条断言逐轮核对：同一会话同一时刻只在一个线程手里、
/// 租期写入的租户态不跨租期可见、IdleCount 回落到容量上界、操作数不低于有效负载地板。</para>
/// <para>长稳压测属 PerfHub 职责，本层只抓串租与状态污染，因此窗口取短并在三轮内保持同一形态。
/// 调试时可用 <c>PALORM_SOAK_SECONDS</c> 缩短窗口，默认 30 秒。</para></summary>
public sealed class ConcurrencySoakTests
{
    private const int WorkerCount = 8;
    private const int Rounds = 3;
    private const long MinimumOperationsPerRound = 100;

    private static readonly TimeSpan RoundWindow = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("PALORM_SOAK_SECONDS"), out int seconds) && seconds > 0
            ? seconds
            : 30);

    [Test]
    public async Task Soak_ThreeConsecutiveRounds_ZeroInvariantViolations()
    {
        var roundSummaries = new List<string>();
        var violations = new List<string>();

        for (int round = 1; round <= Rounds; round++)
        {
            (long operations, long idleAfterRound, IReadOnlyList<string> roundViolations) =
                await RunRoundAsync(round);

            roundSummaries.Add($"第 {round} 轮: 操作 {operations} 次，归还后 IdleCount={idleAfterRound}");
            violations.AddRange(roundViolations.Select(v => $"[第 {round} 轮] {v}"));

            if (operations < MinimumOperationsPerRound)
                violations.Add($"[第 {round} 轮] 操作数 {operations} 低于有效负载地板 {MinimumOperationsPerRound}");
            if (idleAfterRound != 4)
                violations.Add($"[第 {round} 轮] 归还后 IdleCount={idleAfterRound}，期望容量上界 4");
        }

        // 先报三轮的违规明细，再核"确实跑满 3 轮"
        await Assert.That(string.Join("\n", violations.Take(10))).IsEmpty();
        await Assert.That(roundSummaries.Count).IsEqualTo(Rounds);
    }

    private static async Task<(long Operations, long IdleAfterRound, IReadOnlyList<string> Violations)>
        RunRoundAsync(int round)
    {
        await using SharedSqliteDb shared = await CreateSeededAsync($"soak_round_{round}");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 4);

        var holder = new ConcurrentDictionary<int, int>();
        var violations = new ConcurrentBag<string>();
        long[] operations = [0];

        using var window = new CancellationTokenSource(RoundWindow);
        Task[] workers =
        [
            .. Enumerable.Range(0, WorkerCount).Select(workerId
                => Task.Run(() => RunWorkerAsync(pool, holder, violations, operations, workerId, window.Token), window.Token))
        ];
        await Task.WhenAll(workers);

        return (Interlocked.Read(ref operations[0]), pool.IdleCount, [.. violations]);
    }

    private static async Task<SharedSqliteDb> CreateSeededAsync(string name)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name);
        try
        {
            await using DataSession<SqliteProvider> seed = await shared.OpenSessionAsync();
            // 每线程一个租户、各一行：租户态若串，查询会读到别的租户的行
            CfTenantEntity[] rows =
            [
                .. Enumerable.Range(1, WorkerCount)
                    .Select(tenant => new CfTenantEntity { TenantId = tenant, Value = $"t{tenant}" })
            ];
            await seed.BulkInsertAsync(rows);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static async Task RunWorkerAsync(
        DataSessionPool<SqliteProvider> pool,
        ConcurrentDictionary<int, int> holder,
        ConcurrentBag<string> violations,
        long[] operations,
        int workerId,
        CancellationToken ct)
    {
        long tenant = workerId + 1L;
        while (!ct.IsCancellationRequested)
        {
            DataSession<SqliteProvider> session;
            try
            {
                session = await pool.RentAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            int hashCode = RuntimeHelpers.GetHashCode(session);
            if (!holder.TryAdd(hashCode, workerId))
            {
                holder.TryGetValue(hashCode, out int other);
                violations.Add($"串租: 会话 {hashCode} 同时被线程 {other} 与 {workerId} 持有");
            }

            try
            {
                await ProbeTenantIsolationAsync(session, tenant, violations, workerId, ct);
                Interlocked.Increment(ref operations[0]);
            }
            catch (OperationCanceledException)
            {
                // 浸泡窗口结束：正常退出路径
            }
            catch (Exception ex)
            {
                violations.Add($"操作异常: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                holder.TryRemove(hashCode, out _);
                await session.DisposeAsync();
            }
        }
    }

    private static async Task ProbeTenantIsolationAsync(
        DataSession<SqliteProvider> session,
        long tenant,
        ConcurrentBag<string> violations,
        int workerId,
        CancellationToken ct)
    {
        session.WithTenant(tenant);
        List<CfTenantEntity> rows = await session.From<CfTenantEntity>().ToListAsync(ct);
        if (rows.Count != 1 || rows[0].TenantId != tenant)
        {
            violations.Add(
                $"租期隔离: 线程 {workerId} 期望租户 {tenant} 一行，实得 {rows.Count} 行"
                + (rows.Count > 0 ? $"，首行租户 {rows[0].TenantId}" : ""));
        }
    }
}