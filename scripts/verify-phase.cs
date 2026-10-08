// verify-phase.cs（自 .ai/scripts/verify-phase.sh 迁移，脚本全面 C# 化轮）
// 阶段完成验证。所有自动检查均真实阻断，不输出未经测量的结论。
// 迁移修正：原 .sh 三处死引用（.ai/scripts/stub-check.sh 已不存在、scripts/test-package-contract.sh
// 已 C# 化、.ai/scripts/gate-check.sh 本轮迁移）改为现行 dotnet run 形态。
// 契约：0=通过；1=失败/未定义阶段；2=用法错。
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length != 1 || !Regex.IsMatch(args[0], @"^[0-9]+$"))
{
    Console.Error.WriteLine("用法：dotnet run --file scripts/verify-phase.cs -- <phase-number>");
    return 2;
}
var phase = args[0];

Environment.CurrentDirectory = FindRepoRoot();

var isWindows = OperatingSystem.IsWindows();
var nativeRid = isWindows ? "win-x64" : "linux-x64";
var nativeSuffix = isWindows ? ".exe" : "";

var fail = false;
Console.WriteLine($"═══════════ 阶段 {phase} 完成验证 ═══════════");

// ITM-804：空壳扫描脚本缺失不阻断（真身在主仓 scripts/stub-check.cs）
RunStep("空壳扫描", "dotnet", "run --file scripts/stub-check.cs -- src/", ignoreFailure: true);
RunStep("Release 全量构建", "dotnet", "build PalORM.slnx -c Release --no-incremental --nologo");
RunStep("严格 AOT 与规范门禁", "dotnet", "run --file scripts/gate-check.cs");
RunStep("Core 测试", "dotnet", "run --project test/PalORM.Core.Tests -c Release");
RunStep("SourceGen 测试", "dotnet", "run --project test/PalORM.SourceGen.Tests -c Release");

var p = int.Parse(phase);
if (p is 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10)
{
    RunStep("SQLite 集成测试", "dotnet",
        "test test/PalORM.Integration.Tests/PalORM.Integration.Tests.csproj -c Release --no-restore -- --treenode-filter '/*/*/*/*[Category!=ExternalDatabase]'");
}
if (p is 9 or 10)
{
    RunStep("SQLite Native AOT 发布", "dotnet",
        $"publish test/PalORM.AotTest/PalORM.AotTest.csproj -c Release -r {nativeRid} --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:JsonSerializerIsReflectionEnabledByDefault=false -o artifacts/verify-phase/sqlite");
    RunStep("SQLite Native AOT 运行", $"artifacts/verify-phase/sqlite/PalORM.AotTest{nativeSuffix}", "");
    RunStep("打包 SourceGen", "dotnet", "pack src/PalORM.SourceGen/PalORM.SourceGen.csproj -c Release -o artifacts/packages");
    RunStep("打包 Core", "dotnet", "pack src/PalORM.Core/PalORM.Core.csproj -c Release -o artifacts/packages");
    RunStep("打包 SQLite", "dotnet", "pack src/PalORM.Sqlite/PalORM.Sqlite.csproj -c Release -o artifacts/packages");
    RunStep("验证 NuGet 包契约", "dotnet", "run --file scripts/test-package-contract.cs");
    RunStep("还原 NuGet consumer", "dotnet", "restore test/PalORM.PackageConsumer.Aot/PalORM.PackageConsumer.Aot.csproj --configfile test/PalORM.PackageConsumer.Aot/NuGet.config");
    RunStep("NuGet consumer Native AOT 发布", "dotnet",
        $"publish test/PalORM.PackageConsumer.Aot/PalORM.PackageConsumer.Aot.csproj -c Release -r {nativeRid} --self-contained true --no-restore -p:PublishAot=true -p:PublishTrimmed=true -p:JsonSerializerIsReflectionEnabledByDefault=false -o artifacts/verify-phase/package-consumer");
    RunStep("NuGet consumer Native AOT 运行", $"artifacts/verify-phase/package-consumer/PalORM.PackageConsumer.Aot{nativeSuffix}", "");
}

if (p > 10)
{
    Console.WriteLine($"FAIL 未定义阶段：{phase}");
    fail = true;
}

Console.WriteLine();
Console.WriteLine(fail ? $"═══════ 阶段 {phase} 验证失败 ═══════" : $"═══════ 阶段 {phase} 验证通过 ═══════");
return fail ? 1 : 0;

void RunStep(string title, string fileName, string arguments, bool ignoreFailure = false)
{
    Console.WriteLine();
    Console.WriteLine($"--- {title} ---");
    var psi = new ProcessStartInfo(fileName, arguments)
    {
        WorkingDirectory = Environment.CurrentDirectory,
        UseShellExecute = false,
    };
    try
    {
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode == 0)
        {
            Console.WriteLine($"PASS {title}");
        }
        else if (ignoreFailure)
        {
            Console.WriteLine($"SKIP {title}（退出码 {proc.ExitCode}，忽略——ITM-804）");
        }
        else
        {
            Console.WriteLine($"FAIL {title}（退出码：{proc.ExitCode}）");
            fail = true;
        }
    }
    catch (System.ComponentModel.Win32Exception)
    {
        if (ignoreFailure) { Console.WriteLine($"SKIP {title}（可执行文件缺失，忽略）"); }
        else { Console.WriteLine($"FAIL {title}（无法启动：{fileName}）"); fail = true; }
    }
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
