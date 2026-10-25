// 交叉基准报告与基线生成器（bench/crossbench 的配套工具，脚本白名单制：文件式 C#）
//
// 用法：
//   dotnet run --file scripts/crossbench-report.cs -- <batch.json> [--out report.md]
//     单批评级报告（五臂四组表：ADO/PalORM/Dapper/FreeSql/RepoDb；列序/色标/📌 规则与 PerfHub 报告一致）
//     模板已冻结（2026-10-25 用户指令）：所有倍数/计数/最值标签必须由当批数据推导，总览与说明禁止硬编码数字。
//   dotnet run --file scripts/crossbench-report.cs -- --baseline <b1.json,b2.json,b3.json> [--out baseline.md]
//     多批基线（B100 纪律）：每格取各批中位的中位数；地板批间散布 >20% 的格标 ⚠地板漂移。
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

var sw = new StringWriter();
sw.WriteLine($"# 交叉基准{(baselineMode ? $"基线（{batches.Count} 批窗口中位）" : "单批报告")}");
sw.WriteLine();
sw.WriteLine($"> 数据源：{string.Join(" · ", files.Select(Path.GetFileName))}");
sw.WriteLine("> 五臂：ADO.NET（地板）/ PalORM / Dapper / FreeSql / RepoDb，同批同数据同量具（两遍反序中位）。");
sw.WriteLine("> 读表：每格 = 时间 [对 ADO 倍数] / 内存 [对 ADO 倍数]；[1.00] = ±0.5% 持平带；≤0.67 或 ≥1.5 越线加粗；📌 = 越线待判读；⚠地板 = ADO 臂批间散布 >20%；🏆 = 该格最快臂。");
sw.WriteLine("> 口径与形态注记见 bench/crossbench/README.md 与 docs/adr/ADR-Q/R。");
sw.WriteLine();

string[] dialects = ["SQLite", "MySQL", "PostgreSQL"];
int[] tiers = [2000, 20000];
string[] arms = ["ADO_NET", "PalORM", "Dapper", "FreeSql", "RepoDb"];

(string[] Ops, string Title, bool MinTierOnly)[] groups =
[
    (["GetByKey", "GetAllAsync", "QueryAll", "Insert", "Update"], "表一：CRUD 单行与读（三方言，两档）", false),
    (["BulkInsert", "BulkUpdate", "BulkDelete", "UpsertBatch"], "表二：批量处理（三方言，两档）", false),
    (["TxSingleInsert", "TxHundredInserts", "TxBulkInsert", "TxRollback", "EnumInserts"], "表三：事务（三方言，仅最小档）", true),
];

// ── 总览榜：全格胜场统计 + PalORM 视角关键数字 ──
var allCells = new List<(string D, string O, int R)>();
foreach (string d in dialects)
foreach (int r in tiers)
    foreach (var g in groups)
    {
        if (g.MinTierOnly && r != 2000) continue;
        foreach (string o in g.Ops)
            if (arms.All(a => HasCell(d, o, r, a)))
                allCells.Add((d, o, r));
    }

var winCounts = arms.ToDictionary(a => a, a => 0);
foreach (var (d, o, r) in allCells)
    winCounts[arms.OrderBy(a => CellTime(d, o, r, a)).First()]++;

var pAdoAll = allCells
    .Select(c => CellTime(c.D, c.O, c.R, "PalORM") / CellTime(c.D, c.O, c.R, "ADO_NET"))
    .OrderBy(x => x).ToList();

// ── 总结数据提取：全部由本批数据推导；叙述标签（Qual）随数值生成，禁止硬编码倍数与"最快/最差"操作名 ──
double PAdo(string d, string o, int r) => CellTime(d, o, r, "PalORM") / CellTime(d, o, r, "ADO_NET");
double ArmT(string a, string d, string o, int r) => CellTime(d, o, r, a) / CellTime(d, o, r, "ADO_NET");
double ArmA(string a, string d, string o, int r) => CellAlloc(d, o, r, a) / CellAlloc(d, o, r, "ADO_NET");
string Qual(double r) => r switch
{
    >= 0.995 and < 1.005 => "持平",
    < 0.995 => $"快 {(1 - r) * 100:F0}%",
    _ => $"慢 {(r - 1) * 100:F0}%",
};
(string Cell, double V) ArmWorst(string a, string[] ops) => allCells
    .Where(c => ops.Contains(c.O))
    .Select(c => (Cell: $"{c.O} {c.D}", V: ArmT(a, c.D, c.O, c.R)))
    .OrderByDescending(x => x.V).First();
(string Cell, double V) PBest(string[] ops) => allCells
    .Where(c => ops.Contains(c.O))
    .Select(c => (Cell: $"{c.O} {c.D}", V: PAdo(c.D, c.O, c.R)))
    .OrderBy(x => x.V).First();

var pgDelT = PAdo("PostgreSQL", "BulkDelete", 20000);
var pgDelA = ArmA("PalORM", "PostgreSQL", "BulkDelete", 20000);
var pgUpsT = PAdo("PostgreSQL", "UpsertBatch", 20000);
var pgUpsA = ArmA("PalORM", "PostgreSQL", "UpsertBatch", 20000);
var crudGetT = PAdo("SQLite", "GetByKey", 2000);
var crudGetFs = ArmT("FreeSql", "SQLite", "GetByKey", 2000);
var fsGetMax = allCells.Where(c => c.O == "GetByKey").Max(c => ArmT("FreeSql", c.D, c.O, c.R));
var crudQueryA = ArmA("PalORM", "MySQL", "QueryAll", 20000);
var crudQueryDapA = ArmA("Dapper", "MySQL", "QueryAll", 20000);
var crudInsP = PAdo("SQLite", "Insert", 2000);
var crudInsFs = ArmT("FreeSql", "SQLite", "Insert", 2000);
var sqlInsP = PAdo("SQLite", "BulkInsert", 20000);
var sqlInsDap = ArmT("Dapper", "SQLite", "BulkInsert", 20000);
var sqlInsFs = ArmT("FreeSql", "SQLite", "BulkInsert", 20000);
var buP = allCells.Where(c => c.O == "BulkUpdate").Select(c => PAdo(c.D, c.O, c.R)).ToList();
var buDapMax = allCells.Where(c => c.O == "BulkUpdate").Max(c => ArmT("Dapper", c.D, c.O, c.R));
string[] bulkOps = groups[1].Ops;
var rdBulkWorst = ArmWorst("RepoDb", bulkOps);
var rdBulkBest = allCells
    .Where(c => bulkOps.Contains(c.O))
    .Select(c => (Cell: $"{c.O} {c.D}", V: ArmT("RepoDb", c.D, c.O, c.R)))
    .OrderBy(x => x.V).First();
var rdCrudMax = allCells
    .Where(c => groups[0].Ops.Contains(c.O))
    .Max(c => ArmT("RepoDb", c.D, c.O, c.R));
string[] txOps = groups[2].Ops;
var txP = allCells.Where(c => txOps.Contains(c.O)).Select(c => PAdo(c.D, c.O, c.R)).ToList();
var txBestCell = PBest(txOps);
var txFsWorstCell = ArmWorst("FreeSql", txOps);
var txDapWorstCell = ArmWorst("Dapper", txOps);
var enumPAlloc = ArmA("PalORM", "SQLite", "EnumInserts", 2000);
var enumFsAlloc = ArmA("FreeSql", "SQLite", "EnumInserts", 2000);

// ── 总览（组亮点 + 胜场榜） ──
sw.WriteLine("## 总览");
sw.WriteLine();
sw.WriteLine($"五臂 × 三方言 × {groups.Sum(g => g.Ops.Length)} 操作（CRUD 与批量各两档、事务仅 2000 档）= **{allCells.Count} 格**，{batches.Count} 批窗口中位。所有倍数均以 ADO.NET 手写 SQL 为基准 [1.00]。");
sw.WriteLine();

// CRUD 组突出项
sw.WriteLine("### CRUD 单行与读（基准 = ADO [1.00]）");
sw.WriteLine();
sw.WriteLine($"- 🥇 GetByKey SQLite：PalORM **{crudGetT:F2}**（{Qual(crudGetT)}），FreeSql **{crudGetFs:F2}**（{Qual(crudGetFs)}，全方言最差 {fsGetMax:F2}）");
sw.WriteLine($"- QueryAll MySQL 20000 分配：PalORM **{crudQueryA:F2}**（{Qual(crudQueryA)}），Dapper {crudQueryDapA:F2}（{Qual(crudQueryDapA)}）");
sw.WriteLine($"- Insert SQLite：PalORM {crudInsP:F2}（{Qual(crudInsP)}），FreeSql **{crudInsFs:F2}**（{Qual(crudInsFs)}）");
sw.WriteLine($"- RepoDb 单行与读：最差 {rdCrudMax:F2}（{Qual(rdCrudMax)}）——表达式物化与手写 SQL 同量级");
sw.WriteLine();

// 批量组突出项
sw.WriteLine("### 批量处理（基准 = ADO [1.00]）");
sw.WriteLine();
sw.WriteLine($"- 🥇 BulkDelete PG 20000：PalORM **{pgDelT:F2}**（{Qual(pgDelT)}），分配 **{pgDelA:F2}**（{Qual(pgDelA)}）");
sw.WriteLine($"- 🥇 UpsertBatch PG 20000：PalORM **{pgUpsT:F2}**（{Qual(pgUpsT)}），分配 **{pgUpsA:F2}**（{Qual(pgUpsA)}）");
sw.WriteLine($"- BulkInsert SQLite 20000：PalORM {sqlInsP:F2}（{Qual(sqlInsP)}），Dapper {sqlInsDap:F2}（{Qual(sqlInsDap)}），FreeSql **{sqlInsFs:F2}**（{Qual(sqlInsFs)}）");
sw.WriteLine($"- BulkUpdate 三方言两档：PalORM {buP.Min():F2}~{buP.Max():F2}，Dapper 最差 **{buDapMax:F2}**（{Qual(buDapMax)}）");
sw.WriteLine($"- RepoDb 批量族：最好 {rdBulkBest.Cell} **{rdBulkBest.V:F2}**（{Qual(rdBulkBest.V)}），最差 {rdBulkWorst.Cell} **{rdBulkWorst.V:F2}**（{Qual(rdBulkWorst.V)}）");
sw.WriteLine();

// 事务组突出项
sw.WriteLine("### 事务（基准 = ADO [1.00]）");
sw.WriteLine();
sw.WriteLine($"- PalORM 全项 {txP.Min():F2}~{txP.Max():F2}，最快 {txBestCell.Cell} **{txBestCell.V:F2}**");
sw.WriteLine($"- FreeSql 最差 {txFsWorstCell.Cell} **{txFsWorstCell.V:F2}**，Dapper 最差 {txDapWorstCell.Cell} **{txDapWorstCell.V:F2}**");
sw.WriteLine($"- EnumInserts SQLite 分配：PalORM {enumPAlloc:F2}（{Qual(enumPAlloc)}），FreeSql **{enumFsAlloc:F2}**（{Qual(enumFsAlloc)}）");
sw.WriteLine();

sw.WriteLine("### 最快胜场");
sw.WriteLine();
sw.WriteLine($"| 臂 | 最快 / {allCells.Count} 格 |");
sw.WriteLine("|---|---:|");
foreach (string a in arms.OrderByDescending(a => winCounts[a]))
    sw.WriteLine($"| {a switch { "ADO_NET" => "ADO.NET（地板）", _ => a }} | **{winCounts[a]}**（{winCounts[a] * 100.0 / allCells.Count:F0}%） |");
sw.WriteLine();
sw.WriteLine($"> P/ADO 中位 **{pAdoAll[pAdoAll.Count / 2]:F2}**（1.00 = 贴平手写 SQL 地板），全距 {pAdoAll[0]:F2}~{pAdoAll[^1]:F2}。");
sw.WriteLine();

// ── 操作说明与总评：总评只写定性机制，不嵌倍数——嵌入的数字会随数据漂移变成假数据 ──
var opDescriptions = new Dictionary<string, (string Desc, string Verdict)>
{
    ["GetByKey"] = ("主键单行点查", "PalORM 贴地板（SQLite 会话税被 SQL 常量缓存抵消）；FreeSql 全方言最慢（表达式每次求值）"),
    ["GetAllAsync"] = ("全表直查 API（PalORM 专用 GetAllAsync，其余同 QueryAll）", "联网方言各臂接近；SQLite Dapper（反射物化）与 FreeSql（表达式管线）明显偏慢"),
    ["QueryAll"] = ("全表物化查询（From<T>().ToListAsync / Select.ToList）", "同 GetAllAsync：PalORM ≈ ADO 地板；SQLite 两竞品明显偏慢"),
    ["Insert"] = ("单行插入（自增主键）", "联网方言 fsync 主导各臂持平；SQLite PalORM 持平、FreeSql 明显偏慢（表达式管线）"),
    ["Update"] = ("单行整行更新", "联网方言 fsync 主导持平；FreeSql 在 SQLite 与 MySQL 明显偏慢（表达式管线）"),
    ["BulkInsert"] = ("批量插入（SQLite 行预编译 / PG COPY / MySQL BulkCopy）", "PalORM 与 ADO 同形态贴平；RepoDb 走官方 BulkOperations（COPY/LOAD DATA 经 pseudo 表）接近地板；Dapper/FreeSql 批量路径全方言偏慢"),
    ["BulkUpdate"] = ("批量更新（SQLite 逐条裹事务 / PG UPDATE FROM VALUES / MySQL CASE WHEN）", "PalORM 三方言与 ADO 同形态贴平；RepoDb 在 MySQL 用 BulkOperations 的 pseudo 表、PG/SQLite 用 Core UpdateAll 逐行，两条路径都明显偏慢；Dapper/FreeSql 无集合式封装"),
    ["BulkDelete"] = ("批量删除（SQLite json_each 单参数 / PG = ANY 数组 / MySQL IN 分批）", "PG 数组形态快于 ADO 的 IN 占位符；SQLite json_each 与 MySQL 分批接近地板；RepoDb 的 DeleteAll 按主键逐个构造 QueryGroup，分配与时间都显著偏高"),
    ["UpsertBatch"] = ("批量 UPSERT（ON CONFLICT / ON DUPLICATE KEY）", "PG 数组形态快于 ADO 与其余臂；SQLite 各臂同形态贴平；RepoDb 在 MySQL 用 BulkMerge（pseudo 表）、PG 因 BulkMerge 上游缺陷退回 MergeAll；Dapper/FreeSql 偏慢"),
    ["TxSingleInsert"] = ("事务内单行插入", "各臂持平（事务开销主导）"),
    ["TxHundredInserts"] = ("事务内 100 行逐条插入", "联网方言接近；FreeSql SQLite 明显偏慢（逐行表达式开销放大）"),
    ["TxBulkInsert"] = ("事务内批量插入", "PalORM 贴地板；RepoDb 在 MySQL/PG 走 BulkOperations（事务内仍建 pseudo 表）接近地板；Dapper（multi-exec 无语句缓存复用）与 FreeSql 明显偏慢"),
    ["TxRollback"] = ("事务回滚（更新后显式 Rollback）", "联网方言接近；SQLite FreeSql 明显偏慢"),
    ["EnumInserts"] = ("枚举列事务内逐条插入（Status TEXT 列）", "PalORM 贴平（SQLite 略高为每调用会话契约成本，已探针定案）；RepoDb 走 PropertyHandler 显式 enum↔string；FreeSql 全方言明显偏慢，Dapper SQLite 偏慢"),
};

foreach (var g in groups)
{
    var groupCells = allCells.Where(c => g.Ops.Contains(c.O)).ToList();
    var gw = arms.ToDictionary(a => a, a => groupCells.Count(c => arms.OrderBy(x => CellTime(c.D, c.O, c.R, x)).First() == a));
    sw.WriteLine($"## {g.Title}");
    sw.WriteLine();
    var winLine = string.Join(" · ", arms.OrderByDescending(a => gw[a]).Select(a => ArmShort(a) + " " + gw[a]));
    sw.WriteLine("> 胜场：" + winLine);
    sw.WriteLine();

    // 测试项说明表
    sw.WriteLine("### 测试项说明");
    sw.WriteLine();
    sw.WriteLine("| 操作 | 测什么 | 总评 |");
    sw.WriteLine("|---|---|---|");
    foreach (string op in g.Ops)
    {
        if (opDescriptions.TryGetValue(op, out var info))
            sw.WriteLine($"| {op} | {info.Desc} | {info.Verdict} |");
    }
    sw.WriteLine();

    foreach (string d in dialects)
    {
        var dialectCells = new List<(int R, string O)>();
        foreach (int r in tiers)
        {
            if (g.MinTierOnly && r != 2000) continue;
            foreach (string o in g.Ops)
                if (arms.All(a => HasCell(d, o, r, a)))
                    dialectCells.Add((r, o));
        }
        if (dialectCells.Count == 0) continue;

        sw.WriteLine($"**{d}**");
        sw.WriteLine();
        // 格式：时间 [对 ADO 倍数] / 内存 [对 ADO 倍数]，如 4.5ms [1.87] / 4.5MB [0.55]
        sw.WriteLine("| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql | RepoDb |");
        sw.WriteLine("|---|---:|:-:|---|---|---|---|---|");
        foreach (int r in tiers)
        {
            if (g.MinTierOnly && r != 2000) continue;
            foreach (string op in g.Ops)
            {
                if (!arms.All(a => HasCell(d, op, r, a))) continue;
                double adoTime = CellTime(d, op, r, "ADO_NET");
                double adoAlloc = CellAlloc(d, op, r, "ADO_NET");
                string fastest = arms.OrderBy(a => CellTime(d, op, r, a)).First();
                string pin = CellTime(d, op, r, "PalORM") / adoTime is >= 1.5 or <= 0.67 ? "📌" : "";
                string floorFlag = "";
                if (baselineMode)
                {
                    var f = timeByCell[Key(d, op, r, "ADO_NET")];
                    if (f.Count >= 2 && (f.Max() / f.Min()) > 1.2) floorFlag = " ⚠地板";
                }

                string winnerLabel = fastest == "PalORM" ? "🏆 **PalORM**" : fastest switch { "ADO_NET" => "ADO", _ => fastest };

                // 每格双指标：时间 [×] / 分配 [×]，倍数均以 ADO 为基准
                string VsAdo(string a)
                {
                    double t = CellTime(d, op, r, a);
                    double alloc = CellAlloc(d, op, r, a);
                    double tRatio = t / adoTime;
                    double aRatio = adoAlloc > 0 ? alloc / adoAlloc : 1.0;

                    // 持平带 ±0.5% 显示 [1.00]（与色标 |p|<0.5% 记 0% 惯例一致）；
                    // 越线倍数仅非冠军格加粗——冠军格整格加粗，嵌套 ** 破坏渲染
                    string Mul(double ratio) => ratio is >= 0.995 and < 1.005 ? "[1.00]"
                        : (ratio <= 0.67 || ratio >= 1.5) && a != fastest ? $"[**{ratio:F2}**]"
                        : $"[{ratio:F2}]";

                    string timePart = Fmt(t) + " " + Mul(tRatio);
                    string allocPart = Bytes(alloc) + " " + Mul(aRatio);
                    string cell = timePart + " / " + allocPart;
                    return a == fastest ? $"**{cell}**" : cell;
                }

                sw.WriteLine($"| {pin}{op}{floorFlag} | {r} | {winnerLabel} "
                    + string.Concat(arms.Select(VsAdo).Select(v => $"| {v} "))
                    + $"|");
            }
        }
        sw.WriteLine();
    }
}

// ── 附表：绝对时间明细（完整数据供引用） ──
sw.WriteLine("## 附表：绝对时间（全量）");
sw.WriteLine();
sw.WriteLine("> 主表倍数以 ADO 地板为基准；本附表为绝对时间与分配，供外部引用。分配为 AllocatedBytesPerOp。");
sw.WriteLine();
foreach (var g in groups)
{
    sw.WriteLine($"### {g.Title}");
    sw.WriteLine();
    sw.WriteLine("| 操作 | 方言 | 档 | ADO.NET | PalORM | Dapper | FreeSql | RepoDb | 分配 A/P/D/F/R |");
    sw.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---|");
    foreach (string d in dialects)
    foreach (int r in tiers)
    {
        if (g.MinTierOnly && r != 2000) continue;
        foreach (string op in g.Ops)
        {
            if (!arms.All(a => HasCell(d, op, r, a))) continue;
            sw.WriteLine($"| {op} | {d} | {r} "
                + string.Concat(arms.Select(a => $"| {Fmt(CellTime(d, op, r, a))} "))
                + $"| {string.Join("/", arms.Select(a => Bytes(CellAlloc(d, op, r, a))))} |");
        }
    }
    sw.WriteLine();
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

static string ArmShort(string a) => a switch { "ADO_NET" => "ADO", _ => a };

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
