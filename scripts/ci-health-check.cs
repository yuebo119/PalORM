// ci-health-check.cs（2026-11-09 综合根治方案 CI-5）
// CI 健康检查：最近 7 天各工作流结论，红即打印告警。
// 动因：CI 是安全网，但网破了没有阻塞机制（分支保护未配置时 CI 红不阻塞合并，
// 红变成「已知状态」而非「待修信号」）。本脚本让 CI 健康度可查询、可登记。
//
// 用法：dotnet run --file scripts/ci-health-check.cs [--days 7] [--strict]
//   --days N   检查最近 N 天（默认 7）
//   --strict   有红时 exit 1（供门禁/报告用；默认只告警 exit 0）
// 契约：0=无红（或非 strict 模式有红）；1=strict 模式有红；2=gh 不可用/用法错
using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

var days = 7;
var strict = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--days":
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out days) || days <= 0)
            {
                Console.Error.WriteLine("::error::--days 需要正整数");
                return 2;
            }
            break;
        case "--strict":
            strict = true;
            break;
        default:
            Console.Error.WriteLine($"::error::未知参数 {args[i]}");
            return 2;
    }
}

Environment.CurrentDirectory = FindRepoRoot();

var psi = new ProcessStartInfo("gh", "run list --limit 100 --json workflowName,conclusion,status,createdAt")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    StandardOutputEncoding = Encoding.UTF8,
    StandardErrorEncoding = Encoding.UTF8,
};
using var p = Process.Start(psi);
if (p is null)
{
    Console.Error.WriteLine("::error::无法启动 gh（GitHub CLI 未安装或不在 PATH）");
    return 2;
}
var output = p.StandardOutput.ReadToEnd();
var err = p.StandardError.ReadToEnd();
p.WaitForExit();
if (p.ExitCode != 0)
{
    Console.Error.WriteLine($"::error::gh run list 失败（exit {p.ExitCode}）：{err.Trim()}");
    return 2;
}

var cutoff = DateTime.UtcNow.AddDays(-days);
var failures = new Dictionary<string, List<(DateTime at, string conclusion)>>(StringComparer.OrdinalIgnoreCase);
var total = 0;
try
{
    using var doc = JsonDocument.Parse(output);
    foreach (var run in doc.RootElement.EnumerateArray())
    {
        var name = run.GetProperty("workflowName").GetString() ?? "(未知)";
        var conclusion = run.GetProperty("conclusion").GetString() ?? "";
        var status = run.GetProperty("status").GetString() ?? "";
        var createdAt = run.GetProperty("createdAt").GetString() ?? "";
        if (!DateTime.TryParse(createdAt, out var at)) continue;
        if (at < cutoff) continue;
        if (status != "completed") continue;
        total++;
        if (conclusion is "failure" or "timed_out" or "cancelled" or "startup_failure")
        {
            if (!failures.TryGetValue(name, out var list))
            {
                list = new List<(DateTime, string)>();
                failures[name] = list;
            }
            list.Add((at, conclusion));
        }
    }
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"::error::gh 输出解析失败：{ex.Message}");
    return 2;
}

Console.WriteLine($"── CI 健康检查（最近 {days} 天，完成 {total} 次运行）──");
if (failures.Count == 0)
{
    Console.WriteLine("🟢 无红：所有已完成运行均成功。");
    return 0;
}

Console.WriteLine($"🔴 发现 {failures.Count} 个工作流有红：");
foreach (var (name, list) in failures.OrderByDescending(kv => kv.Value.Count))
{
    var latest = list.MaxBy(x => x.at);
    Console.WriteLine($"  {name}：{list.Count} 次红，最近 {latest.at:yyyy-MM-dd HH:mm} UTC（{latest.conclusion}）");
}
Console.WriteLine("  处置：查 `gh run list --workflow <name>` 定位失败 job，修复后确认恢复。");
return strict ? 1 : 0;

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
