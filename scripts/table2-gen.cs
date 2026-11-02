// table2-gen.cs（2026-11-02）：性能表-表二生成器——从 CrossBench 完整批生成 README 性能模块的
// 操作对照与事务对照（按方言分表，13 列，每格四行：时延 / 时延倍数 / 内存 / 内存倍数）。
//
// 用法：dotnet run --file scripts/table2-gen.cs -- <crossbench-batch.json> [输出路径，默认 stdout]
//
// 规则对齐 docs/README性能表模板.md v1.3：着色只落 PalORM 列（GitHub MathJax \color）；基准格写纯
// 1.00；🏆 = 时延最快组、💾 = 内存最省组（均含 ADO 基准）；≥2.0 或 ≤0.5 的极端值加粗。
// 列序为模板冻结序（AGENTS.md 列固定条）：基准 → PalORM → Dapper.AOT → RepoDb → EF Core →
// Dapper → FreeSql → linq2db → SqlSugar。批次里缺失的组整列省略。
//
// 为什么是 C#：项目脚本语言政策（docs/编码规范.md §18-19，2026-09-29 用户决策）要求脚本一律 C#，
// 白名单外的 .mjs/.ps1 会被 Quality Gates 判红。
using System.Globalization;
using System.Text;
using System.Text.Json;

const string Usage = "用法: dotnet run --file scripts/table2-gen.cs -- <crossbench-batch.json> [out.md]";
if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 1;
}

string batchPath = args[0];
string? outPath = args.Length > 1 ? args[1] : null;
if (!File.Exists(batchPath))
{
    Console.Error.WriteLine($"批次文件不存在: {batchPath}");
    return 1;
}

// ── 装载并按 (方言, 组, 操作, 档) 聚合两遍 Average（与 CrossBenchBatch 同口径）──
var agg = new Dictionary<(string D, string Arm, string Op, int Rows), (double Med, double Alloc)>();
using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(batchPath)))
{
    var sums = new Dictionary<(string, string, string, int), (double Med, double Alloc, int N)>();
    foreach (JsonElement m in doc.RootElement.EnumerateArray())
    {
        var key = (
            m.GetProperty("Dialect").GetString() ?? "",
            m.GetProperty("Implementation").GetString() ?? "",
            m.GetProperty("Operation").GetString() ?? "",
            m.GetProperty("Rows").GetInt32());
        double med = m.GetProperty("MedianNs").GetDouble();
        double alloc = m.GetProperty("AllocatedBytesPerOp").GetDouble();
        sums.TryGetValue(key, out var acc);
        sums[key] = (acc.Med + med, acc.Alloc + alloc, acc.N + 1);
    }

    foreach ((var k, var v) in sums)
    {
        agg[k] = (v.Med / v.N, v.Alloc / v.N);
    }
}

// 列序：模板 v1.3 冻结序。显示名映射只改表头，键仍是批次里的组名。
string[] arms =
[
    "ADO_NET", "PalORM", "DapperAot", "RepoDb", "EfCore", "Dapper", "FreeSql", "Linq2Db", "SqlSugar",
];
var display = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["ADO_NET"] = "ADO",
    ["DapperAot"] = "Dapper.AOT",
    ["EfCore"] = "EF Core",
    ["Linq2Db"] = "linq2db",
};

// 操作分组：crud/bulk/extra 走两档，tx 仅最小档（与 PerfHub RunsAtTier 同语义）
(string Op, string Group)[] ops =
[
    ("GetByKey", "crud"), ("GetAllAsync", "crud"), ("QueryAll", "crud"), ("Insert", "crud"), ("Update", "crud"),
    ("BulkInsert", "bulk"), ("BulkUpdate", "bulk"), ("BulkDelete", "bulk"), ("UpsertBatch", "bulk"),
    ("KeysetPage", "extra"), ("WhereIn", "extra"), ("Count", "extra"),
    ("TxSingleInsert", "tx"), ("TxHundredInserts", "tx"), ("TxBulkInsert", "tx"), ("TxRollback", "tx"),
    ("EnumInserts", "tx"),
];
int[] tiers = [2000, 20000];

bool TryGet(string dialect, string op, int tier, string arm, out (double Med, double Alloc) cell)
    => agg.TryGetValue((dialect, arm, op, tier), out cell);

static string FmtTime(double ns)
{
    double us = ns / 1000;
    if (us >= 1_000_000) return (us / 1_000_000).ToString("F2", CultureInfo.InvariantCulture) + "&nbsp;s";
    if (us >= 1000) return (us / 1000).ToString("F2", CultureInfo.InvariantCulture) + "&nbsp;ms";
    if (us >= 1) return us.ToString("F1", CultureInfo.InvariantCulture) + "&nbsp;µs";
    return us.ToString("F3", CultureInfo.InvariantCulture) + "&nbsp;µs";
}

static string FmtAlloc(double bytes)
{
    double kb = bytes / 1024;
    if (kb >= 1_048_576) return (kb / 1024).ToString("F1", CultureInfo.InvariantCulture) + "MB";
    if (kb >= 100) return Math.Round(kb).ToString(CultureInfo.InvariantCulture) + "KB";
    if (kb >= 1) return kb.ToString("F1", CultureInfo.InvariantCulture) + "KB";
    return bytes.ToString("F0", CultureInfo.InvariantCulture) + "B";
}

// 六档色值（模板 v1.3）；1.00 用中性灰，|v-1|<0.005 视作持平
static string Color(double v)
{
    if (v <= 0.70) return "#16a34a";
    if (v <= 0.90) return "#65a30d";
    if (v <= 0.99) return "#2563eb";
    if (Math.Abs(v - 1) < 0.005) return "#6b7280";
    if (v <= 1.09) return "#d97706";
    if (v <= 1.29) return "#ea580c";
    return "#dc2626";
}

// 倍数着色：基准格恒 1.00；PalORM 列走 MathJax \color；其余组只对极端值（≥2.0 / ≤0.5）加粗
static string Paint(string arm, double v)
{
    string s = v.ToString("F2", CultureInfo.InvariantCulture);
    if (arm == "ADO_NET") return "1.00";
    bool bold = v >= 2.0 || v <= 0.5;
    if (arm == "PalORM") return $"$\\color{{{Color(v)}}}{{{s}}}$";
    return bold ? $"**{s}**" : s;
}

var output = new StringBuilder(1 << 16);
_ = output.AppendLine(
    $"> 数据源：`{batchPath.Replace('\\', '/')}`（9 组 × 三方言 × 两档，两遍 Average，比值分母 = 同批 ADO 基准）。");
_ = output.AppendLine();
_ = output.AppendLine("### 操作对照（时延 [倍数] 与内存 [相对基准]，颜色同百分比六档）");
_ = output.AppendLine();
_ = output.AppendLine("> 🏆 = 时延最快组 · 💾 = 内存最省组（均含 ADO 基准）");

foreach (string dialect in new[] { "SQLite", "MySQL", "PostgreSQL" })
{
    _ = output.AppendLine();
    _ = output.AppendLine($"#### {dialect}（2000 与 20000 档）");
    _ = output.AppendLine();
    AppendTable(output, dialect, [.. ops.Where(static o => o.Group != "tx")], tiers);
}

_ = output.AppendLine();
_ = output.AppendLine("### 事务对照（仅 2000 档）");
_ = output.AppendLine();
foreach (string dialect in new[] { "SQLite", "MySQL", "PostgreSQL" })
{
    _ = output.AppendLine($"#### {dialect}");
    _ = output.AppendLine();
    AppendTable(output, dialect, [.. ops.Where(static o => o.Group == "tx")], [2000]);
    _ = output.AppendLine();
}

if (outPath is null)
{
    Console.WriteLine(output.ToString());
}
else
{
    File.WriteAllText(outPath, output.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"written: {outPath}");
}

return 0;

void AppendTable(StringBuilder sb, string dialect, (string Op, string Group)[] tableOps, int[] tableTiers)
{
    // 本表实际有数据的组（批次里缺某组时整列省略，不留空列）
    string[] present = [.. arms.Where(a => TryGet(dialect, tableOps[0].Op, tableTiers[0], a, out _))];
    string Header(string key) => display.TryGetValue(key, out string? shown) ? shown : key;
    _ = sb.AppendLine($"| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | {string.Join(" | ", present.Select(Header))} |");
    _ = sb.AppendLine($"|---|---:|---|{string.Concat(Enumerable.Repeat("---:|", present.Length))}");

    foreach ((string op, _) in tableOps)
    {
        foreach (int tier in tableTiers)
        {
            if (!TryGet(dialect, op, tier, "ADO_NET", out var baseCell))
            {
                continue;   // 该档没有基准格 → 整行缺测，不输出半行
            }

            string bestTimeArm = "", bestAllocArm = "";
            double bestTime = double.MaxValue, bestAlloc = double.MaxValue;
            foreach (string arm in present)
            {
                if (!TryGet(dialect, op, tier, arm, out var c)) continue;
                if (c.Med < bestTime) { bestTime = c.Med; bestTimeArm = arm; }
                if (c.Alloc < bestAlloc) { bestAlloc = c.Alloc; bestAllocArm = arm; }
            }

            string mark = bestTimeArm.Length == 0
                ? ""
                : $"🏆&nbsp;{Header(bestTimeArm)}"
                    + (bestAllocArm.Length == 0 ? "" : $"<br>💾&nbsp;{Header(bestAllocArm)}");

            var cells = new List<string>(present.Length);
            foreach (string arm in present)
            {
                if (!TryGet(dialect, op, tier, arm, out var c))
                {
                    cells.Add("—");
                    continue;
                }

                string tag = (arm == bestTimeArm ? "🏆&nbsp;" : "") + (arm == bestAllocArm ? "💾&nbsp;" : "");
                cells.Add(
                    $"{tag}{FmtTime(c.Med)}<br>{Paint(arm, c.Med / baseCell.Med)}"
                    + $"<br>{FmtAlloc(c.Alloc)}<br>{Paint(arm, c.Alloc / baseCell.Alloc)}");
            }

            _ = sb.AppendLine(
                $"| {op} | {tier.ToString("N0", CultureInfo.InvariantCulture)} | {mark} | {string.Join(" | ", cells)} |");
        }
    }
}
