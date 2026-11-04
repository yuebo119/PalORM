// assert-coverage.cs（自 assert-coverage.sh + scripts/lib/parse-coverage.mjs 合并迁移，
// 脚本 C# 化整改方案 T2-3；Node 依赖随之消除）
// 覆盖率地板断言（防退化，非达标目标）：读 dotnet-coverage 的 XML。
//
// 三种模式：
//   ① 模块行覆盖（原有）：assert-coverage.cs <module 名片段> <coverage.xml> <地板文件键>
//   ② 类级分支覆盖（META-1）：assert-coverage.cs --branch <floor 键> <coverage.xml>
//      floor 条目形如 { "type": "QueryBuilder<T>", "line": 81.0, "branch": 86.0 }；
//      branch 必填、line 可选。XML 无 <class> 节点与 branch_coverage 属性——按 <function>
//      的 namespace+type_name 聚合、行数加权；"branch" 读 block_coverage（dotnet-coverage
//      的 block 覆盖是分支语义在本工具链的唯一可用代理，2026-10-31 实测口径）。
//   ③ 自检（FIX-3，2026-11-04）：assert-coverage.cs --self-check
//      内嵌好/坏夹具跑同一套判据，不读任何外部文件。补这条的理由是 V14 存活监测面只收
//      「无参跑到底 + 完成标记」的脚本，本脚本需要 XML 入参，从设计上进不了监测面
//      （方案 §3.2）；坏夹具必须 exit 1 才算证明判据没被掏空。
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

// 覆盖率随补测自然上升，地板若长期不抬就会变成"只降不升"的松地板。上限 1pp，
// 超过即要求把地板抬到实测值。容差而非精确匹配，因为覆盖率会随代码增删小幅波动。
const double MaxSlackPercentagePoints = 1.0;

if (args.Length == 1 && args[0] == "--self-check")
{
    return SelfCheck(MaxSlackPercentagePoints);
}

bool branchMode = args.Length > 0 && args[0] == "--branch";
if (args.Length < 3)
{
    Console.Error.WriteLine(branchMode
        ? "usage: assert-coverage.cs --branch <floor-key> <coverage.xml>"
        : "usage: assert-coverage.cs <module-substring> <xml> <floor-key>");
    Console.Error.WriteLine("       assert-coverage.cs --self-check");
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

// GATE-3（2026-11-04）：空产物防线——无任何 range 记录的 XML 视为残缺，拒绝判过。
// 上传产物截断/空文件这类事故不能落到"未找到模块"的笼统报错里，要有专属消息。
if (!Regex.Matches(xml, "<range ").Any())
{
    Console.Error.WriteLine("::error::覆盖率 XML 为空或残缺（0 条 range 记录）——拒绝把空产物当成通过");
    return 1;
}

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

    (List<string> failures, string message) = JudgeBranch(xml, typeName, branchFloor, lineFloor, key);
    if (message.Length > 0) Console.WriteLine(message);
    if (failures.Count > 0)
    {
        foreach (string failure in failures) Console.Error.WriteLine($"::error::{failure}");
        return 1;
    }
    return 0;
}

string moduleSub = args[0];
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
double floor = floorNode.GetValue<double>();

(List<string> moduleFailures, string moduleMessage) =
    JudgeModule(xml, moduleSub, floor, key, MaxSlackPercentagePoints);
Console.WriteLine(moduleMessage);
if (moduleFailures.Count > 0)
{
    foreach (string failure in moduleFailures) Console.Error.WriteLine($"::error::{failure}");
    return 1;
}
return 0;

// ── 判定（自检与正式路径共用）──

static (List<string> Failures, string Message) JudgeModule(
    string xml, string moduleSub, double floor, string key, double maxSlackPercentagePoints)
{
    double? moduleCoverage = FindModuleCoverage(xml, moduleSub);
    if (moduleCoverage is null)
    {
        return ([$"XML 中未找到模块 {moduleSub}"], "");
    }

    List<string> failures = [];
    string foundText = moduleCoverage.Value.ToString(CultureInfo.InvariantCulture);
    string floorText = floor.ToString(CultureInfo.InvariantCulture);
    string message = $"{key}: line_coverage={foundText}% (地板 {floorText}%)";
    if (moduleCoverage < floor)
    {
        failures.Add($"覆盖率 {foundText}% < 地板 {floorText}%");
    }

    // 自收紧上限（2026-11-03，《测试机制重设计方案》P2-c 的覆盖率侧）
    double slack = moduleCoverage.Value - floor;
    if (slack > maxSlackPercentagePoints)
    {
        string suggested = Math.Floor(moduleCoverage.Value).ToString(CultureInfo.InvariantCulture);
        failures.Add(
            $"覆盖率 {foundText}% 比地板 {floorText}% 高 {slack.ToString("0.##", CultureInfo.InvariantCulture)}pp，"
            + $"超过自收紧上限 {maxSlackPercentagePoints.ToString(CultureInfo.InvariantCulture)}pp——请把 coverage.{key} 抬到 {suggested}");
    }
    return (failures, message);
}

static (List<string> Failures, string Message) JudgeBranch(
    string xml, string typeName, double branchFloor, double? lineFloor, string key)
{
    (double Line, double Branch, int Functions)? found = AggregateTypeCoverage(xml, typeName);
    if (found is null)
    {
        return ([$"XML 中未找到类型 {typeName} 的 function 覆盖数据"], "");
    }

    string message =
        $"{key} ({typeName}, {found.Value.Functions} funcs): line={found.Value.Line.ToString(CultureInfo.InvariantCulture)}% "
        + $"branch(block)={found.Value.Branch.ToString(CultureInfo.InvariantCulture)}% "
        + $"(地板 line {lineFloor?.ToString(CultureInfo.InvariantCulture) ?? "-"}% / branch {branchFloor.ToString(CultureInfo.InvariantCulture)}%)";

    List<string> failures = [];
    if (found.Value.Branch < branchFloor)
    {
        failures.Add($"{typeName} 分支(block)覆盖 {found.Value.Branch.ToString(CultureInfo.InvariantCulture)}% < 地板 {branchFloor.ToString(CultureInfo.InvariantCulture)}%");
    }
    if (lineFloor is double line && found.Value.Line < line)
    {
        failures.Add($"{typeName} 行覆盖 {found.Value.Line.ToString(CultureInfo.InvariantCulture)}% < 地板 {line.ToString(CultureInfo.InvariantCulture)}%");
    }
    return (failures, message);
}

static double? FindModuleCoverage(string xml, string moduleSub)
{
    var regex = new Regex("<module [^>]*name=\"([^\"]+)\"[^>]*line_coverage=\"([0-9.]+)\"");
    foreach (Match match in regex.Matches(xml))
    {
        if (match.Groups[1].Value.Contains(moduleSub, StringComparison.Ordinal))
        {
            return double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        }
    }
    return null;
}

// ── 自检夹具 ──

static int SelfCheck(double maxSlackPercentagePoints)
{
    string goodModule = ModuleXml("PalORM.Core.dll", 80.0);
    string lowModule = ModuleXml("PalORM.Core.dll", 74.0);
    string highModule = ModuleXml("PalORM.Core.dll", 82.0);

    var cases = new List<(string Name, int Expected, int Actual)>
    {
        ("好夹具（模块 80.0% / 地板 79.5%）", 0,
            JudgeModule(goodModule, "PalORM.Core", 79.5, "core", maxSlackPercentagePoints).Failures.Count == 0 ? 0 : 1),
        ("坏夹具①（模块 74.0% < 地板 75%）", 1,
            JudgeModule(lowModule, "PalORM.Core", 75.0, "core", maxSlackPercentagePoints).Failures.Count == 0 ? 0 : 1),
        ("坏夹具②（模块 82.0% 高于地板 3pp，超自收紧上限）", 1,
            JudgeModule(highModule, "PalORM.Core", 79.0, "core", maxSlackPercentagePoints).Failures.Count == 0 ? 0 : 1),
        ("坏夹具③（XML 无该模块）", 1,
            JudgeModule(goodModule, "NoSuch.Module", 50.0, "core", maxSlackPercentagePoints).Failures.Count == 0 ? 0 : 1),
        ("好夹具②（QueryBuilder&lt;T&gt; 分支 88% / 地板 86%）", 0,
            JudgeBranch(FunctionXml("QueryBuilder&lt;T&gt;", line: 90.0, block: 88.0, lines: 10),
                "QueryBuilder&lt;T&gt;", branchFloor: 86.0, lineFloor: null, key: "queryBuilder").Failures.Count == 0 ? 0 : 1),
        ("坏夹具④（分支 50% < 地板 80%）", 1,
            JudgeBranch(FunctionXml("QueryBuilder&lt;T&gt;", line: 90.0, block: 50.0, lines: 10),
                "QueryBuilder&lt;T&gt;", branchFloor: 80.0, lineFloor: null, key: "queryBuilder").Failures.Count == 0 ? 0 : 1),
        ("坏夹具⑤（分支达标但行覆盖 40% < 行地板 81%）", 1,
            JudgeBranch(FunctionXml("BatchUpdateSqlBuilder", line: 40.0, block: 90.0, lines: 10),
                "BatchUpdateSqlBuilder", branchFloor: 80.0, lineFloor: 81.0, key: "batchUpdateSqlBuilder").Failures.Count == 0 ? 0 : 1),
        ("坏夹具⑥（覆盖率 XML 残缺：0 条 range）", 1,
            JudgeModule(RangelessModuleXml("PalORM.Core.dll", 80.0), "PalORM.Core", 50.0, "core", maxSlackPercentagePoints).Failures.Count == 0 ? 0 : 1),
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
        Console.WriteLine("assert-coverage self-check: PASS（好夹具 exit 0 / 坏夹具如期 exit 1）");
        return 0;
    }
    Console.Error.WriteLine("::error::assert-coverage self-check: FAIL（判据行为与夹具期望不符）");
    return 1;
}

static string ModuleXml(string name, double lineCoverage)
    => "<coverage><module name=\"" + name + "\" block_coverage=\"0\" line_coverage=\""
       + lineCoverage.ToString(CultureInfo.InvariantCulture) + "\">"
       + "<range source_id=\"1\" start_line=\"1\" end_line=\"10\" covered=\"yes\" />"
       + "</module></coverage>";

// GATE-3 夹具：有 module 却零 range 的残缺产物（模拟上传截断/空文件）
static string RangelessModuleXml(string name, double lineCoverage)
    => "<coverage><module name=\"" + name + "\" block_coverage=\"0\" line_coverage=\""
       + lineCoverage.ToString(CultureInfo.InvariantCulture) + "\" /></coverage>";

static string FunctionXml(string typeName, double line, double block, int lines)
    => "<coverage><module name=\"x\"><functions><function type_name=\"" + typeName
       + "\" line_coverage=\"" + line.ToString(CultureInfo.InvariantCulture)
       + "\" block_coverage=\"" + block.ToString(CultureInfo.InvariantCulture)
       + "\" lines_covered=\"" + lines.ToString(CultureInfo.InvariantCulture)
       + "\" /></functions></module></coverage>";

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
