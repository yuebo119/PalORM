using System.Globalization;
using System.Text;
using PalORM.Bench.Shared;

namespace PalORM.PerfGate;

/// <summary>8+1 组跨库横评表生成器（2026-11-02 用户定标「单源 8+1 重组」）。
/// <para><b>数据源唯一</b>：CrossBench 最近完整批（8 库 + ADO 基准 = 9 组，同批同轮、两遍反序平均）。
/// 此前四组表从 PerfHub 信封取 ADO/Dapper/PalORM、从 CrossBench 批取 FreeSql/RepoDb，同一行
/// 混两个批次与两套分母；本生成器删除该跨批拼装，所有比值的分母都是同批 ADO 基准。</para>
/// <para><b>表格组</b>：表一 CRUD / 表二 批量 / 表三 事务 / 表四 跨方言比值 / 表五 分配 /
/// 表六 补充读，外加覆盖账本与异常判读。每格两行：主值 + 派生值（时延表 = P/ADO 比值，
/// 分配表 = 相对 ADO 百分比）。</para>
/// <para><b>口径</b>沿用 2026-10-04/10-05 定标：色标六档、📌 = 比值 ≥1.50 或 ≤0.67（标记待看，
/// 非警告）、⚠ = 任一侧 ErrorRatio &gt; 5%（判别力弱）。时延数值自带单位。</para>
/// </summary>
internal static class TableGen
{
    private static readonly string[] CrudOps =
        ["GetByKey", "GetAllAsync", "QueryAll", "Insert", "Update"];
    private static readonly string[] BulkOps = ["BulkInsert", "BulkUpdate", "BulkDelete", "UpsertBatch"];
    private static readonly string[] TxOps = ["TxSingleInsert", "TxHundredInserts", "TxBulkInsert", "TxRollback", "EnumInserts"];
    private static readonly string[] ExtraReadOps = ["KeysetPage", "WhereIn", "Count"];
    private static readonly string[] Dialects = ["SQLite", "MySQL", "PostgreSQL"];
    /// <summary>列序（2026-11-02 用户定标）：基准 → PalORM → Dapper.AOT → RepoDb → EF Core
    /// → Dapper → FreeSql → linq2db → SqlSugar。与旧报告（按字母序）不同，改序需同步 AGENTS.md
    /// 列固定条与 docs/性能汇报样例。</summary>
    private static readonly (string Key, string Display)[] Arms =
    [
        ("ADO_NET", "ADO(基准)"),
        ("PalORM", "PalORM"),
        ("DapperAot", "Dapper.AOT"),
        ("RepoDb", "RepoDb"),
        ("EfCore", "EF Core"),
        ("Dapper", "Dapper"),
        ("FreeSql", "FreeSql"),
        ("Linq2Db", "linq2db"),
        ("SqlSugar", "SqlSugar"),
    ];
    private const string Floor = "ADO_NET";
    /// <summary>行级 📌：比值 ≥1.50 或 ≤0.67（标记待看：远离基准需人工判读，非警告）。</summary>
    private const string Flag = "📌";
    /// <summary>判别力弱阈值——沿用 Measure.cs 的量具自检线（Error/Mean &gt; 5% 标黄）。</summary>
    private const double NoiseLine = 0.05;

    public static int Run(string resultsDir, string? outPath)
    {
        // 目录按约定从信封目录推导（<root>/bench/results → <root>/bench/crossbench/results），
        // 与 PerfCli 的 CrossBenchResultsDir 同源；不新增 CLI 参数。
        string crossDir = Path.GetFullPath(Path.Combine(resultsDir, "..", "crossbench", "results"));
        string? file = CrossBenchBatch.LatestFullBatchPath(crossDir);
        if (file is null)
        {
            Console.Error.WriteLine("[PerfGate] tables: 结果库里没有完整 CrossBench 批（8 库 + ADO 基准 × 三方言 × 两档）");
            return 1;
        }

        List<CrossBenchBatch.CrossBenchMeasurement>? items = CrossBenchBatch.LoadAggregated(file);
        if (items is null)
        {
            Console.Error.WriteLine($"[PerfGate] tables: 无法解析 {file}");
            return 1;
        }

        var ix = new Dictionary<(string D, string O, int T, string A), CrossBenchBatch.CrossBenchMeasurement>();
        foreach (CrossBenchBatch.CrossBenchMeasurement m in items)
        {
            ix[(m.Dialect, m.Operation, m.Rows, m.Implementation)] = m;
        }

        int[] tiers = [.. items.Select(static m => m.Rows).Distinct().Order()];
        int[] twoTiers = tiers.Length >= 2 ? [tiers[0], tiers[^1]] : [tiers[0]];

        var md = new StringBuilder(1 << 15);
        AppendHeader(md, file, ix, twoTiers);
        AppendSummary(md, ix, twoTiers);
        AppendLatencyTable(md, ix, "**一、CRUD 单行与读**（每格：时延 / 时延百分比 / 内存 / 内存百分比；精确倍数见表四）", CrudOps, twoTiers);
        AppendLatencyTable(md, ix, "**二、批量处理**（每格：时延 / 时延百分比 / 内存 / 内存百分比；精确倍数见表四）", BulkOps, twoTiers);
        AppendLatencyTable(md, ix, "**三、事务**（仅最小档；每格：时延 / 时延百分比 / 内存 / 内存百分比；精确倍数见表四）", TxOps, [tiers[0]]);
        AppendCrossDialectTable(md, ix, twoTiers);
        AppendLatencyTable(md, ix, "**五、补充读**（每格：时延 / 时延百分比 / 内存 / 内存百分比；精确倍数见表四）", ExtraReadOps, twoTiers);
        AppendCoverageLedger(md, ix);
        AppendOutliers(md, ix, tiers);

        md.AppendLine();
        md.AppendLine("**图例**");
        md.AppendLine();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- 百分比 = 该组时延 ÷ 同方言同操作同档 ADO 基准 − 1；色标六档（🟢 ≤−30% · 🟩 −29%~−10% · 🔹 −9%~−1% · ⚪ 0% · 🔸 +1%~+9% · 🟧 +10%~+29% · 🟥 ≥+30%）。精确倍数（两位小数）见表四。"));
        md.AppendLine("- 📌 = 比值 ≥1.50 或 ≤0.67：劣化方向是回归风险，远优方向是基准健全性待核，两者都需人工判读，不等于告警。");
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- ⚠ = 判别力弱：该格任一侧 ErrorRatio（标准误/均值）> {NoiseLine * 100:0}%，比值落在噪声带内、不足以支撑结论。"));
        md.AppendLine("- 分配相对 ADO = 该组 AllocatedBytesPerOp ÷ 同行 ADO 分配 − 1；ADO 分配为 0 或缺值时记 —。");
        md.AppendLine("- 明细表第三行 = 内存绝对值（B/op，1024 进制），第四行 = 内存相对 ADO 百分比。");
        md.AppendLine("- 基准格第二、四行为 —（基准相对自己没有百分比），四行占位保证各列逐行对齐。");
        md.AppendLine("- 两遍反序批内 Average（叉评协议）；比值同批推导，不跨批引用。");
        md.AppendLine();

        // 结构自检（2026-10-05 质量系统优化轮，B126 配套）：以分隔行锁定每表列数，
        // 其后所有表格行必须同列数——机械化拦截"行首漏管道符/列漂移"类缺陷。
        foreach (string warning in StructuralWarnings(md.ToString()))
        {
            md.AppendLine(warning);
            Console.Error.WriteLine(warning);
        }

        // 数值哨兵（B148 配套，改写适配 8+1 形态）：分配表的非基准格若整批全是 ⚪+0%，
        // 说明"相对 ADO"算式坏了（截断/漏算/取错字段），而非真实数据态。
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
            Console.WriteLine($"[PerfGate] 8+1 组横评表已写入 {outPath}");
        }

        return 0;
    }

    /// <summary>报告头：数据源、批次时间、覆盖统计、口径一句话。
    /// 覆盖统计是"缺格可见"的机械落点：某组某方言整列缺失时，表头先亮数字，不靠读者逐表扫 —。</summary>
    private static void AppendHeader(
        StringBuilder md, string file,
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        int[] twoTiers)
    {
        (int cells, int points, int missing) = Coverage(ix, twoTiers);
        string stamp = ParseBatchStamp(file);
        md.AppendLine("## 8+1 组跨库横评（CrossBench 单批：ADO 基准 + 8 库）");
        md.AppendLine();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- 数据源：`{Path.GetFileName(file)}`（批次 {stamp}，组数 {Arms.Length}）"));
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- 口径：三方言 × 两档（{twoTiers[0]:N0}/{twoTiers[^1]:N0}）× 17 操作 × 两遍反序（批内 Average）；比值分母 = 同批 ADO 基准；明细表每格 = 时延 / 时延百分比 / 内存 / 内存百分比，精确倍数见表四"));
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- 覆盖：{cells} 格 × {Arms.Length} 组 = {points} 个测量点，实采 {points - missing}，缺 {missing}"));
        if (missing > 0)
        {
            md.AppendLine("- ⚠ 存在缺失测量点：相关格记 —，缺失清单见「六、覆盖账本」；缺格不参与胜场与中位比。");
        }

        md.AppendLine();
    }

    /// <summary>结论面板（当批推导）：可比格数、胜场榜、PalORM 中位比 ADO 与区间。
    /// 全部从同一批数据算，不引用历史结论；胜场只统计"至少两组有值"的格，缺格不虚增。</summary>
    private static void AppendSummary(
        StringBuilder md,
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        int[] twoTiers)
    {
        (int cells, Dictionary<string, int> wins, List<double> palormRatios) = Summarize(ix, twoTiers);
        md.AppendLine("**结论面板**（当批推导）");
        md.AppendLine();
        if (cells == 0)
        {
            md.AppendLine("- 本批无可比格（数据不足）。");
            md.AppendLine();
            return;
        }

        string winLine = string.Join("、", wins.OrderByDescending(kv => kv.Value).Take(6)
            .Select(kv => $"{Display(kv.Key)} {kv.Value}"));
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- 可比格 {cells}（至少两组有值）；胜场：{(winLine.Length > 0 ? winLine : "无")}"));
        if (palormRatios.Count > 0)
        {
            palormRatios.Sort();
            double median = palormRatios[palormRatios.Count / 2];
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- PalORM 中位比 ADO 基准：{median:F2}（区间 {palormRatios[0]:F2} ~ {palormRatios[^1]:F2}，{palormRatios.Count} 格）"));
        }

        md.AppendLine();
    }

    private static (int Cells, Dictionary<string, int> Wins, List<double> PalormRatios) Summarize(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        int[] tiers)
    {
        int cells = 0;
        var wins = new Dictionary<string, int>();
        var ratios = new List<double>();
        foreach ((string op, int[] opTiers) in OpTiers(tiers.Length >= 2 ? [tiers[0], tiers[^1]] : tiers))
        {
            foreach (string dialect in Dialects)
            {
                foreach (int tier in opTiers)
                {
                    if (FastestArm(ix, dialect, op, tier) is { } fastest)
                    {
                        cells++;
                        wins[fastest] = wins.TryGetValue(fastest, out int n) ? n + 1 : 1;
                    }

                    if (CrossRatio(ix, dialect, op, tier) is { } ratio)
                    {
                        ratios.Add(ratio);
                    }
                }
            }
        }

        return (cells, wins, ratios);
    }

    /// <summary>该格最快组（排除基准；至少两组有值才算可比较）。</summary>
    private static string? FastestArm(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        string dialect, string op, int tier)
    {
        string? fastest = null;
        double best = double.MaxValue;
        int present = 0;
        foreach ((string arm, _) in Arms)
        {
            CrossBenchBatch.CrossBenchMeasurement? m = Get(ix, dialect, op, tier, arm);
            if (m is null)
            {
                continue;
            }

            present++;
            if (arm != Floor && m.MedianUs < best)
            {
                best = m.MedianUs;
                fastest = arm;
            }
        }

        return present >= 2 ? fastest : null;
    }

    internal static string ParseBatchStamp(string file)
    {
        // 文件名 crossbench-yyyyMMdd-HHmmss.json（Program 生成规则）；解析失败回退文件修改时间。
        string name = Path.GetFileNameWithoutExtension(file);
        const string Prefix = "crossbench-";
        if (name.StartsWith(Prefix, StringComparison.Ordinal)
            && DateTime.TryParseExact(
                name[Prefix.Length..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime parsed))
        {
            return parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static (int Cells, int Points, int Missing) Coverage(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix, int[] twoTiers)
    {
        int cells = 0, points = 0, missing = 0;
        foreach ((string op, int[] tiers) in OpTiers(twoTiers))
        {
            foreach (string dialect in Dialects)
            {
                foreach (int tier in tiers)
                {
                    cells++;
                    foreach ((string arm, _) in Arms)
                    {
                        points++;
                        if (!ix.ContainsKey((dialect, op, tier, arm)))
                        {
                            missing++;
                        }
                    }
                }
            }
        }

        return (cells, points, missing);
    }

    private static IEnumerable<(string Op, int[] Tiers)> OpTiers(int[] twoTiers)
    {
        foreach (string op in CrudOps.Concat(BulkOps).Concat(ExtraReadOps))
        {
            yield return (op, twoTiers);
        }

        foreach (string op in TxOps)
        {
            yield return (op, [twoTiers[0]]);
        }
    }

    private static void AppendLatencyTable(
        StringBuilder md,
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        string title, string[] ops, int[] tiers)
    {
        md.AppendLine(title);
        md.AppendLine();
        Header(md);
        foreach (string op in ops)
        {
            foreach (string dialect in Dialects)
            {
                foreach (int tier in tiers)
                {
                    Row(md, ix, dialect, op, tier, latency: true);
                }
            }
        }

        md.AppendLine();
    }

    private static void Header(StringBuilder md)
    {
        md.Append("| 操作 | 方言 | 档 |");
        foreach ((_, string display) in Arms)
        {
            md.Append(' ').Append(display).Append(" |");
        }

        md.AppendLine();
        md.Append("|---|---|---:|");
        foreach ((_, _) in Arms)
        {
            md.Append("---:|");
        }

        md.AppendLine();
    }

    /// <summary>数据行：跳过整行九组全缺的格（不产出全 — 行），否则输出 操作 × 方言 × 档 + 每臂两行。</summary>
    private static void Row(
        StringBuilder md,
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        string dialect, string op, int tier, bool latency)
    {
        if (Arms.All(a => Get(ix, dialect, op, tier, a.Key) is null))
        {
            return;
        }

        CrossBenchBatch.CrossBenchMeasurement? floor = Get(ix, dialect, op, tier, Floor);
        double palRatio = floor is null || floor.MedianUs <= 0
            ? 0
            : (Get(ix, dialect, op, tier, "PalORM")?.MedianUs ?? 0) / floor.MedianUs;
        string flag = palRatio > 0 && IsFlagged(palRatio) ? $"{Flag} " : "";

        md.Append("| ").Append(flag).Append(op)
            .Append(" | ").Append(dialect)
            .Append(" | ").Append(tier.ToString("N0", CultureInfo.InvariantCulture)).Append(" |");
        foreach ((string arm, _) in Arms)
        {
            CrossBenchBatch.CrossBenchMeasurement? m = Get(ix, dialect, op, tier, arm);
            md.Append(' ').Append(Cell(m, floor, arm, latency)).Append(" |");
        }

        md.AppendLine();
    }

    private static string Cell(
        CrossBenchBatch.CrossBenchMeasurement? m,
        CrossBenchBatch.CrossBenchMeasurement? floor,
        string arm, bool latency)
    {
        if (m is null)
        {
            return "—";
        }

        if (latency)
        {
            string main = FmtUs(m.MedianUs);
            string mem = FmtBytes(m.AllocBytes);
            if (arm == Floor || floor is null || floor.MedianUs <= 0)
            {
                // 基准格同样四行，第二/四行写 [1.00]（基准相对自己 = 1.00 倍，2026-11-02 用户定标）：
                // 行数一致时渲染器的垂直居中才能让时延对时延、内存对内存。
                return $"{main}<br>[1.00]<br>{mem}<br>[1.00]";
            }

            // 2026-11-02：明细表第三行内存绝对值、第四行内存相对 ADO 百分比（与表五同公式）。
            // 百分比单独占行，列宽不随时延或内存数值变化而撑宽。
            string memDelta = floor.AllocBytes > 0 && m.AllocBytes > 0
                ? $"<br>{Delta((m.AllocBytes / floor.AllocBytes) - 1)}"
                : "<br>—";
            return $"{main}<br>{PctCell((m.MedianUs / floor.MedianUs) - 1)}{Weak(floor, m)}<br>{mem}{memDelta}";
        }

        string alloc = FmtBytes(m.AllocBytes);
        if (arm == Floor || floor is null || floor.AllocBytes <= 0)
        {
            // 分配表基准同样两行，第二行 [1.00]，与非基准格行数一致，避免垂直居中错位。
            return $"{alloc}<br>[1.00]";
        }

        return $"{alloc}<br>{Delta((m.AllocBytes / floor.AllocBytes) - 1)}";
    }

    /// <summary>表四：跨方言 PalORM/ADO 精确比值（全 17 操作）。明细表只给百分比，本表给裸倍数，
    /// 精确值按需查；比值与表一/二/三/六同源同批，无跨批口径差。</summary>
    private static void AppendCrossDialectTable(
        StringBuilder md,
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        int[] twoTiers)
    {
        md.AppendLine();
        md.AppendLine("**四、跨方言 PalORM/ADO 精确比值**（全 17 操作；格值 = 裸倍数，📌 = 需判读）");
        md.AppendLine();
        md.AppendLine("| 操作 | 档 | SQLite | PostgreSQL | MySQL |");
        md.AppendLine("|---|---:|---|---|---|");
        foreach ((string op, int[] opTiers) in OpTiers(twoTiers))
        {
            foreach (int tier in opTiers)
            {
                md.Append("| ");
                if (CrossRatio(ix, "SQLite", op, tier) is { } sq && IsFlagged(sq))
                {
                    md.Append(Flag).Append(' ');
                }

                md.Append(op).Append(" | ").Append(tier.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ");
                md.Append(CrossCell(ix, "SQLite", op, tier)).Append(" | ");
                md.Append(CrossCell(ix, "PostgreSQL", op, tier)).Append(" | ");
                md.Append(CrossCell(ix, "MySQL", op, tier)).Append(" |");
                md.AppendLine();
            }
        }

        md.AppendLine();
    }

    private static string CrossCell(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        string dialect, string op, int tier)
    {
        if (CrossRatio(ix, dialect, op, tier) is not { } ratio)
        {
            return "—";
        }

        string flag = IsFlagged(ratio) ? Flag : "";
        string cell = $"{flag}{ratio.ToString("F2", CultureInfo.InvariantCulture)}";
        double shown = Math.Round(ratio, 2);
        return shown is >= 1.3 or <= 0.7 ? $"**{cell}**" : cell;
    }

    private static double? CrossRatio(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        string dialect, string op, int tier)
    {
        CrossBenchBatch.CrossBenchMeasurement? ado = Get(ix, dialect, op, tier, Floor);
        CrossBenchBatch.CrossBenchMeasurement? orm = Get(ix, dialect, op, tier, "PalORM");
        return ado is null || orm is null || ado.MedianUs <= 0 ? null : orm.MedianUs / ado.MedianUs;
    }

    /// <summary>覆盖账本：每组每方言的有值点数 / 理论点数（每方言 29 = 12 操作 × 两档 + 5 事务 × 最小档）。
    /// 缺失时列出具体操作与档位，供判读"整列缺"还是"零星缺"。</summary>
    private static void AppendCoverageLedger(
        StringBuilder md,
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix)
    {
        md.AppendLine("**六、覆盖账本**（有值点数 / 理论点数；每方言理论 29 = 12 操作 × 两档 + 5 事务 × 最小档）");
        md.AppendLine();
        md.AppendLine("| 组 | SQLite | MySQL | PostgreSQL |");
        md.AppendLine("|---|---|---:|---:|");
        var missingDetail = new List<string>();
        foreach ((string arm, string display) in Arms)
        {
            md.Append("| ").Append(display).Append(" |");
            foreach (string dialect in Dialects)
            {
                (int have, int total, List<string> gaps) = ArmCoverage(ix, arm, dialect);
                md.Append(' ').Append(have.ToString(CultureInfo.InvariantCulture)).Append('/')
                    .Append(total.ToString(CultureInfo.InvariantCulture));
                if (gaps.Count > 0)
                {
                    md.Append(" ⚠");
                    missingDetail.Add($"{display}@{dialect}：{DescribeGaps(gaps, total)}");
                }

                md.Append(" |");
            }

            md.AppendLine();
        }

        md.AppendLine();
        if (missingDetail.Count == 0)
        {
            md.AppendLine("本批 9 组 × 三方言全部齐备，无缺失测量点。");
        }
        else
        {
            md.AppendLine("缺失明细（该批未采到；不含推断原因）：");
            md.AppendLine();
            foreach (string detail in missingDetail)
            {
                md.Append("- ").AppendLine(detail);
            }
        }

        md.AppendLine();
    }

    /// <summary>缺失描述（覆盖账本用）：整列缺只报点数；零星缺列具体项，超过 6 项截断，
    /// 防止一行 29 项把账本撑成正文（2026-11-02 旧批实测）。</summary>
    private static string DescribeGaps(List<string> gaps, int total)
    {
        if (gaps.Count == total)
        {
            return $"整列缺（{total} 点）";
        }

        return gaps.Count > 6
            ? $"缺 {gaps.Count} 点（{string.Join("、", gaps.Take(6))} 等）"
            : $"缺 {gaps.Count} 点（{string.Join("、", gaps)}）";
    }

    private static (int Have, int Total, List<string> Gaps) ArmCoverage(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        string arm, string dialect)
    {
        int have = 0, total = 0;
        var gaps = new List<string>();
        foreach (string op in CrudOps.Concat(BulkOps).Concat(ExtraReadOps))
        {
            foreach (int tier in AllTiers(ix, dialect))
            {
                total++;
                if (ix.ContainsKey((dialect, op, tier, arm)))
                {
                    have++;
                }
                else
                {
                    gaps.Add($"{op}@{tier:N0}");
                }
            }
        }

        foreach (string op in TxOps)
        {
            int tier = MinTier(ix, dialect);
            total++;
            if (ix.ContainsKey((dialect, op, tier, arm)))
            {
                have++;
            }
            else
            {
                gaps.Add($"{op}@{tier:N0}");
            }
        }

        return (have, total, gaps);
    }

    private static int[] AllTiers(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix, string dialect)
        => [.. ix.Keys.Where(k => k.Item1 == dialect).Select(k => k.Item3).Distinct().Order()];

    private static int MinTier(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix, string dialect)
    {
        int[] tiers = AllTiers(ix, dialect);
        return tiers.Length > 0 ? tiers[0] : 0;
    }

    /// <summary>异常判读（当批推导）：P/ADO ≥ 3× 或 ≤ 0.5× 的格。两侧都列：远优同样要过目
    /// （基准健全性待核）。不写归因，归因属人判读，报告只负责把格找出来。</summary>
    private static void AppendOutliers(
        StringBuilder md,
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        int[] tiers)
    {
        var slow = new List<(string Arm, string Dialect, string Op, int Tier, double Ratio)>();
        var fast = new List<(string Arm, string Dialect, string Op, int Tier, double Ratio)>();
        foreach ((string arm, string _) in Arms)
        {
            if (arm == Floor)
            {
                continue;
            }

            foreach ((string op, int[] opTiers) in OpTiers(tiers.Length >= 2 ? [tiers[0], tiers[^1]] : tiers))
            {
                foreach (string dialect in Dialects)
                {
                    foreach (int tier in opTiers)
                    {
                        if (CrossRatioArm(ix, dialect, op, tier, arm) is not { } ratio)
                        {
                            continue;
                        }

                        if (ratio >= 3.0)
                        {
                            slow.Add((Display(arm), dialect, op, tier, ratio));
                        }
                        else if (ratio <= 0.5)
                        {
                            fast.Add((Display(arm), dialect, op, tier, ratio));
                        }
                    }
                }
            }
        }

        md.AppendLine("**七、异常判读**（当批推导；≥3.0× 或 ≤0.5× ADO）");
        md.AppendLine();
        if (slow.Count == 0 && fast.Count == 0)
        {
            md.AppendLine("无 ≥3.0× 或 ≤0.5× ADO 的格。");
            md.AppendLine();
            return;
        }

        foreach ((string arm, string dialect, string op, int tier, double ratio) in slow.OrderByDescending(o => o.Ratio).Take(15))
        {
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- 🟥 {arm} {dialect} {op}@{tier:N0}：{ratio:F1}× ADO（显著慢，见对应表）"));
        }

        foreach ((string arm, string dialect, string op, int tier, double ratio) in fast.OrderBy(o => o.Ratio).Take(15))
        {
            md.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- 🟢 {arm} {dialect} {op}@{tier:N0}：{ratio:F2}× ADO（显著快，基准健全性待核）"));
        }

        md.AppendLine();
    }

    private static double? CrossRatioArm(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        string dialect, string op, int tier, string arm)
    {
        CrossBenchBatch.CrossBenchMeasurement? ado = Get(ix, dialect, op, tier, Floor);
        CrossBenchBatch.CrossBenchMeasurement? value = Get(ix, dialect, op, tier, arm);
        return ado is null || value is null || ado.MedianUs <= 0 ? null : value.MedianUs / ado.MedianUs;
    }

    private static string Display(string arm)
    {
        foreach ((string key, string display) in Arms)
        {
            if (key == arm)
            {
                return display;
            }
        }

        return arm;
    }

    private static CrossBenchBatch.CrossBenchMeasurement? Get(
        Dictionary<(string, string, int, string), CrossBenchBatch.CrossBenchMeasurement> ix,
        string d, string o, int t, string a)
        => ix.TryGetValue((d, o, t, a), out CrossBenchBatch.CrossBenchMeasurement? m) ? m : null;

    private static bool IsFlagged(double ratio) => ratio is >= 1.5 or <= 0.67;

    /// <summary>判别力弱标注：比值两侧（基准 / 被测臂）任一 ErrorRatio 超线即标——
    /// 比值的不确定度由两侧共同决定。</summary>
    private static string Weak(CrossBenchBatch.CrossBenchMeasurement? a, CrossBenchBatch.CrossBenchMeasurement? b)
        => (a?.ErrorRatio ?? 0) > NoiseLine || (b?.ErrorRatio ?? 0) > NoiseLine ? " ⚠" : "";

    /// <summary>紧凑比值格（列宽优先，2026-11-02 用户反馈后引入）：只显示色标 + 百分比，
    /// 精确倍数见表四。加粗条件与旧比值列一致（|Δ| ≥30%，等价于倍数 ≥1.3 或 ≤0.7）。</summary>
    private static string PctCell(double p)
    {
        int q = Math.Abs(p * 100) < 0.5 ? 0 : (int)Math.Round(p * 100, MidpointRounding.AwayFromZero);
        string cell = $"{Emoji(p)}{Pct(p)}";
        return q is >= 30 or <= -30 ? $"**{cell}**" : cell;
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

    /// <summary>时延数值自带单位：µs（≥1 一位小数，&lt;1 三位小数）或 ms（≥1000 换算两位小数）。</summary>
    private static string FmtUs(double? medianUs)
    {
        if (medianUs is null or <= 0) return "—";
        double us = medianUs.Value;
        // s 档（2026-11-02 列宽优化）：极慢格原本 4012.41 ms 占 10 字符，换算 4.01 s 只占 6。
        if (us >= 1_000_000) return (us / 1_000_000).ToString("F2", CultureInfo.InvariantCulture) + " s";
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

    /// <summary>表格结构自检，两条判据（2026-10-05 质量系统优化轮定稿）：
    /// ① 列数不变量：分隔行确定该表列数，其后表格行必须同列数；② 行首缺管道符：
    /// 非表格行但含 ≥2 个管道符 = 数据行断裂形态。告警追加到输出尾部 + stderr。</summary>
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

    private static void OnTableLine(List<string> warnings, string[] lines, int i, string line, TableScanState state)
    {
        int columns = line.Count('|') - 1;
        if (line.StartsWith("|---", StringComparison.Ordinal))
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

    /// <summary>数值哨兵（B148 配套，适配 8+1 形态）：分配表里任一段内所有非基准格都是 ⚪+0%
    /// 即报警。分配逐位相同的概率可忽略，全 0 只可能是算式坏了（截断/漏算/取错字段）。</summary>
    private static List<string> NumericSentinelWarnings(string md)
    {
        var warnings = new List<string>();
        int deltaCells = 0, zeroDeltas = 0;
        foreach (string raw in md.Split('\n'))
        {
            if (!raw.StartsWith('|', StringComparison.Ordinal)
                || raw.StartsWith("|---", StringComparison.Ordinal)
                || raw.StartsWith("| 操作", StringComparison.Ordinal))
            {
                continue;
            }

            // 明细表所有 <br> 百分比段（时延百分比与内存百分比）：正常批必然有非 0 值。
            foreach (string segment in raw.Split("<br>").Skip(1).Where(static s => s.Contains('%')))
            {
                deltaCells++;
                if (segment.Contains("⚪+0%", StringComparison.Ordinal))
                {
                    zeroDeltas++;
                }
            }
        }

        if (deltaCells >= 8 && zeroDeltas == deltaCells)
        {
            warnings.Add($"[TableGen] 数值哨兵：{deltaCells} 个相对 ADO 百分比全为 0%——"
                + "各组与基准逐位相同的概率可忽略，疑似比值算式坏了（B148：long/long 整数除法截断）");
        }

        return warnings;
    }

    private static string Truncate(string line)
        => line.Length <= 60 ? line : line[..60];
}
