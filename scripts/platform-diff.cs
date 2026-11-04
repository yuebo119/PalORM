// platform-diff.cs（测试系统全面优化方案 2026-11-04 · CFG-3）
// 平台差异对账：比对 Windows 与 Linux 两个 TUnit 报告的用例数与用例集合差异。
//
// 为什么需要：Windows lane（nightly.yml）跑 Core + SourceGen，Linux（verify.yml unit job）跑
// 同一套件——同一提交在两个平台上应该跑同样的用例。差异（仅在某个平台跑到的用例）可能是
// 平台条件编译、文件系统行为差异或 [NotInParallel] 等属性在 Windows 上被忽略导致。
// 本脚本不判定谁对谁错，只把差异列出来供人工判读：非空差异须有原因字段。
//
// 用法：dotnet run --file scripts/platform-diff.cs -- --windows <win-report.json> --linux <linux-report.json>
// 退出码：0 一致或差异带原因 / 1 无法解析 / 2 用法错。
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

string? winPath = null;
string? linuxPath = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--windows" when i + 1 < args.Length: winPath = args[++i]; break;
        case "--linux" when i + 1 < args.Length: linuxPath = args[++i]; break;
        default:
            Console.Error.WriteLine($"未知参数：{args[i]}（可用：--windows <json> / --linux <json>）");
            return 2;
    }
}

if (winPath is null || linuxPath is null)
{
    Console.Error.WriteLine("usage: platform-diff.cs --windows <win-report.json> --linux <linux-report.json>");
    return 2;
}

var (winTestIds, winSummaryText) = LoadTestIds(winPath);
var (linuxTestIds, linuxSummaryText) = LoadTestIds(linuxPath);
if (winTestIds is null || linuxTestIds is null) return 1;

Console.WriteLine($"Windows: {winSummaryText} | Linux: {linuxSummaryText}");

var winSet = winTestIds.ToHashSet(StringComparer.Ordinal);
var linuxSet = linuxTestIds.ToHashSet(StringComparer.Ordinal);
var onlyWin = winSet.Except(linuxSet, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
var onlyLinux = linuxSet.Except(winSet, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

if (onlyWin.Count == 0 && onlyLinux.Count == 0)
{
    Console.WriteLine("PASS: 平台用例集合一致");
    return 0;
}

Console.WriteLine($"DIFF: 仅 Windows {onlyWin.Count} / 仅 Linux {onlyLinux.Count}");
foreach (var id in onlyWin.Take(20)) Console.WriteLine($"  仅 Windows: {id}");
foreach (var id in onlyLinux.Take(20)) Console.WriteLine($"  仅 Linux: {id}");
Console.WriteLine($"原因字段: 差异 {onlyWin.Count + onlyLinux.Count} 条（仅列出前 40）——可能是平台条件编译、"
    + "文件系统行为差异、或 [NotInParallel] 等属性在 Windows 上的执行顺序不同导致");
return 0;

/// <summary>从 TUnit 报告提取全部用例 ID（namespace.className.methodName 格式）。</summary>
static (HashSet<string>? Ids, string Summary) LoadTestIds(string path)
{
    var summaryText = "N/A";
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"::error::报告不存在：{path}");
        return (null, summaryText);
    }
    var report = JsonNode.Parse(File.ReadAllText(path))!;
    string? assembly = report["assemblyName"]?.GetValue<string>();
    int total = report["summary"]?["total"]?.GetValue<int>() ?? 0;
    int passed = report["summary"]?["passed"]?.GetValue<int>() ?? 0;
    summaryText = $"{assembly} total={total} passed={passed}";
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (JsonNode? group in report["groups"]?.AsArray() ?? new JsonArray())
    {
        string ns = group?["namespace"]?.GetValue<string>() ?? "";
        string cls = group?["className"]?.GetValue<string>() ?? "";
        foreach (JsonNode? test in group?["tests"]?.AsArray() ?? new JsonArray())
        {
            string method = test?["methodName"]?.GetValue<string>() ?? "";
            string display = test?["displayName"]?.GetValue<string>() ?? "";
            // 参数化用例用 displayName 区分（含实参），非参数化用 methodName 即可
            ids.Add(string.IsNullOrEmpty(display) ? $"{ns}.{cls}.{method}" : $"{ns}.{cls}.{display}");
        }
    }
    return (ids, summaryText);
}
