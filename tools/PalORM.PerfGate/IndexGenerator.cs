using System.Globalization;
using System.Text;
using System.Text.Json;
using PalORM.Bench.Shared;

namespace PalORM.PerfGate;

/// <summary>结果库索引报告（规范 v2 §6）——扫 <c>bench/results/*.json</c> 生成
/// <c>bench/reports/perf-index.md</c>。
/// <para><b>它回答三个问题</b>：①有哪些批次、各是什么提交与版本；②每套夹具最近一次在什么口径下跑
///（连接配置 + 会话生命周期 + 健康度）——这三项缺失是"三套夹具数字不可互比"的根因；
/// ③关键项的 PalORM 比值速览，明细留给各夹具自己的报告。</para>
/// <para>不重复各夹具的明细：PerfHub 有 HTML、微基准有 BENCHMARKS.md、DapperSuite 有 README，
/// 索引只做"跨夹具可查"的那一层。</para></summary>
internal static class IndexGenerator
{
    /// <summary>生成索引报告；返回退出码（0 成功）。</summary>
    public static int Generate(string resultsDir, string outputPath)
    {
        if (!Directory.Exists(resultsDir))
        {
            Console.Error.WriteLine($"FATAL: 结果库目录不存在: {resultsDir}");
            return 1;
        }

        List<PerfResultEnvelope> runs = LoadRuns(resultsDir);
        if (runs.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: 结果库里没有可解析的批次（{resultsDir}）");
            return 1;
        }

        var md = new StringBuilder();
        md.AppendLine("# 性能结果索引");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"> 生成时间 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · "
            + $"批次 {runs.Count} · 由 `tools/PalORM.PerfGate index` 从 `bench/results/` 生成。");
        md.AppendLine("> 权威归属（哪套夹具负责哪些项）见 `docs/性能基准规范.md` §1.1；"
            + "口径定义见 §4.1；健康度判据见 §4.2。");
        md.AppendLine("> **跨夹具比绝对值没有意义**：每套夹具的形状、档位、连接口径都不同，"
            + "只有同一批次内的比值可比。");
        md.AppendLine();

        AppendLatest(md, runs);
        AppendAllRuns(md, runs);
        AppendKeyItems(md, runs);

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, md.ToString());
        Console.WriteLine($"[PerfGate] 结果索引已生成: {outputPath}（{runs.Count} 个批次）");
        return 0;
    }

    private static List<PerfResultEnvelope> LoadRuns(string resultsDir)
    {
        var runs = new List<PerfResultEnvelope>();
        foreach (string file in Directory.EnumerateFiles(resultsDir, "*.json"))
        {
            // latest-*.json 是副本，只读带时间戳的批次文件，避免同一批出现两次
            if (Path.GetFileName(file).StartsWith("latest-", StringComparison.Ordinal)) continue;
            try
            {
                PerfResultEnvelope? run = JsonSerializer.Deserialize(
                    File.ReadAllText(file), PerfResultJsonContext.Default.PerfResultEnvelope);
                if (run is not null) runs.Add(run);
            }
            catch (JsonException)
            {
                // 半写坏的文件不该让整个索引失败
                Console.Error.WriteLine($"[PerfGate] 跳过无法解析的结果文件: {file}");
            }
        }

        runs.Sort(static (a, b) => string.CompareOrdinal(b.Timestamp, a.Timestamp));
        return runs;
    }

    private static void AppendLatest(StringBuilder md, List<PerfResultEnvelope> runs)
    {
        md.AppendLine("## 各夹具最近一批");
        md.AppendLine();
        md.AppendLine("| 夹具 | 时间 | 提交 | 版本 | 标签 | 连接配置口径 | 会话口径 | 健康度 | 项数 | 明细 |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---:|---|");
        foreach (IGrouping<string, PerfResultEnvelope> group in runs.GroupBy(static r => r.Harness))
        {
            PerfResultEnvelope r = group.First();
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Harness} | {r.Timestamp} | `{r.Commit}` | {r.Version} | {r.Label} | "
                + $"{Shorten(r.Regime.ConnectionConfig)} | {Shorten(r.Regime.SessionLifecycle)} | "
                + $"{Health(r)} | {r.Items.Count} | {Detail(r)} |");
        }

        md.AppendLine();
    }

    private static void AppendAllRuns(StringBuilder md, List<PerfResultEnvelope> runs)
    {
        md.AppendLine("## 全部批次（倒序）");
        md.AppendLine();
        md.AppendLine("| 夹具 | 时间 | 提交 | 版本 | 标签 | 健康度 | 健康度依据(StdDev/Mean) | 项数 | 耗时 s |");
        md.AppendLine("|---|---|---|---|---|---|---:|---:|---:|");
        foreach (PerfResultEnvelope r in runs)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Harness} | {r.Timestamp} | `{r.Commit}` | {r.Version} | {r.Label} | {Health(r)} | "
                + $"{(r.Regime.HealthRatio > 0 ? r.Regime.HealthRatio.ToString("P1", CultureInfo.InvariantCulture) : "未采")} | "
                + $"{r.Items.Count} | {r.ElapsedSeconds:F1} |");
        }

        md.AppendLine();
    }

    /// <summary>关键项速览：只列 PalORM 臂、且同批内有地板的项（比值可比）。
    /// 项名跨夹具不一致（GetByKey / FirstOrDefault&lt;T&gt; / ADO_NET_QueryAll 等），
    /// 故按夹具分组列出，不做跨夹具对齐——对齐是人的判断，不是脚本的。</summary>
    private static void AppendKeyItems(StringBuilder md, List<PerfResultEnvelope> runs)
    {
        md.AppendLine("## 关键项速览（各夹具最近一批的 PalORM 臂）");
        md.AppendLine();
        foreach (IGrouping<string, PerfResultEnvelope> group in runs.GroupBy(static r => r.Harness))
        {
            PerfResultEnvelope r = group.First();
            List<PerfResultItem> palorm = [.. r.Items.Where(static i =>
                i.Arm.Equals("PalORM", StringComparison.OrdinalIgnoreCase) && i.Ratio > 0)];
            if (palorm.Count == 0) continue;

            md.AppendLine(CultureInfo.InvariantCulture,
                $"### {r.Harness}（{r.Timestamp} · `{r.Commit}` · 健康度 {Health(r)}）");
            md.AppendLine();
            md.AppendLine("| 项 | 方言 | 档位 | 均值 µs | 比值(对地板) | 分配 B/op | 往返/op | prepared 复用 |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
            foreach (PerfResultItem i in palorm
                .OrderBy(static i => i.Dialect, StringComparer.Ordinal)
                .ThenBy(static i => i.Name, StringComparer.Ordinal)
                .ThenBy(static i => i.Tier))
            {
                md.AppendLine(CultureInfo.InvariantCulture,
                    $"| {i.Name} | {i.Dialect} | {i.Tier} | {i.MeanUs:F2} | {i.Ratio:F2} | {i.AllocBytes} | "
                    + $"{(i.RoundTripsPerOp > 0 ? i.RoundTripsPerOp.ToString("F2", CultureInfo.InvariantCulture) : "未测")} | "
                    + $"{(i.PreparedReuse > 0 ? i.PreparedReuse.ToString("P0", CultureInfo.InvariantCulture) : "未测")} |");
            }

            md.AppendLine();
        }
    }

    private static string Health(PerfResultEnvelope r)
        => r.Regime.Health switch
        {
            "clean" => "✅ clean",
            "noisy" => "⚠️ noisy",
            _ => "❔ unknown"
        };

    private static string Detail(PerfResultEnvelope r)
        => string.IsNullOrEmpty(r.DetailPath) ? "（未登记）" : "`" + r.DetailPath + "`";

    private static string Shorten(string text)
        => text.Length <= 48 ? text : text[..45] + "...";
}
