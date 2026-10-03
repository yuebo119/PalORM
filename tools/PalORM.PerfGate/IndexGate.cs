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

    /// <summary>非可比项判定：并发吞吐项（名带 <c>Concurrent_</c>）的比值不参与门禁。
    /// <para>它的 Ratio 是**每操作延迟**，批内散布由线程调度与服务器时段支配——
    /// 实测同批同组三臂一致性良好（MySQL/PG 三臂同为 ~1.7）而单点可跳到 9×
    ///（MySQL/20000/t8/PalORM 中位 4.58s vs 同组 0.51s）。录进基线会制造反向地雷：
    /// 上界被抬高后该项永不 FAIL，真实退化反而看不见。绝对值（中位/均值/分配）照常记录。</para></summary>
    private static bool IsIncomparableOperation(string name)
        => name.StartsWith("Concurrent_", StringComparison.Ordinal);

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
                MedianUs = entry.Value.Item.MedianUs,
                MeanUs = entry.Value.Item.MeanUs,
                Incomparable = IsIncomparableOperation(entry.Value.Item.Name),
                Note = IsIncomparableOperation(entry.Value.Item.Name)
                    ? "并发项：比值受线程调度与服务器时段支配，单点跳变可达 9×，不参与比值门禁（绝对值照登）"
                    : ""
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

        return JudgeBaseline(baseline, batches, LoadCurrentItems(batches));
    }

    /// <summary>逐键判定（Check 的主体）：比值超限且非分母漂移 → FAIL；分母漂移 → 告警；
    /// 末尾对"全部缺项"单独判失败（只跑冒烟/过滤批次时的防呆）。</summary>
    private static int JudgeBaseline(
        IndexBaseline baseline, List<PerfResultEnvelope> batches,
        Dictionary<string, (string Harness, PerfResultItem Item)> current)
    {
        var failures = new List<string>();
        var driftWarnings = new List<string>();
        int compared = 0, missing = 0, skipped = 0, floorDrift = 0;
        foreach (IndexBaselineItem expected in baseline.Items)
        {
            // 非可比项（并发族）跳过判定，也不计入缺项——它们的绝对值照常登在基线里供人看
            if (expected.Incomparable)
            {
                skipped++;
                continue;
            }

            if (!current.TryGetValue(
                Key(expected.Harness, expected.Name, expected.Dialect, expected.Tier),
                out (string Harness, PerfResultItem Item) found))
            {
                missing++;
                continue;
            }

            (double Ratio, double MedianUs, double MeanUs) actual =
                (found.Item.Ratio, found.Item.MedianUs, found.Item.MeanUs);

            compared++;
            double limit = expected.Ratio * (1 + baseline.Thresholds.RatioDelta);
            if (actual.Ratio <= limit)
            {
                continue;
            }

            // 分母漂移判定（2026-10-03 把人工定性协议机械化）：比值超限、但被测臂自身中位移动
            // ≤10% 且隐含地板变快 ≥20% 时，超限来自地板窗口而非产品（实测三例：中位 -3.7%/
            // -2.9%/-9.0%，地板 -63.5%/-33.0%/-32.9%）——只告警不 FAIL；真实退化的中位移动会
            // 远超 10%，仍走 FAIL。地板变慢方向只会压低比值，不会触发本门禁，故无须处理。
            if (IsDenominatorDrift(expected, actual))
            {
                floorDrift++;
                driftWarnings.Add(DriftText(expected, actual, limit));
                continue;
            }

            failures.Add(DescribeFailure(expected, actual, limit, baseline.Thresholds.RatioDelta));
        }

        ReportSentinel(batches);

        List<string> outliers = OutlierWarnings(batches);

        Console.WriteLine($"[PerfGate] 结果库门禁: 比对 {compared} 项（基线 {baseline.Items.Count} 项，"
            + $"缺项 {missing}，非可比跳过 {skipped}，分母漂移 {floorDrift}），失败 {failures.Count}");
        foreach (string failure in failures) Console.Error.WriteLine("  FAIL " + failure);
        foreach (string drift in driftWarnings) Console.Error.WriteLine("  漂移 " + drift);
        foreach (string outlier in outliers) Console.Error.WriteLine("  离群 " + outlier);

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

    /// <summary>被测臂中位与隐含地板的跨批移动幅度（与 <see cref="DescribeFailure"/> 同一算术，
    /// 供分母漂移判定与失败描述共用）。</summary>
    private static (double MedianDelta, double FloorDelta) Movement(
        IndexBaselineItem expected, (double Ratio, double MedianUs, double MeanUs) actual)
    {
        double floorBase = expected.Ratio > 0 ? expected.MedianUs / expected.Ratio : 0;
        double floorNow = actual.Ratio > 0 ? actual.MedianUs / actual.Ratio : 0;
        double medianDelta = expected.MedianUs > 0 ? ((actual.MedianUs / expected.MedianUs) - 1) * 100 : 0;
        double floorDelta = floorBase > 0 ? ((floorNow / floorBase) - 1) * 100 : 0;
        return (medianDelta, floorDelta);
    }

    /// <summary>分母漂移判定：比值超限、但被测臂自身中位移动 ≤10% 且隐含地板变快 ≥20%。
    /// 阈值出处与实测三例见 Check 内注释（2026-10-03）。</summary>
    private static bool IsDenominatorDrift(
        IndexBaselineItem expected, (double Ratio, double MedianUs, double MeanUs) actual)
    {
        (double medianDelta, double floorDelta) = Movement(expected, actual);
        return floorDelta <= -20 && Math.Abs(medianDelta) <= 10;
    }

    /// <summary>分母漂移告警文本（自解释：两侧比值、被测臂移动、地板移动与处置建议）。</summary>
    private static string DriftText(
        IndexBaselineItem expected, (double Ratio, double MedianUs, double MeanUs) actual, double limit)
    {
        (double medianDelta, double floorDelta) = Movement(expected, actual);
        return string.Create(CultureInfo.InvariantCulture,
            $"{expected.Name}/{expected.Dialect}/{expected.Tier}: 比值 {actual.Ratio:F2} 超基线 "
            + $"{expected.Ratio:F2}（限 {limit:F2}），但被测臂中位仅 {medianDelta:+0.0;-0.0}%、"
            + $"隐含地板 {floorDelta:+0.0;-0.0}%（地板变快推高比值）——判分母漂移，建议复测确认");
    }

    /// <summary>跨批离群告警（只告警、不参与判定与退出码）：最新非子集 perfhub 批次里，某
    /// (操作,方言,档,臂) 键的中位对照其**此前至多 6 批**的中位偏离超 2× 时列出。
    /// <para><b>为什么需要</b>：健康度只看 ADO 臂散布中位数、err 只测批内散布，对参考臂的
    /// 系统性偏移都不敏感（实测：Dapper 臂 BulkDelete/SQLite/2000 120.77ms，历批 21~22ms，
    /// 7.6×，两者都没抓到）。并发族排除（散布由调度支配，同 <see cref="IsIncomparableOperation"/>）。
    /// 合法优化落地的当批也会触发（如 UNNEST 的 −46~−74%），语义即"复测后再归因"。</para></summary>
    private static List<string> OutlierWarnings(List<PerfResultEnvelope> batchesDesc)
    {
        PerfResultEnvelope? newest = batchesDesc.FirstOrDefault(static b =>
            b.Harness == "perfhub" && !PerfResultWriter.IsSubsetLabel(b.Label));
        if (newest is null)
        {
            return [];
        }

        Dictionary<string, List<double>> history = MedianHistory(batchesDesc);
        var warnings = new List<string>();
        foreach (PerfResultItem item in newest.Items)
        {
            string? warning = OutlierText(item, history);
            if (warning is not null)
            {
                warnings.Add(warning);
            }
        }

        return warnings;
    }

    /// <summary>历史中位序列：键 = (操作,方言,档,臂)，值按时间升序（除最新批之外的非子集 perfhub 批次）。</summary>
    private static Dictionary<string, List<double>> MedianHistory(List<PerfResultEnvelope> batchesDesc)
    {
        var history = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (PerfResultEnvelope run in batchesDesc.Skip(1))
        {
            if (run.Harness != "perfhub" || PerfResultWriter.IsSubsetLabel(run.Label)) continue;
            foreach (PerfResultItem item in run.Items)
            {
                if (item.MedianUs <= 0) continue;
                string key = item.Name + "|" + item.Dialect + "|" + item.Tier + "|" + item.Arm;
                if (!history.TryGetValue(key, out List<double>? list))
                {
                    history[key] = list = [];
                }

                list.Add(item.MedianUs);
            }
        }

        return history;
    }

    /// <summary>单键离群判定：最新中位对照此前至多 6 批的中位，偏离超过 2 倍（两个方向）生成告警文本。</summary>
    private static string? OutlierText(PerfResultItem item, Dictionary<string, List<double>> history)
    {
        if (item.MedianUs <= 0 || IsIncomparableOperation(item.Name)) return null;
        string key = item.Name + "|" + item.Dialect + "|" + item.Tier + "|" + item.Arm;
        if (!history.TryGetValue(key, out List<double>? list) || list.Count == 0) return null;
        List<double> recent = [.. list.TakeLast(6).OrderBy(static x => x)];
        double reference = recent[recent.Count / 2];
        if (reference <= 0) return null;
        double factor = item.MedianUs / reference;
        if (factor is > 2.0 or < 0.5)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{item.Name}/{item.Dialect}/{item.Tier} [{item.Arm}]: 中位 {item.MedianUs:F1} µs "
                + $"vs 此前 {recent.Count} 批中位 {reference:F1} µs（{factor:F2}×）——疑似离群读数，复测后再归因");
        }

        return null;
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

    /// <summary>失败描述必须自解释：两侧中位数与隐含地板一起打印，否则无法分辨"本项退化"
    /// 与"地板移动"（实测先例：StreamAll 三臂都变快，但地板快得更多，比值上升 0.48→0.64）。
    /// <para>比值基数是中位数（2026-10-02），故隐含地板按中位数反推；均值另附一行供判读
    /// "是否只是计时离群"——两者大幅背离时（如 Insert/SQLite/2000 中位 17.1µs / 均值 45.6µs）
    /// 该批次的该项本身不可信，应复测而不是归因产品。</para></summary>
    private static string DescribeFailure(
        IndexBaselineItem expected, (double Ratio, double MedianUs, double MeanUs) actual,
        double limit, double ratioDelta)
    {
        (double medianDelta, double floorDelta) = Movement(expected, actual);
        double floorBase = expected.Ratio > 0 ? expected.MedianUs / expected.Ratio : 0;
        double floorNow = actual.Ratio > 0 ? actual.MedianUs / actual.Ratio : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"{expected.Harness}/{expected.Name}/{expected.Dialect}/{expected.Tier}: "
            + $"比值 {actual.Ratio:F2} 超过基线 {expected.Ratio:F2} × (1+{ratioDelta:P0}) = {limit:F2}"
            + $"｜本项中位 {expected.MedianUs:F2} → {actual.MedianUs:F2} µs（{medianDelta:+0.0;-0.0}%）"
            + $"｜隐含地板 {floorBase:F2} → {floorNow:F2} µs（{floorDelta:+0.0;-0.0}%）"
            + $"｜均值 {expected.MeanUs:F2} → {actual.MeanUs:F2} µs");
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
            // 中位为 0 说明该信封写于 2026-10-02 之前（MedianUs 字段尚不存在）——只报均值，
            // 不显示 "中位 0.00"（那会被读成"极快"）。
            Console.WriteLine(i.MedianUs > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"    {i.Name} 比值 {i.Ratio:F2}（中位 {i.MedianUs:F2} µs / 均值 {i.MeanUs:F2} µs）")
                : string.Create(CultureInfo.InvariantCulture,
                    $"    {i.Name} 比值 {i.Ratio:F2}（{i.MeanUs:F2} µs；均值口径，早于 2026-10-02）"));
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

    /// <summary>非可比项（该批次的该项无判别力，门禁跳过判定）。
    /// <para><b>为什么需要</b>：并发项（`Concurrent_Mixed80_20`）的比值是每操作延迟，
    /// 其批内散布由线程调度与服务器时段支配，单点跳变可达 9×（实测 MySQL/20000/t8/PalORM
    /// 4.58s vs 同组 0.51s）。把这种读数录进基线会制造**反向地雷**：上界被抬到
    /// 15.5× 后，该项从此永不 FAIL，真实退化反而看不见。</para>
    /// <para>与 PL-4 对 `Build*` 项"比值记 0"同族，区别是这里的绝对值（中位/均值/分配）
    /// 照常记录，只是不参与比值判定——数据保留、判读交给人。</para></summary>
    public bool Incomparable { get; set; }

    /// <summary>非可比原因（写进基线，供人工复核时不必回查批次）。</summary>
    public string Note { get; set; } = "";

    /// <summary>本项的绝对中位耗时（µs）——<see cref="Ratio"/> 的基数，也是失败时分辨
    /// "本项退化"与"地板移动"的依据：比值上升可能只是分母（同批地板）变快，看中位数才能判定方向。</summary>
    public double MedianUs { get; set; }

    /// <summary>本项的绝对均值（µs）——仅供判读"是否只是计时离群"，不参与比值。
    /// 与中位数大幅背离说明该批次的该项不可信。</summary>
    public double MeanUs { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(IndexBaseline))]
internal sealed partial class IndexBaselineJsonContext : JsonSerializerContext;
