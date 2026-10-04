using System.Globalization;
using System.Text;
using System.Text.Json;
using PalORM.Bench.Shared;

namespace PalORM.PerfGate;

/// <summary>四组表生成器（性能测试结果输出规范的固定表格组：CRUD 单行与读 / 批量 / 事务 /
/// 跨方言比值）。数据源 = 最新非子集 perfhub 批次的信封（MedianUs/AllocBytes/Ratio 逐项）。
/// <para><b>双渲染器，单一行收集器</b>（2026-10-04 step23）：CollectRows 只走一遍数据装配出
/// 行记录，Markdown 与 HTML 两个渲染器消费同一批行——B120 族防御（两份装配逻辑必然漂移）。
/// 两种输出按参数分流：md（追加进统一报告）与 html（真 rowspan 合并 + 完整边框）。</para>
/// <para>格式口径与规范一致（2026-10-04 step23 用户改版）：明细表（一/二/三）每行展开
/// 「方言」列覆盖三方言；时延数值不带单位（µs 隐含，一位小数）；比值列 = 裸倍数两位小数 +
/// 空格 + 色标紧贴百分比（无括号）；分配相对 ADO 用「P · D」记法；表四只列批量族 +
/// GetAllAsync，列序 SQLite/PostgreSQL/MySQL，格值为裸比值。分配 1024 进制 KB/MB、
/// 百分比取整（|p|&lt;0.5% 记 0%）、色标六档按显示值判定、倍数 ≥1.3 或 ≤0.7 加粗。</para>
/// <para><b>标签列合并</b>（2026-10-04 用户裁决）：md 侧 GFM 无 rowspan，重复标签留空
/// （视觉合并）；html 侧用真 rowspan。合并只作用于标签列（操作/方言/档）——数据列逐行显式，
/// 防"空 = 沿用上行"误读。md 侧带 📌 的行强制写出操作名（标记不随合并丢失）。</para>
/// <para><b>行级标记 📌（2026-10-04 用户两轮裁决换标：🚨 → 🔍 → 📌）</b>：比值 ≥1.50 或
/// ≤0.67 时标记。语义 = 远离基准需人工判读（劣化 = 回归风险；优于地板 &gt;33% = 地板健全性
/// 待核），<b>不是警告</b>——远优方向同样标 📌，故弃用 🚨（警示灯语义易误读为"出错"）；
/// 📌 是"标记待看"，不预设方向。</para>
/// <para>为什么做成工具而非汇报时手工贴：四组表曾是每次跑测后最大的人工步骤（.ai 本地脚本原型），
/// 固化后统一报告自动携带，人工只写"看点"与"行读法"的归因。</para></summary>
internal static class TableGen
{
    private static readonly string[] CrudOps =
        ["GetByKey", "GetAllAsync", "QueryAll", "Insert", "Update"];
    private static readonly string[] BulkOps = ["BulkInsert", "BulkUpdate", "BulkDelete", "UpsertBatch"];
    private static readonly string[] TxOps = ["TxSingleInsert", "TxHundredInserts", "TxBulkInsert", "TxRollback", "EnumInserts"];
    private static readonly string[] Dialects = ["SQLite", "MySQL", "PostgreSQL"];
    private static readonly string[] CrossOps = ["BulkInsert", "BulkUpdate", "BulkDelete", "UpsertBatch", "GetAllAsync"];
    private const string Floor = "ADO_NET";
    /// <summary>行级 📌：比值 ≥1.50 或 ≤0.67（远离基准需人工判读，非警告，标记待看不预设方向）。</summary>
    private const string Flag = "📌";

    /// <summary>明细表行记录（收集器产物，双渲染器共用）。</summary>
    private sealed record Row(
        string Op, string Dialect, int Tier,
        double AdoUs, double? DapUs, double PalUs,
        double AdoBytes, double DapBytes, double PalBytes,
        bool Flagged, bool IsWeak);

    /// <summary>表四行记录：三方言的 P/ADO 比值（null = 该格无数据）。</summary>
    private sealed record CrossRow(string Op, int Tier, double? Sq, double? Pg, double? My);

    public static int Run(string resultsDir, string? outPath, string? htmlOutPath)
    {
        PerfResultEnvelope? batch = NewestPerfHubBatch(resultsDir);
        if (batch is null)
        {
            Console.Error.WriteLine("[PerfGate] tables: 结果库里没有非子集 perfhub 批次");
            return 1;
        }

        var index = new Dictionary<(string D, string O, int T, string A), PerfResultItem>();
        foreach (PerfResultItem item in batch.Items)
        {
            index[(item.Dialect, item.Name, item.Tier, item.Arm)] = item;
        }

        int minTier = batch.Items.Select(static i => i.Tier).Where(static t => t > 0).Min();
        string label = string.IsNullOrEmpty(batch.Label) ? "（无）" : batch.Label;
        var t1 = CollectRows(index, CrudOps, minTier, twoTiers: true);
        var t2 = CollectRows(index, BulkOps, minTier, twoTiers: true);
        var t3 = CollectRows(index, TxOps, minTier, twoTiers: false);
        var t4 = CollectCrossRows(index, minTier);

        // Markdown 渲染（追加进统一报告 / 控制台输出）。
        var md = new StringBuilder();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"### 四组表（批次 {batch.Timestamp}，label `{label}`）"));
        md.AppendLine();
        MdTable(md, "**一、CRUD 单行与读**", t1);
        md.AppendLine();
        MdTable(md, "**二、批量处理**", t2);
        md.AppendLine();
        MdTable(md, "**三、事务（仅最小档）**", t3);
        MdCrossTable(md, t4);
        md.AppendLine();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"⚠ = 判别力弱：该行三臂里最大 ErrorRatio（标准误/均值）> {NoiseLine * 100:0}%，比值落在噪声带内、不足以支撑结论（阈值同 Measure 的量具自检线）。仅 PerfHub 采集该指标。"));
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{Flag} = 远离基准需人工判读（P/ADO ≥1.50 或 ≤0.67）：劣化方向是回归风险，远优方向是地板健全性待核——两者都要过目，不等于告警。"));
        md.AppendLine();

        string? htmlTarget = ResolveHtmlTarget(htmlOutPath, outPath);
        if (!string.IsNullOrEmpty(outPath) && !outPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(outPath, md.ToString());
            Console.WriteLine($"[PerfGate] 四组表已写入 {outPath}");
        }
        else if (string.IsNullOrEmpty(outPath) && htmlTarget is null)
        {
            Console.WriteLine(md.ToString());
        }

        // HTML 渲染（真 rowspan 合并 + 完整边框——GFM 管道表无 rowspan，md 的留空合并是格式上限）。
        if (htmlTarget is not null)
        {
            File.WriteAllText(htmlTarget, HtmlDoc(batch.Timestamp, label, (t1, t2, t3, t4)));
            Console.WriteLine($"[PerfGate] 四组表(HTML) 已写入 {htmlTarget}");
        }

        return 0;
    }

    // ── 行收集（单一数据装配，双渲染器共用）──────────────────────────────

    /// <summary>HTML 输出目标解析：显式 --html 优先；否则 --out 以 .html 结尾时输出到该路径；
    /// 其余（仅 --out x.md 或纯控制台）不产 HTML。</summary>
    private static string? ResolveHtmlTarget(string? htmlOutPath, string? outPath)
    {
        if (htmlOutPath is { Length: > 0 }) return htmlOutPath;
        if (outPath is not null && outPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return outPath;
        return null;
    }

    private static List<Row> CollectRows(
        Dictionary<(string, string, int, string), PerfResultItem> ix,
        string[] ops, int minTier, bool twoTiers)
    {
        var rows = new List<Row>();
        int[] tiers = twoTiers ? [minTier, 20000] : [minTier];
        foreach (string op in ops)
        {
            foreach (string dialect in Dialects)
            {
                foreach (int tier in tiers)
                {
                    PerfResultItem? ado = Get(ix, dialect, op, tier, Floor);
                    PerfResultItem? dap = Get(ix, dialect, op, tier, "Dapper");
                    PerfResultItem? pal = Get(ix, dialect, op, tier, "PalORM");
                    if (ado is null || pal is null) continue;
                    rows.Add(new Row(
                        op, dialect, tier,
                        ado.MedianUs, dap?.MedianUs, pal.MedianUs,
                        ado.AllocBytes, dap?.AllocBytes ?? 0, pal.AllocBytes,
                        ado.MedianUs > 0 && IsFlagged(pal.MedianUs / ado.MedianUs),
                        Weak(ado, dap, pal)));
                }
            }
        }

        return rows;
    }

    private static List<CrossRow> CollectCrossRows(
        Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        var rows = new List<CrossRow>();
        foreach (string op in CrossOps)
        {
            foreach (int tier in new[] { minTier, 20000 })
            {
                if (Get(ix, "SQLite", op, tier, "PalORM") is null && tier != minTier) continue;
                rows.Add(new CrossRow(op, tier,
                    CrossRatio(ix, "SQLite", op, tier),
                    CrossRatio(ix, "PostgreSQL", op, tier),
                    CrossRatio(ix, "MySQL", op, tier)));
            }
        }

        return rows;
    }

    // ── Markdown 渲染 ────────────────────────────────────────────────────

    /// <summary>标签列的视觉合并状态：GFM 无 rowspan，重复的标签单元格（操作/方言/档）以
    /// 留空表达合并——值变化才写。每张表独立重置。带行级 📌 的行强制写出操作名。</summary>
    private sealed class LabelMerge
    {
        public string? Op;
        public string? Dialect;
        public int? Tier;

        public string OpCell(string op, bool flagged)
        {
            if (op == Op && !flagged) return "";
            Op = op;
            return flagged ? $"{Flag} {op}" : op;
        }

        public string DialectCell(string dialect)
        {
            if (dialect == Dialect) return "";
            Dialect = dialect;
            return dialect;
        }

        public string TierCell(int tier)
        {
            if (tier == Tier) return "";
            Tier = tier;
            return tier.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void MdTable(StringBuilder md, string title, List<Row> rows)
    {
        md.AppendLine(title);
        md.AppendLine();
        md.AppendLine("| 操作 | 方言 | 档 | ADO.NET | Dapper | PalORM | P/ADO（倍数±%） | P/Dapper（倍数±%） | 分配（A/D/P） | 分配相对 ADO（D/P） |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        var merge = new LabelMerge();
        foreach (Row r in rows)
        {
            string pAdo = RatioMd(r.PalUs / r.AdoUs) + (r.IsWeak ? " ⚠" : "");
            string pDap = r.DapUs is null or <= 0 ? "—" : RatioMd(r.PalUs / r.DapUs.Value);
            string alloc = $"{FmtBytes(r.AdoBytes)}/{FmtBytes(r.DapBytes)}/{FmtBytes(r.PalBytes)}";
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {merge.OpCell(r.Op, r.Flagged)} | {merge.DialectCell(r.Dialect)} | {merge.TierCell(r.Tier)} | {FmtUs(r.AdoUs)} | {FmtUs(r.DapUs)} | {FmtUs(r.PalUs)} "
                + $"| {pAdo} | {pDap} | {alloc} | {AllocDeltaMd(r)} |"));
        }
    }

    private static void MdCrossTable(StringBuilder md, List<CrossRow> rows)
    {
        md.AppendLine();
        md.AppendLine("**四、跨方言 PalORM/ADO 比值（批量族 + GetAllAsync）**");
        md.AppendLine();
        md.AppendLine("| 操作 | 档 | SQLite | PostgreSQL | MySQL |");
        md.AppendLine("|---|---|---|---|---|");
        string? prevOp = null;
        int? prevTier = null;
        foreach (CrossRow r in rows)
        {
            bool flaggedRow = new[] { r.Sq, r.Pg, r.My }.Any(v => v is { } ratio && IsFlagged(ratio));
            string opCell = r.Op == prevOp && !flaggedRow ? "" : CrossOpCellMd(r.Op, flaggedRow);
            string tierCell = r.Tier == prevTier ? "" : r.Tier.ToString(CultureInfo.InvariantCulture);
            prevOp = r.Op;
            prevTier = r.Tier;
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {opCell} | {tierCell} | {CrossCellMd(r.Sq)} | {CrossCellMd(r.Pg)} | {CrossCellMd(r.My)} |"));
        }
    }

    /// <summary>表四操作名单元格（md）：越线行前缀 📌（与明细表 LabelMerge.OpCell 的强制写规则同源）。</summary>
    private static string CrossOpCellMd(string op, bool flaggedRow)
        => flaggedRow ? $"{Flag} {op}" : op;

    private static string CrossCellMd(double? ratio)
    {
        if (ratio is not { } value) return "—";
        string flag = IsFlagged(value) ? Flag : "";
        string cell = $"{flag}{value.ToString("F2", CultureInfo.InvariantCulture)}";
        double shown = Math.Round(value, 2);
        return shown is >= 1.3 or <= 0.7 ? $"**{cell}**" : cell;
    }

    // ── HTML 渲染（真 rowspan 合并 + 边框）──────────────────────────────

    private static string HtmlDoc(string stamp, string label, (List<Row> T1, List<Row> T2, List<Row> T3, List<CrossRow> T4) tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>四组表</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:'Segoe UI',sans-serif;margin:24px;color:#1f2733}");
        sb.AppendLine("h1{font-size:20px}h2{font-size:16px;margin:26px 0 6px}");
        sb.AppendLine("table{border-collapse:collapse;margin:6px 0 8px}");
        sb.AppendLine("th,td{border:1px solid #b7bfc9;padding:2px 10px;font-family:Consolas,'Courier New',monospace;font-size:13px;text-align:right;white-space:nowrap}");
        sb.AppendLine("th{background:#eef1f5;text-align:center}");
        sb.AppendLine("td.lbl{text-align:left;font-weight:600}");
        sb.AppendLine("td.txt{text-align:left}");
        sb.AppendLine("tr.flag td{background:#fff7e0}");
        sb.AppendLine(".legend{color:#4a5568;font-size:13px;margin:4px 0}");
        sb.AppendLine("</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"<h1>四组表（批次 {Html(stamp)}，label <code>{Html(label)}</code>）</h1>"));
        HtmlTable(sb, "一、CRUD 单行与读", tables.T1);
        HtmlTable(sb, "二、批量处理", tables.T2);
        HtmlTable(sb, "三、事务（仅最小档）", tables.T3);
        HtmlCrossTable(sb, tables.T4);
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"<p class=\"legend\">⚠ = 判别力弱：该行三臂里最大 ErrorRatio（标准误/均值）&gt; {NoiseLine * 100:0}%，比值落在噪声带内、不足以支撑结论。仅 PerfHub 采集该指标。</p>"));
        sb.AppendLine("<p class=\"legend\">📌 = 远离基准需人工判读（P/ADO ≥1.50 或 ≤0.67）：劣化方向是回归风险，远优方向是地板健全性待核——两者都要过目，不等于告警。行底色浅黄 = 该行越线；标签列跨行合并（rowspan）。</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void HtmlTable(StringBuilder sb, string title, List<Row> rows)
    {
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"<h2>{Html(title)}</h2>"));
        sb.AppendLine("<table><thead><tr><th>操作</th><th>方言</th><th>档</th><th>ADO.NET</th><th>Dapper</th><th>PalORM</th><th>P/ADO（倍数±%）</th><th>P/Dapper（倍数±%）</th><th>分配（A/D/P）</th><th>分配相对 ADO（D/P）</th></tr></thead><tbody>");
        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i];
            // 标签列真合并：本行是某标签值连续段的首行时输出 td rowspan=段长，否则省略该 td。
            // 操作名上的 📌 = 该段内任一行越线（单元格跨行共享，行级精确标记由 tr.flag 底色承担）
            string opTd = RunStart(rows, i, static x => x.Op) ? OpTdHtml(rows, i, r) : "";
            string dialectTd = RunStart(rows, i, static x => x.Dialect)
                ? $"<td class=\"txt\" rowspan=\"{RunLength(rows, i, static x => x.Dialect)}\">{Html(r.Dialect)}</td>"
                : "";
            string tierTd = RunStart(rows, i, static x => x.Tier)
                ? $"<td rowspan=\"{RunLength(rows, i, static x => x.Tier)}\">{r.Tier}</td>"
                : "";
            string pAdo = RatioHtml(r.PalUs / r.AdoUs) + (r.IsWeak ? " ⚠" : "");
            string pDap = r.DapUs is null or <= 0 ? "—" : RatioHtml(r.PalUs / r.DapUs.Value);
            string alloc = $"{FmtBytes(r.AdoBytes)}/{FmtBytes(r.DapBytes)}/{FmtBytes(r.PalBytes)}";
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"<tr{(r.Flagged ? " class=\"flag\"" : "")}>{opTd}{dialectTd}{tierTd}"
                + $"<td>{FmtUs(r.AdoUs)}</td><td>{FmtUs(r.DapUs)}</td><td>{FmtUs(r.PalUs)}</td>"
                + $"<td>{pAdo}</td><td>{pDap}</td><td class=\"txt\">{alloc}</td><td class=\"txt\">{Html(AllocDeltaMd(r))}</td></tr>"));
        }

        sb.AppendLine("</tbody></table>");
    }

    /// <summary>操作名单元格（HTML）：rowspan 跨该操作的全部行；段内任一行越线则前缀 📌。</summary>
    private static string OpTdHtml(List<Row> rows, int i, Row r)
    {
        string pin = RunAny(rows, i, static x => x.Op, static x => x.Flagged) ? Flag + " " : "";
        return $"<td class=\"lbl\" rowspan=\"{RunLength(rows, i, static x => x.Op)}\">{pin}{Html(r.Op)}</td>";
    }

    private static void HtmlCrossTable(StringBuilder sb, List<CrossRow> rows)
    {
        sb.AppendLine("<h2>四、跨方言 PalORM/ADO 比值（批量族 + GetAllAsync）</h2>");
        sb.AppendLine("<table><thead><tr><th>操作</th><th>档</th><th>SQLite</th><th>PostgreSQL</th><th>MySQL</th></tr></thead><tbody>");
        for (int i = 0; i < rows.Count; i++)
        {
            CrossRow r = rows[i];
            string opTd = RunStart(rows, i, static x => x.Op)
                ? $"<td class=\"lbl\" rowspan=\"{RunLength(rows, i, static x => x.Op)}\">{Html(r.Op)}</td>"
                : "";
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"<tr>{opTd}<td>{r.Tier}</td><td>{CrossCellHtml(r.Sq)}</td><td>{CrossCellHtml(r.Pg)}</td><td>{CrossCellHtml(r.My)}</td></tr>"));
        }

        sb.AppendLine("</tbody></table>");
    }

    /// <summary>本行是否是指定标签值连续段的首行（HTML rowspan 的输出条件）。</summary>
    private static bool RunStart<T, TKey>(List<T> rows, int i, Func<T, TKey> key) where TKey : notnull
        => i == 0 || !EqualityComparer<TKey>.Default.Equals(key(rows[i - 1]), key(rows[i]));

    /// <summary>本行在指定标签值连续段内的长度（rowspan 值）。</summary>
    private static int RunLength<T, TKey>(List<T> rows, int i, Func<T, TKey> key) where TKey : notnull
    {
        int end = i + 1;
        while (end < rows.Count && EqualityComparer<TKey>.Default.Equals(key(rows[i]), key(rows[end]))) end++;
        return end - i;
    }

    /// <summary>本行在指定标签值连续段内，是否有任一行满足谓词（操作名 📌 的判据）。</summary>
    private static bool RunAny<T, TKey>(List<T> rows, int i, Func<T, TKey> key, Func<T, bool> predicate) where TKey : notnull
    {
        for (int j = i; j < rows.Count && EqualityComparer<TKey>.Default.Equals(key(rows[i]), key(rows[j])); j++)
        {
            if (predicate(rows[j])) return true;
        }

        return false;
    }

    private static string CrossCellHtml(double? ratio)
    {
        if (ratio is not { } value) return "—";
        string flag = IsFlagged(value) ? Flag : "";
        string cell = $"{flag}{value.ToString("F2", CultureInfo.InvariantCulture)}";
        double shown = Math.Round(value, 2);
        return shown is >= 1.3 or <= 0.7 ? $"<b>{cell}</b>" : cell;
    }

    private static string Html(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    // ── 共用格式助手 ────────────────────────────────────────────────────

    private static PerfResultItem? Get(
        Dictionary<(string, string, int, string), PerfResultItem> ix, string d, string o, int t, string a)
        => ix.TryGetValue((d, o, t, a), out PerfResultItem? item) ? item : null;

    private static double? CrossRatio(
        Dictionary<(string, string, int, string), PerfResultItem> ix, string dialect, string op, int tier)
    {
        PerfResultItem? ado = Get(ix, dialect, op, tier, Floor);
        PerfResultItem? orm = Get(ix, dialect, op, tier, "PalORM");
        return ado is null || orm is null || ado.MedianUs <= 0 ? null : orm.MedianUs / ado.MedianUs;
    }

    private static bool IsFlagged(double ratio) => ratio is >= 1.5 or <= 0.67;

    /// <summary>判别力弱阈值——沿用 <c>Measure.cs</c> 的量具自检线（Error/Mean &gt; 5% 标黄）。</summary>
    private const double NoiseLine = 0.05;

    /// <summary>判别力弱判定（2026-10-04）：该键三臂里最大 ErrorRatio 超线时，比值落在噪声带内、
    /// 不足以支撑结论。取三臂最大值而非只看被测臂——比值 = 被测臂 / 地板，两侧噪声都会放大
    /// 比值的不确定度。任一侧缺 ErrorRatio（未采集）按 0 处理，不据此标注。</summary>
    private static bool Weak(params PerfResultItem?[] arms)
    {
        foreach (PerfResultItem? arm in arms)
        {
            if (arm is not null && arm.ErrorRatio > NoiseLine)
            {
                return true;
            }
        }

        return false;
    }

    private static string RatioMd(double ratio)
    {
        double shown = Math.Round(ratio, 2);
        string cell = $"{shown.ToString("F2", CultureInfo.InvariantCulture)} {Emoji(ratio - 1)}{Pct(ratio - 1)}";
        return shown is >= 1.3 or <= 0.7 ? $"**{cell}**" : cell;
    }

    private static string RatioHtml(double ratio)
    {
        double shown = Math.Round(ratio, 2);
        string cell = $"{shown.ToString("F2", CultureInfo.InvariantCulture)} {Emoji(ratio - 1)}{Pct(ratio - 1)}";
        return shown is >= 1.3 or <= 0.7 ? $"<b>{cell}</b>" : cell;
    }

    /// <summary>分配相对 ADO 的「P · D」记法：PalORM 差值在前（无前缀），Dapper 差值带 D 前缀在后。</summary>
    private static string AllocDeltaMd(Row r)
    {
        string p = r.AdoBytes > 0 ? Delta((r.PalBytes / r.AdoBytes) - 1) : "—";
        if (r.DapBytes <= 0 || r.AdoBytes <= 0) return $"{p} · D —";
        return $"{p} · D {Delta((r.DapBytes / r.AdoBytes) - 1)}";
    }

    private static string Delta(double p)
    {
        int q = Math.Abs(p * 100) < 0.5 ? 0 : (int)Math.Round(p * 100, MidpointRounding.AwayFromZero);
        return $"{Emoji(p)}{q:+0;-0;+0}%";
    }

    private static string Pct(double p)
    {
        int q = Math.Abs(p * 100) < 0.5 ? 0 : (int)Math.Round(p * 100, MidpointRounding.AwayFromZero);
        return $"{q:+0;-0;+0}%";
    }

    private static string Emoji(double p)
    {
        int q = Math.Abs(p * 100) < 0.5 ? 0 : (int)Math.Round(p * 100, MidpointRounding.AwayFromZero);
        return q switch
        {
            <= -30 => "🟢",
            <= -10 => "🟩",
            <= -1 => "🔹",
            0 => "⚪",
            <= 9 => "🔸",
            <= 29 => "🟧",
            _ => "🟥",
        };
    }

    /// <summary>时延裸数值（µs 隐含，不带单位）：≥1 一位小数，&lt;1 三位小数（当前夹具面最小中位 ~6µs，子 1µs 为防御）。</summary>
    private static string FmtUs(double? medianUs)
    {
        if (medianUs is null or <= 0) return "—";
        double us = medianUs.Value;
        return us < 1
            ? us.ToString("F3", CultureInfo.InvariantCulture)
            : us.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string FmtBytes(double bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return Math.Round(bytes).ToString(CultureInfo.InvariantCulture) + "B";
        double kb = bytes / 1024;
        if (kb < 100) return kb.ToString("F1", CultureInfo.InvariantCulture) + "KB";
        if (kb < 1024) return Math.Round(kb).ToString(CultureInfo.InvariantCulture) + "KB";
        return (kb / 1024).ToString("F1", CultureInfo.InvariantCulture) + "MB";
    }

    private static PerfResultEnvelope? NewestPerfHubBatch(string resultsDir)
    {
        if (!Directory.Exists(resultsDir)) return null;
        foreach (string file in Directory.EnumerateFiles(resultsDir, "perfhub-*.json").OrderByDescending(static f => f))
        {
            try
            {
                PerfResultEnvelope? run = JsonSerializer.Deserialize(
                    File.ReadAllText(file), PerfResultJsonContext.Default.PerfResultEnvelope);
                if (run is null || PerfResultWriter.IsSubsetLabel(run.Label)) continue;
                return run;
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"[PerfGate] tables: 跳过无法解析的结果文件 {file}");
            }
        }

        return null;
    }
}
