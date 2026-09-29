// assert-test-counts.cs（自 assert-test-counts.sh 迁移，脚本 C# 化整改方案 T2-2；
// 一并消除其内联 node -e 依赖）
// CI"确实执行了"断言：① passed ≥ 地板（bench/baselines/test-counts.json 唯一真源）
// ② skipped == 0（无跳过机制的套件出现跳过 = 静默降级）
// ③（仅 Integration）ExternalDatabase 通过数 ≥ 地板（真库面不许静默消失）
// 陷阱（账本 M0-3）：不 grep stdout（编码/语言漂移）、不硬编码配置目录（从仓库根解析）。
using System.Text;
using System.Text.Json.Nodes;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: assert-test-counts.cs <project> <report.json>");
    return 1;
}
var project = args[0];
var reportPath = args[1];

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
var floor = floors[project];
if (floor is null)
{
    Console.Error.WriteLine($"::error::地板文件缺少项目 {project} 的条目");
    return 1;
}

var passed = summary["passed"]!.GetValue<int>();
var skipped = summary["skipped"]!.GetValue<int>();
var floorPassed = floor["passed"]!.GetValue<int>();

List<string> failures = [];
if (passed < floorPassed)
{
    failures.Add($"passed {passed} < 地板 {floorPassed}（用例被删除或未运行？）");
}
if (skipped != 0)
{
    failures.Add($"skipped {skipped} ≠ 0（无跳过机制的套件出现跳过 = 静默降级）");
}

if (floor["externalDatabase"] is not null)
{
    var external = 0;
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
    var floorExternal = floor["externalDatabase"]!.GetValue<int>();
    if (external < floorExternal)
    {
        failures.Add($"ExternalDatabase 通过数 {external} < 地板 {floorExternal}（真库用例静默消失？）");
    }
    Console.WriteLine($"{project}: passed={passed} skipped={skipped} externalDb={external}");
}
else
{
    Console.WriteLine($"{project}: passed={passed} skipped={skipped}");
}

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
