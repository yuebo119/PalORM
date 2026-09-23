using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalORM.Bench.Shared;

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
        ReportInputs inputs,
        string outputPath)
    {
        ResultSet results = ResultReader.Read(resultsDirectory);
        // 结果库批次——维度总览与跨夹具节都据此判定，避免"报告只有绿项"或"标了未实现其实已实现"
        List<PerfResultEnvelope> runs = string.IsNullOrEmpty(inputs.EnvelopesDirectory)
            ? []
            : IndexGenerator.LoadAll(inputs.EnvelopesDirectory);

        var md = new StringBuilder(8_192);
        AppendHeader(md, baseline);
        AppendDimensionOverview(
            md,
            hasWorkload: File.Exists(inputs.WorkloadJsonPath ?? string.Empty),
            hasMemory: File.Exists(inputs.MemoryJsonPath ?? string.Empty),
            startupPassed: string.Equals(inputs.StartupStatus, "ok", StringComparison.OrdinalIgnoreCase),
            runs);
        int passed = AppendBdnSection(md, baseline, results);
        AppendOptionalSection(md, inputs.WorkloadJsonPath, AppendWorkloadSection);
        AppendOptionalSection(md, inputs.MemoryJsonPath, AppendMemorySection);
        AppendCrossFixtureSection(md, inputs.EnvelopesDirectory, inputs.IndexBaselinePath);
        md.AppendLine();
        md.AppendLine("> 本报告由 `tools/PalORM.PerfGate report` 生成；口径与阈值定义见 docs/性能基准规范.md。");

        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, md.ToString());
        return (passed, baseline.Benchmarks.Count);
    }

    /// <summary>可选输入路径——统一报告的数据来源。缺项即"该节未采集"，不是错误。</summary>
    internal sealed record ReportInputs(
        string? WorkloadJsonPath,
        string? MemoryJsonPath,
        string? StartupStatus,
        string? EnvelopesDirectory,
        string? IndexBaselinePath);

    /// <summary>跨夹具节——把结果库里三套夹具的批次、口径登记、健康度与关键项追加到同一份报告。
    /// 这是"一次跑测出一份报告"的落点：此前 BDN 明细在 perf-report-*.md、跨夹具登记在
    /// perf-index.md，读者要自己拼。</summary>
    private static void AppendCrossFixtureSection(
        StringBuilder md, string? envelopesDirectory, string? indexBaselinePath)
    {
        md.AppendLine();
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine("## 跨夹具批次登记");
        md.AppendLine();

        if (string.IsNullOrEmpty(envelopesDirectory))
        {
            md.AppendLine("（未传入 `--envelopes`，本节未采集）");
            return;
        }

        if (!IndexGenerator.TryAppend(md, envelopesDirectory, out string reason))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"（{reason}）");
            return;
        }

        if (!string.IsNullOrEmpty(indexBaselinePath) && File.Exists(indexBaselinePath))
        {
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"索引门禁基线：`{indexBaselinePath}`。");
            md.AppendLine("判定命令 `tools/PalORM.PerfGate check-index --baseline <该文件>`"
                + "（本报告不重复其退出码）。");
        }
    }

    /// <summary>12 维总览——每维一行：状态 + 本轮数据来源。未测维度如实列出，
    /// 防止"报告只有绿项"掩盖覆盖缺口（规范 §1 的矩阵即为本表的真源）。
    /// <para>状态由**本轮实测批次**推导（<paramref name="runs"/>），不按"功能是否存在"写死：
    /// 硬编码曾把已实现的维度 8（PerfHub 往返计数）与维度 10（长稳量具）标成"未实现"。</para></summary>
    private static void AppendDimensionOverview(
        StringBuilder md, bool hasWorkload, bool hasMemory, bool startupPassed,
        List<PerfResultEnvelope> runs)
    {
        PerfResultEnvelope? perfhub = Latest(runs, "perfhub");
        PerfResultEnvelope? dappersuite = Latest(runs, "dappersuite");
        PerfResultEnvelope? stability = runs.FirstOrDefault(static r =>
            r.Label.Contains("stability", StringComparison.OrdinalIgnoreCase));
        bool hasRoundTrips = perfhub?.Items.Any(static i => i.RoundTripsPerOp > 0) == true;

        md.AppendLine("## 12 维总览");
        md.AppendLine();
        md.AppendLine("| # | 维度 | 本轮状态 | 本轮结果 / 缺口 |");
        md.AppendLine("|---|---|---|---|");
        md.AppendLine("| 1 | 单操作微基准 | ✅ 已测 | BDN 基线项见下表 |");
        md.AppendLine(perfhub is not null
            ? "| 2 | 批量吞吐 | ✅ 已测 | PerfHub 跨方言批量五形态（BulkInsert/Update/Delete/UpsertBatch/TxBulkInsert） |"
            : "| 2 | 批量吞吐 | ⚠️ 部分 | 仅 BDN SQLite 档；PerfHub 批次缺失 |");
        md.AppendLine(hasWorkload
            ? "| 3 | 延迟分布 | ✅ 已测 | p50/p95/p99 见负载表 |"
            : "| 3 | 延迟分布 | ❌ 未采集 | 缺 --workload JSON |");
        md.AppendLine(hasWorkload
            ? "| 4 | 并发扩展 | ✅ 已测 | 线程档曲线见负载表 |"
            : "| 4 | 并发扩展 | ❌ 未采集 | 缺 --workload JSON |");
        md.AppendLine("| 5 | 数据形状敏感性 | ⚠️ 部分 | S1 全档 + S2 宽表；S3/S4/S5 仅在 BDN 专项类覆盖 |");
        md.AppendLine("| 6 | 连接与池 | ❌ 未测 | 建连/空闲回收/churn 专项探针（D3 先例） |");
        md.AppendLine(hasMemory
            ? "| 7 | 资源效率 | ✅ 部分 | 分配/存活曲线见内存表；流式对比仅在 PerfHub StreamAll |"
            : "| 7 | 资源效率 | ❌ 未采集 | 缺 --memory JSON |");
        md.AppendLine(hasRoundTrips
            ? "| 8 | 往返与语句效率 | ✅ 已测 | PerfHub 计数装饰器实测往返/op 与 prepared 复用（见关键项表） |"
            : "| 8 | 往返与语句效率 | ❌ 未采集 | 缺 PerfHub 批次（计数装饰器仅 SQLite 方言生效） |");
        md.AppendLine(startupPassed
            ? "| 9 | 启动与冷路径 | ✅ 已测 | 规模量具全绿（单方法 IL ≤64KB 上界，两维） |"
            : "| 9 | 启动与冷路径 | ⚠️ 未采集 | 启动量具结果未传入（--startup ok） |");
        md.AppendLine(stability is not null
            ? $"| 10 | 长时稳定 | ⚠️ 见时间 | 量具已实现（规范 §5）；最近长稳批次 {stability.Timestamp}，"
                + "早于本轮即视为本轮未采集 |"
            : "| 10 | 长时稳定 | ❌ 未采集 | 本批未跑 --stability（量具已实现，见规范 §5） |");
        md.AppendLine(hasWorkload
            ? "| 11 | 混合负载 | ✅ 已测 | 80/20 读写混合（见负载表） |"
            : "| 11 | 混合负载 | ❌ 未采集 | 缺 --workload JSON |");
        md.AppendLine(dappersuite is not null
            ? "| 12 | 统计纪律 | ✅ | 阈值余量按实测噪声底标定（规范 §4）；官方形状哨兵批次已在结果库 |"
            : "| 12 | 统计纪律 | ✅ | 阈值余量按实测噪声底标定（规范 §4） |");
        md.AppendLine();
    }

    /// <summary>结果库中某夹具最近一批（含子集标记——这里要的是"本轮有没有采到"，不是"能不能引用"）。
    /// <para>跳过有失败登记的批次：方言全失败时批次只剩基线项，用它判定会把"没采到"读成"已采集"。</para></summary>
    private static PerfResultEnvelope? Latest(List<PerfResultEnvelope> runs, string harness)
        => runs.FirstOrDefault(r =>
            r.Harness.Equals(harness, StringComparison.OrdinalIgnoreCase)
            && !r.Sections.Any(static s => s.Kind.Contains("failure", StringComparison.OrdinalIgnoreCase)));

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
