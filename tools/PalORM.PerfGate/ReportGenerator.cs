using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalORM.PerfGate;

/// <summary>一键测评的 markdown 报告汇总——读取 BDN 结果、负载与内存 JSON，产出带表格的完整报告。
/// <para>由 scripts/run-full-perf.sh 在全部测量步之后调用（`report` 子命令）。</para></summary>
internal static partial class ReportGenerator
{
    internal sealed record WorkloadTierJson(
        int Threads, long OpsPerSecond, double P50Ms, double P95Ms, double P99Ms);

    internal sealed class WorkloadReportJson
    {
        public int Schema { get; set; }

        public string? Shape { get; set; }

        public long Rows { get; set; }

        public double WriteRatio { get; set; }

        public List<WorkloadTierJson> Tiers { get; set; } = [];
    }

    internal sealed record MemoryTierJson(long Rows, double AllocMb, double LiveMb, double Ms);

    internal sealed record BuildAllocJson(double FromB, double WhereToSqlB);

    internal sealed class MemoryReportJson
    {
        public int Schema { get; set; }

        public string? Shape { get; set; }

        public long SeedRows { get; set; }

        public BuildAllocJson? Build { get; set; }

        public List<MemoryTierJson> Tiers { get; set; } = [];
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(WorkloadReportJson))]
    [JsonSerializable(typeof(MemoryReportJson))]
    internal sealed partial class SectionJsonContext : JsonSerializerContext;

    internal static (int Passed, int Total) Generate(
        string resultsDirectory,
        PerfBaseline baseline,
        string? workloadJsonPath,
        string? memoryJsonPath,
        string outputPath,
        string? startupStatus = null)
    {
        ResultSet results = ResultReader.Read(resultsDirectory);
        var md = new StringBuilder(8_192);
        AppendHeader(md, baseline);
        AppendDimensionOverview(
            md,
            hasWorkload: File.Exists(workloadJsonPath ?? string.Empty),
            hasMemory: File.Exists(memoryJsonPath ?? string.Empty),
            startupPassed: string.Equals(startupStatus, "ok", StringComparison.OrdinalIgnoreCase));
        int passed = AppendBdnSection(md, baseline, results);
        AppendOptionalSection(md, workloadJsonPath, AppendWorkloadSection);
        AppendOptionalSection(md, memoryJsonPath, AppendMemorySection);
        md.AppendLine();
        md.AppendLine("> 本报告由 `tools/PalORM.PerfGate report` 生成；口径与阈值定义见 docs/性能基准规范.md。");

        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, md.ToString());
        return (passed, baseline.Benchmarks.Count);
    }

    /// <summary>12 维总览——每维一行：状态 + 本轮数据来源。未测维度如实列出，
    /// 防止"报告只有绿项"掩盖覆盖缺口（规范 §1 的矩阵即为本表的真源）。</summary>
    private static void AppendDimensionOverview(StringBuilder md, bool hasWorkload, bool hasMemory, bool startupPassed)
    {
        md.AppendLine("## 12 维总览");
        md.AppendLine();
        md.AppendLine("| # | 维度 | 本轮状态 | 本轮结果 / 缺口 |");
        md.AppendLine($"|---|---|---|---|");
        md.AppendLine(CultureInfo.InvariantCulture, $"| 1 | 单操作微基准 | ✅ 已测 | BDN 27 项，见下表 |");
        md.AppendLine(CultureInfo.InvariantCulture, $"| 2 | 批量吞吐 | ⚠️ 部分 | 分配维度见基线；远程真库档属本地探针（WITH_REMOTE），本轮未采 |");
        md.AppendLine(hasWorkload
            ? "| 3 | 延迟分布 | ✅ 已测 | p50/p95/p99 见负载表 |"
            : "| 3 | 延迟分布 | ❌ 未采集 | 缺 --workload JSON |");
        md.AppendLine(hasWorkload
            ? "| 4 | 并发扩展 | ✅ 已测 | 线程档 1/2/4/8 曲线见负载表 |"
            : "| 4 | 并发扩展 | ❌ 未采集 | 缺 --workload JSON |");
        md.AppendLine("| 5 | 数据形状敏感性 | ⚠️ 部分 | 本轮仅 S1；S2–S5 待各基准接入 |");
        md.AppendLine("| 6 | 连接与池 | ❌ 未测 | 建连/空闲回收/churn 专项探针（D3 先例） |");
        md.AppendLine(hasMemory
            ? "| 7 | 资源效率 | ✅ 部分 | 分配/存活曲线见内存表；流式对比不可做（无真流式公共 API） |"
            : "| 7 | 资源效率 | ❌ 未采集 | 缺 --memory JSON |");
        md.AppendLine("| 8 | 往返与语句效率 | ❌ 未测 | 需 Provider 往返计数器（未实现） |");
        md.AppendLine(startupPassed
            ? "| 9 | 启动与冷路径 | ✅ 已测 | 规模量具全绿（单方法 IL ≤64KB 上界，两维） |"
            : "| 9 | 启动与冷路径 | ⚠️ 未采集 | 启动量具结果未传入（--startup ok） |");
        md.AppendLine("| 10 | 长时稳定 | ❌ 未测 | 30 min+ 持续负载（未实现） |");
        md.AppendLine(hasWorkload
            ? "| 11 | 混合负载 | ✅ 已测 | 80/20 读写混合（见负载表） |"
            : "| 11 | 混合负载 | ❌ 未采集 | 缺 --workload JSON |");
        md.AppendLine("| 12 | 统计纪律 | ✅ | 阈值余量按实测噪声底标定（规范 §4） |");
        md.AppendLine();
    }

    private static void AppendHeader(StringBuilder md, PerfBaseline baseline)
    {
        md.AppendLine("# PalORM 性能全量报告");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"- 生成时间：{DateTime.UtcNow:yyyy-MM-dd HH:mm} (UTC)");
        md.AppendLine(CultureInfo.InvariantCulture, $"- 基线：{baseline.Version} / {baseline.Date}（schema {baseline.Schema}）");
        md.AppendLine(CultureInfo.InvariantCulture, $"- 环境：{baseline.Environment.Os} · {baseline.Environment.Processor} · {baseline.Environment.Runtime}");
        md.AppendLine(CultureInfo.InvariantCulture, $"- 阈值：分配 +{baseline.Thresholds.AllocatedPct:F0}% · 分配比 +{baseline.Thresholds.AllocRatioPct:F0}% · 耗时比 +{baseline.Thresholds.TimeRatioPct:F0}%");
        md.AppendLine();
    }

    private static int AppendBdnSection(StringBuilder md, PerfBaseline baseline, ResultSet results)
    {
        md.AppendLine("## 维度 1 · 单操作微基准（BDN，vs 基线）");
        md.AppendLine();
        md.AppendLine("| 基准 | 分配 B/op | Δ分配 | 分配比（现/基线） | 耗时比（现/基线） | 判定 |");
        md.AppendLine("|---|---:|---:|---|---|---|");

        int passed = 0;
        foreach (BaselineEntry entry in baseline.Benchmarks)
        {
            (string Row, bool Ok) = JudgeRow(entry, results, baseline.Thresholds);
            md.AppendLine(Row);
            if (Ok)
            {
                passed++;
            }
        }

        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture,
            $"**门禁判定：{passed}/{baseline.Benchmarks.Count} 阈值内**（分配为确定性指标；耗时比同轮计算约掉机器差异）。");
        md.AppendLine();
        return passed;
    }

    private static (string Row, bool Ok) JudgeRow(
        BaselineEntry entry, ResultSet results, BaselineThresholds thresholds)
    {
        string shortName = entry.Name[(entry.Name.LastIndexOf('.') + 1)..];
        if (!results.Benchmarks.TryGetValue(entry.Name, out BenchmarkMeasurement? current))
        {
            return ($"| {shortName} | - | - | - | - | 缺失 |", false);
        }

        double deltaPct = entry.AllocatedBytes == 0
            ? 0
            : ((current.AllocatedBytes / entry.AllocatedBytes) - 1) * 100;
        bool ok = current.AllocatedBytes <= entry.AllocatedBytes * (1 + (thresholds.AllocatedPct / 100));

        string allocRatioText = "-";
        string timeRatioText = "-";
        string? peerName = entry.RatioVs ?? RatioPeer(entry.Name, results.Benchmarks);
        if (peerName is not null && results.Benchmarks.TryGetValue(peerName, out BenchmarkMeasurement? peer))
        {
            double allocRatio = peer.AllocatedBytes == 0 ? 0 : current.AllocatedBytes / peer.AllocatedBytes;
            if (entry.AllocRatio is not null)
            {
                allocRatioText = $"{allocRatio:F3}/{entry.AllocRatio:F3}";
                ok &= allocRatio <= entry.AllocRatio.Value * (1 + (thresholds.AllocRatioPct / 100));
            }
            if (entry.TimeRatio is not null && peer.MedianNanoseconds > 0)
            {
                double timeRatio = current.MedianNanoseconds / peer.MedianNanoseconds;
                timeRatioText = $"{timeRatio:F3}/{entry.TimeRatio:F3}";
                ok &= timeRatio <= entry.TimeRatio.Value * (1 + (thresholds.TimeRatioPct / 100));
            }
        }

        string verdict = ok ? "OK" : "FAIL";
        string row = string.Create(
            CultureInfo.InvariantCulture,
            $"| {shortName} | {current.AllocatedBytes:F0} | {deltaPct:+0.0;-0.0}% | {allocRatioText} | {timeRatioText} | {verdict} |");
        return (row, ok);
    }

    private static void AppendWorkloadSection(StringBuilder md, string jsonPath)
    {
        using FileStream stream = File.OpenRead(jsonPath);
        WorkloadReportJson? workload = JsonSerializer.Deserialize(stream, SectionJsonContext.Default.WorkloadReportJson);
        if (workload is null)
        {
            return;
        }

        md.AppendLine(CultureInfo.InvariantCulture,
            $"## 维度 3/4/11 · 并发负载——延迟分布 / 扩展曲线 / 80:20 混合（{workload.Shape} × {workload.Rows:N0}）");
        md.AppendLine();
        md.AppendLine("| 线程 | ops/s | p50 (ms) | p95 (ms) | p99 (ms) |");
        md.AppendLine("|---:|---:|---:|---:|---:|");
        foreach (WorkloadTierJson tier in workload.Tiers)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {tier.Threads} | {tier.OpsPerSecond:N0} | {tier.P50Ms:F3} | {tier.P95Ms:F3} | {tier.P99Ms:F3} |");
        }
        md.AppendLine();
    }

    private static void AppendMemorySection(StringBuilder md, string jsonPath)
    {
        using FileStream stream = File.OpenRead(jsonPath);
        MemoryReportJson? memory = JsonSerializer.Deserialize(stream, SectionJsonContext.Default.MemoryReportJson);
        if (memory is null)
        {
            return;
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"## 维度 7 · 大结果集内存（{memory.Shape}，全量物化）");
        md.AppendLine();
        md.AppendLine("| 档位 | 分配总量 | 实体存活 | 耗时 |");
        md.AppendLine("|---:|---:|---:|---:|");
        foreach (MemoryTierJson tier in memory.Tiers)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {tier.Rows:N0} | {tier.AllocMb:F1} MB | {tier.LiveMb:F1} MB | {tier.Ms:F0} ms |");
        }
        md.AppendLine();
        if (memory.Build is not null)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"查询构建分配：`From<T>()` {memory.Build.FromB:F1} B/次 · `Where + ToSql` {memory.Build.WhereToSqlB:F1} B/次。");
            md.AppendLine();
        }
    }

    private static void AppendOptionalSection(StringBuilder md, string? jsonPath, Action<StringBuilder, string> append)
    {
        if (jsonPath is null || !File.Exists(jsonPath))
        {
            return;
        }
        append(md, jsonPath);
    }

    /// <summary>与 Program.Judge 同规则的对照选取（ADO 优先、其次 Dapper）。</summary>
    private static string? RatioPeer(string entryName, Dictionary<string, BenchmarkMeasurement> benchmarks)
    {
        string leaf = entryName[(entryName.LastIndexOf('.') + 1)..];
        foreach (string prefix in new[] { "ADO_NET_", "Dapper_" })
        {
            string? candidate = benchmarks.Keys.FirstOrDefault(name =>
                name.EndsWith('.' + prefix + leaf, StringComparison.Ordinal)
                || name.EndsWith(prefix + leaf, StringComparison.Ordinal));
            if (candidate is not null)
            {
                return candidate;
            }
        }
        return null;
    }
}
