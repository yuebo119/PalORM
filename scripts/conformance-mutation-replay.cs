// conformance-mutation-replay.cs
// 一致性套件的产品侧变异回放（方案 §4.8 / 附录 B 第 6 项；D1-D7 回放见《测试体系增强方案-2026-10-30》§5.1）。
//
// 对每条规则执行四步：注入缺陷 -> 构建 -> 跑目标测试（期望报红）-> git 还原 -> 构建 -> 跑同一组测试（期望转绿）。
// 判定标准是"注入后红 且 还原后绿"，任一不符即 exit 1。注入内容均为硬替换，不用环境变量开关：
// 源生成器读取环境变量会被 Roslyn RS1035 拦下（分析器禁止读环境变量）。
//
// 规则是数据，不是代码（2026-11-04 MUT-1，《测试系统全面优化方案》）：注入点、原片段、
// 替换片段、目标过滤器、期望契约全部在 bench/baselines/mutation-rules.json。本脚本只做
// 注入/构建/判定。加规则的流程因此变成「往 JSON 加一条 + 跑 --verify-anchors + 跑该条」，
// 不需要改脚本。登记了但不能实施的条目（如 D-TX-01/02）落在 registeredNotImplemented，
// 显式留在表内而不是从表里消失。
//
// 规则可指定自己的测试项目（R-D5 的 PALORM054 检测测试在 test/PalORM.SourceGen.Tests）；
// 未指定时用 defaultTestProject。多行片段按工作树实际行尾适配（仓库索引 LF，工作树受
// core.autocrlf 影响可能是 CRLF）。
//
// R-C 系列（方案《测试机制重设计方案-2026-11-03》§6.2 降级路径）：变异分（Stryker）自 2026-10-03 起
// 产出 0 个有效变异，"整体判别力"无基线可比；退一步只证明「契约这一族有判别力」——往产品代码注入
// 已知缺陷，要求指定契约 ID 的用例报红。期望契约由规则里的 expectedContracts 声明：报红名单里没有它，
// 即使退出码非零也判 FAIL（别的原因红了不构成本契约的判别力证据）。
//
// 前置条件（执行模式）：工作树必须清洁（脚本会用 git checkout 还原被改文件，脏树会丢改动）。
// 用法：dotnet run --file scripts/conformance-mutation-replay.cs [-- --rule <ID 前缀>]
//       dotnet run --file scripts/conformance-mutation-replay.cs -- --verify-anchors
//       dotnet run --file scripts/conformance-mutation-replay.cs -- --list
//   --rule R-C        只跑契约族规则（迭代时省去历史规则的重建）
//   --verify-anchors  只核对每条规则的 Original 片段能否在当前源码里解析（秒级，改源码后先跑这个）
//   --list            打印规则清单（ID / 文件 / 过滤器 / 期望契约）
// 输出末尾带 Markdown 台账（规则 / 注入点 / 报红测试数 / 报红测试名），供总报告登记。
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

string repoRoot = FindRepoRoot();
string dotnet = ResolveDotnet();

string rulesPath = Path.Combine(repoRoot, "bench", "baselines", "mutation-rules.json");
if (!File.Exists(rulesPath))
{
    Console.Error.WriteLine($"::error::规则表不存在：{rulesPath}");
    return 2;
}

JsonNode document = JsonNode.Parse(File.ReadAllText(rulesPath))!;
string defaultProject = document["defaultTestProject"]?.GetValue<string>() ?? "test/PalORM.Integration.Tests";
Rule[] rules = [.. document["rules"]!.AsArray().Select(ParseRule)];
JsonArray? registered = document["registeredNotImplemented"] as JsonArray;
int registeredCount = registered?.Count ?? 0;

// 规则 ID 唯一性是数据表的基本约束：重复 ID 会让 --rule 前缀与台账都失去指向
var duplicateIds = rules.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
    .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
if (duplicateIds.Count > 0)
{
    Console.Error.WriteLine($"::error::规则表存在重复 ID：{string.Join(", ", duplicateIds)}");
    return 2;
}

string? rulePrefix = null;
bool verifyAnchors = false;
bool listOnly = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rule" when i + 1 < args.Length:
            rulePrefix = args[++i];
            break;
        case "--verify-anchors":
            verifyAnchors = true;
            break;
        case "--list":
            listOnly = true;
            break;
        default:
            Console.Error.WriteLine($"未知参数：{args[i]}（可用：--rule <前缀> / --verify-anchors / --list）");
            return 2;
    }
}

if (listOnly)
{
    Console.WriteLine($"规则表：{rulesPath}");
    Console.WriteLine($"共 {rules.Length} 条（R- 族 {rules.Count(r => r.Id.StartsWith("R-", StringComparison.Ordinal))} 条）；登记不实施 {registeredCount} 条");
    Console.WriteLine();
    Console.WriteLine("| 规则 | 文件 | 目标过滤器 | 期望契约 |");
    Console.WriteLine("|------|------|------------|----------|");
    foreach (Rule rule in rules)
    {
        string expected = rule.ExpectedContracts is { Length: > 0 } ids ? string.Join(", ", ids) : "—";
        Console.WriteLine($"| {rule.Id} | `{rule.File}` | `{rule.Filter}` | {expected} |");
    }
    return 0;
}

if (verifyAnchors)
{
    return VerifyAnchors(repoRoot, rules);
}

(List<(string Commit, string Subject)> Head, string Dirty) = (GitLog(repoRoot), GitStatus(repoRoot));
if (Dirty.Length > 0)
{
    Console.Error.WriteLine("工作树不清洁，变异回放会丢失改动。先提交或 stash 再执行：");
    Console.Error.WriteLine(Dirty);
    return 2;
}

Console.WriteLine($"仓库根: {repoRoot}");
Console.WriteLine($"dotnet: {dotnet}");
Console.WriteLine($"规则表: {rulesPath}（{rules.Length} 条，登记不实施 {registeredCount} 条）");
Console.WriteLine($"基线提交: {(Head.Count > 0 ? Head[0].Commit[..8] + " " + Head[0].Subject : "(none)")}");
Console.WriteLine();

var selected = rulePrefix is null
    ? rules
    : rules.Where(rule => rule.Id.StartsWith(rulePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
if (rulePrefix is not null)
    Console.WriteLine($"规则过滤 --rule {rulePrefix}：{selected.Length}/{rules.Length} 条\n");

var results = new List<RuleResult>();
foreach (Rule rule in selected)
{
    string project = rule.TestProject ?? defaultProject;
    string[] expected = rule.ExpectedContracts ?? [];
    Console.WriteLine($"── {rule.Id}: {rule.Note}");
    Console.WriteLine($"   文件 {rule.File}");
    Console.WriteLine($"   项目 {project}");
    if (expected.Length > 0) Console.WriteLine($"   期望报红契约 {string.Join(", ", expected)}");

    string absolutePath = Path.Combine(repoRoot, rule.File);
    string original = File.ReadAllText(absolutePath);
    string matchText = AdaptLineEndings(rule.Original, original);
    string mutation = AdaptLineEndings(rule.Mutated, original);
    if (!original.Contains(matchText, StringComparison.Ordinal))
    {
        Console.WriteLine("   SKIP 原片段未找到（源码可能已改动），跳过该规则");
        results.Add(new RuleResult(
            rule, project, "skip", false, false, new RunOutcome(false, 0, "", ""), false,
            new RunOutcome(false, 0, "", "")));
        continue;
    }

    File.WriteAllText(absolutePath, original.Replace(matchText, mutation, StringComparison.Ordinal));

    bool injectedBuild = Build(dotnet, repoRoot, project);
    RunOutcome injected = injectedBuild
        ? RunTests(dotnet, repoRoot, rule.Filter, project)
        : new RunOutcome(false, 0, "", "");

    Restore(repoRoot, rule.File);
    bool restoredBuild = Build(dotnet, repoRoot, project);
    RunOutcome restored = restoredBuild
        ? RunTests(dotnet, repoRoot, rule.Filter, project)
        : new RunOutcome(false, 0, "", "");

    bool injectedRed = injectedBuild && injected.Red;
    bool restoredGreen = restoredBuild && !restored.Red;
    // 契约族规则：报红名单里必须出现期望契约 ID，否则红的原因不是这条契约
    string[] missing = [.. expected.Where(id => !injected.FailedArgs.Contains(id, StringComparison.Ordinal))];
    bool expectedHit = injectedBuild && missing.Length == 0;
    string verdict = injectedBuild && injectedRed && expectedHit && restoredBuild && restoredGreen ? "PASS" : "FAIL";
    Console.WriteLine($"   注入: 构建={(injectedBuild ? "ok" : "fail")} 测试={(injectedRed ? "red" : "未红")} "
        + $"(报红 {injected.Failed} 项)");
    if (injected.FailedNames.Length > 0) Console.WriteLine($"   报红: {injected.FailedNames}");
    if (injected.FailedArgs.Length > 0) Console.WriteLine($"   报红契约: {injected.FailedArgs}");
    if (expected.Length > 0)
        Console.WriteLine(missing.Length == 0
            ? "   期望契约: 命中"
            : $"   期望契约: 未命中（缺 {string.Join(", ", missing)}）");
    Console.WriteLine($"   还原: 构建={(restoredBuild ? "ok" : "fail")} 测试={(restoredGreen ? "green" : "未转绿")}");
    Console.WriteLine($"   判定 {verdict}");
    Console.WriteLine();

    results.Add(new RuleResult(rule, project, verdict, injectedBuild, injectedRed, injected, restoredBuild, restored));
}

Console.WriteLine("汇总");
Console.WriteLine("| 规则 | 注入构建 | 注入报红 | 还原构建 | 还原转绿 | 判定 |");
Console.WriteLine("|------|----------|----------|----------|----------|------|");
foreach (RuleResult result in results)
{
    Console.WriteLine(
        $"| {result.Rule.Id} | {(result.InjectedBuild ? "ok" : "fail")} "
        + $"| {(result.Injected.Red ? $"red({result.Injected.Failed})" : "未红")} "
        + $"| {(result.RestoredBuild ? "ok" : "fail")} "
        + $"| {(result.RestoredBuild && !result.Restored.Red ? "green" : "未绿")} | {result.Verdict} |");
}

Console.WriteLine();
Console.WriteLine("回放台账（Markdown，供 docs/一致性测试总报告 \"未完成项\" 登记）");
Console.WriteLine("| 规则 | 缺陷 | 期望契约 | 注入点 | 目标过滤器 | 报红测试数 | 报红测试名 | 判定 |");
Console.WriteLine("|------|------|----------|--------|------------|------------|------------|------|");
foreach (RuleResult result in results)
{
    string expected = result.Rule.ExpectedContracts is { Length: > 0 } ids ? string.Join(", ", ids) : "—";
    Console.WriteLine(
        $"| {result.Rule.Id} | {Escape(result.Rule.Note.Split('；')[0])} "
        + $"| {expected} "
        + $"| `{result.Rule.File}` | `{result.Rule.Filter}` "
        + $"| {result.Injected.Failed} | {Escape(result.Injected.FailedNames)} | {result.Verdict} |");
}

int failed = results.Count(r => r.Verdict != "PASS");
int skipped = results.Count(r => r.Verdict == "skip");
Console.WriteLine();
Console.WriteLine(failed == 0 ? "全部规则通过" : $"{failed} 条规则未通过");
if (skipped > 0)
{
    // B189 族（无声 no-op）的机械提示：skip 也是"未通过"，但根因是注入片段与源码失配，
    // 修法是重锚片段而非改判定。不单列出来时，这两条规则会长期以"未通过"名义被当成
    // 已知噪声忽略——D4/D5 就这样静默失效了数月（2026-11-03 重锚后转 PASS）。
    Console.WriteLine($"其中 {skipped} 条为 SKIP（注入片段在源码中已失配，规则从未真正执行）：");
    foreach (RuleResult result in results.Where(static r => r.Verdict == "skip"))
    {
        Console.WriteLine($"  {result.Rule.Id}: `{result.Rule.File}` — 需重锚 Original 片段");
    }
}
return failed == 0 ? 0 : 1;

// ── 锚点自查（MUT-1）：不注入、不构建，只核对 Original 片段能否解析 ──
// 存在的理由：规则数据化后，源码重构会让片段失配，而全量回放要跑几十分钟才暴露出来。
// 这个模式把"规则是否还有效"的成本压到秒级，改产品代码后先跑它。
static int VerifyAnchors(string repoRoot, Rule[] rules)
{
    var missing = new List<string>();
    foreach (Rule rule in rules)
    {
        string absolute = Path.Combine(repoRoot, rule.File);
        if (!File.Exists(absolute))
        {
            missing.Add($"{rule.Id}: 文件不存在 {rule.File}");
            continue;
        }
        string original = File.ReadAllText(absolute);
        if (!original.Contains(AdaptLineEndings(rule.Original, original), StringComparison.Ordinal))
        {
            missing.Add($"{rule.Id}: 原片段未找到 {rule.File}");
        }
    }

    int rFamily = rules.Count(r => r.Id.StartsWith("R-", StringComparison.Ordinal));
    Console.WriteLine($"变异规则锚点自查：{rules.Length} 条（R- 族 {rFamily} 条 + 历史 {rules.Length - rFamily} 条）");
    if (missing.Count == 0)
    {
        Console.WriteLine($"锚点就绪 {rules.Length}/{rules.Length}");
        return 0;
    }
    foreach (string item in missing) Console.Error.WriteLine($"::error::{item}");
    Console.Error.WriteLine($"锚点失配 {missing.Count}/{rules.Length}——按上面的文件与规则 ID 重锚 Original 片段");
    return 1;
}

static string AdaptLineEndings(string text, string sample)
    => sample.Contains("\r\n", StringComparison.Ordinal) ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;

static Rule ParseRule(JsonNode? node)
{
    JsonArray? expected = node?["expectedContracts"] as JsonArray;
    return new Rule(
        node?["id"]!.GetValue<string>() ?? "",
        node?["file"]!.GetValue<string>() ?? "",
        node?["original"]!.GetValue<string>() ?? "",
        node?["mutated"]!.GetValue<string>() ?? "",
        node?["filter"]!.GetValue<string>() ?? "",
        node?["note"]!.GetValue<string>() ?? "",
        node?["testProject"]?.GetValue<string>(),
        expected is null ? null : [.. expected.Select(x => x!.GetValue<string>())]);
}

static string Escape(string text)
    => text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
        .Replace("|", "/", StringComparison.Ordinal);

static bool Build(string dotnet, string workingDirectory, string project)
{
    var (exit, _) = Execute(dotnet, $"build {project} -c Debug --nologo", workingDirectory);
    return exit == 0;
}

static RunOutcome RunTests(string dotnet, string workingDirectory, string filter, string project)
{
    var (exit, output) = Execute(
        dotnet,
        $"run --project {project} -c Debug --no-build -- --treenode-filter \"{filter}\"",
        workingDirectory);
    (int failed, string names, string failedArgs) = ParseFailures(output);
    if (exit != 0 && failed == 0)
    {
        // 解析失败时把尾巴打出来，避免"红但数不出来"静默通过
        Console.WriteLine("   诊断：退出码非零但未解析到失败摘要，输出尾部：");
        string[] tail = output.Split('\n');
        foreach (string line in tail[^Math.Min(20, tail.Length)..])
            Console.WriteLine($"     {line.TrimEnd()}");
    }
    return new RunOutcome(exit != 0, failed, names, failedArgs);
}

/// <summary>从 TUnit 控制台输出取失败项数与失败测试全名。失败块首行只给短名
/// （"失败 E4_MySql_... (1s 163ms)"），全名在紧随其后的栈帧（"在 命名空间.类.方法()"）里取。
/// 参数化用例的首行形如 "失败 Sqlite_Contract_Holds(D-BULKDEL-01) (19ms)"——括号里的实参是
/// 契约 ID 的唯一来源（栈帧里的方法名不含实参），单独收集进 FailedArgs 供契约族规则判定。
/// 摘要行形如 "失败: 3"。输出按 UTF-8 解码（中文摘要与栈帧前缀依赖它）。</summary>
static (int Failed, string Names, string Args) ParseFailures(string output)
{
    string text = Regex.Replace(output, "\u001b\\[[0-9;]*m", string.Empty);
    string[] lines = text.Split('\n');
    var names = new List<string>();
    var args = new List<string>();
    for (int i = 0; i < lines.Length; i++)
    {
        Match header = Regex.Match(
            lines[i],
            @"^\s*(?:失败|failed)\s+(?<short>[^\s(]+)(?:\((?<args>[^)]*)\))?",
            RegexOptions.IgnoreCase);
        if (!header.Success) continue;
        string shortName = header.Groups["short"].Value.Trim();
        if (header.Groups["args"].Success)
            args.Add(header.Groups["args"].Value.Trim());
        string fullName = shortName;
        for (int j = i + 1; j < Math.Min(i + 40, lines.Length); j++)
        {
            Match frame = Regex.Match(lines[j], @"(?:在|at)\s+(?<fqn>[A-Za-z0-9_.+]+)\(");
            if (frame.Success && frame.Groups["fqn"].Value.EndsWith("." + shortName, StringComparison.Ordinal))
            {
                fullName = frame.Groups["fqn"].Value;
                break;
            }
        }
        names.Add(fullName);
    }
    Match summary = Regex.Match(text, @"(?:失败|failed)\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase);
    int failed = summary.Success
        ? int.Parse(summary.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
        : names.Count;
    return (failed,
        string.Join(", ", names.Distinct(StringComparer.Ordinal)),
        string.Join(", ", args.Distinct(StringComparer.Ordinal)));
}

static void Restore(string repoRoot, string file)
    => Execute("git", $"checkout -- \"{file}\"", repoRoot);

static string GitStatus(string repoRoot)
{
    // 只关心已跟踪文件的改动：未跟踪文件不会被 git checkout 影响
    var (_, output) = Execute("git", "status --porcelain -uno", repoRoot);
    return output.Trim();
}

static List<(string Commit, string Subject)> GitLog(string repoRoot)
{
    var (_, output) = Execute("git", "log -1 --pretty=format:%H%x09%s", repoRoot);
    var list = new List<(string, string)>();
    foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        string[] parts = line.Split('\t', 2);
        if (parts.Length == 2) list.Add((parts[0].Trim(), parts[1].Trim()));
    }
    return list;
}

static (int Exit, string Output) Execute(string fileName, string arguments, string workingDirectory)
{
    var startInfo = new ProcessStartInfo(fileName, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory,
    };
    using var process = Process.Start(startInfo)!;
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
}

static string FindRepoRoot()
{
    string? dir = Directory.GetCurrentDirectory();
    while (!string.IsNullOrEmpty(dir))
    {
        if (File.Exists(Path.Combine(dir, "Directory.Build.props"))) return dir;
        dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("找不到仓库根（Directory.Build.props）");
}

static string ResolveDotnet()
{
    // global.json 锁定 rc.2，系统 SDK 可能已升级；优先用仓库既有本地 SDK，其次 PATH
    const string localSdk = @"C:\temp\dotnet-rc2\dotnet.exe";
    return File.Exists(localSdk) ? localSdk : "dotnet";
}

internal sealed record Rule(
    string Id, string File, string Original, string Mutated, string Filter, string Note,
    string? TestProject = null, string[]? ExpectedContracts = null);

internal sealed record RunOutcome(bool Red, int Failed, string FailedNames, string FailedArgs);

internal sealed record RuleResult(
    Rule Rule, string Project, string Verdict, bool InjectedBuild, bool InjectedRed, RunOutcome Injected,
    bool RestoredBuild, RunOutcome Restored);
