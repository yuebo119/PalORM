// precommit-build-check.cs（2026-10-17，用户裁定增设）
// 按暂存文件映射受影响项目并构建——把编译断裂拦截在提交前，替代等 CI 5 分钟反馈。
// 契约（与 secret-guard/stub-check 同族）：0=通过/无需构建，1=构建失败，2=用法/环境错。
// 映射规则（自上而下，命中即止）：
//   1. 根级基础设施文件（Directory.Build.props/Directory.Packages.props/.editorconfig/
//      global.json/NuGet.Config/*.slnf/*.slnx）→ 全量 ci.slnf（影响所有项目）。
//   2. bench/BenchmarkDotNet/**（vendored 子树，ci.slnf 登记豁免）→ 跳过。
//   3. docs//.github//.githooks//.ai/ 与 .md/.txt/.html/.yml/.ps1/.cmd 等非编译产物 → 跳过。
//   4. 其余文件 → 向上找最近含 *.csproj 的目录；找不到 → 保守全量（ci.slnf）并提示。
// 构建口径：Debug 增量（最快反馈；analyzer 严重级不随配置变化）。
// 已知边界（登记不掩饰）：本地 SDK 与 CI 的 analyzer 激活集存在版本漂移（2026-10-17
// IDE0033/IDE0305 实例：本地 rc.2 为 suggestion、CI 侧 SDK 为 warning），该类风格回归
// 本段捕不到，仍由 CI 兜底；本段负责编译断裂与本地可见的一切诊断。
using System.Diagnostics;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var root = RunCapture("git", "rev-parse --show-toplevel").Output.Trim();
if (root.Length == 0)
{
    Console.WriteLine("FAIL 无法解析仓库根（git rev-parse --show-toplevel）。");
    return 2;
}

// 暂存文件清单（兼容无 HEAD 的初始提交：退到空树比对）。
// core.quotepath=false + UTF8 解码：git 默认把非 ASCII 路径 C-quote 成八进制形态
// （"docs\346\226\207..."），拿转义名拼路径必炸 IOException——secret-guard.cs 同型修复
// （2026-09-30 中文路径盲区实锤），本段复用该模式，勿退回去裸调 git。
var staged = RunCapture("git", "-c core.quotepath=false diff --cached --name-only");
if (staged.Code != 0)
{
    staged = RunCapture("git", "-c core.quotepath=false diff --cached --name-only 4b825dc642cb6eb9a060e54bf8d69288fbee4904");
}
var files = staged.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (files.Length == 0)
{
    Console.WriteLine("PASS 无暂存文件，无需构建。");
    return 0;
}

// ─── 映射 ───
var projects = new SortedSet<string>(StringComparer.Ordinal);
var skippedDocs = 0;
var skippedVendored = 0;
var conservative = false;

foreach (var file in files)
{
    var rel = file.Replace('\\', '/');

    if (IsRootInfra(rel))
    {
        projects.Add(SlnPath());
        continue;
    }
    if (rel.StartsWith("bench/BenchmarkDotNet/", StringComparison.Ordinal))
    {
        skippedVendored++;
        continue;
    }
    if (IsNonBuild(rel))
    {
        skippedDocs++;
        continue;
    }

    var proj = FindProject(Path.Combine(root, rel));
    if (proj is null)
    {
        conservative = true;
        projects.Add(SlnPath());
        continue;
    }
    projects.Add(Path.GetRelativePath(root, proj).Replace('\\', '/'));
}

if (projects.Count == 0)
{
    Console.WriteLine($"PASS 暂存 {files.Length} 个文件均不涉及编译（非编译文件 {skippedDocs}，vendored 豁免 {skippedVendored}），跳过构建。");
    return 0;
}

if (conservative)
{
    Console.WriteLine("注意：存在无法映射到项目的暂存文件，按保守策略构建全量 ci.slnf。");
}

// ─── 构建（逐项目循环：dotnet build 只接受单个项目参数，多项目传参第二个会被 MSBuild
// 当成开关——2026-10-17 实证。节点默认复用，后续每次构建仍快） ───
Console.WriteLine($"构建 {projects.Count} 个受影响项目（Debug 增量）：");
foreach (var p in projects)
{
    Console.WriteLine($"  - {p}");
}
if (skippedVendored > 0)
{
    Console.WriteLine($"  （跳过 vendored BenchmarkDotNet 子树 {skippedVendored} 个文件，ci.slnf 登记豁免）");
}

var sw = Stopwatch.StartNew();
var failures = new List<string>();
foreach (var p in projects)
{
    var one = RunCapture("dotnet", $"build \"{p}\" -c Debug -v q --nologo");
    if (one.Code != 0)
    {
        failures.Add(p);
        Console.WriteLine(one.Output.Trim());
    }
}
sw.Stop();

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"FAIL 受影响项目构建失败（{string.Join(' ', failures)}，{sw.Elapsed.TotalSeconds:F1}s）。请先修复再提交；");
    Console.WriteLine("     若失败与暂存内容无关（既有断裂），先修复或改用 git stash 后重试。");
    return 1;
}
Console.WriteLine($"PASS 受影响项目构建通过（{projects.Count} 个项目，{sw.Elapsed.TotalSeconds:F1}s）。");
return 0;

// ─── 辅助 ───

static bool IsRootInfra(string rel)
{
    string[] names =
    [
        "Directory.Build.props", "Directory.Packages.props", ".editorconfig",
        "global.json", "NuGet.Config"
    ];
    if (names.Contains(rel, StringComparer.Ordinal)) return true;
    return (rel.EndsWith(".slnf", StringComparison.Ordinal) || rel.EndsWith(".slnx", StringComparison.Ordinal))
        && !rel.Contains('/', StringComparison.Ordinal);
}

static bool IsNonBuild(string rel)
{
    // scripts/ 为 file-based 应用（无 csproj，运行时由 dotnet run --file 自行编译，
    // 且每个已接线段落每次提交都会实际跑一遍=编译一遍），不属于任何项目构建面。
    foreach (var dir in new[] { "docs/", ".github/", ".githooks/", ".ai/", "scripts/" })
    {
        if (rel.StartsWith(dir, StringComparison.Ordinal)) return true;
    }
    foreach (var name in new[] { ".gitignore", ".gitattributes", "LICENSE" })
    {
        if (rel.Equals(name, StringComparison.Ordinal)) return true;
    }
    foreach (var ext in new[] { ".md", ".txt", ".html", ".json", ".yml", ".yaml", ".ps1", ".cmd", ".sh" })
    {
        if (rel.EndsWith(ext, StringComparison.Ordinal)) return true;
    }
    return false;
}

static string SlnPath() => File.Exists("PalORM.ci.slnf") ? "PalORM.ci.slnf" : "PalORM.slnx";

static string? FindProject(string startPath)
{
    var dir = File.Exists(startPath) ? Path.GetDirectoryName(startPath) : startPath;
    while (dir is not null)
    {
        var found = Directory.GetFiles(dir, "*.csproj");
        if (found.Length > 0) return found[0];
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

static (int Code, string Output) RunCapture(string command, string arguments)
{
    var psi = new ProcessStartInfo(command, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        // UTF8 显式解码：本机 Console 为 GBK，不指定会把 git/dotnet 的 UTF-8 输出解成乱码
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    var stdout = new StringWriter();
    var stderr = new StringWriter();
    using var p = Process.Start(psi);
    if (p is null) return (-1, "");
    p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.WriteLine(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.WriteLine(e.Data); };
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    p.WaitForExit();
    return (p.ExitCode, stdout.ToString() + stderr.ToString());
}
