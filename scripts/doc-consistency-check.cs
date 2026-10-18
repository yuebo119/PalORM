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

// ── D 组重构（2026-10-18 质量审计）──
// 原 D1/D2/D5/D6/D7/D9 六项在 v5.x 文档重构后已无对照对象：每次运行都走"跳过并 PASS"分支，
// 却计入通过数——六项死检查长期冒充有效防线。原 D3/D8 只检查文档里有指定字样（"已整改"/
// "✅"），与代码真实行为无关联，由 D13 的机器对账取代。有效编号现为 D4/D10/D11/D12/D13。

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

// ── D10: [Test] 计数声明（三文件全部写入点）= 实测标记数（含 --fix 自动同步）──
// 写入点台账（2026-10-16 v7.37 轮补全）：docs/架构设计.md ×2（首行总验证 + 状态表行）、
// docs/API参考.md ×1（首行测试口径）、docs/测试体系规范.md ×1（§验证指针行）。原校验只查
// 架构设计.md 的加粗行一处：API参考.md 的声明曾被 V12 探针 git checkout 连带打回（lessons
// B190）、测试体系规范.md 的声明是 533 化石值（少实测 566），两个写入点均无校验面（B103 族）。
var d10Docs = new[] { "docs/架构设计.md", "docs/API参考.md", "docs/测试体系规范.md" };
if (args.Length > 0 && args[0] == "--fix")
{
    var fixCount = CountTestMarkers();
    var fixPlaces = 0;
    foreach (var doc in d10Docs)
    {
        if (!File.Exists(doc)) continue;
        var lines = File.ReadAllLines(doc);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("[Test]") && lines[i].Contains("声明") && Regex.IsMatch(lines[i], @"[0-9]+ 项"))
            {
                lines[i] = Regex.Replace(lines[i], @"[0-9]+ 项", $"{fixCount} 项");
                fixPlaces++;
            }
        }
        File.WriteAllLines(doc, lines, new UTF8Encoding(false));
    }
    Console.WriteLine($"D10 --fix: {fixPlaces} 处计数已同步为 {fixCount}");
    return 0;
}
var markTotal = CountTestMarkers().ToString();
var d10Bad = new List<string>();
foreach (var doc in d10Docs)
{
    if (!File.Exists(doc)) { d10Bad.Add($"{doc} 缺失"); continue; }
    foreach (Match m in Regex.Matches(File.ReadAllText(doc), @"(\d+) 项 `\[Test\]` 声明"))
        if (m.Groups[1].Value != markTotal) d10Bad.Add($"{doc} 声明 {m.Groups[1].Value}≠实测 {markTotal}");
}
if (d10Bad.Count == 0) Pass($"D10 [Test] 计数声明一致（三文件全部写入点 = 实测 {markTotal}）");
else Fail($"D10 [Test] 计数声明漂移（{string.Join("；", d10Bad)}）——用 --fix 同步");

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

// ── D13: 门禁级别三方对账（文档表 ⊗ 脚本实际判定方式）──
// 2026-10-18 审计新增。实例：G10/G15/G16 在 .ai/gate/prompt.md 与 docs/编码规范.md 都标
// 「警告级」，gate-check.cs 却用 CheckZero 按阻断执行——文档说警告、代码真拦截，两边都没人核。
// 判据来源：gate-check.cs 的调用方式（CheckZero=阻断 / CheckWarn=警告），与两处文档表的级别列
// 逐项对账；文档或代码任一侧改动而未同步另一侧即 FAIL。
var gateSrc = File.Exists("scripts/gate-check.cs") ? File.ReadAllText("scripts/gate-check.cs") : "";
var scriptLevels = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (Match m in Regex.Matches(gateSrc, @"Check(Zero|Warn)\(""G(\d+)"""))
{
    scriptLevels[$"G{m.Groups[2].Value}"] = m.Groups[1].Value == "Zero" ? "阻断" : "警告";
}
var docLevelBad = new List<string>();
var docLevelChecked = 0;
foreach (var doc in new[] { ".ai/gate/prompt.md", "docs/编码规范.md" })
{
    if (!File.Exists(doc)) continue;
    foreach (var line in File.ReadLines(doc))
    {
        if (!line.StartsWith('|')) continue;
        var m = Regex.Match(line, @"^\|\s*(G\d+)\s*\|([^|]*)\|([^|]*)\|([^|]*)\|");
        if (!m.Success) continue;
        var id = m.Groups[1].Value;
        if (!scriptLevels.TryGetValue(id, out var actual)) continue;
        var cell = m.Groups[4].Value;
        // 文档侧级别表达：编码规范用 ✅ / ⚠️ 警告级；gate 提示词用 阻断 / 警告
        var declared = cell.Contains("警告", StringComparison.Ordinal) ? "警告"
            : cell.Contains("阻断", StringComparison.Ordinal) ? "阻断"
            : cell.Contains('✅') ? "阻断" : "";
        if (declared.Length == 0) continue;
        docLevelChecked++;
        if (declared != actual) docLevelBad.Add($"{doc} {id} 文档={declared} 脚本={actual}");
    }
}
if (docLevelBad.Count == 0)
    Pass($"D13 门禁级别三方对账一致（{docLevelChecked} 处 G 项声明与 gate-check 判定方式对齐）");
else
    Fail($"D13 门禁级别文档与脚本不一致（{string.Join("；", docLevelBad)}）——改文档级别或改脚本 CheckZero/CheckWarn");

Console.WriteLine();
Console.WriteLine($"通过: {passed}  失败: {failed}");
return failed > 0 ? 1 : 0;

// ── 辅助 ──

static (string Group1, string Group2)? FirstMatch(string content, string pattern)
{
    var m = Regex.Match(content, pattern);
    return m.Success ? (m.Groups[1].Value, m.Groups.Count > 2 ? m.Groups[2].Value : "") : null;
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
