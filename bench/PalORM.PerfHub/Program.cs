using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalORM.PerfHub;

/// <summary>PerfHub 统一性能测试运行器。
///
/// 用法：
///   dotnet run --project bench/PalORM.PerfHub -- run [--dialects sqlite,mysql,pg]
///           [--tiers 100,1000,10000] [--iterations N] [--concurrency] [--threads 1,4,8]
///   dotnet run --project bench/PalORM.PerfHub -- report
///
/// 输出：
///   bench/perfhub/results/history-&lt;yyyyMMdd-HHmmss&gt;.json   每次运行的完整原始数据
///   bench/perfhub/results/latest.json                       最近一次（供回归对比）
///   bench/perfhub/report.html                              统一报告（含 SVG 增长曲线）
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "run" => await RunAsync(args[1..]).ConfigureAwait(false),
                "report" => Report.Build(),
                _ => Fail($"未知命令 '{args[0]}'")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PerfHub 失败: {ex}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            PerfHub — PalORM 统一性能测试系统

            命令:
              run       执行测试（三方言 × 三实现 × 统一数据集）
              report    仅从历史数据重新生成报告

            run 选项:
              --dialects sqlite,mysql,pg    方言子集（默认全部可用）
              --tiers 100,1000,10000        行数档位（默认 100,1000）
              --iterations N                单操作迭代次数（默认按操作类型自适应）
              --concurrency                 启用并发吞吐测试
              --threads 1,4,8               并发线程档位（默认 1,4,8）
              --label TEXT                  本次运行的标签（记入 JSON）

            环境变量:
              PALORM_BENCH_PG       PostgreSQL 连接串
              PALORM_BENCH_MYSQL    MySQL 连接串

            示例:
              dotnet run --project bench/PalORM.PerfHub -- run --dialects sqlite --tiers 1000
              dotnet run --project bench/PalORM.PerfHub -- run --concurrency --threads 1,4,8
            """);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"PerfHub: {message}");
        return 1;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var dialects = new List<Dialect> { Dialect.Sqlite, Dialect.MySql, Dialect.PostgreSql };
        var tiers = new List<int> { 100, 1_000 };
        bool concurrency = false;
        var threadTiers = new List<int> { 1, 4, 8 };
        int iterationsOverride = 0;
        string label = "";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dialects":
                    dialects = ParseList(args[++i], ParseDialect);
                    break;
                case "--tiers":
                    tiers = ParseList(args[++i], int.Parse);
                    break;
                case "--threads":
                    threadTiers = ParseList(args[++i], int.Parse);
                    break;
                case "--iterations":
                    iterationsOverride = int.Parse(args[++i]);
                    break;
                case "--concurrency":
                    concurrency = true;
                    break;
                case "--label":
                    label = args[++i];
                    break;
                default:
                    return Fail($"未知选项 '{args[i]}'");
            }
        }

        // 探测可用方言（PG/MySQL 需连接串）
        var available = new List<DialectInfo>();
        foreach (Dialect d in dialects)
        {
            var info = DialectInfo.Of(d);
            try
            {
                _ = Connections.Resolve(info);
                available.Add(info);
                Console.WriteLine($"[PerfHub] {info.DisplayName} 可用");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"[PerfHub] {info.DisplayName} 跳过 — {ex.Message}");
            }
        }
        if (available.Count == 0) return Fail("没有可用方言");

        var results = new List<Measurement>();
        var swTotal = Stopwatch.StartNew();

        foreach (var info in available)
        {
            Console.WriteLine();
            Console.WriteLine($"══════ {info.DisplayName} ══════");
            string cs = Connections.Resolve(info);
            await using var conn = info.OpenConnection(cs);
            await conn.OpenAsync().ConfigureAwait(false);

            // 三实现共用同一连接（B76：建连口径一致）
            IPerfImplementation[] impls = [new AdoNetImpl(info), new DapperImpl(info), new PalormImpl(info)];

            foreach (int rows in tiers)
            {
                Console.WriteLine();
                Console.WriteLine($"── 行数档位 {rows:N0} ──");

                foreach (var impl in impls)
                {
                    await RunOneImplAsync(info, impl, rows, conn, results, iterationsOverride)
                        .ConfigureAwait(false);
                }

                // 并发吞吐（三实现 × 线程档位）
                if (concurrency)
                {
                    Console.WriteLine();
                    Console.WriteLine("  并发吞吐（80/20 读写混合，3s）");
                    foreach (var impl in impls)
                    {
                        await impl.SetupAsync(conn, rows, CancellationToken.None).ConfigureAwait(false);
                        foreach (int threads in threadTiers)
                        {
                            var m = await Measure.ConcurrentAsync(info, impl, rows, threads, 3.0, 0.20,
                                CancellationToken.None).ConfigureAwait(false);
                            results.Add(m);
                            Console.WriteLine($"    {impl.Name,-9} {threads,2} 线程  {m.OpsPerSecond,10:N0} ops/s  "
                                + $"p50={m.P50Ms,6:F2}ms p95={m.P95Ms,6:F2}ms p99={m.P99Ms,6:F2}ms");
                        }
                    }
                }
            }

            await conn.CloseAsync().ConfigureAwait(false);
        }

        swTotal.Stop();
        Console.WriteLine();
        Console.WriteLine($"[PerfHub] 完成，共 {results.Count} 项测量，耗时 {swTotal.Elapsed.TotalMinutes:F1} 分钟");

        Save(results, label, swTotal.Elapsed);
        Report.Build();
        return 0;
    }

    /// <summary>对一个 (方言, 实现, 行数) 组合跑全部 7 个单操作测量。</summary>
    private static async Task RunOneImplAsync(
        DialectInfo info, IPerfImplementation impl, int rows, DbConnection conn,
        List<Measurement> results, int iterationsOverride)
    {
        await impl.SetupAsync(conn, rows, CancellationToken.None).ConfigureAwait(false);

        var keys = Dataset.KeySet(rows);
        int keyIters = iterationsOverride > 0 ? iterationsOverride : rows <= 100 ? 200 : 50;
        long insertKey = rows + 1;

        await MeasAsync(info, impl, "GetByKey", rows, conn, keyIters, results, async (im, c) =>
        {
            long id = keys[Random.Shared.Next(keys.Length)];
            _ = await im.GetByKeyAsync(c, id, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);


        await MeasAsync(info, impl, "QueryAll", rows, conn,
            iterationsOverride > 0 ? iterationsOverride : 10, results,
            async (im, c) => _ = await im.QueryAllAsync(c, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        await MeasAsync(info, impl, "StreamAll", rows, conn,
            iterationsOverride > 0 ? iterationsOverride : 5, results,
            async (im, c) => _ = await im.StreamAllAsync(c, _ => default, CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);

        await MeasAsync(info, impl, "Insert", rows, conn,
            iterationsOverride > 0 ? iterationsOverride : 30, results, async (im, c) =>
            {
                var row = Dataset.Seed(insertKey - 1);   // Seed(i) 的 Id = i+1
                await im.InsertAsync(c, row, CancellationToken.None).ConfigureAwait(false);
                await im.DeleteAsync(c, row.Id, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

        await MeasAsync(info, impl, "Update", rows, conn,
            iterationsOverride > 0 ? iterationsOverride : 20, results,
            async (im, c) => _ = await im.UpdateAsync(c, Dataset.Seed(Random.Shared.Next(rows)),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        await MeasAsync(info, impl, "BulkInsert", rows, conn,
            iterationsOverride > 0 ? iterationsOverride : 3, results, async (im, c) =>
            {
                await AdoNetImpl.ExecAsync(c, Dataset.DropTableSql(info.Dialect), CancellationToken.None)
                    .ConfigureAwait(false);
                await AdoNetImpl.ExecAsync(c, Dataset.CreateTableSql(info.Dialect), CancellationToken.None)
                    .ConfigureAwait(false);
                _ = await im.BulkInsertAsync(c, Dataset.SeedRows(rows), CancellationToken.None)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

        await MeasAsync(info, impl, "BulkUpdate", rows, conn,
            iterationsOverride > 0 ? iterationsOverride : 3, results,
            async (im, c) => _ = await im.BulkUpdateAsync(c, Dataset.SeedRows(rows),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        PrintRow(results.TakeLast(7).ToArray());
    }

    /// <summary>带操作名上下文的测量包装——失败时报出是哪个操作，便于定位。</summary>
    private static async Task MeasAsync(
        DialectInfo info, IPerfImplementation impl, string operation, int rows, DbConnection conn,
        int iterations, List<Measurement> results,
        Func<IPerfImplementation, DbConnection, Task> action)
    {
        try
        {
            results.Add(await Measure.SingleAsync(info, impl, operation, rows, action, conn,
                iterations, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            // 操作名上下文——失败时直接知道是哪个 (实现, 操作, 方言, 行数) 组合
            throw new InvalidOperationException(
                $"{impl.Name} / {operation} / {info.DisplayName} / {rows} 行 失败", ex);
        }
    }

    private static void PrintRow(Measurement[] rows)
    {
        foreach (var m in rows)
        {
            string warn = m.ErrorRatio > 0.05 ? " !" : "";
            string line = "    " + m.Implementation.PadRight(9) + " " + m.Operation.PadRight(12)
                + " " + Fmt.Time(m.MedianNs).PadLeft(12) + "  "
                + Fmt.Bytes(m.AllocatedBytesPerOp).PadLeft(10) + "/op  堆峰 "
                + Fmt.Bytes(m.PeakHeapBytes).PadLeft(10) + "  Gen0=" + m.Gen0Collections.ToString().PadLeft(3) + warn;
            Console.WriteLine(line);
        }
    }

    private static Dialect ParseDialect(string s) => s.ToUpperInvariant() switch
    {
        "SQLITE" or "SQLITE3" => Dialect.Sqlite,
        "MYSQL" or "MARIA" => Dialect.MySql,
        "PG" or "POSTGRES" or "POSTGRESQL" => Dialect.PostgreSql,
        _ => throw new ArgumentException($"未知方言 '{s}'")
    };

    private static List<T> ParseList<T>(string csv, Func<string, T> parse)
        => [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(parse)];

    private static void Save(List<Measurement> results, string label, TimeSpan elapsed)
    {
        string dir = Path.Combine(FindRepoRoot(), "bench", "perfhub", "results");
        Directory.CreateDirectory(dir);

        var run = new PerfRun
        {
            Schema = 2,
            Label = label,
            Timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            ElapsedSeconds = elapsed.TotalSeconds,
            Environment = new PerfEnvironment
            {
                Os = Environment.OSVersion.ToString(),
                Processor = Environment.ProcessorCount + " 逻辑核",
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Machine = Environment.MachineName,
                GcMode = System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation"
            },
            Measurements = results
        };

        string json = JsonSerializer.Serialize(run, PerfJsonContext.Default.PerfRun);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(Path.Combine(dir, $"history-{stamp}.json"), json);
        File.WriteAllText(Path.Combine(dir, "latest.json"), json);
        Console.WriteLine($"[PerfHub] 原始数据已写入 bench/perfhub/results/history-{stamp}.json");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}

// ─── JSON 序列化模型（AOT 友好：源生成上下文）──

internal sealed class PerfRun
{
    public int Schema { get; set; }
    public string Label { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public double ElapsedSeconds { get; set; }
    public PerfEnvironment Environment { get; set; } = new();
    public List<Measurement> Measurements { get; set; } = [];
}

internal sealed class PerfEnvironment
{
    public string Os { get; set; } = "";
    public string Processor { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string Machine { get; set; } = "";
    public string GcMode { get; set; } = "";
}

[JsonSerializable(typeof(PerfRun))]
internal sealed partial class PerfJsonContext : JsonSerializerContext;
