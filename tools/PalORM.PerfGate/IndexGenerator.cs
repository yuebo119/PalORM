using System.Globalization;
using System.Text;
using System.Text.Json;
using PalORM.Bench.Shared;

namespace PalORM.PerfGate;

/// <summary>结果库索引报告（规范 v2 §6）——扫 <c>bench/results/*.json</c> 生成
/// <c>bench/reports/perf-index.md</c>。
/// <para><b>它回答四个问题</b>：①有哪些批次、各是什么提交与版本；②每套夹具最近一次在什么口径下跑
///（连接配置 + 会话生命周期 + 健康度）——这三项缺失是"三套夹具数字不可互比"的根因；
/// ③哪些批次有失败登记（矩阵不完整，不得当作"该夹具已覆盖"）；
/// ④关键项的 PalORM 比值速览，明细留给各夹具自己的报告。</para>
/// <para>不重复各夹具的明细：PerfHub 有 HTML、微基准有 BENCHMARKS.md、DapperSuite 有 README，
/// 索引只做"跨夹具可查"的那一层。同一份内容通过 <see cref="TryAppend"/> 内嵌进
/// 统一报告（<c>perf.sh report</c>），使一次跑测只有一个报告产物。</para></summary>
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

        AppendBody(md, runs);

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, md.ToString());
        Console.WriteLine($"[PerfGate] 结果索引已生成: {outputPath}（{runs.Count} 个批次）");
        return 0;
    }

    /// <summary>把跨夹具登记节追加到一份已有报告（统一报告用，避免第二份产物）。
    /// 目录缺失或无批次时返回 false 并给出原因，不抛异常——统一报告的主干是 BDN 门禁，
    /// 结果库缺位只应降级为"该节未采集"。</summary>
    public static bool TryAppend(StringBuilder md, string resultsDir, out string reason)
    {
        if (!Directory.Exists(resultsDir))
        {
            reason = $"结果库目录不存在: {resultsDir}";
            return false;
        }

        List<PerfResultEnvelope> runs = LoadRuns(resultsDir);
        if (runs.Count == 0)
        {
            reason = $"结果库里没有可解析的批次: {resultsDir}";
            return false;
        }

        AppendBody(md, runs);
        reason = "";
        return true;
    }

    /// <summary>三节主体：最近一批可引用 / 全部批次 / 关键项速览。</summary>
    private static void AppendBody(StringBuilder md, List<PerfResultEnvelope> runs)
    {
        AppendLatest(md, runs);
        AppendAllRuns(md, runs);
        AppendKeyItems(md, runs);
    }

    /// <summary>读取结果库全部批次（含子集标记），按时间倒序；目录不存在时返回空表。
    /// 统一报告用它判定"某维度本轮是否真被采集"——按实测批次判定，不按功能是否存在写死。</summary>
    public static List<PerfResultEnvelope> LoadAll(string resultsDir)
        => Directory.Exists(resultsDir) ? LoadRuns(resultsDir) : [];

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
        md.AppendLine("## 各夹具最近一批（可引用）");
        md.AppendLine();
        md.AppendLine("> 子集批次（label 带 `quick`/`filtered`/`gate-set`/`workload`/`memory`/`stability`）不在此表，"
            + "它们不是该夹具的完整矩阵；全部批次见下一节。");
        md.AppendLine("> 取**最近一批非子集批次**，完整性由行内两列披露：`方言范围`（缺哪几个方言）与"
            + "`失败登记`（方言级/单项失败的条数）。不自动跳过带失败的批次——"
            + "实测（2026-09-23 全量）386 项、只缺 MySQL 的批次会被\"优先选无失败批次\"的规则跳过，"
            + "于是表里显示的是前一天 152 项的旧批次，把最新最全的数据藏了起来。");
        md.AppendLine();
        md.AppendLine("| 夹具 | 时间 | 提交 | 版本 | 标签 | 方言范围 | 连接配置口径 | 会话口径 | 健康度 | 项数 | 失败登记 | 明细 |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---:|---:|---|");
        foreach (IGrouping<string, PerfResultEnvelope> group in runs.GroupBy(static r => r.Harness))
        {
            // 候选 = 非子集批次；优先无失败登记的那一批（宁缺勿误导）
            List<PerfResultEnvelope> candidates =
                [.. group.Where(static x => !PerfResultWriter.IsSubsetLabel(x.Label))];
            if (candidates.Count == 0) continue;
            PerfResultEnvelope r = SelectQuotable(candidates);
            int failures = FailureSections(r).Count;
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Harness} | {r.Timestamp} | `{r.Commit}` | {r.Version} | {r.Label} | "
                + $"{DialectScope(r)} | {Shorten(r.Regime.ConnectionConfig)} | {Shorten(r.Regime.SessionLifecycle)} | "
                + $"{Health(r)} | {r.Items.Count} | {(failures > 0 ? $"⚠️ {failures}（本批不完整）" : "—")} | {Detail(r)} |");
        }

        md.AppendLine();
        AppendFailures(md, runs);
    }

    /// <summary>失败登记节——方言级/单项失败会让该批次的矩阵不完整。
    /// <para><b>为什么必须出现在报告里</b>：失败是"登记在信封的 sections 里"的，
    /// 而报告此前只渲染 items。于是 PG/MySQL 连接超时时，报告照旧显示"项数 1、健康度 clean"，
    /// 读者会把它当成"该夹具已覆盖"——这正是"一次跑测出可靠报告"要堵的洞。</para></summary>
    private static void AppendFailures(StringBuilder md, List<PerfResultEnvelope> runs)
    {
        var rows = new List<(string Harness, string Timestamp, PerfResultSection Section)>();
        foreach (IGrouping<string, PerfResultEnvelope> group in runs.GroupBy(static r => r.Harness))
        {
            // 只报"可引用批次"（= 最近一批非子集批次）自己的失败——与上一张表同一批次，
            // 两表对齐读者才不会误以为在说两个不同的批次。
            List<PerfResultEnvelope> candidates =
                [.. group.Where(static x => !PerfResultWriter.IsSubsetLabel(x.Label))];
            if (candidates.Count == 0) continue;
            PerfResultEnvelope r = SelectQuotable(candidates);
            rows.AddRange(FailureSections(r).Select(s => (r.Harness, r.Timestamp, s)));
        }

        md.AppendLine("## 批次失败登记（可引用批次）");
        md.AppendLine();
        md.AppendLine("> 反复出现的失败见下一节「全部批次」表的 `失败登记` 列——本节只展开最近一批；"
            + "只留本节会把跨批次的规律藏起来（实测 MySQL `BulkDelete` 在 2026-09-22 与 09-23 的"
            + "5 个批次里反复失败，单看某一批像是偶发）。");
        md.AppendLine();
        if (rows.Count == 0)
        {
            md.AppendLine("无失败登记——各夹具最近的非子集批次矩阵完整。");
            md.AppendLine();
            return;
        }

        md.AppendLine("> **有登记的批次不得当作「该夹具已覆盖」**：方言级失败会让整列方言缺失，"
            + "单项失败会让某个 (实现 × 操作) 组合缺失。缺的项在报告里表现为行数变少，"
            + "不看本节就会把它读成「没测这项」而非「测失败了」。");
        md.AppendLine();
        md.AppendLine("| 夹具 | 时间 | 类型 | 方言 | 上下文 | 原因 |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach ((string harness, string timestamp, PerfResultSection s) in rows)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {harness} | {timestamp} | {s.Kind} | {s.Dialect} | {s.Label} | {Escape(s.Note)} |");
        }

        md.AppendLine();
    }

    /// <summary>批次实际覆盖的方言范围——从 items 的方言列推导（`—` 是数据生成基线，与方言无关）。
    /// <para><b>为什么必须显式列出</b>：一次 <c>--dialects sqlite</c> 的跑测标签是非子集
    /// （不含 quick/filtered 等），项数看着也不少，读者会把它当成"三方言全矩阵"。
    /// 实测 PG/MySQL 不可达期间的批次正是这种形态。</para></summary>
    internal static string DialectScope(PerfResultEnvelope r)
    {
        string[] dialects = [.. r.Items
            .Select(static i => i.Dialect)
            .Where(static d => !string.IsNullOrEmpty(d) && d != "—")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        return dialects.Length == 0 ? "—" : string.Join('+', dialects);
    }

    /// <summary>从非子集批次中选"可引用"的那一批：**取最近一批**（<paramref name="candidates"/>
    /// 已按时间倒序，故 <c>[0]</c> 即最近）。
    /// <para>曾经的规则是"优先无失败登记"，理由是残缺矩阵不该被当成完整矩阵。实测证明该规则
    /// 会反向伤人：2026-09-23 全量跑出 386 项、只缺 MySQL 的批次，被跳过而显示了前一天 152 项的
    /// 旧批次——读者拿到的是更旧更少的数据。完整性改由两列披露（<see cref="DialectScope"/> +
    /// 失败登记条数），不靠隐藏批次。</para></summary>
    private static PerfResultEnvelope SelectQuotable(List<PerfResultEnvelope> candidates)
        => candidates[0];

    private static List<PerfResultSection> FailureSections(PerfResultEnvelope r)
        => [.. r.Sections.Where(static s => s.Kind.Contains("failure", StringComparison.OrdinalIgnoreCase))];

    /// <summary>表格单元格里的竖线与换行会破坏 markdown 结构。</summary>
    private static string Escape(string text)
        => text.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static void AppendAllRuns(StringBuilder md, List<PerfResultEnvelope> runs)
    {
        md.AppendLine("## 全部批次（倒序）");
        md.AppendLine();
        md.AppendLine("> `失败登记` 列让**跨批次反复出现的失败**可见（上一节只展开最近一批）。");
        md.AppendLine();
        md.AppendLine("| 夹具 | 时间 | 提交 | 版本 | 标签 | 健康度 | 健康度依据(StdDev/Mean) | 项数 | 失败登记 | 耗时 s |");
        md.AppendLine("|---|---|---|---|---|---|---:|---:|---:|---:|");
        foreach (PerfResultEnvelope r in runs)
        {
            int failures = FailureSections(r).Count;
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Harness} | {r.Timestamp} | `{r.Commit}` | {r.Version} | {r.Label} | {Health(r)} | "
                + $"{(r.Regime.HealthRatio > 0 ? r.Regime.HealthRatio.ToString("P1", CultureInfo.InvariantCulture) : "未采")} | "
                + $"{r.Items.Count} | {(failures > 0 ? $"⚠️ {failures}" : "—")} | {r.ElapsedSeconds:F1} |");
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
            // 与"可引用"表同一选取口径：优先无失败登记的非子集批次。
            // 用 group.First() 会取到最新的**子集**批次（如一次方言全失败的探测批次只剩 1 项），
            // 读者会把残缺矩阵当成该夹具的能力矩阵。
            List<PerfResultEnvelope> candidates =
                [.. group.Where(static x => !PerfResultWriter.IsSubsetLabel(x.Label))];
            if (candidates.Count == 0) continue;
            PerfResultEnvelope r = SelectQuotable(candidates);
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
