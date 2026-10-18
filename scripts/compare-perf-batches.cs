// compare-perf-batches.cs（2026-10-18，替代 tmp/ 下三个一次性 .mjs 对照脚本）
// 跨批次性能中位值对照：读 bench/perfhub/results/history-<日期>-*.json，按「项 × 臂」横向
// 列出每个批次的 MedianNs，并给出跨批摆动倍数。
//
// 解决的问题（三个 .mjs 的实测缺陷）：
//   ① 单位错误：原脚本对 MedianNs 只除一次 1000 就标 ms，实际得到 µs 却标成 ms（1000× 失真，
//      实测 33615600 ns 被打印成 33615.6ms，正确值 33.62 ms）。本工具按项目口径换算：
//      µs ≥1 一位小数、<1 三位小数、≥1000 转 ms 两位小数。
//   ② 批次选择错误：原脚本用「目录里最后一个文件」冒充指定复测批，且表头写死 09:35/09:22，
//      与实际读取的批次不符。本工具按文件名时间戳生成列标签，不再有写死标签。
//   ③ 可发现性：原脚本把项目清单与日期硬编码在源码里，换一天即失效。本工具用 --date 选日、
//      --items 选项目、--list 列出当日全部可用项；缺省时按摆动幅度自动挑「动过的」项。
//
// 用法：
//   dotnet run --file scripts/compare-perf-batches.cs
//       默认最新日期，自动挑选摆动 ≥ --swing 的项（最多 --top 条）
//   dotnet run --file scripts/compare-perf-batches.cs -- --date 20261017 --items "SQLite/GetByKey/2000,MySQL/QueryAll"
//       项选择器三形态：方言/操作/行数、方言/操作、操作（后者匹配全部方言与档位）
//   dotnet run --file scripts/compare-perf-batches.cs -- --date 20261017 --list
//       列出当日全部可用项（键形态与上面一致），便于拼 --items
//   dotnet run --file scripts/compare-perf-batches.cs -- --arms "ADO_NET,PalORM" --top 50 --swing 1.15
//
// 契约：0=正常输出；1=无匹配批次/项目；2=用法错。
using System.Globalization;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var repoRoot = FindRepoRoot();
// --dir 供夹具与跨仓复用：相对路径按仓库根解析，缺省取本项目的结果目录
var dirArg = ArgValue("--dir");
var resultsDir = dirArg is null
    ? Path.Combine(repoRoot, "bench", "perfhub", "results")
    : Path.IsPathRooted(dirArg) ? dirArg : Path.Combine(repoRoot, dirArg);
if (!Directory.Exists(resultsDir))
{
    Console.Error.WriteLine($"错误：结果目录不存在（{resultsDir}）");
    return 2;
}

if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    Console.WriteLine("用法: compare-perf-batches.cs [--date yyyyMMdd] [--items csv] [--arms csv] [--top N] [--swing X] [--list] [--dir path]");
    Console.WriteLine("  --date   批次日期，缺省取结果目录中最新日期");
    Console.WriteLine("  --items  项选择器，逗号分隔；形态 方言/操作/行数 | 方言/操作 | 操作");
    Console.WriteLine("  --arms   臂过滤，缺省 ADO_NET,Dapper,PalORM（DataGen 恒排除）");
    Console.WriteLine("  --top    自动挑选模式下的最大行数，缺省 30");
    Console.WriteLine("  --swing  自动挑选的摆动阈值（最大/最小），缺省 1.30");
    Console.WriteLine("  --list   只列出当日可用项后退出");
    Console.WriteLine("  --dir    结果目录（相对仓库根或绝对路径），缺省 bench/perfhub/results");
    return 0;
}

var allFiles = Directory.GetFiles(resultsDir, "history-*.json")
    .Select(f => (Path: f, Name: Path.GetFileName(f)))
    .Where(x => x.Name.Length >= 20)
    .ToList();
if (allFiles.Count == 0)
{
    Console.Error.WriteLine($"错误：{resultsDir} 下没有 history-*.json 批次文件");
    return 1;
}

var date = ArgValue("--date") ?? LatestDate(allFiles.Select(x => x.Name));
if (date is null)
{
    Console.Error.WriteLine("错误：无法从文件名解析日期（期望 history-yyyyMMdd-HHmmss.json）");
    return 1;
}

var files = allFiles
    .Where(x => x.Name.StartsWith($"history-{date}-", StringComparison.Ordinal))
    .OrderBy(x => x.Name, StringComparer.Ordinal)
    .ToList();
if (files.Count == 0)
{
    var dates = allFiles.Select(x => LatestDate([x.Name])).Where(d => d is not null).Distinct().OrderBy(d => d, StringComparer.Ordinal);
    Console.Error.WriteLine($"错误：日期 {date} 无批次。可用日期：{string.Join(' ', dates)}");
    return 1;
}

var batches = new List<Batch>(files.Count);
foreach (var (path, name) in files)
{
    var medians = LoadMedians(path);
    if (medians.Count == 0) continue;
    batches.Add(new Batch(TimeLabel(name), medians));
}
if (batches.Count == 0)
{
    Console.Error.WriteLine($"错误：日期 {date} 的批次里没有可用测量项（全部为空或被跳过）");
    return 1;
}

var itemKeys = batches.SelectMany(b => b.Medians.Keys).Distinct()
    .OrderBy(k => k.Dialect, StringComparer.Ordinal)
    .ThenBy(k => k.Operation, StringComparer.Ordinal)
    .ThenBy(k => k.Rows)
    .ToList();
var presentArms = batches.SelectMany(b => b.Medians.Values.SelectMany(a => a.Keys)).Distinct().ToList();

if (args.Contains("--list", StringComparer.Ordinal))
{
    Console.WriteLine($"日期 {date}：{batches.Count} 批，{itemKeys.Count} 项，臂 {string.Join('/', OrderArms(presentArms))}");
    foreach (var k in itemKeys) Console.WriteLine($"  {k.Dialect}/{k.Operation}/{k.Rows}");
    return 0;
}

var armFilter = (ArgValue("--arms") ?? "ADO_NET,Dapper,PalORM")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var arms = OrderArms(presentArms).Where(a => armFilter.Contains(a)).ToList();
if (arms.Count == 0)
{
    Console.Error.WriteLine($"错误：指定臂均不在数据中。可选臂：{string.Join(' ', OrderArms(presentArms))}");
    return 1;
}

var selected = new List<ItemKey>();
var selectors = (ArgValue("--items") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (selectors.Length > 0)
{
    foreach (var sel in selectors)
    {
        var hit = itemKeys.Where(k => Matches(k, sel)).ToList();
        if (hit.Count == 0)
        {
            Console.Error.WriteLine($"警告：选择器 [{sel}] 无匹配项（用 --list 查看当日可用项）");
            continue;
        }
        selected.AddRange(hit);
    }
    selected = selected.Distinct().ToList();
    if (selected.Count == 0)
    {
        Console.Error.WriteLine("错误：全部选择器均无匹配项");
        return 1;
    }
}
else
{
    var swing = double.TryParse(ArgValue("--swing"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1.30;
    var top = int.TryParse(ArgValue("--top"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 30;
    var ranked = itemKeys
        .Select(k => (Key: k, Swing: MaxSwing(batches, k, arms)))
        .Where(x => x.Swing > 0)
        .OrderByDescending(x => x.Swing)
        .ToList();
    selected = ranked.Where(x => x.Swing >= swing).Take(top).Select(x => x.Key).ToList();
    if (selected.Count == 0)
    {
        selected = ranked.Take(top).Select(x => x.Key).ToList();
        Console.WriteLine($"（无摆动 ≥ {swing.ToString("F2", CultureInfo.InvariantCulture)}× 的项，改列摆动最大的 {selected.Count} 项）");
    }
}

Console.WriteLine($"日期 {date}：{batches.Count} 批｜项 {selected.Count}｜臂 {string.Join("/", arms)}");
Console.WriteLine("单位：MedianNs 换算为 µs（≥1000 µs 转 ms）；摆动 = 各批最大/最小；— 表示该批无此项");
Console.WriteLine();

var itemWidth = Math.Min(46, selected.Max(k => k.ToString().Length));
var header = $"{"项".PadRight(itemWidth)} | {"臂".PadRight(8)} | "
    + string.Join(" | ", batches.Select(b => b.Label.PadLeft(10))) + " | " + "摆动".PadLeft(8);
Console.WriteLine(header);
Console.WriteLine(new string('-', header.Length));

foreach (var key in selected)
{
    foreach (var arm in arms)
    {
        var values = batches.Select(b => b.Medians.TryGetValue(key, out var perArm) && perArm.TryGetValue(arm, out var ns) ? ns : (double?)null).ToList();
        if (values.All(v => v is null)) continue;
        var cells = values.Select(v => v is null ? "—".PadLeft(10) : FormatNs(v.Value).PadLeft(10));
        var swing = Swing(values);
        var swingCell = (swing > 0 ? swing.ToString("F2", CultureInfo.InvariantCulture) + "×" : "—").PadLeft(8);
        Console.WriteLine($"{Fit(key.ToString(), itemWidth).PadRight(itemWidth)} | {arm.PadRight(8)} | {string.Join(" | ", cells)} | {swingCell}");
    }
}

Console.WriteLine();
Console.WriteLine($"批次时间：{string.Join("  ", batches.Select(b => b.Label))}");
return 0;

// ── 数据装载 ──

static Dictionary<ItemKey, Dictionary<string, double>> LoadMedians(string path)
{
    var map = new Dictionary<ItemKey, Dictionary<string, double>>();
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    if (!doc.RootElement.TryGetProperty("Measurements", out var arr) || arr.ValueKind != JsonValueKind.Array)
    {
        return map;
    }
    foreach (var m in arr.EnumerateArray())
    {
        var impl = Str(m, "Implementation");
        // DataGen 是数据生成臂（MedianNs 恒 0），不属于被测实现
        if (impl.Length == 0 || impl.Equals("DataGen", StringComparison.OrdinalIgnoreCase)) continue;
        var dialect = Str(m, "Dialect");
        var op = Str(m, "Operation");
        if (dialect.Length == 0 || op.Length == 0 || dialect == "—") continue;
        if (!m.TryGetProperty("Rows", out var rowsEl) || !rowsEl.TryGetInt32(out var rows)) continue;
        if (!m.TryGetProperty("MedianNs", out var nsEl) || nsEl.ValueKind != JsonValueKind.Number) continue;
        var ns = nsEl.GetDouble();
        if (ns <= 0) continue;
        var key = new ItemKey(dialect, op, rows);
        if (!map.TryGetValue(key, out var perArm))
        {
            perArm = new Dictionary<string, double>(StringComparer.Ordinal);
            map[key] = perArm;
        }
        perArm[impl] = ns;
    }
    return map;
}

static string Str(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

// ── 选择与计算 ──

static bool Matches(ItemKey k, string selector)
{
    var parts = selector.Split('/');
    return parts.Length switch
    {
        1 => k.Operation.Equals(parts[0], StringComparison.OrdinalIgnoreCase),
        2 => k.Dialect.Equals(parts[0], StringComparison.OrdinalIgnoreCase)
             && k.Operation.Equals(parts[1], StringComparison.OrdinalIgnoreCase),
        3 => k.Dialect.Equals(parts[0], StringComparison.OrdinalIgnoreCase)
             && k.Operation.Equals(parts[1], StringComparison.OrdinalIgnoreCase)
             && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r == k.Rows,
        _ => false,
    };
}

static double MaxSwing(List<Batch> batches, ItemKey key, List<string> arms) =>
    arms.Select(a => Swing(batches.Select(b => b.Medians.TryGetValue(key, out var perArm) && perArm.TryGetValue(a, out var ns) ? ns : (double?)null).ToList()))
        .DefaultIfEmpty(0)
        .Max();

static double Swing(List<double?> values)
{
    var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
    if (present.Count < 2) return 0;
    var min = present.Min();
    return min <= 0 ? 0 : present.Max() / min;
}

// ── 格式化（项目口径：µs <1 三位、≥1 一位；≥1000 µs 转 ms 两位）──

static string FormatNs(double ns)
{
    var us = ns / 1000.0;
    if (us < 1) return us.ToString("F3", CultureInfo.InvariantCulture) + " µs";
    if (us < 1000) return us.ToString("F1", CultureInfo.InvariantCulture) + " µs";
    return (us / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + " ms";
}

static string Fit(string s, int width) => s.Length <= width ? s : s[..(width - 1)] + "…";

static List<string> OrderArms(IEnumerable<string> arms)
{
    string[] preferred = ["ADO_NET", "Dapper", "PalORM"];
    var list = arms.Distinct(StringComparer.Ordinal).ToList();
    return [.. list.Where(a => preferred.Contains(a, StringComparer.Ordinal))
        .OrderBy(a => Array.IndexOf(preferred, a))
        .Concat(list.Where(a => !preferred.Contains(a, StringComparer.Ordinal)).OrderBy(a => a, StringComparer.Ordinal))];
}

static string? LatestDate(IEnumerable<string> names) =>
    names.Select(n => n.Length >= 16 && n.StartsWith("history-", StringComparison.Ordinal) ? n.Substring(8, 8) : null)
        .Where(d => d is not null && d.Length == 8 && d.All(char.IsAsciiDigit))
        .OrderByDescending(d => d, StringComparer.Ordinal)
        .FirstOrDefault();

static string TimeLabel(string fileName)
{
    // history-yyyyMMdd-HHmmss.json → HH:mm:ss
    var stem = Path.GetFileNameWithoutExtension(fileName);
    var dash = stem.LastIndexOf('-');
    var t = dash >= 0 && stem.Length >= dash + 7 ? stem[(dash + 1)..] : stem;
    return t.Length == 6 ? $"{t[..2]}:{t[2..4]}:{t[4..]}" : stem;
}

string? ArgValue(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    Console.Error.WriteLine("错误：未找到仓库根（PalORM.slnx 哨兵缺失）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}

internal readonly record struct ItemKey(string Dialect, string Operation, int Rows)
{
    public override string ToString() => $"{Dialect}/{Operation}/{Rows}";
}

internal sealed record Batch(string Label, Dictionary<ItemKey, Dictionary<string, double>> Medians);
