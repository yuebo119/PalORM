// assertion-strength-check.cs（自 .ai/scripts/assertion-strength-check.sh 迁移，脚本全面 C# 化轮）
// 断言强度门禁：机械检测测试套件中的恒真/弱断言模式。
// 背景：Stryker.NET 不支持 TUnit 的 MTP（stryker-net#3094）；本脚本覆盖其最高价值子集。
// 迁移修正：原 Python 解释器依赖（python3 缺失假绿/死亡事故根源）随 C# 化退役。
// 契约：0=通过；1=弱断言超基线；用法 `-- --max-weak N` 可临时调阈值（基线只许下调）。
// 2026-10-18 审计修复：基线原为硬编码 17 而实测仅 3——13 个弱断言余量等于棘轮空转（新增
// 13 处弱断言照样全绿）。基线外置到 bench/baselines/assertion-baseline.json 并有"陈旧度"
// 检查：实测低于基线 30% 以上时报告 STALE 提示收紧（防"只降不增"规则被遗忘成永久余量）。
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();

var baselineFile = "bench/baselines/assertion-baseline.json";
var maxWeak = 17; // 兜底值：基线文件缺失时仍按 17 执行（fail-safe，不放行）
if (File.Exists(baselineFile))
{
    var node = JsonNode.Parse(File.ReadAllText(baselineFile));
    maxWeak = node?["maxWeak"]?.GetValue<int>() ?? maxWeak;
}
if (args.Length == 2 && args[0] == "--max-weak") maxWeak = int.Parse(args[1]);
// --ci：CI 环境语义（与本地一致——棘轮不因环境放宽；参数位保留供未来显式化）
_ = args.Length > 0 && args[0] == "--ci";

Console.WriteLine("═══════ 断言强度扫描 ═══════");

var behavioral = AssertionStrengthPatterns.Behavioral;
var assertRe = AssertionStrengthPatterns.Assert;
var testAttrRe = AssertionStrengthPatterns.TestAttr;
// 方法签名头只匹配到 '(' 为止，参数表交给 FindCloseParen 按括号配平扫描。
// 旧写法 `\([^)]*\)` 在含嵌套括号的参数上（如
// Func<string, CancellationToken, ValueTask<(DialectScope Scope, IAsyncDisposable Owner)>>）
// 会在第一个 ')' 处提前收口，`RunOneAsync` 这类 helper 因此漏登记，委托它的测试被误判零断言
// （2026-11-04 CI 实测：PostgreSql/MySql_Contract_Holds 被误报）。
var methodHeadRe = AssertionStrengthPatterns.MethodHead;
var notnullRe = AssertionStrengthPatterns.NotNull;

var weakNotnull = new List<string>();
var zeroAssert = new List<string>();

// --self-check（2026-11-04，GATE-1）：合成好/坏测试文本自证扫描判据——
// 只含 IsNotNull 的坏文本必须被判出（弱断言或零断言），含行为断言的好文本必须干净。
if (args.Length == 1 && args[0] == "--self-check")
{
    return SelfCheck();
}

int SelfCheck()
{
    var badWeak = new List<string>();
    var badZero = new List<string>();
    const string badText = @"
public sealed class ZzSelfCheckProbe
{
    [Test]
    public async Task Bad_OnlyNotNull()
    {
        var x = Make();
        await Assert.That(x).IsNotNull();
    }
}";
    ScanFile("zz_selfcheck_bad.cs", badText, badWeak, badZero);
    bool badCaught = badWeak.Count + badZero.Count > 0;

    var goodWeak = new List<string>();
    var goodZero = new List<string>();
    const string goodText = @"
public sealed class ZzSelfCheckProbe
{
    [Test]
    public async Task Good_Behavioral()
    {
        var x = Make();
        await Assert.That(x).IsEqualTo(1);
    }
}";
    ScanFile("zz_selfcheck_good.cs", goodText, goodWeak, goodZero);
    bool goodClean = goodWeak.Count + goodZero.Count == 0;

    Console.WriteLine($"  {(badCaught ? "PASS" : "FAIL")} 坏文本（仅 IsNotNull）被判出");
    Console.WriteLine($"  {(goodClean ? "PASS" : "FAIL")} 好文本（行为断言）未误报");
    if (badCaught && goodClean)
    {
        Console.WriteLine("assertion-strength-check self-check: PASS（扫描判据可失败、不误报）");
        return 0;
    }
    Console.Error.WriteLine("::error::assertion-strength-check self-check: FAIL");
    return 1;
}

void ScanFile(string rel, string text, List<string> weakNotnull, List<string> zeroAssert)
{
    // 模式 1：IsNotNull 弱断言——仅当所在 [Test] 方法体内无任何行为断言（前置判空豁免 ITM-868）
    var notnullLines = new HashSet<int>();
    foreach (Match m in notnullRe.Matches(text))
    {
        notnullLines.Add(CountNewlines(text, m.Index));
    }
    if (notnullLines.Count > 0)
    {
        foreach (var (_, attrIndex, bodyIndex) in EnumerateTestMethods(text, testAttrRe, methodHeadRe))
        {
            var end = BlockEnd(text, bodyIndex);
            var block = text[attrIndex..(end + 1)];
            if (behavioral.IsMatch(block)) continue; // 方法体内有行为断言：IsNotNull 是前置判空
            var startLine = CountNewlines(text, attrIndex);
            var endLine = CountNewlines(text, end);
            foreach (var ln in notnullLines.Where(l => startLine <= l && l <= endLine).OrderBy(l => l))
            {
                weakNotnull.Add($"{rel}:{ln + 1}:        {(ln < text.Split('\n').Length ? text.Split('\n')[ln].Trim() : "")}");
            }
        }
    }

    // 模式 2：测试方法零 Assert（含 helper 断言跟随豁免——SavepointRoundTripAsync 形态）
    var assertingHelpers = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (name, headIndex, bodyIndex) in EnumerateMethods(text, methodHeadRe))
    {
        var end = BlockEnd(text, bodyIndex);
        if (assertRe.IsMatch(text[headIndex..(end + 1)])) assertingHelpers.Add(name);
    }
    foreach (var (name, attrIndex, bodyIndex) in EnumerateTestMethods(text, testAttrRe, methodHeadRe))
    {
        var end = BlockEnd(text, bodyIndex);
        var block = text[attrIndex..(end + 1)];
        if (assertRe.IsMatch(block)) continue;
        // helper 跟随：调用了本文件内含断言的方法即视为有断言（LINQ Any，方法名逐个转义后匹配调用点）
        if (assertingHelpers.Any(h => Regex.IsMatch(block, $@"\b{Regex.Escape(h)}\s*\("))) continue;
        zeroAssert.Add($"{rel}:{name}");
    }
}

foreach (var path in Directory.EnumerateFiles("test", "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
{
    var rel = path.Replace('\\', '/');
    if (rel.Contains("obj/") || rel.Contains("bin/")) continue;
    ScanFile(rel, string.Join("\n", File.ReadAllLines(path)), weakNotnull, zeroAssert);
}

// [Test] 与其后第一个方法声明配对；中间出现成员边界（'{' / '}' / ';'）即视为不配对。
// 返回 (方法名, 属性起始下标, 方法体 '{' 下标)。
static IEnumerable<(string Name, int AttrIndex, int BodyIndex)> EnumerateTestMethods(string text, Regex testAttr, Regex headRe)
{
    var heads = EnumerateMethods(text, headRe).ToList();
    foreach (Match attr in testAttr.Matches(text))
    {
        int attrEnd = attr.Index + attr.Length;
        foreach (var (name, headIndex, bodyIndex) in heads)
        {
            if (headIndex < attrEnd) continue;
            var between = text[attrEnd..headIndex];
            if (between.Contains('{') || between.Contains('}') || between.Contains(';')) break;
            yield return (name, attr.Index, bodyIndex);
            break;
        }
    }
}

// 全部方法声明 → (方法名, 签名起始下标, 方法体 '{' 下标)。参数表按括号配平扫描：
// 正则 `\([^)]*\)` 在嵌套括号参数上会提前收口（见 methodHeadRe 注释）。
// 表达式体（=>）与无体声明（抽象/接口）跳过——本门禁只审块体方法。
static IEnumerable<(string Name, int HeadIndex, int BodyIndex)> EnumerateMethods(string text, Regex headRe)
{
    foreach (Match m in headRe.Matches(text))
    {
        int open = m.Index + m.Length;              // 签名头以 (?=\() 收尾，紧跟 '('
        int close = FindCloseParen(text, open);
        if (close < 0) continue;
        int body = FindBodyBrace(text, close + 1);
        if (body < 0) continue;
        yield return (m.Groups[1].Value, m.Index, body);
    }
}

// 从 '(' 起按括号配平找配对的 ')'。
static int FindCloseParen(string text, int openIndex)
{
    var depth = 0;
    for (var i = openIndex; i < text.Length; i++)
    {
        var c = text[i];
        if (c == '(') depth++;
        else if (c == ')')
        {
            depth--;
            if (depth == 0) return i;
        }
        else if (c == ';' || c == '{') return -1;   // 未配平即越界到别的成员
    }
    return -1;
}

// 参数表之后找方法体 '{'；先遇到 ';'（声明）或 '=>'（表达式体）返回 -1。
// where 约束里的泛型尖括号与括号不含花括号，直接线性扫描即可。
static int FindBodyBrace(string text, int from)
{
    for (var i = from; i < text.Length; i++)
    {
        var c = text[i];
        if (c == '{') return i;
        if (c == ';') return -1;
        if (c == '=' && i + 1 < text.Length && text[i + 1] == '>') return -1;
    }
    return -1;
}

if (weakNotnull.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"⚠ IsNotNull 弱断言 {weakNotnull.Count} 处（链式 builder 上恒真——改行为断言）：");
    foreach (var l in weakNotnull.Take(20)) Console.WriteLine(l);
}
if (zeroAssert.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"⚠ 零断言测试方法 {zeroAssert.Count} 个：");
    foreach (var l in zeroAssert.Take(20)) Console.WriteLine(l);
}

var total = weakNotnull.Count + zeroAssert.Count;
Console.WriteLine();
Console.WriteLine($"弱断言合计：{total}（基线上限 {maxWeak}）");
if (maxWeak > 0 && total <= maxWeak * 0.7)
{
    Console.WriteLine($"STALE 实测 {total} 远低于基线 {maxWeak}（余量 >30%）——棘轮有空转风险，请下调 {baselineFile} 的 maxWeak（只许下调）");
}
Console.WriteLine("═══════ 扫描完成 ═══════");

if (total > maxWeak)
{
    Console.WriteLine("FAIL：弱断言超出基线——新增测试必须使用行为断言；基线只许下调（改本脚本 maxWeak 默认值）");
    return 1;
}
return 0;

static int CountNewlines(string text, int upto)
{
    return text.AsSpan(0, upto).Count('\n');
}

static int BlockEnd(string text, int openBrace)
{
    // 字符串/字符字面量与注释感知的括号匹配（2026-10-18 审计修复）：原实现裸数括号，
    // 测试体内 `'a}b'`、`"{"` 之类字面量会让 depth 提前归零——整块被截断后 Assert 不在
    // 视野内，产生"零断言"假阳性（FormattableLiteralBraceTests 实测）。
    var depth = 0;
    for (var i = openBrace; i < text.Length; i++)
    {
        var c = text[i];
        if (c == '/' && i + 1 < text.Length)
        {
            if (text[i + 1] == '/')
            {
                var nl = text.IndexOf('\n', i);
                if (nl < 0) return depth == 0 ? i : text.Length - 1;
                i = nl;
                continue;
            }
            if (text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) return text.Length - 1;
                i = close + 1;
                continue;
            }
        }
        if (c == '"')
        {
            // C# 原始字符串字面量 """..."""（含 $$""" 插值变体）：整段跳过——其中花括号是
            // 文本而非代码块（GeneratorPhase2Tests 的内联源码常量实测）。
            var quoteCount = 0;
            while (i + quoteCount < text.Length && text[i + quoteCount] == '"') quoteCount++;
            if (quoteCount >= 3)
            {
                var closing = new string('"', quoteCount);
                var close = text.IndexOf(closing, i + quoteCount, StringComparison.Ordinal);
                if (close < 0) return text.Length - 1;
                i = close + quoteCount - 1;
                continue;
            }
            // 逐字字符串 @"..." 的双引号以 "" 转义；普通字符串以 \" 转义
            var verbatim = i > 0 && text[i - 1] == '@';
            for (i++; i < text.Length; i++)
            {
                if (!verbatim && text[i] == '\\') { i++; continue; }
                if (text[i] == '"')
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                    break;
                }
            }
            continue;
        }
        if (c == '\'')
        {
            for (i++; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '\'') break;
            }
            continue;
        }
        if (c == '{') depth++;
        else if (c == '}')
        {
            depth--;
            if (depth == 0) return i;
        }
    }
    return text.Length - 1;
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

/// <summary>断言强度扫描的判据正则（单一声明点：主扫描与 --self-check 共用）。</summary>
internal static class AssertionStrengthPatterns
{
    internal static readonly Regex Behavioral = new Regex(
        @"\.(IsEqualTo|IsGreaterThan|IsGreaterThanOrEqualTo|IsLessThan|IsLessThanOrEqualTo|IsNotEmpty|IsTypeOf|Contains|StartsWith|EndsWith|Matches|IsEmpty|IsFalse|IsTrue|IsNull)\(",
        RegexOptions.Compiled);
    internal static readonly Regex Assert = new Regex(
        @"\bAssert\w*[.(]|\bThrows(Async)?\s*[<(]|\.Verify\(", RegexOptions.Compiled);
    internal static readonly Regex TestAttr = new Regex(@"\[Test\]", RegexOptions.Compiled);
    internal static readonly Regex MethodHead = new Regex(
        @"(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)(?:<[^>]*>)?\s*(?=\()", RegexOptions.Compiled);
    internal static readonly Regex NotNull = new Regex(@"\.IsNotNull\(\)", RegexOptions.Compiled);
}
