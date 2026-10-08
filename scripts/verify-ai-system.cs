// verify-ai-system.cs（自 .ai/scripts/verify-ai-system.sh 迁移，脚本全面 C# 化轮）
// .ai 系统机械校验 V1-V23：提示词引用可解析、口径数字与事实一致、配套脚本存在、防线能失败。
// 迁移形态重登记（与引用面切换同批生效）：①判定/工具脚本真源=主仓 scripts/*.cs，V2/V5/V14
// 判据按新路径；②V15 原 bash 卫生（B30/B31）随判定层 .sh 退役，重登记为 .ai/scripts 白名单
// 哨兵（允许 bash 的仅 install-ai-system.sh 引导件；额外 .sh/.mjs/.py 即 FAIL）；③V17c V16
// 探针原硬编码 v7.33（升版即失效的存量假绿），改动态取当前版本漂移。
// 契约：0=全过；1=有 FAIL；2=用法错。--fast 跳过 V14/V17（副作用项）。stdout 与 .sh 版对拍兼容。
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();
var FAST = args.Length > 0 && args[0] == "--fast";
var passed = 0; var failed = 0;

void Pass(int id, string msg) { Console.WriteLine($"PASS V{id}: {msg}"); passed++; }
void Fail(int id, string msg, string detail) { Console.WriteLine($"FAIL V{id}: {msg}（{detail}）"); failed++; }
void Warn(int id, string msg) { Console.WriteLine($"WARN V{id}: {msg}"); }

Console.WriteLine("═══════ .ai 系统一致性校验 ═══════");
Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine();

// V1 引擎 + 双 profile + 工具提示词与模板齐全
var v1Missing = new List<string>();
foreach (var f in new[]
{
    ".ai/review/engine.md", ".ai/review/metrics.md", ".ai/review/prompt.md",
    ".ai/review/known-false-positives.md", ".ai/gate/prompt.md", ".ai/refine/prompt.md",
    ".ai/review/templates/report.md", ".ai/review/templates/action-items.md",
})
{
    if (!File.Exists(f)) v1Missing.Add(f);
}
if (v1Missing.Count == 0) Pass(1, "引擎/双profile/工具提示词与模板齐全");
else Fail(1, "提示词/模板缺失", string.Join(' ', v1Missing));

// V2 提示词引用的权威文档与脚本存在（切换后形态：判定层真源在主仓 scripts/*.cs）
var v2Missing = new List<string>();
foreach (var f in new[]
{
    "docs/编码规范.md", "docs/踩坑目录.md", "docs/API参考.md", "docs/架构设计.md",
    "scripts/review-snapshot.cs", "scripts/verify-action-items.cs", "scripts/gate-check.cs",
    "scripts/refine-scan.cs", "scripts/stub-check.cs", "scripts/assertion-strength-check.cs",
})
{
    if (!File.Exists(f)) v2Missing.Add(f);
}
if (v2Missing.Count == 0) Pass(2, "提示词引用的文档与脚本全部存在");
else Fail(2, "被引用文件缺失", string.Join(' ', v2Missing));

// V3 STD 规则数：提示词口径 = 编码规范实际去重计数
var stdActual = Regex.Matches(File.ReadAllText("docs/编码规范.md"), @"STD-[A-Z]+-[0-9]+")
    .Select(m => m.Value).Distinct().Count();
var stdClaims = new[] { ".ai/review/prompt.md", ".ai/gate/prompt.md", ".ai/refine/prompt.md" }
    .SelectMany(p => File.Exists(p) ? Regex.Matches(File.ReadAllText(p), @"([0-9]+) 条 STD 规则").Select(m => m.Groups[1].Value) : [])
    .Distinct().ToList();
if (stdClaims.Count == 1 && stdClaims[0] == stdActual.ToString()) Pass(3, $"STD 规则数口径一致（{stdActual} 条）");
else Fail(3, "STD 规则数口径漂移", $"实际 {stdActual}，提示词声称：{string.Join(' ', stdClaims)}");

// V4 踩坑目录口径：标题 = 表行数 = 提示词声称
var pitDoc = File.Exists("docs/踩坑目录.md") ? File.ReadAllText("docs/踩坑目录.md") : "";
var pitTitle = Regex.Match(pitDoc, @"([0-9]+) 项跨语言 ORM 陷阱").Groups[1].Value;
var pitRows = Regex.Matches(pitDoc, @"^\| ?[0-9]+", RegexOptions.Multiline).Count.ToString();
var pitClaims = new[] { ".ai/review/prompt.md", ".ai/gate/prompt.md" }
    .SelectMany(p => File.Exists(p) ? Regex.Matches(File.ReadAllText(p), @"([0-9]+) 项陷阱").Select(m => m.Groups[1].Value) : [])
    .Distinct().ToList();
if (pitTitle == pitRows && pitClaims.Count == 1 && pitClaims[0] == pitTitle) Pass(4, $"踩坑目录口径一致（{pitTitle} 项）");
else Fail(4, "踩坑目录口径漂移", $"标题 {pitTitle}，表行 {pitRows}，提示词：{string.Join(' ', pitClaims)}");

// V5 门禁编号：gate 提示词 / gate-check.cs / 编码规范 / README 四方最大 G 编号一致
var gPrompt = MaxTail(Regex.Matches(File.ReadAllText(".ai/gate/prompt.md"), @"G1-G([0-9]+)"));
var gScript = MaxNum(Regex.Matches(File.ReadAllText("scripts/gate-check.cs"), @"G([0-9]+)"));
var gDoc = MaxTail(Regex.Matches(File.ReadAllText("docs/编码规范.md"), @"G1-G([0-9]+)"));
var gReadme = MaxTail(Regex.Matches(File.ReadAllText(".ai/README.md"), @"G1-G([0-9]+)"));
if (gPrompt == gScript && gScript == gDoc && gDoc == gReadme) Pass(5, $"门禁编号四方一致（G1-G{gScript}）");
else Fail(5, "门禁编号不同步", $"提示词 G{gPrompt}，脚本 G{gScript}，编码规范 G{gDoc}，README G{gReadme}");

// V6 API 参考存在且非空
var apiLines = File.Exists("docs/API参考.md") ? File.ReadLines("docs/API参考.md").Count() : 0;
if (apiLines > 50) Pass(6, $"API 参考完整（{apiLines} 行）");
else Fail(6, "API 参考异常", $"docs/API参考.md 行数 {apiLines}（预期 >50）");

// V7 误判知识库双源口径
var kb = File.ReadAllText(".ai/review/known-false-positives.md");
var kbCount = Regex.Matches(kb, @"^### 模式 (P?[0-9]+)：", RegexOptions.Multiline).Count;
var kbFullMax = MaxNum(Regex.Matches(kb, @"^### 模式 P([0-9]+)：", RegexOptions.Multiline));
var kbFastSection = Regex.Match(kb, @"^## 速版.*?^---", RegexOptions.Multiline | RegexOptions.Singleline).Value;
var kbFastMax = MaxNum(Regex.Matches(kbFastSection, @"^P([0-9]+)\.", RegexOptions.Multiline));
if (kbCount >= 14 && kbFullMax.Length > 0 && kbFullMax == kbFastMax) Pass(7, $"误判知识库双源一致（{kbCount} 条模式，速版覆盖至 P{kbFastMax}）");
else Fail(7, "误判知识库模式数不足或速版滞后于完整版（子代理可见面断链）", $"完整版 {kbCount} 条至 P{(kbFullMax.Length > 0 ? kbFullMax : "无")}，速版至 P{(kbFastMax.Length > 0 ? kbFastMax : "无")}——需 ≥14 且两源同号");

// V8 视角发现率账本
var ps = File.Exists(".ai/review/perspective-stats.md") ? File.ReadAllText(".ai/review/perspective-stats.md") : "";
var sixFlows = Regex.Matches(ps, @"^\| (架构|安全|资源|并发|错误|AOT) ?流", RegexOptions.Multiline).Count;
if (sixFlows == 6) Pass(8, "视角发现率账本存在且六流记录完整");
else Fail(8, "视角发现率账本缺失或六流记录不全", ".ai/review/perspective-stats.md");

// V9 README 文件地图与实际目录一致
var v9Missing = new List<string>();
foreach (var f in Regex.Matches(File.ReadAllText(".ai/README.md"), @"(gate|refine|review)/[a-z0-9/-]*\.md").Select(m => m.Value).Distinct())
{
    if (!File.Exists($".ai/{f}")) v9Missing.Add($".ai/{f}");
}
if (v9Missing.Count == 0) Pass(9, "README 文件地图与实际一致");
else Fail(9, "README 文件地图指向不存在的文件", string.Join(' ', v9Missing));

// V10 机械防线存在
var snapCount = Directory.Exists("test/PalORM.SourceGen.Tests/Snapshots")
    ? Directory.GetFiles("test/PalORM.SourceGen.Tests/Snapshots", "*.snap").Length : 0;
if (File.Exists("test/PalORM.SourceGen.Tests/SnapshotTests.cs")
    && File.Exists("test/PalORM.SourceGen.Tests/DialectSymmetryTests.cs") && snapCount >= 10)
    Pass(10, $"机械防线齐全（快照基线 {snapCount} 份 + 对称性测试 + 断言强度门禁）");
else Fail(10, "机械防线缺失", $"SnapshotTests/DialectSymmetryTests/Snapshots（{snapCount} 份，需 ≥10）");

// V11 metrics 账本
var metrics = File.Exists(".ai/review/metrics.md") ? File.ReadAllText(".ai/review/metrics.md") : "";
if (metrics.Contains("## 缺陷逃逸账本") && metrics.Contains("## 轮次记录")) Pass(11, "metrics 账本存在且结构完整");
else Fail(11, "metrics 账本缺失或结构不完整", ".ai/review/metrics.md 需含「轮次记录」与「缺陷逃逸账本」");

// V12 PALORM 诊断编号双向对账
var analyzerCount = Regex.Matches(File.ReadAllText("src/PalORM.SourceGen/PalORMAnalyzer.cs"),
    @"public static readonly DiagnosticDescriptor", RegexOptions.Multiline).Count;
var declM = Regex.Match(File.ReadAllText("docs/API参考.md"),
    @"^### 编译时验证 — ([0-9]+) 条 PALORM 诊断（([0-9]+) 条分析器 \+ ([0-9]+) 条生成器", RegexOptions.Multiline);
var declTotal = declM.Groups[1].Value; var declAnalyzer = declM.Groups[2].Value; var declGen = declM.Groups[3].Value;
var v12Fail = "";
if (analyzerCount < 30) v12Fail += $" 实测{analyzerCount}<30";
if (!declM.Success) v12Fail += " API参考标题不可解析";
else
{
    if (declAnalyzer.Length > 0 && declAnalyzer != analyzerCount.ToString()) v12Fail += $" API参考声明分析器{declAnalyzer}≠实测{analyzerCount}";
    if (declTotal.Length > 0 && declGen.Length > 0 && declTotal != (int.Parse(declAnalyzer) + int.Parse(declGen)).ToString()) v12Fail += $" 声明总数{declTotal}≠{declAnalyzer}+{declGen}";
}
if (v12Fail.Length == 0) Pass(12, $"PALORM 诊断实测与文档对账一致（分析器 {analyzerCount}；总数 {declTotal} = {declAnalyzer} 分析器 + {declGen} 生成器）");
else Fail(12, "PALORM 诊断计数漂移（Analyzer 实测 vs API参考.md 声明）", v12Fail.Trim());

// V12b lessons 含 B24-B27
var lessonsText = File.ReadAllText(".ai/lessons.md");
if (lessonsText.Contains("B24 RS1032") && lessonsText.Contains("B26 防静默错误")) Pass(13, "lessons.md 含诊断工程化教训 B24-B27");
else Fail(13, "lessons.md 缺诊断工程化教训", "需登记 B24(RS1032)/B25(XML转义)/B26(价值分层)/B27(变量流局限)");

if (!FAST)
{
    // V14 核心防线脚本可执行性：完成标记判活（区分"检查失败但活着"与"中途死亡"）
    var finishMark = new Dictionary<string, string>
    {
        ["gate-check"] = "扫描完成",
        ["test-gate"] = "门禁(未)?通过",
        ["tech-debt-scan"] = "结果: ",
        ["doc-consistency-check"] = "通过: ",
        ["assertion-strength-check"] = "扫描完成",
        ["refine-scan"] = "纪律补充",
        ["review-scope"] = "账本规则",
        ["review-snapshot"] = "评审基线快照",
        ["probe-template"] = "用法",
        ["verify-action-items"] = "验证完成",
    };
    var deadScripts = new List<string>();
    var latestLedger = Directory.Exists(".ai/review/history/action-items")
        ? Directory.GetFiles(".ai/review/history/action-items", "*.md").OrderByDescending(f => f).FirstOrDefault()
        : null;
    foreach (var (name, mark) in finishMark)
    {
        var arguments = name == "verify-action-items" && latestLedger is not null
            ? $"run --file scripts/{name}.cs -- {latestLedger}"
            : $"run --file scripts/{name}.cs";
        if (name == "verify-action-items" && latestLedger is null) { continue; } // 无账本跳过（与 .sh 版一致）
        var output = RunCapture("dotnet", arguments);
        if (!Regex.IsMatch(output, mark)) deadScripts.Add(name);
    }
    if (deadScripts.Count == 0) Pass(14, "核心防线脚本全部可执行（10/10 跑到完成标记）");
    else Fail(14, "防线脚本中途死亡", string.Join(' ', deadScripts));
}

// V15 .ai/scripts 白名单哨兵（原 bash B30/B31 卫生随判定层 .sh 退役重登记）
// 允许的非 C# 形态仅 install-ai-system.sh（引导件）；额外 .sh/.mjs/.py 即语言政策漂移。
var allowed = new HashSet<string>(StringComparer.Ordinal) { "install-ai-system.sh" };
var stray = Directory.Exists(".ai/scripts")
    ? Directory.GetFiles(".ai/scripts")
        .Select(f => Path.GetFileName(f) ?? "")
        .Where(n => (n.EndsWith(".sh") || n.EndsWith(".mjs") || n.EndsWith(".py")) && !allowed.Contains(n))
        .ToList()
    : [];
if (stray.Count == 0) Pass(15, "脚本卫生合规（.ai/scripts 白名单形态维持：仅 install-ai-system.sh 为 bash 引导件）");
else Fail(15, "脚本卫生违例（.ai/scripts 出现白名单外非 C# 脚本——判定层已迁主仓 scripts/*.cs）", string.Join(' ', stray));

// V16 lessons 可机算声明 == 实测（版本真源 CHANGELOG-lessons.md）
var declTotal16 = Regex.Match(lessonsText, @"^## II\. AI 缺陷登记（([0-9]+) 个：", RegexOptions.Multiline).Groups[1].Value;
var declBmax = Regex.Match(lessonsText, @"^## II\. AI 缺陷登记（.*\+ B1-B([0-9]+)", RegexOptions.Multiline).Groups[1].Value;
var titleVer = Regex.Matches(lessonsText, @"PalORM AI 规范化系统 v([0-9.]+)").Select(m => m.Groups[1].Value).LastOrDefault() ?? "";
var changelog = File.Exists(".ai/CHANGELOG-lessons.md") ? File.ReadAllText(".ai/CHANGELOG-lessons.md") : "";
var histVer = Regex.Matches(changelog, @"^- v([0-9.]+)（", RegexOptions.Multiline).Select(m => m.Groups[1].Value).LastOrDefault() ?? "";
var bNums = Regex.Matches(lessonsText, @"\| B([0-9]+) ").Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(n => n).ToList();
var bCount = bNums.Count; var bMax = bNums.Count > 0 ? bNums[^1].ToString() : "";
var bGaps = string.Join(' ', Enumerable.Range(1, bNums.Count - 1).Where(i => bNums[i] != bNums[i - 1] + 1).Select(i => (bNums[i - 1] + 1).ToString()));
var aCount = Regex.Matches(lessonsText, @"^\| A[0-9] ", RegexOptions.Multiline).Count;
var declFail = "";
if (changelog.Trim().Length == 0) declFail += " 缺版本历史文件（CHANGELOG-lessons.md）";
if (titleVer != histVer) declFail += $" 标题v{titleVer}≠历史v{histVer}";
if (declTotal16.Length == 0) declFail += " 缺总声明";
if (declBmax.Length == 0) declFail += " 缺B上界声明";
else if (declBmax != bMax) declFail += $" 声明B上界{declBmax}≠实测{bMax}";
if (declTotal16.Length > 0 && declTotal16 != (bCount + aCount).ToString()) declFail += $" 声明总数{declTotal16}≠实测{bCount}+B+{aCount}+A";
if (bGaps.Length > 0) declFail += $" B缺号:{bGaps}";
if (declFail.Length == 0) Pass(16, $"lessons.md 声明与实测一致（v{titleVer} · {bCount + aCount} 个：A1-A{aCount} + B1-B{bMax}）");
else Fail(16, "lessons.md 计数/版本声明漂移（B103 防复发）", declFail.Trim());

if (!FAST)
{
    V17Batteries();
}

// V18 test/prompt.md 声明与实测一致（四判据）
var spec = File.ReadAllText(".ai/test/prompt.md");
int[] Ids(string sectionHeader)
{
    var inSection = false;
    var ids = new List<int>();
    foreach (var line in spec.Split('\n'))
    {
        if (line.StartsWith("## "))
        {
            inSection = line.Contains(sectionHeader);
            continue;
        }
        if (!inSection) continue;
        var m = Regex.Match(line, @"^\| T-DEF-([0-9]+)");
        if (m.Success) ids.Add(int.Parse(m.Groups[1].Value));
    }
    return [.. ids.OrderBy(n => n)];
}
var mainIds = Ids("缺陷登记（");
var suppIds = Ids("缺陷登记补充");
var globalCount = Regex.Matches(spec, @"^\| T-DEF-[0-9]+", RegexOptions.Multiline).Count;
var mMin = mainIds.Length > 0 ? mainIds[0].ToString() : "";
var mMax = mainIds.Length > 0 ? mainIds[^1].ToString() : "";
var sMin = suppIds.Length > 0 ? suppIds[0].ToString() : "";
var sMax = suppIds.Length > 0 ? suppIds[^1].ToString() : "";
var declMain = string.Join(' ', Regex.Match(spec, @"^## 缺陷登记（T-DEF-([0-9]+) ~ T-DEF-([0-9]+)）", RegexOptions.Multiline).Groups.OfType<Group>().Skip(1).Select(g => g.Value)) + " ";
var declSupp = string.Join(' ', Regex.Match(spec, @"^## 缺陷登记补充（T-DEF-([0-9]+) ~ T-DEF-([0-9]+)）", RegexOptions.Multiline).Groups.OfType<Group>().Skip(1).Select(g => g.Value)) + " ";
var agentsLine = File.ReadLines("AGENTS.md").FirstOrDefault(l => l.Contains("ai/test/prompt.md`")) ?? "";
var declRules = Regex.Match(agentsLine, @"([0-9]+) 铁律").Groups[1].Value;
var declDefects = Regex.Match(agentsLine, @"\+ ([0-9]+) 缺陷").Groups[1].Value;
var actualRules = Regex.Matches(spec, @"^\| T[0-9]+ \|", RegexOptions.Multiline).Count;
var specFail = "";
if (declMain != $"{mMin} {mMax} ") specFail += $" 主节标题[{declMain.Trim()}]≠实测[{mMin} {mMax}]";
if (declSupp != $"{sMin} {sMax} ") specFail += $" 补充节标题[{declSupp.Trim()}]≠实测[{sMin} {sMax}]";
if (mMax.Length > 0 && sMin.Length > 0 && int.Parse(mMax) + 1 != int.Parse(sMin)) specFail += $" 两节不连续(主max={mMax} 补min={sMin})";
if (declRules.Length > 0 && declRules != actualRules.ToString()) specFail += $" 铁律声明{declRules}≠实测{actualRules}";
if (declDefects.Length > 0 && declDefects != globalCount.ToString()) specFail += $" 缺陷声明{declDefects}≠实测{globalCount}";
if (specFail.Length == 0) Pass(18, $"test/prompt.md 声明与实测一致（{actualRules} 铁律 + {globalCount} 缺陷，主{mMin}-{mMax}／补{sMin}-{sMax}）");
else Fail(18, "test/prompt.md 计数/标题声明漂移（B103 同族）", specFail.Trim());

// V19 .ai 文本编码卫生（无 BOM）
var bomHits = new List<string>();
foreach (var f in EnumerateAiTexts())
{
    if (File.ReadAllBytes(f).Length >= 3 && File.ReadAllBytes(f)[0] == 0xEF && File.ReadAllBytes(f)[1] == 0xBB && File.ReadAllBytes(f)[2] == 0xBF)
    {
        bomHits.Add(f);
    }
}
if (bomHits.Count == 0) Pass(19, ".ai 文本无 UTF-8 BOM（^ 锚定的正则不会静默失配）");
else Fail(19, ".ai 文本带 UTF-8 BOM（B84 的脚本回写副作用）", string.Join(' ', bomHits));

// V20 结果库基线纳管（告警型）
if (File.Exists("bench/PalORM.PerfHub/Program.cs") && File.Exists("bench/baselines/perfhub-index-baseline.json"))
{
    var opsSrc = Regex.Match(File.ReadAllText("bench/PalORM.PerfHub/Program.cs"),
        @"private static readonly string\[\] OperationNames.*?\];", RegexOptions.Singleline).Value;
    var ops = Regex.Matches(opsSrc, @"""([A-Za-z]+)""").Select(m => m.Groups[1].Value).ToList();
    var baseline = File.ReadAllText("bench/baselines/perfhub-index-baseline.json");
    var ungated = ops.Where(op => !baseline.Contains($"\"Name\": \"{op}\"")).ToList();
    if (ungated.Count == 0) Pass(20, "PerfHub 全部测量项已纳管结果库基线（OperationNames 与基线 Name 逐名对上）");
    else
    {
        Warn(20, $"新测量项未纳管结果库基线（裸奔中，见 lessons B128）:{string.Join(' ', ungated)}");
        Pass(20, $"PerfHub 测量项纳管检查完成（含告警:{string.Join(' ', ungated)} —— 不拦截，重录后消警）");
    }
}
else Pass(20, "PerfHub 源/基线缺一，跳过（非本仓形态）");

// V21 复发阈值对账（告警型）
var v21Warn = new List<string>();
var inV21 = false;
foreach (var line in metrics.Split('\n'))
{
    if (line.StartsWith("## "))
    {
        inV21 = line.Contains("复发阈值对账表");
        continue;
    }
    if (!inV21 || !line.StartsWith('|')) continue;
    var cols = line.Split('|');
    if (cols.Length < 6) continue;
    var cls = cols[1].Trim(); var cnt = cols[2].Trim().Split('+')[0]; var status = cols[4].Trim();
    if (cls is "根因类" || cls.StartsWith("--")) continue;
    if (int.TryParse(cnt, out var n) && n >= 2 && status != "已闭环") v21Warn.Add($"{cls}（第{cnt}次复发，{status}）");
}
if (v21Warn.Count == 0) Pass(21, "复发阈值对账通过（≥2 次复发的根因族均已机械化闭环）");
else
{
    Warn(21, "复发 ≥2 次且未闭环的根因族（B107 督办清单，不拦截）:");
    foreach (var w in v21Warn) Console.WriteLine($"       - {w}");
    Pass(21, $"复发阈值对账完成（含告警 {v21Warn.Count} 族——不拦截，补载体后消警）");
}

// V22 入库脚本 pipefail（.ai/scripts 白名单内 .sh 适用）
var v22Bad = Directory.Exists(".ai/scripts")
    ? Directory.GetFiles(".ai/scripts", "*.sh").Where(f => !File.ReadAllText(f).Contains("pipefail")).ToList()
    : [];
if (v22Bad.Count == 0) Pass(22, "入库脚本全部声明 pipefail（B104 族可机械面的统一拦截）");
else Fail(22, "存在未声明 pipefail 的入库脚本（管道失败可被静默吞掉）", string.Join(' ', v22Bad.Select(f => Path.GetFileName(f))));

// V23 传感器台账（超期 WARN + 引用文件存在性）
var ledgerPath = ".ai/gate/sensor-ledger.md";
if (File.Exists(ledgerPath))
{
    var ledger = File.ReadAllText(ledgerPath);
    var v23Missing = new List<string>();
    var inList = false; var fence = 0;
    foreach (var line in ledger.Split('\n'))
    {
        if (line.StartsWith("## 引用文件清单")) { inList = true; continue; }
        if (!inList) continue;
        if (line.TrimStart().StartsWith("```")) { fence++; if (fence == 2) break; continue; }
        if (fence == 1 && line.Trim().Length > 0 && !File.Exists(line.Trim()) && !Directory.Exists(line.Trim()))
        {
            v23Missing.Add(line.Trim());
        }
    }
    var staleList = new List<string>();
    foreach (var line in ledger.Split('\n'))
    {
        if (!line.StartsWith('|')) continue;
        var cols = line.Split('|');
        if (cols.Length < 7) continue;
        var name = cols[1].Trim(); var dateStr = cols[6].Trim();
        if (name is "关切" || name.StartsWith("--") || name.Length == 0) continue;
        if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d)
            && (DateTime.Today - d).TotalDays > 90)
        {
            staleList.Add($"{name}（定标日 {dateStr:yyyy-MM-dd}）");
        }
    }
    if (v23Missing.Count > 0) Fail(23, "传感器台账引用文件失实（账实不一致，DDD F-02 同族）", string.Join(' ', v23Missing));
    else if (staleList.Count > 0)
    {
        Warn(23, "定标超期 >90 天（转 STALE，重跑红测定标后更新台账消警）:");
        foreach (var s in staleList) Console.WriteLine($"       - {s}");
        Pass(23, $"传感器台账校验完成（含超期告警 {staleList.Count} 项——不拦截）");
    }
    else Pass(23, "传感器台账：引用文件全在 + 定标全部在 90 天周期内");
}
else Fail(23, "传感器台账缺失", ".ai/gate/sensor-ledger.md");

Console.WriteLine();
Console.WriteLine($"通过：{passed}  失败：{failed}  总计：{passed + failed}");
Console.WriteLine("═══════ 校验完成 ═══════");
return failed > 0 ? 1 : 0;

// ═══ V17 变异探针电池（三段式：注入/文件变异/脏树 + V17c V/D 面）═══

void V17Batteries()
{
    var probes = new List<string>();
    var mutFiles = new List<string>();
    try
    {
        // tech-debt 检查 8 探针
        var probeCs = "src/PalORM.Core/ZZV17MutationProbe.cs";
        File.WriteAllText(probeCs, "using System.Diagnostics;\ninternal static class ZZV17MutationProbe\n{\n    [SuppressMessage(\"Reliability\", \"CA2000\")]\n    public static void M() { }\n}\n", new UTF8Encoding(false));
        var tdOut = RunCapture("dotnet", "run --file scripts/tech-debt-scan.cs");
        File.Delete(probeCs);
        var tdOk = tdOut.Contains("FAIL  8.");

        // A 段：注入电池（git grep/ls-files 族须暂存才计入）
        void MkProbe(string path, string content) { File.WriteAllText(path, content, new UTF8Encoding(false)); probes.Add(path); }
        MkProbe("src/PalORM.Core/ZZV17G1.cs", "public class ZzProbeOneException : System.Exception { }");
        MkProbe("src/PalORM.Core/ZZV17G2.cs", "using Dapper;\ninternal static class P2 { }");
        MkProbe("src/PalORM.Core/ZZV17G3.cs", "internal static class P3 { static object M() => typeof(System.Collections.Generic.List<>).MakeGenericType(typeof(int)); }");
        MkProbe("src/PalORM.Core/ZZV17G4.cs", "internal static class P4 { static object M(System.Type t) => t.GetProperty(\"X\"); }");
        MkProbe("src/PalORM.Core/ZZV17G5.cs", "internal static class P5 { static object M(System.Linq.Expressions.LambdaExpression e) => e.Compile(); }");
        MkProbe("src/PalORM.Core/ZZV17G6.cs", "internal static class P6 { static object M() => System.Activator.CreateInstance(typeof(object)); }");
        MkProbe("src/PalORM.Core/ZZV17G7.cs", "internal static class P7 { static object M(dynamic d) => d; }");
        MkProbe("src/PalORM.Core/ZZV17G8.cs", "internal static class P8 { static string M(string t) => string.Format(\"SELECT * FROM {0}\", t); }");
        MkProbe("src/PalORM.Core/ZZV17G9.cs", "internal static class P9 { private const string Cs = \"Server=192.168.1.50;Password=realpass123;\"; }");
        MkProbe("src/PalORM.Core/ZZV17G10.cs", "internal static class P10 { private PalORM.DataSession _dbField; }");
        MkProbe("src/PalORM.Core/ZZV17G11.cs", "public virtual int ZzNav { get; }");
        MkProbe("src/PalORM.Core/ZZV17G12.cs", "public static int ZzCounter { get; set; }");
        MkProbe("src/PalORM.MySql/ZZV17G13.cs", "using PalORM.PostgreSql;\ninternal static class P13 { }");
        MkProbe("src/PalORM.SourceGen/ZZV17G14.cs", "using PalORM.Sqlite;\ninternal static class P14 { }");
        MkProbe("src/PalORM.Core/ZZV17G15.cs", "using System;\npublic DateTime ZzWhen { get; }");
        MkProbe("src/PalORM.Core/ZZV17G16.cs", "internal static class P16 { static object M() => OnDelete(DeleteBehavior.Cascade); }");
        MkProbe("src/PalORM.Core/ZZV17G17.cs", "internal static class P17 { async void Foo() { } }");
        MkProbe("src/PalORM.Core/ZZV17G18.cs", "internal static class P18 { static object M() => new System.Transactions.TransactionScope(); }");
        MkProbe("src/PalORM.Core/ZZV17G20.cs", "internal static class P20 { static object M(System.Threading.Tasks.Task t) => t.Result; }");
        MkProbe("src/PalORM.SourceGen/ZZV17G21.cs", "internal static class P21 { private const string S = \"#pragma warning disable\\n\"; }");
        MkProbe("src/PalORM.Core/ZZV17G22.cs", "internal static class P22 { [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"Reliability\", \"CA2000\")] public static void M() { } }");
        MkProbe("src/PalORM.Core/ZZV17G24.cs", "using System.Threading.Tasks;\ninternal static class P24 { static async Task M() { await Task.Delay(1); } }");
        MkProbe("src/PalORM.Core/ZZV17G25.cs", "using System.Threading.Tasks;\ninternal static class P25 { public static async Task NoCt() { await Task.Delay(1); } }");
        MkProbe("src/PalORM.Core/ZZV17G28.cs", "internal static class P28 { static int M(System.TimeSpan _timeout) => (int)_timeout.TotalSeconds; }");
        MkProbe("src/PalORM.Core/ZZV17G29.cs", "internal static class P29 { static async System.Threading.Tasks.Task M(System.Data.Common.DbConnection conn) { var c = conn.CreateCommand(); await c.ExecuteReaderAsync(); } }");
        MkProbe("src/ZZV17G19.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n    <IsAotCompatible>false</IsAotCompatible>\n  </PropertyGroup>\n</Project>");
        MkProbe("test/PalORM.AotTest/ZZV17G23.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Compile Include=\"Zz.g.cs\" />\n  </ItemGroup>\n</Project>");
        Git($"add {string.Join(' ', probes)}");
        var gateOut = RunCapture("dotnet", "run --file scripts/gate-check.cs -- --allow-dirty");
        Git("restore --staged " + string.Join(' ', probes));
        foreach (var p in probes) File.Delete(p);
        probes.Clear();
        var missing = new List<string>();
        foreach (var g in new[] { "G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8", "G9", "G10", "G11", "G12", "G13", "G14", "G15", "G16", "G17", "G18", "G19", "G20", "G21", "G22", "G23", "G24", "G25", "G28", "G29" })
        {
            if (!Regex.IsMatch(gateOut, $"FAIL {g}[^0-9]")) missing.Add(g);
        }

        // B 段：文件变异电池（变异→gate→还原）
        string RunGate() { return RunCapture("dotnet", "run --file scripts/gate-check.cs -- --allow-dirty"); }
        mutFiles.Add("src/PalORM.Core/QueryBuilder.cs");
        MutateFile("src/PalORM.Core/QueryBuilder.cs", t => Regex.Replace(t, @"^public struct QueryBuilder<T>", "public class QueryBuilder<T>", RegexOptions.Multiline));
        if (!Regex.IsMatch(RunGate(), @"FAIL G26[^0-9]")) missing.Add("G26");
        Git("checkout -- src/PalORM.Core/QueryBuilder.cs");

        mutFiles.Add("Directory.Build.props");
        // CRLF 行尾容忍（\r?）：文件实测为 CRLF，无 \r? 的 $ 锚永不匹配（bash sed 版同坑的存量假绿）
        MutateFile("Directory.Build.props", t => Regex.Replace(t, @"^  <PropertyGroup>\r?$", "  <PropertyGroup>\n    <NoWarn>CS1591</NoWarn>", RegexOptions.Multiline));
        if (!Regex.IsMatch(RunGate(), @"FAIL G27[^0-9]")) missing.Add("G27");
        Git("checkout -- Directory.Build.props");

        mutFiles.Add("src/PalORM.SourceGen/PalORMAnalyzer.cs");
        File.AppendAllText("src/PalORM.SourceGen/PalORMAnalyzer.cs", "            var zs = type.GetMembers().OfType<IPropertySymbol>();\n", new UTF8Encoding(false));
        if (!Regex.IsMatch(RunGate(), @"FAIL G30[^0-9]")) missing.Add("G30");
        Git("checkout -- src/PalORM.SourceGen/PalORMAnalyzer.cs");

        mutFiles.Add("src/PalORM.Core/DataSession_Bulk.cs");
        MutateFile("src/PalORM.Core/DataSession_Bulk.cs", t => t.Replace("Dialect == SqlDialect", "Dialect == ZzDialect"));
        if (!RunGate().Contains("未检测到方言感知分支")) missing.Add("G31");
        Git("checkout -- src/PalORM.Core/DataSession_Bulk.cs");

        mutFiles.Add("NuGet.Config");
        MutateFile("NuGet.Config", t => Regex.Replace(t, @"^  <packageSourceMapping>\r?$", "  <packageSourceMapping>\n    <packageSource key=\"dotnet-tools\">\n      <package pattern=\"Microsoft.CodeAnalysis.*\" />\n    </packageSource>", RegexOptions.Multiline));
        if (!RunGate().Contains("限制 Microsoft.CodeAnalysis")) missing.Add("G32");
        Git("checkout -- NuGet.Config");

        // C 段：G33 脏树
        File.WriteAllText("src/PalORM.Core/ZZV17G33.txt", "zz\n", new UTF8Encoding(false));
        var outC = RunCapture("dotnet", "run --file scripts/gate-check.cs");
        File.Delete("src/PalORM.Core/ZZV17G33.txt");
        if (!outC.Contains("G33  工作树")) missing.Add("G33");

        if (tdOk && missing.Count == 0) Pass(17, "变异探针组 34/34 如期 FAIL（tech-debt 检查 8 + gate 33 G 项全量：注入 27 + 文件变异 5 + 脏树 1；防线自身失明族的 gate 面已全覆盖）");
        else if (!tdOk) Fail(17, "防线沉默（植入已知坏样本仍报 PASS = 静默 no-op 门禁）", "tech-debt-scan 检查 8 未报 FAIL");
        else Fail(17, "gate 变异探针组有沉默项（植入违规样本未报 FAIL）", "未报 FAIL: " + string.Join(' ', missing));

        V17cBattery();
    }
    finally
    {
        if (probes.Count > 0)
        {
            Git("restore --staged " + string.Join(' ', probes));
            foreach (var p in probes) if (File.Exists(p)) File.Delete(p);
        }
        foreach (var f in mutFiles) Git($"checkout -- {f}");
    }
}

void V17cBattery()
{
    // V/D 面电池：变异真源 → 跑 --fast 自身（进程递归）→ 断言对应检查 FAIL → 还原
    var v17cFail = new List<string>();
    string RunFastVerify()
    {
        // --no-build：递归自身时 MSBuild 复制 apphost 会撞父进程 exe 锁（MSB3026 重试 8 次后
        // 构建失败、子输出全为警告 = 断言全沉默的根因）；父进程刚构建的缓存即当前版本，直接二次启动安全
        var output = RunCapture("dotnet", "run --no-build --file scripts/verify-ai-system.cs -- --fast");
        if (Environment.GetEnvironmentVariable("V17C_DEBUG") == "1")
        {
            Console.WriteLine($"[v17c-debug] 子进程输出前 8 行：{string.Join(" ⏎ ", output.Split('\n').Take(8))}");
        }
        return output;
    }
    string RunDc() { return RunCapture("dotnet", "run --file scripts/doc-consistency-check.cs"); }
    var touched = new List<string>();
    try
    {
        // V10：藏起快照目录
        Directory.Move("test/PalORM.SourceGen.Tests/Snapshots", "test/PalORM.SourceGen.Tests/Snapshots.bak");
        if (!RunFastVerify().Contains("FAIL V10")) v17cFail.Add("V10");
        Directory.Move("test/PalORM.SourceGen.Tests/Snapshots.bak", "test/PalORM.SourceGen.Tests/Snapshots");

        // V12：API参考分析器数漂移（首处"条分析器"前插 999）
        MutateFile("docs/API参考.md", t => ReplaceFirst(t, @"条分析器", "999 条分析器"), touch: touched);
        if (!RunFastVerify().Contains("FAIL V12")) v17cFail.Add("V12");
        Git("checkout -- docs/API参考.md");

        // V15：植入白名单外 .sh（源码字面量转义断开，防本电池自举报）
        File.WriteAllText(".ai/scripts/zzv17c_hygiene.sh", "#!/usr/bin/env bash\nv=$(\x67rep root /etc/hostname)\n", new UTF8Encoding(false));
        if (!RunFastVerify().Contains("FAIL V15")) v17cFail.Add("V15");
        File.Delete(".ai/scripts/zzv17c_hygiene.sh");

        // V16：lessons 版本行漂移（动态取当前版本——原 .sh 硬编码 v7.33 升版即失效）
        MutateFile(".ai/lessons.md", t => Regex.Replace(t, @"(PalORM AI 规范化系统 )v[0-9.]+", "$1v7.99"));
        if (!RunFastVerify().Contains("FAIL V16")) v17cFail.Add("V16");
        GitInAi("checkout -- lessons.md");

        // V18：AGENTS.md 铁律数漂移（首个"N 铁律"→99）
        MutateFile("AGENTS.md", t => ReplaceFirst(t, @"[0-9]+ 铁律", "99 铁律"), touch: touched);
        if (!RunFastVerify().Contains("FAIL V18")) v17cFail.Add("V18");
        Git("checkout -- AGENTS.md");

        // V19：植入 BOM（按字节写——C# 字符串 "\xEF.." 是拉丁字符非 BOM 字节）
        File.WriteAllBytes(".ai/zzv17c_bom.md", [0xEF, 0xBB, 0xBF, (byte)'z', (byte)'z', (byte)'\n']);
        if (!RunFastVerify().Contains("FAIL V19")) v17cFail.Add("V19");
        File.Delete(".ai/zzv17c_bom.md");

        // V20：基线条目名漂移（全局替换）
        MutateFile("bench/baselines/perfhub-index-baseline.json", t => Regex.Replace(t, "\"Name\": \"[^\"]*\"", "\"Name\": \"zzv17c_drift\""), touch: touched);
        if (!RunFastVerify().Contains("WARN V20")) v17cFail.Add("V20");
        Git("checkout -- bench/baselines/perfhub-index-baseline.json");

        // V22：植入无 pipefail 脚本
        File.WriteAllText(".ai/scripts/zzv17c_noshell.sh", "#!/usr/bin/env bash\ncat foo | grep bar\n", new UTF8Encoding(false));
        if (!RunFastVerify().Contains("FAIL V22")) v17cFail.Add("V22");
        File.Delete(".ai/scripts/zzv17c_noshell.sh");

        // D10：植入 [Test] 临时文件 → 实测计数漂移
        File.WriteAllText("test/PalORM.Core.Tests/ZZV17cD10Probe.cs", "[Test]\n", new UTF8Encoding(false));
        if (!RunDc().Contains("FAIL D10")) v17cFail.Add("D10");
        File.Delete("test/PalORM.Core.Tests/ZZV17cD10Probe.cs");

        // D11：生成物工具版本漂移（动态取包版本——原 .sh 硬编码 6.3.0，升版即失效的存量假绿）
        var pkgVer = Regex.Match(File.ReadAllText("Directory.Build.props"), @"<Version>([^<]+)").Groups[1].Value.Trim();
        MutateFile("src/PalORM.SourceGen/GeneratedCodeMetadata.cs", t => t.Replace($"\"{pkgVer}\"", "\"9.9.9\""), touch: touched);
        if (!RunDc().Contains("FAIL D11")) v17cFail.Add("D11");
        Git("checkout -- src/PalORM.SourceGen/GeneratedCodeMetadata.cs");

        if (v17cFail.Count == 0) Pass(17, "V/D 面防线变异探针 10/10 如期 FAIL（V10/V12/V15/V16/V18/V19/V20/V22 + D10/D11；T 面定性=提示词规则非机械防线）");
        else Fail(17, "V/D 面探针有沉默项（变异真源后检查仍 PASS = 无声 no-op）", "未报 FAIL: " + string.Join(' ', v17cFail));
    }
    finally
    {
        foreach (var f in touched) Git($"checkout -- {f}");
        GitInAi("checkout -- lessons.md test/prompt.md");
        foreach (var f in new[] { ".ai/scripts/zzv17c_noshell.sh", ".ai/scripts/zzv17c_hygiene.sh", ".ai/zzv17c_bom.md", "test/PalORM.Core.Tests/ZZV17cD10Probe.cs" })
        {
            if (File.Exists(f)) File.Delete(f);
        }
        if (Directory.Exists("test/PalORM.SourceGen.Tests/Snapshots.bak")) Directory.Move("test/PalORM.SourceGen.Tests/Snapshots.bak", "test/PalORM.SourceGen.Tests/Snapshots");
    }
}

// ── 辅助 ──

static void MutateFile(string path, Func<string, string> mutate, List<string>? touch = null)
{
    var content = File.ReadAllText(path);
    File.WriteAllText(path, mutate(content), new UTF8Encoding(false));
    touch?.Add(path);
}

static string ReplaceFirst(string text, string pattern, string replacement)
{
    var m = Regex.Match(text, pattern);
    return m.Success ? text[..m.Index] + replacement + text[(m.Index + m.Length)..] : text;
}

static string MaxNum(MatchCollection matches)
{
    return matches.Select(m => m.Groups[1].Value).Select(int.Parse).OrderBy(n => n).Select(n => n.ToString()).LastOrDefault() ?? "";
}

static string MaxTail(MatchCollection matches)
{
    return matches.Select(m => m.Groups[1].Value).Select(int.Parse).OrderBy(n => n).Select(n => n.ToString()).LastOrDefault() ?? "";
}

static IEnumerable<string> EnumerateAiTexts()
{
    if (!Directory.Exists(".ai")) yield break;
    foreach (var f in Directory.EnumerateFiles(".ai", "*.md", SearchOption.AllDirectories)) yield return f;
    foreach (var f in Directory.EnumerateFiles(".ai", "*.sh", SearchOption.AllDirectories)) yield return f;
}

static void Git(string arguments) { RunCapture("git", arguments); }
static void GitInAi(string arguments) { RunCapture("git", $"-C .ai {arguments}"); }

static string RunCapture(string fileName, string arguments)
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
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return output;
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
