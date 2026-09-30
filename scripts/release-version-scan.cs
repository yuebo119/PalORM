// release-version-scan.cs（自 release-version-scan.sh 迁移，脚本 C# 化整改方案 T3-1）
// 升版本旧号残留机械扫描（发布规范 §1.3 的脚本化，2026-09-25；教训 B91 催生）。
//
// 用法: dotnet run --file scripts/release-version-scan.cs -- <旧版本> [新版本]
//   <旧版本>  升级前版本号（如 6.0.1）——扫描其残留
//   [新版本]  可选——正向核对其已落真源（Directory.Build.props 的 <Version>）
//
// 扫描面刻意排除 CHANGELOG / docs 历史叙述 / docs/review 审计——那些旧版本字样是
// 历史记录与当时规划，改了是篡改审计史（B88 的分层纪律）。
//
// 退出码: 0 = 零残留（且新版本已落真源）；1 = 有残留或正向缺失（打印 ::error:: 清单）
using System.Text;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: release-version-scan.cs <old-version> [new-version]");
    return 1;
}
var oldVersion = args[0];
var newVersion = args.Length > 1 ? args[1] : "";

var root = Git("rev-parse --show-toplevel").Trim();
Environment.CurrentDirectory = root;

// 点号转义为字面量（版本号场景 Regex.Escape 与 .sh 的 sed 转义等价）
var oldRe = Regex.Escape(oldVersion);

var fail = 0;

// report 与 .sh 同走 stdout（::error:: 注解从 stdout 也能被 GitHub 识别；流向一致性属 D8）
void Report(string desc, string matches)
{
    if (matches.Length > 0)
    {
        Console.WriteLine($"::error::版本残留（{desc}）:");
        Console.WriteLine(matches);
        fail = 1;
    }
}

static string JoinNonEmpty(params string[] parts)
{
    return string.Join('\n', parts.Where(p => p.Length > 0));
}

// grep -rn 形态：递归 + 行号；--include 过滤；--exclude-dir obj/bin（路径任意段目录名相等）
string GrepRecursiveEx(string baseDir, string includePattern, Regex pattern)
{
    List<string> hits = [];
    if (!Directory.Exists(baseDir))
    {
        return "";
    }
    foreach (var file in Directory.EnumerateFiles(baseDir, includePattern, SearchOption.AllDirectories)
                 .OrderBy(f => f, StringComparer.Ordinal))
    {
        var rel = file.Replace('\\', '/');
        if (ExcludedDir(rel))
        {
            continue;
        }
        var lines = SafeReadLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            if (pattern.IsMatch(lines[i]))
            {
                hits.Add($"{rel}:{i + 1}:{lines[i]}");
            }
        }
    }
    return string.Join('\n', hits);
}

static string[] SafeReadLines(string file)
{
    try
    {
        return File.ReadAllLines(file);
    }
    catch (IOException)
    {
        return [];
    }
}

// 排除目录判断需匹配 grep --exclude-dir 语义：路径任意段的目录名相等
static bool ExcludedDir(string relativePath)
{
    var segments = relativePath.Replace('\\', '/').Split('/');
    return segments.Contains("obj", StringComparer.Ordinal) || segments.Contains("bin", StringComparer.Ordinal);
}

// ─── 判据一至四：旧号残留四类属性模式 ───
Report("props/csproj 的 <Version> 属性",
    JoinNonEmpty(
        GrepRecursiveEx(".", "*.props", new Regex($"<Version>{oldRe}</Version>")),
        GrepRecursiveEx(".", "*.csproj", new Regex($"<Version>{oldRe}</Version>"))));

// 2026-09-28（v6.0.1 发布实测）：PackageReference 残留扫描收窄到 PalORM.* 包——vendored
// BDN 子树（bench/BenchmarkDotNet）的第三方依赖版本与 PalORM 版本号巧合（AsmResolver 6.0.0）
// 构成发布阻断误报；自家版本残留的载体恒为 PalORM.* 前缀引用。
var pkgRefPattern = new Regex($"Include=\"PalORM\\.[A-Za-z]+\"[^>]*Version=\"{oldRe}\"");
Report("PackageReference Version= 属性（PalORM.* 包）", GrepRecursiveEx(".", "*.csproj", pkgRefPattern));

var readmeLines = File.Exists("README.md") ? File.ReadAllLines("README.md") : [];
var readmeHits = new List<string>();
var badgePattern = new Regex($"version-{oldRe}");
for (var i = 0; i < readmeLines.Length; i++)
{
    if (badgePattern.IsMatch(readmeLines[i]))
    {
        readmeHits.Add($"README.md:{i + 1}:{readmeLines[i]}");
    }
}
Report("README badge version-", string.Join('\n', readmeHits));

Report("src 双引号版本字面量（含 ToolVersion）",
    GrepRecursiveEx("src", "*.cs", new Regex($"\"{oldRe}\"")));

// ─── 2026-09-29（v6.1.0 发布实测）两道新判据，封堵 placeholder 残留形态 ───
// 判据 A：消费者 csproj 的 PalORM.* 包引用必须精确等于新版本号——旧号残留与占位符
//   字符串两种形态一网打尽（版本比较而非模式匹配，对任意占位写法免疫）。
// 判据 B：全部 PalORM.* PackageReference 行中凡含 placeholder/TODO/FIXME 字样的行
//   ——兜住"新版本号写好了但行内带占位标记"的中间态提交。
var consumerCsproj = "test/PalORM.PackageConsumer.Aot/PalORM.PackageConsumer.Aot.csproj";
var anyPkgRef = new Regex("Include=\"PalORM\\.[A-Za-z]+\"[^>]*Version=\"");
if (newVersion.Length > 0 && File.Exists(consumerCsproj))
{
    var newRe = Regex.Escape(newVersion);
    var consumerHits = new List<string>();
    var consumerLines = File.ReadAllLines(consumerCsproj);
    for (var i = 0; i < consumerLines.Length; i++)
    {
        if (anyPkgRef.IsMatch(consumerLines[i]) && !new Regex($"Version=\"{newRe}\"").IsMatch(consumerLines[i]))
        {
            consumerHits.Add($"{consumerLines[i]}");
        }
    }
    Report($"消费者 PackageReference 版本≠新版本 {newVersion}（判据 A）", string.Join('\n', consumerHits));
}

var placeholderHits = GrepRecursiveEx(".", "*.csproj", anyPkgRef)
    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Where(line => new Regex("placeholder|todo|fixme", RegexOptions.IgnoreCase).IsMatch(line));
Report("PalORM.* PackageReference 行含占位标记（判据 B）", string.Join('\n', placeholderHits));

if (newVersion.Length > 0)
{
    var propsText = File.Exists("Directory.Build.props") ? File.ReadAllText("Directory.Build.props") : "";
    if (!new Regex($"<Version>{Regex.Escape(newVersion)}</Version>").IsMatch(propsText))
    {
        Console.Error.WriteLine($"::error::正向核对失败：Directory.Build.props 未落新版本 {newVersion}");
        fail = 1;
    }
}

if (fail == 0)
{
    Console.WriteLine(newVersion.Length > 0
        ? $"PASS 版本残留扫描（旧 {oldVersion} → 新 {newVersion}）"
        : $"PASS 版本残留扫描（旧 {oldVersion}）");
}
return fail;

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
