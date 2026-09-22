using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using PalORM.Bench.Shared;

namespace PalORM.DapperSuite;

/// <summary>Dapper 官方基准套件的 PalORM 三臂移植版运行器。
///
/// 用法（方言经环境变量选择，与官方单库运行方式对应）：
///   DAPPER_SUITE_DIALECT=sqlite dotnet run -c Release -- -f * --join
///   DAPPER_SUITE_DIALECT=mysql  dotnet run -c Release -- -f * --join   （需 PALORM_MYSQL_CONNECTION）
///   DAPPER_SUITE_DIALECT=pg     dotnet run -c Release -- -f * --join   （需 PALORM_PG_CONNECTION）
///
/// 数据集与方法学真源：DapperLib/Dapper benchmarks/Dapper.Tests.Performance
///（Post.cs 13 列 POCO / Step() 轮转 1..5000 / Config.cs ShortRun+unroll500）。
/// 方言适配与口径差登记见各文件头注释。</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine($"[DapperSuite] 方言 = {Database.Dialect}（DAPPER_SUITE_DIALECT）");
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("用法: dotnet run -c Release -- -f * --join");
            return 0;
        }

        IEnumerable<Summary> summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new Config());
        WriteEnvelope(summaries);
        return 0;
    }

    /// <summary>健康度阈值（规范 §4.2）——BDN ShortRun（unroll 500 × 10 迭代）的实测噪声底
    /// ±11%（规范 §4 噪声底先例），取 0.10 作阈值。</summary>
    private const double HealthThreshold = 0.10;

    /// <summary>结果库信封（规范 v2 §6）——本夹具不测维度 8（往返计数在 PerfHub），
    /// RoundTripsPerOp/PreparedReuse 留 0 表示未测；比值以同批次 HandCoded/SqlCommand 为地板现算。</summary>
    private static void WriteEnvelope(IEnumerable<Summary> summaries)
    {
        List<BenchmarkReport> reports = [.. summaries.SelectMany(static s => s.Reports)];
        if (reports.Count == 0) return;

        // 地板行：手写臂的单行 SqlCommand（官方基准的 Baseline=true 项）
        BenchmarkReport? floor = reports.Find(static r =>
            ArmOf(r) == "HandCoded" && NameOf(r).Contains("SqlCommand", StringComparison.Ordinal));
        double floorMean = floor?.ResultStatistics?.Mean ?? 0;
        double healthRatio = floor?.ResultStatistics is { Mean: > 0 } st ? st.StandardDeviation / st.Mean : 0;

        var envelope = new PerfResultEnvelope
        {
            Harness = "dappersuite",
            Version = Environment.GetEnvironmentVariable("DAPPER_SUITE_VERSION") ?? "HEAD",
            Label = Database.Dialect,
            DetailPath = "BenchmarkDotNet.Artifacts/results",
            Environment = PerfResultWriter.CaptureEnvironment("DapperSuite"),
            Regime = new PerfResultRegime
            {
                ConnectionConfig = Database.ConnectionConfigDescription,
                SessionLifecycle = "per-scope（范围入口建一次会话/连接，全臂一致；口径差 D9）",
                HealthRatio = healthRatio,
                HealthThreshold = HealthThreshold,
                Health = PerfResultWriter.Verdict(healthRatio, HealthThreshold)
            }
        };

        foreach (BenchmarkReport r in reports)
        {
            // NA 行（无连接/被跳过/失败）不该进结果库：0 均值会被读成"极快"
            if (r.ResultStatistics is not { Mean: > 0 } stats) continue;
            double mean = stats.Mean;
            envelope.Items.Add(new PerfResultItem
            {
                Name = NameOf(r),
                Dialect = Database.Dialect,
                Arm = ArmOf(r),
                Tier = Database.RowCount,
                MeanUs = mean / 1000.0,
                AllocBytes = r.GcStats.GetBytesAllocatedPerOperation(r.BenchmarkCase) ?? 0,
                Ratio = floorMean > 0 ? mean / floorMean : 0,
                Note = "Dapper 官方套件移植"
            });
        }

        string? path = PerfResultWriter.Write(envelope);
        Console.WriteLine(path is null
            ? "[DapperSuite] 结果库信封写入失败（不影响本次测量）"
            : $"[DapperSuite] 结果库信封已写入 {path}");
    }

    /// <summary>臂名：与 ORM 列同源（[Description] 优先，否则类名去 Benchmarks 后缀）。</summary>
    private static string ArmOf(BenchmarkReport report)
    {
        Type type = report.BenchmarkCase.Descriptor.WorkloadMethod.DeclaringType!;
        return type.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? type.Name.Replace("Benchmarks", string.Empty, StringComparison.Ordinal);
    }

    private static string NameOf(BenchmarkReport report)
        => report.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo;
}
