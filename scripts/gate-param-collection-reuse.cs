// gate-param-collection-reuse.cs（形态 A：file-based app，单文件单职责）
//
// 门禁：参数集合复用纪律（R-UNNESTB 族）。
//
// 规则（方言无关的正确性约束）：**跨执行复用的命令，其参数集合在命令生命周期内只允许改
// Value；禁止 Clear / Remove / Add 替换参数对象。确需换参数对象时，命令必须新建。**
//
// 机制依据（探针 mergearray，Npgsql 10.0.3 + auto-prepare 调优）：auto-prepare 在 prepare
// 那一刻缓存当时集合中的参数对象，后续执行从缓存对象取值组装 Bind 消息；集合里换入的新
// 对象不被读取 → 静默发送旧值（数据错、查询成功、无异常）。
//   变体 A 同实例 Clear+重加   ✅   变体 B 同实例就地写 Value ✅
//   变体 C 每次新实例 Clear+重加 ❌ 变体 D 改旧实例则结果跟随 ❌（改新实例不跟随）
//
// 判定方式：**标记制**——每一处参数集合变更调用点必须在邻近注释行声明理由码，缺标记即 FAIL。
// 标记制而非纯静态分析的理由：命令是否「跨执行复用」需要人的判断（载体命令从不执行、
// 池转移是同一实例、方言可达性），门禁强制的是「每处必须声明」而非「门禁替你判断」；
// 新增调用点若不声明即红，声明本身进入 code review。
//
// 用法：dotnet run --file scripts/gate-param-collection-reuse.cs [--verbose]
// 退出码：0 = 全部调用点均已声明；1 = 存在未声明调用点。
using System.Text;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM（对齐 scripts 家族）
Console.OutputEncoding = new UTF8Encoding(false);

var repoRoot = FindRepoRoot();
bool verbose = args.Contains("--verbose");
var violations = new List<string>();
var declared = new List<string>();

// 扫描面：产品源码（不含测试/脚本/工具——测试夹具的命令复用形态与生产不同面）
foreach (string file in Directory
    .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
{
    if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
    {
        continue;
    }

    string[] lines = File.ReadAllLines(file);
    for (int i = 0; i < lines.Length; i++)
    {
        string line = lines[i];
        if (!line.Contains(".Parameters.Clear()", StringComparison.Ordinal)
            && !line.Contains(".Parameters.RemoveAt(", StringComparison.Ordinal))
        {
            continue;
        }

        string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
        string callSite = $"{relative}:{i + 1}  {line.Trim()}";

        // 向上回看 20 行找标记（标记写在调用点或所在分支的注释里）
        int windowStart = Math.Max(0, i - 20);
        string? marker = null;
        for (int k = i; k >= windowStart; k--)
        {
            var m = Regex.Match(lines[k], @"PARAM-REUSE-OK\[(?<code>[a-z-]+)\]\s*(?<why>.+)$");
            if (m.Success)
            {
                marker = $"{m.Groups["code"].Value}: {m.Groups["why"].Value.Trim()}";
                break;
            }
        }

        if (marker is null)
        {
            violations.Add(callSite);
        }
        else
        {
            declared.Add($"{callSite}\n      声明: {marker}");
        }
    }
}

Console.WriteLine($"[参数集合复用门禁] 扫描 src/：已声明 {declared.Count} 处，未声明 {violations.Count} 处");
foreach (string item in declared)
{
    Console.WriteLine($"  OK   {item}");
}
if (verbose)
{
    foreach (string item in declared) Console.WriteLine(item);
}

if (violations.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("FAIL：以下参数集合变更调用点未声明理由码（PARAM-REUSE-OK[code] 原因）");
    foreach (string item in violations) Console.Error.WriteLine($"  {item}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  理由码取值：");
    Console.Error.WriteLine("    carrier   该命令从不执行，仅承载绑定器/参数转换（scratch、probe、keyProbe、列类型采样）");
    Console.Error.WriteLine("    pool      转移参数池中的同一实例（非新实例），命令复用安全");
    Console.Error.WriteLine("    fresh     该命令逐条新建/逐批新建，不跨执行复用");
    Console.Error.WriteLine("    nodbbatch 仅在驱动无 DbBatch 时可达（当前方言：SQLite；SQLite 无 auto-prepare 行为）");
    Console.Error.WriteLine("    legacy    仅旧版生成器模型程序集可达（新生成器恒发射值写入器）");
    Console.Error.WriteLine("  机制与实测见 CHANGELOG「R-UNNESTB」段与探针 mergearray（变体 A/B/C/D）。");
    return 1;
}

Console.WriteLine("PASS：全部参数集合变更调用点均已声明理由码");
return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "src"))
            && File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    throw new InvalidOperationException("未找到仓库根（src/ + Directory.Build.props）");
}
