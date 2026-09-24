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
        List<PerfResultEnvelope> batches = LoadBatches(resultsDir);
        if (batches.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: 结果库里没有可解析的批次（{resultsDir}）");
            return 1;
        }

        // 与 Check 共用 LoadCurrentItems：两侧同源，基线条目数与比对口径才对得上
        var current = LoadCurrentItems(batches);
        var baseline = new IndexBaseline
        {
            Schema = 1,
            Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // 标注必须指向**真正贡献了数据的批次**：条目是逐键从"非子集批次"里取最新值合并出来的
            // （见 KeyItems 的过滤），而 batches[0] 是含子集批次在内的最新一批——实测
            // 2026-09-23 用它标注时写的是 ab/1/pg/2000（被跳过、零贡献），指错了对象。
            Environment = DescribeEnvironment(NewestContributing(batches)),
            Thresholds = new IndexThresholds { RatioDelta = DefaultRatioThreshold },
            Items = [.. current.Select(static entry => new IndexBaselineItem
            {
                Harness = entry.Value.Harness,
                Name = entry.Value.Item.Name,
                Dialect = entry.Value.Item.Dialect,
                Tier = entry.Value.Item.Tier,
                Ratio = entry.Value.Item.Ratio,
                MeanUs = entry.Value.Item.MeanUs
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

        List<PerfResultEnvelope> batches = LoadBatches(resultsDir);
        if (batches.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: 结果库里没有可解析的批次（{resultsDir}）");
            return 1;
        }

        var current = LoadCurrentItems(batches);

        var failures = new List<string>();
        int compared = 0, missing = 0;
        foreach (IndexBaselineItem expected in baseline.Items)
        {
            if (!current.TryGetValue(
                Key(expected.Harness, expected.Name, expected.Dialect, expected.Tier),
                out (string Harness, PerfResultItem Item) found))
            {
                missing++;
                continue;
            }

            (double Ratio, double MeanUs) actual = (found.Item.Ratio, found.Item.MeanUs);

            compared++;
            double limit = expected.Ratio * (1 + baseline.Thresholds.RatioDelta);
            if (actual.Ratio > limit)
            {
                failures.Add(DescribeFailure(expected, actual, limit, baseline.Thresholds.RatioDelta));
            }
        }

        ReportSentinel(batches);

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

    /// <summary>当前比值表：**逐键取最新可得值**（批次按时间倒序，先到先得），同名键取最差比值。
    /// <para>为什么不是"只看最近一批"：最近一批可能只覆盖某个方言（实测：一次 MySQL 单方言跑测
    /// 让 50 项 SQLite 基线全部"缺项"，门禁误报 FATAL）。逐键取最新值后，只有"某个键在所有批次里
    /// 都没有"才算缺项。</para>
    /// <para>同名键取最差：结果库里同一键出现多次（历史遗留的同名并发行，或实现侧漏了唯一化标识）时，
    /// 取最差才是安全方向——门禁宁可提示也不该因重复键直接崩。</para></summary>
    /// <summary>当前项表：**逐键取最新可得项**（批次按时间倒序，先到先得），同名键取最差比值。
    /// <para>为什么不是"只看最近一批"：最近一批可能只覆盖某个方言（实测：一次 MySQL 单方言跑测
    /// 让 50 项 SQLite 基线全部"缺项"，门禁误报 FATAL）。逐键取最新值后，只有"某个键在所有批次里
    /// 都没有"才算缺项。</para>
    /// <para><b>录制与读回必须共用本方法</b>：两侧若用不同口径（例如录制遍历全部批次、读回取最新），
    /// 基线条目数与比对口径就对不上，门禁会因口径错配而红——本方法的存在就是为了让两者同源。</para>
    /// <para>同名键取最差：结果库里同一键出现多次（历史遗留的同名并发行，或实现侧漏了唯一化标识）时，
    /// 取最差才是安全方向——门禁宁可提示也不该因重复键直接崩。</para></summary>
    private static Dictionary<string, (string Harness, PerfResultItem Item)> LoadCurrentItems(
        List<PerfResultEnvelope> batches)
    {
        var map = new Dictionary<string, (string Harness, PerfResultItem Item)>(StringComparer.Ordinal);
        foreach (PerfResultEnvelope run in batches)
        {
            // 批内：同名键取最差（安全方向）。includeZeroRatio：比值 0 的键也要占位，
            // 否则"最新批次已判定该项不可比"无法表达，旧批次会把它复活（见 KeyItems 文档）。
            var perBatch = new Dictionary<string, (string Harness, PerfResultItem Item)>(StringComparer.Ordinal);
            foreach ((string harness, PerfResultItem item) in KeyItems([run], includeZeroRatio: true))
            {
                string key = Key(harness, item.Name, item.Dialect, item.Tier);
                if (!perBatch.TryGetValue(key, out (string Harness, PerfResultItem Item) existing)
                    || item.Ratio > existing.Item.Ratio)
                {
                    perBatch[key] = (harness, item);
                }
            }

            // 跨批次：**新的优先**（batches 已按时间倒序，先到先得），旧批次只补新批次缺的键。
            // 若这里改成"比值大者胜"，旧批次的高比值会盖掉最新值——那测的是历史最差而不是当前状态
            foreach (KeyValuePair<string, (string Harness, PerfResultItem Item)> entry in perBatch)
            {
                map.TryAdd(entry.Key, entry.Value);
            }
        }

        return map;
    }

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

    /// <summary>取进基线/速览的条目：PerfHub 的 PalORM 臂。
    /// <para><b><paramref name="includeZeroRatio"/> 是防复活开关</b>：比值记为 0 的项
    /// （PL-4 起 <c>Build*</c> 这类"地板不干活、对比不成立"的项就是 0）在
    /// <see cref="LoadCurrentItems"/> 里也必须占位——否则最新批次"知道这个键不可比"这件事
    /// 无法表达，旧批次里该项的高比值会把键复活，基线又被灌进一个已知无判别力的比值
    /// （2026-09-24 实测：PL-4 之后录出的 152 项基线里仍有 12 个 Build 项）。</para>
    /// <para>速览/哨兵这类只给人看的路径传 false——比值 0 的项没有可比信息，列出来是噪声。</para></summary>
    private static List<(string Harness, PerfResultItem Item)> KeyItems(
        List<PerfResultEnvelope> latest, bool includeZeroRatio = false)
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
                if (item.Arm.Equals("PalORM", StringComparison.OrdinalIgnoreCase)
                    && (includeZeroRatio || item.Ratio > 0))
                {
                    items.Add((run.Harness, item));
                }
            }
        }

        return items;
    }

    /// <summary>每个夹具取最近一批（含 quick 标记的批次由调用方过滤）。</summary>
    /// <summary>最新一个**为基线贡献条目的**批次——只有 PerfHub 的 PalORM 臂进基线
    /// （见 <see cref="KeyItems"/>），故须同时限定 harness 与"非子集"。
    /// 少限定 harness 会指向 DapperSuite 批次（实测：它比 PerfHub 批次晚 3 分钟），
    /// 少限定子集会指向 ab 块（被跳过、零贡献）。都不满足时退回最新批。</summary>
    private static PerfResultEnvelope NewestContributing(List<PerfResultEnvelope> batches)
        => batches.FirstOrDefault(static r =>
            r.Harness == "perfhub" && !PerfResultWriter.IsSubsetLabel(r.Label)) ?? batches[0];

    /// <summary>环境一句话——写进基线的 environment 字段。
    /// 条目是**逐键合并**自多个非子集批次的（见 <see cref="KeyItems"/>），故这里只说
    /// "最新贡献批次"，不说"录制批次"——后者会让人以为整份基线来自单一批次。</summary>
    private static string DescribeEnvironment(PerfResultEnvelope run)
        => string.Create(CultureInfo.InvariantCulture,
            $"{run.Environment.Processor} / {run.Environment.Runtime} / GC {run.Environment.GcMode}；"
            + $"条目逐键合并自非子集批次，最新贡献批次 {run.Timestamp}"
            + $"（健康度 {run.Regime.Health} {run.Regime.HealthRatio:P1}）");

    /// <summary>结果库全部批次（按时间倒序）——门禁逐键取最新可得值，故需要全量而非只读 latest-*。
    /// 子集标记批次在 <see cref="KeyItems"/> 里被过滤掉。</summary>
    private static List<PerfResultEnvelope> LoadBatches(string resultsDir)
    {
        var runs = new List<PerfResultEnvelope>();
        if (!Directory.Exists(resultsDir)) return runs;
        foreach (string file in Directory.EnumerateFiles(resultsDir, "*.json"))
        {
            // latest-* 是副本，读了会让同一批出现两次
            if (Path.GetFileName(file).StartsWith("latest-", StringComparison.Ordinal)) continue;
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

        runs.Sort(static (a, b) => string.CompareOrdinal(b.Timestamp, a.Timestamp));
        return runs;
    }

    private static string Key(string harness, string name, string dialect, int tier)
        => string.Create(CultureInfo.InvariantCulture, $"{harness}|{name}|{dialect}|{tier}");
}

internal sealed class IndexBaseline
{
    public int Schema { get; set; } = 1;
    public string Date { get; set; } = "";

    /// <summary>录制时的环境说明（进程数、健康度、宿主负载状态）——比值门禁隐含"环境可比"假设，
    /// 实测宿主机跑着数据库虚机时 SQLite 进程内项整体变慢（地板 +45%、CPU 密集项 +156%），
    /// 故环境必须与基线同存亡，否则失败无法区分"退化"与"换了环境"。</summary>
    public string Environment { get; set; } = "";

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
