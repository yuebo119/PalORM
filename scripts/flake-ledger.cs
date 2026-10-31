// flake-ledger.cs（P2 / META-2）：每周 flake 台账。
// 用法：dotnet run --file scripts/flake-ledger.cs -- [--core-runs 3] [--integration-runs 2] [--configuration Release]
// 语义：每个"运行次数"都是独立进程、独立报告；任何一次失败都如实入账并使脚本非零退出。
// 不静默重试——重跑是台账里的一条新记录，不是把失败抹掉的补救。
// 产物：artifacts/flake-ledger/history.json（累积）+ artifacts/flake-ledger/<date>.md（当日可读报表）。
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.OutputEncoding = new UTF8Encoding(false);

string configuration = ReadOption("--configuration") ?? "Release";
int coreRuns = ReadIntOption("--core-runs") ?? 3;
int integrationRuns = ReadIntOption("--integration-runs") ?? 2;
string repoRoot = FindRepoRoot();
string ledgerDir = Path.Combine(repoRoot, "artifacts", "flake-ledger");
Directory.CreateDirectory(ledgerDir);

var entries = new List<JsonObject>();
int failures = 0;

RunProject("PalORM.Core.Tests", coreRuns);
RunProject("PalORM.Integration.Tests", integrationRuns);

WriteLedger();

if (failures > 0)
{
    Console.Error.WriteLine($"::error::flake 台账记录 {failures} 次失败运行（未自动重试，按台账分类）");
    return 1;
}
Console.WriteLine($"flake 台账：{entries.Count} 次运行全部通过");
return 0;

void RunProject(string project, int runs)
{
    for (int run = 1; run <= runs; run++)
    {
        Console.WriteLine($">>> {project} run {run}/{runs}（-c {configuration}）");
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repoRoot,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add($"test/{project}");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configuration);
        psi.ArgumentList.Add("--no-build");

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        int exit = process.ExitCode;

        (int passed, int failed, int skipped)? counts = ReadLatestReport(project);
        bool ok = exit == 0 && (counts is null || counts.Value.failed == 0);
        if (!ok) failures++;

        entries.Add(new JsonObject
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["project"] = project,
            ["run"] = run,
            ["exitCode"] = exit,
            ["passed"] = counts?.passed,
            ["failed"] = counts?.failed,
            ["skipped"] = counts?.skipped,
            ["outcome"] = ok ? "pass" : "fail",
            ["stderrTail"] = ok ? null : Tail(stderr, 20),
            ["stdoutTail"] = ok ? null : Tail(stdout, 6),
        });
        Console.WriteLine($"    exit={exit} passed={counts?.passed} failed={counts?.failed} → {(ok ? "pass" : "fail")}");
    }
}

// TUnit 报告落在 bin/<config>/net11.0/TestResults/*.tunit-report.json（或 bin 根），取最新一份
(int passed, int failed, int skipped)? ReadLatestReport(string project)
{
    string bin = Path.Combine(repoRoot, "test", project, "bin", configuration);
    if (!Directory.Exists(bin)) return null;
    FileInfo? latest = null;
    foreach (string file in Directory.EnumerateFiles(bin, "*.tunit-report.json", SearchOption.AllDirectories))
    {
        var info = new FileInfo(file);
        if (latest is null || info.LastWriteTimeUtc > latest.LastWriteTimeUtc) latest = info;
    }
    if (latest is null) return null;
    try
    {
        JsonNode? root = JsonNode.Parse(File.ReadAllText(latest.FullName));
        if (root is null) return null;
        int passed = root["summary"]?["passed"]?.GetValue<int>() ?? 0;
        int failed = root["summary"]?["failed"]?.GetValue<int>() ?? 0;
        int skipped = root["summary"]?["skipped"]?.GetValue<int>() ?? 0;
        return (passed, failed, skipped);
    }
    catch (JsonException)
    {
        return null;
    }
}

void WriteLedger()
{
    string jsonPath = Path.Combine(ledgerDir, "history.json");
    JsonArray history = File.Exists(jsonPath)
        ? JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonArray ?? []
        : [];
    foreach (JsonObject entry in entries) history.Add(entry.DeepClone());
    File.WriteAllText(jsonPath, history.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    string date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
    var markdown = new StringBuilder();
    markdown.AppendLine($"# flake 台账 {date}");
    markdown.AppendLine();
    markdown.AppendLine("口径：每次运行是独立进程；失败不自动重试。同日同项目的\"首跑失败、后续通过\"记 flake 候选，必须分类后再关账。");
    markdown.AppendLine();
    markdown.AppendLine("| 时间 | 项目 | run | exit | passed | failed | 判定 |");
    markdown.AppendLine("|---|---|---:|---:|---:|---:|---|");
    foreach (JsonObject entry in entries)
    {
        markdown.AppendLine(
            $"| {entry["timestamp"]} | {entry["project"]} | {entry["run"]} | {entry["exitCode"]} "
            + $"| {entry["passed"]?.ToJsonString() ?? "-"} | {entry["failed"]?.ToJsonString() ?? "-"} "
            + $"| {entry["outcome"]} |");
    }
    File.WriteAllText(Path.Combine(ledgerDir, $"{date}.md"), markdown.ToString());
    Console.WriteLine($"台账写入 {Path.Combine(ledgerDir, $"{date}.md")}");
}

string? ReadOption(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
    }
    return null;
}

int? ReadIntOption(string name)
    => int.TryParse(ReadOption(name), out int value) ? value : null;

static string Tail(string text, int lines)
{
    string[] all = text.Split('\n');
    return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
}

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PalORM.slnx"))) return directory.FullName;
        directory = directory.Parent;
    }
    string current = Directory.GetCurrentDirectory();
    if (File.Exists(Path.Combine(current, "PalORM.slnx"))) return current;
    Console.Error.WriteLine("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
    Environment.Exit(2);
    return "";
}