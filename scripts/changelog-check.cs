// changelog-check.cs（2026-10-26 新增，发布变更日志规范 §四 的机械化载体）
// CHANGELOG 版本段结构校验：标题格式、日期权威性与单调性、条目非空、💔 段完整性。
//
// 用法: dotnet run --file scripts/changelog-check.cs -- <version> [old-version]
//   <version>      本次发布的目标版本号（如 6.8.0）
//   [old-version]  可选，上一版本号；给出后按 SemVer 推导升级类型并做 PATCH×💔 警告
// 退出码: 0 = 通过；1 = 有 FAIL；2 = 用法错
//
// 设计约束: 只校验 CHANGELOG 顶部第一个版本段（目标版本）。历史段是审计记录，
// 不可回改也不扫——校验回归零的前提是门禁不碰已发布内容。
using System.Text;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: changelog-check.cs <version> [old-version]");
    return 2;
}
var version = args[0];
var oldVersion = args.Length > 1 ? args[1] : "";

var repoRoot = FindRepoRoot();
Environment.CurrentDirectory = repoRoot;

if (!File.Exists("CHANGELOG.md"))
{
    Console.Error.WriteLine("::error::CHANGELOG.md 不存在");
    return 1;
}

Console.WriteLine($"═══ CHANGELOG 版本段校验（{version}）═══");

var semVer = new Regex(
    @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$");
if (!semVer.IsMatch(version))
{
    Console.Error.WriteLine($"::error::目标版本号不符合 SemVer 2.0.0：{version}（格式门禁见 release-version-scan.cs）");
    return 1;
}

// 逐行扫描（不用 $ 锚点，回避 CRLF 与 MSYS 行尾差异）
var lines = File.ReadAllLines("CHANGELOG.md")
    .Select(l => l.TrimEnd('\r'))
    .ToArray();

// 版本段标题：正则不锚定行尾，容忍历史段的可选副标题（如 "## [6.7.0] - 2026-10-23 · 请求级..."）。
// [Unreleased] 是 Keep a Changelog 约定的可选前置段，不是版本段，校验时跳过。
var headingRe = new Regex(@"^## \[(?<ver>[^\]]+)\] - (?<date>\d{4}-\d{2}-\d{2})");
var unreleasedRe = new Regex(@"^## \[Unreleased\]");

var headingIndexes = new List<int>();
var versionHeadings = new List<(int Index, string Version, string Date)>();
for (var i = 0; i < lines.Length; i++)
{
    if (!lines[i].StartsWith("## [", StringComparison.Ordinal))
    {
        continue;
    }
    headingIndexes.Add(i);
    var hm = headingRe.Match(lines[i]);
    if (hm.Success)
    {
        versionHeadings.Add((i, hm.Groups["ver"].Value, hm.Groups["date"].Value));
    }
}

if (headingIndexes.Count == 0)
{
    Console.Error.WriteLine("::error::CHANGELOG 未找到任何版本标题（期望 ## [X.Y.Z] - YYYY-MM-DD）");
    return 1;
}

var fail = 0;
var warn = 0;

// ── 1. 目标版本段定位（跳过可选的 [Unreleased] 前置段） ──
if (versionHeadings.Count == 0)
{
    Console.Error.WriteLine("::error::CHANGELOG 无任何合法版本段标题（全部 ## 行都不是 ## [X.Y.Z] - YYYY-MM-DD 形态）");
    return 1;
}
var target = versionHeadings[0];
var topVersion = target.Version;
var topDate = target.Date;
if (unreleasedRe.IsMatch(lines[headingIndexes[0]]))
{
    Console.WriteLine("  前置段: [Unreleased]（未发布累积变更，校验跳过）");
}
Console.WriteLine($"  目标版本: {topVersion}（标题格式 ✓，日期 {topDate}）");

// ── 2. 顶部版本 == 目标版本 ──
if (topVersion != version)
{
    Console.Error.WriteLine($"::error::CHANGELOG 顶部版本 {topVersion} ≠ 目标版本 {version}（新版本段必须置于文件顶部）");
    fail = 1;
}

// ── 3. 日期权威性：tag 已存在时精确比对 tag 提交日 ──
var tagDate = Git($"log -1 --format=%ad --date=short v{version}").Trim();
if (tagDate.Length == 0)
{
    Console.WriteLine("  日期比对: tag 尚未创建（首发正常），跳过与 tag 提交日的比对");
}
else if (tagDate != topDate)
{
    Console.Error.WriteLine($"::error::版本段日期 {topDate} ≠ tag v{version} 提交日 {tagDate}");
    fail = 1;
}
else
{
    Console.WriteLine($"  日期比对: 与 tag v{version} 提交日一致 ✓");
}

// ── 4. 日期单调：不得早于下一版本段 ──
if (versionHeadings.Count > 1)
{
    var next = versionHeadings[1];
    if (string.CompareOrdinal(topDate, next.Date) < 0)
    {
        Console.Error.WriteLine($"::error::日期倒序：{topVersion} 为 {topDate}，早于下一版本 {next.Version} 的 {next.Date}");
        fail = 1;
    }
    else
    {
        Console.WriteLine($"  日期单调: {topDate} ≥ {next.Version} 的 {next.Date} ✓");
    }
}

// ── 5. 条目非空 + 💔 段完整性 ──
var sectionEnd = headingIndexes.FirstOrDefault(idx => idx > target.Index, lines.Length);
var section = lines[(target.Index + 1)..sectionEnd];
var bulletCount = section.Count(l => l.StartsWith("- ", StringComparison.Ordinal));
Console.WriteLine($"  条目数: {bulletCount} 条");
if (bulletCount == 0)
{
    Console.Error.WriteLine("::error::版本段无任何条目（至少 1 条 \"- \" 变更描述）");
    fail = 1;
}

// 段标题以 "### 💔" 开头即认（新格式为 "### 💔 破坏性/行为变更"，
// 历史段的 "### 💔 破坏性变更" 同样识别——💔 是语义标记，措辞可微调）
var breakIdx = Array.FindIndex(section, l => l.StartsWith("### 💔", StringComparison.Ordinal));
if (breakIdx >= 0)
{
    var breakEnd = Array.FindIndex(section, breakIdx + 1, l => l.StartsWith("### ", StringComparison.Ordinal));
    var breakSpan = breakEnd >= 0 ? section[(breakIdx + 1)..breakEnd] : section[(breakIdx + 1)..];
    var breakBullets = breakSpan.Count(l => l.StartsWith("- ", StringComparison.Ordinal));
    Console.WriteLine($"  💔 段: 存在（{section[breakIdx]}），{breakBullets} 条");
    if (breakBullets == 0)
    {
        Console.Error.WriteLine("::error::💔 段存在但无条目（空段必须整段删除）");
        fail = 1;
    }

    // ── 6. PATCH 版本带 💔 警告（历史先例见发布变更日志规范 §二，不阻断） ──
    if (oldVersion.Length > 0 && semVer.IsMatch(oldVersion))
    {
        var o = oldVersion.Split('-')[0].Split('+')[0].Split('.');
        var n = version.Split('-')[0].Split('+')[0].Split('.');
        var bump = int.Parse(n[0]) != int.Parse(o[0]) ? "MAJOR"
            : int.Parse(n[1]) != int.Parse(o[1]) ? "MINOR"
            : int.Parse(n[2]) != int.Parse(o[2]) ? "PATCH"
            : "无核心版本变化";
        if (bump == "PATCH")
        {
            Console.Error.WriteLine("::warning::PATCH 版本段出现 💔 段——请确认这是修复而非新破坏（PATCH 不应含行为变更）");
            warn = 1;
        }
        else
        {
            Console.WriteLine($"  升级类型: {bump}（{oldVersion} → {version}）");
        }
    }
}
else
{
    Console.WriteLine("  💔 段: 无（本版无破坏性/行为变更）");
}

if (fail != 0)
{
    Console.Error.WriteLine("\nFAIL CHANGELOG 版本段校验未通过");
    return 1;
}
Console.WriteLine(warn != 0
    ? "\nPASS（含 1 条警告，见上）CHANGELOG 版本段校验通过"
    : "\nPASS CHANGELOG 版本段校验通过");
return 0;

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

static string Git(string arguments)
{
    var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return stdout;
}