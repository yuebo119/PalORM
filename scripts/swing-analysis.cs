// swing-analysis.cs（自 .ai/scripts/swing-analysis.mjs 迁移，脚本全面 C# 化轮）
// 一次性分析→可重跑工具：全键比值摆动分布（选门禁"宽散布"阈值时重跑）。
// 契约：0=正常输出；bench/results 缺失时退出 2。与 .mjs 版输出对拍兼容。
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var dir = Path.Combine(FindRepoRoot(), "bench", "results");
if (!Directory.Exists(dir))
{
    Console.Error.WriteLine($"错误: {dir} 不存在");
    return 2;
}

var exclude = new Regex(@"quick|filtered|ab/|recheck|verify|o1|o2|s11|s12|pg2k", RegexOptions.Compiled);
var batches = new List<(string Label, string Ts, List<(string Name, string Dialect, string Tier, string Arm, double Ratio)> Items)>();
foreach (var f in Directory.GetFiles(dir, "perfhub-*.json").OrderBy(f => f, StringComparer.Ordinal))
{
    using var doc = JsonDocument.Parse(File.ReadAllText(f));
    var j = doc.RootElement;
    var label = j.TryGetProperty("Label", out var l) ? l.GetString() ?? "" : "";
    var ts = j.TryGetProperty("Timestamp", out var t) ? t.ToString() : "";
    var items = new List<(string, string, string, string, double)>();
    if (j.TryGetProperty("Items", out var arr))
    {
        foreach (var it in arr.EnumerateArray())
        {
            // 装载阶段提取标量（JsonElement 生命周期随 doc 释放，不可跨 using 持有）
            items.Add((
                it.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                it.TryGetProperty("Dialect", out var d) ? d.GetString() ?? "" : "",
                it.TryGetProperty("Tier", out var ti) ? (ti.ValueKind == JsonValueKind.String ? ti.GetString() ?? "" : ti.GetRawText()) : "",
                it.TryGetProperty("Arm", out var a) ? a.GetString() ?? "" : "",
                it.TryGetProperty("Ratio", out var r) && r.TryGetDouble(out var rv) ? rv : 0.0));
        }
    }
    if (!exclude.IsMatch(label)) batches.Add((label, ts, items));
}
batches.Sort((x, y) => string.CompareOrdinal(x.Ts, y.Ts));
Console.WriteLine($"非子集批次数: {batches.Count}");

var series = new Dictionary<string, List<double>>();
foreach (var (_, _, items) in batches)
{
    foreach (var (name, dialect, tier, arm, ratio) in items)
    {
        if (arm != "PalORM" || ratio <= 0) continue;
        var key = $"{name}|{dialect}|{tier}";
        if (!series.TryGetValue(key, out var list)) series[key] = list = [];
        list.Add(ratio);
    }
}

var swings = new List<(string Key, double Swing, int N)>();
foreach (var (k, v) in series.Select(kv => (kv.Key, kv.Value)))
{
    if (v.Count < 4) continue;
    swings.Add((k, v.Max() / v.Min(), v.Count));
}
swings.Sort((a, b) => b.Swing.CompareTo(a.Swing));
Console.WriteLine($"有 ≥4 批序列的键: {swings.Count} 个");
Console.WriteLine("摆动分布 top15:");
foreach (var (key, swing, n) in swings.Take(15).Select(s => (s.Key, s.Swing, s.N))) Console.WriteLine($"  {swing:F2}× ({n}批) {key}");
var vals = swings.Select(s => s.Swing).OrderBy(v => v).ToList();
double Q(double p)
{
    return vals[Math.Min((int)(vals.Count * p), vals.Count - 1)];
}
Console.WriteLine($"分位数: p50={Q(0.5):F2} p75={Q(0.75):F2} p90={Q(0.90):F2} p95={Q(0.95):F2}");
Console.WriteLine($"摆动 >2.0× 的键数: {swings.Count(s => s.Swing > 2)}；>1.6×: {swings.Count(s => s.Swing > 1.6)}；>1.5×: {swings.Count(s => s.Swing > 1.5)}");
return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    Console.Error.WriteLine("错误: 未找到仓库根（PalORM.slnx 哨兵缺失）");
    Environment.Exit(2);
    return "";
}
