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
// 用法：assert-test-counts.cs [--update] <project> <report.json>
// 陷阱（账本 M0-3）：不 grep stdout（编码/语言漂移）、不硬编码配置目录（从仓库根解析）。
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

bool update = args.Length > 0 && args[0] == "--update";
string[] rest = update ? args[1..] : args;
if (rest.Length < 2)
{
    Console.Error.WriteLine("usage: assert-test-counts.cs [--update] <project> <report.json>");
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
var summary = report["summary"]!;
var floors = JsonNode.Parse(File.ReadAllText(floorFile))!;
JsonObject? floor = floors[project] as JsonObject;
if (floor is null)
{
    Console.Error.WriteLine($"::error::地板文件缺少项目 {project} 的条目");
    return 1;
}

var passed = summary["passed"]!.GetValue<int>();
var skipped = summary["skipped"]!.GetValue<int>();
var floorPassed = floor["passed"]!.GetValue<int>();

int? external = null;
if (floor["externalDatabase"] is not null)
{
    external = 0;
    foreach (var group in report["groups"]!.AsArray())
    {
        foreach (var test in group!["tests"]!.AsArray())
        {
            var properties = test!["customProperties"]?.AsArray() ?? [];
            var isExternal = properties.Any(p =>
                p!["key"]!.GetValue<string>() == "Category" && p["value"]!.GetValue<string>() == "ExternalDatabase");
            if (isExternal && test["status"]!.GetValue<string>() == "passed")
            {
                external++;
            }
        }
    }
}

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

List<string> failures = [];
string externalText = external is int count ? $" externalDb={count}" : "";
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
