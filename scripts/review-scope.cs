// review-scope.cs（自 .ai/scripts/review-scope.sh 迁移，脚本全面 C# 化轮）
// review 范围清单生成器——地毯式逐行的覆盖度账本基建（质量为先宗旨配套）。
// 用法:
//   dotnet run --file scripts/review-scope.cs                      # 全量档:src/ 手写代码清单+分片建议
//   dotnet run --file scripts/review-scope.cs -- --diff            # 标准档:本次 diff 触及文件全文清单
//   dotnet run --file scripts/review-scope.cs -- --partitions N    # 指定分片数(默认按行数均衡 4 片)
// 产出:文件清单(路径+行数)+ 分片方案 + 可粘贴报告段 1 的覆盖度账本模板。
// 契约:0=正常/空集；1=未知参数；stdout 与 .sh 版对拍兼容。
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var root = FindRepoRoot();
Environment.CurrentDirectory = root;

var mode = "full";
var parts = 4;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--diff") mode = "diff";
    else if (args[i] == "--partitions" && i + 1 < args.Length) parts = int.Parse(args[++i]);
    else { Console.WriteLine($"未知参数: {args[i]}"); return 1; }
}

List<string> files;
if (mode == "diff")
{
    // git diff HEAD~1 --name-only -- 'src/**/*.cs'（.g.cs 排除与 .sh 版对齐）
    files = [.. Run("git", $"diff HEAD~1 --name-only -- src/**/*.cs")
        .Where(f => f.EndsWith(".cs") && !f.EndsWith(".g.cs"))];
    if (files.Count == 0)
    {
        Console.WriteLine("本次 diff 未触及 src/ 手写代码");
        return 0;
    }
}
else
{
    files = [.. Run("git", "ls-files -- src/**/*.cs")
        .Where(f => !f.EndsWith(".g.cs"))];
}

Console.WriteLine($"═══════ review 范围清单（{mode} 档）═══════");
Console.WriteLine($"生成: {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 基线: {Run("git", "rev-parse --short HEAD").FirstOrDefault() ?? "?"}");
Console.WriteLine();

var manifest = new List<(int Lines, string File)>();
var total = 0;
foreach (var f in files)
{
    if (!File.Exists(f)) continue;
    var n = File.ReadLines(f).Count();
    total += n;
    manifest.Add((n, f));
}
// 与 .sh 版 `sort -rn` 对齐：数值降序，同数值 fallback 字节序也反向（降序）
manifest.Sort((a, b) => b.Lines != a.Lines ? b.Lines.CompareTo(a.Lines) : string.CompareOrdinal(b.File, a.File));

Console.WriteLine($"─── 应读清单（{manifest.Count} 文件 · {total} 行）───");
foreach (var (n, f) in manifest) Console.WriteLine($"  {n,5} 行  {f}");

Console.WriteLine();
Console.WriteLine($"─── 分片方案（{parts} 片按行数均衡——供并行子代理各领一片地毯）───");
// 贪心最小负载（LPT）：与 .sh 版 awk 实现逐文件序等价
var load = new int[parts + 1];
var assign = new int[manifest.Count];
for (var i = 0; i < manifest.Count; i++)
{
    var min = 1;
    for (var p = 2; p <= parts; p++) if (load[p] < load[min]) min = p;
    assign[i] = min;
    load[min] += manifest[i].Lines;
}
for (var p = 1; p <= parts; p++)
{
    Console.Write($"  片 {p}（{load[p]} 行）:");
    for (var i = 0; i < manifest.Count; i++) if (assign[i] == p) Console.Write($" {manifest[i].File}");
    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("─── 覆盖度账本模板（粘贴报告段 1，逐文件勾销）───");
foreach (var (n, f) in manifest) Console.WriteLine($"- [ ] {f} ({n} 行)");
Console.WriteLine();
Console.WriteLine("账本规则: 全部勾销 = 地毯完成;未勾销文件出现在报告 = 报告视为草稿。");
return 0;

static List<string> Run(string fileName, string arguments)
{
    var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    p.WaitForExit();
    return lines;
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
