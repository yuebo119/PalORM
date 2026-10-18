// sibling-map.cs（自 .ai/scripts/sibling-map.sh 迁移，脚本全面 C# 化轮）
// 三方言姊妹族枚举器（sibling-map 轴 A 机械生成，跨仓移植 DDD Phase 1a）：
// 「修一个漏姊妹」是复发账本最大族（E3/E4 + r24 四组同型面复发），根因是修复代理
// 的工作单位是文件、族信息不在其上下文——本枚举把族结构在修复任务分解处强制暴露。
// 信息工具非阻断门禁。轴 B 种子表真源 .ai/review/sibling-map.md。
// 轴 C（2026-10-18 增）：Core 共享文件的方言分支族——轴 A 只看三个 Provider 主文件，
// 而真正的方言分叉大量写在 Core 的共享实现里（DataSession_Bulk.cs 等：TProvider.Dialect
// 判据把 PG/MySQL/SQLite 分流到不同路径）。修一个分支漏其余分支与轴 A 同族，故按
// 「类成员 × 方言出现次数」枚举，并对只覆盖 2/3 方言的成员打标（核对缺项是有意不对称
// 还是遗漏）。
// 契约：0=正常（始终）；stdout 与 .sh 版对拍兼容；修复轮/评审轮前现算，禁用过期输出。
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var root = FindRepoRoot();
var providers = new (string Label, string Path)[]
{
    ("Sqlite", "src/PalORM.Sqlite/SqliteProvider.cs"),
    ("PostgreSql", "src/PalORM.PostgreSql/PostgreSqlProvider.cs"),
    ("MySql", "src/PalORM.MySql/MySqlProvider.cs"),
};
foreach (var (_, path) in providers)
{
    if (!File.Exists(Path.Combine(root, path)))
    {
        Console.Error.WriteLine($"错误: {path} 不存在（Provider 结构已变？现算前先核对路径）");
        return 1;
    }
}

// 方法名提取：visibility 修饰行取最后一个 "标识符("（对 Provider 单文件类够用，与 .sh 两段 grep 等价）
var methods = new Dictionary<string, HashSet<string>>();
var extractRegex = new Regex(@"(public|private|internal|protected)[^{(]*\b([A-Za-z]+)\(", RegexOptions.Compiled);
foreach (var (label, rel) in providers)
{
    var set = new HashSet<string>(StringComparer.Ordinal);
    foreach (var line in File.ReadAllLines(Path.Combine(root, rel)))
    {
        foreach (Match m in extractRegex.Matches(line))
        {
            set.Add(m.Groups[2].Value);
        }
    }
    methods[label] = set;
}

var all = methods.Values.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);

Console.WriteLine($"═══ 轴 A：三 Provider 同名方法族（sibling-map 现算 {DateTime.Now:yyyy-MM-dd HH:mm}）═══");
Console.WriteLine();
Console.WriteLine("[3/3 全族]（改一处必查三处——历史漏网高发区）:");
Console.WriteLine("  " + string.Join(' ', all.Where(m => providers.All(p => methods[p.Label].Contains(m))).OrderBy(m => m, StringComparer.Ordinal)));
Console.WriteLine();
Console.WriteLine("[2/3 双族] Pg+MySql:");
Console.WriteLine("  " + Join(methods["PostgreSql"].Intersect(methods["MySql"]).Except(methods["Sqlite"])));
Console.WriteLine("[2/3 双族] Sqlite+Pg:");
Console.WriteLine("  " + Join(methods["Sqlite"].Intersect(methods["PostgreSql"]).Except(methods["MySql"])));
Console.WriteLine("[2/3 双族] Sqlite+MySql:");
Console.WriteLine("  " + Join(methods["Sqlite"].Intersect(methods["MySql"]).Except(methods["PostgreSql"])));
Console.WriteLine();
Console.WriteLine("[1/3 独有] Sqlite: " + Join(methods["Sqlite"].Except(methods["PostgreSql"]).Except(methods["MySql"])));
Console.WriteLine($"[1/3 独有] Pg: {methods["PostgreSql"].Except(methods["Sqlite"]).Except(methods["MySql"]).Count()} 个（Write* 写入族 + COPY 族——独有即方言特化，改语义时对照轴 B 种子表）");
Console.WriteLine("[1/3 独有] MySql: " + Join(methods["MySql"].Except(methods["Sqlite"]).Except(methods["PostgreSql"])));
Console.WriteLine();
Console.WriteLine("═══ 轴 B：语义种子表（无共同签名但行为对称的族）═══");
Console.WriteLine("真源 .ai/review/sibling-map.md（含 Bulk 家族/同步异步对/方言 SQL 生成族/WriteXxx 对称族）");
Console.WriteLine("修复任务分解前必读；每次姊妹类复发当轮收口时补条目。");

// ─── 轴 C：Core 共享文件的方言分支族 ───
// 轴 A 的盲区：方言分叉常写在 Core 共享实现里（TProvider.Dialect 判据），不在三个
// Provider 主文件中。此轴按类成员统计三方言出现次数，并标出只覆盖 2/3 的成员。
// --file <path[,path]> 覆盖默认文件列表（夹具与跨仓复用；相对仓库根或绝对路径）。
var fileArg = ArgValue("--file");
string[] sharedCoreFiles = fileArg is null
    ? ["src/PalORM.Core/DataSession_Bulk.cs"]
    : [.. fileArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
Console.WriteLine();
Console.WriteLine("═══ 轴 C：Core 共享文件的方言分支族（修一处必核其余分支）═══");
foreach (var rel in sharedCoreFiles) ReportDialectBranches(root, rel);
return 0;

static string Join(IEnumerable<string> items)
{
    return string.Join(' ', items.OrderBy(m => m, StringComparer.Ordinal));
}

string? ArgValue(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// ── 轴 C 辅助 ──

static void ReportDialectBranches(string root, string rel)
{
    var path = Path.Combine(root, rel);
    if (!File.Exists(path))
    {
        Console.WriteLine($"  {rel} 不存在（路径已变？先核对再信任本轴输出）");
        return;
    }
    var text = File.ReadAllText(path);
    var members = ExtractMembers(text);
    var withDialect = members
        .Select(m => (m.Name, m.Line, Dialects: CountDialects(m.Body)))
        .Where(x => x.Dialects.Count > 0)
        .OrderBy(x => x.Line)
        .ToList();
    Console.WriteLine($"{Path.GetFileName(rel)}：{members.Count} 个类成员，{withDialect.Count} 个带方言判据");
    foreach (var (name, line, dialects) in withDialect)
    {
        var summary = string.Join(' ', dialects.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}×{kv.Value}"));
        var mark = dialects.Count == 2 ? "  ⚠ 双方言（核对第三方言）" : "";
        Console.WriteLine($"  {name} (L{line}): {summary}{mark}");
    }
    var partial = withDialect.Where(x => x.Dialects.Count == 2).Select(x => x.Name).ToList();
    if (partial.Count > 0)
    {
        Console.WriteLine($"  双方言成员 {partial.Count} 个：{string.Join(' ', partial)}");
        Console.WriteLine("  核对口径：缺的第三方言是有意不对称（如 PG 专属 COPY）还是遗漏；有意的写入轴 B 种子表。");
    }
}

static Dictionary<string, int> CountDialects(string body)
{
    var result = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var (token, label) in new[]
    {
        ("SqlDialect.Sqlite", "Sqlite"),
        ("SqlDialect.PostgreSql", "Pg"),
        ("SqlDialect.MySql", "MySql"),
    })
    {
        var count = CountOccurrences(body, token);
        if (count > 0) result[label] = count;
    }
    return result;
}

static int CountOccurrences(string haystack, string needle)
{
    var count = 0;
    for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
         i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
    {
        count++;
    }
    return count;
}

// 类成员提取：只认 4 空格缩进的成员声明（该仓类成员统一 4 空格；局部函数更深，不混入）。
// 签名可能跨行且含默认参数（= 号）——不能简单按 [^;=]* 截断（实测漏掉带默认值的方法），
// 改为「修饰符到首个左括号」+ 括号闭合后再定位方法体。
static List<(string Name, int Line, string Body)> ExtractMembers(string text)
{
    var members = new List<(string, int, string)>();
    // 非贪婪：贪婪的 [^{;(]* 会跨过后续成员声明一路吃到更远的 '('，导致成员数被吞掉
    var decl = new Regex(@"^ {4}(?:public|private|internal|protected|static)\b[^{;(]*?\(", RegexOptions.Multiline);
    foreach (Match m in decl.Matches(text))
    {
        // 泛型方法签名形如 Foo<T>( / Foo<T, TKey>(——名字与 '(' 之间隔着类型参数，需跳过
        var nameMatch = Regex.Match(m.Value, @"([A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\($");
        if (!nameMatch.Success) continue;
        var openParen = m.Index + m.Length - 1;
        var closeParen = MatchParen(text, openParen);
        if (closeParen < 0) continue;
        var start = closeParen + 1;
        var brace = text.IndexOf('{', start);
        var semi = text.IndexOf(';', start);
        var arrow = text.IndexOf("=>", start, StringComparison.Ordinal);
        string body;
        if (arrow >= 0 && (brace < 0 || arrow < brace) && (semi < 0 || arrow < semi))
        {
            var endSemi = text.IndexOf(';', arrow);
            body = endSemi >= 0 ? text[arrow..endSemi] : text[arrow..];
        }
        else if (brace >= 0 && (semi < 0 || brace < semi))
        {
            var end = BlockEnd(text, brace);
            body = end > brace ? text[brace..end] : text[brace..];
        }
        else
        {
            continue;
        }
        var line = text.AsSpan(0, m.Index).Count('\n') + 1;
        members.Add((nameMatch.Groups[1].Value, line, body));
    }
    return members;
}

// 圆括号匹配（签名参数可含字符串/字符字面量）
static int MatchParen(string text, int openParen)
{
    var depth = 0;
    for (var i = openParen; i < text.Length; i++)
    {
        var c = text[i];
        if (c == '"')
        {
            for (i++; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') break;
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
        if (c == '(') depth++;
        else if (c == ')')
        {
            depth--;
            if (depth == 0) return i;
        }
    }
    return -1;
}

// 字符串/字符/注释感知的括号匹配（裸数括号会被字面量里的花括号带偏）
static int BlockEnd(string text, int openBrace)
{
    var depth = 0;
    for (var i = openBrace; i < text.Length; i++)
    {
        var c = text[i];
        if (c == '/' && i + 1 < text.Length)
        {
            if (text[i + 1] == '/')
            {
                var nl = text.IndexOf('\n', i);
                if (nl < 0) return text.Length - 1;
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
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            Environment.CurrentDirectory = dir.FullName; // 与 .sh 版 cd 仓库根对齐（相对路径消费方依赖）
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    Console.Error.WriteLine("错误: 未找到仓库根（PalORM.slnx 哨兵缺失）");
    Environment.Exit(2);
    return "";
}
