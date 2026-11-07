using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>C 层并发浸泡（方案 §6.4 / §6.5）。
/// <para>八线程各做"租出、操作、归还"，连续 3 轮；四条断言逐轮核对：同一会话同一时刻只在一个
/// 线程手里、租期写入的租户态不跨租期可见、IdleCount 回落到容量上界、操作数不低于有效负载地板。</para>
/// <para><b>默认按迭代次数有界</b>（每 worker 每轮 100 次，3 轮共 2,400 次操作，约 2 秒）：
/// 四条判据都按操作计而不依赖实际耗时，按时间跑没有额外判别力，却让 PR 全量跑付 90 秒
/// （2026-11-07 实测占 Integration 总时长 64%）。需要时间维度长稳时用
/// <c>PALORM_SOAK_SECONDS=&lt;秒&gt;</c> 切换回时间窗口（每次操作数下限 100），
/// 混合读写长稳另见 <c>ConcurrencyMixedSoakTests</c>（<c>PALORM_CS22_ROUNDS</c>）。</para></summary>
public sealed class ConcurrencySoakTests
{
    private const int WorkerCount = 8;
    private const int Rounds = 3;
    private const long TimeBoundedMinimumOperations = 100;

    /// <summary>迭代模式下每 worker 每轮的次数；环境变量 <c>PALORM_SOAK_ITERATIONS</c> 可覆盖。</summary>
    private static readonly int IterationsPerWorker = ResolveIterations();

    /// <summary>时间模式窗口；null = 迭代模式（默认）。设置 <c>PALORM_SOAK_SECONDS</c> 即切换。</summary>
    private static readonly TimeSpan? RoundWindow = ResolveWindow();

    /// <summary>每轮操作数下限：迭代模式是确定值（worker × 迭代次数），时间模式保留经验地板。</summary>
    private static long MinimumOperationsPerRound
        => RoundWindow is null ? (long)WorkerCount * IterationsPerWorker : TimeBoundedMinimumOperations;

    private static int ResolveIterations()
        => int.TryParse(Environment.GetEnvironmentVariable("PALORM_SOAK_ITERATIONS"), out int configured)
           && configured > 0
            ? configured
            : 100;

    private static TimeSpan? ResolveWindow()
        => int.TryParse(Environment.GetEnvironmentVariable("PALORM_SOAK_SECONDS"), out int seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

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

        using var window = RoundWindow is { } roundWindow
            ? new CancellationTokenSource(roundWindow)
            : new CancellationTokenSource();
        int? maxIterations = RoundWindow is null ? IterationsPerWorker : null;
        Task[] workers =
        [
            .. Enumerable.Range(0, WorkerCount).Select(workerId
                => Task.Run(
                    () => RunWorkerAsync(pool, holder, violations, operations, workerId, maxIterations, window.Token),
                    window.Token))
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
        int? maxIterations,
        CancellationToken ct)
    {
        long tenant = workerId + 1L;
        int executed = 0;
        while (!ct.IsCancellationRequested && (maxIterations is null || executed < maxIterations))
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
            executed++;
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