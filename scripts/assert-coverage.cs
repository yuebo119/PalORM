// assert-coverage.cs（自 assert-coverage.sh + scripts/lib/parse-coverage.mjs 合并迁移，
// 脚本 C# 化整改方案 T2-3；Node 依赖随之消除）
// 覆盖率地板断言（防退化，非达标目标）：读 dotnet-coverage 的 XML
// （results/module 的 line_coverage 属性），断言 ≥ 地板。
// 调用：assert-coverage.cs <module 名片段> <coverage.xml> <地板文件键>
// 设计取舍（报告原方案 line≥70/branch≥60 的修正）：地板取当前实测 −5 防退化；
// 追高数字在该项目为负价值（645+ 测试 0 无断言，错误路径充分）。
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: assert-coverage.cs <module-substring> <xml> <floor-key>");
    return 1;
}
var moduleSub = args[0];
var xmlPath = args[1];
var key = args[2];

if (!File.Exists(xmlPath))
{
    Console.Error.WriteLine($"::error::覆盖率文件不存在：{xmlPath}（未运行 --coverage 或路径错）");
    return 1;
}

var repoRoot = FindRepoRoot();
var floorFile = Path.Combine(repoRoot, "bench", "baselines", "test-counts.json");

var xml = File.ReadAllText(xmlPath);
var regex = new Regex("<module [^>]*name=\"([^\"]+)\"[^>]*line_coverage=\"([0-9.]+)\"");
double? found = null;
foreach (Match match in regex.Matches(xml))
{
    if (match.Groups[1].Value.Contains(moduleSub, StringComparison.Ordinal))
    {
        found = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        break;
    }
}
if (found is null)
{
    Console.Error.WriteLine($"::error::XML 中未找到模块 {moduleSub}");
    return 1;
}

var coverage = JsonNode.Parse(File.ReadAllText(floorFile))!["coverage"]!;
var floorNode = coverage[key];
if (floorNode is null)
{
    Console.Error.WriteLine($"::error::地板文件缺 coverage.{key}");
    return 1;
}
var floor = floorNode.GetValue<double>();

var foundText = found.Value.ToString(CultureInfo.InvariantCulture);
var floorText = floor.ToString(CultureInfo.InvariantCulture);
Console.WriteLine($"{key}: line_coverage={foundText}% (地板 {floorText}%)");
if (found < floor)
{
    Console.Error.WriteLine($"::error::覆盖率 {foundText}% < 地板 {floorText}%");
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
