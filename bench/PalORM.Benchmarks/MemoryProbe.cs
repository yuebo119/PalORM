using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using PalORM;
using Microsoft.Data.Sqlite;
using PalORM.Bench.Shared;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ─────────────────────────────────────────────────────────────────────────────
// 大结果集内存曲线 + 查询构建分配（维度 1/7，规范 docs/性能基准规范.md §1）。
// 由 scripts/run-full-perf.sh 调用（--memory），产出 memory-sqlite.json 供报告汇总。
// 口径（规范 §4）：分配 = GetTotalAllocatedBytes(precise)（确定性事实）；
// 存活内存 = GC.Collect 后 GetTotalMemory(true) 差值；时间只作量级参考。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>内存探针参数。</summary>
internal sealed record MemoryOptions
{
    /// <summary>种子行数（须 ≥ 最大测量档位）。</summary>
    public long SeedRows { get; init; } = 120_000;

    /// <summary>测量档位（规范 §2 行数档位的子集）。</summary>
    public long[] Tiers { get; init; } = [10_000, 100_000];

    /// <summary>构建分配测量的迭代次数。</summary>
    public int BuildIterations { get; init; } = 200_000;
}

internal static class MemoryProbe
{
    [SuppressMessage("Globalization", "CA1303",
        Justification = "负载/内存探针的控制台输出是机器可读报告，固定文本。")]
    public static async Task RunAsync(MemoryOptions options)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"palorm-memory-{Environment.ProcessId}.db");
        var sessionOptions = new DbOptions
        {
            ConnectionString = $"Data Source={dbPath}",
            MaxRetries = 0,
            CircuitBreakerThreshold = 0
        };

        var build = new BuildAllocs();
        var tiers = new List<MemoryTierResult>(options.Tiers.Length);
        try
        {
            await using DataSession<SqliteProvider> setup = await DataSession<SqliteProvider>.CreateAsync(sessionOptions).ConfigureAwait(false);
            await StandardShapes.SeedAsync<BenchNarrow>(setup, options.SeedRows).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[memory] 种子 S1 Narrow × {options.SeedRows:N0} 完成"));

            // ── 查询构建分配 ──
            build.FromBytes = Alloc(() =>
            {
                for (int i = 0; i < options.BuildIterations; i++)
                {
                    _ = setup.From<BenchNarrow>();
                }
            }) / (double)options.BuildIterations;
            build.WhereToSqlBytes = Alloc(() =>
            {
                for (int i = 0; i < options.BuildIterations; i++)
                {
                    _ = setup.From<BenchNarrow>().Where($"\"Id\" = {1L}").ToSql();
                }
            }) / (double)options.BuildIterations;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[memory] From<T>()={build.FromBytes:F1} B/次 · Where+ToSql={build.WhereToSqlBytes:F1} B/次"));

            // ── 大结果集内存曲线 ──
            foreach (long rows in options.Tiers)
            {
                GC.Collect();
                long liveBefore = GC.GetTotalMemory(true);
                long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
                var stopwatch = Stopwatch.StartNew();
                List<BenchNarrow> list = await setup.From<BenchNarrow>()
                    .Take((int)rows).ToListAsync().ConfigureAwait(false);
                stopwatch.Stop();
                long allocTotal = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
                long liveAfter = GC.GetTotalMemory(true) - liveBefore;
                int count = list.Count;
                list.Clear();
                tiers.Add(new MemoryTierResult(
                    rows,
                    Math.Round(allocTotal / 1048576.0, 2),
                    Math.Round(liveAfter / 1048576.0, 2),
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 0)));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[memory] {rows,7:N0} 行: 分配 {allocTotal / 1048576.0,6:F1} MB · " +
                    $"存活 {liveAfter / 1048576.0,6:F1} MB · {stopwatch.Elapsed.TotalMilliseconds,5:F0} ms · 行数={count}"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            File.Delete(dbPath + "-wal");
            File.Delete(dbPath + "-shm");
        }

        string json = MemoryJson(options, build, tiers);
        string outputPath = Path.Combine(AppContext.BaseDirectory, "memory-sqlite.json");
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine($"[memory] 结果已写入 {outputPath}");
        WriteEnvelope(options, build, tiers);
    }

    /// <summary>结果库信封（规范 v2 §6）：内存曲线节的指标进 sections（每档位一行）。</summary>
    private static void WriteEnvelope(MemoryOptions options, BuildAllocs build, IReadOnlyList<MemoryTierResult> tiers)
    {
        var envelope = new PerfResultEnvelope
        {
            Harness = "benchmarks",
            Version = Environment.GetEnvironmentVariable("PALORM_BENCH_VERSION") ?? "HEAD",
            Label = "memory",
            DetailPath = "memory-sqlite.json",
            Environment = PerfResultWriter.CaptureEnvironment("PalORM.Benchmarks --memory"),
            Regime = new PerfResultRegime
            {
                ConnectionConfig = BenchmarkConfig.RegimeFileDb,
                SessionLifecycle = "per-query（内存曲线每档位独立测量，无跨档复用）",
                Health = "unknown"
            }
        };

        envelope.Sections.Add(new PerfResultSection
        {
            Kind = "memory",
            Dialect = "sqlite",
            Label = "build",
            Note = "SQL 构建路径的每操作分配",
            Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["fromBytesPerOp"] = build.FromBytes,
                ["whereToSqlBytesPerOp"] = build.WhereToSqlBytes,
                ["iterations"] = options.BuildIterations
            }
        });

        foreach (MemoryTierResult tier in tiers)
        {
            envelope.Sections.Add(new PerfResultSection
            {
                Kind = "memory",
                Dialect = "sqlite",
                Label = tier.Rows.ToString("N0", CultureInfo.InvariantCulture) + "行",
                Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["rows"] = tier.Rows,
                    ["allocMb"] = tier.AllocMb,
                    ["liveMb"] = tier.LiveMb,
                    ["milliseconds"] = tier.Milliseconds
                }
            });
        }

        string? path = PerfResultWriter.Write(envelope);
        Console.WriteLine(path is null
            ? "[memory] 结果库信封写入失败（不影响本次测量）"
            : $"[memory] 结果库信封已写入 {path}");
    }

    private static long Alloc(Action body)
    {
        body();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        body();
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    private static string MemoryJson(MemoryOptions options, BuildAllocs build, List<MemoryTierResult> tiers)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{{\"schema\":2,\"shape\":\"S1\",\"seed_rows\":{options.SeedRows},");
        sb.Append(CultureInfo.InvariantCulture,
            $"\"build\":{{\"from_b\":{build.FromBytes.ToString("F1", CultureInfo.InvariantCulture)}," +
            $"\"where_to_sql_b\":{build.WhereToSqlBytes.ToString("F1", CultureInfo.InvariantCulture)}}},");
        sb.Append(CultureInfo.InvariantCulture, $"\"date\":\"{DateTime.UtcNow:yyyy-MM-dd}\",\"tiers\":[");
        for (int i = 0; i < tiers.Count; i++)
        {
            MemoryTierResult tier = tiers[i];
            sb.Append(CultureInfo.InvariantCulture,
                $"{{\"rows\":{tier.Rows},\"alloc_mb\":{tier.AllocMb.ToString("F2", CultureInfo.InvariantCulture)}," +
                $"\"live_mb\":{tier.LiveMb.ToString("F2", CultureInfo.InvariantCulture)}," +
                $"\"ms\":{tier.Milliseconds.ToString("F0", CultureInfo.InvariantCulture)}}}");
            if (i < tiers.Count - 1)
            {
                sb.Append(',');
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }
}

/// <summary>构建器分配（B/次）。</summary>
internal sealed record BuildAllocs
{
    public double FromBytes { get; set; }
    public double WhereToSqlBytes { get; set; }
}

/// <summary>单档位内存结果。</summary>
internal sealed record MemoryTierResult(long Rows, double AllocMb, double LiveMb, double Milliseconds);
