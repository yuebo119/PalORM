// sibling-map.cs（自 .ai/scripts/sibling-map.sh 迁移，脚本全面 C# 化轮）
// 三方言姊妹族枚举器（sibling-map 轴 A 机械生成，跨仓移植 DDD Phase 1a）：
// 「修一个漏姊妹」是复发账本最大族（E3/E4 + r24 四组同型面复发），根因是修复代理
// 的工作单位是文件、族信息不在其上下文——本枚举把族结构在修复任务分解处强制暴露。
// 信息工具非阻断门禁。轴 B 种子表真源 .ai/review/sibling-map.md。
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
return 0;

static string Join(IEnumerable<string> items)
{
    return string.Join(' ', items.OrderBy(m => m, StringComparer.Ordinal));
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
