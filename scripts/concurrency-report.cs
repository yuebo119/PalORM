// concurrency-report.cs（高并发压测摘要，2026-11-07）
//
// 动机：压测跑完后的判读一直靠临时脚本手工解析 TUnit 报告，指标定义与门禁不同源时会给出
// 两套数字。本脚本复用 assert-test-counts.cs 的族分类正则（concurrency = Concurrency|Parallel|
// Pool|Soak；dialect = Dialect；faultInjection = Fault；先匹配先归属），一次输出：
//   ① 套件总量/通过/失败/跳过/超时与实际耗时
//   ② 三族分布（按套件）
//   ③ 最慢 10 类（同类用例耗时求和，与并行墙钟不同，报告中明确标注）
//   ④ 失败明细
// 并可用 --json 落地机器可读摘要，供台账或后续对比。
//
// 用法：
//   concurrency-report.cs [--json <out.json>] [--seed <n>] [--rounds <n>] <report.json> [<report.json> ...]
// 退出码：有失败/超时 = 1；否则 0。参数错误 = 2。
//
// 口径约定：族分类与门禁同源（改正则要同步 assert-test-counts.cs 与 test-counts.json 族地板）；
// 类耗时是「该类全部用例 durationMs 之和」，并行执行下不等于墙钟，不能与其他类相加当总时长。
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

string? jsonOut = null;
int? seed = null;
int? rounds = null;
var inputs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json":
            if (i + 1 >= args.Length) return Usage("--json 缺参数");
            jsonOut = args[++i];
            break;
        case "--seed":
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out int seedValue)) return Usage("--seed 需要整数");
            seed = seedValue;
            break;
        case "--rounds":
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out int roundsValue)) return Usage("--rounds 需要整数");
            rounds = roundsValue;
            break;
        case "-h" or "--help":
            return Usage(null);
        default:
            inputs.Add(args[i]);
            break;
    }
}
if (inputs.Count == 0) return Usage("至少给一个 TUnit 报告路径");

// 与 assert-test-counts.cs 的 FamilyCatalog 同源（顺序即优先级，先匹配先归属）。
var families = new (string Key, Regex Pattern)[]
{
    ("concurrency", new Regex(@"Concurrency|Parallel|Pool|Soak", RegexOptions.IgnoreCase)),
    ("dialect", new Regex(@"Dialect", RegexOptions.IgnoreCase)),
    ("faultInjection", new Regex(@"Fault", RegexOptions.IgnoreCase)),
};

var suites = new List<SuiteRow>();
var failures = new List<string>();
foreach (string path in inputs)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"::error::报告不存在：{path}");
        return 2;
    }
    JsonNode root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
    string assembly = root["assemblyName"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path);
    JsonNode summary = root["summary"] ?? throw new InvalidDataException($"{path}: 缺 summary");
    long wallMs = (long)(root["totalDurationMs"]?.GetValue<double>() ?? 0);
    var familyCounts = new Dictionary<string, long>(StringComparer.Ordinal);
    var classRows = new List<ClassRow>();

    foreach (JsonNode? groupNode in root["groups"]?.AsArray() ?? [])
    {
        if (groupNode is null) continue;
        string className = groupNode["className"]?.GetValue<string>() ?? "?";
        JsonNode groupSummary = groupNode["summary"] ?? new JsonObject();
        long passed = (long)(groupSummary["passed"]?.GetValue<double>() ?? 0);
        long failed = (long)(groupSummary["failed"]?.GetValue<double>() ?? 0);
        long skipped = (long)(groupSummary["skipped"]?.GetValue<double>() ?? 0);
        long timedOut = (long)(groupSummary["timedOut"]?.GetValue<double>() ?? 0);
        long durationMs = 0;
        long count = 0;
        foreach (JsonNode? testNode in groupNode["tests"]?.AsArray() ?? [])
        {
            if (testNode is null) continue;
            count++;
            durationMs += (long)(testNode["durationMs"]?.GetValue<double>() ?? 0);
            string displayName = testNode["displayName"]?.GetValue<string>() ?? "";
            string status = testNode["status"]?.GetValue<string>() ?? "";
            string family = Classify(className + " " + displayName, families);
            familyCounts[family] = familyCounts.GetValueOrDefault(family) + 1;
            if (status is "failed" or "error" or "timeout" or "timedOut" or "cancelled")
                failures.Add($"{assembly} / {className}.{displayName}: {status}");
        }
        classRows.Add(new ClassRow(className, count, passed, failed, skipped, timedOut, durationMs));
    }

    suites.Add(new SuiteRow(
        assembly,
        (long)(summary["total"]?.GetValue<double>() ?? 0),
        (long)(summary["passed"]?.GetValue<double>() ?? 0),
        (long)(summary["failed"]?.GetValue<double>() ?? 0),
        (long)(summary["skipped"]?.GetValue<double>() ?? 0),
        (long)(summary["timedOut"]?.GetValue<double>() ?? 0),
        wallMs,
        familyCounts,
        classRows));
}

// ── 控制台摘要（Markdown）────────────────────────────────────────────
Console.WriteLine("# 并发压测摘要");
Console.WriteLine();
Console.WriteLine($"运行参数：seed={seed?.ToString(CultureInfo.InvariantCulture) ?? "-"}，"
    + $"CS22 轮数={rounds?.ToString(CultureInfo.InvariantCulture) ?? "-"}；"
    + $"数据源 {inputs.Count} 份报告。");
Console.WriteLine();
Console.WriteLine("| 套件 | 总数 | 通过 | 失败 | 跳过 | 超时 | 实际耗时 |");
Console.WriteLine("|---|---|---|---|---|---|---|");
foreach (SuiteRow suite in suites)
{
    Console.WriteLine($"| {suite.Assembly} | {suite.Total} | {suite.Passed} | {suite.Failed} | "
        + $"{suite.Skipped} | {suite.TimedOut} | {FormatDuration(suite.WallMs)} |");
}
Console.WriteLine();

Console.WriteLine("## 族分布（与门禁同源分类）");
Console.WriteLine();
Console.WriteLine("| 族 | " + string.Join(" | ", suites.Select(s => s.Assembly)) + " | 合计 |");
Console.WriteLine("|---|" + string.Concat(suites.Select(_ => "---|")) + "---|");
foreach (string family in new[] { "concurrency", "dialect", "faultInjection", "other" })
{
    long[] perSuite = [.. suites.Select(s => s.FamilyCounts.GetValueOrDefault(family))];
    Console.WriteLine($"| {family} | " + string.Join(" | ", perSuite.Select(v => v.ToString(CultureInfo.InvariantCulture)))
        + $" | {perSuite.Sum()} |");
}
Console.WriteLine();
Console.WriteLine("说明：`other` 为未命中三族关键词的用例；分类对象是 className + displayName，先匹配先归属。");
Console.WriteLine();

Console.WriteLine("## 最慢 10 类（同类用例耗时求和，并行下不等于墙钟）");
Console.WriteLine();
Console.WriteLine("| 类 | 套件 | 用例 | 总耗时 | 均耗时 |");
Console.WriteLine("|---|---|---|---|---|");
foreach ((string Suite, ClassRow Class) row in suites
    .SelectMany(s => s.Classes.Select(c => (Suite: s.Assembly, Class: c)))
    .OrderByDescending(x => x.Class.DurationMs)
    .Take(10))
{
    double mean = row.Class.Count == 0 ? 0 : (double)row.Class.DurationMs / row.Class.Count;
    Console.WriteLine($"| {row.Class.ClassName} | {row.Suite} | {row.Class.Count} | "
        + $"{FormatDuration(row.Class.DurationMs)} | {mean:F0} ms |");
}
Console.WriteLine();

Console.WriteLine("## 失败明细");
Console.WriteLine();
if (failures.Count == 0)
    Console.WriteLine("无。");
else
    foreach (string failure in failures) Console.WriteLine($"- {failure}");
Console.WriteLine();

// ── 机器可读摘要 ───────────────────────────────────────────────────
if (jsonOut is not null)
{
    var root = new JsonObject
    {
        ["capturedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        ["seed"] = seed,
        ["rounds"] = rounds,
        ["suites"] = new JsonArray([.. suites.Select(s => (JsonNode)new JsonObject
        {
            ["assembly"] = s.Assembly,
            ["total"] = s.Total,
            ["passed"] = s.Passed,
            ["failed"] = s.Failed,
            ["skipped"] = s.Skipped,
            ["timedOut"] = s.TimedOut,
            ["wallMs"] = s.WallMs,
            ["families"] = new JsonObject(
                [.. s.FamilyCounts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))]),
        })]),
        ["slowest"] = new JsonArray([.. suites
            .SelectMany(s => s.Classes.Select(c => (Suite: s.Assembly, Class: c)))
            .OrderByDescending(x => x.Class.DurationMs)
            .Take(10)
            .Select(x => (JsonNode)new JsonObject
            {
                ["class"] = x.Class.ClassName,
                ["suite"] = x.Suite,
                ["count"] = x.Class.Count,
                ["durationMs"] = x.Class.DurationMs,
            })]),
        ["failures"] = new JsonArray([.. failures.Select(f => (JsonNode)JsonValue.Create(f))]),
    };
    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(jsonOut, root.ToJsonString(options) + "\n", new UTF8Encoding(false));
    Console.WriteLine($"JSON 摘要已写入 {jsonOut}");
}

return suites.Any(s => s.Failed > 0 || s.TimedOut > 0) ? 1 : 0;

static string Classify(string text, (string Key, Regex Pattern)[] patterns)
    => patterns.FirstOrDefault(p => p.Pattern.IsMatch(text)).Key ?? "other";

static string FormatDuration(long ms)
    => ms >= 60_000
        ? (ms / 60_000.0).ToString("F1", CultureInfo.InvariantCulture) + " min"
        : (ms / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + " s";

static int Usage(string? error)
{
    if (error is not null) Console.Error.WriteLine($"::error::{error}");
    Console.Error.WriteLine("usage: concurrency-report.cs [--json <out.json>] [--seed <n>] [--rounds <n>] <report.json>...");
    return 2;
}

internal sealed record SuiteRow(
    string Assembly, long Total, long Passed, long Failed, long Skipped, long TimedOut, long WallMs,
    Dictionary<string, long> FamilyCounts, List<ClassRow> Classes);

internal sealed record ClassRow(
    string ClassName, long Count, long Passed, long Failed, long Skipped, long TimedOut, long DurationMs);
