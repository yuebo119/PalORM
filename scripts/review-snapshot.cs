// review-snapshot.cs（自 .ai/scripts/review-snapshot.sh 迁移，脚本全面 C# 化轮）
// 生成可复制到评审报告的仓库基线快照。
// 用法: dotnet run --file scripts/review-snapshot.cs [-- --no-build]
// 契约: 0=快照完成；构建失败透传构建退出码；2=用法错；stdout 与 .sh 版对拍兼容（时间行除外）。
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var noBuild = false;
if (args.Length == 1 && args[0] == "--no-build") noBuild = true;
else if (args.Length > 0)
{
    Console.Error.WriteLine("用法：dotnet run --file scripts/review-snapshot.cs [-- --no-build]");
    return 2;
}

Environment.CurrentDirectory = FindRepoRoot();

Console.WriteLine("═══════ 评审基线快照 ═══════");
Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss} {TimeZoneInfo.Local.Id}");
Console.WriteLine($"分支：{Git("branch --show-current").FirstOrDefault() ?? ""}");
Console.WriteLine($"提交：{Git("rev-parse HEAD").FirstOrDefault() ?? ""}");
Console.WriteLine($"短提交：{Git("rev-parse --short HEAD").FirstOrDefault() ?? ""}");
Console.WriteLine($"SDK：{RunCapture("dotnet", "--version").Trim()}");
Console.WriteLine(Git("status --porcelain").Count > 0 ? "工作树：有未提交变更" : "工作树：干净");

Console.WriteLine();
Console.WriteLine("─── 最近一次提交变更 ───");
var diffStat = GitRaw("diff --stat HEAD~1 HEAD");
Console.WriteLine(diffStat.Length > 0 ? diffStat : "(初始提交)");

Console.WriteLine();
Console.WriteLine("─── 最近 10 个提交 ───");
foreach (var l in Git("log --oneline -10")) Console.WriteLine(l);

Console.WriteLine();
Console.WriteLine("─── 项目结构 ───");
foreach (var d in new[] { "src", "test" })
{
    foreach (var f in Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories)
                 .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                          && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                 .OrderBy(f => f, StringComparer.Ordinal))
    {
        Console.WriteLine(f.Replace('\\', '/'));
    }
}

Console.WriteLine();
Console.WriteLine("─── 文件统计 ───");
var exclude = new Regex(@"(^|/)(bin|obj|Generated)/|\.g\.cs$", RegexOptions.Compiled);
var sourceCount = Git("ls-files -- src/**/*.cs").Count(f => !exclude.IsMatch(f));
var testCount = Git("ls-files -- test/**/*.cs").Count(f => !exclude.IsMatch(f));
var docCount = Git("ls-files -- docs/*.md docs/**/*.md").Distinct().Count();
Console.WriteLine($"C# 源文件：{sourceCount}");
Console.WriteLine($"测试文件：{testCount}");
Console.WriteLine($"文档文件：{docCount}");

Console.WriteLine();
Console.WriteLine("─── 构建状态 ───");
if (noBuild)
{
    Console.WriteLine("已跳过（--no-build）");
}
else
{
    var psi = new ProcessStartInfo("dotnet", "build PalORM.slnx -c Release --no-incremental --nologo")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    using var p = Process.Start(psi)!;
    var log = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode == 0)
    {
        var summary = string.Join('\n', log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => Regex.IsMatch(l, @"[0-9]+ (个警告|Warning\(s\))|[0-9]+ (个错误|Error\(s\))"))
            .TakeLast(2));
        if (summary.Length > 0) Console.WriteLine(summary);
        Console.WriteLine("构建退出码：0");
    }
    else
    {
        Console.WriteLine(string.Join('\n', log.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(40)));
        Console.WriteLine($"构建退出码：{p.ExitCode}");
        return p.ExitCode;
    }
}

Console.WriteLine();
Console.WriteLine("═══════ 快照完成 ═══════");
return 0;

static List<string> Git(string args)
{
    return [.. RunCapture("git", args).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

static string GitRaw(string args)
{
    return RunCapture("git", args).TrimEnd();
}

static string RunCapture(string fileName, string arguments)
{
    var psi = new ProcessStartInfo(fileName, arguments)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return output;
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
