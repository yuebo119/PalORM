// check-sdk.cs（2026-11-09 综合根治方案 ENV-2）
// 本机 SDK 与 global.json 钉版一致性校验——本机 SDK 随系统更新漂移、仓库 rollForward: disable
// 零容忍，任何一次系统级 SDK 更新都会让本地开发断链（CI 按 global.json 现装所以 CI 绿本地红）。
// 本门禁让漂移在提交前立即可见，而不是等构建失败才发现。
// 契约：0=匹配；1=不匹配（打印 dotnet-install 命令）；2=环境错（dotnet 不可用 / global.json 解析失败）。
// --self-check：内嵌好/坏夹具自证判据（匹配态 exit 0、不匹配态 exit 1）。
using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

if (args.Length == 1 && args[0] == "--self-check")
{
    var good = Evaluate("11.0.100-rc.3.26522.148", "disable", new List<string> { "10.0.403", "11.0.100-rc.3.26522.148" });
    var bad = Evaluate("9.9.9", "disable", new List<string> { "11.0.100-rc.3.26522.148" });
    var badRoll = Evaluate("11.0.100-rc.3.26522.148", "latestMinor", new List<string> { "11.0.100-rc.3.26522.148" });
    var ok = good && !bad && badRoll;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  check-sdk --self-check：匹配={(good ? "exit 0" : "exit 1")}，不匹配={(bad ? "exit 0" : "exit 1")}，非 disable 宽松={(badRoll ? "exit 0" : "exit 1")}");
    return ok ? 0 : 1;
}

Environment.CurrentDirectory = FindRepoRoot();
var globalJsonPath = Path.Combine(Environment.CurrentDirectory, "global.json");
if (!File.Exists(globalJsonPath))
{
    Console.WriteLine("SKIP  check-sdk：仓库根无 global.json");
    return 0;
}

string pinned;
string rollForward;
try
{
    using var doc = JsonDocument.Parse(File.ReadAllText(globalJsonPath));
    var sdk = doc.RootElement.GetProperty("sdk");
    pinned = sdk.GetProperty("version").GetString() ?? "";
    rollForward = sdk.TryGetProperty("rollForward", out var rf) ? rf.GetString() ?? "" : "";
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL  check-sdk：global.json 解析失败——{ex.Message}");
    return 2;
}

if (pinned.Length == 0)
{
    Console.WriteLine("SKIP  check-sdk：global.json 未声明 sdk.version");
    return 0;
}

string installedRaw;
try
{
    var psi = new ProcessStartInfo("dotnet", "--list-sdks")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    using var p = Process.Start(psi);
    if (p is null)
    {
        Console.WriteLine("FAIL  check-sdk：无法启动 dotnet");
        return 2;
    }
    installedRaw = p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL  check-sdk：dotnet 不可用——{ex.Message}");
    return 2;
}

var installed = new List<string>();
foreach (var line in installedRaw.Split('\n'))
{
    var first = line.Split(' ')[0].Trim();
    if (first.Length > 0) installed.Add(first);
}

var match = Evaluate(pinned, rollForward, installed);

if (match)
{
    Console.WriteLine($"PASS  check-sdk：{pinned} 已安装（rollForward={rollForward}）");
    return 0;
}

Console.WriteLine($"FAIL  check-sdk：global.json 钉 {pinned}（rollForward={rollForward}），本机未安装");
Console.WriteLine("      本机已安装：" + (installed.Count > 0 ? string.Join(", ", installed) : "(无)"));
Console.WriteLine("      安装命令（官方 dotnet-install）：");
Console.WriteLine($"        powershell -c \"iwr https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\\dotnet-install.ps1; & $env:TEMP\\dotnet-install.ps1 -Version {pinned}\"");
Console.WriteLine("      或：winget install Microsoft.DotNet.SDK.11");
Console.WriteLine("      安装后重跑本脚本确认。CI 按 global-json-file 现装，与本机对齐后两边一致。");
return 1;

// rollForward: disable → 精确匹配（本仓当前策略，钉版目的就是规则集完全一致）；
// 其余取值（patch/minor/major/...）按「同主版本前缀 + 不低于钉版」宽松判定，仅作兜底。
static bool Evaluate(string pinned, string rollForward, List<string> installed)
{
    return rollForward == "disable"
        ? installed.Contains(pinned)
        : installed.Any(v => v == pinned || (v.StartsWith(pinned.Split('-')[0] + ".", StringComparison.Ordinal) && VersionAtLeast(v, pinned)));
}

static bool VersionAtLeast(string candidate, string pinned)
{
    var c = candidate.Split('-')[0].Split('.');
    var p = pinned.Split('-')[0].Split('.');
    for (var i = 0; i < Math.Min(c.Length, p.Length); i++)
    {
        if (!int.TryParse(c[i], out var ci) || !int.TryParse(p[i], out var pi)) return true;
        if (ci != pi) return ci > pi;
    }
    return true;
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
