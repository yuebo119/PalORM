// release-notes.cs（2026-11-05 新增，发布变更日志规范 §七 的机械化载体）
// 从 CHANGELOG 版本段派生「精简 Release Body」，本地预览与 CI 共用同一实现。
//
// 用法:
//   dotnet run --file scripts/release-notes.cs -- <version> [选项]
//     <version>              目标版本号（如 6.9.0），或 unreleased 预览未发布累积段
//   选项:
//     --write <path>         写入文件（默认输出到 stdout）
//     --check                形态校验：头条存在、每条带短标题、body 体量 ≤ 上限
//     --packages-dir <dir>   从实际 nupkg 推导包清单（默认 artifacts/packages，缺省回退内置 5 包）
//     --tag <name>           覆盖对比链接的 tag 名（默认 v<version>）
//     --max-chars <n>        body 字符上限（默认 1400，仅 --check 生效）
// 退出码: 0 = 成功/校验通过；1 = FAIL；2 = 用法错
//
// 派生规则（单一事实源：CHANGELOG 版本段，禁止手写第二份）:
//   头条一句话      = 版本标题下第一段（≤80 字）
//   ⚠️ 升级须知     = ### 💔 段，取「短标题：动作句」（截到第一句）
//   本版变更        = 其余段落的条目，只取短标题，跳过 〔内部〕 标记项
//   包清单          = 一行 NuGet 链接，替代逐包表格
//   页脚            = 完整变更日志 + compare 链接
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: release-notes.cs <version|unreleased> [--write <path>] [--check] [--packages-dir <dir>] [--tag <name>] [--max-chars <n>]");
    return 2;
}

var version = args[0];
var writePath = "";
var checkMode = false;
var packagesDir = "artifacts/packages";
var tagName = version == "unreleased" ? "" : $"v{version}";
var maxChars = 1400;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--write":
            writePath = args[++i];
            break;
        case "--check":
            checkMode = true;
            break;
        case "--packages-dir":
            packagesDir = args[++i];
            break;
        case "--tag":
            tagName = args[++i];
            break;
        case "--max-chars":
            maxChars = int.Parse(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"::error::未知参数 {args[i]}");
            return 2;
    }
}

var repoRoot = FindRepoRoot();
Environment.CurrentDirectory = repoRoot;

if (!File.Exists("CHANGELOG.md"))
{
    Console.Error.WriteLine("::error::CHANGELOG.md 不存在");
    return 1;
}

var lines = File.ReadAllLines("CHANGELOG.md").Select(l => l.TrimEnd('\r')).ToArray();
var headingRe = new Regex(@"^## \[(?<ver>[^\]]+)\]");

// ── 1. 定位目标段 ──
var headings = new List<(int Index, string Version)>();
for (var i = 0; i < lines.Length; i++)
{
    var m = headingRe.Match(lines[i]);
    if (m.Success)
    {
        headings.Add((i, m.Groups["ver"].Value));
    }
}

var targetIdx = headings.FindIndex(h => h.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
if (targetIdx < 0)
{
    Console.Error.WriteLine($"::error::CHANGELOG 中未找到版本段 [{version}]");
    return 1;
}

var target = headings[targetIdx];
var sectionEnd = targetIdx + 1 < headings.Count ? headings[targetIdx + 1].Index : lines.Length;
var section = lines[(target.Index + 1)..sectionEnd];

// 上一版本：CHANGELOG 中目标段之后的下一个版本段（含 Unreleased 预览形态）
var prevVersion = targetIdx + 1 < headings.Count ? headings[targetIdx + 1].Version : "";

var fail = 0;

// ── 2. 头条一句话 ──
var headline = ExtractHeadline(section);
if (headline.Length == 0)
{
    if (checkMode)
    {
        Console.Error.WriteLine("::error::版本段缺头条：标题下第一段必须是一句话概述（≤80 字，Release 页首行）");
        fail = 1;
    }
    headline = SynthesizeHeadline(section);
}
else if (headline.Length > 80 && checkMode)
{
    Console.Error.WriteLine($"::error::头条 {headline.Length} 字，超过 80 字上限：{headline}");
    fail = 1;
}

// ── 3. 条目分组与解析 ──
var breaking = new List<Bullet>();
var changes = new List<Bullet>();
var untitled = 0;
var currentGroup = "";

foreach (var line in section)
{
    if (line.StartsWith("### ", StringComparison.Ordinal))
    {
        currentGroup = line;
        continue;
    }
    if (!line.StartsWith("- ", StringComparison.Ordinal))
    {
        continue;
    }

    var b = ParseBullet(line);
    if (!b.HasTitle)
    {
        untitled++;
        if (checkMode)
        {
            Console.Error.WriteLine($"::error::条目缺短标题（须写成 \"- **标题**：细节\"）：{Truncate(line, 60)}");
            fail = 1;
        }
    }
    else if (b.Title.Length > 32)
    {
        Console.Error.WriteLine($"::warning::条目标题 {b.Title.Length} 字，超出 32 字建议值：{b.Title}");
    }

    if (currentGroup.StartsWith("### 💔", StringComparison.Ordinal))
    {
        breaking.Add(b);
    }
    else
    {
        changes.Add(b);
    }
}

if (changes.Count == 0 && breaking.Count == 0)
{
    Console.Error.WriteLine("::error::版本段无任何条目");
    return 1;
}

// ── 4. 组装 body ──
var packages = DiscoverPackages(packagesDir);
var sb = new StringBuilder();
sb.Append(headline).Append("\n\n");

var breakingOut = breaking.Where(b => !b.Internal).ToList();
if (breakingOut.Count > 0)
{
    sb.Append("## ⚠️ 升级须知\n\n");
    foreach (var b in breakingOut)
    {
        var detail = FirstSentence(b.Detail);
        sb.Append(detail.Length > 0 ? $"- **{b.Title}**：{detail}\n" : $"- **{b.Title}**\n");
    }
    sb.Append('\n');
}

var changesOut = changes.Where(b => !b.Internal).ToList();
if (changesOut.Count > 0)
{
    sb.Append("## 本版变更\n\n");
    foreach (var b in changesOut)
    {
        sb.Append($"- **{b.Title}**\n");
    }
    sb.Append('\n');
}

sb.Append("---\n\n");

// 包清单压成一行：一个 NuGet 链接 + 包名列表，替代逐包链接与表格（省约 220 字符）
var pkgNames = string.Join(" · ", packages.Select(p => p.Replace("PalORM.", "", StringComparison.Ordinal)));
var pkgAnchor = packages.Count > 0 ? packages[0] : "PalORM.Core";
sb.Append($"[NuGet 上的 {packages.Count} 个包](https://www.nuget.org/packages/{pkgAnchor})：{pkgNames}\n\n");

var changelogUrl = "https://github.com/yuebo119/PalORM/blob/main/CHANGELOG.md";
var footer = $"[完整变更日志]({changelogUrl})";
if (prevVersion.Length > 0 && tagName.Length > 0)
{
    footer += $" · [v{prevVersion}...{tagName} 对比](https://github.com/yuebo119/PalORM/compare/v{prevVersion}...{tagName})";
}
sb.Append(footer).Append('\n');

var body = sb.ToString();

// ── 5. 体量门禁 ──
if (checkMode)
{
    var len = body.Length;
    Console.WriteLine($"  头条: {headline.Length} 字");
    Console.WriteLine($"  升级须知: {breakingOut.Count} 条（源 {breaking.Count} 条，内部 {breaking.Count - breakingOut.Count} 条）");
    Console.WriteLine($"  本版变更: {changesOut.Count} 条（源 {changes.Count} 条，内部 {changes.Count - changesOut.Count} 条）");
    Console.WriteLine($"  body 体量: {len} 字符（上限 {maxChars}）");
    if (len > maxChars)
    {
        Console.Error.WriteLine($"::error::Release body {len} 字符超过 {maxChars} 上限，请压缩条目细节或拆分为更小版本");
        fail = 1;
    }
    if (untitled > 0)
    {
        Console.WriteLine($"  无标题条目: {untitled} 条（已在上面逐条报告）");
    }
}

if (fail != 0)
{
    Console.Error.WriteLine($"\nFAIL Release notes 校验未通过（{version}）");
    return 1;
}

if (writePath.Length > 0)
{
    File.WriteAllText(writePath, body, new UTF8Encoding(false));
    if (!checkMode)
    {
        Console.WriteLine($"已写入 {writePath}（{body.Length} 字符）");
    }
}
else
{
    Console.Write(body);
}

if (checkMode)
{
    Console.WriteLine($"\nPASS Release notes 校验通过（{version}，{body.Length} 字符）");
}
return 0;

// ── 辅助 ──

static string ExtractHeadline(string[] section)
{
    var buf = "";
    foreach (var raw in section)
    {
        var line = raw.Trim();
        if (line.Length == 0)
        {
            if (buf.Length > 0)
            {
                break;
            }
            continue;
        }
        if (line.StartsWith('#'))
        {
            break;
        }
        if (line.StartsWith("- ", StringComparison.Ordinal))
        {
            break;
        }
        // Keep a Changelog 允许版本标题下写一段概述，引用式（>）与普通段落都接受
        line = line.TrimStart('>').Trim();
        buf = buf.Length == 0 ? line : $"{buf}{line}";
    }
    return buf;
}

static string SynthesizeHeadline(string[] section)
{
    var count = section.Count(l => l.StartsWith("- ", StringComparison.Ordinal));
    return count == 0 ? "本版发布。" : $"本版发布，含 {count} 项变更。";
}

static Bullet ParseBullet(string line)
{
    var text = line[2..].Trim();
    var isInternal = text.Contains("〔内部〕");
    var m = Regex.Match(line, @"^- \*\*(?<title>[^*]{1,64})\*\*(?<rest>.*)$");
    if (m.Success)
    {
        var title = m.Groups["title"].Value.Trim();
        var detail = m.Groups["rest"].Value.TrimStart('：', ':', ' ');
        return new Bullet(title, detail.Replace("〔内部〕", "").Trim(), isInternal, HasTitle: true);
    }

    // 旧格式（无短标题）：从正文派生一个可读标题，渲染不塌成空行；--check 仍判 FAIL
    var plain = text.Replace("〔内部〕", "").Trim();
    var cut = plain.IndexOfAny(['：', '，', '。', '；']);
    var title2 = cut > 0 && cut <= 32 ? plain[..cut] : Truncate(plain, 32);
    return new Bullet(title2, plain, isInternal, HasTitle: false);
}

static string FirstSentence(string detail)
{
    if (detail.Length == 0)
    {
        return "";
    }
    var end = detail.IndexOf('。');
    var s = end >= 0 ? detail[..(end + 1)] : detail;
    // 长句仍可能撑大体量：截到 160 字符，保留完整标点语义
    return s.Length > 160 ? $"{s[..160]}…" : s;
}

static List<string> DiscoverPackages(string dir)
{
    if (Directory.Exists(dir))
    {
        // 产物目录可能残留其他版本的 nupkg（如预览未发布版本时），按版本后缀剥离而非硬替换
        var names = Directory.GetFiles(dir, "*.nupkg")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(n => Regex.Replace(n!, @"\.\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", ""))
            .Where(n => n.StartsWith("PalORM.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count > 0)
        {
            return names;
        }
    }
    return ["PalORM.Core", "PalORM.MySql", "PalORM.PostgreSql", "PalORM.SourceGen", "PalORM.Sqlite"];
}

static string Truncate(string s, int max) => s.Length <= max ? s : $"{s[..max]}…";

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

internal sealed record Bullet(string Title, string Detail, bool Internal, bool HasTitle);
