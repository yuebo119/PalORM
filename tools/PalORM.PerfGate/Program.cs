using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PalORM.PerfGate;

/// <summary>PalORM 性能基线的记录与回归检查。
/// <para><b>为什么是独立工具而非内联脚本</b>：判定逻辑曾被写成 workflow 内联的
/// <c>python3 -c</c> 片段，读的是控制台表格、失败落到 warning 分支——即"仪器本身从未被验证"。
/// 抽成工具后可本地对同一份 BDN 产物反复跑，阳性对照与变异探针都能直接打在它上面。</para>
/// <para><b>判定口径</b>：只对分配字节数与"相对同轮手写对照的比值"设阈值，不设绝对耗时
/// （理由见 <see cref="PerfBaseline"/>）。缺基准、缺对照、schema 不符、哨兵缺失一律失败。</para>
/// <para>退出码：0 通过；1 回归或不可判定。</para></summary>
internal static class Program
{
    private const int Schema = 1;

    /// <summary>OS 描述归一到平台族——用于"基线录在别的平台"的提示。
    /// 只认三族：判不出时返回描述原文，宁可提示也不要静默当作同平台。</summary>
    private static string PlatformFamily(string osDescription)
    {
        if (osDescription.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (osDescription.Contains("Darwin", StringComparison.OrdinalIgnoreCase)
            || osDescription.Contains("macOS", StringComparison.OrdinalIgnoreCase)) return "macOS";
        // Linux 的 OSDescription 是发行版名（"Ubuntu 24.04.1 LTS" / "Debian GNU/Linux 12"），
        // 字面常不含 "Linux"——只认 "Linux" 会把两个 Ubuntu 补丁版本判成不同族而误报。
        string[] linuxMarkers =
            ["Linux", "Ubuntu", "Debian", "Alpine", "CentOS", "Fedora", "Red Hat", "Rocky", "SUSE"];
        return linuxMarkers.Any(marker => osDescription.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? "Linux"
            : osDescription;
    }

    /// <summary>哨兵：按基准名的**最后一段**判定，与根命名空间无关。
    /// <para>FullName 形如 <c>PalORM.Benchmarks.CrudBenchmarks.PalORM_QueryAll</c>——
    /// 对整串做 <c>CrudBenchmarks.PalORM_</c> 前缀匹配会永远失败（阳性对照实测过一次：
    /// 门禁恒红，与恒绿同样致命）。比值检查同时需要被测侧与手写对照侧。</para></summary>
    private static readonly string[] RequiredLeafPrefixes = ["PalORM_", "ADO_NET_"];

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "record" => Record(CommandLine.Parse(args[1..])),
                "check" => Check(CommandLine.Parse(args[1..])),
                "report" => Report(CommandLine.Parse(args[1..])),
                "index" => Index(CommandLine.Parse(args[1..])),
                "record-index" => RecordIndex(CommandLine.Parse(args[1..])),
                "check-index" => CheckIndex(CommandLine.Parse(args[1..])),
                _ => Fail($"未知子命令: {args[0]}"),
            };
        }
        catch (InvalidOperationException exception)
        {
            return Fail(exception.Message);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法:");
        Console.WriteLine("  record --results <BDN结果目录> --out <基线路径> --version <v> --date <yyyy-MM-dd>");
        Console.WriteLine("         [--scope <说明>] [--notes <说明>] [--exclude <基准全名> ...]");
        Console.WriteLine("  check  --results <BDN结果目录> --baseline <基线路径>");
        Console.WriteLine("  report --results <BDN结果目录> --baseline <基线路径> --workload <json> --memory <json> --out <md>");
        Console.WriteLine("         [--startup ok] [--envelopes <结果库目录>] [--index-baseline <索引基线路径>]");
        Console.WriteLine("         （workload/memory/out 可选；缺省只含微基准节。传 --envelopes 时同一份报告");
        Console.WriteLine("           追加跨夹具批次登记/口径/健康度与关键项——一次跑测只产出一份报告）");
        Console.WriteLine("  index  --results <结果库目录> --out <md>");
        Console.WriteLine("         （扫三套夹具的信封 JSON，生成跨夹具索引报告；默认 bench/results → bench/reports/perf-index.md）");
        Console.WriteLine("  record-index --out <基线路径> [--results <结果库目录>]");
        Console.WriteLine("         （把结果库里 PerfHub 的 PalORM 比值录成基线；DapperSuite 只作哨兵不进基线）");
        Console.WriteLine("  check-index --baseline <基线路径> [--results <结果库目录>]");
        Console.WriteLine("         （比对最近一批的比值，恶化超阈值即失败；缺项不判失败，全缺项判失败）");
    }

    /// <summary>结果库索引（规范 v2 §6）：默认目录与默认输出都可被命令行覆盖。</summary>
    private static int Index(CommandLine line)
    {
        string resultsDir = line.Optional("results") is { Length: > 0 } dir
            ? dir
            : Path.Combine(RepoRoot(), "bench", "results");
        string output = line.Optional("out") is { Length: > 0 } outPath
            ? outPath
            : Path.Combine(RepoRoot(), "bench", "reports", "perf-index.md");
        return IndexGenerator.Generate(resultsDir, output);
    }

    private static int RecordIndex(CommandLine line)
    {
        string resultsDir = line.Optional("results") is { Length: > 0 } dir
            ? dir
            : Path.Combine(RepoRoot(), "bench", "results");
        return IndexGate.Record(resultsDir, line.Require("out"));
    }

    private static int CheckIndex(CommandLine line)
    {
        string resultsDir = line.Optional("results") is { Length: > 0 } dir
            ? dir
            : Path.Combine(RepoRoot(), "bench", "results");
        return IndexGate.Check(resultsDir, line.Require("baseline"));
    }

    /// <summary>仓库根：向上找 PalORM.slnx（与 run-full-perf.sh 的定位方式一致）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"FATAL: {message}");
        return 1;
    }

    // ─── record ────────────────────────────────────────────────────────────

    private static int Record(CommandLine line)
    {
        string resultsDirectory = line.Require("results");
        string output = line.Require("out");
        ResultSet results = ResultReader.Read(resultsDirectory);
        HashSet<string> excluded = [.. line.Many("exclude")];

        List<BaselineEntry> entries = [];
        int ratioCount = 0;
        foreach (string name in results.Benchmarks.Keys.Order(StringComparer.Ordinal))
        {
            if (excluded.Contains(name)) continue;
            BenchmarkMeasurement measurement = results.Benchmarks[name];
            var entry = new BaselineEntry
            {
                Name = name,
                AllocatedBytes = Math.Round(measurement.AllocatedBytes, 1),
                MedianNs = Math.Round(measurement.MedianNanoseconds, 1),
            };

            string? peer = RatioPeer(name, results.Benchmarks);
            if (peer is not null)
            {
                entry.RatioVs = peer;
                entry.AllocRatio = Ratio(measurement.AllocatedBytes, results.Benchmarks[peer].AllocatedBytes);
                entry.TimeRatio = Ratio(measurement.MedianNanoseconds, results.Benchmarks[peer].MedianNanoseconds);
                ratioCount++;
            }
            entries.Add(entry);
        }

        var baseline = new PerfBaseline
        {
            Schema = Schema,
            Version = line.Require("version"),
            Date = line.Require("date"),
            Scope = line.Optional("scope"),
            Notes = line.Optional("notes"),
            Environment = new BaselineEnvironment
            {
                Os = results.Environment?.OsVersion,
                Processor = results.Environment?.ProcessorName,
                Runtime = results.Environment?.RuntimeVersion,
                BenchmarkDotNet = results.Environment?.BenchmarkDotNetVersion,
            },
            Thresholds = new BaselineThresholds(),
            Benchmarks = entries,
        };

        string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(output, JsonSerializer.Serialize(baseline, BaselineJsonContext.Default.PerfBaseline));

        Console.WriteLine($"已记录 {entries.Count} 项基准 → {output}");
        Console.WriteLine($"  其中带同轮对照比值的 {ratioCount} 项（分配比 + 耗时比）");
        return 0;
    }

    /// <summary>给 PalORM_X 找同轮手写对照：优先 ADO.NET，其次 Dapper。
    /// <para>首选手写 ADO.NET——它没有 ORM 层的可变因素，是"ORM 税"的干净基线；
    /// Dapper 作为回退用于 OrmComparison 组（那一组的设计对照就是 Dapper 的 IL 缓存 miss，
    /// 没有 ADO.NET 对手）。</para></summary>
    private static string? RatioPeer(string name, Dictionary<string, BenchmarkMeasurement> benchmarks)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0) return null;
        string head = name[..dot];
        string leaf = name[(dot + 1)..];
        if (!leaf.StartsWith("PalORM_", StringComparison.Ordinal)) return null;

        string suffix = leaf["PalORM_".Length..];
        foreach (string prefix in (string[])["ADO_NET_", "Dapper_"])
        {
            string candidate = $"{head}.{prefix}{suffix}";
            if (benchmarks.ContainsKey(candidate)) return candidate;
        }
        return null;
    }

    private static double? Ratio(double value, double peer)
        => peer == 0 ? null : Math.Round(value / peer, 4);

    // ─── report ────────────────────────────────────────────────────────────

    private static int Report(CommandLine line)
    {
        string baselinePath = line.Require("baseline");
        PerfBaseline baseline;
        using (FileStream stream = File.OpenRead(baselinePath))
        {
            baseline = JsonSerializer.Deserialize(stream, BaselineJsonContext.Default.PerfBaseline)
                ?? throw new InvalidOperationException($"基线 {baselinePath} 反序列化为 null");
        }

        (int passed, int total) = ReportGenerator.Generate(
            line.Require("results"),
            baseline,
            new ReportGenerator.ReportInputs(
                line.Optional("workload"),
                line.Optional("memory"),
                line.Optional("startup"),
                line.Optional("envelopes"),
                line.Optional("index-baseline")),
            line.Require("out"));
        Console.WriteLine($"报告已生成：{line.Require("out")}（门禁判定 {passed}/{total} 阈值内）");
        return passed == total ? 0 : 1;
    }

    // ─── check ─────────────────────────────────────────────────────────────

    private static int Check(CommandLine line)
    {
        string baselinePath = line.Require("baseline");
        PerfBaseline baseline;
        using (FileStream stream = File.OpenRead(baselinePath))
        {
            baseline = JsonSerializer.Deserialize(stream, BaselineJsonContext.Default.PerfBaseline)
                ?? throw new InvalidOperationException($"基线 {baselinePath} 反序列化为 null");
        }

        if (baseline.Schema != Schema)
        {
            throw new InvalidOperationException(
                $"基线 schema={baseline.Schema} 与本工具要求的 {Schema} 不符");
        }

        ResultSet results = ResultReader.Read(line.Require("results"));
        EnsureSentinel(results);

        Console.WriteLine($"基线: {baselinePath}（{baseline.Version} / {baseline.Date}）");
        // 基线可能录在别的 OS 上（本仓库基线录于 Windows，workflow 跑在 ubuntu-latest）。
        // 分配字节数在 Windows 上四轮逐位相同（仅 QueryAll ±0.002%），跨平台是否相同**未验证**——
        // 分配阈值放宽到 +20% 的一部分原因即此。差异出现时先提示，避免把平台差读成真回归。
        string currentPlatform = PlatformFamily(RuntimeInformation.OSDescription);
        if (baseline.Environment.Os is { Length: > 0 } recordedOs
            && PlatformFamily(recordedOs) != currentPlatform)
        {
            Console.WriteLine(
                $"⚠ 录制平台与当前不同：基线 {recordedOs}（{PlatformFamily(recordedOs)}）"
                + $" / 当前 {currentPlatform}。跨平台差异可能造成误报——判定前先确认差异是否为平台性。");
        }

        Console.WriteLine(
            $"阈值: 分配 +{baseline.Thresholds.AllocatedPct.ToString("F0", CultureInfo.InvariantCulture)}%"
            + $" · 分配比 +{baseline.Thresholds.AllocRatioPct.ToString("F0", CultureInfo.InvariantCulture)}%"
            + $" · 耗时比 +{baseline.Thresholds.TimeRatioPct.ToString("F0", CultureInfo.InvariantCulture)}%");
        Console.WriteLine();
        Console.WriteLine($"{"基准",-44} {"分配B/op",12} {"Δ分配",8} {"分配比",12} {"耗时比",14} {"判定",6}");
        Console.WriteLine(new string('-', 104));

        List<string> failures = [];
        foreach (BaselineEntry entry in baseline.Benchmarks)
        {
            failures.AddRange(Judge(entry, results, baseline.Thresholds));
        }

        Console.WriteLine();
        if (failures.Count > 0)
        {
            Console.WriteLine($"❌ 回归检查未通过（{failures.Count} 项）：");
            foreach (string failure in failures) Console.WriteLine($"  - {failure}");
            return 1;
        }

        Console.WriteLine($"✅ 通过：{baseline.Benchmarks.Count} 项基准均在阈值内");
        return 0;
    }

    /// <summary>哨兵：基准没真跑时立刻失败，而不是逐项报"缺失"（后者容易被误读成基线过期）。</summary>
    private static void EnsureSentinel(ResultSet results)
    {
        foreach (string prefix in RequiredLeafPrefixes)
        {
            bool present = false;
            foreach (string name in results.Benchmarks.Keys)
            {
                int dot = name.LastIndexOf('.');
                string leaf = dot < 0 ? name : name[(dot + 1)..];
                if (leaf.StartsWith(prefix, StringComparison.Ordinal))
                {
                    present = true;
                    break;
                }
            }

            if (!present)
            {
                throw new InvalidOperationException(
                    $"本轮结果里没有任何 {prefix}* 基准——基准未真正执行"
                    + $"（共 {results.Benchmarks.Count} 项）。检查 --filter 是否匹配到类名。");
            }
        }
    }

    /// <summary>判定单条基准，返回失败描述（通过则为空）。
    /// 同时打印该行——打印与判定共用同一份计算，避免"表里显示 OK 但判定另有口径"。</summary>
    private static List<string> Judge(BaselineEntry entry, ResultSet results, BaselineThresholds thresholds)
    {
        List<string> failures = [];
        string shortName = entry.Name[(entry.Name.LastIndexOf('.') + 1)..];

        if (!results.Benchmarks.TryGetValue(entry.Name, out BenchmarkMeasurement? current))
        {
            Console.WriteLine($"{shortName,-44} {"(缺失)",12} {"-",8} {"-",12} {"-",14} {"FAIL",6}");
            failures.Add($"{entry.Name}: 本轮结果缺失（基线里有、本次没跑）");
            return failures;
        }

        double deltaPct = entry.AllocatedBytes == 0
            ? 0
            : ((current.AllocatedBytes / entry.AllocatedBytes) - 1) * 100;
        string verdict = "OK";

        if (current.AllocatedBytes > entry.AllocatedBytes * (1 + (thresholds.AllocatedPct / 100)))
        {
            verdict = "FAIL";
            failures.Add(
                $"{entry.Name}: 分配 {current.AllocatedBytes:F0} B/op 超基线 {entry.AllocatedBytes:F0} B/op 的 "
                + $"+{thresholds.AllocatedPct:F0}%（实测 {deltaPct:+0.0;-0.0}%）");
        }

        // 对照名优先沿用基线的记录（保证同口径），未记录时按规则重找
        string? peerName = entry.RatioVs ?? RatioPeer(entry.Name, results.Benchmarks);
        BenchmarkMeasurement? peer = peerName is not null
            && results.Benchmarks.TryGetValue(peerName, out BenchmarkMeasurement? found) ? found : null;

        RatioVerdict alloc = JudgeRatio(
            "分配比", entry.AllocRatio, thresholds.AllocRatioPct,
            new RatioSubject(entry.Name, peerName, peer, current.AllocatedBytes, static m => m.AllocatedBytes));
        RatioVerdict time = JudgeRatio(
            "耗时比", entry.TimeRatio, thresholds.TimeRatioPct,
            new RatioSubject(entry.Name, peerName, peer, current.MedianNanoseconds, static m => m.MedianNanoseconds));

        if (alloc.Verdict == "FAIL" || time.Verdict == "FAIL") verdict = "FAIL";
        failures.AddRange(alloc.Failures);
        failures.AddRange(time.Failures);

        Console.WriteLine(
            $"{shortName,-44} {current.AllocatedBytes,12:F0} {deltaPct,7:F1}% "
            + $"{alloc.Text,12} {time.Text,14} {verdict,6}");

        return failures;
    }

    /// <summary>判定一条比值维度（分配比或耗时比）。比值在本轮内计算，与基线比值同口径比较。</summary>
    private static RatioVerdict JudgeRatio(
        string label, double? baselineRatio, double thresholdPct, RatioSubject subject)
    {
        if (baselineRatio is null) return new RatioVerdict("-", "OK", []);

        if (subject.PeerName is null || subject.Peer is null)
        {
            return new RatioVerdict("缺失", "FAIL",
                [$"{subject.EntryName}: {label}对照本轮缺失，无法判定"]);
        }

        double peerValue = subject.Select(subject.Peer);
        if (peerValue == 0) return new RatioVerdict("-", "OK", []);

        double currentRatio = subject.CurrentValue / peerValue;
        string text = $"{currentRatio:F3}/{baselineRatio.Value:F3}";
        if (currentRatio > baselineRatio.Value * (1 + (thresholdPct / 100)))
        {
            return new RatioVerdict(text, "FAIL",
                [$"{subject.EntryName}: {label} {currentRatio:F3} 超基线 {baselineRatio.Value:F3} 的 +{thresholdPct:F0}%"]);
        }
        return new RatioVerdict(text, "OK", []);
    }
}

/// <summary>一条比值判定的被测对象与其同轮对照。</summary>
/// <param name="EntryName">被判定基准的全名（用于失败消息）。</param>
/// <param name="PeerName">对照基准全名（基线未记录对照时为 null）。</param>
/// <param name="Peer">对照基准本轮的测量值（缺失时为 null）。</param>
/// <param name="CurrentValue">被测基准本轮该维度的值。</param>
/// <param name="Select">从测量值中取出该维度（分配字节数 / 中位耗时）。</param>
internal sealed record RatioSubject(
    string EntryName,
    string? PeerName,
    BenchmarkMeasurement? Peer,
    double CurrentValue,
    Func<BenchmarkMeasurement, double> Select);

/// <summary>一条比值维度的判定结果。</summary>
/// <param name="Text">表格显示文本（本轮比值 / 基线比值）。</param>
/// <param name="Verdict">OK 或 FAIL。</param>
/// <param name="Failures">失败描述（通过时为空）。</param>
internal sealed record RatioVerdict(string Text, string Verdict, List<string> Failures);

/// <summary>极简命令行解析——只支持 <c>--key value</c> 与可重复的 <c>--key v1 v2 ...</c>。</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    private CommandLine() { }

    /// <summary>解析参数数组。</summary>
    public static CommandLine Parse(string[] args)
    {
        var line = new CommandLine();
        string? current = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                current = arg[2..];
                if (!line._values.ContainsKey(current)) line._values[current] = [];
            }
            else if (current is not null)
            {
                line._values[current].Add(arg);
            }
        }
        return line;
    }

    /// <summary>取必填单值。</summary>
    public string Require(string key)
        => Optional(key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"缺少必填参数 --{key}");

    /// <summary>取可选单值（缺省为空串）。</summary>
    public string Optional(string key)
        => _values.TryGetValue(key, out List<string>? values) && values.Count > 0 ? values[0] : "";

    /// <summary>取可重复的多值。</summary>
    public IEnumerable<string> Many(string key)
        => _values.TryGetValue(key, out List<string>? values) ? values : [];
}
