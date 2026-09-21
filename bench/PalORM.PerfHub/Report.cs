using System.Text;
using System.Text.Json;

namespace PalORM.PerfHub;

/// <summary>统一报告生成器——HTML + 内联 SVG。
///
/// 报告包含六个区块：
///  ① 单操作指标总览：按测试分组（Build/CRUD/Query/Transaction/Baseline）分表，
///     时延中位数/均值、Error/Mean、分配/op、采样堆峰、Gen0；
///  ② ORM vs ADO.NET 地板比值（跨环境可比的核心口径）；
///  ③ 并发吞吐（ops/s + p50/p95/p99）；
///  ④ 版本对比（v5.5.1 vs 最新）——同项比值 + 提升幅度；
///  ⑤ 增长曲线（跨历史运行，SVG 折线，对数轴）；
///  ⑥ 说明与未覆盖维度。
///
/// 设计纪律：
///  · 跨环境绝对值禁止直接比较（规范 §3）——比值以同轮 ADO.NET 为分母；
///  · SVG 手写，零第三方绘图依赖；
///  · 所有数字来自 JSON 历史文件，报告可重复生成（report 子命令）。
/// </summary>
internal static class Report
{
    /// <summary>分组展示顺序——构建开销在最前（它是纯 ORM 成本，与数据库无关），
    /// 然后是读写、专业查询、事务、数据基线。</summary>
    private static readonly (string Group, string Title)[] GroupTitles =
        [
            ("Build", "SQL 构建开销（不执行，纯 ORM 成本）"),
            ("CRUD", "增删改查（点查 / 全查 / 流式 / 单条写 / 批量写）"),
            ("Query", "专业查询（键集分页 / IN / 计数）"),
            ("Transaction", "事务场景（提交 / 回滚）"),
            ("Baseline", "数据生成基线（不含 ORM 与数据库）")
        ];

    public static int Build()
    {
        string dir = Path.Combine(FindRepoRoot(), "bench", "perfhub", "results");
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine("没有结果目录——先跑 run");
            return 1;
        }

        string[] files = [.. Directory.GetFiles(dir, "history-*.json").OrderBy(static f => f, StringComparer.Ordinal)];
        if (files.Length == 0)
        {
            Console.Error.WriteLine("没有历史数据——先跑 run");
            return 1;
        }

        var runs = new List<PerfRun>();
        foreach (string? f in files)
        {
            try
            {
                PerfRun? run = JsonSerializer.Deserialize(File.ReadAllText(f), PerfJsonContext.Default.PerfRun);
                if (run is not null)
                {
                    runs.Add(run);
                }
            }
            catch (JsonException) { /* 跳过损坏文件 */ }
        }
        if (runs.Count == 0)
        {
            return 1;
        }

        string html = Render(runs);
        string outPath = Path.Combine(FindRepoRoot(), "bench", "perfhub", "report.html");
        File.WriteAllText(outPath, html);
        Console.WriteLine($"[PerfHub] 报告已生成: bench/perfhub/report.html（{runs.Count} 次历史运行）");
        return 0;
    }

    private static string Render(List<PerfRun> runs)
    {
        PerfRun latest = runs[^1];
        var sb = new StringBuilder();
        sb.Append(Header(latest, runs.Count));
        sb.Append(Overview(latest));
        sb.Append(RatioAndConcurrency(latest));
        if (HasVersionPair(runs))
        {
            sb.Append(VersionCompare(runs));
        }

        string ab = AbCompare(runs);
        if (ab.Length > 0)
        {
            sb.Append(ab);
        }

        if (runs.Count >= 2)
        {
            sb.Append(GrowthCurves(runs));
        }

        sb.Append(Notes(latest));
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>是否存在至少两个不同版本的历史运行——有才出版本对比段。</summary>
    private static bool HasVersionPair(List<PerfRun> runs)
        => runs.Select(static r => r.Version).Distinct(StringComparer.Ordinal).Count() >= 2;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HTML 头部与样式
    // ─────────────────────────────────────────────────────────────────────────

    private static string Header(PerfRun latest, int runCount)
    {
        PerfEnvironment e = latest.Environment;
        return DocType
            + "<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">"
            + "<title>PalORM 统一性能测试报告</title>"
            + "<style>" + Css + "</style></head><body>"
            + "<div class=\"wrap\">"
            + "<h1>PalORM 统一性能测试报告</h1>"
            + "<div class=\"sub\">"
            + "运行时间 <b>" + latest.Timestamp + "</b> · 标签 <b>" + Esc(latest.Label)
            + "</b> · 历史运行 <b>" + runCount + "</b> 次<br>"
            + "环境 " + e.Machine + " · " + e.Processor + " · " + e.Runtime
            + " · GC " + e.GcMode + " · " + e.Os + "<br>"
            + "<span class=\"badge\">数据集 S1 Narrow</span>"
            + "<span class=\"badge\">三方言同构</span>"
            + "<span class=\"badge\">ADO.NET 地板基线</span>"
            + "</div>";
    }

    private const string DocType = "<!DOCTYPE html>";

    private const string Css =
        ":root{--bg:#f4f5f7;--card:#fff;--line:#e5e7eb;--txt:#1f2937;--mut:#6b7280;"
        + "--good:#059669;--bad:#dc2626;--warn:#d97706}"
        + "*{box-sizing:border-box}"
        + "body{font-family:Segoe UI,system-ui,-apple-system,sans-serif;background:var(--bg);"
        + "margin:0;padding:26px;color:var(--txt);font-size:13px}"
        + ".wrap{max-width:1500px;margin:0 auto}"
        + "h1{font-size:22px;margin:0 0 4px;letter-spacing:-.2px}"
        + "h2{font-size:15px;margin:30px 0 12px;padding-bottom:7px;border-bottom:2px solid var(--line)}"
        + ".sub{color:var(--mut);font-size:12.5px;margin-bottom:20px;line-height:1.7}"
        + ".card{background:var(--card);border-radius:10px;padding:16px;"
        + "box-shadow:0 1px 3px rgba(0,0,0,.07);margin-bottom:16px;overflow-x:auto}"
        + "table{border-collapse:collapse;width:100%;font-size:12.5px}"
        + "th,td{padding:7px 10px;text-align:left;border-bottom:1px solid var(--line);white-space:nowrap}"
        + "th{background:#f9fafb;font-weight:600;font-size:11.5px;color:#374151}"
        + "td.n{text-align:right;font-variant-numeric:tabular-nums}"
        + "tr:hover td{background:#fafbfc}"
        + ".impl{font-weight:600}"
        + ".impl-ADO_NET{color:#6b7280}.impl-Dapper{color:#2563eb}.impl-PalORM{color:#059669}"
        + ".grid2{display:grid;grid-template-columns:1fr 1fr;gap:16px}"
        + ".good{color:var(--good)}.bad{color:var(--bad)}.warn{color:var(--warn)}"
        + ".mut{color:var(--mut);font-weight:400;font-size:12px}"
        + ".note{font-size:12px;color:var(--mut);line-height:1.85;margin-top:10px}"
        + ".note b{color:#374151}"
        + ".badge{display:inline-block;padding:1px 7px;border-radius:20px;font-size:10.5px;"
        + "font-weight:600;background:#eef2ff;color:#4338ca;margin-left:6px}"
        + "svg{display:block;max-width:100%}";

    private static string Esc(string? s)
        => string.IsNullOrEmpty(s)
            ? "—"
            : s.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);

    // ─────────────────────────────────────────────────────────────────────────
    // ① 单操作指标总览
    // ─────────────────────────────────────────────────────────────────────────

    private static string Overview(PerfRun run)
    {
        Measurement[] single = [.. run.Measurements
            .Where(static m => m.Operation != ConcurrentOp && m.Group != "Concurrency")];

        var sb = new StringBuilder();
        sb.Append("<h2>① 单操作指标总览（时延 · 内存 · 峰值 · 按测试分组）</h2>");

        foreach ((string? group, string? title) in GroupTitles)
        {
            Measurement[] rows = [.. single.Where(m => m.Group == group)
                .OrderBy(static m => m.Rows).ThenBy(static m => m.Operation).ThenBy(static m => m.Dialect)
                .ThenBy(static m => ImplOrder(m.Implementation))];
            if (rows.Length == 0)
            {
                continue;
            }

            sb.Append("<h3 style=\"font-size:13px;margin:18px 0 8px;color:#374151\">")
                .Append(group).Append(" — ").Append(Esc(title)).Append("</h3>");
            sb.Append("<div class=\"card\"><table>");
            sb.Append("<tr><th>行数</th><th>操作</th><th>方言</th><th>实现</th>"
                + "<th class=\"n\">时延中位数</th><th class=\"n\">时延均值</th><th class=\"n\">Error/Mean</th>"
                + "<th class=\"n\">分配/op</th><th class=\"n\">堆峰(采样)</th><th class=\"n\">Gen0</th></tr>");

            foreach (Measurement? m in rows)
            {
                string warnCls = m.ErrorRatio > 0.05 ? " class=\"n warn\"" : " class=\"n\"";
                sb.Append("<tr><td class=\"n\">").Append(m.Rows.ToString("N0"))
                    .Append("</td><td>").Append(Esc(m.Operation))
                    .Append("</td><td>").Append(m.Dialect)
                    .Append("</td><td class=\"impl impl-").Append(m.Implementation).Append("\">").Append(m.Implementation)
                    .Append("</td><td class=\"n\">").Append(Fmt.Time(m.MedianNs))
                    .Append("</td><td class=\"n\">").Append(Fmt.Time(m.MeanNs))
                    .Append("</td><td").Append(warnCls).Append('>').Append(m.ErrorRatio.ToString("P1"))
                    .Append("</td><td class=\"n\">").Append(Fmt.Bytes(m.AllocatedBytesPerOp))
                    .Append("</td><td class=\"n\">").Append(Fmt.Bytes(m.PeakHeapBytes))
                    .Append("</td><td class=\"n\">").Append(m.Gen0Collections)
                    .Append("</td></tr>");
            }
            sb.Append("</table></div>");
        }

        sb.Append("<div class=\"note\"><b>Build 组读法（重要）</b>：ADO.NET / Dapper 两臂的"
            + "<code>BuildXxxSql</code> 只是返回一条预先写好的字面量 SQL，测的是「不构建」的下限；"
            + "PalORM 臂测的是 <c>From&lt;T&gt;()/Where/OrderBy/Take</c> 链式构建 + 方言引用符 + "
            + "DryRun 出参的完整开销。两者不是同一件事，该组的差值应读作「ORM 构建税」，"
            + "不是「PalORM 查询慢 20 倍」——查询快慢看 CRUD/Query 组。<br>"
            + "<b>口径</b>：时延取中位数（同轮对照）；"
            + "<b>全路径</b>——被测动作内部包含 SQL 构建（From&lt;T&gt;()/Where/BuildSql）→ 执行 → 物化，"
            + "测量引擎不剥离任何段；种子数据的生成在测量前完成，不计入时延。"
            + "分配为 <code>GC.GetTotalAllocatedBytes</code> 精确计数（确定性指标，复现性优于 1%）；"
            + "堆峰为 <code>GC.GetGCMemoryInfo().HeapSizeBytes</code> 采样——.NET 无「操作期间曾达到的"
            + "最大值」API，故标注为<b>采样堆峰</b>而非精确峰值，它含 ORM 内部与数据对象的真实驻留；"
            + "Error/Mean &gt; 5% 标黄（量具自检参考，非失败）。</div>");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ② 比值表 + ③ 并发吞吐
    // ─────────────────────────────────────────────────────────────────────────

    private const string ConcurrentOp = "Concurrent_Mixed80_20";

    private static string RatioAndConcurrency(PerfRun run)
    {
        int suspectFloors = 0;
        Measurement[] single = [.. run.Measurements
            .Where(static m => m.Operation != ConcurrentOp)];
        IEnumerable<IGrouping<(int Rows, string Operation, string Dialect), Measurement>> groups = single.GroupBy(static m => (m.Rows, m.Operation, m.Dialect));

        var sb = new StringBuilder();
        sb.Append("<h2>② ORM vs ADO.NET 地板（比值 = 实现 / 地板）</h2><div class=\"card\"><table>");
        sb.Append("<tr><th>行数</th><th>操作</th><th>方言</th>"
            + "<th class=\"n\">Dapper 时延比</th><th class=\"n\">PalORM 时延比</th>"
            + "<th class=\"n\">Dapper 分配比</th><th class=\"n\">PalORM 分配比</th></tr>");

        foreach (IGrouping<(int Rows, string Operation, string Dialect), Measurement>? g in groups.OrderBy(static g => g.Key.Rows).ThenBy(static g => g.Key.Operation)
                 .ThenBy(static g => g.Key.Dialect))
        {
            Measurement? floor = g.FirstOrDefault(static m => m.Implementation == "ADO_NET");
            Measurement? dapper = g.FirstOrDefault(static m => m.Implementation == "Dapper");
            Measurement? palorm = g.FirstOrDefault(static m => m.Implementation == "PalORM");
            if (floor is null || floor.MedianNs <= 0)
            {
                continue;
            }

            sb.Append("<tr><td class=\"n\">").Append(g.Key.Rows.ToString("N0"))
                .Append("</td><td>").Append(g.Key.Operation)
                .Append("</td><td>").Append(g.Key.Dialect).Append("</td>");
            double? dapperRatio = dapper is null ? null : dapper.MedianNs / floor.MedianNs;
            double? palormRatio = palorm is null ? null : palorm.MedianNs / floor.MedianNs;
            // Build 组不入门禁：ADO/Dapper 臂只是返回预写字面量，与 ORM 真实构建非同类
            // 对比（三臂契约已声明），其比值在此口径下无物理意义
            if (!g.Key.Operation.StartsWith("Build", StringComparison.Ordinal)
                && ((dapperRatio is < 0.90) || (palormRatio is < 0.90)))
            {
                suspectFloors++;
            }

            sb.Append(RatioCell(dapperRatio));
            sb.Append(RatioCell(palormRatio));
            sb.Append(RatioCell(dapper is null || floor.AllocatedBytesPerOp <= 0
                ? null : dapper.AllocatedBytesPerOp / floor.AllocatedBytesPerOp));
            sb.Append(RatioCell(palorm is null || floor.AllocatedBytesPerOp <= 0
                ? null : palorm.AllocatedBytesPerOp / floor.AllocatedBytesPerOp));
            sb.Append("</tr>");
        }
        sb.Append("</table><div class=\"note\"><b>读法</b>：时延比 &lt; 1 表示比 ADO.NET 地板快；"
            + "分配比 &lt; 1 表示比地板省。跨机器绝对值不可比（同机 ADO.NET 地板漂移可达 43%），"
            + "<b>跨环境只比比值</b>（规范 §3）。"
            + "<b>健全性门禁</b>：" + suspectFloors + " 组出现 ORM 快过地板 >10%（标 ⚠地板?）——"
            + "物理上不成立，出现即该组地板实现有缺陷（低性能写法），须对照三臂契约修正地板后重测，"
            + "不得当 ORM 优势解读。</div></div>");

        Measurement[] conc = [.. run.Measurements.Where(static m => m.Operation == ConcurrentOp)];
        if (conc.Length > 0)
        {
            sb.Append("<h2>③ 并发吞吐（80/20 读写混合）</h2><div class=\"card\"><table>");
            sb.Append("<tr><th>行数</th><th>方言</th><th>实现</th><th class=\"n\">线程</th>"
                + "<th class=\"n\">ops/s</th><th class=\"n\">p50</th><th class=\"n\">p95</th>"
                + "<th class=\"n\">p99</th><th class=\"n\">样本</th></tr>");
            foreach (Measurement? m in conc.OrderBy(static m => m.Rows).ThenBy(static m => m.Dialect)
                     .ThenBy(static m => ImplOrder(m.Implementation)).ThenBy(static m => m.ConcurrencyThreads))
            {
                sb.Append("<tr><td class=\"n\">").Append(m.Rows.ToString("N0"))
                    .Append("</td><td>").Append(m.Dialect)
                    .Append("</td><td class=\"impl impl-").Append(m.Implementation)
                    .Append("\">").Append(m.Implementation)
                    .Append("</td><td class=\"n\">").Append(m.ConcurrencyThreads)
                    .Append("</td><td class=\"n\"><b>").Append(m.OpsPerSecond.ToString("N0"))
                    .Append("</b></td><td class=\"n\">").Append(m.P50Ms.ToString("F2")).Append(" ms")
                    .Append("</td><td class=\"n\">").Append(m.P95Ms.ToString("F2")).Append(" ms")
                    .Append("</td><td class=\"n\">").Append(m.P99Ms.ToString("F2")).Append(" ms")
                    .Append("</td><td class=\"n\">").Append(m.Iterations.ToString("N0"))
                    .Append("</td></tr>");
            }
            sb.Append("</table></div>");
        }
        return sb.ToString();
    }

    private static int ImplOrder(string impl) => impl switch
    {
        "ADO_NET" => 0,
        "Dapper" => 1,
        "PalORM" => 2,
        _ => 3
    };

    private static string RatioCell(double? ratio)
    {
        if (ratio is null)
        {
            return "<td class=\"n\">—</td>";
        }

        double r = ratio.Value;
        // v2 健全性门禁：手写 ADO.NET 是天花板，ORM 比它快 10% 以上在物理上不成立——
        // 出现即地板实现有缺陷（低性能写法），标 ⚠ 提示检查三臂契约，不再误标成"绿色优势"。
        if (r < 0.90)
        {
            return "<td class=\"n warn\">" + r.ToString("F2") + "× ⚠地板?</td>";
        }

        string cls = RatioClass(r, 0.95, 1.10);
        return "<td" + cls + ">" + r.ToString("F2") + "×</td>";
    }

    /// <summary>比值配色——优于阈值 5% 绿，劣于 10% 红，其余中性。</summary>
    /// <summary>A/B 判定四档：显著变快/显著变慢（全轮同向且越阈值）、
    /// 不可分辨（散布跨 1.0）、方向偏快/偏慢（其余）。</summary>
    private static string AbVerdict((string Dialect, string Tier, string Operation, string Impl,
        double Median, double Min, double Max, int Rounds) row)
    {
        if (row.Max < 0.90)
        {
            return "<span class=\"good\">显著变快</span>";
        }

        if (row.Min > 1.10)
        {
            return "<span class=\"bad\">显著变慢</span>";
        }

        if (row.Max >= 1.0 && row.Min <= 1.0)
        {
            return "<span class=\"mut\">不可分辨</span>";
        }

        return DirectionalVerdict(row.Median);
    }

    private static string DirectionalVerdict(double median)
    {
        if (median < 1.0)
        {
            return "方向偏快";
        }

        return "方向偏慢";
    }

    private static string RatioClass(double ratio, double goodBelow, double badAbove)
    {
        if (ratio < goodBelow) return " class=\"n good\"";
        return ratio > badAbove ? " class=\"n bad\"" : " class=\"n\"";
    }

    /// <summary>变化率配色——下降 2% 以上绿（改进），上升 2% 以上红（退化）。</summary>
    private static string DeltaClass(double delta)
    {
        if (delta < -0.02) return " class=\"n good\"";
        return delta > 0.02 ? " class=\"n bad\"" : " class=\"n\"";
    }

    /// <summary>几何平均的三档判读——优于 3% 记 good，劣于 3% 记 bad，其余中性。</summary>
    private static string GeoClass(double geo)
    {
        if (geo < 0.97) return "good";
        return geo > 1.03 ? "bad" : "";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ④ 版本对比
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>版本对比——每个版本取最近一次运行，取最早与最晚两个版本对照。
    /// <para>比较的是同一台机器、同一份数据、同一套测试项下的同项比值，
    /// 不是跨机器绝对值——后者不可比（规范 §3）。</para></summary>
    /// <summary>版本排序键——语义化版本按 (major, minor, patch) 升序，非语义化标识统一排到最后。</summary>
    private static (int Rank, int Major, int Minor, int Patch) VersionRank(string version)
    {
        string v = version.Trim();
        if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V'))
        {
            v = v[1..];
        }

        return System.Version.TryParse(v, out System.Version? parsed) && parsed is not null
            ? (0, parsed.Major, parsed.Minor, parsed.Build)
            : (1, 0, 0, 0);
    }

    private static string VersionCompare(List<PerfRun> runs)
    {
        // 排序规则：语义化版本号（v5.5.1）按版本号升序在前，非语义化标识（HEAD / main /
        // 自定义标签）排到最后视为"最新"。不按时间戳排序——两轮实测的执行顺序不固定，
        // 按时间戳会在"先跑新版本再补跑基线"时把对比方向搞反。
        PerfRun[] byVersion = [.. runs
            .GroupBy(static r => r.Version, StringComparer.Ordinal)
            .Select(static g => g.Last())
            .OrderBy(static r => VersionRank(r.Version))
            .ThenBy(static r => r.Timestamp, StringComparer.Ordinal)];
        if (byVersion.Length < 2)
        {
            return "";
        }

        PerfRun baseRun = byVersion[0];
        PerfRun headRun = byVersion[^1];
        if (ReferenceEquals(baseRun, headRun))
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.Append("<h2>④ 版本对比（").Append(Esc(baseRun.Version)).Append(" → ")
            .Append(Esc(headRun.Version)).Append("）</h2>");

        // 汇总：PalORM 臂的几何平均时延比（跨操作/方言/档位）
        var ratios = new List<double>();
        foreach (Measurement h in headRun.Measurements)
        {
            if (h.Implementation != "PalORM" || h.MedianNs <= 0)
            {
                continue;
            }

            Measurement? b = baseRun.Measurements.FirstOrDefault(x =>
                x.Implementation == "PalORM" && x.Operation == h.Operation &&
                x.Dialect == h.Dialect && x.Rows == h.Rows);
            if (b is not null && b.MedianNs > 0)
            {
                ratios.Add(h.MedianNs / b.MedianNs);
            }
        }
        if (ratios.Count > 0)
        {
            double geo = Math.Exp(ratios.Average(Math.Log));
            // 几何平均的三档判读：优于 3% 记 good，劣于 3% 记 bad，其余中性
            string cls = GeoClass(geo);
            sb.Append("<div class=\"card\"><b>PalORM 整体时延（几何平均，").Append(ratios.Count)
                .Append(" 项同口径对照）：</b><span class=\"").Append(cls)
                .Append("\" style=\"font-size:17px;font-weight:700\">")
                .Append(geo.ToString("F3")).Append("×</span>"
                + " 即 ").Append(geo < 1 ? "快 " : "慢 ").Append(Math.Abs(1 - geo).ToString("P1"))
                .Append("。<span class=\"mut\">（&lt; 1 = 新版本更快）</span></div>");
        }

        sb.Append("<div class=\"card\"><table>");
        sb.Append("<tr><th>行数</th><th>分组</th><th>操作</th><th>方言</th><th>实现</th>"
            + "<th class=\"n\">基线时延</th><th class=\"n\">最新时延</th><th class=\"n\">时延变化</th>"
            + "<th class=\"n\">基线分配/op</th><th class=\"n\">最新分配/op</th><th class=\"n\">分配变化</th></tr>");

        foreach (Measurement? h in headRun.Measurements
                     .Where(static m => m.Group != "Concurrency" && m.Operation != ConcurrentOp)
                     .OrderBy(static m => m.Rows).ThenBy(static m => ImplOrder(m.Implementation))
                     .ThenBy(static m => m.Dialect).ThenBy(static m => m.Operation))
        {
            Measurement? b = baseRun.Measurements.FirstOrDefault(x =>
                x.Operation == h.Operation && x.Dialect == h.Dialect &&
                x.Rows == h.Rows && x.Implementation == h.Implementation);
            if (b is null)
            {
                continue;
            }

            sb.Append("<tr><td class=\"n\">").Append(h.Rows.ToString("N0"))
                .Append("</td><td>").Append(Esc(h.Group))
                .Append("</td><td>").Append(Esc(h.Operation))
                .Append("</td><td>").Append(h.Dialect)
                .Append("</td><td class=\"impl impl-").Append(h.Implementation).Append("\">").Append(h.Implementation)
                .Append("</td><td class=\"n\">").Append(Fmt.Time(b.MedianNs))
                .Append("</td><td class=\"n\">").Append(Fmt.Time(h.MedianNs))
                .Append("</td>").Append(DeltaCell(h.MedianNs, b.MedianNs))
                .Append("<td class=\"n\">").Append(Fmt.Bytes(b.AllocatedBytesPerOp))
                .Append("</td><td class=\"n\">").Append(Fmt.Bytes(h.AllocatedBytesPerOp))
                .Append("</td>").Append(DeltaCell(h.AllocatedBytesPerOp, b.AllocatedBytesPerOp))
                .Append("</tr>");
        }
        sb.Append("</table><div class=\"note\"><b>读法</b>：时延变化 / 分配变化的负值（绿色）= 新版本更快 / 更省。"
            + "每行是同一台机器、同一份确定性种子数据、同一套测试项下的同项对照；"
            + "跨机器绝对值不可比，只有同机比值可比。基线 = ").Append(Esc(baseRun.Version))
            .Append('（').Append(baseRun.Timestamp).Append("），最新 = ").Append(Esc(headRun.Version))
            .Append('（').Append(headRun.Timestamp).Append("）。</div></div>");
        sb.Append(NormalizedCompare(baseRun, headRun));
        return sb.ToString();
    }

    /// <summary>交替 A/B 配对比值（阶段 4.2）——跨版本对比的黄金口径。
    /// <para>数据源：label 形如 <c>ab/&lt;round&gt;/&lt;dialect&gt;/&lt;tier&gt;</c> 的历史运行
    ///（由 <c>scripts/perfhub-ab.sh</c> 产出，块内两版背靠背）。每个 (方言, 档位, 操作, 实现)
    /// 按轮配对求 HEAD/基线比值，取<b>逐轮中位</b>并展示轮间散布——散布跨 1.0 即判
    /// "不可分辨"，不硬出结论。</para></summary>
    private static string AbCompare(List<PerfRun> runs)
    {
        List<PerfRun> abRuns = [.. runs
            .Where(static r => r.Label.StartsWith("ab/", StringComparison.Ordinal))];
        if (abRuns.Count == 0)
        {
            return "";
        }

        var byDialectTier = new Dictionary<(string Dialect, string Tier), List<(int Round, PerfRun Run)>>();
        foreach (PerfRun run in abRuns)
        {
            string[] parts = run.Label.Split('/');
            if (parts.Length < 4 || !int.TryParse(parts[1], out int round))
            {
                continue;
            }

            var key = (parts[2], parts[3]);
            if (!byDialectTier.TryGetValue(key, out var list))
            {
                list = [];
                byDialectTier[key] = list;
            }
            list.Add((round, run));
        }

        var rows = new List<(string Dialect, string Tier, string Operation, string Impl,
            double Median, double Min, double Max, int Rounds)>();
        foreach (((string dialect, string tier), List<(int Round, PerfRun Run)> roundRuns) in byDialectTier)
        {
            var paired = roundRuns
                .SelectMany(static rr => rr.Run.Measurements
                    .Where(static m => !m.Operation.StartsWith("Concurrent", StringComparison.Ordinal))
                    .Select(m => (rr.Round, rr.Run.Version, m)))
                .GroupBy(static x => (x.m.Operation, x.m.Implementation, x.Round))
                .Where(static g => g.Select(static x => x.Version).Distinct().Count() == 2)
                .GroupBy(static g => (g.Key.Operation, g.Key.Implementation));
            foreach (var opGroup in paired)
            {
                var ratios = new List<double>();
                foreach (var roundGroup in opGroup)
                {
                    double head = roundGroup.First(static x => x.Version != "v5.5.1").m.MedianNs;
                    double baseline = roundGroup.First(static x => x.Version == "v5.5.1").m.MedianNs;
                    if (baseline > 0 && head > 0)
                    {
                        ratios.Add(head / baseline);
                    }
                }

                if (ratios.Count >= 2)
                {
                    ratios.Sort();
                    rows.Add((dialect, tier, opGroup.Key.Operation, opGroup.Key.Implementation,
                        ratios[ratios.Count / 2], ratios[0], ratios[^1], ratios.Count));
                }
            }
        }

        if (rows.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.Append("<h2>⑤b 交替 A/B 配对比值（逐轮中位 · 黄金口径）</h2>")
            .Append("<div class=\"note\">数据源 label=<code>ab/轮/方言/档位</code>——块内两版背靠背，")
            .Append("机器漂移在两版间对称分摊。散布 = 轮间最小–最大比；<b>散布跨 1.0 即判不可分辨</b>。</div>")
            .Append("<div class=\"card\"><table>")
            .Append("<tr><th>方言</th><th>档位</th><th>操作</th><th>实现</th>")
            .Append("<th class=\"n\">中位比</th><th class=\"n\">散布</th><th class=\"n\">轮数</th><th>判定</th></tr>");
        foreach (var row in rows.OrderBy(static r => r.Median))
        {
            string verdict = AbVerdict(row);

            string cls = RatioClass(row.Median, 0.90, 1.10);
            sb.Append("<tr><td>").Append(row.Dialect).Append("</td><td class=\"n\">").Append(row.Tier)
                .Append("</td><td>").Append(Esc(row.Operation)).Append("</td><td>").Append(row.Impl)
                .Append("</td><td class=\"").Append(cls).Append("\"><b>").Append(row.Median.ToString("F3"))
                .Append("×</b></td><td class=\"n\">").Append(row.Min.ToString("F2"))
                .Append('–').Append(row.Max.ToString("F2"))
                .Append("</td><td class=\"n\">").Append(row.Rounds)
                .Append("</td><td>").Append(verdict).Append("</td></tr>");
        }

        sb.Append("</table></div>");
        return sb.ToString();
    }

    /// <summary>地板归一化对比——跨版本比较唯一可信的口径。
    /// <para><b>为什么必须归一化</b>：两轮实测相隔数十分钟，同机 ADO.NET 地板本身会漂移
    /// （实测单项漂移可达 50%）。直接比绝对时延会把「机器那轮更快」误读成「ORM 那轮更快」。
    /// 归一化算式 <c>(新_PalORM / 新_地板) ÷ (旧_PalORM / 旧_地板)</c> 对机器漂移取一阶抵消：
    /// 只有 ORM 相对地板的变化被保留下来。</para>
    /// <para><b>仍会残留噪声</b>：地板自身波动大的项（下表标 ⚠）归一化后被放大，
    /// 这类行的结论只能当方向性参考。要拿硬结论需交替 A/B 重测。</para></summary>
    private static string NormalizedCompare(PerfRun baseRun, PerfRun headRun)
    {
        var rows = new List<(string Dialect, string Operation, int RowCount, double Ratio, bool FloorMoved)>();
        foreach (var h in headRun.Measurements)
        {
            if (h.Implementation != "PalORM" || h.MedianNs <= 0)
            {
                continue;
            }

            // Build 组的 ADO.NET 臂只是返回预写字面量，不是同类地板；并发用 ops/s 单独看
            if (h.Operation.StartsWith("Build", StringComparison.Ordinal)
                || h.Operation.StartsWith("Concurrent", StringComparison.Ordinal))
            {
                continue;
            }

            var hFloor = headRun.Measurements.FirstOrDefault(x =>
                x.Operation == h.Operation && x.Dialect == h.Dialect &&
                x.Rows == h.Rows && x.Implementation == "ADO_NET");
            var b = baseRun.Measurements.FirstOrDefault(x =>
                x.Operation == h.Operation && x.Dialect == h.Dialect &&
                x.Rows == h.Rows && x.Implementation == "PalORM");
            var bFloor = baseRun.Measurements.FirstOrDefault(x =>
                x.Operation == h.Operation && x.Dialect == h.Dialect &&
                x.Rows == h.Rows && x.Implementation == "ADO_NET");
            if (hFloor is null || b is null || bFloor is null)
            {
                continue;
            }

            if (hFloor.MedianNs <= 0 || b.MedianNs <= 0 || bFloor.MedianNs <= 0)
            {
                continue;
            }

            double floorMoved = hFloor.MedianNs / bFloor.MedianNs;
            rows.Add((h.Dialect, h.Operation, h.Rows,
                h.MedianNs / hFloor.MedianNs / (b.MedianNs / bFloor.MedianNs),
                floorMoved is < 0.85 or > 1.18));
        }

        if (rows.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.Append("<h3 style=\"font-size:13px;margin:18px 0 8px;color:#374151\">")
            .Append("地板归一化对比（PalORM，跨版本唯一可信口径）</h3><div class=\"card\"><table>");
        sb.Append("<tr><th>方言</th><th>操作</th><th class=\"n\">行数</th>"
            + "<th class=\"n\">归一化时延比</th><th class=\"n\">相对地板变化</th><th>量具健康</th></tr>");

        foreach (var (dialect, operation, rowCount, ratio, floorMoved) in rows.OrderBy(static r => r.Ratio))
        {
            string cls = RatioClass(ratio, 0.95, 1.05);
            sb.Append("<tr><td>").Append(dialect).Append("</td><td>").Append(Esc(operation))
                .Append("</td><td class=\"n\">").Append(rowCount.ToString("N0"))
                .Append("</td><td").Append(cls).Append("><b>").Append(ratio.ToString("F3"))
                .Append("×</b></td><td class=\"n\">").Append(Fmt.Pct(ratio - 1))
                .Append("</td><td class=\"").Append(floorMoved ? "warn" : "mut").Append("\">")
                .Append(floorMoved ? "⚠ 地板漂移 >18%，结论仅方向性" : "地板稳定")
                .Append("</td></tr>");
        }

        double geo = Math.Exp(rows.Average(static r => Math.Log(r.Ratio)));
        int moved = rows.Count(static r => r.FloorMoved);
        sb.Append("</table><div class=\"note\"><b>整体（几何平均）</b>：<b style=\"font-size:15px\">")
            .Append(geo.ToString("F3")).Append("×</b>，即 PalORM 相对同机 ADO.NET 地板 ")
            .Append(geo < 1 ? "快 " : "慢 ").Append(Math.Abs(1 - geo).ToString("P1"))
            .Append("。<b>").Append(moved).Append('/').Append(rows.Count)
            .Append("</b> 项的地板漂移超过 ±18%，这些行标 ⚠——归一化对大幅漂移的地板会放大噪声，"
                + "只能当方向性参考。要拿硬结论，需对关注项做交替 A/B 重测（两版轮流跑、多轮取中位）。</div></div>");
        return sb.ToString();
    }

    /// <summary>变化率单元格——负值为改进（绿），正值为退化（红）。</summary>
    private static string DeltaCell(double current, double baseline)
    {
        if (baseline <= 0)
        {
            return "<td class=\"n\">—</td>";
        }

        double delta = (current - baseline) / baseline;
        string cls = DeltaClass(delta);
        return "<td" + cls + ">" + Fmt.Pct(delta) + "</td>";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ⑤ 增长曲线
    // ─────────────────────────────────────────────────────────────────────────

    private static string GrowthCurves(List<PerfRun> runs)
    {
        string[] ops = [ "GetByKey", "QueryAll", "StreamAll", "KeysetPage", "WhereIn", "Count",
            "Insert", "Update", "BulkInsert", "BulkUpdate", "BulkDelete",
            "TxTenInserts", "TxBulkInsert", "TxRollback" ];
        string[] impls = ["PalORM", "Dapper", "ADO_NET"];
        string[] colors = ["#059669", "#2563eb", "#9ca3af"];

        var series = new List<(string Key, List<(string Label, double Value)>, string Color)>();
        foreach (string? op in ops)
        {
            for (int ii = 0; ii < impls.Length; ii++)
            {
                string impl = impls[ii];
                var pts = new List<(string, double)>();
                foreach (PerfRun run in runs)
                {
                    Measurement? m = run.Measurements
                        .Where(x => x.Operation == op && x.Implementation == impl && x.Dialect == "SQLite")
                        .OrderBy(static x => x.Rows).FirstOrDefault();
                    if (m is not null && m.MedianNs > 0)
                    {
                        string lab = run.Timestamp.Length >= 16 ? run.Timestamp[5..16] : run.Timestamp;
                        pts.Add((lab, m.MedianNs));
                    }
                }
                if (pts.Count >= 2)
                {
                    series.Add((impl + " · " + op, pts, colors[ii]));
                }
            }
        }
        if (series.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.Append("<h2>⑤ 增长曲线（跨历史运行 · SQLite · 时延中位数 · 对数轴）</h2><div class=\"grid2\">");
        for (int i = 0; i < series.Count; i += 3)
        {
            sb.Append("<div class=\"card\">");
            sb.Append(SvgLineChart([.. series.Skip(i).Take(3)]));
            sb.Append("</div>");
        }
        sb.Append("</div><div class=\"note\"><b>读法</b>：纵轴对数刻度（时延跨数量级）；"
            + "曲线向下 = 优化生效。灰色为 ADO.NET 地板（应基本水平，若它大幅波动说明环境漂移，"
            + "该轮数据需重测）。每点 = 一次 <code>run</code> 的历史记录。</div>");
        return sb.ToString();
    }

    private static string SvgLineChart(List<(string Key, List<(string Label, double Value)> Pts, string Color)> series)
    {
        const int W = 620, H = 280, PL = 66, PR = 14, PT = 28, PB = 40;
        double maxV = series.SelectMany(static s => s.Pts).Max(static p => p.Value);
        double minV = series.SelectMany(static s => s.Pts).Min(static p => p.Value);
        double lo = Math.Log10(Math.Max(minV * 0.7, 1e-9));
        double hi = Math.Log10(maxV * 1.5);
        int n = series.Max(static s => s.Pts.Count);

        int nPts = n;
        static double Xf(int i, int nPts)
        {
            return PL + (nPts <= 1 ? 0 : i * (W - PL - PR) / (double)(nPts - 1));
        }

        double Yf(double v)
        {
            double share = (hi - Math.Log10(Math.Max(v, 1e-9))) / (hi - lo);
            return PT + (share * (H - PT - PB));
        }

        var sb = new StringBuilder();
        sb.Append("<svg viewBox=\"0 0 ").Append(W).Append(' ').Append(H)
            .Append("\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"Segoe UI,sans-serif\">");
        sb.Append("<rect width=\"").Append(W).Append("\" height=\"").Append(H)
            .Append("\" fill=\"#fbfbfc\" rx=\"6\"/>");

        for (int k = 0; k <= 4; k++)
        {
            double fraction = k / 4.0;
            double lv = lo + ((hi - lo) * fraction);
            double y = PT + ((1 - fraction) * (H - PT - PB));
            sb.Append("<line x1=\"").Append(PL).Append("\" y1=\"").Append(y.ToString("F1"))
                .Append("\" x2=\"").Append(W - PR).Append("\" y2=\"").Append(y.ToString("F1"))
                .Append("\" stroke=\"#e5e7eb\"/>");
            sb.Append("<text x=\"").Append(PL - 6).Append("\" y=\"").Append((y + 3.5).ToString("F1"))
                .Append("\" font-size=\"8.5\" fill=\"#9ca3af\" text-anchor=\"end\">")
                .Append(Fmt.Time(Math.Pow(10, lv))).Append("</text>");
        }

        string[] labels = [.. series[0].Pts.Select(static p => p.Label)];
        int step = Math.Max(1, labels.Length / 6);
        for (int i = 0; i < labels.Length; i += step)
        {
            sb.Append("<text x=\"").Append(Xf(i, nPts).ToString("F1")).Append("\" y=\"").Append(H - PB + 14)
                .Append("\" font-size=\"8.5\" fill=\"#6b7280\" text-anchor=\"middle\">")
                .Append(labels[i]).Append("</text>");
        }

        foreach ((string? key, List<(string Label, double Value)>? pts, string? color) in series)
        {
            var d = new StringBuilder();
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0)
                {
                    d.Append(' ');
                }

                d.Append(Xf(i, nPts).ToString("F1")).Append(',').Append(Yf(pts[i].Value).ToString("F1"));
            }
            sb.Append("<polyline points=\"").Append(d).Append("\" fill=\"none\" stroke=\"").Append(color)
                .Append("\" stroke-width=\"2\" stroke-linejoin=\"round\"/>");
            for (int i = 0; i < pts.Count; i++)
            {
                sb.Append("<circle cx=\"").Append(Xf(i, nPts).ToString("F1")).Append("\" cy=\"")
                    .Append(Yf(pts[i].Value).ToString("F1")).Append("\" r=\"2.8\" fill=\"").Append(color)
                    .Append("\"/>");
            }
            sb.Append("<text x=\"").Append(PL + 4).Append("\" y=\"").Append(PT + 10 + (series.IndexOf(series.First(s => s.Key == key)) * 11))
                .Append("\" font-size=\"9\" fill=\"").Append(color).Append("\" font-weight=\"600\">")
                .Append(key).Append("</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ⑤ 说明
    // ─────────────────────────────────────────────────────────────────────────

    private static string Notes(PerfRun latest)
    {
        int total = latest.Measurements.Count;
        int warn = latest.Measurements.Count(static m => m.ErrorRatio > 0.05);
        return "<h2>⑥ 说明与未覆盖维度</h2><div class=\"card\"><div class=\"note\">"
            + "<b>本次运行</b>：" + total + " 项测量，其中 " + warn + " 项 Error/Mean &gt; 5%"
            + "（量具自检参考，非失败）。耗时 " + latest.ElapsedSeconds.ToString("F0") + " 秒。<br>"
            + "<b>数据集</b>：S1 Narrow（主键 + 4 数据列），种子由行号确定性派生——三次测量库内容逐位相同。<br>"
            + "<b>三实现同连接</b>：ADO.NET / Dapper / PalORM 复用同一条已打开连接"
            + "（建连口径一致，否则比值被污染，见 lessons B76）。<br>"
            + "<b>会话生命周期</b>：PalORM 臂按操作新建 <c>DataSession</c>——与 Dapper 的无状态"
            + "扩展方法对等；会话不释放，因为 <c>DisposeAsync</c> 会关闭三实现共用的那条连接。"
            + "若应用层是「一个请求一个会话、用完后释放」，会话构建开销会被摊薄，PalORM 的点查数字会更好。<br>"
            + "<b>Build 组不是同类对比</b>：ADO.NET / Dapper 返回预写字面量，PalORM 真的在构建 SQL，"
            + "该组差值读作「ORM 构建税」而非查询快慢。<br>"
            + "<b>未覆盖维度</b>："
            + "方言 DDL 差异（三方言表结构同构，未测 JSON/数组/枚举等方言特有类型）；"
            + "连接池行为（PerfHub 用单连接，池参数不在本报告范围）；"
            + "长时衰减（单次运行无衰减数据，需连续多轮对比）；"
            + "并发态分配（多线程下 GetTotalAllocatedBytes 被干扰，不测，见 lessons B74）。<br>"
            + "<b>重新生成报告</b>：<code>dotnet run --project bench/PalORM.PerfHub -- report</code>"
            + "（纯读历史 JSON，不重跑）。"
            + "</div></div>";
    }
}
