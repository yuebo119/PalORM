// verify-ai-system.cs（自 .ai/scripts/verify-ai-system.sh 迁移，脚本全面 C# 化轮）
// .ai 系统机械校验 V1-V25：提示词引用可解析、口径数字与事实一致、配套脚本存在、防线能失败、引用锚真实在案。
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

// --public：只跑「主仓可见」的校验（不读 .ai 本地知识层）。用途：CI 镜像里 .ai 不在克隆中，
// 但 V2/V6/V10/V12/V20 只依赖 docs/scripts/test/bench，完全可以在 CI 运行；此前这五项
// 在 CI 零覆盖（V12 的 PALORM 诊断计数对账尤其关键）。--probes 追加 V17 gate/tech-debt
// 变异探针电池（V17c 的 V/D 面依赖 .ai 文件，public 模式下自动跳过）。
var PUBLIC = args.Contains("--public", StringComparer.Ordinal);
var PROBES = args.Contains("--probes", StringComparer.Ordinal);
if (PUBLIC)
{
    Console.WriteLine("模式：--public（只跑主仓可见校验；.ai 本地知识层相关项不参与）");
    Console.WriteLine();
    CheckV2();
    CheckV6();
    CheckV10();
    CheckV12();
    CheckV20();
    if (PROBES)
    {
        Console.WriteLine();
        V17Batteries();
    }
    Console.WriteLine();
    Console.WriteLine($"通过：{passed}  失败：{failed}  总计：{passed + failed}");
    Console.WriteLine("═══════ 校验完成（public 子集）═══════");
    return failed > 0 ? 1 : 0;
}

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
CheckV2();

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
CheckV6();

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
CheckV10();

// V11 metrics 账本
var metrics = File.Exists(".ai/review/metrics.md") ? File.ReadAllText(".ai/review/metrics.md") : "";
if (metrics.Contains("## 缺陷逃逸账本") && metrics.Contains("## 轮次记录")) Pass(11, "metrics 账本存在且结构完整");
else Fail(11, "metrics 账本缺失或结构不完整", ".ai/review/metrics.md 需含「轮次记录」与「缺陷逃逸账本」");

// V12 PALORM 诊断编号双向对账
CheckV12();

// V12b lessons 含 B24-B27
var lessonsText = File.ReadAllText(".ai/lessons.md");
if (lessonsText.Contains("B24 RS1032") && lessonsText.Contains("B26 防静默错误")) Pass(13, "lessons.md 含诊断工程化教训 B24-B27");
else Fail(13, "lessons.md 缺诊断工程化教训", "需登记 B24(RS1032)/B25(XML转义)/B26(价值分层)/B27(变量流局限)");

if (!FAST)
{
    // V14 核心防线脚本可执行性：完成标记判活（区分"检查失败但活着"与"中途死亡"）
    // 2026-10-18 第二轮审计扩面：原注册表固定 10 项而 scripts/ 实有 28 个，新增工具
    // （backup-ai-system / compare-perf-batches 等）不在存活监测面。现补齐可无副作用运行
    // 的防线与工具，并在结果行报告「未注册」数量，避免"10/10"被误读为全部脚本。
    var finishMark = new Dictionary<string, (string Mark, string Args)>
    {
        ["gate-check"] = ("扫描完成", ""),
        ["test-gate"] = (@"门禁(未)?通过", ""),
        ["tech-debt-scan"] = ("结果: ", ""),
        ["doc-consistency-check"] = ("通过: ", ""),
        ["assertion-strength-check"] = ("扫描完成", ""),
        ["refine-scan"] = ("纪律补充", ""),
        ["review-scope"] = ("账本规则", ""),
        ["review-snapshot"] = ("评审基线快照", ""),
        ["probe-template"] = ("用法", ""),
        ["verify-action-items"] = ("验证完成", ""),
        // 以下为第二轮审计补齐：均可无副作用运行（不写库、不建备份、不跑基准）
        // 注意 secret-guard 在无暂存文件时走「✓ 无 staged 文件」分支（exit 0），
        // 标记必须覆盖该分支，否则干净树上会被误判为无完成标记（实测于 2026-10-18）。
        ["secret-guard"] = (@"敏感信息检查通过|无 staged 文件", ""),
        ["stub-check"] = (@"无明确空壳|发现 \d+ 个", ""),
        ["gate-param-collection-reuse"] = (@"全部参数集合变更|未声明 \d+ 处", ""),
        ["precommit-build-check"] = (@"无需构建|受影响项目构建通过", ""),
        ["compare-perf-batches"] = ("用法:", "--help"),
    };
    var deadScripts = new List<string>();
    var latestLedger = Directory.Exists(".ai/review/history/action-items")
        ? Directory.GetFiles(".ai/review/history/action-items", "*.md").OrderByDescending(f => f).FirstOrDefault()
        : null;
    foreach (var (name, entry) in finishMark)
    {
        var arguments = name switch
        {
            "verify-action-items" when latestLedger is not null => $"run --file scripts/{name}.cs -- {latestLedger}",
            _ => $"run --file scripts/{name}.cs{(entry.Args.Length > 0 ? " -- " + entry.Args : "")}",
        };
        if (name == "verify-action-items" && latestLedger is null) { continue; } // 无账本跳过（与 .sh 版一致）
        var (code, output) = RunCaptureWithCode("dotnet", arguments);
        // V14 语义（2026-10-18 审计修复）：只判"跑到完成标记"会把"检查红着但活着"算通过，
        // 与 E5「防线无触发点」同型——完成标记与退出码必须同时满足。
        // 退出码契约按各脚本自述区分（probe-template 等辅助件缺参返回 2=用法错，属正常行为，
        // 其自证方式是"打印用法说明"而非完成标记）：0=健康；2=用法错（该项以用法标记替代
        // 完成标记判定）；1=检查失败，一律计入失败。
        if (code == 2 && name == "probe-template")
        {
            if (!output.Contains("用法", StringComparison.Ordinal)) deadScripts.Add("probe-template(exit=2 且无用法说明)");
            continue;
        }
        if (code != 0) deadScripts.Add($"{name}(exit={code})");
        else if (!Regex.IsMatch(output, entry.Mark)) deadScripts.Add($"{name}(无完成标记)");
    }
    var allScripts = Directory.Exists("scripts") ? Directory.GetFiles("scripts", "*.cs").Length : 0;
    var unregistered = allScripts - finishMark.Count;
    var unregisteredNote = unregistered > 0
        ? $"；另有 {unregistered} 个脚本未注册（需入参/耗时/有副作用，见 scripts/README 人工工具）"
        : "";
    if (deadScripts.Count == 0)
        Pass(14, $"已注册防线脚本全部可执行且全绿（{finishMark.Count}/{finishMark.Count} 退出码 0 + 完成标记{unregisteredNote}）");
    else Fail(14, "防线脚本失败（退出码非 0 或未跑到完成标记——结果未被消费的静默门禁）", string.Join(' ', deadScripts));
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
CheckV20();

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

// V24 提交锚验存（lessons/CHANGELOG/perf-ledger 引用的短哈希须在主仓或 .ai 仓真实存在）
// 出处：2026-10-14 独立复核轮抓到 ledger 凭记忆写假哈希（82d8d01 不存在，实为 50478d9）——
// "锚点验存"此前靠人工 git log 逐个对账，本轮机械化（B93 核真家族的延伸面）。
// 口径：7-12 位十六进制候选且含 a-f 字母（纯数字候选=日期/批号，排除）；双仓 cat-file 验存；
// 只验存在性不验语义（存在但引错提交属人工面）；2026-07-17 仓库重建前的锚对象不在本地库，
// 走 .ai/gate/anchor-baseline.md 基线豁免（共同理由+清理触发见该文件；另含表 2 文献性引用与
// 表 3 姊妹仓引用——假锚反例/变异探针形态/跨仓（Pal.DDD）提交等本地双仓验存范围外的故意非锚，
// V24 不辨语义故一并登记）。
var v24Baseline = new HashSet<string>();
if (File.Exists(".ai/gate/anchor-baseline.md"))
{
    foreach (var bl in File.ReadAllText(".ai/gate/anchor-baseline.md").Split('\n'))
        if (bl.TrimStart().StartsWith('|'))
        {
            var bm = Regex.Match(bl, @"\b[0-9a-f]{7,12}\b");
            if (bm.Success) v24Baseline.Add(bm.Value);
        }
}
var v24Missing = new List<string>();
var v24Checked = 0; var v24Exempt = 0;
foreach (var f in new[] { ".ai/lessons.md", ".ai/CHANGELOG-lessons.md", ".ai/perf-ledger.md" })
{
    if (!File.Exists(f)) { v24Missing.Add($"{f}(文件缺失)"); continue; }
    var seen = new HashSet<string>();
    foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\b[0-9a-f]{7,12}\b"))
    {
        var h = m.Value;
        if (!h.Any(c => c is >= 'a' and <= 'f')) continue;
        if (!seen.Add(h)) continue;
        if (v24Baseline.Contains(h)) { v24Exempt++; continue; }
        v24Checked++;
        if (RunCapture("git", $"cat-file -t {h}").Trim() == "commit") continue;
        if (RunCapture("git", $"-C .ai cat-file -t {h}").Trim() == "commit") continue;
        v24Missing.Add($"{h}({Path.GetFileName(f)})");
    }
}
if (v24Missing.Count == 0)
    Pass(24, $"提交锚验存通过（{v24Checked} 个哈希在主仓/.ai 均在案，基线豁免 {v24Exempt} 个见 anchor-baseline.md 三表）");
else
    Fail(24, "知识文件引用本地不存在的提交锚（凭记忆写哈希族——订正哈希或登记基线理由）", string.Join(' ', v24Missing));

// V25 verify 计数声明三方对齐（B103 族：README 头段/README 防线表行/主仓 AGENTS.md 多写入点）
// 2026-10-16 实例：v7.36 配套机械化只改 README 头段（23→24/fast 21→22），防线清单表行
// （仍 23 项/V1–V23/fast 21/V17c 10 探针）与主仓 AGENTS.md（仍"23 项里的 21 项"）两处漏改，
// --fast 全绿照过——V16/V18 只覆盖 lessons/test 两个声明面，README 与 AGENTS 的计数无人核。
// 判据全部从本脚本源码推导（实现最大值/fast 跳过数/V17c 探针数），新增检查项零硬编码维护。
var v25Src = File.ReadAllText("scripts/verify-ai-system.cs");
var v25Impl = MaxNum(Regex.Matches(v25Src, @"(?:Pass|Fail|Warn)\(([0-9]+)"));
var v25Skip = Regex.Matches(v25Src, @"if \(!FAST\)").Count;
var v25Readme = File.ReadAllText(".ai/README.md");
var v25Header = Regex.Match(v25Readme, @"([0-9]+) 项（V1–V([0-9]+)）");
var v25Table = Regex.Match(v25Readme, @"\*\*([0-9]+) 项\*\*：V1–V");
var v25Fast = Regex.Matches(v25Readme, @"--fast`?\s*[跑出] ([0-9]+) 项").Select(m => m.Groups[1].Value).Distinct().ToList();
var v25Ag = Regex.Match(File.ReadAllText("AGENTS.md"), @"([0-9]+) 项里的 ([0-9]+) 项");
var v25ProbeClaim = Regex.Match(v25Readme, @"V/D 面 (\d+) 探针");
var v25ProbeActual = Regex.Matches(v25Src, @"v17cFail\.Add\(").Count;
var v25ExpectFast = v25Impl.Length > 0 ? (int.Parse(v25Impl) - v25Skip).ToString() : "";
if (v25Impl.Length > 0 && v25Header.Success && v25Header.Groups[1].Value == v25Impl && v25Header.Groups[2].Value == v25Impl
    && v25Table.Success && v25Table.Groups[1].Value == v25Impl
    && v25Fast.Count == 1 && v25Fast[0] == v25ExpectFast
    && v25Ag.Success && v25Ag.Groups[1].Value == v25Impl && v25Ag.Groups[2].Value == v25ExpectFast
    && v25ProbeClaim.Success && v25ProbeClaim.Groups[1].Value == v25ProbeActual.ToString())
    Pass(25, $"verify 计数声明三方对齐（{v25Impl} 项全量 / fast {v25ExpectFast} 项 / V17c 探针 {v25ProbeActual} 个）");
else
    Fail(25, "verify 计数声明漂移（README 头段/表行/AGENTS.md 与脚本实现不一致——B103 族）",
        $"实现 V1-V{(v25Impl.Length > 0 ? v25Impl : "?")}（fast 跳过 {v25Skip} → {v25ExpectFast}，V17c 探针 {v25ProbeActual} 个）；README 头段 {(v25Header.Success ? v25Header.Value : "未匹配")}，表行 {(v25Table.Success ? v25Table.Groups[1].Value + " 项" : "未匹配")}，fast {string.Join("/", v25Fast)}，探针声称 {(v25ProbeClaim.Success ? v25ProbeClaim.Groups[1].Value : "无")}，AGENTS.md {(v25Ag.Success ? v25Ag.Value : "未匹配")}");

// V26 pre-commit 段数声明四方对齐（B103 族：主仓 AGENTS.md / .ai/README 头注 /
// .ai/README 表行 / docs/编码规范.md STD-GATE-015）
// 2026-10-17 实例：用户裁定增设第七段（受影响项目构建）后全仓 grep 段数声明，发现四处，
// 其中 .ai/README 传感器表「提交前四段」自参数集合复用段加入起已漂移约 14 天、
// docs/编码规范.md STD-GATE-015「四段/21-23 项」比 README 更陈旧，均无人发现——
// V25 只对齐 verify 项数，钩子段数是另一个"文档里写着数字但无机器校验"的场合（族实例 3）。
// 判据源码推导：实际段数 = 钩子中段头 echo 行数（每段恰好一条；条件段的"跳过"变体 echo
// 不计入——.ai 缺失时 verify/gate 两段仍计为定义内段，声明文本另注条件语义）。
var v26Hook = File.ReadAllText(".githooks/pre-commit");
var v26Actual = Regex.Matches(v26Hook, @"echo ""── pre-commit:(?![^\n]*跳过)").Count;
var v26Cn = new Dictionary<string, int>(StringComparer.Ordinal)
{
    ["一"] = 1, ["二"] = 2, ["三"] = 3, ["四"] = 4, ["五"] = 5,
    ["六"] = 6, ["七"] = 7, ["八"] = 8, ["九"] = 9, ["十"] = 10
};
int CnNum(string s) => v26Cn.TryGetValue(s, out var n) ? n : -1;
var v26Agents = Regex.Match(File.ReadAllText("AGENTS.md"), @"提交前([一二三四五六七八九十]+)段");
var v26Head = Regex.Match(v25Readme, @"pre-commit 共([一二三四五六七八九十]+)段");
var v26Table = Regex.Match(v25Readme, @"提交前([一二三四五六七八九十]+)段");
var v26Std = Regex.Match(File.ReadAllText("docs/编码规范.md"), @"pre-commit ([一二三四五六七八九十]+)段拦截");
if (v26Agents.Success && v26Head.Success && v26Table.Success && v26Std.Success
    && CnNum(v26Agents.Groups[1].Value) == v26Actual
    && CnNum(v26Head.Groups[1].Value) == v26Actual
    && CnNum(v26Table.Groups[1].Value) == v26Actual
    && CnNum(v26Std.Groups[1].Value) == v26Actual)
    Pass(26, $"pre-commit 段数声明四方对齐（钩子实际 {v26Actual} 段 = AGENTS.md / README 头注 / README 表行 / 编码规范 STD-GATE-015）");
else
    Fail(26, "pre-commit 段数声明漂移（钩子实际段数与 AGENTS.md/.ai/README/编码规范声明不一致——B103 族）",
        $"钩子实际 {v26Actual} 段；AGENTS.md {(v26Agents.Success ? v26Agents.Groups[1].Value : "未匹配")} 段，README 头注 {(v26Head.Success ? v26Head.Groups[1].Value : "未匹配")} 段，README 表行 {(v26Table.Success ? v26Table.Groups[1].Value : "未匹配")} 段，编码规范 {(v26Std.Success ? v26Std.Groups[1].Value : "未匹配")} 段");

// V27 活文档引用与计数口径完整性（2026-10-18 审计轮，B103 族泛化面）
// 背景：V2 只核对一个固定文件清单、V25/V26 只核对四处写入点，固定名单之外的活文档
// （传感器台账「V1-V23」「四段」、test 提示词里已退役的 .sh 引用）无人核对，实测均漂移
// 约 2 周至 2 个月。本项把判据从"列出所有写入点"改为"扫描全部活文档"：任何活文档中的
// scripts/*.cs|sh|py|mjs 引用必须真实存在；任何 （V1-VN， 形式的 verify 总范围声明必须
// 等于脚本实现最大编号；任何 "pre-commit N 段" 声明必须等于钩子实际段数。
// 排除历史面（review/history、CHANGELOG、metrics、lessons）——历史记录按规则不可改写。
var v27Missing = new List<string>();
var v27CountBad = new List<string>();
var v27Max = MaxNum(Regex.Matches(v25Src, @"(?:Pass|Fail|Warn)\(([0-9]+)"));
var v27Docs = new List<string> { ".ai/README.md", "AGENTS.md", "CONTRIBUTING.md", "docs/编码规范.md" };
if (Directory.Exists(".ai"))
{
    foreach (var f in Directory.GetFiles(".ai", "prompt.md", SearchOption.AllDirectories)) v27Docs.Add(f.Replace('\\', '/'));
    foreach (var f in Directory.GetFiles(".ai/gate", "*.md", SearchOption.TopDirectoryOnly)) v27Docs.Add(f.Replace('\\', '/'));
}
var v27Seen = new HashSet<string>(StringComparer.Ordinal);
foreach (var doc in v27Docs.Distinct().OrderBy(d => d, StringComparer.Ordinal))
{
    if (!v27Seen.Add(doc)) continue;
    if (!File.Exists(doc)) { v27Missing.Add($"{doc}(文件缺失)"); continue; }
    var docLines = File.ReadAllLines(doc);
    for (var li = 0; li < docLines.Length; li++)
    {
        var line = docLines[li];
        // 路径必须带可选 .ai/ 前缀整体匹配：裸匹配 scripts/ 会把 .ai/scripts/x.sh 截成
        // scripts/x.sh，对主仓根做存在性判定必然失败（2026-10-18 实测）。
        foreach (Match m in Regex.Matches(line, @"(?:\.ai/)?scripts/[A-Za-z0-9._-]+\.(?:cs|sh|py|mjs)"))
        {
            if (File.Exists(m.Value)) continue;
            // 历史性提及豁免：行内明确标注已迁移/原形态/曾用/历史/退役/作废/旧版/淘汰/已删除
            if (Regex.IsMatch(line, @"已迁移|原形态|曾用|历史|退役|作废|旧版|淘汰|已删除")) continue;
            // 示例文件豁免：文档中的虚构示例名（example.*）不指向真实文件，属排版示范
            if (Regex.IsMatch(m.Value, @"/example\.[a-z]+$")) continue;
            v27Missing.Add($"{doc}:{li + 1} -> {m.Value}");
        }
        foreach (Match m in Regex.Matches(line, @"（V1[–-]V([0-9]+)[，）]"))
        {
            if (v27Max.Length > 0 && m.Groups[1].Value != v27Max)
                v27CountBad.Add($"{doc}:{li + 1} V1-V{m.Groups[1].Value}≠V1-V{v27Max}");
        }
        foreach (Match m in Regex.Matches(line, @"pre-commit[^\n]{0,16}?([一二三四五六七八九十]+)段|提交前([一二三四五六七八九十]+)段"))
        {
            var cn = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var n = CnNum(cn);
            if (n != v26Actual) v27CountBad.Add($"{doc}:{li + 1} {cn}段≠{v26Actual}段");
        }
    }
}
if (v27Missing.Count == 0 && v27CountBad.Count == 0)
    Pass(27, $"活文档引用与计数完整性（{v27Seen.Count} 份活文档：脚本引用全部存在，V1-V{v27Max} / pre-commit {v26Actual} 段声明一致）");
else
    Fail(27, "活文档引用失实或计数声明漂移（固定名单之外的写入点）",
        string.Join("；", v27Missing.Concat(v27CountBad)));

Console.WriteLine();
Console.WriteLine($"通过：{passed}  失败：{failed}  总计：{passed + failed}");
// 编号数与通过数不等是设计使然：V17 在非 fast 模式下分三段执行（gate 探针组 + V/D 面
// 探针组 + public 面探针组），各记一次通过——编号最大值为 27，全量通过数为 29（public
// 模式 --probes 无 .ai 时为 2 段）。V25 按编号最大值对账，此处显式说明防误读。
Console.WriteLine("（注：V17 分段记录——gate 探针组 / V/D 面 / public 面，故总数比最大编号多 2）");
Console.WriteLine("═══════ 校验完成 ═══════");
return failed > 0 ? 1 : 0;

// ═══ V17 变异探针电池（三段式：注入/文件变异/脏树 + V17c V/D 面）═══

void V17Batteries()
{
    // 脏树守卫（2026-10-16，lessons B190）：探针还原全部走 git checkout（"回到 HEAD"而非
    // "回到电池前状态"）——带着目标文件未提交改动跑全量 verify 会被静默打回 HEAD，且 git status
    // 显示干净（与 HEAD 一致）使损害对 git 隐身（实测：一轮全量把未提交的 lessons/README/
    // AGENTS 三文件改动打没，主流程 V16 在电池前已跑过还显示 PASS）。full verify 前双仓
    // tracked 改动必须为零；未跟踪文件（?? 行）不被 checkout 还原，放行。
    var v17Dirty = new List<string>();
    foreach (var line in RunCapture("git", "status --porcelain").Split('\n'))
        if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("??")) v17Dirty.Add(line.Trim());
    // --public（CI）场景没有 .ai 仓，跳过其脏树检查（V17c 同时被跳过）
    if (Directory.Exists(".ai"))
    {
        foreach (var line in RunCapture("git", "-C .ai status --porcelain").Split('\n'))
            if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("??")) v17Dirty.Add(".ai: " + line.Trim());
    }
    if (v17Dirty.Count > 0)
    {
        Fail(17, "全量 verify 前双仓存在未提交改动——变异探针将以 git checkout 还原目标文件，先提交或 stash 再跑（B190）", string.Join(' ', v17Dirty));
        return;
    }

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
        // G10/G15/G16 文档登记为警告级（2026-10-18 与 gate-check 的 CheckWarn 对齐）——探针期望
        // 输出为 WARN 而非 FAIL，其余注入项仍须 FAIL。
        var warnLevelGates = new HashSet<string>(StringComparer.Ordinal) { "G10", "G15", "G16" };
        foreach (var g in new[] { "G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8", "G9", "G10", "G11", "G12", "G13", "G14", "G15", "G16", "G17", "G18", "G19", "G20", "G21", "G22", "G23", "G24", "G25", "G28", "G29" })
        {
            var expected = warnLevelGates.Contains(g) ? "WARN" : "FAIL";
            if (!Regex.IsMatch(gateOut, $"{expected} {g}[^0-9]")) missing.Add($"{g}({expected})");
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

        // V17c 的变异目标全是 .ai 知识文件（lessons/README/perf-ledger/prompt），
        // CI 的 --public 场景没有 .ai，只能跑 gate/tech-debt 面电池。
        if (Directory.Exists(".ai")) V17cBattery();
        else Console.WriteLine("SKIP V17c: .ai 不在克隆中（--public 模式只跑 gate/tech-debt 面探针）");

        // V17d 只变异主仓文件，任何模式都跑（CI 每周 --public --probes 也执行）
        V17dPublicBattery();
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

        // V24：植入未登记假提交锚（8 位十六进制含 a-f 字母，过纯数字排除、不在任一仓、不在基线）。
        // 值只存在于本源码、不进知识文件——v7.36 旧值与基线表 2 登记互斥（探针描述逐字进
        // CHANGELOG → 植入值被基线豁免 → 探针退化为无声 no-op），full verify 在基线登记后
        // 从未重跑故横跨两个会话无人发现，v7.37 全量跑当场抓出（lessons B189）。
        MutateFile(".ai/perf-ledger.md", t => t + "\n\nzzv24probe: deadbeef\n");
        if (!RunFastVerify().Contains("FAIL V24")) v17cFail.Add("V24");
        GitInAi("checkout -- perf-ledger.md");

        // V25：README verify 计数写入点漂移（头段 25→24，模拟"只改一处"复发——B103 族 2026-10-16 实例形态）
        MutateFile(".ai/README.md", t => ReplaceFirst(t, @"([0-9]+) 项（V1–V", "24 项（V1–V"));
        if (!RunFastVerify().Contains("FAIL V25")) v17cFail.Add("V25");
        GitInAi("checkout -- README.md");

        // V26：钩子段数漂移（追加一条假段头，模拟"加了段忘改声明"——B103 族 2026-10-17 实例形态）
        MutateFile(".githooks/pre-commit", t => t + "\necho \"── pre-commit: zzprobe ──\"\n", touch: touched);
        if (!RunFastVerify().Contains("FAIL V26")) v17cFail.Add("V26");
        Git("checkout -- .githooks/pre-commit");

        // V27：活文档引用失实（植入指向不存在脚本的活文档——固定名单外写入点的判别力实证）
        File.WriteAllText(".ai/gate/zzv27_probe.md", "# probe\n`scripts/zzv27_missing_script.cs`\n", new UTF8Encoding(false));
        if (!RunFastVerify().Contains("FAIL V27")) v17cFail.Add("V27");
        File.Delete(".ai/gate/zzv27_probe.md");

        // D10：植入 [Test] 临时文件 → 实测计数漂移
        File.WriteAllText("test/PalORM.Core.Tests/ZZV17cD10Probe.cs", "[Test]\n", new UTF8Encoding(false));
        if (!RunDc().Contains("FAIL D10")) v17cFail.Add("D10");
        File.Delete("test/PalORM.Core.Tests/ZZV17cD10Probe.cs");

        // D14：T 级严重度漂移（把 test/prompt.md 的 T6 从「阻断」改成「警告」）——
        // 证明 D14 对账真的能抓到文档与 test-gate 的级别不一致（第二轮审计的实弹场景）
        MutateFile(".ai/test/prompt.md", t => new Regex(@"(\| T6 \|[^\n]*\| )阻断( \|)").Replace(t, "$1警告$2", 1));
        if (!RunDc().Contains("FAIL D14")) v17cFail.Add("D14");
        GitInAi("checkout -- test/prompt.md");

        // D11：生成物工具版本漂移（动态取包版本——原 .sh 硬编码 6.3.0，升版即失效的存量假绿）
        var pkgVer = Regex.Match(File.ReadAllText("Directory.Build.props"), @"<Version>([^<]+)").Groups[1].Value.Trim();
        MutateFile("src/PalORM.SourceGen/GeneratedCodeMetadata.cs", t => t.Replace($"\"{pkgVer}\"", "\"9.9.9\""), touch: touched);
        if (!RunDc().Contains("FAIL D11")) v17cFail.Add("D11");
        Git("checkout -- src/PalORM.SourceGen/GeneratedCodeMetadata.cs");

        var vdcTotal = Regex.Matches(File.ReadAllText("scripts/verify-ai-system.cs"), @"v17cFail\.Add\(").Count;
        if (v17cFail.Count == 0) Pass(17, $"V/D 面防线变异探针 {vdcTotal}/{vdcTotal} 如期 FAIL（V10/V12/V15/V16/V18/V19/V20/V22/V24/V25/V26/V27 + D10/D11/D14；T 面定性=提示词规则非机械防线）");
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

// ═══ V17d：public 面变异探针电池（2026-10-18 第三轮审计）═══
// 第二轮审计发现检查项自身的负向测试覆盖不均：D4/D12/D13、全部 T 项、tech-debt #15
// 都没有探针——检查静默失效时无人发现（与 E5/V17 立项同因）。本电池只变异主仓文件，
// 不依赖 .ai，故可在 CI 的 `--public --probes` 模式按周执行；与 V17c 的分工是
// 「V17c 变异 .ai 知识文件，V17d 变异主仓文件」。
void V17dPublicBattery()
{
    var silent = new List<string>();
    var touched = new List<string>();
    string RunDc() => RunCapture("dotnet", "run --file scripts/doc-consistency-check.cs");
    string RunTg() => RunCapture("dotnet", "run --file scripts/test-gate.cs");
    string RunTd() => RunCapture("dotnet", "run --file scripts/tech-debt-scan.cs");
    try
    {
        // D12：文档表格声明一个源码不存在的调用式 API
        MutateFile("docs/API参考.md", t => t + "\n| `.ZzV17dMissingApi(` | 探针行 |\n", touch: touched);
        if (!RunDc().Contains("FAIL D12")) silent.Add("D12");
        Git("checkout -- docs/API参考.md");

        // D13：把 G1 的级别从阻断改成警告（文档与 gate-check 判定不一致）
        MutateFile("docs/编码规范.md",
            t => new Regex(@"(\| G1 \|[^\n]*\| )✅( \|)").Replace(t, "$1⚠️ 警告级$2", 1), touch: touched);
        if (!RunDc().Contains("FAIL D13")) silent.Add("D13");
        Git("checkout -- docs/编码规范.md");

        // tech-debt #15：索引里抹掉一个真实脚本名（全部出现处替换，防残留同名通过）
        MutateFile("scripts/README.md", t => t.Replace("stub-check.cs", "stub-chek.cs", StringComparison.Ordinal), touch: touched);
        if (!RunTd().Contains("FAIL  15.")) silent.Add("tech-debt#15");
        Git("checkout -- scripts/README.md");

        // T4：多行体例的测试方法名不含下划线
        var t4 = "test/PalORM.Core.Tests/ZZV17dT4Probe.cs";
        File.WriteAllText(t4,
            "using TUnit.Core;\npublic sealed class ZZV17dT4Probe\n{\n    [Test]\n    public async Task BadName()\n    {\n        await Assert.That(1).IsEqualTo(1);\n    }\n}\n",
            new UTF8Encoding(false));
        if (!RunTg().Contains("WARN  T4")) silent.Add("T4");
        File.Delete(t4);

        // T6：外部库 DDL 测试体内无 finally
        var t6 = "test/PalORM.Integration.Tests/ZZV17dT6Probe.cs";
        File.WriteAllText(t6,
            "using PalORM.MySql;\npublic sealed class ZZV17dT6Probe\n{\n    [Test]\n    public async Task Probe_Cleanup()\n    {\n        await using var db = await DataSession<MySqlProvider>.CreateAsync(new DbOptions());\n        await db.ExecuteAsync($\"DROP TABLE IF EXISTS zz_v17d\");\n    }\n}\n",
            new UTF8Encoding(false));
        if (!RunTg().Contains("FAIL  T6")) silent.Add("T6");
        File.Delete(t6);

        // T9：同一概念混用两个 BenchmarkCategory 标签
        var t9 = "bench/PalORM.Benchmarks/ZZV17dT9Probe.cs";
        File.WriteAllText(t9,
            "internal static class ZZV17dT9Probe\n{\n    [BenchmarkCategory(\"Bulk\")]\n    internal static void A() { }\n\n    [BenchmarkCategory(\"BulkInsert\")]\n    internal static void B() { }\n}\n",
            new UTF8Encoding(false));
        if (!RunTg().Contains("WARN  T9")) silent.Add("T9");
        File.Delete(t9);

        // T11：声明口径地板被抬到不可能通过的数值
        MutateFile("bench/baselines/test-counts.json",
            t => Regex.Replace(t, @"""sourceMarkers"": \d+", "\"sourceMarkers\": 99999"), touch: touched);
        if (!RunTg().Contains("FAIL  T11")) silent.Add("T11");
        Git("checkout -- bench/baselines/test-counts.json");

        if (silent.Count == 0) Pass(17, "public 面变异探针 7/7 如期报红（D12/D13/tech-debt#15/T4/T6/T9/T11；CI 每周 --public --probes 执行）");
        else Fail(17, "public 面探针有沉默项（变异主仓真源后检查仍不报红 = 无声 no-op）", string.Join(' ', silent));
    }
    finally
    {
        foreach (var f in touched) Git($"checkout -- {f}");
        foreach (var f in new[]
        {
            "test/PalORM.Core.Tests/ZZV17dT4Probe.cs",
            "test/PalORM.Integration.Tests/ZZV17dT6Probe.cs",
            "bench/PalORM.Benchmarks/ZZV17dT9Probe.cs",
        })
        {
            if (File.Exists(f)) File.Delete(f);
        }
    }
}

// ── 辅助 ──

// 以下五项同时被全量流程与 --public 子集调用：抽成函数避免两条路径逻辑漂移
void CheckV2()
{
    var missing = new List<string>();
    foreach (var f in new[]
    {
        "docs/编码规范.md", "docs/踩坑目录.md", "docs/API参考.md", "docs/架构设计.md",
        "scripts/review-snapshot.cs", "scripts/verify-action-items.cs", "scripts/gate-check.cs",
        "scripts/refine-scan.cs", "scripts/stub-check.cs", "scripts/assertion-strength-check.cs",
    })
    {
        if (!File.Exists(f)) missing.Add(f);
    }
    if (missing.Count == 0) Pass(2, "提示词引用的文档与脚本全部存在");
    else Fail(2, "被引用文件缺失", string.Join(' ', missing));
}

void CheckV6()
{
    var apiLines = File.Exists("docs/API参考.md") ? File.ReadLines("docs/API参考.md").Count() : 0;
    if (apiLines > 50) Pass(6, $"API 参考完整（{apiLines} 行）");
    else Fail(6, "API 参考异常", $"docs/API参考.md 行数 {apiLines}（预期 >50）");
}

void CheckV10()
{
    var snapCount = Directory.Exists("test/PalORM.SourceGen.Tests/Snapshots")
        ? Directory.GetFiles("test/PalORM.SourceGen.Tests/Snapshots", "*.snap").Length : 0;
    if (File.Exists("test/PalORM.SourceGen.Tests/SnapshotTests.cs")
        && File.Exists("test/PalORM.SourceGen.Tests/DialectSymmetryTests.cs") && snapCount >= 10)
        Pass(10, $"机械防线齐全（快照基线 {snapCount} 份 + 对称性测试 + 断言强度门禁）");
    else Fail(10, "机械防线缺失", $"SnapshotTests/DialectSymmetryTests/Snapshots（{snapCount} 份，需 ≥10）");
}

void CheckV12()
{
    var analyzerCount = Regex.Matches(File.ReadAllText("src/PalORM.SourceGen/PalORMAnalyzer.cs"),
        @"public static readonly DiagnosticDescriptor", RegexOptions.Multiline).Count;
    var declM = Regex.Match(File.ReadAllText("docs/API参考.md"),
        @"^### 编译时验证 — ([0-9]+) 条 PALORM 诊断（([0-9]+) 条分析器 \+ ([0-9]+) 条生成器", RegexOptions.Multiline);
    var declTotal = declM.Groups[1].Value; var declAnalyzer = declM.Groups[2].Value; var declGen = declM.Groups[3].Value;
    var fail = "";
    if (analyzerCount < 30) fail += $" 实测{analyzerCount}<30";
    if (!declM.Success) fail += " API参考标题不可解析";
    else
    {
        if (declAnalyzer.Length > 0 && declAnalyzer != analyzerCount.ToString()) fail += $" API参考声明分析器{declAnalyzer}≠实测{analyzerCount}";
        if (declTotal.Length > 0 && declGen.Length > 0 && declTotal != (int.Parse(declAnalyzer) + int.Parse(declGen)).ToString()) fail += $" 声明总数{declTotal}≠{declAnalyzer}+{declGen}";
    }
    if (fail.Length == 0) Pass(12, $"PALORM 诊断实测与文档对账一致（分析器 {analyzerCount}；总数 {declTotal} = {declAnalyzer} 分析器 + {declGen} 生成器）");
    else Fail(12, "PALORM 诊断计数漂移（Analyzer 实测 vs API参考.md 声明）", fail.Trim());
}

void CheckV20()
{
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
}

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
    return RunCaptureWithCode(fileName, arguments).Output;
}

static (int Code, string Output) RunCaptureWithCode(string fileName, string arguments)
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
