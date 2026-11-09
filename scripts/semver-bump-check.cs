// semver-bump-check.cs（2026-11-05 新增，发布规范 §1.4 的机械化载体）
// SemVer 2.0.0 规则 6/7/8 判定：把「声明升了哪一段」与「本版实际改了什么」对账。
//
// 用法:
//   dotnet run --file scripts/semver-bump-check.cs -- <新版本> [选项]
//   选项:
//     --prev <版本>              指定上一版本（默认取 git tag 中除新版本外的最新语义 tag）
//     --allow-missing-baseline   上一版本无归档 API 基线时降级为告警（默认 FAIL）
// 退出码: 0 = 声明 ≥ 要求；1 = 升得不够或缺证据；2 = 用法错
//
// 判定依据（两个机械来源，不靠人读 diff）:
//   1. MAJOR：src/**/CompatibilitySuppressions.xml 有抑制条目。该文件只能由
//      dotnet pack /p:ApiCompatGenerateSuppressionFile=true 生成，而它只在 Package Validation
//      检测到二进制破坏时才产出条目，等于「本版确有破坏」的书面证据。
//   2. MINOR：当前 API 基线相对 bench/baselines/public-api/v<上一版本>/ 有新增行。
//   3. PATCH：以上皆无。
//
// 前置条件（由 CI 保证，本脚本不复跑）:
//   public-api-snapshot --check 通过，即「当前基线 == 当前构建产物」。
//   缺这条前提时 MINOR 判定会失真，因此在末尾打印该前提。
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: semver-bump-check.cs <new-version> [--prev <version>] [--allow-missing-baseline]");
    return 2;
}

var newVersion = args[0];
var prevArg = "";
var allowMissingBaseline = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--prev":
            prevArg = args[++i];
            break;
        case "--allow-missing-baseline":
            allowMissingBaseline = true;
            break;
        default:
            Console.Error.WriteLine($"::error::未知参数 {args[i]}");
            return 2;
    }
}

var semVer = new Regex(
    @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?$");
var newMatch = semVer.Match(newVersion);
if (!newMatch.Success)
{
    Console.Error.WriteLine($"::error::新版本号不符合 SemVer 2.0.0（且不允许 +build，NuGet 包版本身份不含构建元数据）：{newVersion}");
    return 1;
}

var repoRoot = FindRepoRoot();
Environment.CurrentDirectory = repoRoot;

Console.WriteLine($"═══ SemVer 升版类型校验（{newVersion}）═══");

// ── 1. 上一版本 ──
var prevVersion = prevArg.Length > 0 ? prevArg : LatestTagVersion(exclude: newVersion);
if (prevVersion.Length == 0)
{
    Console.Error.WriteLine("::error::无法确定上一版本：git tag 中没有可比较的语义版本，且未传 --prev");
    return 1;
}
if (!semVer.IsMatch(prevVersion))
{
    Console.Error.WriteLine($"::error::上一版本号不符合 SemVer 2.0.0：{prevVersion}");
    return 1;
}
Console.WriteLine($"  上一版本: {prevVersion}");

// ── 2. 声明段 ──
var declared = BumpOf(prevVersion, newVersion);
if (declared == "BACKWARDS")
{
    Console.Error.WriteLine($"::error::版本号未递增或倒退（{prevVersion} → {newVersion}）：已发布版本不可复用（规则 3）");
    return 1;
}
Console.WriteLine($"  声明升版: {declared}");

// 预发布不承诺兼容性（规则 9），跳过判定
if (newMatch.Groups["pre"].Success)
{
    Console.WriteLine("  预发布版本：SemVer 不为先行版承诺兼容性，跳过升版类型判定");
    return 0;
}

// ── 3. 破坏性证据：Package Validation 抑制条目 ──
var suppressions = FindSuppressions();
if (suppressions.Count > 0)
{
    Console.WriteLine($"  破坏性证据: {suppressions.Count} 条 CompatibilitySuppressions 条目");
    foreach (var hit in suppressions.Take(10))
    {
        Console.WriteLine($"    {hit}");
    }
}
else
{
    Console.WriteLine("  破坏性证据: 无（src/ 下无 CompatibilitySuppressions.xml 条目）");
}

// ── 4. 新增证据：与上一版归档基线比 ──
var archiveDir = Path.Combine("bench", "baselines", "public-api", $"v{prevVersion}");
var adds = new List<string>();
var missingArchive = !Directory.Exists(archiveDir);
if (missingArchive)
{
    Console.WriteLine($"  上一版基线: 缺 {archiveDir}/");
}
else
{
    foreach (var file in Directory.GetFiles(archiveDir, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
    {
        var name = Path.GetFileName(file);
        var currentPath = Path.Combine("bench", "baselines", "public-api", name);
        if (!File.Exists(currentPath))
        {
            Console.WriteLine($"::warning::当前基线缺 {name}，跳过该程序集");
            continue;
        }
        var before = new HashSet<string>(File.ReadAllLines(file), StringComparer.Ordinal);
        var now = File.ReadAllLines(currentPath);
        adds.AddRange(now.Where(line => !before.Contains(line)).Select(line => $"{name}: {line}"));
    }
    Console.WriteLine($"  API 增量: 新增 {adds.Count} 行");
}

if (missingArchive && !allowMissingBaseline)
{
    Console.Error.WriteLine(
        $"::error::上一版本 {prevVersion} 无归档 API 基线 {archiveDir}/，无法判定 MINOR。\n"
        + "  补法：在该版本的可构建检出上运行\n"
        + $"    dotnet run --file scripts/public-api-snapshot.cs -- --archive {prevVersion} --root <该检出路径> --configuration Release\n"
        + "  仅当确认无法补时用 --allow-missing-baseline 降级（会失去 MINOR 判定能力）。");
    return 1;
}

// ── 5. 要求段与对账 ──
// 2026-11-09（综合根治方案 GATE-3）：行为变更（CHANGELOG 💔 段）不改二进制签名，Package
// Validation 拦不住，原判据只认抑制文件 → 行为变更可静默走 PATCH。项目政策（发布变更日志
// 规范 §二/§28）：行为变更走 MINOR + 💔 段 + 升级指南；签名破坏才 MAJOR。
var behaviorBreak = HasBehaviorBreakSection();
var required = suppressions.Count > 0 ? "MAJOR" : (adds.Count > 0 || behaviorBreak) ? "MINOR" : "PATCH";
Console.WriteLine($"  要求升版: {required}" + (behaviorBreak ? "（含行为变更 💔 段）" : ""));

var order = new Dictionary<string, int>(StringComparer.Ordinal) { ["PATCH"] = 1, ["MINOR"] = 2, ["MAJOR"] = 3 };
var declaredRank = order[declared];
var requiredRank = order[required];

if (declaredRank < requiredRank)
{
    Console.Error.WriteLine(
        $"::error::升版类型不足：本版实际需要 {required}，但版本号只升了 {declared}（{prevVersion} → {newVersion}）");
    if (required == "MAJOR")
    {
        Console.Error.WriteLine("  依据：src/ 下存在 CompatibilitySuppressions 条目，证明本版有二进制破坏。");
        Console.Error.WriteLine("  确认这是有意破坏则升 MAJOR；否则修掉破坏并删除抑制文件。");
    }
    else
    {
        if (behaviorBreak)
            Console.Error.WriteLine("  依据：CHANGELOG 顶部版本段的 💔 破坏性/行为变更段有条目（项目政策：行为变更走 MINOR + 💔 段，见发布变更日志规范 §二）。");
        else
            Console.Error.WriteLine("  依据：API 基线相对上一版有新增成员，属于向后兼容的新功能（规则 7）。");
    }
    return 1;
}

if (declaredRank > requiredRank)
{
    Console.WriteLine($"  提示: 本次升得比最低要求高（要求 {required}，声明 {declared}），SemVer 允许（规则 7/8 的 MAY）");
}

// ── 6. 本版归档基线必须已生成（下一版的 MINOR 判定全靠它） ──
// 不做成警告：警告会被忽略，下一版就会退化成「只看得见破坏、看不见新增」。
// 归档内容就是当前基线，而当前基线由 public-api-snapshot --check 保证等于构建产物。
var newArchiveDir = Path.Combine("bench", "baselines", "public-api", $"v{newVersion}");
if (!Directory.Exists(newArchiveDir))
{
    Console.Error.WriteLine(
        $"::error::本版归档基线 {newArchiveDir}/ 不存在，下一版将无法判定 MINOR。\n"
        + $"  发布前依次运行：\n"
        + $"    dotnet run --file scripts/public-api-snapshot.cs -- --check --configuration Release   # 基线须等于构建产物\n"
        + $"    dotnet run --file scripts/public-api-snapshot.cs -- --archive {newVersion} --configuration Release\n"
        + $"  并把 {newArchiveDir}/ 随发布提交入库。");
    return 1;
}

Console.WriteLine($"  本版归档: {newArchiveDir}/ 已存在");

Console.WriteLine("  前提: public-api-snapshot --check 须通过（基线 == 当前构建产物），CI 已覆盖");
Console.WriteLine($"\nPASS 升版类型匹配（{prevVersion} → {newVersion}：声明 {declared}，要求 {required}）");
return 0;

// ── 辅助 ──

static string BumpOf(string from, string to)
{
    var a = from.Split('-')[0].Split('.');
    var b = to.Split('-')[0].Split('.');
    if (int.Parse(b[0]) != int.Parse(a[0]))
    {
        return int.Parse(b[0]) > int.Parse(a[0]) ? "MAJOR" : "BACKWARDS";
    }
    if (int.Parse(b[1]) != int.Parse(a[1]))
    {
        return int.Parse(b[1]) > int.Parse(a[1]) ? "MINOR" : "BACKWARDS";
    }
    if (int.Parse(b[2]) != int.Parse(a[2]))
    {
        return int.Parse(b[2]) > int.Parse(a[2]) ? "PATCH" : "BACKWARDS";
    }
    return "BACKWARDS";
}

static List<string> FindSuppressions()
{
    var hits = new List<string>();
    if (!Directory.Exists("src"))
    {
        return hits;
    }
    foreach (var file in Directory.GetFiles("src", "CompatibilitySuppressions.xml", SearchOption.AllDirectories))
    {
        var rel = file.Replace('\\', '/');
        foreach (var line in File.ReadAllLines(file))
        {
            // <Suppression> 与 <Suppression Attribute="..."> 两种形态都要认
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"<Suppression[\s>]"))
            {
                hits.Add(rel);
                break;
            }
        }
    }
    return hits;
}

static string LatestTagVersion(string exclude)
{
    var psi = new System.Diagnostics.ProcessStartInfo("git", "tag --list \"v*\" --sort=-v:refname")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var tag = raw.Trim();
        if (!tag.StartsWith('v'))
        {
            continue;
        }
        var v = tag[1..];
        if (v.Contains('-'))
        {
            continue; // 预发布 tag 不作为升版基准
        }
        if (v == exclude)
        {
            continue;
        }
        return v;
    }
    return "";
}

// 读 CHANGELOG 顶部第一个版本段（Unreleased 或最新版本）的 💔 小节，有条目即为行为变更。
static bool HasBehaviorBreakSection()
{
    const string changelog = "CHANGELOG.md";
    if (!File.Exists(changelog)) return false;
    var inFirstSection = false;
    var inBreak = false;
    foreach (var line in File.ReadLines(changelog))
    {
        if (line.StartsWith("## [", StringComparison.Ordinal))
        {
            if (inFirstSection) break; // 只读第一个版本段
            inFirstSection = true;
            inBreak = false;
            continue;
        }
        if (!inFirstSection) continue;
        if (line.StartsWith("### ", StringComparison.Ordinal))
        {
            inBreak = line.StartsWith("### 💔", StringComparison.Ordinal);
            continue;
        }
        if (inBreak && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            return true;
    }
    return false;
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
