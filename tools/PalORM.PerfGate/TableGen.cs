using System.Globalization;
using System.Text;
using System.Text.Json;
using PalORM.Bench.Shared;

namespace PalORM.PerfGate;

/// <summary>四组表生成器（性能测试结果输出规范的固定表格组：CRUD 单行与读 / 批量 / 事务 /
/// 跨方言比值）。数据源 = 最新非子集 perfhub 批次的信封（MedianUs/AllocBytes/Ratio 逐项）。
/// <para>格式口径与规范一致（2026-10-04 step23 用户改版）：明细表（一/二/三）每行展开
/// 「方言」列覆盖三方言；时延数值不带单位（µs 隐含，一位小数）；比值列 = 裸倍数两位小数 +
/// 空格 + 色标紧贴百分比（无括号）；分配相对 ADO 用「P · D」记法；表四只列批量族 +
/// GetAllAsync，列序 SQLite/PostgreSQL/MySQL，格值为裸比值。分配 1024 进制 KB/MB、
/// 百分比取整（|p|&lt;0.5% 记 0%）、色标六档按显示值判定、倍数 ≥1.3 或 ≤0.7 加粗。</para>
/// <para><b>行级标记 📌（2026-10-04 用户两轮裁决换标：🚨 → 🔍 → 📌）</b>：比值 ≥1.50 或
/// ≤0.67 时操作名/格值前置 📌。语义 = 远离基准需人工判读（劣化 = 回归风险；优于地板 &gt;33% =
/// 地板健全性待核），<b>不是警告</b>——远优方向同样标 📌，故弃用 🚨（警示灯语义易误读为"出错"）
/// 与 🔍（用户复判仍不够清晰）；📌 = 标记待看，不预设方向。</para>
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

    public static int Run(string resultsDir, string? outPath)
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
        var md = new StringBuilder();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"### 四组表（批次 {batch.Timestamp}，label `{(string.IsNullOrEmpty(batch.Label) ? "（无）" : batch.Label)}`）"));
        md.AppendLine();
        Table1(md, index, minTier);
        Table2(md, index, minTier);
        Table3(md, index, minTier);
        Table4(md, index, minTier);
        md.AppendLine();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"⚠ = 判别力弱：该行三臂里最大 ErrorRatio（标准误/均值）> {NoiseLine * 100:0}%，比值落在噪声带内、不足以支撑结论（阈值同 Measure 的量具自检线）。仅 PerfHub 采集该指标。"));
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{Flag} = 远离基准需人工判读（P/ADO ≥1.50 或 ≤0.67）：劣化方向是回归风险，远优方向是地板健全性待核——两者都要过目，不等于告警。"));
        md.AppendLine();

        if (string.IsNullOrEmpty(outPath))
        {
            Console.WriteLine(md.ToString());
        }
        else
        {
            File.WriteAllText(outPath, md.ToString());
            Console.WriteLine($"[PerfGate] 四组表已写入 {outPath}");
        }

        return 0;
    }

    /// <summary>标签列的视觉合并状态（2026-10-04 用户裁决）：GFM 无 rowspan，重复的
    /// 标签单元格（操作/方言/档）以留空表达合并——值变化才写。每张表独立重置。
    /// 数据列不合并：逐行显式值避免"空 = 沿用上行"的误读。带行级 📌 的行强制写出
    /// 操作名（否则标记随合并丢失，且标记本身就该把行身份顶到眼前）。</summary>
    private sealed class LabelMerge
    {
        public string? Op;
        public string? Dialect;
        public int? Tier;

        /// <summary>操作名单元格：与上行相同且本行无行级标记时留空。</summary>
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

    private static void Table1(StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        md.AppendLine("**一、CRUD 单行与读**");
        md.AppendLine();
        Header(md);
        var merge = new LabelMerge();
        foreach (string op in CrudOps)
        {
            foreach (string dialect in Dialects)
            {
                foreach (int tier in new[] { minTier, 20000 })
                {
                    Row(md, ix, merge, dialect, op, tier, flaggable: true);
                }
            }
        }
    }

    private static void Table2(StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        md.AppendLine();
        md.AppendLine("**二、批量处理**");
        md.AppendLine();
        Header(md);
        var merge = new LabelMerge();
        foreach (string op in BulkOps)
        {
            foreach (string dialect in Dialects)
            {
                foreach (int tier in new[] { minTier, 20000 })
                {
                    Row(md, ix, merge, dialect, op, tier, flaggable: true);
                }
            }
        }
    }

    private static void Table3(StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        md.AppendLine();
        md.AppendLine("**三、事务（仅最小档）**");
        md.AppendLine();
        Header(md);
        var merge = new LabelMerge();
        foreach (string op in TxOps)
        {
            foreach (string dialect in Dialects)
            {
                Row(md, ix, merge, dialect, op, minTier, flaggable: true);
            }
        }
    }

    private static void Table4(StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        md.AppendLine();
        md.AppendLine("**四、跨方言 PalORM/ADO 比值（批量族 + GetAllAsync）**");
        md.AppendLine();
        md.AppendLine("| 操作 | 档 | SQLite | PostgreSQL | MySQL |");
        md.AppendLine("|---|---|---|---|---|");
        string? prevOp = null;
        int? prevTier = null;
        foreach (string op in CrossOps)
        {
            foreach (int tier in new[] { minTier, 20000 })
            {
                if (Get(ix, "SQLite", op, tier, "PalORM") is null && tier != minTier) continue;
                bool flaggedRow = CrossFlag(ix, op, tier).Length > 0;
                string opCell = op == prevOp && !flaggedRow ? "" : $"{CrossFlag(ix, op, tier)}{op}";
                string tierCell = tier == prevTier ? "" : tier.ToString(CultureInfo.InvariantCulture);
                prevOp = op;
                prevTier = tier;
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {opCell} | {tierCell} | {CrossCell(ix, "SQLite", op, tier)} | "
                    + $"{CrossCell(ix, "PostgreSQL", op, tier)} | {CrossCell(ix, "MySQL", op, tier)} |"));
            }
        }
    }

    private static string CrossFlag(
        Dictionary<(string, string, int, string), PerfResultItem> ix, string op, int tier)
    {
        foreach (string dialect in Dialects)
        {
            if (CrossRatio(ix, dialect, op, tier) is { } r && IsFlagged(r)) return $"{Flag} ";
        }

        return "";
    }

    private static string CrossCell(
        Dictionary<(string, string, int, string), PerfResultItem> ix, string dialect, string op, int tier)
    {
        if (CrossRatio(ix, dialect, op, tier) is not { } ratio) return "—";
        // 裸比值（无 ×、无色标——三列全展开后逐格色标噪声大，越线格前缀 📌 已足够指向需判读的格）
        string flag = IsFlagged(ratio) ? Flag : "";
        string cell = $"{flag}{ratio.ToString("F2", CultureInfo.InvariantCulture)}";
        double shown = Math.Round(ratio, 2);
        return shown is >= 1.3 or <= 0.7 ? $"**{cell}**" : cell;
    }

    private static double? CrossRatio(
        Dictionary<(string, string, int, string), PerfResultItem> ix, string dialect, string op, int tier)
    {
        PerfResultItem? ado = Get(ix, dialect, op, tier, Floor);
        PerfResultItem? orm = Get(ix, dialect, op, tier, "PalORM");
        return ado is null || orm is null || ado.MedianUs <= 0 ? null : orm.MedianUs / ado.MedianUs;
    }

    private static void Header(StringBuilder md)
    {
        md.AppendLine("| 操作 | 方言 | 档 | ADO.NET | Dapper | PalORM | P/ADO（倍数±%） | P/Dapper（倍数±%） | 分配（A/D/P） | 分配相对 ADO（D/P） |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
    }

    private static void Row(
        StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix,
        LabelMerge merge, string dialect, string op, int tier, bool flaggable)
    {
        PerfResultItem? ado = Get(ix, dialect, op, tier, Floor);
        PerfResultItem? dap = Get(ix, dialect, op, tier, "Dapper");
        PerfResultItem? pal = Get(ix, dialect, op, tier, "PalORM");
        if (ado is null || pal is null) return;

        string pAdo = RatioCell(pal.MedianUs / ado.MedianUs) + WeakMark(ado, dap, pal);
        string pDap = dap is null || dap.MedianUs <= 0 ? "—" : RatioCell(pal.MedianUs / dap.MedianUs);
        string alloc = $"{FmtBytes(ado.AllocBytes)}/{FmtBytes(dap?.AllocBytes ?? 0)}/{FmtBytes(pal.AllocBytes)}";
        string allocDelta = AllocDeltaCell(pal, dap, ado);
        bool flagged = flaggable && ado.MedianUs > 0 && IsFlagged(pal.MedianUs / ado.MedianUs);
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {merge.OpCell(op, flagged)} | {merge.DialectCell(dialect)} | {merge.TierCell(tier)} | {FmtUs(ado.MedianUs)} | {FmtUs(dap?.MedianUs)} | {FmtUs(pal.MedianUs)} "
            + $"| {pAdo} | {pDap} | {alloc} | {allocDelta} |"));
    }

    private static PerfResultItem? Get(
        Dictionary<(string, string, int, string), PerfResultItem> ix, string d, string o, int t, string a)
        => ix.TryGetValue((d, o, t, a), out PerfResultItem? item) ? item : null;

    private static bool IsFlagged(double ratio) => ratio is >= 1.5 or <= 0.67;

    /// <summary>判别力弱阈值——沿用 <c>Measure.cs</c> 的量具自检线（Error/Mean &gt; 5% 标黄）。</summary>
    private const double NoiseLine = 0.05;

    /// <summary>判别力弱标注（2026-10-04）：该键三臂里最大 ErrorRatio 超线时，比值落在噪声带内、
    /// 不足以支撑结论。取三臂最大值而非只看被测臂——比值 = 被测臂 / 地板，两侧噪声都会放大
    /// 比值的不确定度。任一侧缺 ErrorRatio（未采集）按 0 处理，不据此标注。</summary>
    private static string WeakMark(params PerfResultItem?[] arms)
    {
        foreach (PerfResultItem? arm in arms)
        {
            if (arm is not null && arm.ErrorRatio > NoiseLine)
            {
                return " ⚠";
            }
        }

        return "";
    }

    private static string RatioCell(double ratio)
    {
        double shown = Math.Round(ratio, 2);
        string cell = $"{shown.ToString("F2", CultureInfo.InvariantCulture)} {Emoji(ratio - 1)}{Pct(ratio - 1)}";
        return shown is >= 1.3 or <= 0.7 ? $"**{cell}**" : cell;
    }

    /// <summary>分配相对 ADO 的「P · D」记法：PalORM 差值在前（无前缀），Dapper 差值带 D 前缀在后。</summary>
    private static string AllocDeltaCell(PerfResultItem pal, PerfResultItem? dap, PerfResultItem ado)
    {
        string p = ado.AllocBytes > 0 ? Delta((pal.AllocBytes / (double)ado.AllocBytes) - 1) : "—";
        if (dap is null || ado.AllocBytes <= 0) return $"{p} · D —";
        return $"{p} · D {Delta((dap.AllocBytes / (double)ado.AllocBytes) - 1)}";
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
