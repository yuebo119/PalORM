using System.Globalization;
using System.Text;
using System.Text.Json;
using PalORM.Bench.Shared;

namespace PalORM.PerfGate;

/// <summary>四组表生成器（性能测试结果输出规范的固定表格组：CRUD 单行与读 / 批量 / 事务 /
/// 跨方言比值）。数据源 = 最新非子集 perfhub 批次的信封（MedianUs/AllocBytes/Ratio 逐项）。
/// <para>格式口径与规范一致（2026-10-04 step23 用户改版）：明细表（一/二/三）每行展开
/// 「方言」列覆盖三方言；时延数值自带单位（µs/ms，2026-10-05 用户定标）；比值列 = 裸倍数两位小数 +
/// 空格 + 色标紧贴百分比（无括号）；分配相对 ADO 拆 P/D 两列右对齐；表四只列批量族 +
/// GetAllAsync，列序 SQLite/PostgreSQL/MySQL，格值为裸比值。分配 1024 进制 KB/MB、
/// 百分比取整（|p|&lt;0.5% 记 0%）、色标六档按显示值判定、倍数 ≥1.3 或 ≤0.7 加粗。</para>
/// <para><b>行级标记 📌（2026-10-05 用户定标，历经 🚨 → 🔍 → 📌 → 复核回 📌）</b>：比值
/// ≥1.50 或 ≤0.67 时操作名/格值前置 📌。语义 = 标记待看：远离基准需人工判读（劣化 =
/// 回归风险；优于地板 &gt;33% = 地板健全性待核），<b>不是警告</b>——远优方向同样标 📌；
/// 弃用 🚨（警示灯语义易误读为"出错"）与 🔍（用户复判不够清晰）。</para>
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
    /// <summary>行级 📌：比值 ≥1.50 或 ≤0.67（标记待看：远离基准需人工判读，非警告）。</summary>
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

        // 输出结构自检（2026-10-05 质量系统优化轮，B126 配套）：以分隔行锁定每表列数，
        // 其后所有表格行必须同列数——机械化拦截"行首漏管道符/列漂移"类缺陷
        // （本会话表四曾漏行首 | 生成断裂表，靠人工重生成核对才发现）。
        foreach (string warning in StructuralWarnings(md.ToString()))
        {
            md.AppendLine(warning);
            Console.Error.WriteLine(warning);
        }

        // 数值哨兵（B148 配套，2026-10-03 教训：long/long 整数除法截断让分配百分比列全 0%，
        // 三道防线全漏）：批量族各行对 ADO 分配差恒 0 时报错——ADO/PalORM 分配逐位相同的
        // 概率可忽略，全 0 只可能是算式坏了（截断/漏算/取错字段）。
        foreach (string warning in NumericSentinelWarnings(md.ToString()))
        {
            md.AppendLine(warning);
            Console.Error.WriteLine(warning);
        }

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

    /// <summary>表格结构自检，两条判据（2026-10-05 质量系统优化轮，变异探针三轮定稿）：
    /// ① 列数不变量：分隔行（|---|…）确定该表列数，其后表格行必须同列数，表头（下一行是
    /// 分隔行）重置期望列数（表间列数不同不误报）；② 行首缺管道符：非表格行但含 ≥2 个
    /// 管道符 = 数据行断裂形态（标题/图例/空行管道数为 0，天然免疫）。
    /// 告警非致命——追加到输出尾部 + stderr（报告管线对四组表有容错，结构告警必须留在
    /// 产物里可见，与 B123"零匹配假绿"同族：坏输出要能自己喊出来）。</summary>
    private static List<string> StructuralWarnings(string md)
    {
        var warnings = new List<string>();
        string[] lines = md.Split('\n');
        var state = new TableScanState();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.StartsWith('|', StringComparison.Ordinal))
            {
                OnTableLine(warnings, lines, i, line, state);
            }
            else if (state.Expected > 0 && line.Count('|') >= 2)
            {
                // 判据②：非表格行但含 ≥2 个管道符 = 数据行断裂形态（标题/图例/空行管道数为 0）
                warnings.Add($"[TableGen] 结构告警：第 {i + 1} 行疑似行首缺管道符：{Truncate(line)}");
            }
        }

        return warnings;
    }

    /// <summary>判据①（表格行）：分隔行设定该表期望列数；新表头（下一行是分隔行）重置期望
    /// （表间列数不同是合法形态）；其余表格行列数必须等于期望；分隔行额外核对表头列数。</summary>
    private sealed class TableScanState
    {
        public int Expected;
        public int LastColumns;
    }

    /// <summary>数值哨兵（B148 配套）：表 2（批量处理）里"分配相对 ADO"末列全为 0% 即报错。
    /// 该列由 long/long 相除得比值——若忘 `(double)` 转型，截断让商恒 0/1、delta 恒 0%，
    /// 全列 `⚪ 0%`（实测：ADO 2.1KB/PalORM 3.0KB 明明差 +42% 却显示 0%）。ADO 与 PalORM
    /// 的分配逐位相同的概率可忽略，全 0 只可能是算式坏（截断/漏算/取错字段），不是数据态。</summary>
    private static List<string> NumericSentinelWarnings(string md)
    {
        var warnings = new List<string>();
        bool inTable2 = false;
        int zeroRows = 0, totalRows = 0;
        foreach (string raw in md.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("**表 2", StringComparison.Ordinal)) inTable2 = true;
            else if (line.StartsWith("**表 3", StringComparison.Ordinal)) break;
            else if (inTable2 && line.StartsWith('|', StringComparison.Ordinal) && !line.StartsWith("|---", StringComparison.Ordinal)
                && !line.StartsWith("| 操作", StringComparison.Ordinal))
            {
                totalRows++;
                if (line.Contains("⚪ 0%", StringComparison.Ordinal)) zeroRows++;
            }
        }

        if (totalRows >= 4 && zeroRows == totalRows)
        {
            warnings.Add($"[TableGen] 数值哨兵：表 2 的 {totalRows} 行分配相对 ADO 全为 0%——"
                + "ADO/PalORM 分配逐位相同的概率可忽略，疑似比值算式坏了（B148：long/long 整数除法截断）");
        }

        return warnings;
    }

    private static void OnTableLine(List<string> warnings, string[] lines, int i, string line, TableScanState state)
    {
        int columns = line.Count('|') - 1;
        bool isSeparator = line.StartsWith("|---", StringComparison.Ordinal);
        if (isSeparator)
        {
            if (state.LastColumns > 0 && state.LastColumns != columns)
            {
                warnings.Add($"[TableGen] 结构告警：第 {i} 行表头列数 {state.LastColumns} ≠ 分隔行 {columns}");
            }

            state.Expected = columns;
        }
        else if (FollowedBySeparator(lines, i))
        {
            state.Expected = 0;
        }
        else if (state.Expected > 0 && columns != state.Expected)
        {
            warnings.Add($"[TableGen] 结构告警：第 {i + 1} 行列数 {columns} ≠ 表头 {state.Expected}：{Truncate(line)}");
        }

        state.LastColumns = columns;
    }

    /// <summary>本行是否是"新表的表头"（下一行为分隔行）。</summary>
    private static bool FollowedBySeparator(string[] lines, int i)
        => i + 1 < lines.Length && lines[i + 1].TrimEnd('\r').StartsWith("|---", StringComparison.Ordinal);

    private static string Truncate(string line)
        => line.Length <= 60 ? line : line[..60];

    private static void Table1(StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        md.AppendLine("**一、CRUD 单行与读**");
        md.AppendLine();
        Header(md);
        foreach (string op in CrudOps)
        {
            foreach (string dialect in Dialects)
            {
                foreach (int tier in new[] { minTier, 20000 })
                {
                    Row(md, ix, dialect, op, tier, flaggable: true);
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
        foreach (string op in BulkOps)
        {
            foreach (string dialect in Dialects)
            {
                foreach (int tier in new[] { minTier, 20000 })
                {
                    Row(md, ix, dialect, op, tier, flaggable: true);
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
        foreach (string op in TxOps)
        {
            foreach (string dialect in Dialects)
            {
                Row(md, ix, dialect, op, minTier, flaggable: true);
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
        foreach (string op in CrossOps)
        {
            foreach (int tier in new[] { minTier, 20000 })
            {
                if (Get(ix, "SQLite", op, tier, "PalORM") is null && tier != minTier) continue;
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {CrossFlag(ix, op, tier)}{op} | {tier} | {CrossCell(ix, "SQLite", op, tier)} | "
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
        // 分配相对 ADO 拆 P/D 两列右对齐（2026-10-05 用户定标）：两列之间的边框即竖向中线，
        // 由渲染器保证对齐——单格内以分隔符对齐在 GFM 下不可行（空格折叠 + 比例字体）。
        md.AppendLine("| 操作 | 方言 | 档 | ADO.NET | Dapper | PalORM | P/ADO（倍数±%） | P/Dapper（倍数±%） | 分配（A/D/P） | 分配相对 ADO（P） | 分配相对 ADO（D） |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---:|---:|");
    }

    private static void Row(
        StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix,
        string dialect, string op, int tier, bool flaggable)
    {
        PerfResultItem? ado = Get(ix, dialect, op, tier, Floor);
        PerfResultItem? dap = Get(ix, dialect, op, tier, "Dapper");
        PerfResultItem? pal = Get(ix, dialect, op, tier, "PalORM");
        if (ado is null || pal is null) return;

        string pAdo = RatioCell(pal.MedianUs / ado.MedianUs) + WeakMark(ado, dap, pal);
        string pDap = dap is null || dap.MedianUs <= 0 ? "—" : RatioCell(pal.MedianUs / dap.MedianUs);
        string alloc = $"{FmtBytes(ado.AllocBytes)}/{FmtBytes(dap?.AllocBytes ?? 0)}/{FmtBytes(pal.AllocBytes)}";
        string flag = flaggable && ado.MedianUs > 0 && IsFlagged(pal.MedianUs / ado.MedianUs) ? $"{Flag} " : "";
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {flag}{op} | {dialect} | {tier} | {FmtUs(ado.MedianUs)} | {FmtUs(dap?.MedianUs)} | {FmtUs(pal.MedianUs)} "
            + $"| {pAdo} | {pDap} | {alloc} | {PalDeltaCell(pal, ado)} | {DapDeltaCell(dap, ado)} |"));
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

    /// <summary>分配相对 ADO 的 P 列（PalORM 对 ADO，右对齐）——拆两列形态（2026-10-05 用户定标）：
    /// 列边框即竖向中线，渲染器保证对齐。单格内以分隔符对齐在 GFM 下不可行（空格折叠 +
    /// 比例字体），故放弃单格 丨 形态。</summary>
    private static string PalDeltaCell(PerfResultItem pal, PerfResultItem ado)
        => ado.AllocBytes > 0 ? Delta((pal.AllocBytes / (double)ado.AllocBytes) - 1) : "—";

    /// <summary>分配相对 ADO 的 D 列（Dapper 对 ADO，右对齐）；Dapper 缺失或 ADO 零分配为 —。</summary>
    private static string DapDeltaCell(PerfResultItem? dap, PerfResultItem ado)
        => dap is null || ado.AllocBytes <= 0 ? "—" : Delta((dap.AllocBytes / (double)ado.AllocBytes) - 1);

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

    /// <summary>时延数值自带单位（2026-10-05 用户定标恢复带单位）：µs（≥1 一位小数，&lt;1 三位小数）
    /// 或 ms（≥1000 换算两位小数）——与 AGENTS.md 口径"单元格数值自带单位"一致
    /// （当前夹具面最小中位 ~6µs，子 1µs 为防御）。</summary>
    private static string FmtUs(double? medianUs)
    {
        if (medianUs is null or <= 0) return "—";
        double us = medianUs.Value;
        if (us >= 1000) return (us / 1000).ToString("F2", CultureInfo.InvariantCulture) + " ms";
        return us < 1
            ? us.ToString("F3", CultureInfo.InvariantCulture) + " µs"
            : us.ToString("F1", CultureInfo.InvariantCulture) + " µs";
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
