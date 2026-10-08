// tech-debt-scan.cs（自 .ai/scripts/tech-debt-scan.sh 迁移，脚本全面 C# 化轮）
// PalORM 技术债扫描——14 类检查，每项零残留即通过。
// 迁移修正（按现形态重登记，DDD F-07 同款）：检查 8 的 Python 依赖消除（原 python3 缺失
// 假绿事故的根源直接不存在了，B 族防线失明根因退役）；检查 3 原 bash 引号 bug（'|| true'
// 被包进 grep -v 模式）按意图修正；检查 13 原对照 scripts/secret-guard.sh 已 C# 化且
// 钩子机制已改 core.hooksPath=.githooks，按现形态检查。
// 契约：0=全过；1=有 FAIL；stdout 与 .sh 版对拍兼容。
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();
var passCount = 0; var failCount = 0;

Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine($" PalORM 技术债扫描 ({DateTime.Now:yyyy-MM-dd HH:mm})");
Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine();

// ─── 1. [Obsolete] 残留（WARN 级，有明确移除计划的允许）───
// ITM-681：锚定行首——注释行（/// [Obsolete...）不算真实特性（B13 同型）
var obsolete = GrepLines(Rx(@"^\s*\[Obsolete"), null, "src", "test", "tools", "bench");
if (obsolete.Count == 0)
{
    Console.WriteLine("PASS  1. [Obsolete] 残留 (0 处)");
    passCount++;
}
else
{
    Console.WriteLine($"WARN  1. [Obsolete] 残留 ({obsolete.Count} 处——有明确移除计划的允许)");
    foreach (var l in obsolete.Take(5)) Console.WriteLine("      " + l);
    passCount++;
}

Check("2. TODO/HACK/FIXME 注释", GrepLines(Rx(@"// TODO|// HACK|// FIXME|// XXX"), "bench/BenchmarkDotNet/", "src", "test", "tools", "bench"), strict: true);
Check("3. Console.WriteLine 在 src/", GrepLines(Rx(@"Console\."), null, "src"), strict: true);
Check("4. 空 catch 无注释", GrepLines(Rx(@"catch.*\{\}"), null, "src", "test"), strict: true);
Check("5. tab 字符（应为 space）", TabFiles(), strict: true);
Check("6. src/ 超长行 > 180（SourceGen 允许）", LongLines("src", excludeSourceGen: true, excludeAotTest: false, max: 20), strict: false);
Check("7. test/ 超长行 > 180（AotTest 允许）", LongLines("test", excludeSourceGen: false, excludeAotTest: true, max: 20), strict: false);

// ─── 8. SuppressMessage 无 Justification（原 Python DOTALL 多行扫描 → C# Regex）───
var noJust = new List<string>();
var suppressRegex = new Regex(@"\[SuppressMessage\([^]]+\)\]", RegexOptions.Singleline);
foreach (var f in EnumerateCs("src"))
{
    var content = File.ReadAllText(f);
    foreach (Match m in suppressRegex.Matches(content))
    {
        if (!m.Value.Contains("Justification"))
        {
            var line = content[..m.Index].Count(c => c == '\n') + 1;
            noJust.Add($"{f}:{line} MISSING Justification");
        }
    }
}
Check("8. SuppressMessage 无 Justification", noJust, strict: true);

// ─── 9. 测试用例数对照（grep [Test] 标记数，不跑测试；B14 口径）───
var coreCount = CountTestMarkers("test/PalORM.Core.Tests");
var sgCount = CountTestMarkers("test/PalORM.SourceGen.Tests");
var intCount = CountTestMarkers("test/PalORM.Integration.Tests");
var actual = coreCount + sgCount + intCount;
if (actual >= 400)
{
    Console.WriteLine($"PASS  9. 测试用例数充足（[Test] 标记 {actual} 个：Core={coreCount} SourceGen={sgCount} Integration={intCount}）");
    passCount++;
}
else
{
    Console.WriteLine($"FAIL  9. 测试用例数不足（[Test] 标记 {actual} 个）");
    failCount++;
}

// ─── 10. 版本号三方一致（Build.props = csproj = README 安装 = 消费者 = tag）───
var baseVer = ExtractFirst(File.Exists("Directory.Build.props") ? File.ReadAllText("Directory.Build.props") : "", @"<Version>([^<]+)");
var mismatch = "";
if (baseVer.Length == 0) mismatch += " 版本真源缺失";
foreach (var v in DistinctVersions("src/*/*.csproj", @"<Version>([^<]+)")) if (v != baseVer) mismatch += $" csproj={v}";
foreach (var v in DistinctVersions("README.md", """Include="PalORM\.[A-Za-z]+" Version="([0-9.]+)""")) if (v != baseVer) mismatch += $" README-安装={v}";
foreach (var v in DistinctVersions("test/PalORM.PackageConsumer.Aot/*.csproj", """Include="PalORM\.[A-Za-z]+" Version="([0-9.]+)""")) if (v != baseVer) mismatch += $" 消费者={v}";
var tagVer = RunCapture("git", "tag --sort=-v:refname").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimStart('v') ?? "";
if (tagVer.Length > 0 && tagVer != baseVer) mismatch += $" tag=v{tagVer}";
if (mismatch.Length == 0)
{
    Console.WriteLine($"PASS  10. 版本三方一致 ({baseVer} + README + 消费者 + tag)");
    passCount++;
}
else
{
    Console.WriteLine($"FAIL  10. 版本三方不一致 (基线={baseVer}:{mismatch})");
    failCount++;
}

// ─── 11. PALORM006/007 占位诊断复发检测（排除注释行与已删除记录）───
var g11 = new List<string>();
if (File.Exists("src/PalORM.SourceGen/PalORMAnalyzer.cs"))
{
    foreach (var (line, idx) in File.ReadLines("src/PalORM.SourceGen/PalORMAnalyzer.cs").Select((l, i) => (l, i + 1)))
    {
        if (!line.Contains("PALORM006") && !line.Contains("PALORM007")) continue;
        if (Regex.IsMatch(line, @"^\s*//")) continue;
        if (line.Contains("已删除") || line.Contains("已删") || line.Contains("已移除")) continue;
        g11.Add($"src/PalORM.SourceGen/PalORMAnalyzer.cs:{idx}:{line.Trim()}");
    }
}
Check("11. PALORM006/007 占位诊断复发", g11, strict: true);

// ─── 12. .gitignore 关键排除项 ───
var gitignoreOk = true;
if (File.Exists(".gitignore"))
{
    var gi = File.ReadAllText(".gitignore");
    foreach (var pattern in new[] { ".env.test", ".vscode/", ".idea/", "bin/", "obj/" })
    {
        if (!gi.Contains(pattern))
        {
            Console.WriteLine($"FAIL  12. .gitignore 缺少排除项: {pattern}");
            failCount++;
            gitignoreOk = false;
            break;
        }
    }
}
else
{
    Console.WriteLine("FAIL  12. .gitignore 缺失");
    failCount++;
    gitignoreOk = false;
}
if (gitignoreOk)
{
    Console.WriteLine("PASS  12. .gitignore 关键排除项完整");
    passCount++;
}

// ─── 13. 钩子接线检查（按现形态：core.hooksPath=.githooks + pre-commit 存在）───
// 原检查对照 scripts/secret-guard.sh vs .git/hooks/pre-commit 的 cp 副本——该形态已被
// core.hooksPath 指向 .githooks/ 取代，且源脚本已 C# 化（secret-guard.cs）。
var hooksPath = RunCapture("git", "config core.hooksPath").Trim();
if (hooksPath.Length == 0 && !File.Exists(".git/hooks/pre-commit"))
{
    Console.WriteLine("PASS  13. 提交钩子未安装（跳过）");
    passCount++;
}
else if (hooksPath == ".githooks" && File.Exists(".githooks/pre-commit"))
{
    Console.WriteLine("PASS  13. 提交钩子接线正常（core.hooksPath=.githooks）");
    passCount++;
}
else
{
    Console.WriteLine($"FAIL  13. 提交钩子接线异常（hooksPath={hooksPath}）——执行: git config core.hooksPath .githooks");
    failCount++;
}

// ─── 14. CI 盲区：本地分支领先远端（B181）───
var hasOrigin = RunCapture("git", "rev-parse --verify -q origin/dev").Trim().Length > 0
             && RunCapture("git", "rev-parse --verify -q origin/main").Trim().Length > 0;
if (!hasOrigin)
{
    Console.WriteLine("PASS  14. CI 盲区检查跳过（远端引用不可用：网络或首次克隆）");
    passCount++;
}
else
{
    var aheadDev = int.Parse(RunCapture("git", "rev-list --count origin/dev..dev").Trim() is { Length: > 0 } s1 ? s1 : "0");
    var aheadMain = int.Parse(RunCapture("git", "rev-list --count origin/main..main").Trim() is { Length: > 0 } s2 ? s2 : "0");
    if (aheadDev <= 10 && aheadMain <= 10)
    {
        Console.WriteLine($"PASS  14. CI 盲区正常（dev 领先 {aheadDev} / main 领先 {aheadMain}，阈值 10）");
        passCount++;
    }
    else
    {
        Console.WriteLine($"FAIL  14. CI 盲区：dev 领先 {aheadDev} / main 领先 {aheadMain}（阈值 10）——尽快 push 恢复 CI 反馈回路，防发布门禁沦为首次 CI 接触");
        failCount++;
    }
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine($" 结果: {passCount} 通过 / {failCount} 失败");
Console.WriteLine("═══════════════════════════════════════════");
return failCount > 0 ? 1 : 0;

// ── 辅助 ──

void Check(string name, List<string> result, bool strict)
{
    if (strict && result.Count > 0)
    {
        Console.WriteLine($"FAIL  {name} ({result.Count} 处)");
        foreach (var l in result.Take(5)) Console.WriteLine("      " + l);
        failCount++;
    }
    else
    {
        Console.WriteLine($"PASS  {name} ({result.Count} 处)");
        passCount++;
    }
}

static System.Text.RegularExpressions.Regex Rx(string pattern)
{
    return new(pattern, RegexOptions.Compiled);
}

static List<string> GrepLines(System.Text.RegularExpressions.Regex regex, string? extraExclude, params string[] dirs)
{
    var hits = new List<string>();
    foreach (var dir in dirs)
    {
        if (!Directory.Exists(dir)) continue;
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = f.Replace('\\', '/');
            if (rel.Contains("obj/") || rel.Contains("bin/")) continue;
            if (extraExclude is not null && rel.Contains(extraExclude)) continue;
            var i = 0;
            foreach (var line in File.ReadLines(f))
            {
                i++;
                if (regex.IsMatch(line)) hits.Add($"{rel}:{i}:{line}");
            }
        }
    }
    return hits;
}

static List<string> TabFiles()
{
    var hits = new List<string>();
    foreach (var f in Directory.EnumerateFiles("src", "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
    {
        var rel = f.Replace('\\', '/');
        if (rel.Contains("obj/") || rel.Contains("bin/")) continue;
        if (File.ReadAllText(f).Contains('\t')) hits.Add(rel);
    }
    return hits;
}

static List<string> LongLines(string root, bool excludeSourceGen, bool excludeAotTest, int max)
{
    var hits = new List<string>();
    foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
    {
        var rel = f.Replace('\\', '/');
        if (rel.Contains("obj/") || rel.Contains("bin/")) continue;
        if (excludeSourceGen && rel.Contains("PalORM.SourceGen/")) continue;
        if (excludeAotTest && rel.Contains("PalORM.AotTest")) continue;
        var i = 0;
        foreach (var line in File.ReadLines(f))
        {
            i++;
            if (line.Length > 180) hits.Add($"{rel}:{i}");
        }
    }
    return [.. hits.Take(max)];
}

static IEnumerable<string> EnumerateCs(string root)
{
    foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
    {
        var rel = f.Replace('\\', '/');
        if (rel.Contains("obj/") || rel.Contains("bin/")) continue;
        yield return rel;
    }
}

static int CountTestMarkers(string dir)
{
    var total = 0;
    foreach (var f in EnumerateCs(dir)) total += File.ReadLines(f).Count(l => l.Contains("[Test]"));
    return total;
}

static string ExtractFirst(string content, string pattern)
{
    var m = Regex2(pattern).Match(content);
    return m.Success ? m.Groups[1].Value.Trim() : "";
}

static IEnumerable<string> DistinctVersions(string glob, string pattern)
{
    // glob 支持 src/*/*.csproj 与单文件两种形态（与 .sh 的 grep 目标集合对齐）
    var files = new List<string>();
    if (glob.Contains('*'))
    {
        var parts = glob.Split('/');
        var root = parts[0];
        foreach (var mid in Directory.Exists(root) ? Directory.GetDirectories(root) : [])
        {
            var candidate = Path.Combine(mid, parts[^1]);
            if (File.Exists(candidate)) files.Add(candidate);
        }
    }
    else if (File.Exists(glob)) files.Add(glob);
    var versions = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var f in files)
    {
        foreach (Match m in Regex2(pattern).Matches(File.ReadAllText(f)))
        {
            versions.Add(m.Groups[1].Value.Trim());
        }
    }
    return versions;
}

static System.Text.RegularExpressions.Regex Regex2(string pattern)
{
    return new(pattern, RegexOptions.Compiled);
}

static string RunCapture(string fileName, string arguments)
{
    var psi = new ProcessStartInfo(fileName, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    return output;
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
