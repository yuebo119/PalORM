// conformance-mutation-replay.cs
// 一致性套件的产品侧变异回放（方案 §4.8 / 附录 B 第 6 项；D1-D7 回放见《测试体系增强方案-2026-10-30》§5.1）。
//
// 对每条规则执行四步：注入缺陷 -> 构建 -> 跑目标测试（期望报红）-> git 还原 -> 构建 -> 跑同一组测试（期望转绿）。
// 判定标准是"注入后红 且 还原后绿"，任一不符即 exit 1。注入内容均为硬替换，不用环境变量开关：
// 源生成器读取环境变量会被 Roslyn RS1035 拦下（分析器禁止读环境变量）。
//
// 规则可指定自己的测试项目（R-D5 的 PALORM054 检测测试在 test/PalORM.SourceGen.Tests）；
// 未指定时用默认项目 test/PalORM.Integration.Tests。多行片段按工作树实际行尾适配（仓库索引 LF，工作树受
// core.autocrlf 影响可能是 CRLF）。
//
// 前置条件：工作树必须清洁（脚本会用 git checkout 还原被改文件，脏树会丢改动）。
// 用法：dotnet run --file scripts/conformance-mutation-replay.cs [-- --keep-artifacts]
// 输出末尾带 Markdown 台账（规则 / 注入点 / 报红测试数 / 报红测试名），供总报告登记。
using System.Diagnostics;
using System.Text.RegularExpressions;

const string DefaultTestProject = "test/PalORM.Integration.Tests";

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

// D1-D7 七条规则对应已修复的历史产品缺陷（《测试体系增强方案-2026-10-30》§4.2）：每条有真实缺陷背书，
// 注入点一行可换、可观测量（哪个期望值会变）在 Note 里写明。
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

    // ── D1-D7 历史缺陷回放（2026-10-30 补齐）──────────────────────────────
    new("R-D1", "src/PalORM.SourceGen/MigrationEmitter.cs",
        "SqlGenerationDialect.PostgreSql => \"TIMESTAMPTZ\",",
        "SqlGenerationDialect.PostgreSql => \"TIMESTAMP\",",
        "/*/*/TimeKindMatrixTests/PostgreSql_DateTimeRoundTrip_ReadThenWriteBackSucceeds",
        "D1 PG 时间列型 TIMESTAMPTZ→TIMESTAMP（两处映射同时命中）；"
        + "可观测量：DateTime 读回 Kind=Unspecified，写回抛 ArgumentException"),
    new("R-D2", "src/PalORM.Core/DataSession_Bulk.cs",
        "BackfillGeneratedId(backfill!, entities[start + i], returnedIds[i]);",
        "BackfillGeneratedId(backfill!, entities[start + i], returnedIds[0]);",
        "/*/*/BulkInsertReturningTests/*",
        "D2 批量回填把首值写给所有行；可观测量：实体 Id 与库内 Id 逐行比对失败"),
    new("R-D3", "src/PalORM.Core/QueryBuilder.cs",
        "if (_dialect != SqlDialect.Sqlite) return;",
        "if (_dialect == SqlDialect.Sqlite) return;",
        "/*/*/BoundaryContractTests/*",
        "D3 SQLite 行锁放行（条件反号）；可观测量：ForUpdate/ForShare 不再抛 NotSupportedException"),
    new("R-D4", "src/PalORM.Core/CommandTimeoutNormalizer.cs",
        "        catch (Exception ex) when (ex is not TimeoutException && ex.InnerException is TimeoutException timeout)\n"
        + "        {\n"
        + "            throw new TimeoutException(timeout.Message, ex);\n"
        + "        }\n",
        "",
        "/*/*/ErrorContractTests/*",
        "D4 超时不解包（删内层 TimeoutException catch，保留 D7 耗时判定 catch）；"
        + "可观测量：PG 命令超时异常类型不再是 TimeoutException"),
    new("R-D5", "src/PalORM.SourceGen/PalORMAnalyzer.cs",
        "        bool hasConcurrency = SourceGenerationValidation.EnumerateMappedProperties(entityType)\n"
        + "            .Any(p => p.GetAttributes().Any(a => SourceGenerationValidation.IsPalORMAttribute(a, \"ConcurrencyCheck\")));\n"
        + "        if (hasConcurrency)\n"
        + "        {\n"
        + "            ctx.ReportDiagnostic(Diagnostic.Create(\n"
        + "                SaveWithConcurrencyToken, invocation.GetLocation(), entityType.Name));\n"
        + "        }\n",
        "        // 注入：PALORM054 检测停用（回放 R-D5）\n"
        + "        _ = entityType;\n",
        "/*/*/AnalyzerDiagnosticsTests/*",
        "D5 SaveAsync 无编译期拒绝（PALORM054 不再报告）；"
        + "可观测量：期望诊断 PALORM054 缺席",
        "test/PalORM.SourceGen.Tests"),
    new("R-D6", "src/PalORM.Core/QueryBuilderExtensions.cs",
        "        if (paged._skip.HasValue)\n"
        + "            throw new InvalidOperationException(\n"
        + "                \"ToPageAsync uses keyset pagination with lastValue as the continuation anchor, so a Skip() on the builder \"\n"
        + "                + \"would be silently ignored. Remove Skip(), or use ToListAsync with Skip/Take for OFFSET pagination.\");\n",
        "",
        "/*/*/CapabilityContractTests/*",
        "D6 ToPageAsync 放行 Skip（删构建期拒绝）；可观测量：Skip()+ToPageAsync 不再抛 InvalidOperationException"),
    new("R-D7", "src/PalORM.Core/CommandTimeoutNormalizer.cs",
        "=> dialect == SqlDialect.MySql && timeoutSeconds > 0;",
        "=> false;",
        "/*/*/ErrorContractTests/*",
        "D7 MySQL 超时未归一（耗时判定恒关）；可观测量：超时返回部分结果被当成功"),
};

var results = new List<RuleResult>();
foreach (Rule rule in rules)
{
    string project = rule.TestProject ?? DefaultTestProject;
    Console.WriteLine($"── {rule.Id}: {rule.Note}");
    Console.WriteLine($"   文件 {rule.File}");
    Console.WriteLine($"   项目 {project}");

    string absolutePath = Path.Combine(repoRoot, rule.File);
    string original = File.ReadAllText(absolutePath);
    bool crlf = original.Contains("\r\n", StringComparison.Ordinal);
    string matchText = crlf ? rule.Original.Replace("\n", "\r\n", StringComparison.Ordinal) : rule.Original;
    string mutation = crlf ? rule.Mutated.Replace("\n", "\r\n", StringComparison.Ordinal) : rule.Mutated;
    if (!original.Contains(matchText, StringComparison.Ordinal))
    {
        Console.WriteLine("   SKIP 原片段未找到（源码可能已改动），跳过该规则");
        results.Add(new RuleResult(
            rule, project, "skip", false, false, new RunOutcome(false, 0, ""), false, new RunOutcome(false, 0, "")));
        continue;
    }

    File.WriteAllText(absolutePath, original.Replace(matchText, mutation, StringComparison.Ordinal));

    bool injectedBuild = Build(dotnet, repoRoot, project);
    RunOutcome injected = injectedBuild
        ? RunTests(dotnet, repoRoot, rule.Filter, project)
        : new RunOutcome(false, 0, "");

    Restore(repoRoot, rule.File);
    bool restoredBuild = Build(dotnet, repoRoot, project);
    RunOutcome restored = restoredBuild
        ? RunTests(dotnet, repoRoot, rule.Filter, project)
        : new RunOutcome(false, 0, "");

    bool injectedRed = injectedBuild && injected.Red;
    bool restoredGreen = restoredBuild && !restored.Red;
    string verdict = injectedBuild && injectedRed && restoredBuild && restoredGreen ? "PASS" : "FAIL";
    Console.WriteLine($"   注入: 构建={(injectedBuild ? "ok" : "fail")} 测试={(injectedRed ? "red" : "未红")} "
        + $"(报红 {injected.Failed} 项)");
    if (injected.FailedNames.Length > 0) Console.WriteLine($"   报红: {injected.FailedNames}");
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
Console.WriteLine("| 规则 | 缺陷 | 注入点 | 目标过滤器 | 报红测试数 | 报红测试名 | 判定 |");
Console.WriteLine("|------|------|--------|------------|------------|------------|------|");
foreach (RuleResult result in results)
{
    Console.WriteLine(
        $"| {result.Rule.Id} | {Escape(result.Rule.Note.Split('；')[0])} "
        + $"| `{result.Rule.File}` | `{result.Rule.Filter}` "
        + $"| {result.Injected.Failed} | {Escape(result.Injected.FailedNames)} | {result.Verdict} |");
}

int failed = results.Count(r => r.Verdict != "PASS");
Console.WriteLine();
Console.WriteLine(failed == 0 ? "全部规则通过" : $"{failed} 条规则未通过");
return failed == 0 ? 0 : 1;

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
    (int failed, string names) = ParseFailures(output);
    if (exit != 0 && failed == 0)
    {
        // 解析失败时把尾巴打出来，避免"红但数不出来"静默通过
        Console.WriteLine("   诊断：退出码非零但未解析到失败摘要，输出尾部：");
        string[] tail = output.Split('\n');
        foreach (string line in tail[^Math.Min(20, tail.Length)..])
            Console.WriteLine($"     {line.TrimEnd()}");
    }
    return new RunOutcome(exit != 0, failed, names);
}

/// <summary>从 TUnit 控制台输出取失败项数与失败测试全名。失败块首行只给短名
/// （"失败 E4_MySql_... (1s 163ms)"），全名在紧随其后的栈帧（"在 命名空间.类.方法()"）里取。
/// 摘要行形如 "失败: 3"。输出按 UTF-8 解码（中文摘要与栈帧前缀依赖它）。</summary>
static (int Failed, string Names) ParseFailures(string output)
{
    string text = Regex.Replace(output, "\u001b\\[[0-9;]*m", string.Empty);
    string[] lines = text.Split('\n');
    var names = new List<string>();
    for (int i = 0; i < lines.Length; i++)
    {
        Match header = Regex.Match(
            lines[i],
            @"^\s*(?:失败|failed)\s+(?<short>[^\s(]+)",
            RegexOptions.IgnoreCase);
        if (!header.Success) continue;
        string shortName = header.Groups["short"].Value.Trim();
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
    return (failed, string.Join(", ", names.Distinct(StringComparer.Ordinal)));
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
    string Id, string File, string Original, string Mutated, string Filter, string Note, string? TestProject = null);

internal sealed record RunOutcome(bool Red, int Failed, string FailedNames);

internal sealed record RuleResult(
    Rule Rule, string Project, string Verdict, bool InjectedBuild, bool InjectedRed, RunOutcome Injected,
    bool RestoredBuild, RunOutcome Restored);