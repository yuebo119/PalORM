// diff-coverage.cs（测试体系优化方案 2026-11-01 · T0-5）
// diff 覆盖率门禁：只统计本次改动涉及的行（git diff -U0 base...head 的行号区间），
// 要求 80% 被 dotnet-coverage 命中。门禁命名 T1-diff，对应 T1 铁律（"新增/修改的
// 生产代码必须有对应测试"）的机械载体。
//
// 与 assert-coverage 的分工：assert-coverage 断言**全量**地板（防整体退化）；
// 本脚本断言**改动行**命中（防"新增 20 行未覆盖方法"从分母增长里溜过去——
// 2026-10-18 定地板 74 后 src 新增 10 个文件把 core 摊薄到 74.33 的教训）。
//
// 白名单只允许三类（与方案 §6.4 一致）：纯注释行、#if 条件编译块、生成物 .g.cs。
//
// --self-check（2026-11-04，测试系统全面优化方案 FIX-3）：内嵌夹具跑比对判据，不调 git、
// 不读 XML。夹具覆盖 Linux 与 Windows 两种 coverage XML 路径形态（FIX-1 修的就是这条：
// 单向反斜杠转换使 Linux runner 上改动行全记未覆盖）、半覆盖报红、无该文件报红。
//
// usage: dotnet run --file scripts/diff-coverage.cs -- <base> <head> <xml>...
//        dotnet run --file scripts/diff-coverage.cs -- --self-check
//   base/head：git ref（如 origin/main、HEAD）
//   xml：一份或多份 dotnet-coverage XML（Core 侧与 Integration 侧都传，
//        命中取并集——Core 路径可能由 Integration 跑到）
// exit 0 = 通过；exit 1 = 有改动行未覆盖超阈值；exit 2 = 输入错误
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

const double ThresholdPercent = 80.0;

if (args.Length == 1 && args[0] == "--self-check")
{
    return SelfCheck(ThresholdPercent);
}

if (args.Length < 3 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("usage: dotnet run --file scripts/diff-coverage.cs -- <base> <head> <coverage.xml>...");
    Console.Error.WriteLine("       dotnet run --file scripts/diff-coverage.cs -- --self-check");
    Console.Error.WriteLine("  例：dotnet run --file scripts/diff-coverage.cs -- origin/dev HEAD cov-core.xml cov-integration.xml");
    return 2;
}
string baseRef = args[0];
string headRef = args[1];
// 空 head 表示"对工作树"（本地预检形态）：git diff BASE -- <file> 会带上未提交改动。
// CI 里传显式 head（如 HEAD 或 PR 的 head SHA），语义是 BASE...HEAD 的改动行。
string diffSpec = string.IsNullOrEmpty(headRef) ? baseRef : $"{baseRef}...{headRef}";
string diffLabel = string.IsNullOrEmpty(headRef) ? $"{baseRef} 起（含工作树）" : $"{baseRef}...{headRef}";
string[] xmlPaths = args[2..];
foreach (string xml in xmlPaths)
{
    if (!File.Exists(xml))
    {
        Console.Error.WriteLine($"::error::覆盖率文件不存在：{xml}（先跑 --coverage 或路径错）");
        return 2;
    }
}
string repoRoot = FindRepoRoot();

// ── 第一步：git diff -U0 拿改动行 ────────────────────────────────────
// -U0（零上下文）让 @@ 每段恰好只含改动行：hunk 头 +N,count 表示新文件
// 从 N 起连续 count 行都是新增/修改。白名单在此判定（行内容随 diff 输出）。
var diffLines = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);   // 文件 → 新增行号
var skippedFiles = new List<string>();
foreach (string file in ListChangedFiles(repoRoot, diffSpec))
{
    if (IsExcluded(file)) { skippedFiles.Add(file); continue; }
    string body = RunGit(repoRoot, $"diff -U0 {diffSpec} -- \"{file}\"");
    var lines = new HashSet<int>();
    bool inHunk = false;
    int nextLine = 0;
    foreach (string raw in body.Split('\n'))
    {
        string line = raw.TrimEnd('\r');
        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            Match m = Regex.Match(line, @"\+(\d+)(?:,(\d+))?");
            if (!m.Success) { inHunk = false; continue; }
            nextLine = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int count = m.Groups[2].Success
                ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            inHunk = count > 0;
            continue;
        }
        if (!inHunk) continue;
        // -U0 下 hunk 内每行都带 '+'（新增/修改）或 '-'（删除）；只数 '+'（新文件行号）
        if (line.StartsWith('+', StringComparison.Ordinal))
        {
            string content = line[1..].Trim();
            // 白名单：纯注释行不算进分母（对齐方案 §6.4）
            if (content.Length == 0 || content.StartsWith("//", StringComparison.Ordinal)
                || (content.StartsWith("/*", StringComparison.Ordinal) && content.EndsWith("*/", StringComparison.Ordinal)))
            {
                nextLine++;
                continue;
            }
            lines.Add(nextLine);
            nextLine++;
        }
        else if (line.StartsWith('-', StringComparison.Ordinal))
        {
            // 删除行不占新文件行号
        }
        else
        {
            // -U0 理论上不出现上下文行；出现时按新文件行推进防御性处理
            nextLine++;
        }
    }
    if (lines.Count > 0) diffLines[file] = lines;
}

if (diffLines.Count == 0)
{
    Console.WriteLine("diff-coverage: 无可统计的改动行（纯删除/注释/生成物）→ PASS");
    return 0;
}

// ── 第二步：XML 行命中映射（并集）────────────────────────────────────
// source_file 的绝对 path → id；range 的 source_id + start/end/covered → 文件行集。
// 同一行在多份 XML（Core 采集 + Integration 采集）任一命中即算覆盖。
var coveredByFile = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
foreach (string xmlPath in xmlPaths)
{
    string xml = File.ReadAllText(xmlPath);
    var idToPath = new Dictionary<int, string>();
    foreach (Match sf in Regex.Matches(xml, "<source_file id=\"(\\d+)\" path=\"([^\"]+)\""))
    {
        idToPath[int.Parse(sf.Groups[1].Value, CultureInfo.InvariantCulture)] = sf.Groups[2].Value;
    }
    foreach (Match rg in Regex.Matches(xml,
        "<range source_id=\"(\\d+)\" start_line=\"(\\d+)\" end_line=\"(\\d+)\"[^>]*covered=\"(yes|no)\""))
    {
        if (rg.Groups[4].Value != "yes") continue;
        int id = int.Parse(rg.Groups[1].Value, CultureInfo.InvariantCulture);
        if (!idToPath.TryGetValue(id, out string? path)) continue;
        if (!coveredByFile.TryGetValue(path, out var set))
            coveredByFile[path] = set = [];
        for (int ln = int.Parse(rg.Groups[2].Value, CultureInfo.InvariantCulture);
             ln <= int.Parse(rg.Groups[3].Value, CultureInfo.InvariantCulture); ln++)
        {
            set.Add(ln);
        }
    }
}

// ── 第三步：比对 ─────────────────────────────────────────────────────
(int totalRelevant, int totalCovered, List<string> uncoveredDetail) = Compare(diffLines, coveredByFile);

double percent = totalRelevant == 0 ? 100.0 : (double)totalCovered / totalRelevant * 100;
string percentText = percent.ToString("F1", CultureInfo.InvariantCulture);
Console.WriteLine($"diff-coverage（{diffLabel}，{xmlPaths.Length} 份 XML）：");
Console.WriteLine($"  改动行 {totalRelevant}，命中 {totalCovered}（{percentText}%，阈值 {ThresholdPercent.ToString(CultureInfo.InvariantCulture)}%）");
if (skippedFiles.Count > 0)
    Console.WriteLine($"  跳过 {skippedFiles.Count} 个白名单文件（生成物/obj）");
if (uncoveredDetail.Count > 0)
{
    Console.WriteLine("  未覆盖明细：");
    foreach (string detail in uncoveredDetail) Console.WriteLine(detail);
}

if (percent < ThresholdPercent)
{
    Console.Error.WriteLine($"::error::diff 覆盖率 {percentText}% < 阈值 {ThresholdPercent.ToString(CultureInfo.InvariantCulture)}%——新增/修改的行需要测试命中（T1）");
    return 1;
}
Console.WriteLine("diff-coverage: PASS");
return 0;

// ── 比对判据（自检与正式路径共用）──
// 统一按 POSIX 分隔比较：git 输出正斜杠；coverage XML 在 Linux runner 上是
// /home/runner/work/... 正斜杠，在 Windows 上是 C:\... 反斜杠。此前单向往
// 反斜杠转换，Linux CI 上 XML 路径永不以 src\A\B.cs 结尾，改动行全部记为
// 未覆盖（假红）；改动集为空时又走上面的空集 PASS（假绿）。两处都已实测。
static (int Total, int Covered, List<string> Detail) Compare(
    Dictionary<string, HashSet<int>> diffLines,
    Dictionary<string, HashSet<int>> coveredByFile)
{
    int totalRelevant = 0, totalCovered = 0;
    var uncoveredDetail = new List<string>();
    foreach ((string file, HashSet<int> lines) in diffLines)
    {
        string normalized = file.Replace('\\', '/').TrimStart('.', '/');
        HashSet<int>? covered = null;
        foreach ((string xmlPath, HashSet<int> set) in coveredByFile)
        {
            string xmlNormalized = xmlPath.Replace('\\', '/');
            if (xmlNormalized.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase))
            {
                covered = set;
                break;
            }
        }
        int fileRelevant = lines.Count;
        int fileCovered = 0;
        if (covered is not null)
        {
            foreach (int ln in lines)
            {
                if (covered.Contains(ln)) fileCovered++;
            }
        }
        totalRelevant += fileRelevant;
        totalCovered += fileCovered;
        if (fileCovered < fileRelevant)
        {
            var missed = new List<int>();
            if (covered is not null)
            {
                foreach (int ln in lines)
                {
                    if (!covered.Contains(ln)) missed.Add(ln);
                }
            }
            else
            {
                missed.AddRange(lines);
            }
            uncoveredDetail.Add($"  {file}: {fileCovered}/{fileRelevant} 命中，未覆盖行 {string.Join(",", missed.Take(10))}{(missed.Count > 10 ? "…" : "")}");
        }
    }
    return (totalRelevant, totalCovered, uncoveredDetail);
}

// ── 自检夹具 ──
static int SelfCheck(double thresholdPercent)
{
    HashSet<int> changed = [10, 11, 12, 13, 14, 15, 16, 17, 18, 19];
    var diffLines = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal)
    {
        ["src/PalORM.Core/A.cs"] = changed,
    };
    var fullLinux = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase)
    {
        ["/home/runner/work/Pal.ORM/src/PalORM.Core/A.cs"] = [.. changed],
    };
    var fullWindows = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\agent\_work\Pal.ORM\src\PalORM.Core\A.cs"] = [.. changed],
    };
    var half = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase)
    {
        ["/home/runner/work/Pal.ORM/src/PalORM.Core/A.cs"] = [10, 11, 12, 13, 14],
    };
    var none = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

    var cases = new List<(string Name, int Expected, int Actual)>
    {
        ("好夹具（Linux 形态路径 10/10 命中）", 0, Judge(diffLines, fullLinux, thresholdPercent)),
        ("好夹具②（Windows 形态路径 10/10 命中）", 0, Judge(diffLines, fullWindows, thresholdPercent)),
        ("坏夹具①（5/10 命中 = 50% < 阈值）", 1, Judge(diffLines, half, thresholdPercent)),
        ("坏夹具②（XML 无该文件，全未覆盖）", 1, Judge(diffLines, none, thresholdPercent)),
    };

    bool ok = true;
    foreach ((string name, int expected, int actual) in cases)
    {
        bool behaved = expected == actual;
        ok &= behaved;
        Console.WriteLine($"  {(behaved ? "PASS" : "FAIL")} {name}：期望 exit {expected}，实际 exit {actual}");
    }
    if (ok)
    {
        Console.WriteLine("diff-coverage self-check: PASS（好夹具 exit 0 / 坏夹具如期 exit 1）");
        return 0;
    }
    Console.Error.WriteLine("::error::diff-coverage self-check: FAIL（判据行为与夹具期望不符）");
    return 1;
}

static int Judge(
    Dictionary<string, HashSet<int>> diffLines,
    Dictionary<string, HashSet<int>> coveredByFile,
    double thresholdPercent)
{
    (int total, int covered, _) = Compare(diffLines, coveredByFile);
    double percent = total == 0 ? 100.0 : (double)covered / total * 100;
    return percent < thresholdPercent ? 1 : 0;
}

static bool IsExcluded(string file)
    => file.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
    || file.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
    || file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
    || file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
    || file.StartsWith("bench/", StringComparison.Ordinal)
    || file.StartsWith("test/", StringComparison.Ordinal)
    || file.StartsWith("tools/", StringComparison.Ordinal)
    || file.StartsWith("scripts/", StringComparison.Ordinal)
    || file.StartsWith("docs/", StringComparison.Ordinal)
    || file.StartsWith(".ai/", StringComparison.Ordinal);

static List<string> ListChangedFiles(string repoRoot, string diffSpec)
{
    string output = RunGit(repoRoot, $"diff --name-only {diffSpec}");
    return [.. output.Split('\n')
        .Select(static l => l.TrimEnd('\r').Trim())
        .Where(static l => l.Length > 0 && l.EndsWith(".cs", StringComparison.Ordinal))];
}

static string RunGit(string repoRoot, string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = arguments,
        WorkingDirectory = repoRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var process = Process.Start(psi)!;
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit(30_000);
    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine($"::error::git {arguments} 失败（exit {process.ExitCode}）：{process.StandardError.ReadToEnd()}");
        Environment.Exit(2);
    }
    return output;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    Console.Error.WriteLine("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}
