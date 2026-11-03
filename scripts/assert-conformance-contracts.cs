// assert-conformance-contracts.cs（2026-11-03）
// 契约覆盖账本门禁：把「哪些契约必须存在、SQLite 侧必须全跑」从测试方法内部搬到静态数据。
//
// 要解决的问题：Sqlite_CapabilityContracts_Hold 的两条守卫写在方法体内，整方法被删守卫随之
// 消失；而 assert-test-counts 的 passed 地板对单条删除无感（Integration 地板 286 对实测 574）。
// 本脚本不看测试方法，只看两个契约目录的源文件与豁免表，因此方法改名、搬家、重构都不影响判定：
//   ① 目录里的 ID 集合必须与账本逐项相等（删条目、加条目未登记都报）
//   ② DialectUnsupported 里不得出现 SqlDialect.Sqlite（SQLite 侧必须全跑）
//   ③ 每条目至少一个 Assert.That（防条目留着、断言被掏空）
//
// 退出码：0 通过 / 1 有 FAIL / 2 用法或环境错。
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

string repoRoot = FindRepoRoot();
string baselinePath = Path.Combine(repoRoot, "bench", "baselines", "conformance-contracts.json");
if (!File.Exists(baselinePath))
{
    Console.Error.WriteLine($"::error::账本不存在：{baselinePath}（本断言不接受'找不到账本即放行'）");
    return 2;
}

JsonNode baseline = JsonNode.Parse(File.ReadAllText(baselinePath))!;
List<string> failures = [];

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

    Console.WriteLine($"{key}: {actual.Count} 条（账本 {expectedCount}）");
}

{
    JsonNode node = baseline["sqliteExemptions"]!;
    string relative = node["file"]!.GetValue<string>();
    string className = node["className"]!.GetValue<string>();
    int allowed = node["allowed"]!.GetValue<int>();
    string full = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(full))
    {
        failures.Add($"sqliteExemptions: 源文件不存在 {relative}");
    }
    else
    {
        int count = CountSqliteExemptions(File.ReadAllText(full), className);
        Console.WriteLine($"sqliteExemptions: {count} 条（允许 {allowed}）");
        if (count > allowed)
            failures.Add($"sqliteExemptions: {className} 里有 {count} 条 SqlDialect.Sqlite，允许 {allowed}——SQLite 侧将失去这些契约的覆盖");
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

static int CountSqliteExemptions(string source, string className)
{
    int classIndex = source.IndexOf("class " + className, StringComparison.Ordinal);
    if (classIndex < 0) return 0;
    int braceStart = source.IndexOf('{', classIndex);
    if (braceStart < 0) return 0;
    int depth = 0;
    int braceEnd = braceStart;
    for (int i = braceStart; i < source.Length; i++)
    {
        if (source[i] == '{') depth++;
        else if (source[i] == '}')
        {
            depth--;
            if (depth == 0) { braceEnd = i; break; }
        }
    }
    return Regex.Matches(source[braceStart..(braceEnd + 1)], "SqlDialect\\.Sqlite").Count;
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
