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
var failCount = 0; var warnCount = 0;

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine(" PalORM 测试规范门禁（T1-T14）");
Console.WriteLine($" 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

// ─── T4：测试命名规范 ───
Console.WriteLine();
Console.WriteLine("─── T4: 测试命名规范 ───");
var badNames = new List<string>();
foreach (var f in EnumerateTestCs())
{
    foreach (var line in File.ReadLines(f))
    {
        if (!line.Contains("[Test]")) continue;
        var m = Regex.Match(line, @"public async Task ([A-Za-z_]+)", RegexOptions.IgnoreCase);
        if (m.Success && !m.Groups[1].Value.Contains('_')) badNames.Add(m.Groups[1].Value);
    }
}
if (badNames.Count > 0)
{
    Console.WriteLine("WARN  T4  以下测试方法名不含下划线（建议 Method_Scenario_ExpectedResult）:");
    foreach (var n in badNames.Take(5)) Console.WriteLine($"      - {n}");
    warnCount++;
}
else Console.WriteLine("PASS  T4  测试命名规范检查通过");

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

// ─── T6：外部 DB 测试清理检查（启发式）───
Console.WriteLine();
Console.WriteLine("─── T6: 外部 DB 测试清理检查 ───");
var leakingDrops = 0;
foreach (var f in EnumerateTestCs().Where(f => f.Contains("PalORM.Integration.Tests")))
{
    var i = 0;
    foreach (var line in File.ReadLines(f))
    {
        i++;
        if (!line.Contains("DROP TABLE")) continue;
        if (line.Contains("bin/") || line.Contains("obj/")) continue;
        if (line.Contains("finally")) continue;
        if (line.Contains("IF EXISTS")) continue;
        leakingDrops++;
    }
}
if (leakingDrops > 5)
{
    Console.WriteLine($"WARN  T6  发现 {leakingDrops} 处 DROP TABLE 可能不在 finally 中（需人工审查）");
    warnCount++;
}
else Console.WriteLine($"PASS  T6  DROP TABLE 清理模式检查通过（{leakingDrops} 处需 IF EXISTS 兜底）");

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
Console.WriteLine();
Console.WriteLine("─── T-DEF-4: CI job timeout-minutes ───");
var totalJobs = 0; var timeouts = 0;
foreach (var wf in new[] { ".github/workflows/ci.yml", ".github/workflows/perf-gate.yml" })
{
    if (!File.Exists(wf)) continue;
    var lines = File.ReadAllLines(wf);
    var inJobs = false;
    for (var i = 0; i < lines.Length; i++)
    {
        if (lines[i].StartsWith("jobs:")) { inJobs = true; continue; }
        if (!inJobs) continue;
        // 4 空格缩进 job 定义；uses: 复用薄壳（ITM-802）不计
        if (Regex.IsMatch(lines[i], @"^    [a-z][a-z-]*:$"))
        {
            if (i + 1 < lines.Length && lines[i + 1].Contains("uses:")) continue;
            totalJobs++;
        }
    }
    timeouts += lines.Count(l => l.Contains("timeout-minutes"));
}
if (timeouts < totalJobs)
{
    Console.WriteLine($"WARN  T-DEF-4  CI 有 {totalJobs} 个 job，仅 {timeouts} 个有 timeout-minutes");
    warnCount++;
}
else Console.WriteLine($"PASS  T-DEF-4  所有 CI job 均有 timeout-minutes（{totalJobs} 个）");

// ─── T11：远程 DB 环境变量命名分离（SKIP 登记）───
Console.WriteLine();
Console.WriteLine("─── T11: 远程 DB 环境变量命名分离 ───");
Console.WriteLine("SKIP  T11  bench 与测试共用凭证源为 perf.sh 成文设计（作废定义不再执行；见 ITM-806 裁决）");

// ─── T12：三方一致——包版本号 ───
Console.WriteLine();
Console.WriteLine("─── T12: 三方一致——包版本号 + 连接串参数同步 ───");
var centralVer = FirstVersion("Directory.Build.props");
var t12Ok = true;
foreach (var csproj in new[] { "src/PalORM.Core/PalORM.Core.csproj", "src/PalORM.PostgreSql/PalORM.PostgreSql.csproj" })
{
    var v = FirstVersion(csproj);
    if (v.Length > 0 && v != centralVer)
    {
        Console.WriteLine($"FAIL  T12  包版本号漂移：csproj={v} ≠ Directory.Build.props={centralVer}");
        failCount++;
        t12Ok = false;
    }
}
if (t12Ok) Console.WriteLine($"PASS  T12  包版本号一致（{centralVer}，中央管理于 Directory.Build.props）");

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
