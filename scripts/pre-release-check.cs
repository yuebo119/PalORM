// pre-release-check.cs（自 pre-release-check.sh 迁移，脚本 C# 化整改方案 T3-2）
// 发布前预检（发布规范 §4 的"模拟 CI 消费者路径"，2026-09-29 v6.1.0 发布事故固化）。
//
// 用法: dotnet run --file scripts/pre-release-check.cs -- <新版本>
// 步骤（与 verify.yml "NuGet Consumer Native AOT" job 同序同参）:
//   0. 版本残留扫描（release-version-scan，含 placeholder 双判据）
//   1. pack 五包到本地目录（= CI "Pack local packages"，多含 PG/MySQL 两包）
//   2. 用消费者自己的 NuGet.config（clear + 本地源 PalORM.* 映射）做 dotnet restore
//      ——占位版本在这一步必然失败（本地源里没有该包），正是 CI 抓到的形态
//   3. 包契约脚本
// 退出码: 任一步失败即 1。发布 tag 前必跑；全绿才允许 push tag（SOP §5.1 第 4 步前）。
using System.Text;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: pre-release-check.cs <new-version>");
    return 1;
}
var version = args[0];

var repoRoot = FindRepoRoot();
Environment.CurrentDirectory = repoRoot;
const string consumerDir = "test/PalORM.PackageConsumer.Aot";
const string pkgDir = "artifacts/packages"; // 消费者 NuGet.config 的本地源路径（../../artifacts/packages）

var fail = 0;

Step("0/3 版本残留扫描（含 placeholder 双判据）");
// 旧版本号从 git tag 推导：语义 tag 里除本版本外最新的一个
var tags = Git("tag --list \"v*\" --sort=-v:refname").Split('\n', StringSplitOptions.RemoveEmptyEntries);
var oldVersion = tags.FirstOrDefault(t => t != $"v{version}") is { } tag && tag.StartsWith('v') ? tag[1..] : "";
Console.WriteLine($"推导旧版本: {(oldVersion.Length > 0 ? oldVersion : "无")}（git tag 序）");
if (oldVersion.Length > 0)
{
    if (RunDotnet($"run --file scripts/release-version-scan.cs -- \"{oldVersion}\" \"{version}\"", true) != 0)
    {
        fail = 1;
    }
}
else
{
    Console.Error.WriteLine("::error::无法从 git tag 推导旧版本号");
    fail = 1;
}

Step($"1/3 pack 五包到本地目录（{pkgDir}）");
if (Directory.Exists(pkgDir))
{
    Directory.Delete(pkgDir, recursive: true);
}
Directory.CreateDirectory(pkgDir);
foreach (var project in new[] { "Core", "SourceGen", "Sqlite", "PostgreSql", "MySql" })
{
    if (RunDotnet($"pack src/PalORM.{project}/PalORM.{project}.csproj -c Release -o \"{pkgDir}\"", true) != 0)
    {
        // set -e 语义：pack 失败即以该退出码终止
        return 1;
    }
}
var pkgCount = Directory.GetFiles(pkgDir, "*.nupkg").Length;
Console.WriteLine($"打包数: {pkgCount}（应 5）");
if (pkgCount != 5)
{
    Console.Error.WriteLine($"::error::pack 数量 {pkgCount} ≠ 5");
    fail = 1;
}

Step("2/3 消费者 restore 走本地包源（复刻 CI Restore package consumer）");
if (RunDotnet($"restore {consumerDir} --configfile {consumerDir}/NuGet.config", true) != 0)
{
    return 1;
}
Console.WriteLine("消费者 restore OK——占位版本/缺包形态已在此步排除");

Step("3/3 包契约");
if (RunDotnet("run --file scripts/test-package-contract.cs", true) != 0)
{
    return 1;
}

if (fail == 0)
{
    Console.WriteLine($"\nPASS 发布预检全绿（{version}）——允许 push tag");
    return 0;
}
Console.Error.WriteLine("\nFAIL 发布预检有失败项——禁止 push tag");
return 1;

static void Step(string title)
{
    Console.WriteLine();
    Console.WriteLine($"═══ {title} ═══");
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

static string Git(string arguments)
{
    var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return stdout;
}

static int RunDotnet(string arguments, bool inherit)
{
    var psi = new System.Diagnostics.ProcessStartInfo("dotnet", arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = !inherit,
        RedirectStandardError = !inherit,
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var stdoutTask = !inherit ? p.StandardOutput.ReadToEndAsync() : null;
    var stderrTask = !inherit ? p.StandardError.ReadToEndAsync() : null;
    p.WaitForExit();
    if (stdoutTask is not null)
    {
        Console.Write(stdoutTask.Result);
    }
    if (stderrTask is not null)
    {
        Console.Error.Write(stderrTask.Result);
    }
    return p.ExitCode;
}
