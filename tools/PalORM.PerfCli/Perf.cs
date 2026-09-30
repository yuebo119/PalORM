// PerfCli 公共底座（自 perf.sh 家族五件套的共享机制收敛：仓库根定位、env loader、
// 进程编排、步骤横幅、计时与进度表）。脚本 C# 化整改方案 T4-1。
//
// env loader 吸收自 set-test-env.sh（方案裁决点 1 = 淘汰独立脚本）：
// .env.test 为 gitignored 本地文件，KEY=VALUE 逐行读入当前进程环境后由子进程继承；
// C# 进程无法向父 shell 导出变量，故不再提供 source 形态。
using System.Text;
using System.Text.RegularExpressions;

namespace PalORM.PerfCli;

internal static class Perf
{
    public const string Red = "\x1b[0;31m";
    public const string Nc = "\x1b[0m";

    /// <summary>UI 行输出（显式 LF，对齐 bash echo 的字节形态；与子进程直通行相区分）。</summary>
    public static void Out(string text) => Console.Out.Write(text + "\n");

    /// <summary>UI 行错误输出（显式 LF）。</summary>
    public static void Err(string text) => Console.Error.Write(text + "\n");

    /// <summary>从 CWD 向上找仓库根（PalORM.slnx 哨兵；禁用 AppContext.BaseDirectory——file-based app 指向缓存，项目形态指向 bin，均非仓库根）。</summary>
    public static string RepoRoot()
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
        Perf.Err("::error::未找到仓库根（PalORM.slnx）——请在仓库内运行");
        Environment.Exit(2);
        return "";
    }

    /// <summary>输出编码统一 UTF-8 无 BOM；换行保持平台默认（Windows 子进程重定向输出为 CRLF，
    /// 与 bash 版 tee 日志字节形态一致——对拍实证，勿改回强制 LF）。</summary>
    public static void UseUtf8Output()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
    }

    public static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    public static void Step(string title)
    {
        Out("");
        Out("═══════════════════════════════════════════");
        Out($" {title}");
        Out("═══════════════════════════════════════════");
    }

    /// <summary>set-test-env.sh 的 .env.test 加载（verbose=false 时不回显状态行，等价 perf.sh 的静默 source）。</summary>
    /// <returns>文件存在且加载成功返回 true；缺失时打印指引并按 hardFail 决定退出。</returns>
    public static bool LoadEnvTest(string repoRoot, bool verbose, bool hardFail)
    {
        var envFile = Path.Combine(repoRoot, ".env.test");
        if (!File.Exists(envFile))
        {
            Perf.Err("未找到 .env.test——复制 .env.test.example 到仓库根目录的 .env.test 并填入凭据");
            Perf.Err("  cp .env.test.example .env.test");
            if (hardFail)
            {
                Environment.Exit(1);
            }
            return false;
        }
        ApplyEnvFile(envFile);
        if (verbose)
        {
            CheckEnv("PALORM_PG_CONNECTION", "PALORM_PG_HOST", "PG");
            CheckEnv("PALORM_MYSQL_CONNECTION", "PALORM_MYSQL_HOST", "MySQL");
            Perf.Out("环境变量加载完成——运行 dotnet test 即可");
        }
        return true;
    }

    private static void ApplyEnvFile(string envFile)
    {
        foreach (var rawLine in File.ReadAllLines(envFile))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            // 剥一层成对引号（source 语义等价：bash 展开赋值时去引号）
            if (value.Length >= 2 && IsQuoted(value))
            {
                value = value[1..^1];
            }
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static bool IsQuoted(string value)
    {
        return (value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'');
    }

    private static void CheckEnv(string connectionVar, string hostVar, string label)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(connectionVar)))
        {
            Perf.Out($"{connectionVar} 已设置（整串覆盖，JSON 模板将被绕过）");
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(hostVar)))
        {
            Perf.Out($"{hostVar} 已设置——appsettings.test.json 模板占位符将被解析");
        }
        else
        {
            Perf.Err($"警告：{label} 凭据未配置——需 {connectionVar} 或 {label.ToUpperInvariant()}_* 拆分项");
        }
    }

    /// <summary>
    /// 运行命令（默认 dotnet），合并 stderr 到 stdout 逐行回调（等价 bash 的 2&gt;| 管道形态），
    /// 可选 tee 到日志文件。返回退出码。
    /// 参数串统一正斜杠化：对齐 bash 版传给子进程的路径渲染（PerfGate 等会原样回显路径）。
    /// </summary>
    public static int Run(string command, string arguments, string? logFile = null, Action<string>? onLine = null, bool silentStdout = false)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(command, arguments.Replace('\\', '/'))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        StreamWriter? log = logFile is null ? null : new StreamWriter(logFile, append: false, new UTF8Encoding(false));
        try
        {
            // stdout/stderr 合并逐行：line 回调先于文件写入（tee 语义：全量入文件，过滤后进控制台）
            var outTask = PumpAsync(p.StandardOutput, log, onLine, silentStdout);
            var errTask = PumpAsync(p.StandardError, log, onLine, silentStdout);
            Task.WaitAll(outTask, errTask);
        }
        finally
        {
            log?.Dispose();
        }
        p.WaitForExit();
        return p.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, StreamWriter? log, Action<string>? onLine, bool silentStdout)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            log?.WriteLine(line);
            if (!silentStdout)
            {
                // 默认控制台直通（对齐 bash 的 `| tee` 形态：子进程输出可见）；onLine 供过滤形态覆写
                (onLine ?? Console.WriteLine).Invoke(line);
            }
        }
    }

    /// <summary>运行命令并把全量输出 tee 到文件（控制台不过滤）。</summary>
    public static int RunTee(string command, string arguments, string logFile)
    {
        return Run(command, arguments, logFile, onLine: Console.WriteLine);
    }

    /// <summary>运行命令：全量 tee 到文件，控制台只显示匹配行（等价 `cmd 2&gt;| tee log | grep -E pat`）。</summary>
    public static int RunFiltered(string command, string arguments, string logFile, string consolePattern)
    {
        var filter = new Regex(consolePattern, RegexOptions.Compiled);
        return Run(command, arguments, logFile, onLine: line =>
        {
            if (filter.IsMatch(line))
            {
                // 等价 grep 直通：保留子进程原始行尾（CRLF），勿改 UI 的 LF 形态
                Console.WriteLine(line);
            }
        });
    }

    /// <summary>运行命令：全量 tee 到文件，控制台只显示最后 tailLines 行（等价 `| tee log | tail -3`）。</summary>
    public static int RunTail(string command, string arguments, string logFile, int tailLines)
    {
        List<string> all = [];
        var code = Run(command, arguments, logFile, onLine: all.Add);
        foreach (var line in all.TakeLast(tailLines))
        {
            // 等价 tail 直通：保留子进程原始行尾
            Console.WriteLine(line);
        }
        return code;
    }

    /// <summary>静默运行（stdout 丢弃），返回退出码与全量输出（供 contains 判定）。</summary>
    public static (int Code, string Output) RunCapture(string command, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(command, arguments.Replace('\\', '/'))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdoutTask.Result + stderrTask.Result);
    }

    public static string Git(string arguments)
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

    // ── 计时与进度表（perf.sh 的实时进度/末尾汇总机制）──
    private static readonly List<string[]> StepRows = [];
    public static DateTime TotalStart { get; private set; }

    public static void StartClock() => TotalStart = DateTime.Now;

    public static string FmtDur(int seconds)
    {
        return seconds switch
        {
            >= 3600 => $"{seconds / 3600}h{seconds % 3600 / 60}m",
            >= 60 => $"{seconds / 60}m{seconds % 60:D2}s",
            _ => $"{seconds}s",
        };
    }

    public static int ElapsedSince(DateTime t0) => (int)(DateTime.Now - t0).TotalSeconds;

    public static void RecordStep(string name, string status, int secs, int cum)
    {
        StepRows.Add([name, status, secs.ToStringInvariant(), cum.ToStringInvariant()]);
        Perf.Out("");
        Perf.Out($"  {status} 本步 {FmtDur(secs)}，累计 {FmtDur(cum)}");
        PrintProgressTable();
    }

    private static void PrintProgressTable()
    {
        Perf.Out("");
        Perf.Out("┌ 进度 ────────────────────────────────────────────────────────────────");
        foreach (var row in StepRows)
        {
            var secs = int.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture);
            var cum = int.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture);
            Perf.Out($"│ {row[1],-4} 本步 {FmtDur(secs),-8} 累计 {FmtDur(cum),-9} {row[0]}");
        }
        Perf.Out("└──────────────────────────────────────────────────────────────────────");
    }

    public static void PrintFinalTable()
    {
        var total = ElapsedSince(TotalStart);
        Perf.Out("");
        Perf.Out($"╭─ 全量跑测汇总 {DateTime.Now:yyyy-MM-dd HH:mm} ──────────────────────────────");
        foreach (var row in StepRows)
        {
            var secs = int.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture);
            var cum = int.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture);
            var pct = total > 0 ? $"{secs * 100 / total}%" : "-";
            Perf.Out($"│ {row[1],-4} {FmtDur(secs),-9} {pct,-5} 累计 {FmtDur(cum),-9} {row[0]}");
        }
        Perf.Out("│");
        Perf.Out($"│ 总计 {FmtDur(total)}（OK=通过 FAIL=失败；报告：bench/reports/perf-report-<时间戳>.md）");
        Perf.Out("╰──────────────────────────────────────────────────────────────────────");
    }

    public static void ClearSteps() => StepRows.Clear();

    /// <summary>取目录下按文件名排序最新的匹配文件（等价 ls -t | head -1；文件名含时间戳时与 mtime 排序一致）。</summary>
    public static string? NewestFile(string dir, string pattern)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }
        return Directory.GetFiles(dir, pattern).OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    private static string ToStringInvariant(this int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
