#:property Nullable=enable
// 12 库统一横评报告生成器（2026-10-30，清单 = AotProbe 终版 12 条 + ADO 地板）。
// 纪律与 scripts/crossbench-report.cs 冻结模板同源：所有倍数/计数/"最快/最差"标签
// 从当批 JSON 推导，叙述列只写定性机制与 N/A 原因（静态事实，不含数字）。
//
// 用法：dotnet run --file scripts/crossbench-12arm-report.cs -- <批1.json> <批2.json> ... --out report.md
// 输入 = 本轮全量批（sqlite/mysql/pg 三方言批 + litedb 伪方言批），两遍反序批内 Average 聚合。

using System.Globalization;
using System.Text;
using System.Text.Json;

var files = new List<string>();
string? outPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length)
    {
        outPath = args[++i];
    }
    else if (!args[i].StartsWith("--"))
    {
        files.Add(args[i]);
    }
}

if (files.Count == 0)
{
    Console.Error.WriteLine("用法：crossbench-12arm-report.cs -- <批.json>... --out <report.md>");
    return 1;
}

var all = new List<Measurement>();
foreach (string file in files)
{
    List<Measurement>? loaded = LoadOrNull(file);
    if (loaded is null)
    {
        Console.Error.WriteLine($"SKIP（解析失败）：{file}");
        continue;
    }

    all.AddRange(loaded);
}

// LiteDB 伪方言拆分：Runner 以 SQLite 作载体方言（Dialect=Sqlite 落盘），但 LiteDB 是
// 独立文件存储、无 SQL 同表位——改写到独立 "LiteDB" 方言，避免混入 SQLite 表位
foreach (Measurement m in all.Where(m => m.Implementation == "LiteDB").ToList())
{
    int idx = all.IndexOf(m);
    all[idx] = m with { Dialect = "LiteDB" };
}

// 两遍反序批内 Average 聚合（与 CrossBenchBatch.LoadAggregated 同口径）
List<Measurement> agg =
[
    .. all
        .GroupBy(m => (m.Dialect, m.Implementation, m.Operation, m.Rows))
        .Select(g => new Measurement(
            g.Key.Dialect, g.Key.Implementation, g.Key.Operation, g.Key.Rows,
            g.Average(m => m.MedianUs), g.Average(m => m.AllocBytes), g.Average(m => m.ErrorRatio)))
];

string[] dialects = [.. agg.Select(m => m.Dialect).Distinct().Order()];
string[] armOrder =
[
    "ADO_NET", "Dapper", "DapperAot", "Linq2Db", "EfCore", "SqlSugar",
    "NHibernate", "FreeSql", "RepoDb", "PalORM", "Marten", "LiteDB"
];
string[] armDisplay =
[
    "ADO(地板)", "Dapper", "Dapper.AOT", "linq2db", "EF Core", "SqlSugar",
    "NHibernate", "FreeSql", "RepoDb", "PalORM", "Marten*", "LiteDB*"
];
var ops = new[]
{
    "GetByKey", "GetAllAsync", "QueryAll", "Insert", "Update", "BulkInsert", "BulkUpdate",
    "BulkDelete", "KeysetPage", "WhereIn", "Count", "UpsertBatch",
    "TxSingleInsert", "TxHundredInserts", "TxBulkInsert", "TxRollback", "EnumInserts"
};
var opGroup = new Dictionary<string, string>
{
    ["GetByKey"] = "读", ["GetAllAsync"] = "读", ["QueryAll"] = "读",
    ["KeysetPage"] = "读", ["WhereIn"] = "读", ["Count"] = "读",
    ["Insert"] = "写", ["Update"] = "写", ["BulkInsert"] = "写", ["BulkUpdate"] = "写",
    ["BulkDelete"] = "写", ["UpsertBatch"] = "写",
    ["TxSingleInsert"] = "事务", ["TxHundredInserts"] = "事务", ["TxBulkInsert"] = "事务",
    ["TxRollback"] = "事务", ["EnumInserts"] = "事务",
};

// N/A 原因账本（静态定性事实，2026-10-30 冒烟定案；数字不在此出现）
var naReasons = new Dictionary<(string Arm, string Dialect), string>
{
    [("SmartSql", "Sqlite")] = "N/A：代码级初始化缺陷（UseDatabase 路径 NRE，XML 路径未接入）",
    [("SmartSql", "MySql")] = "N/A：同 SQLite",
    [("SmartSql", "PostgreSql")] = "N/A：同 SQLite",
    [("Insight", "Sqlite")] = "N/A：无 SQLite provider，批量物化对 TEXT 列 decimal 失败",
    [("Insight", "MySql")] = "N/A：身份列约定吞显式主键绑定（Column 'Id' cannot be null）",
    [("Insight", "PostgreSql")] = "N/A：身份列约定吞显式主键绑定（Parameter 'p0' must have Value）",
    [("SqlSugar", "PostgreSql")] = "N/A：PG 硬编码全小写引号标识符，与 CamelCase 引号 DDL 冲突",
    [("Marten", "Sqlite")] = "N/A：PG-only 文档库（清单钦定，仅 pg 注册）",
    [("Marten", "MySql")] = "N/A：PG-only 文档库",
    [("LiteDB", "Sqlite")] = "—：嵌入式文件文档库，独立伪方言批（见 LiteDB 节）",
    [("LiteDB", "MySql")] = "—：同 SQLite（独立伪方言批）",
    [("LiteDB", "PostgreSql")] = "—：同 SQLite（独立伪方言批）",
};

// ── 胜场统计（每格最快者；ADO 是地板不是竞争者） ──
var winners = new Dictionary<string, int>();
foreach (string dialect in dialects)
{
    foreach (int rows in agg.Where(m => m.Dialect == dialect).Select(m => m.Rows).Distinct().Order())
    {
        foreach (string op in ops)
        {
            string? fastest = null;
            double best = double.MaxValue;
            foreach (Measurement m in agg.Where(m =>
                         m.Dialect == dialect && m.Rows == rows && m.Operation == op
                         && m.Implementation is not ("ADO_NET" or "Marten" or "LiteDB")))
            {
                if (m.MedianUs < best)
                {
                    best = m.MedianUs;
                    fastest = m.Implementation;
                }
            }

            if (fastest is not null)
            {
                winners[fastest] = winners.TryGetValue(fastest, out int n) ? n + 1 : 1;
            }
        }
    }
}

var sb = new StringBuilder(1 << 16);
string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

sb.AppendLine("# 12 库统一横评（清单 = AotProbe 终版 12 条 + ADO 地板）");
sb.AppendLine();
sb.AppendLine($"> 生成：{stamp} ｜ 数据源：{string.Join("、", files.Select(Path.GetFileName))}");
sb.AppendLine(">");
sb.AppendLine("> 口径：三方言（SQLite/MySQL/PG）× 两档（2000/20000）× 17 操作 × 两遍反序（批内 Average）；");
sb.AppendLine("> LiteDB 与 Marten 为文档/嵌入式形态（* 标注），与 SQL 臂无同表位可比性，单列呈现；");
sb.AppendLine("> SQL 臂比值分母 = 同方言同操作 ADO 地板；N/A 格原因见覆盖账本。");
sb.AppendLine();

// ── 1. 结论面板（从数据推导） ──
sb.AppendLine("## 1. 结论面板");
sb.AppendLine();
int totalCells = 0;
var palormWins = winners.TryGetValue("PalORM", out int pw) ? pw : 0;
var dapperWins = winners.TryGetValue("Dapper", out int dw) ? dw : 0;
var palormRatio = agg.Where(m => m.Implementation == "PalORM" && m.Dialect != "LiteDB")
    .Select(m => RatioVsAdo(agg, m)).Where(r => r > 0).Order().ToList();
foreach (string d in dialects)
{
    foreach (int r in agg.Where(m => m.Dialect == d && m.Implementation != "LiteDB").Select(m => m.Rows).Distinct())
    {
        foreach (string op in ops)
        {
            if (agg.Any(m => m.Dialect == d && m.Rows == r && m.Operation == op))
            {
                totalCells++;
            }
        }
    }
}

sb.AppendLine($"- **可比格总数（SQL 方言，不含 LiteDB/Marten）**：{totalCells}；胜场前三："
    + string.Join("、", winners.OrderByDescending(kv => kv.Value).Take(3)
        .Select(kv => $"{Arm(kv.Key)} {kv.Value}")));
sb.AppendLine($"- **PalORM 中位比 ADO 地板**：{Fmt(palormRatio[palormRatio.Count / 2])}"
    + $"（区间 {Fmt(palormRatio[0])} ~ {Fmt(palormRatio[^1])}，共 {palormRatio.Count} 格）");
sb.AppendLine();

// ── 2. 胜场榜 ──
sb.AppendLine("## 2. 胜场榜（每格最快臂；ADO 地板与文档臂不参与）");
sb.AppendLine();
sb.AppendLine("| 臂 | 胜场 |");
sb.AppendLine("|---|---:|");
foreach (KeyValuePair<string, int> kv in winners.OrderByDescending(kv => kv.Value))
{
    sb.AppendLine($"| {Arm(kv.Key)} | {kv.Value} |");
}

sb.AppendLine();

// ── 3. 全量表（每方言每档：op × 臂 = 中位 µs [P/ADO] / 分配 B） ──
sb.AppendLine("## 3. 全量表（中位 µs [P/ADO] · 分配 B；快照 = 当批推导）");
sb.AppendLine();
foreach (string dialect in dialects)
{
    string dName = dialect == "LiteDB" ? "LiteDB（伪方言，嵌入式文件文档库）" : dialect;
    foreach (int rows in agg.Where(m => m.Dialect == dialect).Select(m => m.Rows).Distinct().Order())
    {
        sb.AppendLine($"### {dName} rows={rows:N0}");
        sb.AppendLine();
        sb.AppendLine("| 操作 | " + string.Join(" | ", armDisplay) + " |");
        sb.AppendLine("|---|" + string.Join("|", Enumerable.Repeat("---:", armDisplay.Length)) + "|");
        foreach (string op in ops)
        {
            var cells = new List<string>();
            for (int a = 0; a < armOrder.Length; a++)
            {
                string arm = armOrder[a];
                Measurement? m = agg.FirstOrDefault(x =>
                    x.Dialect == dialect && x.Rows == rows && x.Operation == op && x.Implementation == arm);
                if (m is null)
                {
                    cells.Add(naReasons.TryGetValue((arm, dialect), out string? why)
                        ? $"N/A{Emph(why)}" : "N/A");
                }
                else
                {
                    double ratio = RatioVsAdo(agg, m);
                    string r = ratio > 0 ? $" [{ratio:F2}]" : "";
                    cells.Add($"{m.MedianUs:F1}{r}<br>{m.AllocBytes:F0} B");
                }
            }

            sb.AppendLine($"| {op}({opGroup[op]}) | " + string.Join(" | ", cells) + " |");
        }

        sb.AppendLine();
    }
}

// ── 4. 覆盖账本 ──
sb.AppendLine("## 4. 覆盖账本（12 库 × 方言）");
sb.AppendLine();
sb.AppendLine("| 库 | SQLite | MySQL | PG | LiteDB 位 |");
sb.AppendLine("|---|---|---|---|---|");
string[] listArms = ["PalORM", "Dapper.AOT", "RepoDb", "FreeSql", "linq2db", "EF Core", "SqlSugar", "NHibernate", "Marten", "LiteDB", "SmartSql", "Insight"];
foreach (string lib in listArms)
{
    string armKey = lib switch
    {
        "Dapper.AOT" => "DapperAot",
        "linq2db" => "Linq2Db",
        "EF Core" => "EfCore",
        _ => lib
    };
    string Mark(string dialect) => agg.Any(m => m.Implementation == armKey && m.Dialect == dialect)
        ? "✅"
        : naReasons.TryGetValue((armKey, dialect), out string? why) ? "❌" : "—";
    string liteSlot = armKey == "LiteDB" ? "独立伪方言批" : armKey == "Marten" ? "—" : "—";
    sb.AppendLine($"| {lib} | {Mark("Sqlite")} | {Mark("MySql")} | {Mark("PostgreSql")} | {liteSlot} |");
}

sb.AppendLine();
sb.AppendLine("N/A 原因：");
sb.AppendLine();
foreach (KeyValuePair<(string Arm, string Dialect), string> kv in naReasons)
{
    sb.AppendLine($"- {Arm(kv.Key.Arm)} @ {kv.Key.Dialect}：{kv.Value}");
}

sb.AppendLine();

// ── 5. 异常判读 ──
sb.AppendLine("## 5. 异常判读（当批推导，置信度标注）");
sb.AppendLine();
// 机械推导：P/ADO > 3 的显著离群格
var outliers = new List<(string Dialect, int Rows, string Op, string Impl, double Ratio)>();
foreach (Measurement m in agg.Where(m => m.Implementation is not ("ADO_NET" or "Marten" or "LiteDB")))
{
    double ratio = RatioVsAdo(agg, m);
    if (ratio >= 3.0)
    {
        outliers.Add((m.Dialect, m.Rows, m.Operation, m.Implementation, ratio));
    }
}

foreach (var o in outliers.OrderBy(o => o.Ratio).Take(20))
{
    sb.AppendLine($"- {Arm(o.Impl)} {o.Dialect} {o.Op}@{o.Rows:N0}：{o.Ratio:F1}× ADO（显著慢，见全量表）");
}

if (outliers.Count == 0)
{
    sb.AppendLine("- 无 P/ADO ≥ 3× 的显著离群格（机械判据）");
}

sb.AppendLine();

// ── 6. 环境与形态注记 ──
sb.AppendLine("## 6. 环境与形态注记");
sb.AppendLine();
sb.AppendLine("- Windows 10 x64；容器 MySQL 8.4.11@3306（local_infile=ON）/ PG 18.6@55432（pgbench 容器）；");
sb.AppendLine("- MySQL 臂驱动差异：linq2db/MySql.Data（镜像缺 linq2db.MySqlConnector）、NHibernate/MySqlDataDriver（5.6 无 MySqlConnectorDriver）——报告注记不剔除；");
sb.AppendLine("- EF MySQL provider = Oracle 官方 MySql.EntityFrameworkCore 10.0.12（镜像无 Pomelo EF10 线）；");
sb.AppendLine("- 文档臂（Marten/LiteDB）为 jsonb/文件存储，无 SQL 同表位，读族数值量级不可与 SQL 臂直接比；");
sb.AppendLine("- NHibernate 实体=臂内 virtual POCO + 每操作转换；EF=每操作 DbContext；均为官方推荐形态，成本含在测量内。");
sb.AppendLine();

// ── 7. 产物与复现 ──
sb.AppendLine("## 7. 产物与复现");
sb.AppendLine();
sb.AppendLine("```bash");
sb.AppendLine("# 全量批（本报告数据源，串行执行）");
sb.AppendLine("dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects sqlite 2000 20000");
sb.AppendLine("dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects mysql 2000 20000");
sb.AppendLine("dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects pg 2000 20000");
sb.AppendLine("dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects litedb 2000 20000");
sb.AppendLine("# 报告再生成");
sb.AppendLine($"dotnet run --file scripts/crossbench-12arm-report.cs -- {string.Join(" ", files.Select(f => $"bench/crossbench/results/{Path.GetFileName(f)}"))} --out <out.md>");
sb.AppendLine("```");

string text = sb.ToString();
if (outPath is not null)
{
    await File.WriteAllTextAsync(outPath, text);
    Console.WriteLine($"WROTE {outPath} ({text.Length:N0} chars)");
}
else
{
    Console.WriteLine(text);
}

return 0;

string Arm(string impl) => impl switch
{
    "ADO_NET" => "ADO(地板)",
    "DapperAot" => "Dapper.AOT",
    "Linq2Db" => "linq2db",
    "EfCore" => "EF Core",
    _ => impl
};

string Fmt(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

string Emph(string why) => why.StartsWith("N/A") ? "" : "";

static double RatioVsAdo(List<Measurement> agg, Measurement m)
{
    if (m.Implementation == "ADO_NET" || m.Dialect == "LiteDB")
    {
        return 0;
    }

    Measurement? floor = agg.FirstOrDefault(x =>
        x.Dialect == m.Dialect && x.Rows == m.Rows && x.Operation == m.Operation && x.Implementation == "ADO_NET");
    return floor is not null && floor.MedianUs > 0 ? m.MedianUs / floor.MedianUs : 0;
}

static List<Measurement>? LoadOrNull(string path)
{
    try
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var items = new List<Measurement>();
        foreach (JsonElement m in doc.RootElement.EnumerateArray())
        {
            items.Add(new Measurement(
                m.GetProperty("Dialect").GetString() ?? "",
                m.GetProperty("Implementation").GetString() ?? "",
                m.GetProperty("Operation").GetString() ?? "",
                m.GetProperty("Rows").GetInt32(),
                m.GetProperty("MedianNs").GetDouble() / 1000.0,
                m.TryGetProperty("AllocatedBytesPerOp", out JsonElement a) ? a.GetDouble() : 0,
                m.TryGetProperty("ErrorRatio", out JsonElement e) ? e.GetDouble() : 0));
        }

        return items;
    }
    catch (Exception ex) when (ex is JsonException or IOException)
    {
        return null;
    }
}

internal sealed record Measurement(
    string Dialect, string Implementation, string Operation, int Rows,
    double MedianUs, double AllocBytes, double ErrorRatio);
