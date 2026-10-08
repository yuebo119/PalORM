// gate-check.cs（自 .ai/scripts/gate-check.sh 迁移，脚本全面 C# 化轮）
// PalORM 仓库门禁 G1-G33。每条规则独立执行，最终统一返回结果。
// 语义保真要点：count_matches 带 B13 注释剥离与 B178 目录形态 pathspec；G12/G24/G25 的
// perl -0777 多行正则逐条译为 .NET Regex（Singleline）；G29 awk 状态机译为逐行扫描。
// 契约：0=全过；1=有 FAIL；2=用法错。stdout 与 .sh 版对拍兼容（含 ANSI 色码）。
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();

var passed = 0; var failed = 0; var warned = 0;
const string RED = "\x1b[0;31m", GREEN = "\x1b[0;32m", YELLOW = "\x1b[0;33m", NC = "\x1b[0m";

Console.WriteLine("═══════ PalORM 门禁扫描 ═══════");
Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine("规范：docs/编码规范.md");
Console.WriteLine();

// G1：具体异常必须 sealed 或 abstract（PalORMException 基类豁免）
var g1 = GitGrepLines("""public\s+(class|record class)\s+[A-Za-z0-9_]*Exception""", ignoreCase: false, "src/**/*.cs")
    .Count(l => !Regex.IsMatch(l, @"sealed|abstract|class PalORMException"));
CheckZero("G1", "具体异常类型 sealed", g1);

CheckZero("G2", "Core 零外部 ORM 依赖", CountMatches("""using\s+(Dapper|Microsoft\.EntityFrameworkCore|NHibernate|Newtonsoft)""", "src/PalORM.Core"));
CheckZero("G3", "运行时零泛型构造", CountMatches("MakeGeneric(Type|Method)", "src/PalORM.Core", "src/PalORM.Sqlite", "src/PalORM.PostgreSql", "src/PalORM.MySql"));
CheckZero("G4", "运行时零反射发现", CountMatches("""(Type|Assembly)\.GetType|\.Get(Method|Property|Field|Constructor)\(""", "src/PalORM.Core", "src/PalORM.Sqlite", "src/PalORM.PostgreSql", "src/PalORM.MySql"));
CheckZero("G5", "运行时零 Expression.Compile", CountMatches("""Expression\.Compile|\.Compile\(\)""", "src/PalORM.Core", "src/PalORM.Sqlite", "src/PalORM.PostgreSql", "src/PalORM.MySql"));
CheckZero("G6", "运行时零 Activator.CreateInstance", CountMatches("Activator\\.CreateInstance", "src/PalORM.Core", "src/PalORM.Sqlite", "src/PalORM.PostgreSql", "src/PalORM.MySql"));
CheckZero("G7", "运行时零 dynamic", CountMatches("""(^|[^A-Za-z0-9_])dynamic([^A-Za-z0-9_]|$)|DynamicParameters""", "src/PalORM.Core", "src/PalORM.Sqlite", "src/PalORM.PostgreSql", "src/PalORM.MySql"));
CheckZero("G8", "零 string.Format 拼接 SQL", CountMatches("""string\.Format.*(SELECT|INSERT|UPDATE|DELETE)""", "src/**/*.cs"));

// G9：受跟踪文件零硬编码连接凭据（多层豁免过滤，逐层与 .sh 对齐）
var g9 = GitGrepLines("""(Password|Pwd)=[^;$"'\s]+|connectionString\s*=\s*"(Server|Host)=""", ignoreCase: true, ":!docs/**", ":!**/bin/**", ":!**/obj/**")
    .Where(l => !Regex.IsMatch(l, @"example|sample|placeholder|<password>|\$\{[^}]+\}|\.\.\.|change-me|USER.*PASS|palorm_bench", RegexOptions.IgnoreCase))
    .Where(l => !l.Contains("Password=`"))
    .Where(l => !Regex.IsMatch(l, @"^(\.ai/|scripts/gate-check\.(sh|cs)|scripts/secret-guard\.(sh|cs)|\.github/PULL_REQUEST_TEMPLATE\.md|CONTRIBUTING\.md)"))
    .Where(l => !Regex.IsMatch(l, @"localhost.*Database=test", RegexOptions.IgnoreCase))
    .Where(l => !Regex.IsMatch(l, @"Host=(primary|replica)", RegexOptions.IgnoreCase))
    .Count(l => !(l.Contains("localhost", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(l, @"(Password|Pwd)=[^;""\s]")));
CheckZero("G9", "受跟踪文件零硬编码连接凭据", g9);

CheckZero("G10", "DataSession 不得长期持有（字段/属性缓存会话）", CountMatches("""DataSession.*(_db|field|property)""", "src/**/*.cs"));
CheckZero("G11", "零 virtual 导航属性", CountMatches("""public.*virtual|virtual.*public""", "src/**/*.cs"));
CheckZero("G12", "禁止公开 static 可写状态", CountPublicStaticState());

var g13 = 0;
foreach (var (path, pattern) in new[]
{
    ("src/PalORM.PostgreSql", @"PalORM\.Sqlite|PalORM\.MySql"),
    ("src/PalORM.Sqlite", @"PalORM\.PostgreSql|PalORM\.MySql"),
    ("src/PalORM.MySql", @"PalORM\.PostgreSql|PalORM\.Sqlite"),
})
{
    g13 += CountMatches(pattern, path);
}
CheckZero("G13", "Provider 不跨引用", g13);

CheckZero("G14", "SourceGen 不引用运行时 Provider", CountMatches("""using\s+PalORM\.(Sqlite|PostgreSql|MySql|Testing)""", "src/PalORM.SourceGen"));
CheckZero("G15", "实体禁用裸 DateTime（用 DateTimeOffset）", CountMatches("""public\s+DateTime[?\s]""", "src/**/*.cs"));
CheckZero("G16", "级联删除必须显式启用（默认 NO ACTION）", CountMatches("""OnDelete.*Cascade|Cascade.*Delete""", "src/**/*.cs"));
CheckZero("G17", "禁止 async void", CountMatches("""async\s+void""", "src/**/*.cs"));
// v7.2.1 词边界匹配（B13 同族教训）：命中行须不含 /// 与 //（与 .sh 管道 grep -v 等价）
var g18 = 0;
foreach (var f in TrackedFiles("src/**/*.cs"))
{
    if (f.Contains("obj/") || f.Contains("bin/")) continue;
    foreach (var line in File.ReadLines(f))
    {
        if (Regex.IsMatch(line, @"\bTransactionScope\b") && !line.Contains("///") && !line.Contains("//")) g18++;
    }
}

CheckZero("G18", "禁止 TransactionScope", g18);

// G19：运行时项目声明 IsAotCompatible=true（dotnet msbuild -getProperty 逐项目评估）
var g19 = 0;
foreach (var project in Directory.EnumerateFiles("src", "*.csproj", SearchOption.AllDirectories)
             .Where(p => !p.Replace('\\', '/').StartsWith("src/PalORM.SourceGen/"))
             .OrderBy(p => p, StringComparer.Ordinal))
{
    var evaluated = RunCapture("dotnet", $"msbuild \"{project}\" -nologo -getProperty:IsAotCompatible").Trim();
    if (evaluated != "true")
    {
        Console.WriteLine($"IsAotCompatible 未评估为 true：{project.Replace('\\', '/')}（实际：{(evaluated.Length == 0 ? "<空>" : evaluated)}）");
        g19++;
    }
}
CheckZero("G19", "运行时项目声明 IsAotCompatible=true", g19);

CheckZero("G20", "禁止同步阻塞异步操作", CountMatches("""\.Result([^A-Za-z0-9_]|$)|\.Wait\(|GetAwaiter\(\)\.GetResult\(""", "src/**/*.cs"));
CheckZero("G21", "生成器不得输出 blanket pragma", CountMatches("""#pragma warning disable(\\n|")""", "src/PalORM.SourceGen"));
CheckZero("G22", "禁止抑制裁剪与 AOT 警告", CountMatches("""(NoWarn|pragma warning disable).*(IL2[0-9]{3}|IL3[0-9]{3})|UnconditionalSuppressMessage""", "src/**/*.cs", "**/*.csproj", "Directory.Build.props"));

var g23 = 0;
foreach (var dir in new[] { "test/PalORM.AotTest", "test/PalORM.AotTest.MySql", "test/PalORM.AotTest.Pg" })
{
    if (!Directory.Exists(dir)) continue;
    foreach (var csproj in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
    {
        foreach (var line in File.ReadLines(csproj))
        {
            if (Regex.IsMatch(line, """Compile\s+(Include|Remove)=.*\.g\.cs""")) g23++;
        }
    }
}
CheckZero("G23", "AOT 项目不得手工编译生成文件", g23);

// G24：库代码 await 必须 ConfigureAwait(false)（跨行统计，先剥 /// 与 //）
var g24 = 0;
var awaitRegex = new Regex(@"\bawait\s+(?!using\b|foreach\b|Task\.Yield\(\))", RegexOptions.Singleline);
var caRegex = new Regex(@"ConfigureAwait\(false\)");
foreach (var f in TrackedFiles("src/**/*.cs"))
{
    var content = File.ReadAllText(f);
    content = Regex.Replace(content, @"^\s*///.*$", "", RegexOptions.Multiline);
    content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
    var diff = awaitRegex.Matches(content).Count - caRegex.Matches(content).Count;
    if (diff > 0)
    {
        Console.WriteLine($"ConfigureAwait 缺失 {diff} 处：{f}");
        g24 += diff;
    }
}
CheckZero("G24", "库代码 await 必须 ConfigureAwait(false)", g24);

// G25：公共 async API 必须带 CancellationToken（跨行签名，Dispose 系/StopAsync 豁免）
var g25 = 0;
var sigRegex = new Regex(
    @"\bpublic\s+(?:static\s+)?(?:async\s+)?(?:Task|ValueTask)(?:<[^;{()]*>)?\s+([A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\(([^)]*)\)",
    RegexOptions.Singleline);
foreach (var f in TrackedFiles("src/**/*.cs"))
{
    var content = File.ReadAllText(f);
    foreach (Match m in sigRegex.Matches(content))
    {
        var name = m.Groups[1].Value;
        var prm = m.Groups[2].Value;
        if (Regex.IsMatch(name, @"^(DisposeAsync|DisposeAsyncCore|DisposePreservingAsync)$")) continue;
        if (name == "StopAsync") continue;
        if (prm.Contains("CancellationToken")) continue;
        Console.WriteLine($"CancellationToken 缺失：{f} 中的 {name}");
        g25++;
    }
}
CheckZero("G25", "公共 async API 必须带 CancellationToken", g25);

// G26：QueryBuilder 保持 struct（文件缺失按 0）
var g26 = 0;
if (File.Exists("src/PalORM.Core/QueryBuilder.cs"))
{
    var qbStruct = File.ReadLines("src/PalORM.Core/QueryBuilder.cs").Count(l => Regex.IsMatch(l, @"^public struct QueryBuilder<T>"));
    g26 = 1 - Math.Min(qbStruct, 1);
}
CheckZero("G26", "QueryBuilder 保持 struct 声明", g26);

// G27：CS1591 豁免防回退
var g27 = 0;
if (File.Exists("Directory.Build.props"))
{
    var inGroup = false;
    foreach (var line in File.ReadLines("Directory.Build.props"))
    {
        if (line.Contains("<PropertyGroup>")) inGroup = true;
        if (inGroup && line.Contains("CS1591")) g27++;
        if (line.Contains("</PropertyGroup>")) inGroup = false;
    }
}
CheckZero("G27", "src/ 全域 CS1591 强制不回退", g27);

// G28：禁止裸 (int)…TotalSeconds 截断
var g28 = 0;
foreach (var f in EnumerateSrcCs().Where(f => !f.Contains("/obj/")))
{
    foreach (var line in File.ReadLines(f))
    {
        if (Regex.IsMatch(line, """\(int\)[^;]*(CommandTimeout|_commandTimeout|_timeout)\.TotalSeconds""") && !line.Contains("ToCommandTimeoutSeconds")) g28++;
    }
}
CheckZero("G28", "禁止裸 CommandTimeout.TotalSeconds 截断（用 CommandTimeoutSeconds）", g28);

// G29：绕过 CreateCommand 工厂的执行型命令必须设 CommandTimeout（awk 状态机逐行译）
var g29 = 0;
foreach (var dir in new[] { "src/PalORM.Core", "src/PalORM.Sqlite", "src/PalORM.MySql", "src/PalORM.PostgreSql" })
{
    if (!Directory.Exists(dir)) continue;
    foreach (var f in Directory.EnumerateFiles(dir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
    {
        var lines = File.ReadAllLines(f);
        int ln = 0; var found = false; var cnt = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("conn.CreateCommand()") || line.Contains("Connection.CreateCommand()")) { ln = i; found = false; }
            if (ln > 0 && i <= ln + 20 && line.Contains("CommandTimeout")) { found = true; ln = 0; }
            if (ln > 0 && i <= ln + 20 && Regex.IsMatch(line, @"Execute(Reader|Scalar|NonQuery)Async") && !found) { cnt++; ln = 0; }
        }
        g29 += cnt;
    }
}
CheckZero("G29", "绕过 CreateCommand 工厂的执行型命令必须设 CommandTimeout（ITM-557）", g29);

// G30：PalORMAnalyzer 基类链口径统一
var g30 = 0;
if (File.Exists("src/PalORM.SourceGen/PalORMAnalyzer.cs"))
{
    g30 = File.ReadLines("src/PalORM.SourceGen/PalORMAnalyzer.cs").Count(l => l.Contains("type.GetMembers().OfType<IPropertySymbol>"));
}
CheckZero("G30", "PalORMAnalyzer 基类链口径统一（用 EnumerateMappedProperties，ITM-607）", g30);

// G31：方言感知验证（WARN 级）
Console.WriteLine();
Console.WriteLine("─── G31: 方言感知验证 ───");
var g31 = File.Exists("src/PalORM.Core/DataSession_Bulk.cs")
    ? File.ReadLines("src/PalORM.Core/DataSession_Bulk.cs").Count(l => l.Contains("Dialect == SqlDialect")) : 0;
if (g31 > 0) { Console.WriteLine($"{GREEN}PASS{NC}  G31  方言感知分支存在（{g31} 处 SqlDialect 检查）"); passed++; }
else { Console.WriteLine($"{YELLOW}WARN{NC}  G31  未检测到方言感知分支——跨方言 API 需验证行为一致"); warned++; }

// G32：NuGet.Config packageSourceMapping CI 兼容性
Console.WriteLine();
Console.WriteLine("─── G32: NuGet.Config packageSourceMapping CI 兼容性 ───");
if (!File.Exists("NuGet.Config"))
{
    Console.WriteLine($"{GREEN}PASS{NC}  G32  NuGet.Config 不存在（非仓库根场景，跳过检查）");
    passed++;
}
else
{
    var nugetConfig = File.ReadAllText("NuGet.Config");
    var mappingIdx = nugetConfig.IndexOf("packageSourceMapping", StringComparison.Ordinal);
    var afterMapping = mappingIdx >= 0 ? nugetConfig[mappingIdx..] : "";
    if (nugetConfig.Contains("nuget.org") && afterMapping.Contains("Microsoft.CodeAnalysis"))
    {
        var nugetOrgIdx = afterMapping.IndexOf("key=\"nuget.org\"", StringComparison.Ordinal);
        var window = nugetOrgIdx >= 0 ? afterMapping[nugetOrgIdx..Math.Min(nugetOrgIdx + 400, afterMapping.Length)] : "";
        if (window.Contains("Microsoft.CodeAnalysis"))
        {
            Console.WriteLine($"{GREEN}PASS{NC}  G32  nuget.org 也映射了 Microsoft.CodeAnalysis.*（CI NU1103 防护）");
            passed++;
        }
        else
        {
            Console.WriteLine($"{RED}FAIL{NC}  G32  packageSourceMapping 限制 Microsoft.CodeAnalysis.* 只从 dotnet-tools 取（CI 会 NU1103）");
            failed++;
        }
    }
    else
    {
        Console.WriteLine($"{GREEN}PASS{NC}  G32  NuGet.Config 无限制性映射（跳过检查）");
        passed++;
    }
}

// G33：工作树脏检查（--allow-dirty 跳过）
Console.WriteLine();
Console.WriteLine("─── G33: 工作树脏检查 ───");
if (args.Length > 0 && args[0] == "--allow-dirty")
{
    Console.WriteLine($"{YELLOW}SKIP{NC}  G33  --allow-dirty 指定，跳过工作树检查");
}
else
{
    var dirty = RunCapture("git", "status --short").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (dirty.Length > 0)
    {
        Console.WriteLine($"{RED}FAIL{NC}  G33  工作树有 {dirty.Length} 个未提交改动——跨任务切换前必须 clean 或 stash");
        failed++;
    }
    else
    {
        Console.WriteLine($"{GREEN}PASS{NC}  G33  工作树清洁");
        passed++;
    }
}

Console.WriteLine();
Console.WriteLine($"通过：{passed}  警告：{warned}  失败：{failed}  总计：{passed + warned + failed}");
Console.WriteLine("═══════ 扫描完成 ═══════");
return failed > 0 ? 1 : 0;

// ── 判定输出 ──

void CheckZero(string id, string title, int violations)
{
    if (violations == 0) { Console.WriteLine($"{GREEN}PASS {id}: {title}{NC}"); passed++; }
    else { Console.WriteLine($"{RED}FAIL {id}: {title}（违规数：{violations}）{NC}"); failed++; }
}

// ── git grep 语义（B178 目录形态 pathspec + B13 注释剥离）──

static List<string> GitGrepLines(string pattern, bool ignoreCase, params string[] pathspecs)
{
    // ArgumentList 逐参传递：pattern 含正则元字符，字符串拼接会受引号/转义干扰（B163 同族）
    var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
    psi.ArgumentList.Add("grep");
    psi.ArgumentList.Add("-n");
    psi.ArgumentList.Add("-E");
    if (ignoreCase) psi.ArgumentList.Add("-i");
    psi.ArgumentList.Add("--");
    psi.ArgumentList.Add(pattern);
    foreach (var p in pathspecs) psi.ArgumentList.Add(p);
    using var p0 = Process.Start(psi)!;
    var output = p0.StandardOutput.ReadToEnd();
    p0.WaitForExit();
    if (output.Length == 0) return [];
    return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

int CountMatches(string pattern, params string[] pathspecs)
{
    var lines = GitGrepLines(pattern, ignoreCase: false, pathspecs);
    if (lines.Count == 0) return 0;
    // B13 注释剥离：行尾 // 截断后整行为空者剔除（URL 截断为已登记可接受面）
    return lines
        .Select(l => Regex.Replace(l, @"//.*$", ""))
        .Where(l => !Regex.IsMatch(l, @"^[^:]*:[0-9]+:\s*$"))
        .Count(l => l.Length > 0);
}

static IEnumerable<string> TrackedFiles(string pattern)
{
    return RunCapture("git", $"ls-files -- {pattern}")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

static IEnumerable<string> EnumerateSrcCs()
{
    return Directory.EnumerateFiles("src", "*.cs", SearchOption.AllDirectories).Select(f => f.Replace('\\', '/'));
}

// G12：public static 可写状态（perl -0777 全文匹配两分支，逐支译为 .NET Regex）
int CountPublicStaticState()
{
    var count = 0;
    var branch1 = new Regex(
        @"public\s+static\s+(?:(?:readonly\s+)?(?:Dictionary|List|HashSet|ConcurrentDictionary)<[^;=(){}]+>\s+[A-Za-z_]\w*\s*(?:[;=]|\{[^{}]*\})|[^{;=()]+?\s+[A-Za-z_]\w*\s*\{[^{}]*\bset\s*;[^{}]*\})",
        RegexOptions.Singleline);
    foreach (var f in TrackedFiles("src/**/*.cs"))
    {
        count += branch1.Matches(File.ReadAllText(f)).Count;
    }
    return count;
}

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
    var output = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
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
