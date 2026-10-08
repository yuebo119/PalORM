// doc-consistency-check.cs（自 .ai/scripts/doc-consistency-check.sh 迁移，脚本全面 C# 化轮）
// PalORM 文档一致性机械校验 D1-D12。所有 checks 独立运行，单一事实来源原则。
// --fix：D10 三处计数自动同步（ITM-735 的 awk 精确重写语义保真——只替换含 [Test]+声明+计数的行）。
// 契约：0=全过；1=有 FAIL；--fix 模式 0=同步完成；stdout 与 .sh 版对拍兼容（含 ANSI 色码）。
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();
var passed = 0; var failed = 0;
const string RED = "\x1b[0;31m", GREEN = "\x1b[0;32m", NC = "\x1b[0m";

void Fail(string msg) { Console.WriteLine($"{RED}FAIL {msg}{NC}"); failed++; }
void Pass(string msg) { Console.WriteLine($"{GREEN}PASS {msg}{NC}"); passed++; }

// ── D1: README tests badge（若存在）与架构总计一致 ──
var readmeBadge = FirstMatch(File.Exists("README.md") ? File.ReadAllText("README.md") : "", @"tests-(\d+)%2F(\d+)") is { } b1 ? $"{b1.Group1}/{b1.Group2}" : "";
var archText = File.Exists("docs/架构设计.md") ? File.ReadAllText("docs/架构设计.md") : "";
var archTotal = (LastMatch(archText, @"\*\*(\d+/\d+)\*\*")?.Group1 ?? "").Replace("*", "");
if (readmeBadge.Length == 0) Pass("D1 README 无 tests badge（v5.0 精简）——无对照需求");
else if (readmeBadge == archTotal) Pass($"D1 README badge({readmeBadge}) = 架构总计({archTotal})");
else Fail($"D1 README badge({readmeBadge}) ≠ 架构总计({archTotal})");

// ── D2: 架构表格 Core 计数 = API参考中 Core 计数 ──
var coreArch = FirstMatch(archText, @"Core\.Tests\s*\|\s*(\d+/\d+)")?.Group1 ?? "";
var apiText = File.Exists("docs/API参考.md") ? File.ReadAllText("docs/API参考.md") : "";
var coreApi = FirstMatch(apiText, @"Core (\d+/\d+)")?.Group1 ?? "";
if (coreArch.Length == 0 && coreApi.Length == 0) Pass("D2 架构/API 参考均无分项 X/X 计数（v5.x 文档重构后口径）——跳过");
else if (coreArch.Length > 0 && coreApi.Length > 0 && coreArch == coreApi) Pass($"D2 Core 计数一致: {coreArch}");
else Fail($"D2 Core 计数: 架构={coreArch}, API={coreApi}（单侧缺失或值漂移）");

// ── D3: G9 文档状态与整改事实一致 ──
var stdText = File.Exists("docs/编码规范.md") ? File.ReadAllText("docs/编码规范.md") : "";
if (Regex.IsMatch(stdText, @"G9.*仓库侧已整改.*AUD-001", RegexOptions.Singleline)) Pass("D3 G9 标注为仓库侧已整改/AUD-001 在案");
else Fail("D3 G9 状态不符合预期（应标注仓库侧已整改并引用 AUD-001）");

// ── D4: 所有文档不含已过期的当前计数陈述 ──
var staleRegex = new Regex(@"Core (71/71|89/89|92/92|94/94|96/96|104/104)|tests-(241|258|259|262|264|266|274|276)%2F|(241|258|259|262|264|266|274|276)/", RegexOptions.Compiled);
var staleFound = false;
foreach (var (doc, di) in new[] { ("docs", 0), ("README.md", 1) })
{
    var files = di == 0 && Directory.Exists("docs")
        ? Directory.EnumerateFiles("docs", "*.md", SearchOption.AllDirectories)
        : (File.Exists("README.md") ? ["README.md"] : Enumerable.Empty<string>());
    foreach (var f in files)
    {
        var i = 0;
        foreach (var line in File.ReadLines(f))
        {
            i++;
            if (!staleRegex.IsMatch(line)) continue;
            if (Regex.IsMatch(line, @"基线|S1|S2|历史|阶段|迁移")) continue;
            if (line.StartsWith('|')) continue;
            if (Regex.IsMatch(line, @"\.cs:[0-9]")) continue;
            staleFound = true;
            Console.WriteLine($"  {f.Replace('\\', '/')}:{i}: {line.Trim()}");
        }
    }
}
if (!staleFound) Pass("D4 无过期当前计数陈述");
else Fail("D4 存在过期当前计数（见上）");

// ── D5: 架构设计 Core 页眉 = 表格 ──
var coreHeader = FirstMatch(archText, @"Core (\d+/\d+)")?.Group1 ?? "";
if (coreHeader.Length == 0 && coreArch.Length == 0) Pass("D5 架构页无 Core X/X 计数（v5.x 文档重构后口径）——跳过");
else if (coreHeader == coreArch) Pass($"D5 架构页眉({coreHeader}) = 表格({coreArch})");
else Fail($"D5 架构页眉({coreHeader}) ≠ 表格({coreArch})");

// ── D6: SourceGen 计数一致 ──
var sgHeader = FirstMatch(archText, @"SourceGen (\d+/\d+)")?.Group1 ?? "";
var sgTable = FirstMatch(archText, @"SourceGen\.Tests\s*\|\s*(\d+/\d+)")?.Group1 ?? "";
if (sgHeader.Length == 0 && sgTable.Length == 0) Pass("D6 架构页无 SourceGen X/X 计数（v5.x 文档重构后口径）——跳过");
else if (sgHeader == sgTable && sgHeader.Length > 0) Pass($"D6 SourceGen 计数一致: {sgHeader}");
else Fail($"D6 SourceGen: 页眉={sgHeader}, 表格={sgTable}");

// ── D7: Integration 本地通过数一致 ──
var intHeader = FirstMatch(archText, @"集成 (\d+)/\d+")?.Group1 ?? "";
var intTable = FirstMatch(archText, @"Integration\.Tests\s*\|\s*(\d+)/\d+")?.Group1 ?? "";
if (intHeader.Length == 0 && intTable.Length == 0) Pass("D7 架构页无集成 X/X 计数（v5.x 文档重构后口径）——跳过");
else if (intHeader == intTable && intHeader.Length > 0) Pass($"D7 Integration 本地通过数一致: {intHeader}");
else Fail($"D7 Integration: 表头={intHeader}, 表格={intTable}");

// ── D9: 分项加和 = 总计 ──
var coreN = FirstMatch(archText, @"Core\.Tests\s*\|\s*(\d+)")?.Group1;
var sgN = FirstMatch(archText, @"SourceGen\.Tests\s*\|\s*(\d+)")?.Group1;
var intN = FirstMatch(archText, @"Integration\.Tests\s*\|\s*(\d+)")?.Group1;
var totalN = FirstMatch(archText, @"\*\*总计\*\*\s*\|\s*\*\*(\d+)")?.Group1;
if (string.Concat(coreN, sgN, intN, totalN).Length == 0) Pass("D9 分项/总计行不存在（v5.x 文档重构后口径）——跳过");
else if (coreN is { Length: > 0 } c && sgN is { Length: > 0 } s && intN is { Length: > 0 } it && totalN is { Length: > 0 } tt && int.Parse(c) + int.Parse(s) + int.Parse(it) == int.Parse(tt))
    Pass($"D9 分项加和 = 总计: {c}+{s}+{it}={tt}");
else Fail($"D9 分项计数不一致（core={coreN} sg={sgN} int={intN} total={totalN}）");

// ── D8: 编码规范 G12 标记为通过 ──
if (Regex.IsMatch(stdText, @"G12.*禁止公开 static.*✅", RegexOptions.Singleline)) Pass("D8 G12 标注为通过");
else Fail("D8 G12 状态不符合预期（应包含 ✅）");

// ── D10: 架构总声明 = 实测 [Test] 标记数（含 --fix 自动同步）──
if (args.Length > 0 && args[0] == "--fix")
{
    var fixCount = CountTestMarkers();
    foreach (var doc in new[] { "docs/架构设计.md", "docs/API参考.md" })
    {
        if (!File.Exists(doc)) continue;
        var lines = File.ReadAllLines(doc);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("[Test]") && lines[i].Contains("声明") && Regex.IsMatch(lines[i], @"[0-9]+ 项"))
            {
                lines[i] = Regex.Replace(lines[i], @"[0-9]+ 项", $"{fixCount} 项");
            }
        }
        File.WriteAllLines(doc, lines, new UTF8Encoding(false));
    }
    Console.WriteLine($"D10 --fix: 三处计数已同步为 {fixCount}");
    return 0;
}
var declTotal = FirstMatch(archText, @"\*\*(\d+) 项 `\[Test\]` 声明\*\*")?.Group1 ?? "";
var markTotal = CountTestMarkers().ToString();
if (declTotal.Length == 0) Fail("D10 架构设计.md 未找到 '**N 项 `[Test]` 声明**' 总声明（格式变更？）");
else if (declTotal == markTotal) Pass($"D10 架构总声明 = 实测标记数（{declTotal}）");
else Fail($"D10 架构声明 {declTotal} ≠ 实测标记数 {markTotal}（同步 docs/架构设计.md 273 行附近的总声明）");

// ── D11: 生成物工具版本 = 包版本真源（含 OTel InstrumentationVersion）───
var propsText = File.Exists("Directory.Build.props") ? File.ReadAllText("Directory.Build.props") : "";
var pkgVersion = FirstMatch(propsText, @"<Version>([^<]+)")?.Group1?.Trim() ?? "";
var metaText = File.Exists("src/PalORM.SourceGen/GeneratedCodeMetadata.cs") ? File.ReadAllText("src/PalORM.SourceGen/GeneratedCodeMetadata.cs") : "";
var toolVersion = FirstMatch(metaText, "ToolVersion = \"([^\"]+)")?.Group1 ?? "";
var metricsText = File.Exists("src/PalORM.Core/PalORMMetrics.cs") ? File.ReadAllText("src/PalORM.Core/PalORMMetrics.cs") : "";
var otelVersion = FirstMatch(metricsText, "InstrumentationVersion = \"([^\"]+)")?.Group1 ?? "";
if (pkgVersion.Length == 0 || toolVersion.Length == 0 || otelVersion.Length == 0)
    Fail("D11 未找到包版本（Directory.Build.props <Version>）、生成物工具版本（GeneratedCodeMetadata.ToolVersion）或 OTel 版本（PalORMMetrics.InstrumentationVersion）");
else if (pkgVersion == toolVersion && pkgVersion == otelVersion) Pass($"D11 生成物工具版本与 OTel 版本 = 包版本（{pkgVersion}）");
else Fail($"D11 版本不一致：工具 {toolVersion} · OTel {otelVersion} · 包 {pkgVersion}（同步 GeneratedCodeMetadata.cs 与 PalORMMetrics.cs）");

// ── D12: 文档表格声明的调用式 API 必须存在于源码 ──
List<string> srcFiles = [];
if (Directory.Exists("src"))
{
    srcFiles = [.. Directory.EnumerateFiles("src", "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Replace('\\', '/').Contains("obj/") && !f.Replace('\\', '/').Contains("bin/"))];
}
var srcContent = new Lazy<string>(() => string.Join("\n", srcFiles.Select(File.ReadAllText)));
var declaredApis = new SortedSet<string>(StringComparer.Ordinal);
foreach (var line in File.ReadLines("docs/API参考.md"))
{
    if (!line.StartsWith('|')) continue;
    foreach (Match m in Regex.Matches(line, @"`\.(?<n>[A-Za-z_][A-Za-z0-9_]*)\("))
    {
        declaredApis.Add(m.Groups["n"].Value);
    }
}
var apiMissing = new List<string>();
foreach (var name in declaredApis)
{
    var declRegex = new Regex($@"\b(public|internal|protected)\b[^;{{}}\n]*\b{name}\s*[<(]", RegexOptions.Compiled);
    if (!srcFiles.Any(f => declRegex.IsMatch(File.ReadAllText(f)))) apiMissing.Add(name);
}
if (apiMissing.Count == 0) Pass($"D12 文档表格声明的 {declaredApis.Count} 个调用式 API 均在源码中存在");
else Fail($"D12 文档声明但源码不存在: {string.Join(' ', apiMissing)}（补实现或从 docs/API参考.md 删除该行）");

Console.WriteLine();
Console.WriteLine($"通过: {passed}  失败: {failed}");
return failed > 0 ? 1 : 0;

// ── 辅助 ──

static (string Group1, string Group2)? FirstMatch(string content, string pattern)
{
    var m = Regex.Match(content, pattern);
    return m.Success ? (m.Groups[1].Value, m.Groups.Count > 2 ? m.Groups[2].Value : "") : null;
}

static (string Group1, string Group2)? LastMatch(string content, string pattern)
{
    var matches = Regex.Matches(content, pattern);
    return matches.Count > 0 ? (matches[^1].Groups[1].Value, "") : null;
}

static int CountTestMarkers()
{
    var total = 0;
    foreach (var dir in new[] { "test/PalORM.Core.Tests", "test/PalORM.SourceGen.Tests", "test/PalORM.Integration.Tests" })
    {
        if (!Directory.Exists(dir)) continue;
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = f.Replace('\\', '/');
            if (rel.Contains("obj/") || rel.Contains("bin/")) continue;
            total += File.ReadLines(f).Count(l => l.Contains("[Test]"));
        }
    }
    return total;
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
