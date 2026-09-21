using System.Text;
using System.Text.Json;

namespace PalORM.PerfHub;

/// <summary>统一报告生成器——HTML + 内联 SVG。
///
/// 报告包含五个区块：
///  ① 单操作指标总览：时延中位数/均值、Error/Mean、分配/op、采样堆峰、Gen0；
///  ② ORM vs ADO.NET 地板比值（跨环境可比的核心口径）；
///  ③ 并发吞吐（ops/s + p50/p95/p99）；
///  ④ 增长曲线（跨历史运行，SVG 折线，对数轴）；
///  ⑤ 说明与未覆盖维度。
///
/// 设计纪律：
///  · 跨环境绝对值禁止直接比较（规范 §3）——比值以同轮 ADO.NET 为分母；
///  · SVG 手写，零第三方绘图依赖；
///  · 所有数字来自 JSON 历史文件，报告可重复生成（report 子命令）。
/// </summary>
internal static class Report
{
    public static int Build()
    {
        string dir = Path.Combine(FindRepoRoot(), "bench", "perfhub", "results");
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine("没有结果目录——先跑 run");
            return 1;
        }

        var files = Directory.GetFiles(dir, "history-*.json").OrderBy(static f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine("没有历史数据——先跑 run");
            return 1;
        }

        var runs = new List<PerfRun>();
        foreach (var f in files)
        {
            try
            {
                var run = JsonSerializer.Deserialize(File.ReadAllText(f), PerfJsonContext.Default.PerfRun);
                if (run is not null) runs.Add(run);
            }
            catch (JsonException) { /* 跳过损坏文件 */ }
        }
        if (runs.Count == 0) return 1;

        string html = Render(runs);
        string outPath = Path.Combine(FindRepoRoot(), "bench", "perfhub", "report.html");
        File.WriteAllText(outPath, html);
        Console.WriteLine($"[PerfHub] 报告已生成: bench/perfhub/report.html（{runs.Count} 次历史运行）");
        return 0;
    }

    private static string Render(List<PerfRun> runs)
    {
        var latest = runs[^1];
        var sb = new StringBuilder();
        sb.Append(Header(latest, runs.Count));
        sb.Append(Overview(latest));
        sb.Append(RatioAndConcurrency(latest));
        if (runs.Count >= 2) sb.Append(GrowthCurves(runs));
        sb.Append(Notes(latest));
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HTML 头部与样式
    // ─────────────────────────────────────────────────────────────────────────

    private static string Header(PerfRun latest, int runCount)
    {
        var e = latest.Environment;
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
        + ".note{font-size:12px;color:var(--mut);line-height:1.85;margin-top:10px}"
        + ".note b{color:#374151}"
        + ".badge{display:inline-block;padding:1px 7px;border-radius:20px;font-size:10.5px;"
        + "font-weight:600;background:#eef2ff;color:#4338ca;margin-left:6px}"
        + "svg{display:block;max-width:100%}";

    private static string Esc(string? s)
        => string.IsNullOrEmpty(s)
            ? "—"
            : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ─────────────────────────────────────────────────────────────────────────
    // ① 单操作指标总览
    // ─────────────────────────────────────────────────────────────────────────

    private static string Overview(PerfRun run)
    {
        var rows = run.Measurements
            .Where(static m => m.Operation != ConcurrentOp)
            .OrderBy(static m => m.Rows).ThenBy(static m => m.Operation).ThenBy(static m => m.Dialect)
            .ThenBy(static m => ImplOrder(m.Implementation))
            .ToArray();

        var sb = new StringBuilder();
        sb.Append("<h2>① 单操作指标总览（时延 · 内存 · 峰值）</h2><div class=\"card\"><table>");
        sb.Append("<tr><th>行数</th><th>操作</th><th>方言</th><th>实现</th>"
            + "<th class=\"n\">时延中位数</th><th class=\"n\">时延均值</th><th class=\"n\">Error/Mean</th>"
            + "<th class=\"n\">分配/op</th><th class=\"n\">堆峰(采样)</th><th class=\"n\">Gen0</th></tr>");

        foreach (var m in rows)
        {
            string warnCls = m.ErrorRatio > 0.05 ? " class=\"n warn\"" : " class=\"n\"";
            sb.Append("<tr><td class=\"n\">").Append(m.Rows.ToString("N0"))
                .Append("</td><td>").Append(m.Operation)
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
        sb.Append("</table><div class=\"note\"><b>口径</b>：时延取中位数（同轮对照）；分配为 "
            + "<code>GC.GetTotalAllocatedBytes</code> 精确计数（确定性指标，复现性优于 1%）；"
            + "堆峰为 <code>GC.GetGCMemoryInfo().HeapSizeBytes</code> 采样——.NET 无「操作期间曾达到的"
            + "最大值」API，故标注为<b>采样堆峰</b>而非精确峰值；Error/Mean &gt; 5% 标黄"
            + "（量具自检参考，非失败）。</div></div>");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ② 比值表 + ③ 并发吞吐
    // ─────────────────────────────────────────────────────────────────────────

    private const string ConcurrentOp = "Concurrent_Mixed80_20";

    private static string RatioAndConcurrency(PerfRun run)
    {
        var single = run.Measurements.Where(static m => m.Operation != ConcurrentOp).ToArray();
        var groups = single.GroupBy(static m => (m.Rows, m.Operation, m.Dialect));

        var sb = new StringBuilder();
        sb.Append("<h2>② ORM vs ADO.NET 地板（比值 = 实现 / 地板）</h2><div class=\"card\"><table>");
        sb.Append("<tr><th>行数</th><th>操作</th><th>方言</th>"
            + "<th class=\"n\">Dapper 时延比</th><th class=\"n\">PalORM 时延比</th>"
            + "<th class=\"n\">Dapper 分配比</th><th class=\"n\">PalORM 分配比</th></tr>");

        foreach (var g in groups.OrderBy(static g => g.Key.Rows).ThenBy(static g => g.Key.Operation)
                 .ThenBy(static g => g.Key.Dialect))
        {
            var floor = g.FirstOrDefault(static m => m.Implementation == "ADO_NET");
            var dapper = g.FirstOrDefault(static m => m.Implementation == "Dapper");
            var palorm = g.FirstOrDefault(static m => m.Implementation == "PalORM");
            if (floor is null || floor.MedianNs <= 0) continue;

            sb.Append("<tr><td class=\"n\">").Append(g.Key.Rows.ToString("N0"))
                .Append("</td><td>").Append(g.Key.Operation)
                .Append("</td><td>").Append(g.Key.Dialect).Append("</td>");
            sb.Append(RatioCell(dapper is null ? null : dapper.MedianNs / floor.MedianNs));
            sb.Append(RatioCell(palorm is null ? null : palorm.MedianNs / floor.MedianNs));
            sb.Append(RatioCell(dapper is null || floor.AllocatedBytesPerOp <= 0
                ? null : dapper.AllocatedBytesPerOp / floor.AllocatedBytesPerOp));
            sb.Append(RatioCell(palorm is null || floor.AllocatedBytesPerOp <= 0
                ? null : palorm.AllocatedBytesPerOp / floor.AllocatedBytesPerOp));
            sb.Append("</tr>");
        }
        sb.Append("</table><div class=\"note\"><b>读法</b>：时延比 &lt; 1 表示比 ADO.NET 地板快；"
            + "分配比 &lt; 1 表示比地板省。跨机器绝对值不可比（同机 ADO.NET 地板漂移可达 43%），"
            + "<b>跨环境只比比值</b>（规范 §3）。</div></div>");

        var conc = run.Measurements.Where(static m => m.Operation == ConcurrentOp).ToArray();
        if (conc.Length > 0)
        {
            sb.Append("<h2>③ 并发吞吐（80/20 读写混合）</h2><div class=\"card\"><table>");
            sb.Append("<tr><th>行数</th><th>方言</th><th>实现</th><th class=\"n\">线程</th>"
                + "<th class=\"n\">ops/s</th><th class=\"n\">p50</th><th class=\"n\">p95</th>"
                + "<th class=\"n\">p99</th><th class=\"n\">样本</th></tr>");
            foreach (var m in conc.OrderBy(static m => m.Rows).ThenBy(static m => m.Dialect)
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
        if (ratio is null) return "<td class=\"n\">—</td>";
        double r = ratio.Value;
        string cls = r < 0.95 ? " class=\"n good\"" : r > 1.10 ? " class=\"n bad\"" : " class=\"n\"";
        return "<td" + cls + ">" + r.ToString("F2") + "×</td>";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ④ 增长曲线
    // ─────────────────────────────────────────────────────────────────────────

    private static string GrowthCurves(List<PerfRun> runs)
    {
        var ops = new[] { "GetByKey", "QueryAll", "StreamAll", "BulkUpdate", "BulkInsert", "Insert", "Update" };
        var impls = new[] { "PalORM", "Dapper", "ADO_NET" };
        var colors = new[] { "#059669", "#2563eb", "#9ca3af" };

        var series = new List<(string Key, List<(string Label, double Value)>, string Color)>();
        foreach (var op in ops)
        {
            for (int ii = 0; ii < impls.Length; ii++)
            {
                string impl = impls[ii];
                var pts = new List<(string, double)>();
                foreach (var run in runs)
                {
                    var m = run.Measurements
                        .Where(x => x.Operation == op && x.Implementation == impl && x.Dialect == "SQLite")
                        .OrderBy(static x => x.Rows).FirstOrDefault();
                    if (m is not null && m.MedianNs > 0)
                    {
                        string lab = run.Timestamp.Length >= 16 ? run.Timestamp[5..16] : run.Timestamp;
                        pts.Add((lab, m.MedianNs));
                    }
                }
                if (pts.Count >= 2) series.Add((impl + " · " + op, pts, colors[ii]));
            }
        }
        if (series.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append("<h2>④ 增长曲线（跨历史运行 · SQLite · 时延中位数 · 对数轴）</h2><div class=\"grid2\">");
        for (int i = 0; i < series.Count; i += 3)
        {
            sb.Append("<div class=\"card\">");
            sb.Append(SvgLineChart(series.Skip(i).Take(3).ToList()));
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
        static double Xf(int i, int nPts) => PL + (nPts <= 1 ? 0 : i * (W - PL - PR) / (double)(nPts - 1));
        double Yf(double v) => PT + (hi - Math.Log10(Math.Max(v, 1e-9))) / (hi - lo) * (H - PT - PB);

        var sb = new StringBuilder();
        sb.Append("<svg viewBox=\"0 0 ").Append(W).Append(' ').Append(H)
            .Append("\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"Segoe UI,sans-serif\">");
        sb.Append("<rect width=\"").Append(W).Append("\" height=\"").Append(H)
            .Append("\" fill=\"#fbfbfc\" rx=\"6\"/>");

        for (int k = 0; k <= 4; k++)
        {
            double lv = lo + (hi - lo) * k / 4;
            double y = PT + (1 - k / 4.0) * (H - PT - PB);
            sb.Append("<line x1=\"").Append(PL).Append("\" y1=\"").Append(y.ToString("F1"))
                .Append("\" x2=\"").Append(W - PR).Append("\" y2=\"").Append(y.ToString("F1"))
                .Append("\" stroke=\"#e5e7eb\"/>");
            sb.Append("<text x=\"").Append(PL - 6).Append("\" y=\"").Append((y + 3.5).ToString("F1"))
                .Append("\" font-size=\"8.5\" fill=\"#9ca3af\" text-anchor=\"end\">")
                .Append(Fmt.Time(Math.Pow(10, lv))).Append("</text>");
        }

        var labels = series[0].Pts.Select(static p => p.Label).ToArray();
        int step = Math.Max(1, labels.Length / 6);
        for (int i = 0; i < labels.Length; i += step)
        {
            sb.Append("<text x=\"").Append(Xf(i, nPts).ToString("F1")).Append("\" y=\"").Append(H - PB + 14)
                .Append("\" font-size=\"8.5\" fill=\"#6b7280\" text-anchor=\"middle\">")
                .Append(labels[i]).Append("</text>");
        }

        foreach (var (key, pts, color) in series)
        {
            var d = new StringBuilder();
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) d.Append(' ');
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
            sb.Append("<text x=\"").Append(PL + 4).Append("\" y=\"").Append(PT + 10 + series.IndexOf(series.First(s => s.Key == key)) * 11)
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
        return "<h2>⑤ 说明与未覆盖维度</h2><div class=\"card\"><div class=\"note\">"
            + "<b>本次运行</b>：" + total + " 项测量，其中 " + warn + " 项 Error/Mean &gt; 5%"
            + "（量具自检参考，非失败）。耗时 " + latest.ElapsedSeconds.ToString("F0") + " 秒。<br>"
            + "<b>数据集</b>：S1 Narrow（主键 + 4 数据列），种子由行号确定性派生——三次测量库内容逐位相同。<br>"
            + "<b>三实现同连接</b>：ADO.NET / Dapper / PalORM 复用同一条已打开连接"
            + "（建连口径一致，否则比值被污染，见 lessons B76）。<br>"
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
