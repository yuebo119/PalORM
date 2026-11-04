// assert-test-counts.cs（自 assert-test-counts.sh 迁移，脚本 C# 化整改方案 T2-2；
// 一并消除其内联 node -e 依赖）
// CI"确实执行了"断言：① passed 必须**等于**地板（精确匹配，2026-11-03 由 >= 改，见
// 《测试机制重设计方案》P2-c）；② skipped == 0（无跳过机制的套件出现跳过 = 静默降级）；
// ③（仅 Integration）ExternalDatabase 通过数必须等于地板（真库面不许静默消失）。
//
// 为什么改成精确匹配：>= 形态下地板是手写常数，实测增长后地板相对变松（曾出现 286 对 574），
// 删掉一条用例无法被发现。精确匹配让「删用例」和「加用例」都必须显式改地板，
// 而 --update 把取新值这一步自动化，避免人工抄错。
//
// 报告可信度（2026-11-04，GATE-3）：读取报告前先校验四件事，全部通过才进入地板判定——
// ① assemblyName 与被断言项目一致（防喂错报告）；② timestamp 可解析且在 24 小时内
// （防旧报告/陈旧产物）；③ summary 各桶齐全；④ total 算术一致（total = 各桶之和，防篡改/截断）。
// 任一不符 exit 1——本断言不接受无法归属或疑似陈旧的报告。
//
// --families（2026-11-04，GATE-4）：按族用例地板。以报告 className/displayName 关键词把用例
// 归入并发/方言/故障注入三族，与 test-counts.json 的 families.<project> 地板比对（>= 语义）。
// AOT 族不适用：PalORM.AotTest 是原生 AOT 控制台程序（退出码 + stdout 断言），没有 [Test] 标记。
//
// --self-check（2026-11-04，FIX-3）：内嵌夹具自证判据，不读任何外部文件。
// 理由见方案 §3.2：本脚本需要报告入参，从设计上进不了 V14 的「无参跑到底 + 完成标记」存活
// 监测面；自检模式让「脚本能跑」与「判据能失败」同时可判。好夹具必须 exit 0，坏夹具必须
// 各自 exit 1，任一不符则自检自身 exit 1。
//
// 用法：assert-test-counts.cs [--update] <project> <report.json>
//       assert-test-counts.cs --families <project> <report.json>
//       assert-test-counts.cs --self-check
// 陷阱（账本 M0-3）：不 grep stdout（编码/语言漂移）、不硬编码配置目录（从仓库根解析）。
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

const int ReportFreshnessHours = 24;

if (args.Length == 1 && args[0] == "--self-check")
{
    return SelfCheck();
}

if (args.Length == 3 && args[0] == "--families")
{
    return AssertFamilies(args[1], args[2]);
}

bool update = args.Length > 0 && args[0] == "--update";
string[] rest = update ? args[1..] : args;
if (rest.Length < 2)
{
    Console.Error.WriteLine("usage: assert-test-counts.cs [--update] <project> <report.json>");
    Console.Error.WriteLine("       assert-test-counts.cs --families <project> <report.json>");
    Console.Error.WriteLine("       assert-test-counts.cs --self-check");
    return 1;
}
var project = rest[0];
var reportPath = rest[1];

if (!File.Exists(reportPath))
{
    Console.Error.WriteLine($"::error::报告不存在：{reportPath}（测试未运行或路径错——本断言不接受'找不到报告即放行'）");
    return 1;
}

var repoRoot = FindRepoRoot();
var floorFile = Path.Combine(repoRoot, "bench", "baselines", "test-counts.json");

var report = JsonNode.Parse(File.ReadAllText(reportPath))!;
var floors = JsonNode.Parse(File.ReadAllText(floorFile))!;
JsonObject? floor = floors[project] as JsonObject;
if (floor is null)
{
    Console.Error.WriteLine($"::error::地板文件缺少项目 {project} 的条目");
    return 1;
}

// GATE-3：报告可信度先行——无法归属或疑似陈旧的报告不进入地板判定
List<string> trustFailures = ValidateReportTrust(report, project);
if (trustFailures.Count > 0)
{
    foreach (var failure in trustFailures)
    {
        Console.Error.WriteLine($"::error::{project}: {failure}");
    }
    return 1;
}

(int passed, int skipped, int? external) = ReadCounts(report, floor);
int floorPassed = floor["passed"]!.GetValue<int>();

if (update)
{
    floor["passed"] = passed;
    if (external is int externalValue)
    {
        floor["externalDatabase"] = externalValue;
    }
    string json = floors
        .ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        })
        .Replace("\r\n", "\n", StringComparison.Ordinal);
    File.WriteAllText(floorFile, json + "\n", new UTF8Encoding(false));
    Console.WriteLine($"{project}: 地板已更新 passed={passed}{(external is int e ? $" externalDb={e}" : "")}");
    return 0;
}

List<string> failures = Evaluate(passed, skipped, external, floor);
string externalText = external is int count ? $" externalDb={count}" : "";
Console.WriteLine($"{project}: passed={passed} skipped={skipped}{externalText}");

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"::error::{project}: {failure}");
    }
    return 1;
}
return 0;

// ── 报告可信度（GATE-3）──

static List<string> ValidateReportTrust(JsonNode report, string project)
{
    var failures = new List<string>();

    string? assemblyName = report["assemblyName"]?.GetValue<string>();
    if (string.IsNullOrEmpty(assemblyName))
    {
        failures.Add("报告缺 assemblyName（schema 变更或伪造报告）——无法归属的报告不放行");
    }
    else if (!string.Equals(assemblyName, project, StringComparison.Ordinal))
    {
        failures.Add($"报告 assemblyName {assemblyName} ≠ 被断言项目 {project}（喂错报告）");
    }

    string? timestampText = report["timestamp"]?.GetValue<string>();
    DateTime? timestamp = ParseReportTimestamp(timestampText);
    if (timestamp is null)
    {
        failures.Add($"报告缺可解析的 timestamp（拿到 {(timestampText is null ? "null" : $"无法解析的 {timestampText}")}）");
    }
    else if (Math.Abs((DateTime.UtcNow - timestamp.Value).TotalHours) > ReportFreshnessHours)
    {
        failures.Add($"报告时间戳 {timestamp:yyyy-MM-dd HH:mm:ss} 距今超过 {ReportFreshnessHours} 小时（旧报告/陈旧产物）");
    }

    JsonObject? summary = report["summary"] as JsonObject;
    string[] buckets = ["total", "passed", "failed", "skipped", "cancelled", "timedOut", "flaky"];
    var values = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (string bucket in buckets)
    {
        if (summary?[bucket]?.GetValue<int>() is not int value)
        {
            failures.Add($"报告 summary 缺 {bucket}（字段缺失）");
        }
        else
        {
            values[bucket] = value;
        }
    }
    if (values.Count == buckets.Length)
    {
        int sum = values["passed"] + values["failed"] + values["skipped"]
            + values["cancelled"] + values["timedOut"] + values["flaky"];
        if (values["total"] != sum)
        {
            failures.Add($"summary 算术不一致：total {values["total"]} ≠ 各桶之和 {sum}");
        }
    }
    return failures;
}

/// <summary>TUnit 的 timestamp 形如 "04 Nov 2026, 16:36:58 UTC"（自定义格式）；
/// 先按精确格式解析，失败再退回通用解析，两处都失败视为不可解析（fail-closed）。</summary>
static DateTime? ParseReportTimestamp(string? text)
{
    if (string.IsNullOrEmpty(text)) return null;
    if (DateTime.TryParseExact(text, "dd MMM yyyy, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime exact))
    {
        return DateTime.SpecifyKind(exact, DateTimeKind.Utc);
    }
    if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset general))
    {
        return general.UtcDateTime;
    }
    return null;
}

// ── 按族用例地板（GATE-4）──


static int AssertFamilies(string project, string reportPath)
{
    if (!File.Exists(reportPath))
    {
        Console.Error.WriteLine($"::error::报告不存在：{reportPath}");
        return 1;
    }
    var repoRoot = FindRepoRoot();
    var floors = JsonNode.Parse(File.ReadAllText(Path.Combine(repoRoot, "bench", "baselines", "test-counts.json")))!;
    JsonObject? familyFloors = floors[project]?["families"] as JsonObject;
    if (familyFloors is null)
    {
        Console.Error.WriteLine($"::error::test-counts.json 缺 {project}.families 族地板（GATE-4：新增套件必须登记族地板）");
        return 1;
    }

    var report = JsonNode.Parse(File.ReadAllText(reportPath))!;
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (JsonNode? group in report["groups"]?.AsArray() ?? new JsonArray())
    {
        foreach (JsonNode? test in group?["tests"]?.AsArray() ?? new JsonArray())
        {
            if (test?["status"]?.GetValue<string>() != "passed") continue;
            string className = test["className"]?.GetValue<string>() ?? "";
            string displayName = test["displayName"]?.GetValue<string>() ?? "";
            string probe = $"{className} {displayName}";
            foreach ((string key, Regex pattern) in FamilyCatalog.Patterns)
            {
                if (pattern.IsMatch(probe)) counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
    }

    List<string> familyFailures = [];
    foreach ((string key, JsonNode? floorNode) in familyFloors)
    {
        int floorValue = floorNode?.GetValue<int>() ?? 0;
        int actual = counts.GetValueOrDefault(key);
        Console.WriteLine($"{project} 族 {key}: {actual}（地板 {floorValue}）");
        if (actual < floorValue)
        {
            familyFailures.Add($"族 {key} 用例 {actual} < 地板 {floorValue}（族内用例被删？分类关键词变化？）");
        }
    }
    if (familyFailures.Count > 0)
    {
        foreach (string failure in familyFailures) Console.Error.WriteLine($"::error::{project}: {failure}");
        return 1;
    }
    return 0;
}

// ── 判定与夹具（自检与正式路径共用同一实现，避免两套判据漂移）──

static int Judge(JsonNode report, JsonObject floor, string project)
{
    var trust = ValidateReportTrust(report, project);
    (int passed, int skipped, int? external) = ReadCounts(report, floor);
    var failures = Evaluate(passed, skipped, external, floor);
    failures.AddRange(trust);
    return failures.Count == 0 ? 0 : 1;
}

static List<string> Evaluate(int passed, int skipped, int? external, JsonObject floor)
{
    List<string> failures = [];
    int floorPassed = floor["passed"]!.GetValue<int>();
    if (passed != floorPassed)
    {
        failures.Add($"passed {passed} ≠ 地板 {floorPassed}（删/加用例后未同步地板；确认无误后运行 --update 写入新值）");
    }
    if (skipped != 0)
    {
        failures.Add($"skipped {skipped} ≠ 0（无跳过机制的套件出现跳过 = 静默降级）");
    }
    if (external is int externalPassed)
    {
        var floorExternal = floor["externalDatabase"]!.GetValue<int>();
        if (externalPassed != floorExternal)
        {
            failures.Add($"ExternalDatabase 通过数 {externalPassed} ≠ 地板 {floorExternal}（真库用例增减未同步地板）");
        }
    }
    return failures;
}

// 真库口径只在有真库地板的项目上计算（与正式路径逐字同义）
static (int Passed, int Skipped, int? External) ReadCounts(JsonNode report, JsonObject floor)
{
    var summary = report["summary"]!;
    int passed = summary["passed"]!.GetValue<int>();
    int skipped = summary["skipped"]!.GetValue<int>();
    int? external = null;
    if (floor["externalDatabase"] is not null)
    {
        external = 0;
        if (report["groups"] is JsonArray groups)
        {
            foreach (JsonNode? group in groups)
            {
                if (group?["tests"] is not JsonArray tests) continue;
                foreach (JsonNode? test in tests)
                {
                    if (test is null) continue;
                    var properties = test["customProperties"] as JsonArray ?? [];
                    bool isExternal = properties.Any(p =>
                        p?["key"]?.GetValue<string>() == "Category"
                        && p["value"]?.GetValue<string>() == "ExternalDatabase");
                    if (isExternal && test["status"]?.GetValue<string>() == "passed")
                    {
                        external++;
                    }
                }
            }
        }
    }
    return (passed, skipped, external);
}

/// <summary>夹具：构造一份与 TUnit 报告同构的完整报告——assemblyName/timestamp/summary 各桶
/// 齐全且 total 算术一致。正是这种完整性让「字段缺失/算术不一致」类坏夹具可以逐项构造。</summary>
static JsonObject Fixture(string project, int passed, int skipped, int externalPassed)
{
    var tests = new JsonArray();
    for (int i = 0; i < externalPassed; i++)
    {
        tests.Add(new JsonObject
        {
            ["status"] = "passed",
            ["customProperties"] = new JsonArray(
                new JsonObject { ["key"] = "Category", ["value"] = "ExternalDatabase" }),
        });
    }
    tests.Add(new JsonObject { ["status"] = "passed", ["customProperties"] = new JsonArray() });
    int failed = 0;
    return new JsonObject
    {
        ["schemaVersion"] = 1,
        ["assemblyName"] = project,
        ["timestamp"] = DateTime.UtcNow.ToString("dd MMM yyyy, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
        ["summary"] = new JsonObject
        {
            ["total"] = passed + failed + skipped,
            ["passed"] = passed,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["cancelled"] = 0,
            ["timedOut"] = 0,
            ["flaky"] = 0,
        },
        ["groups"] = new JsonArray(new JsonObject { ["tests"] = tests }),
    };
}

static int SelfCheck()
{
    const string project = "PalORM.Core.Tests";
    JsonObject plainFloor = Parse("""{"passed":10}""");
    JsonObject externalFloor = Parse("""{"passed":10,"externalDatabase":2}""");

    // 坏夹具变体：在完整夹具上做单项破坏，逐项证明判据能失败
    JsonObject wrongAssembly = Fixture(project, 10, 0, 0);
    wrongAssembly["assemblyName"] = "PalORM.Other.Tests";
    JsonObject noAssembly = Fixture(project, 10, 0, 0);
    noAssembly.Remove("assemblyName");
    JsonObject staleReport = Fixture(project, 10, 0, 0);
    staleReport["timestamp"] = DateTime.UtcNow.AddHours(-(ReportFreshnessHours + 1))
        .ToString("dd MMM yyyy, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    JsonObject unparseableTimestamp = Fixture(project, 10, 0, 0);
    unparseableTimestamp["timestamp"] = "not-a-timestamp";
    JsonObject badArithmetic = Fixture(project, 10, 0, 0);
    badArithmetic["summary"]!["total"] = 99;
    JsonObject missingBucket = Fixture(project, 10, 0, 0);
    missingBucket["summary"]!.AsObject().Remove("timedOut");

    var cases = new List<(string Name, int Expected, int Actual)>
    {
        ("好夹具（passed=10 = 地板 10）", 0, Judge(Fixture(project, 10, 0, 0), plainFloor, project)),
        ("坏夹具①（passed=9 ≠ 地板 10）", 1, Judge(Fixture(project, 9, 0, 0), plainFloor, project)),
        ("坏夹具②（skipped=1）", 1, Judge(Fixture(project, 10, 1, 0), plainFloor, project)),
        ("好夹具②（真库 2 = 地板 2）", 0, Judge(Fixture(project, 10, 0, 2), externalFloor, project)),
        ("坏夹具③（真库 1 ≠ 地板 2）", 1, Judge(Fixture(project, 10, 0, 1), externalFloor, project)),
        ("坏夹具④（assemblyName = 别的项目）", 1, Judge(wrongAssembly, plainFloor, project)),
        ("坏夹具⑤（缺 assemblyName）", 1, Judge(noAssembly, plainFloor, project)),
        ("坏夹具⑥（时间戳超过 24 小时）", 1, Judge(staleReport, plainFloor, project)),
        ("坏夹具⑦（timestamp 无法解析）", 1, Judge(unparseableTimestamp, plainFloor, project)),
        ("坏夹具⑧（total 算术不一致）", 1, Judge(badArithmetic, plainFloor, project)),
        ("坏夹具⑨（summary 缺 timedOut 桶）", 1, Judge(missingBucket, plainFloor, project)),
    };

    bool ok = true;
    foreach ((string name, int expected, int actual) in cases)
    {
        bool behaved = expected == actual;
        ok &= behaved;
        Console.WriteLine($"  {(behaved ? "PASS" : "FAIL")} {name}：期望 exit {expected}，实际 exit {actual}");
    }
    if (ok)
    {
        Console.WriteLine("assert-test-counts self-check: PASS（好夹具 exit 0 / 坏夹具如期 exit 1）");
        return 0;
    }
    Console.Error.WriteLine("::error::assert-test-counts self-check: FAIL（判据行为与夹具期望不符）");
    return 1;
}

static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    Console.Error.WriteLine("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}

internal static class FamilyCatalog
{
    /// <summary>族 → 分类关键词。分类对象是报告的 className + displayName；
    /// 改关键词会移动族归属，地板必须随之显式调整。</summary>
    internal static readonly (string Key, Regex Pattern)[] Patterns =
    [
        ("concurrency", new Regex(@"Concurrency|Parallel|Pool|Soak", RegexOptions.IgnoreCase)),
        ("dialect", new Regex(@"Dialect", RegexOptions.IgnoreCase)),
        ("faultInjection", new Regex(@"Fault", RegexOptions.IgnoreCase)),
    ];
}
