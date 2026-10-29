// conformance-mutation-replay.cs
// 一致性套件的产品侧变异回放（方案 §4.8 / 附录 B 第 6 项）。
//
// 对每条规则执行四步：注入缺陷 -> 构建 -> 跑目标测试（期望报红）-> git 还原 -> 构建 -> 跑同一组测试（期望转绿）。
// 判定标准是"注入后红 且 还原后绿"，任一不符即 exit 1。注入内容均为硬替换，不用环境变量开关：
// 源生成器读取环境变量会被 Roslyn RS1035 拦下（分析器禁止读环境变量）。
//
// 前置条件：工作树必须清洁（脚本会用 git checkout 还原被改文件，脏树会丢改动）。
// 用法：dotnet run --file scripts/conformance-mutation-replay.cs [-- --keep-artifacts]
using System.Diagnostics;

const string TestProject = "test/PalORM.Integration.Tests";

string repoRoot = FindRepoRoot();
string dotnet = ResolveDotnet();

(List<(string Commit, string Subject)> Head, string Dirty) = (GitLog(repoRoot), GitStatus(repoRoot));
if (Dirty.Length > 0)
{
    Console.Error.WriteLine("工作树不清洁，变异回放会丢失改动。先提交或 stash 再执行：");
    Console.Error.WriteLine(Dirty);
    return 2;
}

Console.WriteLine($"仓库根: {repoRoot}");
Console.WriteLine($"dotnet: {dotnet}");
Console.WriteLine($"基线提交: {(Head.Count > 0 ? Head[0].Commit[..8] + " " + Head[0].Subject : "(none)")}");
Console.WriteLine();

var rules = new Rule[]
{
    new("R1a", "src/PalORM.Core/DataSession.Crud.cs",
        "if (!ignoreFilters && (features & EntityFeatures.SoftDelete) != 0)",
        "if ((features & EntityFeatures.SoftDelete) != 0)",
        "/*/*/SoftDeletePartitionTests/*",
        "From<T> 的软删过滤不再响应 IgnoreFilters"),
    new("R1b", "src/PalORM.Core/DataSession.cs",
        "bool hasSoftDelete = !_ignoreFilters && (features & EntityFeatures.SoftDelete) != 0;",
        "bool hasSoftDelete = (features & EntityFeatures.SoftDelete) != 0;",
        "/*/*/SoftDeletePartitionTests/*",
        "GetAll/Count 的第二注入路径同样失效"),
    new("R2", "src/PalORM.SourceGen/RowFactoryEmitter.cs",
        "string readCall = GetReadExpression(col, ordinal, model.GeneratedTypeSuffix);",
        "string readCall = GetReadExpression(col, ordinal < 2 ? 1 - ordinal : ordinal, model.GeneratedTypeSuffix);",
        "/*/*/DualSideComparisonTests/*",
        "物化列序前两位互换（RowFactory 序号漂移）"),
    new("R4", "src/PalORM.Core/QueryBuilder.cs",
        "return hasOr && (start > 0 || end < clauses.Count);",
        "return hasOr && (start > 0 || end < clauses.Count) && start < 0;",
        "/*/*/IncidentFilterPrecedenceTests/*",
        "AND 组补括号回退（C16 同源）"),
};

var results = new List<RuleResult>();
foreach (Rule rule in rules)
{
    Console.WriteLine($"── {rule.Id}: {rule.Note}");
    Console.WriteLine($"   文件 {rule.File}");

    string absolutePath = Path.Combine(repoRoot, rule.File);
    string original = File.ReadAllText(absolutePath);
    if (!original.Contains(rule.Original, StringComparison.Ordinal))
    {
        Console.WriteLine("   SKIP 原片段未找到（源码可能已改动），跳过该规则");
        results.Add(new RuleResult(rule.Id, "skip", false, false, false, false));
        continue;
    }

    File.WriteAllText(absolutePath, original.Replace(rule.Original, rule.Mutated, StringComparison.Ordinal));

    bool injectedBuild = Build(dotnet, repoRoot);
    bool injectedRed = injectedBuild && RunTests(dotnet, repoRoot, rule.Filter);

    Restore(repoRoot, rule.File);
    bool restoredBuild = Build(dotnet, repoRoot);
    bool restoredGreen = restoredBuild && !RunTests(dotnet, repoRoot, rule.Filter);

    string verdict = injectedBuild && injectedRed && restoredBuild && restoredGreen ? "PASS" : "FAIL";
    Console.WriteLine($"   注入: 构建={(injectedBuild ? "ok" : "fail")} 测试={(injectedRed ? "red" : "未红")}");
    Console.WriteLine($"   还原: 构建={(restoredBuild ? "ok" : "fail")} 测试={(restoredGreen ? "green" : "未转绿")}");
    Console.WriteLine($"   判定 {verdict}");
    Console.WriteLine();

    results.Add(new RuleResult(rule.Id, verdict, injectedBuild, injectedRed, restoredBuild, restoredGreen));
}

Console.WriteLine("汇总");
Console.WriteLine("| 规则 | 注入构建 | 注入报红 | 还原构建 | 还原转绿 | 判定 |");
Console.WriteLine("|------|----------|----------|----------|----------|------|");
foreach (RuleResult result in results)
{
    Console.WriteLine(
        $"| {result.Id} | {(result.InjectedBuild ? "ok" : "fail")} | {(result.InjectedRed ? "red" : "未红")} "
        + $"| {(result.RestoredBuild ? "ok" : "fail")} | {(result.RestoredGreen ? "green" : "未绿")} | {result.Verdict} |");
}

int failed = results.Count(r => r.Verdict != "PASS");
Console.WriteLine();
Console.WriteLine(failed == 0 ? "全部规则通过" : $"{failed} 条规则未通过");
return failed == 0 ? 0 : 1;

static bool Build(string dotnet, string workingDirectory)
{
    var (exit, _) = Execute(dotnet, $"build {TestProject} -c Debug --nologo", workingDirectory);
    return exit == 0;
}

static bool RunTests(string dotnet, string workingDirectory, string filter)
{
    var (exit, _) = Execute(
        dotnet,
        $"run --project {TestProject} -c Debug --no-build -- --treenode-filter \"{filter}\"",
        workingDirectory);
    return exit != 0;
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
    string Id, string File, string Original, string Mutated, string Filter, string Note);

internal sealed record RuleResult(
    string Id, string Verdict, bool InjectedBuild, bool InjectedRed, bool RestoredBuild, bool RestoredGreen);