// refine-scan.cs（自 .ai/scripts/refine-scan.sh 迁移，脚本全面 C# 化轮）
// PalORM 精炼扫描：对 .ai/refine/prompt.md 的 27 项操作矩阵输出机械命中数。
// 可 grep 的 17 项输出命中量级；7 项需 Roslyn/IDE 分析，明确标注 [人工]。
// 命中数是候选量级，不是执行清单——逐项评估后才可执行。
// 契约：0=正常；stdout 与 .sh 版对拍兼容（hit 计数逐项一致）。
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

Environment.CurrentDirectory = FindRepoRoot();
var excludeRegex = new Regex(@"/obj/|/bin/|/Generated/|\.g\.cs", RegexOptions.Compiled);

// 预读全部源文件一次（.sh 版每项 grep 全仓 ×17 次——C# 版单遍装载，计数语义等价）
var srcLines = new List<(string File, string Line)>();
foreach (var f in Directory.EnumerateFiles("src", "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
{
    if (excludeRegex.IsMatch(f.Replace('\\', '/'))) continue;
    foreach (var line in File.ReadAllLines(f)) srcLines.Add((f.Replace('\\', '/'), line));
}

Console.WriteLine("═══════ 精炼扫描（27 项操作矩阵）═══════");
Console.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine("范围: src/ 手写代码（排除 bin/obj/Generated/*.g.cs）");
Console.WriteLine();
Console.WriteLine("─── 一类：减法（A1-A8）───");
Console.WriteLine("A1 0引用类型/方法: [人工] 需 Roslyn 引用分析；grep 计数有四类盲区（生成物类名/公共注解/工厂 lambda/扩展方法）——见误判库 P9，2026-07-19 十候选全证伪");
Console.WriteLine("A2 重复实现: [人工] 需语义比对");
Console.WriteLine("A3 ≤10行标记接口/常量类: [人工] 需逐文件评估合并可行性");
Console.WriteLine($"A4 死 catch 块: {Hits("""catch\s*\((Db)?Exception[^)]*\)\s*\{\s*\}""")} 处空 catch 候选");
Console.WriteLine("A5 冗余 using: 0 处（TreatWarningsAsErrors + IDE0005 编译期强制）");
Console.WriteLine($"A6 SuppressMessage 注解: {Hits("SuppressMessage")} 处（逐项核对 Justification 是否仍必要）");
Console.WriteLine("A7 未使用内建类: [人工] 需 Roslyn 引用分析");
Console.WriteLine($"A8 回退兼容路径: {Hits("(legacy|fallback)")} 处关键词候选");
Console.WriteLine();
Console.WriteLine("─── 二类：现代化（M1-M10）───");
Console.WriteLine($"M1 new List<>: {Hits("""new List<[^>]+>\(\)""")} 处裸构造（带容量/拷贝参数的合规不计）");
Console.WriteLine("M2 传统构造注入: [人工] 主构造函数迁移需逐类评估（🔴 风险）");
Console.WriteLine($"M3 get+校验→required: {Hits(@"\brequired\s")} 处已用 required（其余候选人工确认，🔴 风险）");
Console.WriteLine($"M4 类型判断链: {Hits("else if.*\\bis\\b")} 处 is 链候选");
Console.WriteLine($"M5 string.Format: {Hits("""string\.Format""")} 处");
Console.WriteLine($"M6 new ArgumentNullException: {Hits("new ArgumentNullException")} 处（可改 ThrowIfNull）");
Console.WriteLine("M7 ref struct 枚举器: [人工] 需热路径分析");
Console.WriteLine($"M8 struct→readonly record struct: {Hits("(^|[^a-zA-Z_])struct\\s+[A-Z]")} 处 struct 声明（逐项评估，🔴 风险）");
Console.WriteLine($"M9 object 锁字段→Lock: {Hits("""readonly object _(sync|lock|gate)""")} 处（lock 语句本体不计——Lock 类型迁移后语句无需改动）");
Console.WriteLine($"M10 params T[]: {Hits("""params\s+[A-Za-z0-9_<>,\s]*\[\]""")} 处（可改 params Span<T>）");
Console.WriteLine();
Console.WriteLine("─── 三类：优化（O1-O9）───");
Console.WriteLine($"O1 Dictionary→FrozenDictionary: {Hits("new Dictionary<")} 处 new Dictionary（只读场景评估）");
Console.WriteLine("O2 集合无预分配: [人工] 需容量可知性分析");
Console.WriteLine($"O3 .ToArray/.ToList: {Hits("""\.ToArray\(\)|\.ToList\(\)""")} 处");
Console.WriteLine($"O4 foreach+yield: {Hits("yield return")} 处 yield（枚举器评估）");
Console.WriteLine($"O5 LINQ链热路径: {Hits("""\.Where\(.*\.Select\(|\.Select\(.*\.Where\(""")} 处");
// O6 三行窗口计数（bash：grep -A3 循环头后 3 行内查 += " 或 $ 串接）
var o6 = 0;
for (var i = 0; i < srcLines.Count; i++)
{
    if (!Regex.IsMatch(srcLines[i].Line, @"(for|foreach|while)\s*\(")) continue;
    for (var j = i; j < Math.Min(i + 4, srcLines.Count); j++)
    {
        if (Regex.IsMatch(srcLines[j].Line, """\+=\s*("|\$)""")) { o6++; break; }
    }
}
Console.WriteLine($"O6 字符串 += 循环内累加: {o6} 处（单次条件追加不计——2026-07-19 二轮证实旧模式全为误报）");
Console.WriteLine("O7 ValueTask 多重 await: [人工] 需数据流分析（🔴 风险）");
Console.WriteLine($"O8 object 参数装箱: {Hits(@"\(object\s|,\s*object\s")} 处 object 参数候选");
Console.WriteLine($"O9 Enum.ToString: {Hits("(dialect|[Kk]ind|[Ll]evel|[Cc]onvention|[Oo]utcome)[a-zA-Z_]*\\.ToString\\(\\)")} 处枚举词干 ToString（全量 ToString 含诊断消息，不再作为候选口径）");
Console.WriteLine();
Console.WriteLine("═══════ 扫描完成 ═══════");
Console.WriteLine("机械扫描 17 项；[人工] 标注 7 项（A1/A2/A3/A7/M2/M7/O2/O7）需 Roslyn 或语义分析。");
Console.WriteLine("命中数为候选量级。执行顺序按 prompt 的 P0→P1→P2，每项执行后 dotnet build，每批后 dotnet run --project <test项目>（MTP 口径）。");
Console.WriteLine("纪律：① 批量文本替换前核对行尾（CRLF 文件禁止全文重写——TableModel ±454 假 diff 教训）；");
Console.WriteLine("     ② O 类性能项声称显著收益时须 BenchmarkDotNet 实证，否则效果栏只写机制不写倍数。");
Console.WriteLine();
Console.WriteLine("═══════ v5.0 新增扫描（O25-O27）═══════");
Console.WriteLine();
Console.WriteLine("─── 四类：v5.0 架构精炼（O25-O27）───");
Console.WriteLine($"O25 行数阈值分流候选: {Hits(""">= [0-9]+.*&&|count >= |\.Count >= """)} 处（评估改为环境能力检测，如 local_infile/方言——阈值是伪精确）");
Console.WriteLine("O26 三模式配置开关: [人工] 检查是否有多模式开关可改为方案 Y 双方法（API 即文档）");
Console.WriteLine($"O27 SourceGen WithComparer 覆盖: {srcLines.Count(x => x.File.StartsWith("src/PalORM.SourceGen") && x.Line.Contains("WithComparer"))} 处（sealed record 管道需显式 WithComparer）");
Console.WriteLine();
Console.WriteLine("纪律补充：③ O25 阈值分流改为能力检测前需实测验证两种路径行为一致；");
Console.WriteLine("     ④ O26 方案 Y 双方法的两种语义必须正确性不同（非性能选择）；");
Console.WriteLine("     ⑤ O27 WithComparer 仅对 sealed record + EquatableArray 有效（非 record 不加）。");
return 0;

int Hits(string pattern)
{
    var regex = new Regex(pattern, RegexOptions.Compiled);
    return srcLines.Count(x => regex.IsMatch(x.Line));
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
