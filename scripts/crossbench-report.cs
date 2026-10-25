// 交叉基准报告与基线生成器（bench/crossbench 的配套工具，脚本白名单制：文件式 C#）
//
// 用法：
//   dotnet run --file scripts/crossbench-report.cs -- <batch.json> [--out report.md]
//     单批评级报告（四臂四组表，列/色标/📌 规则与 PerfHub 报告一致，Dapper 列换 FreeSql 的四臂版）
//   dotnet run --file scripts/crossbench-report.cs -- --baseline <b1.json,b2.json,b3.json> [--out baseline.md]
//     多批基线（B100 纪律）：每格取各批中位的中位数；地板批间散布 >20% 的格标 ⚠地板漂移；
//     样本数不足 3 的格（形态断点）标 ⚠n=k 并注明。
//
// 输入 JSON 是 Runner 的 Measurement[] 序列（每批每格两遍，先按批内均值聚合成批中位）。
using System.Text.Json;

if (args.Length < 1)
{
    Console.Error.WriteLine("用法：crossbench-report.cs -- <batch.json> [--out x.md] | --baseline <b1,b2,b3> [--out x.md]");
    return 2;
}

bool baselineMode = args[0] == "--baseline";
string[] files = (baselineMode ? args[1] : args[0]).Split(',', StringSplitOptions.TrimEntries);
string? outFile = ParseOption(args, "--out");
if (files.Length == 0 || files.Any(f => !File.Exists(f)))
{
    Console.Error.WriteLine("FAIL：输入 JSON 不存在");
    return 2;
}

var batches = files.Select(f => JsonSerializer.Deserialize<List<Measurement>>(File.ReadAllText(f))!).ToList();

// 批内聚合：每格两遍 → 批中位（均值）；再跨批取中位数
Dictionary<string, List<double>> timeByCell = new();
Dictionary<string, List<double>> allocByCell = new();
foreach (List<Measurement> batch in batches)
{
    foreach (var g in batch.GroupBy(m => (m.Dialect, m.Operation, m.Rows, m.Implementation)))
    {
        string key = Key(g.Key.Dialect, g.Key.Operation, g.Key.Rows, g.Key.Implementation);
        double time = g.Average(m => m.MedianNs);
        double alloc = g.Average(m => m.AllocatedBytesPerOp);
        (timeByCell.TryGetValue(key, out var t) ? t : timeByCell[key] = new()).Add(time);
        (allocByCell.TryGetValue(key, out var a) ? a : allocByCell[key] = new()).Add(alloc);
    }
}

double CellTime(string d, string o, int r, string i) =>
    Median(timeByCell[Key(d, o, r, i)]);
double CellAlloc(string d, string o, int r, string i) =>
    Median(allocByCell[Key(d, o, r, i)]);
bool HasCell(string d, string o, int r, string i) =>
    timeByCell.ContainsKey(Key(d, o, r, i));
int CellN(string d, string o, int r, string i) =>
    timeByCell.TryGetValue(Key(d, o, r, i), out var v) ? v.Count : 0;

var sw = new StringWriter();
sw.WriteLine($"# 交叉基准{(baselineMode ? $"基线（{batches.Count} 批窗口中位）" : "单批报告")}");
sw.WriteLine();
sw.WriteLine($"> 数据源：{string.Join(" · ", files.Select(Path.GetFileName))}");
sw.WriteLine("> 四臂：ADO.NET（地板）/ Dapper / FreeSql / PalORM，同批同数据同量具（两遍反序中位）。");
sw.WriteLine("> 口径与形态注记见 bench/crossbench/README.md 与 docs/adr/ADR-Q。");
sw.WriteLine();

string[] dialects = ["SQLite", "MySQL", "PostgreSQL"];
int[] tiers = [2000, 20000];
string[] arms = ["ADO_NET", "Dapper", "FreeSql", "PalORM"];

(string[] Ops, string Title, bool MinTierOnly)[] groups =
[
    (["GetByKey", "GetAllAsync", "QueryAll", "Insert", "Update"], "表一：CRUD 单行与读（三方言，两档）", false),
    (["BulkInsert", "BulkUpdate", "BulkDelete", "UpsertBatch"], "表二：批量处理（三方言，两档）", false),
    (["TxSingleInsert", "TxHundredInserts", "TxBulkInsert", "TxRollback", "EnumInserts"], "表三：事务（三方言，仅最小档）", true),
];

sw.WriteLine("| 操作 | 方言 | 档 | ADO.NET | Dapper | FreeSql | PalORM | P/ADO | P/Dapper | P/FreeSql | 分配 A/D/F/P | n |");
sw.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|");
foreach (var g in groups)
{
    sw.WriteLine();
    sw.WriteLine($"**{g.Title}**");
    foreach (string d in dialects)
    foreach (int r in tiers)
    {
        if (g.MinTierOnly && r != 2000) continue;
        foreach (string op in g.Ops)
        {
            if (!arms.All(a => HasCell(d, op, r, a))) continue;
            double p = CellTime(d, op, r, "PalORM");
            double pAdo = p / CellTime(d, op, r, "ADO_NET");
            double pDap = p / CellTime(d, op, r, "Dapper");
            double pFs = p / CellTime(d, op, r, "FreeSql");
            string pin = pAdo is >= 1.5 or <= 0.67 ? "📌" : "";
            string n = baselineMode ? CellN(d, op, r, "PalORM").ToString() : "—";
            string floorFlag = "";
            if (baselineMode)
            {
                var f = timeByCell[Key(d, op, r, "ADO_NET")];
                if (f.Count >= 2 && (f.Max() / f.Min()) > 1.2) floorFlag = " ⚠地板漂移";
            }

            sw.WriteLine($"| {pin}{op}{floorFlag} | {d} | {r} "
                + string.Concat(arms.Select(a => $"| {Fmt(CellTime(d, op, r, a))} "))
                + $"| {Ratio(pAdo)} | {Ratio(pDap)} | {Ratio(pFs)} "
                + $"| {string.Join("/", arms.Select(a => Bytes(CellAlloc(d, op, r, a))))} | {n} |");
        }
    }
}

sw.WriteLine();
sw.WriteLine("**表四：跨方言 PalORM/ADO 比值（批量族 + GetAllAsync；裸比值，越线 📌）**");
sw.WriteLine();
sw.WriteLine("| 操作 | 档 | SQLite | PostgreSQL | MySQL |");
sw.WriteLine("|---|---:|---:|---:|---:|");
foreach (string op in new[] { "BulkInsert", "BulkUpdate", "BulkDelete", "UpsertBatch", "GetAllAsync" })
foreach (int r in tiers)
{
    string Cell(string d) => HasCell(d, op, r, "ADO_NET") && HasCell(d, op, r, "PalORM")
        ? (CellTime(d, op, r, "PalORM") / CellTime(d, op, r, "ADO_NET")) is double v && (v >= 1.5 || v <= 0.67) ? $"📌{v:F2}" : $"{v:F2}"
        : "—";
    sw.WriteLine($"| {op} | {r} | {Cell("SQLite")} | {Cell("PostgreSQL")} | {Cell("MySQL")} |");
}

string text = sw.ToString();
if (outFile is null) Console.Write(text);
else { File.WriteAllText(outFile, text); Console.WriteLine($"WROTE {outFile}"); }
return 0;

static string? ParseOption(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Key(string d, string o, int r, string i) => $"{d}|{o}|{r}|{i}";

static double Median(List<double> v)
{
    var s = v.OrderBy(x => x).ToList();
    return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
}

static string Fmt(double ns)
{
    double us = ns / 1000;
    return us >= 1000 ? $"{us / 1000:F2} ms" : us >= 1 ? $"{us:F1} µs" : $"{us:F3} µs";
}

static string Bytes(double b)
{
    if (b < 1024) return $"{b:F0}B";
    double kb = b / 1024;
    return kb < 100 ? $"{kb:F1}KB" : kb < 1024 ? $"{kb:F0}KB" : $"{kb / 1024:F1}MB";
}

static string Ratio(double v)
{
    int p = (int)Math.Round((v - 1) * 100);
    if (Math.Abs(p) < 1) p = 0;
    string color = p <= -30 ? "🟢" : p <= -10 ? "🟩" : p <= -1 ? "🔹" : p == 0 ? "⚪"
        : p <= 9 ? "🔸" : p <= 29 ? "🟧" : "🟥";
    string cell = $"{v:F2} {color}{(p >= 0 ? "+" : "")}{p}%";
    return v >= 1.3 || v <= 0.7 ? $"**{cell}**" : cell;
}

// Runner Measurement 的最小镜像（字段名与 bench/PalORM.PerfHub/Measure.cs 一致）
sealed class Measurement
{
    public string Dialect { get; set; } = "";
    public string Implementation { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Group { get; set; } = "";
    public int Rows { get; set; }
    public double MedianNs { get; set; }
    public double MeanNs { get; set; }
    public double ErrorRatio { get; set; }
    public int Iterations { get; set; }
    public double AllocatedBytesPerOp { get; set; }
    public long PeakHeapBytes { get; set; }
    public long LiveHeapAfterBytes { get; set; }
    public int Gen0Collections { get; set; }
    public double RoundTripsPerOp { get; set; }
    public double PreparedReuse { get; set; }
}
