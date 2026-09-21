using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;

namespace PalORM.PerfHub;

/// <summary>一次测量的原始结果——三类核心指标（时延 / 峰值内存 / 并发吞吐）。</summary>
internal sealed class Measurement
{
    public required string Dialect { get; init; }
    public required string Implementation { get; init; }
    public required string Operation { get; init; }
    public required int Rows { get; init; }

    // ── 指标 1：时延（单操作，多次取中位数）──
    /// <summary>每次操作的耗时中位数（纳秒）。</summary>
    public double MedianNs { get; set; }
    /// <summary>每次操作的耗时均值（纳秒）——BDN 口径的 Mean。</summary>
    public double MeanNs { get; set; }
    /// <summary>Error/Mean（BDN 的 StdErr/Mean）——量具自检指标，>5% 标黄。</summary>
    public double ErrorRatio { get; set; }
    public int Iterations { get; set; }

    // ── 指标 2：内存（每次操作的分配 + 峰值）──
    /// <summary>每次操作的托管分配字节数（GC.GetTotalAllocatedBytes 口径）。</summary>
    public double AllocatedBytesPerOp { get; set; }
    /// <summary>该操作的托管堆峰值字节（GC.GetGCMemoryInfo 口径，采样于操作后）。</summary>
    public long PeakHeapBytes { get; set; }
    /// <summary>Gen0 回收次数（操作期间）。</summary>
    public int Gen0Collections { get; set; }

    // ── 指标 3：并发吞吐 ──
    /// <summary>多线程混合负载下的 ops/s（0 = 未测该项）。</summary>
    public double OpsPerSecond { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public int ConcurrencyThreads { get; set; }
}

/// <summary>测量引擎——统一口径的三类指标采集。
///
/// 口径纪律（对应 docs/性能基准规范.md §3/§4）：
///  · 时延：预热后测 N 轮，取中位数（分配是确定性指标，耗时比同轮对照中位数之比）；
///  · 分配：GC.GetTotalAllocatedBytes 精确计数（确定性，复现性优于 1%）；
///  · 峰值内存：GC.GetGCMemoryInfo().HeapSizeBytes 采样——注意它是"当前堆大小"而非
///    "操作期间曾达到的最大值"，.NET 无后者 API，故报告里明确标注为「采样堆峰值」；
///  · 并发：每线程独立连接，预热 1.5s 不计数，近邻秩分位数。
/// </summary>
internal static class Measure
{
    /// <summary>测单操作：时延中位数 + 每次分配 + 采样堆峰值 + Gen0 次数。</summary>
    public static async Task<Measurement> SingleAsync(
        DialectInfo dialect, IPerfImplementation impl, string operation, int rows,
        Func<IPerfImplementation, DbConnection, Task> action,
        DbConnection conn, int iterations, CancellationToken ct)
    {
        // 预热（JIT + 驱动缓冲 + 缓存填充）——不计入样本
        for (int i = 0; i < Math.Max(3, iterations / 5); i++)
            await action(impl, conn).ConfigureAwait(false);

        var samples = new List<double>(iterations);
        int gen0Before = GC.CollectionCount(0);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocBefore = GC.GetTotalAllocatedBytes(true);
        long peak = 0;
        var sw = new Stopwatch();

        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            await action(impl, conn).ConfigureAwait(false);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
            long heap = GC.GetGCMemoryInfo().HeapSizeBytes;
            if (heap > peak) peak = heap;
        }

        long allocAfter = GC.GetTotalAllocatedBytes(true);
        int gen0 = GC.CollectionCount(0) - gen0Before;

        samples.Sort();
        double median = samples[samples.Count / 2];
        double mean = samples.Average();
        double stdErr = samples.Count > 1 ? StdDev(samples) / Math.Sqrt(samples.Count) : 0;

        return new Measurement
        {
            Dialect = dialect.DisplayName,
            Implementation = impl.Name,
            Operation = operation,
            Rows = rows,
            MedianNs = median * 1_000_000,
            MeanNs = mean * 1_000_000,
            ErrorRatio = mean > 0 ? stdErr / mean : 0,
            Iterations = iterations,
            AllocatedBytesPerOp = (allocAfter - allocBefore) / (double)iterations,
            PeakHeapBytes = peak,
            Gen0Collections = gen0
        };
    }

    /// <summary>测并发吞吐：每线程独立连接，80/20 读写混合，预热不计数。</summary>
    public static async Task<Measurement> ConcurrentAsync(
        DialectInfo dialect, IPerfImplementation impl, int rows, int threads,
        double seconds, double writeRatio, CancellationToken ct)
    {
        var cs = Connections.Resolve(dialect);
        var conns = new DbConnection[threads];
        var samples = new ConcurrentQueue<double>[threads];
        try
        {
            // 每线程独立连接 + 轻量查询确保物理连接真实建立（消除池生长噪声）
            for (int t = 0; t < threads; t++)
            {
                conns[t] = dialect.OpenConnection(cs);
                await conns[t].OpenAsync(ct).ConfigureAwait(false);
                await impl.GetByKeyAsync(conns[t], 1, ct).ConfigureAwait(false);
            }

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

            // 预热 1.5s（规范 §4：无预热的数字不得发布）
            using (var warmup = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
            {
                await RunWorkers(conns, samples, threads, rows, writeRatio, impl,
                    warmup.Token).ConfigureAwait(false);
            }
            Array.Clear(samples);

            var clock = Stopwatch.StartNew();
            await RunWorkers(conns, samples, threads, rows, writeRatio, impl, stop.Token).ConfigureAwait(false);
            clock.Stop();

            // 过滤空队列：worker 在取消路径提前 return 时该线程可能零样本
            var all = samples.Where(static q => q is not null && !q.IsEmpty)
                .SelectMany(static q => q!).ToArray();
            if (all.Length == 0)
                throw new InvalidOperationException(
                    $"并发测量未采到样本（threads={threads}, rows={rows}）——所有 worker 均在取消路径退出。");
            Array.Sort(all);
            return new Measurement
            {
                Dialect = dialect.DisplayName,
                Implementation = impl.Name,
                Operation = "Concurrent_Mixed80_20",
                Rows = rows,
                OpsPerSecond = all.Length / clock.Elapsed.TotalSeconds,
                P50Ms = Pct(all, 0.50) / 1000.0,
                P95Ms = Pct(all, 0.95) / 1000.0,
                P99Ms = Pct(all, 0.99) / 1000.0,
                ConcurrencyThreads = threads,
                Iterations = all.Length,
                MedianNs = Pct(all, 0.50) * 1_000_000,
                MeanNs = all.Average() * 1_000_000,
                ErrorRatio = all.Length > 1 ? StdDev(all) / Math.Sqrt(all.Length) / all.Average() : 0,
                AllocatedBytesPerOp = 0,   // 并发态分配计数被多线程干扰，不测（B74）
                PeakHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes
            };
        }
        finally
        {
            foreach (var c in conns)
                if (c is not null) await c.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunWorkers(
        DbConnection[] conns, ConcurrentQueue<double>[] samples, int threads, int rows,
        double writeRatio, IPerfImplementation impl, CancellationToken stop)
    {
        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int ti = t;
            samples[ti] = new ConcurrentQueue<double>();
            tasks[t] = Task.Run(() => Worker(conns[ti], samples[ti], ti, rows, writeRatio, impl, stop));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task Worker(
        DbConnection conn, ConcurrentQueue<double> samples, int threadIndex, int rows,
        double writeRatio, IPerfImplementation impl, CancellationToken stop)
    {
        // 按线程号确定性播种——操作序列可复现，且与被测系统行为无耦合
        var rnd = new Random(42 + threadIndex);
        var sw = new Stopwatch();
        while (!stop.IsCancellationRequested)
        {
            long id = rnd.NextInt64(1, rows + 1);
            bool write = rnd.NextDouble() < writeRatio;
            sw.Restart();
            try
            {
                if (write)
                {
                    var row = Dataset.Seed(id - 1);
                    await impl.UpdateAsync(conn, row, stop).ConfigureAwait(false);
                }
                else
                {
                    _ = await impl.GetByKeyAsync(conn, id, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // 测量窗口关闭——优雅退出，不把取消当失败
                return;
            }
            sw.Stop();
            samples.Enqueue(sw.Elapsed.TotalMilliseconds * 1000);   // 微秒
        }
    }

    private static double Pct(double[] sorted, double p)
        => sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * p)];

    private static double StdDev(ICollection<double> values)
    {
        double mean = values.Average();
        double sum = 0;
        foreach (double v in values) sum += (v - mean) * (v - mean);
        return Math.Sqrt(sum / values.Count);
    }
}
