using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ─────────────────────────────────────────────────────────────────────────────
// 长稳测试（维度 10：30 分钟级持续负载——抓内存泄漏/吞吐衰减/池耗尽，规范 §1）。
// 由 --stability <秒> 调用；全量 30 分钟档属 CI/手动（run-full-perf 不含），
// 本模式 3–5 分钟即可当衰减初筛。与 --workload 的分工：workload 测扩展曲线（每档
// 3 秒），stability 测时间维度的稳定性（单档长跑 + 区间采样）。
// ─────────────────────────────────────────────────────────────────────────────

internal static class StabilityHarness
{
    private sealed record StabilityOptions(int Seconds, int Threads, long Rows, double WriteRatio);

    [SuppressMessage("Globalization", "CA1303",
        Justification = "稳定性测试的控制台输出是机器可读报告，固定文本。")]
    public static async Task RunAsync(int seconds, string dialect)
    {
        var options = new StabilityOptions(seconds, Threads: 4, Rows: 10_000, WriteRatio: 0.20);
        string dbPath = Path.Combine(Path.GetTempPath(), $"palorm-stability-{Environment.ProcessId}.db");
        try
        {
            var sessionOptions = new DbOptions
            {
                ConnectionString = $"Data Source={dbPath}",
                MaxRetries = 0,
                CircuitBreakerThreshold = 0
            };
            await using DataSession<SqliteProvider> setup = await DataSession<SqliteProvider>.CreateAsync(sessionOptions).ConfigureAwait(false);
            await setup.ExecuteAsync(FormattableStringFactory.Create("PRAGMA journal_mode=WAL")).ConfigureAwait(false);
            await StandardShapes.SeedAsync<BenchNarrow>(setup, options.Rows).ConfigureAwait(false);
            await setup.DisposeAsync().ConfigureAwait(false);
            Log(string.Create(CultureInfo.InvariantCulture,
                $"[stability] 种子 S1 × {options.Rows:N0}，{options.Threads} 线程 × {options.Seconds}s，读写比 {1 - options.WriteRatio:P0}/{options.WriteRatio:P0}"));
            Log("[stability] 窗口=10s；输出：窗口 ops/s · p95 · 累计分配 MB · Gen0/1/2 · 工作集 MB");

            var sessions = new DataSession<SqliteProvider>[options.Threads];
            var queues = new ConcurrentQueue<long>[options.Threads];
            for (int t = 0; t < options.Threads; t++)
            {
                sessions[t] = await DataSession<SqliteProvider>.CreateAsync(sessionOptions).ConfigureAwait(false);
                _ = await sessions[t].ScalarAsync<long>(FormattableStringFactory.Create("SELECT 1")).ConfigureAwait(false);
                queues[t] = new ConcurrentQueue<long>();
            }

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
            Task[] workers = [.. queues.Select((queue, t) => Task.Run(() => StabilityWorker(
                sessions[t], queue, t, options, stop.Token)))];

            // 区间采样器：每 10s 汇总窗口样本与资源指标
            long totalAllocBase = GC.GetTotalAllocatedBytes(precise: true);
            var samplesPerWindow = new List<(int Window, long Ops, double P95Micros, double AllocMb, int Gen0, int Gen1, int Gen2, double WorkingSetMb)>();
            int windowCount = 0;
            int lastGen0 = GC.CollectionCount(0), lastGen1 = GC.CollectionCount(1), lastGen2 = GC.CollectionCount(2);
            var samplingStopwatch = Stopwatch.StartNew();
            void SampleWindow()
            {
                windowCount++;
                long windowOps = queues.Sum(static q => q.Count);
                long[] window = [.. queues.SelectMany(static q => q)];
                foreach (ConcurrentQueue<long> queue in queues) queue.Clear();
                double p95 = window.Length == 0 ? 0 : window.OrderBy(static v => v).ElementAt((int)(window.Length * 0.95)) / 1000.0;
                double allocMb = (GC.GetTotalAllocatedBytes(precise: true) - totalAllocBase) / 1048576.0;
                int gen0 = GC.CollectionCount(0) - lastGen0; lastGen0 += gen0;
                int gen1 = GC.CollectionCount(1) - lastGen1; lastGen1 += gen1;
                int gen2 = GC.CollectionCount(2) - lastGen2; lastGen2 += gen2;
                double workingSet = Environment.WorkingSet / 1048576.0;
                samplesPerWindow.Add((windowCount, windowOps, p95, allocMb, gen0, gen1, gen2, workingSet));
                Log(string.Create(CultureInfo.InvariantCulture,
                    $"[stability] w{windowCount,3}  ops={windowOps / 10.0,9:F0}/s  p95={p95,6:F3}ms  alloc={allocMb,8:F1}MB  gen0/1/2={gen0}/{gen1}/{gen2}  ws={workingSet,7:F0}MB"));
            }

            using var sampler = new Timer(_ => SampleWindow(), null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            await Task.WhenAll(workers).ConfigureAwait(false);
            samplingStopwatch.Stop();
            await sampler.DisposeAsync().ConfigureAwait(false);
            foreach (DataSession<SqliteProvider> session in sessions) await session.DisposeAsync().ConfigureAwait(false);

            ReportTrend(samplesPerWindow, totalAllocBase, options);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            File.Delete(dbPath + "-wal");
            File.Delete(dbPath + "-shm");
        }
    }

    /// <summary>衰减判定：前 1/3 vs 后 1/3 的窗口吞吐与工作集趋势——
    /// 吞吐跌 >20% 或工作集单调增 >30% 即标记（初筛阈值，非门禁）。</summary>
    private static void ReportTrend(
        IReadOnlyList<(int Window, long Ops, double P95Micros, double AllocMb, int Gen0, int Gen1, int Gen2, double WorkingSetMb)> samples,
        long allocBase, StabilityOptions options)
    {
        if (samples.Count < 3)
        {
            Log("[stability] 窗口数不足 3，趋势判定跳过（延长时长）");
            return;
        }
        int third = samples.Count / 3;
        double firstThird = samples.Take(third).Average(static s => (double)s.Ops);
        double lastThird = samples.Skip(samples.Count - third).Average(static s => (double)s.Ops);
        double throughputDelta = (lastThird - firstThird) / firstThird * 100;
        double wsFirst = samples.Take(third).Average(static s => s.WorkingSetMb);
        double wsLast = samples.Skip(samples.Count - third).Average(static s => s.WorkingSetMb);
        double wsDelta = (wsLast - wsFirst) / wsFirst * 100;

        Log(string.Create(CultureInfo.InvariantCulture,
            $"[stability] 趋势：前1/3 {firstThird:F0} ops/窗口 → 后1/3 {lastThird:F0}（{throughputDelta:+0.0;-0.0}%）· 工作集 {wsFirst:F0}→{wsLast:F0}MB（{wsDelta:+0.0;-0.0}%）"));
        string verdict = throughputDelta < -20
            ? "⚠ 吞吐衰减超 20%——需排查"
            : wsDelta > 30
                ? "⚠ 工作集增长超 30%——疑似泄漏，需排查"
                : "✓ 无衰减迹象（初筛）";
        Log($"[stability] 判定：{verdict}");
    }

    private static async Task StabilityWorker(
        DataSession<SqliteProvider> session,
        ConcurrentQueue<long> samples,
        int threadIndex,
        StabilityOptions options,
        CancellationToken stop)
    {
        var random = new Random(42 + threadIndex);
        string quotedId = SqliteProvider.QuoteIdentifier("Id");
        string quotedTable = SqliteProvider.QuoteIdentifier("bench_s1_narrow");
        var stopwatch = new Stopwatch();
        while (!stop.IsCancellationRequested)
        {
            long id = random.NextInt64(1, options.Rows + 1);
            bool write = random.NextDouble() < options.WriteRatio;
            stopwatch.Restart();
            if (write)
            {
                // 幂等 SET（不是 qty+1）：累加会让 WAL 无限增长——衰减来自负载自身状态
                // 而非被测系统；稳定性要的是平稳态（规范 §2 确定性种子同纪律）
                await session.ExecuteAsync(FormattableStringFactory.Create(
                    $"UPDATE {quotedTable} SET qty = {1} WHERE {quotedId} = {id}")).ConfigureAwait(false);
            }
            else
            {
                _ = await session.From<BenchNarrow>().Where($"\"Id\" = {id}").FirstOrDefaultAsync()
                    .ConfigureAwait(false);
            }
            stopwatch.Stop();
            samples.Enqueue((long)(stopwatch.Elapsed.TotalMilliseconds * 1000));
        }
    }

    private static void Log(string message) => Console.WriteLine(message);
}
