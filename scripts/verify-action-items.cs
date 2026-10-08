// verify-action-items.cs（自 .ai/scripts/verify-action-items.sh v2 迁移，脚本全面 C# 化轮）
// 验证行动项/报告账本中「文件路径引用」的真实性（防幻觉路径引用）。
// v2 语义保真：只核对可机械判真伪的对象（文件路径），标识符一律跳过；
// 提取两层 token（反引号 + 扩展名白名单）；路径解析链 原样→剥行号→.ai/ 前缀→相对账本目录→
// git ls-files 裸名/后缀兜底；中文分号是账本多路径联合列举分隔符，拆开后逐个核对。
// 契约：0=全找到；1=有缺失；2=用法/文件不存在；stdout 与 .sh 版对拍兼容。
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();

if (args.Length != 1)
{
    Console.Error.WriteLine("用法：dotnet run --file scripts/verify-action-items.cs -- <action-items-file>");
    return 2;
}
var actionFile = args[0];
if (!File.Exists(actionFile))
{
    Console.Error.WriteLine($"错误：文件不存在：{actionFile}");
    return 2;
}
var actionDir = Path.GetDirectoryName(Path.GetFullPath(actionFile))!;

Console.WriteLine("═══════ Action Items 验证（v2 路径核对）═══════");
Console.WriteLine($"文件：{actionFile}");
Console.WriteLine();

// 历史改名豁免登记（账本不可变——维护规则 4；登记时须验证新去向存在）
var renamed = new Dictionary<string, string>
{
    ["scripts/test-quality-scripts.sh"] = "scripts/test-quality-scripts.cs",
    ["scripts/test-package-contract.sh"] = "scripts/test-package-contract.cs",
};

var text = File.ReadAllText(actionFile);
var tokens = new SortedSet<string>(StringComparer.Ordinal);

// 两层提取：反引号包裹 + 扩展名白名单（新格式表格"文件"列无反引号）；中文/ASCII 分号拆分
foreach (Match m in Regex.Matches(text, @"`[^`]+`")) tokens.UnionWith(SplitPaths(m.Value[1..^1]));
foreach (Match m in Regex.Matches(text, @"[^\s|`(\u4e00-\u9fff]+[.](cs|csproj|slnx|md|sh|yml|yaml|props|targets|json|xml|config)"))
    tokens.UnionWith(SplitPaths(m.Value));

var ignoredRegex = new Regex(@"^(P[0-3]|AUD-[0-9]+|ITM(-[0-9]+)?|PASS|FAIL|WARN|SKIP|urgent|near|future|assess)$", RegexOptions.Compiled);
var extRegex = new Regex(@"\.(cs|csproj|slnx|md|sh|yml|yaml|props|targets|json|xml|config|slnf)$", RegexOptions.Compiled);
var linenoRegex = new Regex(@":[0-9][0-9,./-]*$", RegexOptions.Compiled);
// 不可机械判真伪形态：空格/通配/变量/代码表达式/markdown 残段（与 .sh case-glob 黑名单逐字符对齐）
var unverifiableChars = " $*?|=<>{}[]@#…（）".ToArray();

var found = 0; var missing = 0; var skipToken = 0; var skipExempt = 0; var skipUrl = 0;
foreach (var tok in tokens)
{
    if (tok.Length == 0) continue;
    if (ignoredRegex.IsMatch(tok)) { skipToken++; continue; }
    var exemptKey = StripLineno(tok);
    if (exemptKey.Length > 0 && renamed.TryGetValue(exemptKey, out var dest) && File.Exists(dest))
    {
        skipExempt++;
        continue;
    }
    var isPathLike = tok.EndsWith('/') || extRegex.IsMatch(tok);
    if (!isPathLike || tok.IndexOfAny(unverifiableChars) >= 0) { skipToken++; continue; }
    if (tok.Contains("://")) { skipUrl++; continue; }
    if (ResolvePath(tok) is null)
    {
        Console.WriteLine($"FAIL 文件不存在：{tok}");
        missing++;
    }
    else found++;
}

Console.WriteLine();
Console.WriteLine($"路径核对：找到 {found} / 缺失 {missing}");
Console.WriteLine($"豁免（历史改名登记）：{skipExempt} · 跳过（标识符/表达式/URL 等非路径形态）：{skipToken}（含 URL {skipUrl}）");
Console.WriteLine("═══════ 验证完成 ══════");
return missing == 0 ? 0 : 1;

static IEnumerable<string> SplitPaths(string raw)
{
    foreach (var part in raw.Split('；').SelectMany(p => p.Split(';')))
    {
        var t = part.Trim();
        if (t.Length > 0) yield return t;
    }
}

static string StripLineno(string tok)
{
    return Regex.Replace(tok, @":[0-9][0-9,./-]*$", "");
}

string? ResolvePath(string tok)
{
    var b = StripLineno(tok);
    if (b.Length == 0) return null;
    if (File.Exists(b) || Directory.Exists(b)) return b;
    if (File.Exists($".ai/{b}") || Directory.Exists($".ai/{b}")) return $".ai/{b}";
    var rel = $"{actionDir}/{b}";
    if (File.Exists(rel) || Directory.Exists(rel)) return rel;
    // 裸文件名（无 /）兜底：tracked 文件或 .ai 本地工具（.ai 不入库，git ls-files 盲区）
    if (!b.Contains('/'))
    {
        if (GitLsFiles($"*{b}").Count > 0) return $"(裸名兜底) {b}";
        if (Directory.Exists(".ai"))
        {
            foreach (var f in Directory.EnumerateFiles(".ai", b, SearchOption.AllDirectories))
            {
                if (!f.Replace('\\', '/').Contains("brain-data")) return $"(裸名兜底) {b}";
            }
        }
    }
    // 后缀递归兜底：账本常省略顶层目录前缀
    if (GitLsFiles($"**/{b}").Count > 0) return $"(后缀兜底) {b}";
    return null;
}

static List<string> GitLsFiles(string pattern)
{
    var psi = new System.Diagnostics.ProcessStartInfo("git", $"ls-files -- {pattern}")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx"))) return dir.FullName;
        dir = dir.Parent!;
    }
    Console.Error.WriteLine("错误: 未找到仓库根（PalORM.slnx 哨兵缺失）");
    Environment.Exit(2);
    return "";
}
