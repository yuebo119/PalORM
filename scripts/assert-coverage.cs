// assert-coverage.cs（自 assert-coverage.sh + scripts/lib/parse-coverage.mjs 合并迁移，
// 脚本 C# 化整改方案 T2-3；Node 依赖随之消除）
// 覆盖率地板断言（防退化，非达标目标）：读 dotnet-coverage 的 XML。
//
// 两种模式：
//   ① 模块行覆盖（原有）：assert-coverage.cs <module 名片段> <coverage.xml> <地板文件键>
//   ② 类级分支覆盖（META-1）：assert-coverage.cs --branch <floor 键> <coverage.xml>
//      floor 条目形如 { "type": "QueryBuilder<T>", "line": 81.0, "branch": 86.0 }；
//      branch 必填、line 可选。XML 无 <class> 节点与 branch_coverage 属性——按 <function>
//      的 namespace+type_name 聚合、行数加权；"branch" 读 block_coverage（dotnet-coverage
//      的 block 覆盖是分支语义在本工具链的唯一可用代理，2026-10-31 实测口径）。
//
// 设计取舍：地板取当前实测 −2pp（防退化非达标）；追高数字在该项目为负价值。
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

bool branchMode = args.Length > 0 && args[0] == "--branch";
if (args.Length < 3)
{
    Console.Error.WriteLine(branchMode
        ? "usage: assert-coverage.cs --branch <floor-key> <coverage.xml>"
        : "usage: assert-coverage.cs <module-substring> <xml> <floor-key>");
    return 1;
}

string xmlPath = branchMode ? args[2] : args[1];
string key = branchMode ? args[1] : args[2];
if (!File.Exists(xmlPath))
{
    Console.Error.WriteLine($"::error::覆盖率文件不存在：{xmlPath}（未运行 --coverage 或路径错）");
    return 1;
}

var repoRoot = FindRepoRoot();
var floorFile = Path.Combine(repoRoot, "bench", "baselines", "test-counts.json");
var xml = File.ReadAllText(xmlPath);
var coverage = JsonNode.Parse(File.ReadAllText(floorFile))!["coverage"]!;

if (branchMode)
{
    JsonNode? entry = coverage["branch"]?[key];
    if (entry is null)
    {
        Console.Error.WriteLine($"::error::地板文件缺 coverage.branch.{key}");
        return 1;
    }
    string typeName = entry["type"]!.GetValue<string>();
    double branchFloor = entry["branch"]!.GetValue<double>();
    double? lineFloor = entry["line"]?.GetValue<double>();

    (double Line, double Branch, int Functions)? found = AggregateTypeCoverage(xml, typeName);
    if (found is null)
    {
        Console.Error.WriteLine($"::error::XML 中未找到类型 {typeName} 的 function 覆盖数据");
        return 1;
    }

    Console.WriteLine(
        $"{key} ({typeName}, {found.Value.Functions} funcs): line={found.Value.Line.ToString(CultureInfo.InvariantCulture)}% "
        + $"branch(block)={found.Value.Branch.ToString(CultureInfo.InvariantCulture)}% "
        + $"(地板 line {lineFloor?.ToString(CultureInfo.InvariantCulture) ?? "-"}% / branch {branchFloor.ToString(CultureInfo.InvariantCulture)}%)");
    if (found.Value.Branch < branchFloor)
    {
        Console.Error.WriteLine(
            $"::error::{typeName} 分支(block)覆盖 {found.Value.Branch.ToString(CultureInfo.InvariantCulture)}% < 地板 {branchFloor.ToString(CultureInfo.InvariantCulture)}%");
        return 1;
    }
    if (lineFloor is double line && found.Value.Line < line)
    {
        Console.Error.WriteLine(
            $"::error::{typeName} 行覆盖 {found.Value.Line.ToString(CultureInfo.InvariantCulture)}% < 地板 {line.ToString(CultureInfo.InvariantCulture)}%");
        return 1;
    }
    return 0;
}

string moduleSub = args[0];
var regex = new Regex("<module [^>]*name=\"([^\"]+)\"[^>]*line_coverage=\"([0-9.]+)\"");
double? moduleCoverage = null;
foreach (Match match in regex.Matches(xml))
{
    if (match.Groups[1].Value.Contains(moduleSub, StringComparison.Ordinal))
    {
        moduleCoverage = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        break;
    }
}
if (moduleCoverage is null)
{
    Console.Error.WriteLine($"::error::XML 中未找到模块 {moduleSub}");
    return 1;
}

JsonNode? floorNode = coverage[key];
if (floorNode is null)
{
    Console.Error.WriteLine($"::error::地板文件缺 coverage.{key}");
    return 1;
}
if (floorNode is JsonObject obj && obj["line"] is not null)
{
    floorNode = obj["line"];
}
if (floorNode is not JsonValue)
{
    Console.Error.WriteLine($"::error::coverage.{key} 不是数值（分支地板请用 --branch 模式）");
    return 1;
}
var floor = floorNode.GetValue<double>();

var foundText = moduleCoverage.Value.ToString(CultureInfo.InvariantCulture);
var floorText = floor.ToString(CultureInfo.InvariantCulture);
Console.WriteLine($"{key}: line_coverage={foundText}% (地板 {floorText}%)");
if (moduleCoverage < floor)
{
    Console.Error.WriteLine($"::error::覆盖率 {foundText}% < 地板 {floorText}%");
    return 1;
}
return 0;

/// <summary>按 type_name 聚合 function 覆盖（行数加权）。
/// dotnet-coverage XML 无 class 节点与 branch_coverage 属性，block_coverage 即分支代理。</summary>
static (double Line, double Branch, int Functions)? AggregateTypeCoverage(string xml, string typeName)
{
    // XML 里泛型参数被转义（QueryBuilder&lt;T&gt;），type 名按转义后的字面量匹配
    double weightedLine = 0, weightedBlock = 0;
    int functions = 0, totalLines = 0;
    foreach (Match tag in Regex.Matches(xml, "<function [^>]*>"))
    {
        Match type = Regex.Match(tag.Value, "type_name=\"([^\"]*)\"");
        if (!type.Success || !string.Equals(type.Groups[1].Value, typeName, StringComparison.Ordinal))
            continue;
        double line = ParsePercent(tag.Value, "line_coverage");
        double block = ParsePercent(tag.Value, "block_coverage");
        int lines = ParseCount(tag.Value, "lines_covered")
            + ParseCount(tag.Value, "lines_partially_covered")
            + ParseCount(tag.Value, "lines_not_covered");
        if (lines == 0) lines = 1;
        weightedLine += line * lines;
        weightedBlock += block * lines;
        totalLines += lines;
        functions++;
    }
    if (functions == 0) return null;
    return (weightedLine / totalLines, weightedBlock / totalLines, functions);
}

static double ParsePercent(string tag, string attribute)
{
    Match match = Regex.Match(tag, attribute + "=\"([0-9.]+)\"");
    return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
}

static int ParseCount(string tag, string attribute)
{
    Match match = Regex.Match(tag, attribute + "=\"([0-9]+)\"");
    return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
}

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