using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalORM.Bench.Shared;

namespace PalORM.PerfCli;

/// <summary>阶段 0 时间分解报告（作战卡 §4.4）：读 PerfHub 的阶段计时旁车 + 本进程的编排步骤耗时，
/// 生成 <c>bench/reports/perf-time-breakdown-&lt;stamp&gt;.md/.json</c>。
/// <para><b>只读</b>：不改任何测量结果、不写结果库、不碰基线。产物在 gitignored 的 bench/reports 下，
/// 是诊断材料而非可引用数字（可引用真源仍是 history JSON）。</para></summary>
internal static class TimingReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] StageNames =
    [
        "prepare-before", "warmup", "prepare-after", "probe", "pre-gc", "timed", "post"
    ];

    public static int Build(string? rawPath = null, string? outPath = null)
    {
        var root = Perf.RepoRoot();
        var reportsDir = Path.Combine(root, "bench", "reports");
        rawPath ??= Perf.NewestFile(reportsDir, "perf-timing-*.json");
        if (rawPath is null || !File.Exists(rawPath))
        {
            Perf.Err("时间分解：未找到 perf-timing-*.json"
                + "（本批未以 PALORM_PERFHUB_TIMING=1 运行，或 PerfHub 未完成）");
            return 1;
        }

        if (!TryLoadRaw(rawPath, out PerfTimingReport? raw) || raw is null || raw.Items.Count == 0)
        {
            return 1;
        }

        List<PerfInnerStep> inner = LoadInnerSteps(reportsDir);
        int fullTotalSeconds = Perf.Steps.Count > 0
            ? int.Parse(Perf.Steps[^1][3], Inv)
            : 0;

        var breakdown = Aggregate(raw, inner, fullTotalSeconds, rawPath);

        string stamp = raw.Timestamp.Length > 0
            ? raw.Timestamp
            : DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
        outPath ??= Path.Combine(reportsDir, $"perf-time-breakdown-{stamp}.md");
        string jsonPath = Path.ChangeExtension(outPath, ".json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(
            breakdown, TimingCliJsonContext.Default.TimingBreakdown));
        File.WriteAllText(outPath, RenderMarkdown(breakdown, raw));

        Perf.Out($"时间分解报告: {outPath}");
        Perf.Out($"时间分解数据: {jsonPath}");
        return 0;
    }

    private static bool TryLoadRaw(string rawPath, out PerfTimingReport? raw)
    {
        try
        {
            raw = JsonSerializer.Deserialize(
                File.ReadAllText(rawPath), TimingCliJsonContext.Default.PerfTimingReport);
        }
        catch (JsonException ex)
        {
            Perf.Err($"时间分解：旁车 JSON 解析失败 — {ex.Message}");
            raw = null;
            return false;
        }
        if (raw is null || raw.Items.Count == 0)
        {
            Perf.Err("时间分解：旁车里没有测量项（空批次？）");
            return false;
        }
        return true;
    }

    /// <summary>内部步骤耗时：同进程优先（full 的 [0/6]…[5/6]），跨进程回落到同批旁车 JSON。</summary>
    private static List<PerfInnerStep> LoadInnerSteps(string reportsDir)
    {
        if (FullPerf.Steps.Count > 0)
        {
            return [.. FullPerf.Steps];
        }
        string? innerPath = Perf.NewestFile(reportsDir, "fullperf-steps-*.json");
        if (innerPath is null)
        {
            return [];
        }
        try
        {
            var loaded = JsonSerializer.Deserialize(
                File.ReadAllText(innerPath), TimingCliJsonContext.Default.ListPerfInnerStep);
            return loaded is null ? [] : [.. loaded];
        }
        catch (JsonException)
        {
            // 旁车损坏不影响主分解——编排成本行留空并在报告里注明
            return [];
        }
    }

    private static double StageOf(PerfStageTiming item, string stage) => stage switch
    {
        "prepare-before" => item.PrepareBeforeSeconds,
        "warmup" => item.WarmupSeconds,
        "prepare-after" => item.PrepareAfterSeconds,
        "probe" => item.ProbeSeconds,
        "pre-gc" => item.PreGcSeconds,
        "timed" => item.TimedSeconds,
        "post" => item.PostSeconds,
        _ => 0
    };

    private static TimingBreakdown Aggregate(
        PerfTimingReport raw, List<PerfInnerStep> inner, int fullTotalSeconds, string sourcePath)
    {
        var breakdown = new TimingBreakdown
        {
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv),
            Commit = Perf.Git("rev-parse --short HEAD").Trim(),
            SourceFile = Path.GetFileName(sourcePath),
            PerfHubRunSeconds = raw.RunTotalSeconds,
            FullTotalSeconds = fullTotalSeconds,
            Items = raw.Items
        };

        foreach (string stage in StageNames)
        {
            breakdown.StageTotals[stage] = raw.Items.Sum(i => StageOf(i, stage));
        }
        breakdown.StageTotals["write-result"] = raw.Items.Sum(static i => i.WriteResultSeconds);

        foreach (var group in raw.Phases.GroupBy(static p => p.Name, StringComparer.Ordinal)
            .OrderByDescending(static g => g.Sum(static p => p.Seconds)))
        {
            breakdown.PhaseTotals.Add(new TimingPhaseAggregate
            {
                Name = group.Key,
                Count = group.Count(),
                TotalSeconds = group.Sum(static p => p.Seconds)
            });
        }

        double itemsTotal = raw.Items.Sum(static i => i.TotalSeconds);
        breakdown.ItemsTotalSeconds = itemsTotal;
        var recorded = raw.Items.Where(static i => i.Recorded).ToList();
        breakdown.ByOperation = [.. recorded
            .GroupBy(static i => i.Operation, StringComparer.Ordinal)
            .Select(g => new TimingOperationAggregate
            {
                Operation = g.Key,
                Count = g.Count(),
                TotalSeconds = g.Sum(static i => i.TotalSeconds),
                AverageSeconds = g.Average(static i => i.TotalSeconds),
                WarmupSeconds = g.Sum(static i => i.WarmupSeconds),
                TimedSeconds = g.Sum(static i => i.TimedSeconds),
                PrepareSeconds = g.Sum(static i => i.PrepareBeforeSeconds + i.PrepareAfterSeconds),
                BudgetBoundCount = g.Count(static i => i.WarmupSeconds >= 1.4),
                ShareOfItems = itemsTotal <= 0 ? 0 : g.Sum(static i => i.TotalSeconds) / itemsTotal
            })
            .OrderByDescending(static o => o.TotalSeconds)];
        breakdown.ByDialect = [.. raw.Items
            .GroupBy(static i => i.Dialect, StringComparer.Ordinal)
            .Select(g => new TimingDialectAggregate
            {
                Dialect = g.Key,
                Count = g.Count(),
                TotalSeconds = g.Sum(static i => i.TotalSeconds),
                ShareOfItems = itemsTotal <= 0 ? 0 : g.Sum(static i => i.TotalSeconds) / itemsTotal
            })
            .OrderByDescending(static d => d.TotalSeconds)];
        breakdown.ByTier = [.. raw.Items
            .GroupBy(static i => i.Rows)
            .Select(g => new TimingTierAggregate
            {
                Rows = g.Key,
                Count = g.Count(),
                TotalSeconds = g.Sum(static i => i.TotalSeconds),
                ShareOfItems = itemsTotal <= 0 ? 0 : g.Sum(static i => i.TotalSeconds) / itemsTotal
            })
            .OrderBy(static t => t.Rows)];

        // 固定成本 = 编排步骤（PerfCli）+ 内部步骤（FullPerf）+ PerfHub 内部固定项
        foreach (string[] row in Perf.Steps)
        {
            breakdown.FixedCosts.Add(new PerfInnerStep
            {
                Name = row[0],
                Seconds = int.Parse(row[2], Inv)
            });
        }
        foreach (PerfInnerStep step in inner)
        {
            breakdown.FixedCosts.Add(new PerfInnerStep { Name = "FullPerf " + step.Name, Seconds = step.Seconds });
        }
        breakdown.FixedCosts.Add(new PerfInnerStep { Name = "PerfHub 结果落盘（history+信封）", Seconds = raw.SaveSeconds });
        breakdown.FixedCosts.Add(new PerfInnerStep { Name = "PerfHub 报告生成（Report.Build）", Seconds = raw.ReportBuildSeconds });

        // 未归因：PerfHub 总墙钟 − 测量项总耗时 − 非重叠相位。
        // 排除三类重叠：arm-block:* 覆盖该臂测量、dialect-total 覆盖整个方言、
        // interleaved-record/discard-pass 覆盖其下 9 个测量（那些测量的耗时已在 Items 里）。
        double nonOverlap = raw.Phases
            .Where(static p => !p.Name.StartsWith("arm-block:", StringComparison.Ordinal)
                && p.Name is not ("dialect-total" or "interleaved-discard-pass" or "interleaved-record-pass"))
            .Sum(static p => p.Seconds);
        breakdown.PhaseNonOverlapSeconds = nonOverlap;
        breakdown.UnattributedSeconds = raw.RunTotalSeconds - itemsTotal - nonOverlap
            - raw.SaveSeconds - raw.ReportBuildSeconds;

        return breakdown;
    }

    private static string RenderMarkdown(TimingBreakdown b, PerfTimingReport raw)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, b, raw);
        AppendSummary(sb, b, raw);
        AppendStageTable(sb, b);
        AppendDialectTable(sb, b);
        AppendTierTable(sb, b);
        AppendOperationTable(sb, b);
        AppendParetoTable(sb, b);
        AppendFixedCostTable(sb, b);
        AppendSelfCheck(sb, b);
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, TimingBreakdown b, PerfTimingReport raw)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"# 性能测试时间分解（阶段 0）· {raw.Timestamp}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"> 提交 `{(b.Commit.Length > 0 ? b.Commit : "?")}` ｜ 数据源 `{b.SourceFile}` ｜ 生成 {b.GeneratedAt}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"> 口径：PerfHub 本批 {b.Items.Count} 项测量（含交错读 discarded pass），run 总墙钟 {S(b.PerfHubRunSeconds)}；"
            + $"全量编排总墙钟 {(b.FullTotalSeconds > 0 ? S(b.FullTotalSeconds) : "未采集")}。");
        sb.AppendLine("> 本文件是诊断材料，不是可引用性能数字；可引用真源仍是 `bench/perfhub/results/history-*.json`。");
        sb.AppendLine();
    }

    /// <summary>机器聚合的一句话结论：只陈述数字与占比，分类解释留给阶段 1 报告。
    /// <para>分母口径：记录项（Recorded=true）按阶段拆；discarded pass 单独按整段计时列，
    /// 避免与记录项的阶段和重复相加。</para></summary>
    private static void AppendSummary(StringBuilder sb, TimingBreakdown b, PerfTimingReport raw)
    {
        double runTotal = b.PerfHubRunSeconds;
        List<PerfStageTiming> recorded = [.. b.Items.Where(static i => i.Recorded)];
        List<PerfStageTiming> discarded = [.. b.Items.Where(static i => !i.Recorded)];
        double necessary = recorded.Sum(i => i.WarmupSeconds + i.ProbeSeconds + i.PreGcSeconds
            + i.TimedSeconds + i.PostSeconds);
        double reset = recorded.Sum(static i => i.PrepareBeforeSeconds + i.PrepareAfterSeconds);
        double discard = discarded.Sum(static i => i.TotalSeconds);
        double hubFixed = raw.SaveSeconds + raw.ReportBuildSeconds
            + PhaseTotal(b, "setup-tables") + PhaseTotal(b, "interleaved-reset")
            + PhaseTotal(b, "connect") + PhaseTotal(b, "close");
        List<PerfInnerStep> orchestration = [.. b.FixedCosts
            .Where(static f => f.Name.StartsWith('[')
                && !f.Name.Contains("PerfHub", StringComparison.Ordinal))];

        sb.AppendLine("## 1. 时间去哪了（机器聚合，只陈述数字）");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 记录项 · 测量必需（预热+探针+预GC+计时+后处理）：{S(necessary)}，占 PerfHub {P(necessary, runTotal)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 记录项 · 重置/播种（prepare 前后）：{S(reset)}，占 PerfHub {P(reset, runTotal)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 交错读 discarded pass（整段，未记录结果）：{S(discard)}，占 PerfHub {P(discard, runTotal)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- PerfHub 夹具固定（建连/铺表/交错复位/落盘/报告）：{S(hubFixed)}，占 PerfHub {P(hubFixed, runTotal)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 未归因（项间间隙、进度与采样线程启停）：{S(b.UnattributedSeconds)}，占 PerfHub {P(b.UnattributedSeconds, runTotal)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 编排层固定（不含 PerfHub 步）：{S(orchestration.Sum(static f => f.Seconds))}（{orchestration.Count} 步；明细见第 7 节）");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"> 对账：记录项合计 {S(recorded.Sum(static i => i.TotalSeconds))} + discarded {S(discard)}"
            + $" + 非重叠相位 {S(b.PhaseNonOverlapSeconds)} + 落盘/报告 {S(raw.SaveSeconds + raw.ReportBuildSeconds)}"
            + $" + 未归因 {S(b.UnattributedSeconds)}"
            + $" = {S(recorded.Sum(static i => i.TotalSeconds) + discard + b.PhaseNonOverlapSeconds + raw.SaveSeconds + raw.ReportBuildSeconds + b.UnattributedSeconds)}"
            + $" vs run 总墙钟 {S(runTotal)}");
        sb.AppendLine();
    }

    private static void AppendStageTable(StringBuilder sb, TimingBreakdown b)
    {
        double itemsTotal = b.ItemsTotalSeconds;
        sb.AppendLine("## 2. 按阶段聚合（全部测量项，含 discarded pass）");
        sb.AppendLine();
        sb.AppendLine("| 阶段 | 合计 | 占测量项总耗时 | 说明 |");
        sb.AppendLine("| --- | ---: | ---: | --- |");
        foreach (string stage in StageNames.Append("write-result"))
        {
            string note = stage switch
            {
                "prepare-before" => "预热前重置/播种（prepare 不计入指标）",
                "warmup" => "按时间收敛的预热（不计入样本）",
                "prepare-after" => "预热后、计时前重置",
                "probe" => "单次探针，用于自适应迭代数",
                "pre-gc" => "阻塞三重 GC + 分配基线读取",
                "timed" => "计时循环（含峰值采样线程生命周期）",
                "post" => "分配统计/中位数/样本落盘",
                _ => "结果登记 + 进度行（测量外层）"
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {stage} | {S(b.StageTotals[stage])} | {P(b.StageTotals[stage], itemsTotal)} | {note} |");
        }
        sb.AppendLine();
    }

    /// <summary>按方言聚合——三方言串行执行的时间分布，是"并行是否值得"的直接输入（T4）。</summary>
    private static void AppendDialectTable(StringBuilder sb, TimingBreakdown b)
    {
        sb.AppendLine("## 3. 按方言聚合（串行执行的分布）");
        sb.AppendLine();
        sb.AppendLine("| 方言 | 项数 | 总墙钟 | 占测量项 |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (TimingDialectAggregate d in b.ByDialect)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {d.Dialect} | {d.Count} | {S(d.TotalSeconds)} | {P(d.ShareOfItems, 1)} |");
        }
        sb.AppendLine();
    }

    /// <summary>按档位聚合——两档的成本分布（行数不进测量的项只在 2000 档跑）。</summary>
    private static void AppendTierTable(StringBuilder sb, TimingBreakdown b)
    {
        sb.AppendLine("## 4. 按档位聚合");
        sb.AppendLine();
        sb.AppendLine("| 档位 | 项数 | 总墙钟 | 占测量项 |");
        sb.AppendLine("| ---: | ---: | ---: | ---: |");
        foreach (TimingTierAggregate t in b.ByTier)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {t.Rows} | {t.Count} | {S(t.TotalSeconds)} | {P(t.ShareOfItems, 1)} |");
        }
        sb.AppendLine();
    }

    private static void AppendOperationTable(StringBuilder sb, TimingBreakdown b)    {
        sb.AppendLine("## 5. 按操作聚合（记录项，降序）");
        sb.AppendLine();
        sb.AppendLine("| 操作 | 项数 | 总墙钟 | 预热 | 计时 | 重置 | 预热达 1.4s 项数 | 占测量项 |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (TimingOperationAggregate op in b.ByOperation)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {op.Operation} | {op.Count} | {S(op.TotalSeconds)} | {S(op.WarmupSeconds)} "
                + $"| {S(op.TimedSeconds)} | {S(op.PrepareSeconds)} | {op.BudgetBoundCount} | {P(op.ShareOfItems, 1)} |");
        }
        sb.AppendLine();
    }

    private static void AppendParetoTable(StringBuilder sb, TimingBreakdown b)
    {
        sb.AppendLine("## 6. Pareto：最慢 30 项（含各阶段占比）");
        sb.AppendLine();
        sb.AppendLine("| # | 方言 | 档 | 臂 | 操作 | 总墙钟 | prepare | warmup | probe | timed | post | 记录 |");
        sb.AppendLine("| ---: | --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | :---: |");
        int rank = 0;
        foreach (PerfStageTiming item in b.Items.OrderByDescending(static i => i.TotalSeconds).Take(30))
        {
            rank++;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {rank} | {item.Dialect} | {item.Rows} | {item.Arm} | {item.Operation} "
                + $"| {S(item.TotalSeconds)} | {P(item.PrepareBeforeSeconds, item.TotalSeconds)} "
                + $"| {P(item.WarmupSeconds, item.TotalSeconds)} | {P(item.ProbeSeconds, item.TotalSeconds)} "
                + $"| {P(item.TimedSeconds, item.TotalSeconds)} | {P(item.PostSeconds, item.TotalSeconds)} "
                + $"| {(item.Recorded ? "是" : "否")} |");
        }
        sb.AppendLine();
    }

    private static void AppendFixedCostTable(StringBuilder sb, TimingBreakdown b)
    {
        sb.AppendLine("## 7. 固定成本（编排与夹具）");
        sb.AppendLine();
        sb.AppendLine("| 步骤 | 墙钟 |");
        sb.AppendLine("| --- | ---: |");
        foreach (PerfInnerStep step in b.FixedCosts.OrderByDescending(static f => f.Seconds))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {step.Name} | {S(step.Seconds)} |");
        }
        sb.AppendLine();
    }

    private static void AppendSelfCheck(StringBuilder sb, TimingBreakdown b)
    {
        int over5 = 0;
        double worst = 0;
        foreach (PerfStageTiming item in b.Items)
        {
            if (item.TotalSeconds <= 0)
            {
                continue;
            }
            double sum = StageNames.Sum(s => StageOf(item, s));
            double deviation = Math.Abs(item.TotalSeconds - sum) / item.TotalSeconds;
            if (deviation > 0.05)
            {
                over5++;
            }
            worst = Math.Max(worst, deviation);
        }

        sb.AppendLine("## 8. 自检");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 阶段之和与单项墙钟的偏差：最大 {P(worst, 1)}，超过 5% 的项 {over5}/{b.Items.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- PerfHub 总墙钟与（测量项 + 非重叠相位 + 落盘 + 报告）之差：{S(b.UnattributedSeconds)}（{P(b.UnattributedSeconds, b.PerfHubRunSeconds)}）");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- 记录项 {b.Items.Count(static i => i.Recorded)} / 丢弃项 {b.Items.Count(static i => !i.Recorded)}");
        sb.AppendLine();
        sb.AppendLine("> 偏差 >5% 的处置：先查是否有相位未采集（如 BDN 内部 / 子进程启动），"
            + "再进入优化；不要用未归因余量当「可压缩」证据。");
    }

    private static double PhaseTotal(TimingBreakdown b, string name)
        => b.PhaseTotals.FirstOrDefault(p => p.Name == name)?.TotalSeconds ?? 0;

    private static string S(double seconds)
        => seconds.ToString(seconds >= 100 ? "F0" : "F1", Inv) + "s";

    /// <summary>占比（百分比）。参数已是 double——显式 (double) 转型会触发 S1905；
    /// 真正要防的是 long/long 静默截断（B148），那类算式在调用方保持 double。</summary>
    private static string P(double part, double whole)
        => whole <= 0 ? "—" : (part / whole * 100).ToString("F1", Inv) + "%";
}

/// <summary>时间分解的机器可读产物（供后续脚本算 Pareto/趋势）。</summary>
internal sealed class TimingBreakdown
{
    public int Schema { get; set; } = 1;
    public string GeneratedAt { get; set; } = "";
    public string Commit { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public double PerfHubRunSeconds { get; set; }
    public int FullTotalSeconds { get; set; }
    public double ItemsTotalSeconds { get; set; }
    public double PhaseNonOverlapSeconds { get; set; }
    public double UnattributedSeconds { get; set; }
    public Dictionary<string, double> StageTotals { get; set; } = [];
    public List<PerfInnerStep> FixedCosts { get; set; } = [];
    public List<TimingPhaseAggregate> PhaseTotals { get; set; } = [];
    public List<TimingDialectAggregate> ByDialect { get; set; } = [];
    public List<TimingTierAggregate> ByTier { get; set; } = [];
    public List<TimingOperationAggregate> ByOperation { get; set; } = [];
    public List<PerfStageTiming> Items { get; set; } = [];
}

internal sealed class TimingPhaseAggregate
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public double TotalSeconds { get; set; }
}

internal sealed class TimingOperationAggregate
{
    public string Operation { get; set; } = "";
    public int Count { get; set; }
    public double TotalSeconds { get; set; }
    public double AverageSeconds { get; set; }
    public double WarmupSeconds { get; set; }
    public double TimedSeconds { get; set; }
    public double PrepareSeconds { get; set; }
    /// <summary>预热达到时间预算（≥1.4s）的项数——这些项能通过下调预热预算直接省时。</summary>
    public int BudgetBoundCount { get; set; }
    public double ShareOfItems { get; set; }
}

internal sealed class TimingDialectAggregate
{
    public string Dialect { get; set; } = "";
    public int Count { get; set; }
    public double TotalSeconds { get; set; }
    public double ShareOfItems { get; set; }
}

internal sealed class TimingTierAggregate
{
    public int Rows { get; set; }
    public int Count { get; set; }
    public double TotalSeconds { get; set; }
    public double ShareOfItems { get; set; }
}

[JsonSerializable(typeof(PerfTimingReport))]
[JsonSerializable(typeof(PerfInnerStep))]
[JsonSerializable(typeof(List<PerfInnerStep>))]
[JsonSerializable(typeof(TimingBreakdown))]
internal sealed partial class TimingCliJsonContext : JsonSerializerContext;
