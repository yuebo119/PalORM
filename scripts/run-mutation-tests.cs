// run-mutation-tests.cs（自 run-mutation-tests.sh 迁移，脚本 C# 化整改方案 T4-8）
// 变异测试——使用 Stryker.NET 验证测试有效性。
// 变异分数 >80% 表示测试体系能有效捕获代码错误。
// 配置：test/PalORM.Core.Tests/stryker-config.json；报告：StrykerOutput/（HTML + JSON）。
using System.Text;

// 输出统一 UTF-8 无 BOM（对齐家族 bash 输出；行尾平台默认）
Console.OutputEncoding = new UTF8Encoding(false);

var repoRoot = FindRepoRoot();
var testsDir = Path.Combine(repoRoot, "test", "PalORM.Core.Tests");

PerfBanner();

// 检查 dotnet-stryker 是否安装，缺失则安装（与 .sh 同语义）
var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "stryker --version")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
});
probe!.WaitForExit();
if (probe.ExitCode != 0)
{
    Console.WriteLine(">>> 安装 dotnet-stryker 全局工具...");
    var install = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "tool install -g dotnet-stryker")
    {
        UseShellExecute = false,
    });
    install!.WaitForExit();
    if (install.ExitCode != 0)
    {
        return install.ExitCode;
    }
}

// 进入测试项目目录运行（Stryker 按工作目录定位工程与配置）
Environment.CurrentDirectory = testsDir;

Console.WriteLine(">>> 开始变异测试（Core 项目 CRUD/Query/Bulk/SessionState 路径）...");
Console.WriteLine(">>> 变异目标：DataSession.Crud / MultiValueBulkInsert / QueryBuilderExtensions / SessionOperationState");
Console.WriteLine(">>> 预计运行时间：10-30 分钟");
Console.WriteLine();

var stryker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "stryker --config-file stryker-config.json")
{
    UseShellExecute = false,
});
stryker!.WaitForExit();
if (stryker.ExitCode != 0)
{
    return stryker.ExitCode;
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine(" 变异测试完成");
Console.WriteLine(" HTML 报告: StrykerOutput/<timestamp>/reports/mutation-report.html");
Console.WriteLine(" JSON 报告: StrykerOutput/<timestamp>/reports/mutation-report.json");
Console.WriteLine();
Console.WriteLine(" 变异分数判据：");
Console.WriteLine("   >80% = 高（绿）  60-80% = 中（黄）  <60% = 低（红）");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
return 0;

static void PerfBanner()
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine(" PalORM 变异测试（Stryker.NET）");
    Console.WriteLine($" 时间: {DateTime.Now}");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    Console.Error.WriteLine("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}
