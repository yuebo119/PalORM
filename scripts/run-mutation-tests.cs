// run-mutation-tests.cs（自 run-mutation-tests.sh 迁移，脚本 C# 化整改方案 T4-8）
// 变异测试——使用 Stryker.NET 验证测试有效性。
// 变异分数 >80% 表示测试体系能有效捕获代码错误。
// 配置：test/<项目>/stryker-config.json（5.x schema + mtp runner）；报告：StrykerOutput/。
//
// 退出码协议：0=得分达标；1=得分低于阈值或运行失败（真信号）；2=用法错；3=上游阻塞
//（stryker MTP 预览 runner 已知缺陷，issue #3799 同族，修复 PR #3817 已合未发版——
//  上游发版后本脚本装最新工具自动重试，无需改代码）；4=变异面为空（0 个变异被测，
//  无信息——SourceGen 实测形态：3782 变异全被 mutate filter/编译错滤掉，跨项目过滤
//  上游缺陷的另一表现；无得分 ≠ 通过，禁止以绿呈现）。
using System.Text;

// 输出统一 UTF-8 无 BOM（对齐家族 bash 输出；行尾平台默认）
Console.OutputEncoding = new UTF8Encoding(false);

var target = args.Length > 0 && args[0] is "core" or "sourcegen"
    ? args[0]
    : "core";
var (testDirName, configName) = target switch
{
    "core" => ("PalORM.Core.Tests", "CRUD/Query/Bulk/SessionState"),
    "sourcegen" => ("PalORM.SourceGen.Tests", "SourceGen 生成器全量"),
    _ => throw new InvalidOperationException(),
};

var repoRoot = FindRepoRoot();
var testsDir = Path.Combine(repoRoot, "test", testDirName);

Banner(target, configName);

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

Console.WriteLine($">>> 开始变异测试（{target}：{configName} 路径）...");
Console.WriteLine(">>> 预计运行时间：10-30 分钟");
Console.WriteLine();

var (strykerExit, output) = RunCapture("dotnet", "stryker --config-file stryker-config.json");
Console.Write(output);

// 上游阻塞识别：MTP 预览 runner 的恢复器缺陷在初始构建阶段即中止（0.00% 得分必伴随），
// 与"测试真的杀不死变异"的低得分（1）必须区分——前者不是测试体系的信号
if (output.Contains("Failed to restore the project to a buildable state", StringComparison.Ordinal))
{
    Console.WriteLine();
    Console.WriteLine("═══ 上游阻塞（exit 3）═══");
    Console.WriteLine("stryker MTP 预览 runner 已知缺陷（stryker-net#3799 同族，修复 PR #3817 已合未发版）。");
    Console.WriteLine("这不是测试体系的信号。上游发版后重跑即自动恢复；CI 对 exit 3 记 notice 不判红。");
    return 3;
}
if (output.Contains("is using Microsoft.Testing.Platform which is not yet supported", StringComparison.Ordinal))
{
    Console.WriteLine();
    Console.WriteLine("═══ 上游阻塞（exit 3）═══");
    Console.WriteLine("当前 stryker 版本无 MTP runner 支持（需 4.13+ 且配置 test-runner: mtp）。");
    return 3;
}
if (output.Contains("unable to calculate a mutation score", StringComparison.Ordinal)
    || output.Contains("0     total mutants will be tested", StringComparison.Ordinal))
{
    Console.WriteLine();
    Console.WriteLine("═══ 变异面为空（exit 4）═══");
    Console.WriteLine("全部变异体被 mutate filter/编译错滤除，0 个被测——无信息，不以绿呈现。");
    Console.WriteLine("上游跨项目 mutate 过滤缺陷（同 #3799 家族观察）或排除配置过宽，待上游发版复测。");
    return 4;
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($" 变异测试完成（exit {strykerExit}）");
Console.WriteLine(" HTML 报告: StrykerOutput/<timestamp>/reports/mutation-report.html");
Console.WriteLine(" JSON 报告: StrykerOutput/<timestamp>/reports/mutation-report.json");
Console.WriteLine();
Console.WriteLine(" 变异分数判据：");
Console.WriteLine("   >80% = 高（绿）  60-80% = 中（黄）  <60% = 低（红）");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
return strykerExit;

static void Banner(string target, string configName)
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine($" PalORM 变异测试（Stryker.NET · {target}）");
    Console.WriteLine($" 范围: {configName}");
    Console.WriteLine($" 时间: {DateTime.Now}");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
}

static (int Code, string Output) RunCapture(string command, string arguments)
{
    var psi = new System.Diagnostics.ProcessStartInfo(command, arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var outTask = p.StandardOutput.ReadToEndAsync();
    var errTask = p.StandardError.ReadToEndAsync();
    p.WaitForExit();
    return (p.ExitCode, outTask.Result + errTask.Result);
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
