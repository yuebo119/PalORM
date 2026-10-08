// assertion-strength-check.cs（自 .ai/scripts/assertion-strength-check.sh 迁移，脚本全面 C# 化轮）
// 断言强度门禁：机械检测测试套件中的恒真/弱断言模式。
// 背景：Stryker.NET 不支持 TUnit 的 MTP（stryker-net#3094）；本脚本覆盖其最高价值子集。
// 迁移修正：原 Python 解释器依赖（python3 缺失假绿/死亡事故根源）随 C# 化退役。
// 契约：0=通过；1=弱断言超基线；用法 `-- --max-weak N` 可临时调阈值（基线只许下调）。
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();

var maxWeak = 17; // 基线上限（2026-08-15 按真实现状重定；只许下调）
if (args.Length == 2 && args[0] == "--max-weak") maxWeak = int.Parse(args[1]);

Console.WriteLine("═══════ 断言强度扫描 ═══════");

var behavioral = new Regex(@"\.(IsEqualTo|IsGreaterThan|IsGreaterThanOrEqualTo|IsLessThan|IsLessThanOrEqualTo|IsNotEmpty|IsTypeOf|Contains|StartsWith|EndsWith|Matches|IsEmpty|IsFalse|IsTrue|IsNull)\(", RegexOptions.Compiled);
var assertRe = new Regex(@"\bAssert\w*[.(]|\bThrows(Async)?\s*[<(]|\.Verify\(", RegexOptions.Compiled);
var testMethodRe = new Regex(@"\[Test\][^{]*?(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)\s*\([^)]*\)\s*(\{)", RegexOptions.Singleline | RegexOptions.Compiled);
var helperMethodRe = new Regex(@"(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)(?:<[^>]*>)?\s*\([^)]*\)\s*(?:where[^{]*)?\{", RegexOptions.Singleline | RegexOptions.Compiled);
var notnullRe = new Regex(@"\.IsNotNull\(\)", RegexOptions.Compiled);

var weakNotnull = new List<string>();
var zeroAssert = new List<string>();

foreach (var path in Directory.EnumerateFiles("test", "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
{
    var rel = path.Replace('\\', '/');
    if (rel.Contains("obj/") || rel.Contains("bin/")) continue;
    var lines = File.ReadAllLines(path);
    var text = string.Join("\n", lines);

    // 模式 1：IsNotNull 弱断言——仅当所在 [Test] 方法体内无任何行为断言（前置判空豁免 ITM-868）
    var notnullLines = new HashSet<int>();
    foreach (Match m in notnullRe.Matches(text))
    {
        notnullLines.Add(CountNewlines(text, m.Index));
    }
    if (notnullLines.Count > 0)
    {
        foreach (Match m in testMethodRe.Matches(text))
        {
            var end = BlockEnd(text, m.Groups[2].Index);
            var block = text[m.Index..(end + 1)];
            if (behavioral.IsMatch(block)) continue; // 方法体内有行为断言：IsNotNull 是前置判空
            var startLine = CountNewlines(text, m.Index);
            var endLine = CountNewlines(text, end);
            foreach (var ln in notnullLines.Where(l => startLine <= l && l <= endLine).OrderBy(l => l))
            {
                weakNotnull.Add($"{rel}:{ln + 1}:        {(ln < lines.Length ? lines[ln].Trim() : "")}");
            }
        }
    }

    // 模式 2：测试方法零 Assert（含 helper 断言跟随豁免——SavepointRoundTripAsync 形态）
    var assertingHelpers = new HashSet<string>(StringComparer.Ordinal);
    foreach (Match m in helperMethodRe.Matches(text))
    {
        var end = BlockEnd(text, m.Index + m.Length - 1);
        if (assertRe.IsMatch(text[m.Index..(end + 1)])) assertingHelpers.Add(m.Groups[1].Value);
    }
    foreach (Match m in testMethodRe.Matches(text))
    {
        var end = BlockEnd(text, m.Groups[2].Index);
        var block = text[m.Index..(end + 1)];
        if (assertRe.IsMatch(block)) continue;
        // helper 跟随：调用了本文件内含断言的方法即视为有断言（LINQ Any，方法名逐个转义后匹配调用点）
        if (assertingHelpers.Any(h => Regex.IsMatch(block, $@"\b{Regex.Escape(h)}\s*\("))) continue;
        zeroAssert.Add($"{rel}:{m.Groups[1].Value}");
    }
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
    var depth = 0;
    for (var i = openBrace; i < text.Length; i++)
    {
        if (text[i] == '{') depth++;
        else if (text[i] == '}')
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
