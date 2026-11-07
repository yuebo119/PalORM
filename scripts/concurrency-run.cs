// concurrency-run.cs（高并发压测 lane 入口，2026-11-07）
//
// 动机：模块内压测一直靠手工敲两条 dotnet run + 一段临时解析脚本，且「容器没起全」会让
// 真库用例集体连接被拒、白跑十分钟（2026-11-07 实测：只起 pg186，PG 连接串却指向 pgbench:55432）。
// 本脚本把四件事固化成一条命令：
//   ① 预检真库端口（3306 / 55432），缺失时给出容器名与拉起方式后退出，不开始跑
//   ② 构建两个测试项目（--skip-build 跳过），随后 --no-build 执行
//   ③ 按 Concurren* 通配跑压测 lane（--full 改为全量套件），种子/轮数/时间窗经环境变量传入
//   ④ 把两次运行的 TUnit 报告交给 concurrency-report.cs 汇总（--json 落地机器可读摘要）
//
// 用法：
//   concurrency-run.cs [--rounds N] [--seed N] [--soak-seconds N] [--full] [--skip-build] [--json <out>]
// 退出码：0 全绿；1 有用例失败；2 环境预检或参数错误。
// 纪律：不把子进程输出管进 grep/tail（B104）；先落盘日志，失败时再打印尾部。
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

const int MySqlPort = 3306;
const int PgBenchPort = 55432;

int? rounds = null;
int? seed = null;
int? soakSeconds = null;
bool full = false;
bool skipBuild = false;
string? jsonOut = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rounds" when i + 1 < args.Length && int.TryParse(args[++i], out int r):
            rounds = r;
            break;
        case "--seed" when i + 1 < args.Length && int.TryParse(args[++i], out int s):
            seed = s;
            break;
        case "--soak-seconds" when i + 1 < args.Length && int.TryParse(args[++i], out int w):
            soakSeconds = w;
            break;
        case "--json" when i + 1 < args.Length:
            jsonOut = args[++i];
            break;
        case "--full":
            full = true;
            break;
        case "--skip-build":
            skipBuild = true;
            break;
        case "-h" or "--help":
            return Usage();
        default:
            return Usage($"未知参数：{args[i]}");
    }
}

string root = FindRepoRoot();
string coreProject = Path.Combine(root, "test", "PalORM.Core.Tests");
string integrationProject = Path.Combine(root, "test", "PalORM.Integration.Tests");

// ① 预检：真库端口。压力 lane 的 ConcurrencyRealDb 依赖 PG(55432)+MySQL(3306)。
bool mySqlUp = await CanConnectAsync(MySqlPort);
bool pgUp = await CanConnectAsync(PgBenchPort);
if (!mySqlUp || !pgUp)
{
    Console.Error.WriteLine(
        $"::error::真库端口不可用（MySQL {MySqlPort}: {(mySqlUp ? "ok" : "down")}，"
        + $"PG {PgBenchPort}: {(pgUp ? "ok" : "down")}）。");
    Console.Error.WriteLine(
        "先逐个拉起容器（不要只起 pg186，PG 连接串指向 pgbench:55432）：");
    Console.Error.WriteLine("  wslc container start mysql84 && wslc container start pgbench && wslc container start pg186");
    return 2;
}

string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
string workDir = Path.Combine(Path.GetTempPath(), "palorm-concurrency", stamp);
Directory.CreateDirectory(workDir);

// ② 构建（一次，避免两次 run 各付一次增量构建的解析开销）。
if (!skipBuild)
{
    foreach (string project in new[] { coreProject, integrationProject })
    {
        (int code, _) = await RunAsync(root, "dotnet", $"build \"{project}\" -c Debug", Path.Combine(workDir, "build.log"));
        if (code != 0) return ReportFailure("构建失败", Path.Combine(workDir, "build.log"), code);
    }
}

// ③ 两次运行。--full 不带过滤器；默认 Concurren* 同时覆盖 Concurrency* 与 Concurrent*。
string? filter = full ? null : "/*/*/Concurren*/*";
var reports = new List<string>();
foreach ((string label, string project, string reportPrefix) in new[]
{
    ("Core", coreProject, "core"),
    ("Integration", integrationProject, "integration"),
})
{
    string log = Path.Combine(workDir, $"{reportPrefix}.log");
    string filterArg = filter is null ? "" : $" --treenode-filter \"{filter}\"";
    string command = $"run --project \"{project}\" -c Debug --no-build --{filterArg}";
    Console.WriteLine($"[{label}] {command}");
    (int code, _) = await RunAsync(root, "dotnet", command, log, BuildEnv());
    if (code != 0) return ReportFailure($"{label} 运行失败", log, code);
    string? report = NewestReport(Path.Combine(project, "bin"));
    if (report is null)
    {
        Console.Error.WriteLine($"::error::{label} 未产出 TUnit 报告（检查 bin/**/TestResults）");
        return 2;
    }
    string copy = Path.Combine(workDir, $"{reportPrefix}.tunit-report.json");
    File.Copy(report, copy, overwrite: true);
    reports.Add(copy);
    Console.WriteLine($"[{label}] 通过，报告 {copy}");
}

// ④ 汇总。
var reportArgs = new StringBuilder("run --file \"scripts/concurrency-report.cs\" --");
if (seed is not null) reportArgs.Append($" --seed {seed.Value.ToString(CultureInfo.InvariantCulture)}");
if (rounds is not null) reportArgs.Append($" --rounds {rounds.Value.ToString(CultureInfo.InvariantCulture)}");
if (jsonOut is not null) reportArgs.Append($" --json \"{jsonOut}\"");
foreach (string report in reports) reportArgs.Append($" \"{report}\"");
string reportLog = Path.Combine(workDir, "report.log");
(int reportCode, _) = await RunAsync(root, "dotnet", reportArgs.ToString(), reportLog);
if (File.Exists(reportLog)) Console.WriteLine(await File.ReadAllTextAsync(reportLog));
return reportCode;

Dictionary<string, string> BuildEnv()
{
    var env = new Dictionary<string, string>(StringComparer.Ordinal);
    if (seed is not null) env["PALORM_CONCURRENCY_SEED"] = seed.Value.ToString(CultureInfo.InvariantCulture);
    if (rounds is not null) env["PALORM_CS22_ROUNDS"] = rounds.Value.ToString(CultureInfo.InvariantCulture);
    if (soakSeconds is not null) env["PALORM_SOAK_SECONDS"] = soakSeconds.Value.ToString(CultureInfo.InvariantCulture);
    return env;
}

static int ReportFailure(string message, string log, int code)
{
    Console.Error.WriteLine($"::error::{message}（exit {code}），日志尾部：");
    if (File.Exists(log))
    {
        string[] lines = File.ReadAllLines(log);
        foreach (string line in lines.TakeLast(40)) Console.Error.WriteLine(line);
    }
    return code == 0 ? 1 : code;
}

// 子进程：输出落盘日志（不接 grep/tail），返回退出码（B104 纪律）。
static async Task<(int Code, string LogPath)> RunAsync(
    string workingDirectory, string fileName, string arguments, string logPath,
    IReadOnlyDictionary<string, string>? environment = null)
{
    var startInfo = new ProcessStartInfo(fileName, arguments)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding = new UTF8Encoding(false),
    };
    if (environment is not null)
        foreach ((string key, string value) in environment) startInfo.Environment[key] = value;

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {fileName}");
    var buffer = new StringBuilder();
    Task pump = Task.Run(async () =>
    {
        char[] chunk = new char[8192];
        int read;
        while ((read = await process.StandardOutput.ReadAsync(chunk)) > 0)
            lock (buffer) buffer.Append(chunk, 0, read);
    });
    Task pumpError = Task.Run(async () =>
    {
        char[] chunk = new char[8192];
        int read;
        while ((read = await process.StandardError.ReadAsync(chunk)) > 0)
            lock (buffer) buffer.Append(chunk, 0, read);
    });
    await Task.WhenAll(pump, pumpError);
    await process.WaitForExitAsync();
    await File.WriteAllTextAsync(logPath, buffer.ToString(), new UTF8Encoding(false));
    return (process.ExitCode, logPath);
}

static async Task<bool> CanConnectAsync(int port)
{
    try
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        return true;
    }
    catch (Exception)
    {
        return false;
    }
}

static string? NewestReport(string binRoot)
    => Directory.Exists(binRoot)
        ? Directory.EnumerateFiles(binRoot, "*.tunit-report.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
        : null;

static string FindRepoRoot()
{
    // 文件式 App 的 AppContext.BaseDirectory 在临时构建目录，不是脚本位置；先看当前目录
    // （脚本按仓库根调用），再回退到 BaseDirectory 向上走。
    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        DirectoryInfo? dir = new(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PalORM.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
    }
    throw new DirectoryNotFoundException("未找到仓库根（PalORM.slnx）");
}

static int Usage(string? error = null)
{
    if (error is not null) Console.Error.WriteLine($"::error::{error}");
    Console.Error.WriteLine(
        "usage: concurrency-run.cs [--rounds N] [--seed N] [--soak-seconds N] [--full] [--skip-build] [--json <out>]");
    return 2;
}
