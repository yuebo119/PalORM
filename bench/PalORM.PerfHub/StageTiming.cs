using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalORM.Bench.Shared;

namespace PalORM.PerfHub;

/// <summary>阶段 0 的计时汇聚器：PerfHub 内各处把阶段/相位计时投进来，run 末尾落一份旁车 JSON。
/// <para><b>开关</b>：环境变量 <c>PALORM_PERFHUB_TIMING=1</c>。默认关闭——插桩只在做时间分解
/// 时打开，避免常态跑测多出未使用的写文件行为；关闭时所有采集点是一次布尔判断。</para>
/// <para><b>为什么不进结果信封</b>：信封与 history JSON 是门禁与报告的输入，schema 变动会牵连
/// 三个读侧；时间分解是一次性/诊断性数据，旁车文件足够，且写坏它不影响任何判定（红线 #1/#5）。</para></summary>
internal static class TimingSink
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("PALORM_PERFHUB_TIMING") == "1";

    private static readonly List<PerfStageTiming> StageRows = [];
    private static readonly List<PerfPhaseTiming> PhaseRows = [];

    public static void Stage(PerfStageTiming row)
    {
        if (Enabled)
        {
            StageRows.Add(row);
        }
    }

    public static void Phase(string name, string dialect, int rows, double seconds)
    {
        if (Enabled)
        {
            PhaseRows.Add(new PerfPhaseTiming
            {
                Name = name,
                Dialect = dialect,
                Rows = rows,
                Seconds = seconds
            });
        }
    }

    /// <summary>写 <c>bench/reports/perf-timing-&lt;stamp&gt;.json</c>。返回路径（未启用/无数据返回 null）。
    /// <para>时间戳用本机墙钟的批次结束时刻，与 history/logs 的命名规则一致；旁车不覆盖历史。</para></summary>
    public static string? Flush(TimeSpan runTotal, double saveSeconds, double reportBuildSeconds)
    {
        if (!Enabled || StageRows.Count == 0)
        {
            return null;
        }

        var report = new PerfTimingReport
        {
            Timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            Commit = Program.CurrentCommit(),
            RunTotalSeconds = runTotal.TotalSeconds,
            SaveSeconds = saveSeconds,
            ReportBuildSeconds = reportBuildSeconds,
            Items = StageRows,
            Phases = PhaseRows
        };

        string dir = Path.Combine(Program.FindRepoRoot(), "bench", "reports");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"perf-timing-{report.Timestamp}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, TimingJsonContext.Default.PerfTimingReport));
        Console.WriteLine($"[PerfHub] 阶段计时旁车: bench/reports/perf-timing-{report.Timestamp}.json"
            + $"（{StageRows.Count} 项 / {PhaseRows.Count} 相位）");
        return path;
    }
}

[JsonSerializable(typeof(PerfTimingReport))]
internal sealed partial class TimingJsonContext : JsonSerializerContext;
