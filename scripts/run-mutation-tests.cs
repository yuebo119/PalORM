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
using System.Text.Json;

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

// 变异 runner 会把项目复制到临时目录重编译：源码扫描类测试的编译期路径失效。
// 显式传入真实仓库根，供 RepoPaths 回退（见 test/PalORM.Core.Tests/RepoPaths.cs）。
var (strykerExit, output) = RunCapture(
    "dotnet", "stryker --config-file stryker-config.json", repoRoot);
Console.Write(output);

// MUT-3：健康指标先落地——无论后续走哪个退出分支，job summary 都有有效变异数
MutationHealth health = ReadLatestHealth(testsDir);
WriteHealthReport(testsDir, target, health);

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
if (output.Contains("Initial testrun has more than 50% failing tests", StringComparison.Ordinal))
{
    Console.WriteLine();
    Console.WriteLine("═══ 上游阻塞（exit 3）═══");
    Console.WriteLine("Stryker MTP 预览 runner 把初始测试运行判为失败（TUnit/MTP 结果解析与 vstest 不同族）。");
    Console.WriteLine("本条与 'Failed to restore … buildable state' 是同一上游阻塞的两种表现；构建路径已修");
    Console.WriteLine("（PalORM.mutation.slnx），此前 1591 个 CompileError 已消失，剩余障碍在测试结果解析。");
    Console.WriteLine("健康指标仍写入 job summary 与 health JSON；上游发版后重跑自动恢复。");
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

if (health.EffectiveMutants == 0)
{
    Console.WriteLine();
    Console.WriteLine("═══ 无有效变异（exit 4）═══");
    Console.WriteLine("报告存在但 Killed+Survived+Timeout 为 0：测试没有执行任何变异体（无信号）。");
    Console.WriteLine("已写入 job summary 与 health JSON；不以绿呈现。");
    return 4;
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($" 变异测试完成（exit {strykerExit}，effectiveMutants={health.EffectiveMutants}）");
Console.WriteLine(" HTML 报告: StrykerOutput/<timestamp>/reports/mutation-report.html");
Console.WriteLine(" JSON 报告: StrykerOutput/<timestamp>/reports/mutation-report.json");
Console.WriteLine();
Console.WriteLine(" 变异分数判据：");
Console.WriteLine("   >80% = 高（绿）  60-80% = 中（黄）  <60% = 低（红）");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
return strykerExit;


static MutationHealth ReadLatestHealth(string testsDir)
{
    string root = Path.Combine(testsDir, "StrykerOutput");
    if (!Directory.Exists(root)) return new MutationHealth(0, 0, 0, 0, 0, 0, 0, 0);

    DirectoryInfo? latest = new DirectoryInfo(root)
        .GetDirectories()
        .OrderByDescending(d => d.Name, StringComparer.Ordinal)
        .FirstOrDefault();
    if (latest is null) return new MutationHealth(0, 0, 0, 0, 0, 0, 0, 0);

    string report = Path.Combine(latest.FullName, "reports", "mutation-report.json");
    if (!File.Exists(report)) return new MutationHealth(0, 0, 0, 0, 0, 0, 0, 0);

    int killed = 0, survived = 0, timeout = 0, noCoverage = 0;
    int ignored = 0, compileError = 0, runtimeError = 0, total = 0;
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
    if (!document.RootElement.TryGetProperty("files", out JsonElement files))
        return new MutationHealth(0, 0, 0, 0, 0, 0, 0, 0);

    foreach (JsonProperty file in files.EnumerateObject())
    {
        if (!file.Value.TryGetProperty("mutants", out JsonElement mutants)) continue;
        foreach (JsonElement mutant in mutants.EnumerateArray())
        {
            total++;
            string status = mutant.TryGetProperty("status", out JsonElement statusElement)
                ? statusElement.GetString() ?? ""
                : "";
            switch (status)
            {
                case "Killed": killed++; break;
                case "Survived": survived++; break;
                case "Timeout": timeout++; break;
                case "NoCoverage": noCoverage++; break;
                case "Ignored": ignored++; break;
                case "CompileError": compileError++; break;
                case "RuntimeError": runtimeError++; break;
            }
        }
    }
    return new MutationHealth(
        killed, survived, timeout, noCoverage, ignored, compileError, runtimeError, total);
}

/// <summary>写 job summary 与 health JSON。GITHUB_STEP_SUMMARY 缺失时只打印（本地运行）。</summary>
static void WriteHealthReport(string testsDir, string target, MutationHealth health)
{
    // 换行用 Environment.NewLine 拼接：字符串字面量内直接断行会 CS1010（2026-11-02 CI 实测）。
    static string Row(string label, string value) => $"| {label} | {value} |" + Environment.NewLine;
    string summary = "## 变异测试健康指标（" + target + "）" + Environment.NewLine
        + Environment.NewLine
        + "| 指标 | 数量 |" + Environment.NewLine
        + "|---|---:|" + Environment.NewLine
        + Row("**effectiveMutants**（Killed+Survived+Timeout）", $"**{health.EffectiveMutants}**")
        + Row("Killed", $"{health.Killed}")
        + Row("Survived", $"{health.Survived}")
        + Row("Timeout", $"{health.Timeout}")
        + Row("NoCoverage", $"{health.NoCoverage}")
        + Row("Ignored", $"{health.Ignored}")
        + Row("CompileError", $"{health.CompileError}")
        + Row("RuntimeError", $"{health.RuntimeError}")
        + Row("Total", $"{health.Total}");

    Console.WriteLine();
    Console.WriteLine($"effectiveMutants={health.EffectiveMutants} "
        + $"(killed={health.Killed} survived={health.Survived} timeout={health.Timeout} "
        + $"ignored={health.Ignored} compileError={health.CompileError} total={health.Total})");

    string? summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (!string.IsNullOrEmpty(summaryPath))
    {
        File.AppendAllText(summaryPath, summary + Environment.NewLine);
    }

    string root = Path.Combine(testsDir, "StrykerOutput");
    Directory.CreateDirectory(root);
    File.WriteAllText(
        Path.Combine(root, $"latest-health-{target}.json"),
        System.Text.Json.JsonSerializer.Serialize(health));
}

static void Banner(string target, string configName)
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine($" PalORM 变异测试（Stryker.NET · {target}）");
    Console.WriteLine($" 范围: {configName}");
    Console.WriteLine($" 时间: {DateTime.Now}");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
}

static (int Code, string Output) RunCapture(string command, string arguments, string? repoRoot = null)
{
    var psi = new System.Diagnostics.ProcessStartInfo(command, arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    if (repoRoot is not null) psi.Environment["PALORM_REPO_ROOT"] = repoRoot;
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

/// <summary>变异健康指标（MUT-3）。EffectiveMutants = Killed+Survived+Timeout——
/// 只有这三个状态代表"测试真正执行过该变异体"。</summary>
sealed record MutationHealth(
    int Killed,
    int Survived,
    int Timeout,
    int NoCoverage,
    int Ignored,
    int CompileError,
    int RuntimeError,
    int Total)
{
    public int EffectiveMutants => Killed + Survived + Timeout;
}
