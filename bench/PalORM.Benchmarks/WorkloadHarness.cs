using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ─────────────────────────────────────────────────────────────────────────────
// 并发负载测试（维度 3：延迟分布 p50/p95/p99；维度 4：并发扩展曲线；维度 11：混合读写）。
// 规范依据：docs/性能基准规范.md §1/§4——并行场景只做采样统计，不做墙钟断言。
//
// 与 BDN 的分工：BDN 是单线程微基准，并发行为（锁、池、调度）必须用负载测试；
// 采样用每操作 Stopwatch（与 wrk/hey 同口径），预热不计数。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>负载测试参数（环境变量可覆盖，缺省值即规范档位）。</summary>
internal sealed record WorkloadOptions
{
    /// <summary>种子行数（标准档位，见规范 §2）。</summary>
    public long Rows { get; init; } = 10_000;

    /// <summary>线程档位（并发扩展曲线的横轴）。</summary>
    public int[] ThreadTiers { get; init; } = [1, 2, 4, 8];

    /// <summary>每档测量时长（秒）。预热另有 1.5 s，不计数。</summary>
    public int SecondsPerTier { get; init; } = 3;

    /// <summary>读写混合比：写操作占比（默认 20%——读多写少的典型业务形态）。</summary>
    public double WriteRatio { get; init; } = 0.20;
}

internal static class WorkloadHarness
{
    [SuppressMessage("Globalization", "CA1303",
        Justification = "负载测试的表格输出是机器可读报告，固定文本。")]
    public static async Task RunAsync(WorkloadOptions options)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"palorm-workload-{Environment.ProcessId}.db");
        var sessionOptions = new DbOptions { ConnectionString = $"Data Source={dbPath}" };
        var allTiers = new List<WorkloadTierResult>(options.ThreadTiers.Length);
        try
        {
            await using DataSession<SqliteProvider> setup = await DataSession<SqliteProvider>.CreateAsync(sessionOptions).ConfigureAwait(false);
            // WAL：允许并发读与单写交叠（SQLite 默认 journal 模式下写会阻塞全部读）
            await setup.ExecuteAsync(FormattableStringFactory.Create("PRAGMA journal_mode=WAL")).ConfigureAwait(false);
            await StandardShapes.SeedAsync<BenchNarrow>(setup, options.Rows).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[workload] 种子 S1 Narrow × {options.Rows:N0} 完成，开始负载测试"));

            foreach (int threads in options.ThreadTiers)
            {
                WorkloadTierResult result = await RunTierAsync(sessionOptions, threads, options).ConfigureAwait(false);
                allTiers.Add(result);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[workload] threads={threads,2}  ops/s={result.OpsPerSecond,9:N0}  " +
                    $"p50={result.P50,7:F2}ms  p95={result.P95,7:F2}ms  p99={result.P99,7:F2}ms  " +
                    $"samples={result.SampleCount,9:N0}"));
            }

            await File.WriteAllLinesAsync(
                Path.Combine(AppContext.BaseDirectory, $"workload-sqlite-{options.Rows}.json"),
                [WorkloadJson(allTiers, options)]).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[workload] 结果已写入 workload-sqlite-{options.Rows}.json"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            File.Delete(dbPath + "-wal");
            File.Delete(dbPath + "-shm");
        }
    }

    private static async Task<WorkloadTierResult> RunTierAsync(
        DbOptions options, int threads, WorkloadOptions workload)
    {
        // 每线程独立会话（真实业务的连接形态）；SQLite 文件库 + WAL 下并发读并行、写串行
        var sessions = new DataSession<SqliteProvider>[threads];
        var barriers = new Task[threads];
        var samplesByThread = new ConcurrentQueue<long>[threads];
        using var stopSignal = new CancellationTokenSource(TimeSpan.FromSeconds(workload.SecondsPerTier));

        for (int t = 0; t < threads; t++)
        {
            sessions[t] = await DataSession<SqliteProvider>.CreateAsync(options).ConfigureAwait(false);
        }

        try
        {
            // 预热 1.5 s（规范 §4：无预热的数字不得发布）
            var warmup = RunWorkers(sessions, samplesByThread, workload, TimeSpan.FromSeconds(1.5));
            await Task.WhenAll(warmup).ConfigureAwait(false);
            Array.Clear(samplesByThread);

            var clock = Stopwatch.StartNew();
            Task[] workers = RunWorkers(sessions, samplesByThread, workload, stopSignal.Token);
            await Task.WhenAll(workers).ConfigureAwait(false);
            clock.Stop();

            long[] all = [.. samplesByThread.SelectMany(static queue => queue)];
            Array.Sort(all);
            return new WorkloadTierResult(
                Threads: threads,
                SampleCount: all.Length,
                OpsPerSecond: (long)(all.Length / clock.Elapsed.TotalSeconds),
                P50: Percentile(all, 0.50),
                P95: Percentile(all, 0.95),
                P99: Percentile(all, 0.99));
        }
        finally
        {
            foreach (DataSession<SqliteProvider> session in sessions)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static Task[] RunWorkers(
        DataSession<SqliteProvider>[] sessions,
        ConcurrentQueue<long>[] samplesByThread,
        WorkloadOptions workload,
        TimeSpan duration)
        => RunWorkers(sessions, samplesByThread, workload, new CancellationTokenSource(duration).Token);

    private static Task[] RunWorkers(
        DataSession<SqliteProvider>[] sessions,
        ConcurrentQueue<long>[] samplesByThread,
        WorkloadOptions workload,
        CancellationToken stop)
    {
        var tasks = new Task[sessions.Length];
        for (int t = 0; t < sessions.Length; t++)
        {
            int threadIndex = t;
            samplesByThread[threadIndex] = new ConcurrentQueue<long>();
            tasks[t] = Task.Run(() => WorkerLoop(
                sessions[threadIndex], samplesByThread[threadIndex], threadIndex, workload, stop));
        }
        return tasks;
    }

    /// <summary>混合负载工作循环：80% 主键点读 + 20% 定点更新（规范 §1 维度 11 的 80/20 形态）。
    /// 随机数按线程号确定性播种——每次运行的操作序列相同（可复现），但与被测系统行为无耦合。</summary>
    private static async Task WorkerLoop(
        DataSession<SqliteProvider> session,
        ConcurrentQueue<long> samples,
        int threadIndex,
        WorkloadOptions workload,
        CancellationToken stop)
    {
        var random = new Random(42 + threadIndex);
        long maxId = workload.Rows;
        var stopwatch = new Stopwatch();
        while (!stop.IsCancellationRequested)
        {
            long id = random.NextInt64(1, maxId + 1);
            bool write = random.NextDouble() < workload.WriteRatio;
            stopwatch.Restart();
            if (write)
            {
                await session.ExecuteAsync(
                    FormattableStringFactory.Create($"UPDATE bench_s1_narrow SET qty = qty + 1 WHERE \"Id\" = {id}"))
                    .ConfigureAwait(false);
            }
            else
            {
                _ = await session.From<BenchNarrow>().Where($"\"Id\" = {id}").FirstOrDefaultAsync()
                    .ConfigureAwait(false);
            }
            stopwatch.Stop();
            samples.Enqueue(stopwatch.Elapsed.TotalMilliseconds switch { var ms => (long)(ms * 1000) });
        }
    }

    /// <summary>近邻秩分位数（样本已排序，微秒）。</summary>
    private static double Percentile(long[] sortedMicros, double percentile)
    {
        if (sortedMicros.Length == 0)
        {
            return 0;
        }
        int rank = (int)Math.Ceiling(percentile * sortedMicros.Length);
        return sortedMicros[Math.Min(rank, sortedMicros.Length) - 1] / 1000.0;
    }

    private static string WorkloadJson(IReadOnlyList<WorkloadTierResult> tiers, WorkloadOptions options)
    {
        // schema 2 草案（规范 §6）——先落盘积累噪声底，门禁暂不卡分位数
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{{\"schema\":2,\"shape\":\"S1\",\"rows\":{options.Rows},");
        sb.Append(CultureInfo.InvariantCulture, $"\"write_ratio\":{options.WriteRatio.ToString(CultureInfo.InvariantCulture)},");
        sb.Append(CultureInfo.InvariantCulture, $"\"date\":\"{DateTime.UtcNow:yyyy-MM-dd}\",\"tiers\":[");
        for (int i = 0; i < tiers.Count; i++)
        {
            WorkloadTierResult tier = tiers[i];
            sb.Append(CultureInfo.InvariantCulture,
                $"{{\"threads\":{tier.Threads},\"ops_per_second\":{tier.OpsPerSecond}," +
                $"\"p50_ms\":{tier.P50.ToString("F3", CultureInfo.InvariantCulture)}," +
                $"\"p95_ms\":{tier.P95.ToString("F3", CultureInfo.InvariantCulture)}," +
                $"\"p99_ms\":{tier.P99.ToString("F3", CultureInfo.InvariantCulture)}}}");
            if (i < tiers.Count - 1)
            {
                sb.Append(',');
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }
}

/// <summary>单档位结果——时间单位：ms（由微秒样本换算）。</summary>
internal sealed record WorkloadTierResult(
    int Threads,
    long SampleCount,
    long OpsPerSecond,
    double P50,
    double P95,
    double P99);
