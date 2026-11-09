// check-locked-tests.cs（2026-11-09 综合根治方案 LOCK-1）
// 修复证据门禁：src/ 变更必须伴随 test/ 变更（Karpathy 准则 #5「修 bug 前先写复现测试」
// 的机制保障）。此前 8 个未推送提交中 3 个是 src-only（c0409c3/bcf373f/46e7cc9），改动
// 声称修复但撤掉不会变红——修复流程缺少「锁定测试」环节，补测只能靠人脑记忆。
//
// 用法：dotnet run --file scripts/check-locked-tests.cs
// 契约：0=通过（无 src 变更 / src 与 test 同步变更 / 全部登记例外）；
//       1=src 变更无 test 变更且未登记例外；2=环境错（git 不可用）。
//
// 例外登记：.ai/src-only-registry.md（本地知识层，表格首列为 src 文件路径）。
// 登记理由须写明「纯重构无行为变更」等，人工核验后录入。
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();

var (exitCode, output) = RunCapture("git", "diff --name-only HEAD");
if (exitCode != 0)
{
    Console.WriteLine($"FAIL  check-locked-tests：git diff 失败（exit {exitCode}）");
    return 2;
}

var srcFiles = new List<string>();
var testFiles = new List<string>();
foreach (var line in output.Split('\n'))
{
    var f = line.Trim().Replace('\\', '/');
    if (f.Length == 0) continue;
    if (Regex.IsMatch(f, @"^src/.*\.cs$")) srcFiles.Add(f);
    else if (Regex.IsMatch(f, @"^test/.*\.cs$")) testFiles.Add(f);
}

if (srcFiles.Count == 0)
{
    Console.WriteLine("PASS  check-locked-tests：本次无 src/ 变更");
    return 0;
}
if (testFiles.Count > 0)
{
    Console.WriteLine($"PASS  check-locked-tests：src/ 变更 {srcFiles.Count} 个文件，test/ 同步变更 {testFiles.Count} 个文件");
    return 0;
}

// 例外登记（.ai 是本地知识层，缺失时视为无例外）
const string registry = ".ai/src-only-registry.md";
var exempt = new HashSet<string>(StringComparer.Ordinal);
if (File.Exists(registry))
{
    foreach (var line in File.ReadLines(registry))
    {
        if (!line.StartsWith('|')) continue;
        var cells = line.Split('|');
        if (cells.Length < 3) continue;
        var f = cells[1].Trim().Replace('\\', '/');
        if (f.StartsWith("src/", StringComparison.Ordinal)) exempt.Add(f);
    }
}

var untracked = srcFiles.Where(f => !exempt.Contains(f)).ToList();
if (untracked.Count == 0)
{
    Console.WriteLine($"PASS  check-locked-tests：src/ 变更 {srcFiles.Count} 个文件均已登记例外（{registry}）");
    return 0;
}

Console.WriteLine($"FAIL  check-locked-tests：src/ 变更 {srcFiles.Count} 个文件但 test/ 无同步变更，其中 {untracked.Count} 个未登记例外：");
foreach (var f in untracked) Console.WriteLine($"      {f}");
Console.WriteLine("  处置：补锁定测试（撤修复即红），或在 .ai/src-only-registry.md 登记理由（如「纯重构无行为变更」）。");
return 1;

static (int exitCode, string output) RunCapture(string fileName, string arguments)
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
    return (p.ExitCode, output);
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
