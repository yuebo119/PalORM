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
// --self-check（2026-11-04，测试系统全面优化方案 FIX-3）：内嵌夹具自证判据，不读任何外部文件。
// 理由见方案 §3.2：本脚本需要报告入参，从设计上进不了 V14 的「无参跑到底 + 完成标记」存活
// 监测面；自检模式让「脚本能跑」与「判据能失败」同时可判。好夹具必须 exit 0，三条坏夹具
// （少用例、有跳过、真库数不足）必须各自 exit 1，任一不符则自检自身 exit 1。
//
// 用法：assert-test-counts.cs [--update] <project> <report.json>
//       assert-test-counts.cs --self-check
// 陷阱（账本 M0-3）：不 grep stdout（编码/语言漂移）、不硬编码配置目录（从仓库根解析）。
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length == 1 && args[0] == "--self-check")
{
    return SelfCheck();
}

bool update = args.Length > 0 && args[0] == "--update";
string[] rest = update ? args[1..] : args;
if (rest.Length < 2)
{
    Console.Error.WriteLine("usage: assert-test-counts.cs [--update] <project> <report.json>");
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

// ── 判定与夹具（自检与正式路径共用同一实现，避免两套判据漂移）──

static int SelfCheck()
{
    JsonObject plainFloor = Parse("""{"passed":10}""");
    JsonObject externalFloor = Parse("""{"passed":10,"externalDatabase":2}""");

    var cases = new List<(string Name, int Expected, int Actual)>
    {
        ("好夹具（passed=10 = 地板 10）", 0, Judge(Fixture(10, 0, 0), plainFloor)),
        ("坏夹具①（passed=9 ≠ 地板 10）", 1, Judge(Fixture(9, 0, 0), plainFloor)),
        ("坏夹具②（skipped=1）", 1, Judge(Fixture(10, 1, 0), plainFloor)),
        ("好夹具②（真库 2 = 地板 2）", 0, Judge(Fixture(10, 0, 2), externalFloor)),
        ("坏夹具③（真库 1 ≠ 地板 2）", 1, Judge(Fixture(10, 0, 1), externalFloor)),
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

static int Judge(JsonNode report, JsonObject floor)
{
    (int passed, int skipped, int? external) = ReadCounts(report, floor);
    return Evaluate(passed, skipped, external, floor).Count == 0 ? 0 : 1;
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

// 夹具：summary 数字与 groups 里的外部库条目分开构造——正是这种「摘要与明细可背离」
// 让本门禁的两条判据互不代偿（只改 passed 不动 groups 时外部库口径仍会被判）。
static JsonObject Fixture(int passed, int skipped, int externalPassed)
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
    return new JsonObject
    {
        ["summary"] = new JsonObject { ["passed"] = passed, ["skipped"] = skipped },
        ["groups"] = new JsonArray(new JsonObject { ["tests"] = tests }),
    };
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
