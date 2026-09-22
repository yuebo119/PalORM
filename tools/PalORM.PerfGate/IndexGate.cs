using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalORM.Bench.Shared;

namespace PalORM.PerfGate;

/// <summary>结果库门禁（规范 v2 §5/§6）——把结果库里**能力矩阵夹具（PerfHub）**的 PalORM 比值
/// 与基线比对，卡住"ORM 对照项回归"。与 <see cref="Program"/> 的 BDN 门禁分工：
/// 那个卡微基准（形状敏感性），这个卡跨方言能力矩阵。
/// <para><b>DapperSuite 只作哨兵</b>：官方形状与档位固定，不适合卡回归阈值（规范 §1.1），
/// 它的存在价值是"换驱动/换大版本时"提醒人复跑并人工判读，故本门禁只报告不卡它。</para>
/// <para>阈值沿用微基准门禁的纪律：比值恶化 &gt; 30%（实测噪声 ±11% 的 2.7 倍，规范 §4）。</para></summary>
internal static class IndexGate
{
    private const double DefaultRatioThreshold = 0.30;

    internal static int Record(string resultsDir, string outputPath)
    {
        List<PerfResultEnvelope> latest = LoadLatest(resultsDir);
        if (latest.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: 结果库里没有可解析的批次（{resultsDir}）");
            return 1;
        }

        var baseline = new IndexBaseline
        {
            Schema = 1,
            Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Thresholds = new IndexThresholds { RatioDelta = DefaultRatioThreshold },
            Items = [.. KeyItems(latest).Select(static i => new IndexBaselineItem
            {
                Harness = i.Harness,
                Name = i.Item.Name,
                Dialect = i.Item.Dialect,
                Tier = i.Item.Tier,
                Ratio = i.Item.Ratio,
                MeanUs = i.Item.MeanUs
            })]
        };

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(baseline, IndexBaselineJsonContext.Default.IndexBaseline));
        Console.WriteLine($"[PerfGate] 结果库基线已写入 {outputPath}（{baseline.Items.Count} 项，"
            + $"阈值 比值 +{baseline.Thresholds.RatioDelta:P0}）");
        return 0;
    }

    internal static int Check(string resultsDir, string baselinePath)
    {
        if (!File.Exists(baselinePath))
        {
            Console.Error.WriteLine($"FATAL: 基线不存在: {baselinePath}");
            return 1;
        }

        IndexBaseline? baseline = JsonSerializer.Deserialize(
            File.ReadAllText(baselinePath), IndexBaselineJsonContext.Default.IndexBaseline);
        if (baseline is null || baseline.Schema != 1)
        {
            Console.Error.WriteLine($"FATAL: 基线 schema 不符（要求 1）: {baselinePath}");
            return 1;
        }

        List<PerfResultEnvelope> latest = LoadLatest(resultsDir);
        if (latest.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: 结果库里没有可解析的批次（{resultsDir}）");
            return 1;
        }

        var current = LoadCurrentRatios(latest);

        var failures = new List<string>();
        int compared = 0, missing = 0;
        foreach (IndexBaselineItem expected in baseline.Items)
        {
            if (!current.TryGetValue(
                Key(expected.Harness, expected.Name, expected.Dialect, expected.Tier),
                out (double Ratio, double MeanUs) actual))
            {
                missing++;
                continue;
            }

            compared++;
            double limit = expected.Ratio * (1 + baseline.Thresholds.RatioDelta);
            if (actual.Ratio > limit)
            {
                failures.Add(DescribeFailure(expected, actual, limit, baseline.Thresholds.RatioDelta));
            }
        }

        ReportSentinel(latest);

        Console.WriteLine($"[PerfGate] 结果库门禁: 比对 {compared} 项（基线 {baseline.Items.Count} 项，"
            + $"缺项 {missing}），失败 {failures.Count}");
        foreach (string failure in failures) Console.Error.WriteLine("  FAIL " + failure);

        // 缺项不判失败：基线里的项可能因本轮方言/档位未跑而缺席（例如只跑了 sqlite）。
        // 但"全部缺项"说明没有可引用的批次（只跑了冒烟/过滤批次，或跑错了夹具），按失败处理。
        if (failures.Count == 0 && compared == 0)
        {
            Console.Error.WriteLine("FATAL: 没有任何可比对项——检查本轮跑的是不是同一套夹具与方言，"
                + "以及结果库里是否存在**非子集标记**的批次（冒烟批次带 quick/filtered 标记，门禁会跳过）");
            return 1;
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>当前比值表：同名键取**最差比值**。结果库里同一键出现多次（历史遗留的同名并发行，
    /// 或实现侧漏了唯一化标识）时，取最差才是安全方向——门禁宁可提示也不该因重复键直接崩。</summary>
    private static Dictionary<string, (double Ratio, double MeanUs)> LoadCurrentRatios(List<PerfResultEnvelope> latest)
        => KeyItems(latest)
            .GroupBy(static i => Key(i.Harness, i.Item.Name, i.Item.Dialect, i.Item.Tier))
            .ToDictionary(
                static g => g.Key,
                static g =>
                {
                    (_, PerfResultItem worst) = g.MaxBy(static i => i.Item.Ratio);
                    return (worst.Ratio, worst.MeanUs);
                });

    /// <summary>失败描述必须自解释：两侧均值与隐含地板一起打印，否则无法分辨"本项退化"
    /// 与"地板移动"（实测先例：StreamAll 三臂都变快，但地板快得更多，比值上升 0.48→0.64）。</summary>
    private static string DescribeFailure(
        IndexBaselineItem expected, (double Ratio, double MeanUs) actual, double limit, double ratioDelta)
    {
        double floorBase = expected.Ratio > 0 ? expected.MeanUs / expected.Ratio : 0;
        double floorNow = actual.Ratio > 0 ? actual.MeanUs / actual.Ratio : 0;
        double meanDelta = expected.MeanUs > 0 ? ((actual.MeanUs / expected.MeanUs) - 1) * 100 : 0;
        double floorDelta = floorBase > 0 ? ((floorNow / floorBase) - 1) * 100 : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"{expected.Harness}/{expected.Name}/{expected.Dialect}/{expected.Tier}: "
            + $"比值 {actual.Ratio:F2} 超过基线 {expected.Ratio:F2} × (1+{ratioDelta:P0}) = {limit:F2}"
            + $"｜本项均值 {expected.MeanUs:F2} → {actual.MeanUs:F2} µs（{meanDelta:+0.0;-0.0}%）"
            + $"｜隐含地板 {floorBase:F2} → {floorNow:F2} µs（{floorDelta:+0.0;-0.0}%）");
    }

    /// <summary>哨兵报告：DapperSuite 不卡阈值，但它的健康度与地板比值要打印出来供人工判读。</summary>
    private static void ReportSentinel(List<PerfResultEnvelope> latest)
    {
        PerfResultEnvelope? sentinel = latest.Find(static r => r.Harness == "dappersuite");
        if (sentinel is null) return;
        List<PerfResultItem> palorm = [.. sentinel.Items.Where(static i =>
            i.Arm.Equals("PalORM", StringComparison.OrdinalIgnoreCase) && i.Ratio > 0)];
        if (palorm.Count == 0) return;
        Console.WriteLine($"[PerfGate] 哨兵（不卡阈值）DapperSuite {sentinel.Timestamp} `{sentinel.Commit}` "
            + $"健康度 {sentinel.Regime.Health}:");
        foreach (PerfResultItem i in palorm.OrderBy(static i => i.Ratio))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {i.Name} 比值 {i.Ratio:F2}（{i.MeanUs:F2} µs）"));
        }
    }

    private static List<(string Harness, PerfResultItem Item)> KeyItems(List<PerfResultEnvelope> latest)
    {
        var items = new List<(string, PerfResultItem)>();
        foreach (PerfResultEnvelope run in latest)
        {
            // 只有 PerfHub 的 PalORM 臂进基线：微基准由 BDN 门禁覆盖，DapperSuite 只作哨兵
            if (run.Harness != "perfhub") continue;
            if (PerfResultWriter.IsSubsetLabel(run.Label))
            {
                Console.Error.WriteLine($"[PerfGate] 跳过子集批次（label={run.Label}）: {run.Timestamp}");
                continue;
            }

            foreach (PerfResultItem item in run.Items)
            {
                if (item.Arm.Equals("PalORM", StringComparison.OrdinalIgnoreCase) && item.Ratio > 0)
                {
                    items.Add((run.Harness, item));
                }
            }
        }

        return items;
    }

    /// <summary>每个夹具取最近一批（含 quick 标记的批次由调用方过滤）。</summary>
    private static List<PerfResultEnvelope> LoadLatest(string resultsDir)
    {
        var runs = new List<PerfResultEnvelope>();
        if (!Directory.Exists(resultsDir)) return runs;
        foreach (string file in Directory.EnumerateFiles(resultsDir, "latest-*.json"))
        {
            try
            {
                PerfResultEnvelope? run = JsonSerializer.Deserialize(
                    File.ReadAllText(file), PerfResultJsonContext.Default.PerfResultEnvelope);
                if (run is not null) runs.Add(run);
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"[PerfGate] 跳过无法解析的结果文件: {file}");
            }
        }

        return runs;
    }

    private static string Key(string harness, string name, string dialect, int tier)
        => string.Create(CultureInfo.InvariantCulture, $"{harness}|{name}|{dialect}|{tier}");
}

internal sealed class IndexBaseline
{
    public int Schema { get; set; } = 1;
    public string Date { get; set; } = "";
    public IndexThresholds Thresholds { get; set; } = new();
    public List<IndexBaselineItem> Items { get; set; } = [];
}

internal sealed class IndexThresholds
{
    /// <summary>比值恶化容忍度（相对基线的比例）。</summary>
    public double RatioDelta { get; set; } = 0.30;
}

internal sealed class IndexBaselineItem
{
    public string Harness { get; set; } = "";
    public string Name { get; set; } = "";
    public string Dialect { get; set; } = "";
    public int Tier { get; set; }
    public double Ratio { get; set; }

    /// <summary>本项的绝对均值（µs）——失败时用来分辨"本项退化"与"地板移动"：
    /// 比值上升可能只是分母（同批地板）变快，看均值才能判定方向。</summary>
    public double MeanUs { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(IndexBaseline))]
internal sealed partial class IndexBaselineJsonContext : JsonSerializerContext;
