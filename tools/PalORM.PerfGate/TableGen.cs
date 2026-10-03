using System.Globalization;
using System.Text;
using System.Text.Json;
using PalORM.Bench.Shared;

namespace PalORM.PerfGate;

/// <summary>四组表生成器（性能测试结果输出规范的固定表格组：CRUD 单行与读 / 批量 / 事务 /
/// 跨方言比值）。数据源 = 最新非子集 perfhub 批次的信封（MedianUs/AllocBytes/Ratio 逐项）。
/// <para>格式口径与规范一致：中位 µs（自适应 ms）、分配 1024 进制 KB/MB、百分比取整
/// （|p|&lt;0.5% 记 0%）、色标六档按显示值判定、倍数 ≥1.3 或 ≤0.7 加粗、🚨 = ≥1.50 或 ≤0.67。</para>
/// <para>为什么做成工具而非汇报时手工贴：四组表曾是每次跑测后最大的人工步骤（.ai 本地脚本原型），
/// 固化后统一报告自动携带，人工只写"看点"与"行读法"的归因。</para></summary>
internal static class TableGen
{
    private static readonly string[] CrudOps =
        ["GetByKey", "QueryAll", "StreamAll", "Insert", "Update", "InsertReturningId"];
    private static readonly string[] BulkOps = ["BulkInsert", "BulkUpdate", "BulkDelete", "UpsertBatch"];
    private static readonly string[] TxOps = ["TxSingleInsert", "TxHundredInserts", "TxBulkInsert", "TxRollback"];
    private static readonly string[] Dialects = ["SQLite", "MySQL", "PostgreSQL"];
    private const string Floor = "ADO_NET";

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

    private static void Table1(StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        md.AppendLine("**表 1 · CRUD 单行与读（SQLite）**");
        md.AppendLine();
        Header(md);
        foreach (string op in CrudOps)
        {
            foreach (int tier in new[] { minTier, 20000 })
            {
                Row(md, ix, "SQLite", op, tier, flaggable: true);
            }
        }
    }

    private static void Table2(StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        md.AppendLine();
        md.AppendLine("**表 2 · 批量处理（SQLite）**");
        md.AppendLine();
        Header(md);
        foreach (string op in BulkOps)
        {
            foreach (int tier in new[] { minTier, 20000 })
            {
                Row(md, ix, "SQLite", op, tier, flaggable: true);
            }
        }
    }

    private static void Table3(StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        md.AppendLine();
        md.AppendLine("**表 3 · 事务（SQLite，仅最小档）**");
        md.AppendLine();
        Header(md);
        foreach (string op in TxOps)
        {
            Row(md, ix, "SQLite", op, minTier, flaggable: true);
        }
    }

    private static void Table4(StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix, int minTier)
    {
        md.AppendLine();
        md.AppendLine("**表 4 · 跨方言 PalORM/ADO 比值**");
        md.AppendLine();
        md.AppendLine("| 操作 | 档 | SQLite | MySQL | PostgreSQL |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (string op in CrudOps.Concat(BulkOps).Concat(TxOps))
        {
            foreach (int tier in new[] { minTier, 20000 })
            {
                if (Get(ix, "SQLite", op, tier, "PalORM") is null && tier != minTier) continue;
                md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{CrossFlag(ix, op, tier)}{op} | {tier} | {CrossCell(ix, "SQLite", op, tier)} | "
                    + $"{CrossCell(ix, "MySQL", op, tier)} | {CrossCell(ix, "PostgreSQL", op, tier)} |"));
            }
        }
    }

    private static string CrossFlag(
        Dictionary<(string, string, int, string), PerfResultItem> ix, string op, int tier)
    {
        foreach (string dialect in Dialects)
        {
            if (CrossRatio(ix, dialect, op, tier) is { } r && IsFlagged(r)) return "🚨 ";
        }

        return "";
    }

    private static string CrossCell(
        Dictionary<(string, string, int, string), PerfResultItem> ix, string dialect, string op, int tier)
    {
        if (CrossRatio(ix, dialect, op, tier) is not { } ratio) return "—";
        string cell = $"{Emoji(ratio - 1)} {ratio.ToString("F2", CultureInfo.InvariantCulture)}×";
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
        md.AppendLine("| 操作 | 档 | ADO.NET | Dapper | PalORM | P/ADO | P/Dapper | 分配 ADO/Dapper/PalORM | 分配相对 ADO（D/P） |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
    }

    private static void Row(
        StringBuilder md, Dictionary<(string, string, int, string), PerfResultItem> ix,
        string dialect, string op, int tier, bool flaggable)
    {
        PerfResultItem? ado = Get(ix, dialect, op, tier, Floor);
        PerfResultItem? dap = Get(ix, dialect, op, tier, "Dapper");
        PerfResultItem? pal = Get(ix, dialect, op, tier, "PalORM");
        if (ado is null || pal is null) return;

        string pAdo = RatioCell(pal.MedianUs / ado.MedianUs);
        string pDap = dap is null || dap.MedianUs <= 0 ? "—" : RatioCell(pal.MedianUs / dap.MedianUs);
        string alloc = $"{FmtBytes(ado.AllocBytes)}/{FmtBytes(dap?.AllocBytes ?? 0)}/{FmtBytes(pal.AllocBytes)}";
        string dDelta = DapDelta(dap, ado);
        string pDelta = PalDelta(pal, ado);
        string flag = flaggable && ado.MedianUs > 0 && IsFlagged(pal.MedianUs / ado.MedianUs) ? "🚨 " : "";
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {flag}{op} | {tier} | {FmtUs(ado.MedianUs)} | {FmtUs(dap?.MedianUs)} | {FmtUs(pal.MedianUs)} "
            + $"| {pAdo} | {pDap} | {alloc} | {dDelta} / {pDelta} |"));
    }

    private static PerfResultItem? Get(
        Dictionary<(string, string, int, string), PerfResultItem> ix, string d, string o, int t, string a)
        => ix.TryGetValue((d, o, t, a), out PerfResultItem? item) ? item : null;

    private static bool IsFlagged(double ratio) => ratio is >= 1.5 or <= 0.67;

    private static string RatioCell(double ratio)
    {
        double shown = Math.Round(ratio, 2);
        string cell = $"{Emoji(ratio - 1)} {shown.ToString("F2", CultureInfo.InvariantCulture)}× ({Pct(ratio - 1)})";
        return shown is >= 1.3 or <= 0.7 ? $"**{cell}**" : cell;
    }

    private static string PalDelta(PerfResultItem pal, PerfResultItem ado)
        => ado.AllocBytes > 0 ? Delta((pal.AllocBytes / (double)ado.AllocBytes) - 1) : "—";

    private static string DapDelta(PerfResultItem? dap, PerfResultItem ado)
        => dap is null || ado.AllocBytes <= 0 ? "—" : Delta((dap.AllocBytes / (double)ado.AllocBytes) - 1);

    private static string Delta(double p)
    {
        int q = Math.Abs(p * 100) < 0.5 ? 0 : (int)Math.Round(p * 100, MidpointRounding.AwayFromZero);
        return $"{Emoji(p)} {q:+0;-0;0}%";
    }

    private static string Pct(double p)
    {
        int q = Math.Abs(p * 100) < 0.5 ? 0 : (int)Math.Round(p * 100, MidpointRounding.AwayFromZero);
        return $"{q:+0;-0;0}%";
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

    private static string FmtUs(double? medianUs)
    {
        if (medianUs is null or <= 0) return "—";
        double us = medianUs.Value;
        if (us < 1) return us.ToString("F3", CultureInfo.InvariantCulture) + " µs";
        if (us < 10) return us.ToString("F2", CultureInfo.InvariantCulture) + " µs";
        if (us < 1000) return us.ToString("F1", CultureInfo.InvariantCulture) + " µs";
        return (us / 1000).ToString("F2", CultureInfo.InvariantCulture) + " ms";
    }

    private static string FmtBytes(double bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return Math.Round(bytes).ToString(CultureInfo.InvariantCulture) + " B";
        double kb = bytes / 1024;
        if (kb < 100) return kb.ToString("F1", CultureInfo.InvariantCulture) + " KB";
        if (kb < 1024) return Math.Round(kb).ToString(CultureInfo.InvariantCulture) + " KB";
        return (kb / 1024).ToString("F1", CultureInfo.InvariantCulture) + " MB";
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
