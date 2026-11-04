// assert-conformance-contracts.cs（2026-11-03）
// 契约覆盖账本门禁，双模式：
//   ① 源码侧（默认，无参）：解析两个契约目录，核 ID 集合 / 条数 / 豁免表 / 每条目断言数。
//      与测试方法是否存在无关，防「条目被删、被登记豁免、被掏空」。进 pre-commit。
//   ② 报告侧（--report <report.json>）：核报告里实际出现的契约 ID 与次数。
//      防「条目还在源码里但没跑」（被过滤、方法被删、数据源变空）。进 CI integration job。
// 两模式互补：源码侧证明「声明存在」，报告侧证明「实际执行」。
//
// 退出码：0 通过 / 1 有 FAIL / 2 用法或环境错。
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

string? reportPath = null;
bool selfCheck = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--report" && i + 1 < args.Length)
    {
        reportPath = args[++i];
    }
    else if (args[i] == "--self-check")
    {
        selfCheck = true;
    }
    else
    {
        Console.Error.WriteLine("usage: assert-conformance-contracts.cs [--report <report.json>] | --self-check");
        return 2;
    }
}

// --self-check（2026-11-04，GATE-1）：内嵌夹具自证 ID 提取与断言计数判据——
// 含两条正确条目的合成源必须提取 2 条 ID 且断言数 ≥1；空断言的条目必须被计数逻辑捕获。
if (selfCheck)
{
    return SelfCheck();
}

string repoRoot = FindRepoRoot();
string baselinePath = Path.Combine(repoRoot, "bench", "baselines", "conformance-contracts.json");
if (!File.Exists(baselinePath))
{
    Console.Error.WriteLine($"::error::账本不存在：{baselinePath}（本断言不接受'找不到账本即放行'）");
    return 2;
}

JsonNode baseline = JsonNode.Parse(File.ReadAllText(baselinePath))!;
List<string> failures = [];
var expectedCounts = new Dictionary<string, int>(StringComparer.Ordinal);

foreach (string key in new[] { "capabilityCatalog", "dialectCatalog" })
{
    JsonNode node = baseline[key]!;
    string relative = node["file"]!.GetValue<string>();
    string prefix = node["prefix"]!.GetValue<string>();
    int expectedCount = node["count"]!.GetValue<int>();
    int minAssertions = node["minAssertionsPerEntry"]?.GetValue<int>() ?? 1;
    List<string> expected = [.. node["ids"]!.AsArray().Select(x => x!.GetValue<string>())];

    string full = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(full))
    {
        failures.Add($"{key}: 源文件不存在 {relative}");
        continue;
    }

    string source = File.ReadAllText(full);
    List<string> actual = ExtractIds(source, prefix);

    if (actual.Count != expectedCount)
        failures.Add($"{key}: 条数 {actual.Count} ≠ 账本 {expectedCount}");
    List<string> missing = [.. expected.Except(actual, StringComparer.Ordinal)];
    List<string> extra = [.. actual.Except(expected, StringComparer.Ordinal)];
    if (missing.Count > 0)
        failures.Add($"{key}: 账本有、源缺失 {string.Join(", ", missing)}（契约条目被删？）");
    if (extra.Count > 0)
        failures.Add($"{key}: 源有、账本缺 {string.Join(", ", extra)}（新增条目未登记账本）");

    foreach ((string id, int asserts) in CountAssertions(source, prefix))
    {
        if (asserts < minAssertions)
            failures.Add($"{key}: {id} 只有 {asserts} 条断言（下限 {minAssertions}，条目被掏空？）");
    }

    // 报告的期望次数：能力契约每条跑 1 次（仅 SQLite）；方言契约每条跑 3 次（三方言）。
    int timesPerContract = node["timesPerContract"]?.GetValue<int>() ?? 1;
    foreach (string id in expected)
        expectedCounts[id] = timesPerContract;

    Console.WriteLine($"{key}: {actual.Count} 条（账本 {expectedCount}），每条期望执行 {timesPerContract} 次");
}

string exemptionFile = baseline["sqliteExemptions"]!["file"]!.GetValue<string>();
string exemptionClass = baseline["sqliteExemptions"]!["className"]!.GetValue<string>();
int allowedExemptions = baseline["sqliteExemptions"]!["allowed"]!.GetValue<int>();
string exemptionFull = Path.Combine(repoRoot, exemptionFile.Replace('/', Path.DirectorySeparatorChar));
int totalExemptions = 0;
if (!File.Exists(exemptionFull))
{
    failures.Add($"sqliteExemptions: 源文件不存在 {exemptionFile}");
}
else
{
    string block = ClassBlock(File.ReadAllText(exemptionFull), exemptionClass);
    int sqliteExemptions = Regex.Matches(block, "SqlDialect\\.Sqlite").Count;
    totalExemptions = Regex.Matches(block, "SqlDialect\\.").Count;
    Console.WriteLine($"sqliteExemptions: {sqliteExemptions} 条（允许 {allowedExemptions}）");
    if (sqliteExemptions > allowedExemptions)
        failures.Add($"sqliteExemptions: {exemptionClass} 里有 {sqliteExemptions} 条 SqlDialect.Sqlite，允许 {allowedExemptions}——SQLite 侧将失去这些契约的覆盖");
}

if (reportPath is not null)
{
    if (!File.Exists(reportPath))
    {
        failures.Add($"报告不存在：{reportPath}（本断言不接受'找不到报告即放行'）");
    }
    else if (totalExemptions > 0)
    {
        // 豁免会让报告次数少于固定值；先清理豁免表或扩展本检查，不做静默放宽。
        failures.Add($"报告模式暂不支持方言豁免（{exemptionClass} 现有 {totalExemptions} 条），请先清理或扩展本检查");
    }
    else
    {
        var actualCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(File.ReadAllText(reportPath), "\"displayName\"\\s*:\\s*\"([^\"]*)\""))
        {
            foreach (Match id in Regex.Matches(m.Groups[1].Value, "[CD]-[A-Z0-9]+(?:-[A-Z0-9]+)*"))
            {
                actualCounts[id.Value] = actualCounts.GetValueOrDefault(id.Value) + 1;
            }
        }

        int executed = 0;
        foreach ((string id, int expectedTimes) in expectedCounts)
        {
            int times = actualCounts.GetValueOrDefault(id);
            executed += times;
            if (times != expectedTimes)
                failures.Add($"报告侧：{id} 执行 {times} 次，期望 {expectedTimes} 次（契约在源码里但没跑？）");
        }

        List<string> unknown = [.. actualCounts.Keys.Where(id => !expectedCounts.ContainsKey(id))];
        if (unknown.Count > 0)
            failures.Add($"报告侧：出现账本外的契约 ID {string.Join(", ", unknown)}（新增未登记账本）");

        Console.WriteLine($"报告侧：契约执行 {executed} 次（期望 {expectedCounts.Values.Sum()} 次）");
    }
}

if (failures.Count > 0)
{
    foreach (string failure in failures)
        Console.Error.WriteLine($"::error::{failure}");
    return 1;
}

Console.WriteLine("契约覆盖账本一致");
return 0;

static int SelfCheck()
{
    const string goodSource = """
        var catalog = new[]
        {
            new("C-TEST-01", "description", () =>
            {
                await Assert.That(1).IsEqualTo(1);
            }),
            new("C-TEST-02", "description", () =>
            {
                await Assert.That(2).IsEqualTo(2);
            }),
        };
        """;
    const string emptyAssertSource = """
        var catalog = new[]
        {
            new("C-TEST-01", "description", () => { }),
        };
        """;

    var goodIds = ExtractIds(goodSource, "C-");
    int goodCount = goodIds.Count;
    bool goodExtracted = goodCount == 2 && goodIds[0] == "C-TEST-01" && goodIds[1] == "C-TEST-02";

    var goodAssertions = CountAssertions(goodSource, "C-").ToList();
    bool goodAssertionsOk = goodAssertions.All(a => a.Assertions >= 1);

    var emptyAssertions = CountAssertions(emptyAssertSource, "C-").ToList();
    bool emptyCaught = emptyAssertions.Any(a => a.Assertions < 1);

    Console.WriteLine($"  {(goodExtracted ? "PASS" : "FAIL")} 好源提取 2 条 ID（C-TEST-01/02）");
    Console.WriteLine($"  {(goodAssertionsOk ? "PASS" : "FAIL")} 好源每条断言数 ≥1");
    Console.WriteLine($"  {(emptyCaught ? "PASS" : "FAIL")} 空断言条目被捕获（坏夹具）");
    if (goodExtracted && goodAssertionsOk && emptyCaught)
    {
        Console.WriteLine("assert-conformance-contracts self-check: PASS（ID 提取与断言计数判据可失败、不误报）");
        return 0;
    }
    Console.Error.WriteLine("::error::assert-conformance-contracts self-check: FAIL");
    return 1;
}

static List<string> ExtractIds(string source, string prefix)
{
    MatchCollection matches = Regex.Matches(source, "new\\(\\s*\"(" + prefix + "[A-Z0-9-]+)\"");
    return [.. matches.Select(m => m.Groups[1].Value)];
}

static IEnumerable<(string Id, int Assertions)> CountAssertions(string source, string prefix)
{
    MatchCollection matches = Regex.Matches(source, "new\\(\\s*\"(" + prefix + "[A-Z0-9-]+)\"");
    for (int i = 0; i < matches.Count; i++)
    {
        int start = matches[i].Index;
        int end;
        if (i + 1 < matches.Count)
        {
            end = matches[i + 1].Index;
        }
        else
        {
            // 末条：截到集合表达式结束 "];"，否则会把后面的执行器代码算进来（其断言会掩盖空条目）
            Match listEnd = Regex.Match(source[start..], "\\n\\s*\\];");
            end = listEnd.Success ? start + listEnd.Index : source.Length;
        }
        string block = source[start..end];
        int assertions = Regex.Matches(block, "await Assert\\.").Count;
        yield return (matches[i].Groups[1].Value, assertions);
    }
}

static string ClassBlock(string source, string className)
{
    int classIndex = source.IndexOf("class " + className, StringComparison.Ordinal);
    if (classIndex < 0) return "";
    int braceStart = source.IndexOf('{', classIndex);
    if (braceStart < 0) return "";
    int depth = 0;
    for (int i = braceStart; i < source.Length; i++)
    {
        if (source[i] == '{') depth++;
        else if (source[i] == '}')
        {
            depth--;
            if (depth == 0) return source[braceStart..(i + 1)];
        }
    }
    return source[braceStart..];
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }
    Console.Error.WriteLine("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}
