using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>C 层并发浸泡（方案 §6.4）。
/// <para>8 个线程各做"租出、操作、归还"，持续 5 秒（方案上限 30 到 60 秒留给夜间长程；
/// PR 门禁取短程，因为要抓的是串租与状态污染，不是压力上限）。四条断言：同一会话同一时刻
/// 只在一个线程手里；租期写入的租户态不跨租期可见；IdleCount 回落到容量上界；无违规记录。</para></summary>
public sealed class ConcurrencySoakTests
{
    private const int WorkerCount = 8;
    private static readonly TimeSpan SoakWindow = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Soak_RentOperateReturn_PreservesFourInvariants()
    {
        await using SharedSqliteDb shared = await CreateSeededAsync();
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 4);

        var holder = new ConcurrentDictionary<int, int>();
        var violations = new ConcurrentBag<string>();
        int[] operations = [0];

        using var window = new CancellationTokenSource(SoakWindow);
        Task[] workers =
        [
            .. Enumerable.Range(0, WorkerCount).Select(workerId
                => Task.Run(() => RunWorkerAsync(pool, holder, violations, operations, workerId, window.Token), window.Token))
        ];

        await Task.WhenAll(workers);

        // 断言 3：归还全部完成，空闲数回落到容量上界（无泄漏）
        await Assert.That(pool.IdleCount).IsEqualTo(4);
        // 断言 1（串租）与断言 2（租期隔离）的汇总
        await Assert.That(string.Join(" | ", violations.Take(10))).IsEmpty();
        // 有效负载下限：确保浸泡真的跑了足够多轮，而不是被启动开销吞掉
        await Assert.That(operations[0]).IsGreaterThan(50);
    }

    private static async Task<SharedSqliteDb> CreateSeededAsync()
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("soak_rent");
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
        int[] operations,
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