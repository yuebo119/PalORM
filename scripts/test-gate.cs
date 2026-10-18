// test-gate.cs（自 .ai/scripts/test-gate.sh 迁移，脚本全面 C# 化轮）
// PalORM 测试规范门禁——T4/T8/T9/T6/T-DEF-1/T-DEF-4/T11(SKIP)/T12。
// 迁移修正：T-DEF-1 原扫 scripts/*.sh 的 set -euo pipefail——主仓 scripts/ 已全量 C# 化，
// 按现形态改为"scripts/ 下出现未登记 .sh 即 FAIL"（语言政策残留哨兵，正主在 test-quality-scripts.cs）。
// 契约：0=通过；1=有 FAIL；stdout 与 .sh 版对拍兼容。
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();
// --ci：CI 环境语义（目前本地/CI 判据一致；保留参数位以便未来对 CI 专属豁免显式化）
_ = args.Length > 0 && args[0] == "--ci";
var failCount = 0; var warnCount = 0;

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine(" PalORM 测试规范门禁（T1-T14）");
Console.WriteLine($" 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

// ─── T4：测试命名规范 ───
// 2026-10-18 审计修复：原实现只匹配 `[Test]` 与 `public async Task X(` 同一行，而项目规范
// 要求多行测试体例，实测全仓同行命中 0——检查恒 PASS（空转）。改为块级：从 [Test] 起向后
// 找到首个方法声明（跨 [Arguments]/[Property] 等特性行），再判方法名是否含下划线。
Console.WriteLine();
Console.WriteLine("─── T4: 测试命名规范 ───");
var badNames = new List<string>();
var testSigRe = new Regex(
    @"\[Test\][\s\S]{0,600}?\b(?:public|private|internal)\s+(?:static\s+)?(?:async\s+)?(?:Task|ValueTask|void)\s+([A-Za-z_]\w*)\s*\(",
    RegexOptions.Compiled);
foreach (var f in EnumerateTestCs())
{
    var text = File.ReadAllText(f);
    foreach (Match m in testSigRe.Matches(text))
    {
        var name = m.Groups[1].Value;
        if (!name.Contains('_', StringComparison.Ordinal))
        {
            badNames.Add($"{f}:{LineOf(text, m.Index)} {name}");
        }
    }
}
if (badNames.Count > 0)
{
    Console.WriteLine($"WARN  T4  以下测试方法名不含下划线（T4 要求 Method_Scenario_ExpectedResult）:");
    foreach (var n in badNames.Take(10)) Console.WriteLine($"      - {n}");
    warnCount++;
}
else Console.WriteLine("PASS  T4  测试命名规范检查通过（块级扫描全部 [Test] 方法签名）");

// ─── T8：基准配置注释 ───
Console.WriteLine();
Console.WriteLine("─── T8: 基准配置注释 ───");
var benchDir = "bench/PalORM.Benchmarks";
var benchFiles = Directory.Exists(benchDir) ? Directory.GetFiles(benchDir, "*.cs") : [];
if (benchFiles.Length > 0)
{
    var jobsWithoutComment = new List<string>();
    foreach (var f in benchFiles)
    {
        var lines = File.ReadAllLines(f);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("SimpleJob")) continue;
            var hasComment = false;
            for (var j = Math.Max(0, i - 3); j < i; j++)
            {
                if (lines[j].Contains("//") || lines[j].Contains("/*")) { hasComment = true; break; }
            }
            if (!hasComment) jobsWithoutComment.Add($"{f.Replace('\\', '/')}:{i + 1}: {lines[i].Trim()}");
        }
    }
    if (jobsWithoutComment.Count > 0)
    {
        Console.WriteLine("WARN  T8  以下 SimpleJob 缺少配置理由注释（T8 要求）:");
        foreach (var l in jobsWithoutComment.Take(5)) Console.WriteLine($"      line {l}");
        warnCount++;
    }
    else Console.WriteLine("PASS  T8  基准配置注释检查通过");
}
else Console.WriteLine("SKIP  T8  基准目录无 .cs 文件");

// ─── T9：BenchmarkCategory 同义词 ───
Console.WriteLine();
Console.WriteLine("─── T9: BenchmarkCategory 同义词 ───");
if (benchFiles.Length > 0)
{
    var hasBulk = 0; var hasBulkInsert = 0;
    foreach (var f in benchFiles)
    {
        var t = File.ReadAllText(f);
        hasBulk += Regex.Count(t, @"BenchmarkCategory\(""Bulk""\)");
        hasBulkInsert += Regex.Count(t, @"BenchmarkCategory\(""BulkInsert""\)");
    }
    if (hasBulk > 0 && hasBulkInsert > 0)
    {
        Console.WriteLine("FAIL  T9  Bulk 和 BulkInsert 混用（应统一）");
        failCount++;
    }
    else Console.WriteLine("PASS  T9  BenchmarkCategory 无同义词混用");
}
else Console.WriteLine("SKIP  T9  基准目录无 .cs 文件");

// ─── T6：外部 DB 测试清理检查 ───
// 2026-10-18 审计修复：原实现判 `DROP TABLE` 与 `finally` 是否同一行——Integration 实测
// 110 处 DROP 仅 4 处同行 finally，跨行 try/finally 结构完全不在判据内。改为块级：逐测试
// 方法取完整方法体，凡体触碰 DDL（CREATE/DROP TABLE）且无 finally、且未调用"内含 finally
// 的同文件 helper"者计为可疑；委托给 helper 的用法（如 `=> await RunAsync(db)`）不误报。
Console.WriteLine();
Console.WriteLine("─── T6: 外部 DB 测试清理检查 ───");
var leakingCleanup = new List<string>();
var helperFinallyRe = new Regex(
    @"(?:private|internal|public)\s+(?:static\s+)?(?:async\s+)?(?:Task|ValueTask|void)\s+([A-Za-z_]\w*)\s*\(",
    RegexOptions.Compiled);
var testBodyRe = new Regex(
    @"\[Test\][\s\S]{0,600}?\b(?:public|private|internal)\s+(?:static\s+)?(?:async\s+)?(?:Task|ValueTask|void)\s+([A-Za-z_]\w*)\s*\(",
    RegexOptions.Compiled);
foreach (var f in EnumerateTestCs().Where(f => f.Contains("PalORM.Integration.Tests", StringComparison.Ordinal)))
{
    var text = File.ReadAllText(f);
    var helpersWithFinally = new HashSet<string>(StringComparer.Ordinal);
    foreach (Match h in helperFinallyRe.Matches(text))
    {
        var end = BlockEnd(text, text.IndexOf('{', h.Index));
        if (end > 0 && text[h.Index..end].Contains("finally", StringComparison.Ordinal))
        {
            helpersWithFinally.Add(h.Groups[1].Value);
        }
    }
    foreach (Match m in testBodyRe.Matches(text))
    {
        var open = text.IndexOf('{', m.Index);
        var end = BlockEnd(text, open);
        if (end < 0) continue;
        // 注释剥离（B13 同族）：正文里提到 "裸 CREATE TABLE" 的说明文字不是 DDL 调用
        var body = StripComments(text[m.Index..end]);
        var touchesDdl = body.Contains("CREATE TABLE", StringComparison.Ordinal)
                      || body.Contains("DROP TABLE", StringComparison.Ordinal);
        if (!touchesDdl) continue;
        // T6 只约束外部 DB（PG/MySQL）：SQLite 内存库随 Dispose 自动销毁，表格残留不成立。
        var isExternal = Regex.IsMatch(body, @"PostgreSql|MySql|Npgsql", RegexOptions.None, TimeSpan.FromSeconds(5));
        if (!isExternal) continue;
        if (body.Contains("finally", StringComparison.Ordinal)) continue;
        if (helpersWithFinally.Any(h => Regex.IsMatch(body, $@"\b{Regex.Escape(h)}\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5)))) continue;
        leakingCleanup.Add($"{f}:{LineOf(text, m.Index)} {m.Groups[1].Value}");
    }
}
if (leakingCleanup.Count > 0)
{
    Console.WriteLine($"WARN  T6  以下测试触碰 DDL 但方法体内无 finally 清理（T6 要求 try/finally）:");
    foreach (var l in leakingCleanup.Take(10)) Console.WriteLine($"      - {l}");
    warnCount++;
}
else Console.WriteLine("PASS  T6  外部 DB 测试清理检查通过（块级：全部 DDL 测试有 finally 或委托含 finally 的 helper）");

// ─── T-DEF-1：scripts/ 语言残留哨兵（原 set -euo pipefail 检查按现形态重登记）───
Console.WriteLine();
Console.WriteLine("─── T-DEF-1: scripts/ 语言政策残留 ───");
// 主仓 scripts/ 已全量 C# 化（file-based app）；.sh/.py 残留即语言政策漂移。
// 合法豁免：无（.githooks/pre-commit 在 scripts/ 目录之外，由编码规范 §19 白名单管辖）。
var strayScripts = Directory.Exists("scripts")
    ? Directory.GetFiles("scripts").Where(f => f.EndsWith(".sh") || f.EndsWith(".py")).ToList()
    : [];
if (strayScripts.Count == 0) Console.WriteLine("PASS  T-DEF-1  scripts/ 无 .sh/.py 残留（全量 C# 形态维持）");
else
{
    foreach (var f in strayScripts) Console.WriteLine($"FAIL  T-DEF-1  {f} 为非 C# 脚本残留（编码规范 §19）");
    failCount++;
}

// ─── T-DEF-4：CI job timeout-minutes ───
// 2026-10-18 审计修复：原实现只扫 ci.yml + perf-gate.yml，且按 4 空格缩进识别 job——ci.yml
// 现为调用 verify.yml 的 1-job 薄壳、perf-gate.yml 用 2 空格缩进，实测只统计到 1 个 job，
// 覆盖的已不是真实 CI 面。改为扫描 .github/workflows 全部文件，按 jobs 块实际缩进识别。
Console.WriteLine();
Console.WriteLine("─── T-DEF-4: CI job timeout-minutes ───");
var totalJobs = 0; var timeouts = 0;
var workflowDir = ".github/workflows";
var workflowFiles = Directory.Exists(workflowDir)
    ? Directory.GetFiles(workflowDir, "*.yml").Concat(Directory.GetFiles(workflowDir, "*.yaml"))
        .OrderBy(f => f, StringComparer.Ordinal).ToList()
    : new List<string>();
foreach (var wf in workflowFiles)
{
    var lines = File.ReadAllLines(wf);
    var jobsIdx = Array.FindIndex(lines, l => l.TrimEnd() == "jobs:");
    if (jobsIdx < 0) continue;
    // jobs 块内首个 job 键的缩进即该文件的 job 层缩进（2 或 4 空格两种风格并存）
    var jobIndent = -1;
    var jobStarts = new List<int>();
    for (var i = jobsIdx + 1; i < lines.Length; i++)
    {
        var line = lines[i];
        if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
        var indent = line.Length - line.TrimStart().Length;
        if (!line.TrimEnd().EndsWith(':') || line.TrimEnd().Contains(": ")) continue;
        if (jobIndent < 0) jobIndent = indent;
        if (indent == jobIndent) jobStarts.Add(i);
    }
    for (var k = 0; k < jobStarts.Count; k++)
    {
        var start = jobStarts[k];
        var end = k + 1 < jobStarts.Count ? jobStarts[k + 1] : lines.Length;
        // 复用薄壳 job（body 仅 uses:）无自建步骤，不计
        var body = string.Join('\n', lines[start..end]);
        if (Regex.IsMatch(body, @"^\s+uses:\s", RegexOptions.Multiline)
            && !body.Contains("steps:", StringComparison.Ordinal)
            && !body.Contains("timeout-minutes", StringComparison.Ordinal)) continue;
        totalJobs++;
        if (body.Contains("timeout-minutes", StringComparison.Ordinal)) timeouts++;
    }
}
if (timeouts < totalJobs)
{
    Console.WriteLine($"WARN  T-DEF-4  CI 有 {totalJobs} 个 job，仅 {timeouts} 个有 timeout-minutes（扫描 {workflowFiles.Count} 个 workflow）");
    warnCount++;
}
else Console.WriteLine($"PASS  T-DEF-4  所有 CI job 均有 timeout-minutes（{totalJobs} 个 job / {workflowFiles.Count} 个 workflow）");

// ─── T11：远程 DB 环境变量命名分离（SKIP 登记）───
Console.WriteLine();
Console.WriteLine("─── T11: 远程 DB 环境变量命名分离 ───");
Console.WriteLine("SKIP  T11  bench 与测试共用凭证源为 perf.sh 成文设计（作废定义不再执行；见 ITM-806 裁决）");

// ─── T12：三方一致——包版本号 ───
// 2026-10-18 审计修复：原实现只比 Core 与 PostgreSql 两个 csproj，其余四个打包项目漏检。
// 改为全量 src/*/*.csproj + README 安装示例 + PackageConsumer 引用，任一处漂移即 FAIL。
Console.WriteLine();
Console.WriteLine("─── T12: 三方一致——包版本号 + 连接串参数同步 ───");
var centralVer = FirstVersion("Directory.Build.props");
var t12Bad = new List<string>();
foreach (var csproj in Directory.Exists("src")
    ? Directory.GetFiles("src", "*.csproj", SearchOption.AllDirectories)
    : Array.Empty<string>())
{
    var v = FirstVersion(csproj);
    if (v.Length > 0 && v != centralVer) t12Bad.Add($"{csproj.Replace('\\', '/')}={v}");
}
foreach (var (doc, pattern) in new[]
{
    ("README.md", """Include="PalORM\.[A-Za-z]+" Version="([0-9.]+)"""),
    ("test/PalORM.PackageConsumer.Aot/PalORM.PackageConsumer.Aot.csproj", """Include="PalORM\.[A-Za-z]+" Version="([0-9.]+)"""),
})
{
    if (!File.Exists(doc)) continue;
    foreach (Match m in Regex.Matches(File.ReadAllText(doc), pattern))
    {
        if (m.Groups[1].Value != centralVer) t12Bad.Add($"{doc}={m.Groups[1].Value}");
    }
}
if (t12Bad.Count > 0)
{
    Console.WriteLine($"FAIL  T12  包版本号漂移（中央 {centralVer}）: {string.Join(" ", t12Bad.Take(10))}");
    failCount++;
}
else Console.WriteLine($"PASS  T12  包版本号一致（{centralVer}，src 全部 csproj + README + 消费者）");

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($" 结果: {failCount} 失败 / {warnCount} 警告");
if (failCount > 0)
{
    Console.WriteLine(" ❌ 门禁未通过——修复 FAIL 项后重试");
    return 1;
}
Console.WriteLine(" ✅ 测试规范门禁通过");
return 0;

static IEnumerable<string> EnumerateTestCs()
{
    if (!Directory.Exists("test")) yield break;
    foreach (var f in Directory.EnumerateFiles("test", "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
    {
        var rel = f.Replace('\\', '/');
        if (rel.Contains("obj/") || rel.Contains("bin/")) continue;
        yield return rel;
    }
}

static int LineOf(string text, int index)
{
    return text.AsSpan(0, index).Count('\n') + 1;
}

static string StripComments(string code)
{
    var noBlock = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
    return Regex.Replace(noBlock, @"//.*$", "", RegexOptions.Multiline, TimeSpan.FromSeconds(5));
}

static int BlockEnd(string text, int openBrace)
{
    // 字符串/字符字面量与注释感知（与 assertion-strength-check.cs 同实现）：裸数括号会让
    // 测试体内 `'a}b'` 之类字面量提前闭合块，T4/T6 判据随之失真。
    if (openBrace < 0) return -1;
    var depth = 0;
    for (var i = openBrace; i < text.Length; i++)
    {
        var c = text[i];
        if (c == '/' && i + 1 < text.Length)
        {
            if (text[i + 1] == '/')
            {
                var nl = text.IndexOf('\n', i);
                if (nl < 0) return depth == 0 ? i : text.Length - 1;
                i = nl;
                continue;
            }
            if (text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) return text.Length - 1;
                i = close + 1;
                continue;
            }
        }
        if (c == '"')
        {
            // C# 原始字符串 """..."""（含 $$""" 插值变体）整段跳过——其中花括号是文本。
            var quoteCount = 0;
            while (i + quoteCount < text.Length && text[i + quoteCount] == '"') quoteCount++;
            if (quoteCount >= 3)
            {
                var closing = new string('"', quoteCount);
                var close = text.IndexOf(closing, i + quoteCount, StringComparison.Ordinal);
                if (close < 0) return -1;
                i = close + quoteCount - 1;
                continue;
            }
            var verbatim = i > 0 && text[i - 1] == '@';
            for (i++; i < text.Length; i++)
            {
                if (!verbatim && text[i] == '\\') { i++; continue; }
                if (text[i] == '"')
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                    break;
                }
            }
            continue;
        }
        if (c == '\'')
        {
            for (i++; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '\'') break;
            }
            continue;
        }
        if (c == '{') depth++;
        else if (c == '}')
        {
            depth--;
            if (depth == 0) return i;
        }
    }
    return -1;
}

static string FirstVersion(string file)
{
    if (!File.Exists(file)) return "";
    var m = Regex.Match(File.ReadAllText(file), @"<Version>([^<]+)");
    return m.Success ? m.Groups[1].Value.Trim() : "";
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    Console.Error.WriteLine("错误: 未找到仓库根（PalORM.slnx 哨兵缺失）");
    Environment.Exit(2);
    return "";
}
