using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;

namespace PalORM.PerfHub;

/// <summary>一次测量的完整结果。</summary>
internal sealed class Measurement
{
    public required string Dialect { get; init; }
    public required string Implementation { get; init; }
    /// <summary>测试项标识（如 GetByKey / BulkInsert / Tx_TenInserts）。</summary>
    public required string Operation { get; init; }
    /// <summary>测试项分组（Build / CRUD / Query / Bulk / Transaction / Baseline）。</summary>
    public required string Group { get; init; }
    public required int Rows { get; init; }

    // ── 指标 1：全路径时延 ──
    /// <summary>时延测量区间为「SQL 构建开始 → 构建完成 → 执行 → 测试完成」的全路径，
    /// 不含测试夹具自身的数据准备（种子数据在测量前生成）。</summary>
    /// <summary>每次操作的全路径耗时中位数（纳秒）。</summary>
    public double MedianNs { get; set; }
    public double MeanNs { get; set; }
    /// <summary>Error/Mean（BDN 的 StdErr/Mean）——量具自检指标，>5% 标黄。</summary>
    public double ErrorRatio { get; set; }
    public int Iterations { get; set; }

    // ── 指标 2：内存 ──
    /// <summary>每次操作的托管分配字节数（GC.GetTotalAllocatedBytes 精确计数，确定性指标）。</summary>
    public double AllocatedBytesPerOp { get; set; }
    /// <summary>操作期间的托管堆峰值（采样近似）——含 ORM 内部与数据对象的真实驻留。</summary>
    public long PeakHeapBytes { get; set; }
    /// <summary>操作结束后的堆大小（保留量）。</summary>
    public long LiveHeapAfterBytes { get; set; }
    public int Gen0Collections { get; set; }

    // ── 指标 3：并发吞吐 ──
    public double OpsPerSecond { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public int ConcurrencyThreads { get; set; }

    // ── 指标 4：往返与语句效率（维度 8，规范 §1）──
    /// <summary>每操作命令执行次数（往返次数）。由 CountingConnection 装饰器实测，
    /// 非"按实现形态声明"——它能抓到意外 N+1（Dapper 的 multi-exec 会如实计入）。</summary>
    public double RoundTripsPerOp { get; set; }

    /// <summary>prepared 复用率 = Prepare 调用次数 / 执行次数（0 表示从不 prepare）。</summary>
    public double PreparedReuse { get; set; }
}

/// <summary>峰值内存采样器——后台线程高频轮询堆大小，取操作期间的最大值。
/// <para><b>为什么需要采样</b>：.NET 不提供「操作期间曾达到的堆峰值」API，
/// <see cref="GC.GetGCMemoryInfo()"/> 只返回当前堆大小。故用独立线程高频采样取上界。</para>
/// <para><b>口径诚实声明</b>：采样间隔内的瞬时尖峰可能被漏采，故报告标注为「采样堆峰」。
/// 与之互补的 <see cref="GC.GetTotalAllocatedBytes(bool)"/> 是精确分配量（确定性、可复现），
/// 两者共同刻画内存：前者是驻留峰值，后者是分配吞吐。</para></summary>
internal sealed class PeakSampler : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private long _peak;   // volatile 语义经 Volatile.Read/Write 保证
    private volatile bool _running;

    /// <summary>采样间隔 100µs——在「不漏太多尖峰」与「采样器自身开销可忽略」之间平衡，
    /// 毫秒级操作的采样点约 10~50 个。</summary>
    private const int SampleIntervalUs = 100;

    public long Peak => Volatile.Read(ref _peak);

    private PeakSampler()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "PerfHub-PeakSampler" };
    }

    public static PeakSampler Start()
    {
        var sampler = new PeakSampler { _running = true };
        sampler._thread.Start();
        return sampler;
    }

    private void Loop()
    {
        // 先取一次基线，避免把「操作前就存在的堆」算进来
        _peak = GC.GetGCMemoryInfo().HeapSizeBytes;
        var sw = new Stopwatch();
        while (_running)
        {
            sw.Restart();
            long heap = GC.GetGCMemoryInfo().HeapSizeBytes;
            if (heap > _peak)
            {
                Volatile.Write(ref _peak, heap);
            }

            while (sw.Elapsed.TotalMilliseconds * 1000 < SampleIntervalUs && _running)
            {
                Thread.SpinWait(20);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _stop.Set();
        _thread.Join(200);
        _stop.Dispose();
    }
}

/// <summary>测量引擎——统一口径的三类指标采集。</summary>
internal static class Measure
{
    /// <summary>预热的时间预算（秒）——与规范 §4「负载测试每档 ≥1.5 s 预热不计数」同值。
    /// 快操作在达到上限次数前就用满这个预算（行为与固定次数一致），慢操作在此收敛。</summary>
    private const double WarmupBudgetSeconds = 1.5;

    /// <summary>测单操作：全路径时延（构建→执行→完成）+ 分配量 + 采样堆峰 + Gen0。
    /// <para><b>全路径口径</b>：被测 <paramref name="action"/> 内部必须包含 SQL 构建
    /// （From&lt;T&gt;()/Where/BuildSql）→ 执行 → 物化的完整链路；测量引擎不额外剥离任何段。</para>
    /// <para><b>prepare 语义</b>：在预热前与计时循环前各调用一次，用于把库重置到确定状态
    /// （写操作每轮要用不同主键，否则第二轮撞主键；删操作每轮要有行可删）。
    /// prepare 不计入时延与分配。</para></summary>
    public static async Task<Measurement> SingleAsync(
        DialectInfo dialect, IPerfImplementation impl, string operation, string group, int rows,
        Func<IPerfImplementation, DbConnection, int, Task> action,
        DbConnection conn, int maxIterations, CancellationToken ct,
        Func<DbConnection, Task>? prepare = null, double budgetSeconds = 1.5)
    {
        // 预热（JIT + 驱动缓冲 + 缓存填充）——不计入样本
        if (prepare is not null)
        {
            await prepare(conn).ConfigureAwait(false);
        }

        // 预热次数按**时间**收敛：上限仍是 maxIterations/5，但累计耗时达 WarmupBudgetSeconds
        // 即停（至少 3 次）。固定次数对慢操作是无界成本——实测 20K 档 Dapper BulkInsert
        // 单次 1.36 s，40 次预热 54 s，是计时段（4 s 预算 → 3 次 ≈ 4.1 s）的 13 倍；
        // 而预热的目的（JIT、驱动缓冲、语句缓存）在前几次即达成，规范 §4 也只要求
        // 「≥1.5 s 预热不计数」。
        int warmupCap = Math.Max(3, maxIterations / 5);
        var warmupSw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < warmupCap; i++)
        {
            await action(impl, conn, i).ConfigureAwait(false);
            if (i >= 2 && warmupSw.Elapsed.TotalSeconds >= WarmupBudgetSeconds)
            {
                break;
            }
        }

        // 预热已改变库状态（插入/删除），计时前再重置一次，保证每轮起点一致
        if (prepare is not null)
        {
            await prepare(conn).ConfigureAwait(false);
        }

        // 阶段 3.1 自适应迭代：重置后用一次探针（i=0）测单次耗时，迭代数收敛到时间预算。
        // 探针占用 i=0 的主键段，计时轮从 i=1 起——写操作的每轮主键互不重叠
        //（探针在重置前跑会与预热轮撞主键，实测 Insert 类 UNIQUE 冲突）。
        var probe = System.Diagnostics.Stopwatch.StartNew();
        await action(impl, conn, 0).ConfigureAwait(false);
        probe.Stop();
        double perOpSeconds = Math.Max(probe.Elapsed.TotalSeconds, 1e-7);
        int iterations = Math.Clamp(
            (int)(budgetSeconds / perOpSeconds), 3, Math.Max(3, maxIterations));

        var samples = new List<double>(iterations);
        int gen0Before = GC.CollectionCount(0);
        long peakSeen;
        long liveAfter;

        // 分配量用「整轮一次计数 / 轮数」——比每次前后计数更稳（避免单次测量被 GC 干扰）
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocBefore = GC.GetTotalAllocatedBytes(true);

        // 维度 8：往返次数与 prepared 复用同样按「整轮一次计数 / 轮数」——连接被装饰时才计
        long execBefore = 0, prepareBefore = 0;
        bool counting = conn is CountingConnection;
        if (conn is CountingConnection cc0)
        {
            execBefore = cc0.Executes;
            prepareBefore = cc0.Prepares;
        }

        using (var sampler = PeakSampler.Start())
        {
            var sw = new Stopwatch();
            for (int i = 1; i <= iterations; i++)
            {
                sw.Restart();
                await action(impl, conn, i).ConfigureAwait(false);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            peakSeen = sampler.Peak;
            liveAfter = GC.GetGCMemoryInfo().HeapSizeBytes;
        }

        long allocAfter = GC.GetTotalAllocatedBytes(true);
        int gen0 = GC.CollectionCount(0) - gen0Before;

        double roundTrips = 0, preparedReuse = 0;
        if (counting && conn is CountingConnection cc1)
        {
            long execs = cc1.Executes - execBefore;
            roundTrips = execs / (double)iterations;
            preparedReuse = execs == 0 ? 0 : (cc1.Prepares - prepareBefore) / (double)execs;
        }

        samples.Sort();
        double median = samples[samples.Count / 2];
        double mean = samples.Average();
        double stdErr = samples.Count > 1 ? StdDev(samples) / Math.Sqrt(samples.Count) : 0;

        return new Measurement
        {
            Dialect = dialect.DisplayName,
            Implementation = impl.Name,
            Operation = operation,
            Group = group,
            Rows = rows,
            MedianNs = median * 1_000_000,
            MeanNs = mean * 1_000_000,
            ErrorRatio = mean > 0 ? stdErr / mean : 0,
            Iterations = iterations,
            AllocatedBytesPerOp = (allocAfter - allocBefore) / (double)iterations,
            PeakHeapBytes = peakSeen,
            LiveHeapAfterBytes = liveAfter,
            Gen0Collections = gen0,
            RoundTripsPerOp = roundTrips,
            PreparedReuse = preparedReuse
        };
    }

    /// <summary>测量「数据生成」本身的成本——不涉及数据库，纯粹是构造 N 个实体对象。
    /// 这一项与实现无关（三实现相同），作为内存基线登记：它回答「测试数据自身的真实内存占用」。</summary>
    public static Measurement MeasureDataGeneration(int rows, int iterations = 20)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocBefore = GC.GetTotalAllocatedBytes(true);
        long peak;
        using (var sampler = PeakSampler.Start())
        {
            for (int i = 0; i < iterations; i++)
            {
                _ = Dataset.SeedRows(rows);
            }

            peak = sampler.Peak;
        }
        long allocAfter = GC.GetTotalAllocatedBytes(true);
        return new Measurement
        {
            Dialect = "—",
            Implementation = "DataGen",
            Operation = "GenerateRows",
            Group = "Baseline",
            Rows = rows,
            MedianNs = 0,
            MeanNs = 0,
            ErrorRatio = 0,
            Iterations = iterations,
            AllocatedBytesPerOp = (allocAfter - allocBefore) / (double)iterations,
            PeakHeapBytes = peak,
            LiveHeapAfterBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
            Gen0Collections = 0
        };
    }

    /// <summary>测并发吞吐：每线程独立连接，80/20 读写混合，预热不计数。</summary>
    public static async Task<Measurement> ConcurrentAsync(
        DialectInfo dialect, IPerfImplementation impl, int rows, int threads,
        double seconds, double writeRatio, CancellationToken ct)
    {
        string cs = Connections.Resolve(dialect);
        var conns = new DbConnection[threads];
        var samples = new ConcurrentQueue<double>[threads];
        try
        {
            for (int t = 0; t < threads; t++)
            {
                // 维度 8：并发项也包计数装饰器（每线程一条，计数走 Interlocked，
                // 读取时跨线程求和）——否则 7 个并发项是维度 8 的覆盖缺口
                conns[t] = new CountingConnection(dialect.OpenConnection(cs));
                await conns[t].OpenAsync(ct).ConfigureAwait(false);
                await impl.GetByKeyAsync(conns[t], 1, ct).ConfigureAwait(false);
            }

            // 预热与计时的取消源必须分别构造：若 stop 在预热前就开始倒计时，预热一旦超过
            // seconds，计时窗口开跑时令牌已取消——每个 worker 第一次 await 就抛
            // OperationCanceledException 直接返回，采到 0 个样本（实测 8 线程 SQLite 踩中）。
            using (var warmup = new CancellationTokenSource(TimeSpan.FromSeconds(1.0)))
            {
                await RunWorkers(conns, samples, threads, rows, writeRatio, impl,
                    warmup.Token).ConfigureAwait(false);
            }
            Array.Clear(samples);

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var clock = Stopwatch.StartNew();
            await RunWorkers(conns, samples, threads, rows, writeRatio, impl, stop.Token).ConfigureAwait(false);
            clock.Stop();

            double[] all = [.. samples.Where(static q => q is not null && !q.IsEmpty).SelectMany(static q => q)];
            if (all.Length == 0)
            {
                throw new InvalidOperationException(
                    $"并发测量未采到样本（threads={threads}, rows={rows}）——所有 worker 均在取消路径退出。");
            }

            Array.Sort(all);

            // 维度 8：跨线程求和后按采样操作数摊平。分母用采样数而非执行数——
            // 取消路径上未完成的尝试不计入样本，故该值是"每个完成操作的往返数"上界。
            long executes = 0, prepares = 0;
            foreach (DbConnection c in conns)
            {
                if (c is CountingConnection cc)
                {
                    executes += cc.Executes;
                    prepares += cc.Prepares;
                }
            }

            return new Measurement
            {
                Dialect = dialect.DisplayName,
                Implementation = impl.Name,
                Operation = "Concurrent_Mixed80_20",
                Group = "Concurrency",
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
                PeakHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
                RoundTripsPerOp = executes / (double)all.Length,
                PreparedReuse = executes == 0 ? 0 : prepares / (double)executes
            };
        }
        finally
        {
            foreach (DbConnection? c in conns)
            {
                if (c is not null)
                {
                    await c.DisposeAsync().ConfigureAwait(false);
                }
            }
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
            // 令牌由 Worker 自行轮询（stop.IsCancellationRequested），不传给 Task.Run——
            // 传了会让已取消时的 Task.Run 直接返回已取消任务，worker 根本不启动。
            tasks[t] = Task.Run(
                () => Worker(conns[ti], samples[ti], ti, rows, writeRatio, impl, stop),
                CancellationToken.None);
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
                    await impl.UpdateAsync(conn, Dataset.Seed(id - 1), stop).ConfigureAwait(false);
                }
                else
                {
                    _ = await impl.GetByKeyAsync(conn, id, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;   // 测量窗口关闭——优雅退出
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
        foreach (double v in values)
        {
            sum += (v - mean) * (v - mean);
        }

        return Math.Sqrt(sum / values.Count);
    }
}
