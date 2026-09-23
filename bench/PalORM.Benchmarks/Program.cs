using System.Diagnostics.CodeAnalysis;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Parameters;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using PalORM.Bench.Shared;
using PalORM.Sqlite;
using PalORM.PostgreSql;
using PalORM.MySql;
// RepoDb（含 Sqlite 扩展，全部位于 RepoDb 命名空间下：SqliteGlobalConfiguration.UseSqlite）
using RepoDb;

[assembly: DapperAot]
[assembly: SuppressMessage("Design", "CA1515", Justification = "BenchmarkDotNet requires public types.")]

namespace PalORM.Benchmarks;

public static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "CLI 模式分派是顺序 if-return 的天然形态（--boxing/--workload/--memory/--stability/--bdn-debug），分支间无嵌套逻辑。")]
    public static void Main(string[] args)
    {
        // v5.0 阶段 3.4：--boxing 切到手写微基准（绕过 BDN .NET 11 preview 不兼容）
        if (args.Length > 0 && args[0] == "--boxing")
        {
            BoxingMicroBenchmark.RunAsync().GetAwaiter().GetResult();
            return;
        }
        // 并发负载测试（维度 3/4/11，规范 docs/性能基准规范.md）——BDN 单线程测不了锁/池/调度
        if (args.Length > 0 && args[0] == "--workload")
        {
            // --workload [sqlite|pg|mysql] [线程档位CSV]：方言默认 sqlite（pg/mysql 读
            // PALORM_*_CONNECTION）；档位默认 1,2,4,8——找并发拐点时传 1,4,8,16,32,64
            WorkloadHarness.RunAsync(new WorkloadOptions
            {
                Dialect = args.Length > 1 ? args[1] : "sqlite",
                ThreadTiers = args.Length > 2
                    ? [.. args[2].Split(',').Select(static tier => int.Parse(tier.Trim()))]
                    : [1, 2, 4, 8]
            }).GetAwaiter().GetResult();
            return;
        }
        // 长稳测试（维度 10：持续负载抓泄漏/衰减）——--stability <秒> [dialect]
        if (args.Length > 0 && args[0] == "--stability")
        {
            StabilityHarness.RunAsync(
                seconds: args.Length > 1 ? int.Parse(args[1]) : 180,
                dialect: args.Length > 2 ? args[2] : "sqlite").GetAwaiter().GetResult();
            return;
        }
        // 大结果集内存曲线 + 查询构建分配（维度 1/7）
        if (args.Length > 0 && args[0] is string mode && mode == "--memory")
        {
            MemoryProbe.RunAsync(new MemoryOptions()).GetAwaiter().GetResult();
            return;
        }
        // 诊断 BDN RuntimeMoniker 推断
        if (args.Length > 0 && args[0] == "--bdn-debug")
        {
            var attrs = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false);
            var attr = attrs.Length > 0 ? (System.Runtime.Versioning.TargetFrameworkAttribute)attrs[0] : null;
            Console.WriteLine($"TargetFrameworkAttribute.FrameworkName: {attr?.FrameworkName ?? "(null)"}");
            Console.WriteLine($"Environment.Version: {Environment.Version}");
            Console.WriteLine($"RuntimeInformation.FrameworkDescription: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            return;
        }
        IEnumerable<Summary> summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        // 批次标签优先级：调用方显式声明（PALORM_BENCH_LABEL，如全量脚本的 gate-set）> 过滤跑标记 >
        // 默认 bdn。显式声明让"可复现的操作性子集"（门禁同参集）与"临时单基准跑"在结果库里有区别。
        string? declared = Environment.GetEnvironmentVariable("PALORM_BENCH_LABEL");
        bool filtered = Array.Exists(args, static a => a.Contains("filter", StringComparison.OrdinalIgnoreCase));
        string label = !string.IsNullOrWhiteSpace(declared) ? declared : filtered ? "filtered" : "bdn";
        WriteEnvelope(summaries, label);
    }

    /// <summary>健康度阈值（规范 §4.2）——BDN 标准跑（launch 3 × warmup 5 × 迭代 10）
    /// 的实测噪声底 ±11%（规范 §4 先例），取 0.10 作阈值。</summary>
    private const double HealthThreshold = 0.10;

    /// <summary>结果库信封（规范 v2 §6）。本夹具不测维度 8（往返计数在 PerfHub），
    /// RoundTripsPerOp/PreparedReuse 留 0 表示未测；比值以同批次同操作的 ADO_NET 行为地板现算。</summary>
    private static void WriteEnvelope(IEnumerable<Summary> summaries, string label)
    {
        List<BenchmarkReport> reports = [.. summaries.SelectMany(static s => s.Reports)];
        if (reports.Count == 0) return;

        double healthRatio = reports
            .Where(static r => ArmOf(r) == "ADO_NET" && r.ResultStatistics is { Mean: > 0 })
            .Select(static r => r.ResultStatistics!.StandardDeviation / r.ResultStatistics.Mean)
            .DefaultIfEmpty(0)
            .Max();

        var envelope = new PerfResultEnvelope
        {
            Harness = "benchmarks",
            Version = Environment.GetEnvironmentVariable("PALORM_BENCH_VERSION") ?? "HEAD",
            Label = label,
            DetailPath = "BenchmarkDotNet.Artifacts/results",
            Environment = PerfResultWriter.CaptureEnvironment("PalORM.Benchmarks"),
            Regime = new PerfResultRegime
            {
                ConnectionConfig = BenchmarkConfig.RegimeBdnMemory,
                SessionLifecycle = "混合（各基准类自述：单操作类每操作建会话，负载/长稳类复用会话）",
                HealthRatio = healthRatio,
                HealthThreshold = HealthThreshold,
                Health = PerfResultWriter.Verdict(healthRatio, HealthThreshold)
            }
        };

        // NA 行（无连接/被跳过/失败）不该进结果库：0 均值会被读成"极快"
        foreach (BenchmarkReport r in reports.Where(static r => r.ResultStatistics is { Mean: > 0 }))
        {
            double mean = r.ResultStatistics!.Mean;
            string operation = OperationOf(r);
            BenchmarkReport? floor = reports.Find(b => ArmOf(b) == "ADO_NET" && OperationOf(b) == operation);
            double floorMean = floor?.ResultStatistics?.Mean ?? 0;
            envelope.Items.Add(new PerfResultItem
            {
                Name = operation,
                Dialect = "sqlite",
                Arm = ArmOf(r),
                Tier = TierOf(r),
                MeanUs = mean / 1000.0,
                AllocBytes = r.GcStats.GetBytesAllocatedPerOperation(r.BenchmarkCase) ?? 0,
                Ratio = floorMean > 0 ? mean / floorMean : 0,
                Note = r.BenchmarkCase.Descriptor.Type.Name
            });
        }

        string? path = PerfResultWriter.Write(envelope);
        Console.WriteLine(path is null
            ? "[Benchmarks] 结果库信封写入失败（不影响本次测量）"
            : $"[Benchmarks] 结果库信封已写入 {path}");
    }

    /// <summary>臂前缀（长的在前：ADO_NET_ 自带下划线，不能按第一个 '_' 切）。</summary>
    private static readonly string[] ArmPrefixes = ["ADO_NET_", "RepoDb_", "Dapper_", "PalORM_"];

    /// <summary>命中臂前缀（长的在前，ADO_NET_ 自带下划线）；无命中返回 null。</summary>
    private static string? MatchArmPrefix(string method)
        => Array.Find(ArmPrefixes, prefix => method.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>臂名：按已知前缀匹配；无前缀则退回类名。</summary>
    private static string ArmOf(BenchmarkReport report)
    {
        string method = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
        string? prefix = MatchArmPrefix(method);
        return prefix is null ? report.BenchmarkCase.Descriptor.Type.Name : prefix[..^1];
    }

    /// <summary>操作名：方法名去掉臂前缀（ADO_NET_QueryAll → QueryAll）；无前缀则用方法名。</summary>
    private static string OperationOf(BenchmarkReport report)
    {
        string method = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
        string? prefix = MatchArmPrefix(method);
        return prefix is null ? method : method[prefix.Length..];
    }

    /// <summary>行数档位：有 [Params] 取第一个 int 参数；没有则用种子行数（固定 10000）。</summary>
    private static int TierOf(BenchmarkReport report)
    {
        foreach (ParameterInstance p in report.BenchmarkCase.Parameters.Items)
        {
            if (p.Value is int rows) return rows;
        }

        return BenchmarkConfig.SeedRows;
    }
}

// v5.0 基准体系重构：所有 benchmark 类已迁移到独立文件
// 01_CrudBenchmarks.cs / 02_BulkBenchmarks.cs / 03_GcBenchmarks.cs /
// 04_SqlBuildBenchmarks.cs / 06_FeatureBenchmarks.cs /
// 07_OrmComparisonBenchmarks.cs / 08_BinaryBenchmarks.cs /
// PgBenchmarks.cs / MySqlBenchmarks.cs / MySqlBulkColumnWidthBenchmarks.cs
// 实体定义在 BenchmarkEntities.cs，统一配置在 BenchmarkConfig.cs
