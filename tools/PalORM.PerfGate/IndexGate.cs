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

    /// <summary>判别力弱阈值——沿用 <c>Measure.cs</c> 的量具自检线（Error/Mean &gt; 5% 标黄）。
    /// 同键三臂里最大 ErrorRatio 超线时，该行比值落在噪声带内、不足以支撑结论；
    /// 超限幅度小于该水位时判定降级为告警（见 <see cref="Classify"/>）。
    /// 取三臂最大值而非只看被测臂：比值 = 被测臂 / 地板，两侧噪声都会放大比值的不确定度。</summary>
    private const double NoiseLine = 0.05;

    /// <summary>门禁输出里最多展开几条判别力弱项（总数照报，避免刷屏）。</summary>
    private const int NoiseWarnPreview = 8;

    /// <summary>非可比项判定：并发吞吐项（名带 <c>Concurrent_</c>）的比值不参与门禁。
    /// <para>它的 Ratio 是**每操作延迟**，批内散布由线程调度与服务器时段支配——
    /// 实测同批同组三臂一致性良好（MySQL/PG 三臂同为 ~1.7）而单点可跳到 9×
    ///（MySQL/20000/t8/PalORM 中位 4.58s vs 同组 0.51s）。录进基线会制造反向地雷：
    /// 上界被抬高后该项永不 FAIL，真实退化反而看不见。绝对值（中位/均值/分配）照常记录。</para></summary>
    private static bool IsIncomparableOperation(string name)
        => name.StartsWith("Concurrent_", StringComparison.Ordinal);

    /// <summary>默认录制窗口：逐键取最近 N 个非子集批次的中位数。
    /// <para><b>为什么不是单批</b>（2026-10-19）：单批录制会把批次级噪声烤进基线，下一批判红。
    /// 实测两例：①09:21 批 PG 2000 落入服务器慢窗（GetAllAsync 3.11ms vs 常态 736µs），
    /// 若录成基线，后续每批都会被判"变快 4 倍"从而掩盖真实退化；②09:42 批 SQLite 2000
    /// 整段偏慢（ADO 地板 +40%、PalORM 批内再叠 +40%），录成基线则该项阈值被抬到几乎失效。
    /// 三批中位数对单批离群不敏感，且与门禁"比值摆动 ±8pp"的噪声模型同量级。</para>
    /// <para><c>--batches 1</c> 复现旧行为（与历史基线可比时用）。</para></summary>
    internal const int DefaultRecordWindow = 3;

    internal static int Record(string resultsDir, string outputPath, int batchWindow)
    {
        List<PerfResultEnvelope> batches = LoadBatches(resultsDir);
        if (batches.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: 结果库里没有可解析的批次（{resultsDir}）");
            return 1;
        }

        if (batchWindow < 1)
        {
            Console.Error.WriteLine($"FATAL: --batches 必须 ≥1（收到 {batchWindow}）");
            return 1;
        }

        // 与 Check 共用 LoadWindowItems：两侧同源同窗口（录制取窗口中位数进基线，
        // 判定取窗口中位数作当前值），基线条目数与比对口径才对得上。
        var current = LoadWindowItems(batches, batchWindow);
        List<string> contributors = ContributingLabels(batches, batchWindow);
        var baseline = new IndexBaseline
        {
            Schema = 1,
            Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // 标注必须指向**真正贡献了数据的批次**：条目是逐键从"非子集批次"里取中位数合并出来的
            // （见 KeyItems 的过滤），而 batches[0] 是含子集批次在内的最新一批——实测
            // 2026-09-23 用它标注时写的是 ab/1/pg/2000（被跳过、零贡献），指错了对象。
            Environment = DescribeEnvironment(NewestContributing(batches), contributors),
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
                AllocBytes = entry.Value.Item.AllocBytes,
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
            + $"阈值 比值 +{baseline.Thresholds.RatioDelta:P0}，"
            + $"录制窗口 {batchWindow} 批：{string.Join("、", contributors)}）");
        return 0;
    }

    internal static int Check(string resultsDir, string baselinePath, int batchWindow)
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

        return JudgeBaseline(baseline, batches, LoadWindowItems(batches, batchWindow));
    }

    /// <summary>L0 基线录制：只取 <c>verify-l0</c> 子集批次，逐键取最近 N 批中位数。
    /// <para>与全量索引基线分开的原因是上下文不同：L0 只跑 6 个操作、单档位，它在夹具里的
    /// 进场顺序与表状态与全量批不同（2026-10-24 探针实测子集与全量的逐项偏差可达 100%），
    /// 拿全量基线判 L0 会引入口径差。L0 基线只与 L0 批次比，自洽。</para></summary>
    internal static int RecordL0(string resultsDir, string outputPath, int batchWindow)
    {
        List<PerfResultEnvelope> batches = [.. LoadBatches(resultsDir)
            .Where(static b => b.Label == PerfL0.Label)];
        if (batches.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: 结果库里没有 label={PerfL0.Label} 的批次（先跑 PerfCli l0 若干轮）");
            return 1;
        }
        if (batchWindow < 1)
        {
            Console.Error.WriteLine($"FATAL: --batches 必须 ≥1（收到 {batchWindow}）");
            return 1;
        }

        var current = LoadWindowItems(batches, batchWindow, includeSubsets: true);
        List<IndexBaselineItem> items = [.. current
            .Where(static entry => PerfL0.Contains(entry.Value.Item.Name)
                && entry.Value.Item.Tier == PerfL0.Tier)
            .Select(static entry => new IndexBaselineItem
            {
                Harness = entry.Value.Harness,
                Name = entry.Value.Item.Name,
                Dialect = entry.Value.Item.Dialect,
                Tier = entry.Value.Item.Tier,
                Ratio = entry.Value.Item.Ratio,
                MedianUs = entry.Value.Item.MedianUs,
                MeanUs = entry.Value.Item.MeanUs,
                AllocBytes = entry.Value.Item.AllocBytes
            })];
        if (items.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: 最近 {batchWindow} 个 L0 批次里没有覆盖项（检查 --ops 与档位）");
            return 1;
        }

        var baseline = new IndexBaseline
        {
            Schema = 1,
            Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Environment = $"L0 快线基线：逐键取最近 {batchWindow} 个 {PerfL0.Label} 批次的中位数"
                + $"（{string.Join("、", batches.Take(batchWindow).Select(static b => b.Timestamp))}）",
            Thresholds = new IndexThresholds(),
            Items = items
        };
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(baseline, IndexBaselineJsonContext.Default.IndexBaseline));
        Console.WriteLine($"[PerfGate] L0 基线已写入 {outputPath}（{items.Count} 项，"
            + $"阈值 比值 +{baseline.Thresholds.RatioDelta:P0} / 分配 +{baseline.Thresholds.AllocDelta:P0}，"
            + $"窗口 {batchWindow} 个 L0 批次）");
        return 0;
    }

    /// <summary>L0 判定：取最新一个 <c>verify-l0</c> 批次，比 L0 基线的比值与分配。
    /// <para>覆盖度是硬门禁：三方言缺一即 FAIL——L0 的价值是"关键项在三个方言上的快速反馈"，
    /// 单方言批次被读成通过等于静默缩水（红线 #3）。</para></summary>
    internal static int CheckL0(string resultsDir, string baselinePath)
    {
        if (!File.Exists(baselinePath))
        {
            Console.Error.WriteLine($"FATAL: L0 基线不存在: {baselinePath}（先跑 PerfGate record-l0）");
            return 1;
        }

        IndexBaseline? baseline = JsonSerializer.Deserialize(
            File.ReadAllText(baselinePath), IndexBaselineJsonContext.Default.IndexBaseline);
        if (baseline is null || baseline.Schema != 1)
        {
            Console.Error.WriteLine($"FATAL: L0 基线 schema 不符（要求 1）: {baselinePath}");
            return 1;
        }

        List<IndexBaselineItem> l0Items = [.. baseline.Items
            .Where(static i => PerfL0.Contains(i.Name) && i.Tier == PerfL0.Tier)];
        if (l0Items.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: L0 基线里没有覆盖项（先跑 PerfGate record-l0）");
            return 1;
        }

        List<PerfResultEnvelope> batches = LoadBatches(resultsDir);
        List<PerfResultEnvelope> l0Batches = [.. batches
            .Where(static b => b.Label == PerfL0.Label)
            .Take(IndexGate.DefaultRecordWindow)];
        if (l0Batches.Count == 0)
        {
            Console.Error.WriteLine($"FATAL: 结果库里没有 label={PerfL0.Label} 的批次（先跑 PerfCli l0）");
            return 1;
        }

        PerfResultEnvelope batch = l0Batches[0];
        string[] required = ["SQLite", "MySQL", "PostgreSQL"];
        List<string> present = [.. batch.Items
            .Where(static i => i.Arm.Equals("PalORM", StringComparison.OrdinalIgnoreCase)
                && PerfL0.Contains(i.Name) && i.Tier == PerfL0.Tier && i.Ratio > 0)
            .Select(static i => i.Dialect)
            .Distinct(StringComparer.Ordinal)];
        List<string> missingDialects = [.. required.Where(d => !present.Contains(d, StringComparer.Ordinal))];
        if (missingDialects.Count > 0)
        {
            Console.Error.WriteLine($"[PerfGate] FAIL L0 覆盖不完整：缺方言 {string.Join('/', missingDialects)}"
                + $"（本批 {string.Join('/', present)}）——L0 必须三方言齐跑");
            return 1;
        }

        var l0Baseline = new IndexBaseline
        {
            Schema = 1,
            Date = baseline.Date,
            Environment = baseline.Environment,
            Thresholds = baseline.Thresholds,
            Items = l0Items
        };
        // 判定与全量门禁同源：取最近 3 个 L0 批次的窗口中位数（单批离群被吸收，持续回归需
        // 连续 2 批才越线，B203）。单批尖峰不判失败是有意的——L0 的目标是持续回归，不是抖动。
        var current = LoadWindowItems(l0Batches, IndexGate.DefaultRecordWindow, includeSubsets: true);
        // L0 严格口径：不做"噪声带内不判失败"降级（B202 的降级是为全量批避免单批假 FAIL；
        // L0 已是 3 批窗口，剩下的超限按失败处理；分母漂移保护仍保留）。
        Dictionary<string, double> noise = [];

        Console.WriteLine($"[PerfGate] L0 判定：窗口 {l0Batches.Count} 批"
            + $"（最新 {batch.Timestamp} label={batch.Label}），方言 {string.Join('/', present)}，覆盖 {l0Items.Count} 键");
        return JudgeBaseline(l0Baseline, l0Batches, current, checkAllocation: true, noiseOverride: noise);
    }

    /// <summary>比值超限的三分类判定与文案（从判定主循环抽出，控制主循环复杂度）。
    /// <para>返回 <see cref="OverrunVerdict.FloorDrift"/>/<see cref="OverrunVerdict.WithinNoise"/> 时
    /// 调用方只告警；返回 <see cref="OverrunVerdict.Fail"/> 时 message 是完整失败描述（含锚定后缀）。</para></summary>
    private static OverrunVerdict JudgeRatio(
        IndexBaselineItem expected, (double Ratio, double MedianUs, double MeanUs) actual,
        double limit, double ratioDelta, double noiseLevel, string anchorSuffix, out string message)
    {
        switch (Classify(expected, actual, limit, noiseLevel))
        {
            case OverrunVerdict.FloorDrift:
                message = DriftText(expected, actual, limit);
                return OverrunVerdict.FloorDrift;
            case OverrunVerdict.WithinNoise:
                message = NoiseBandText(expected, actual, limit, noiseLevel);
                return OverrunVerdict.WithinNoise;
            default:
                message = DescribeFailure(expected, actual, limit, ratioDelta) + anchorSuffix;
                return OverrunVerdict.Fail;
        }
    }

    /// <summary>L0 分配判定：PalORM 每操作分配超基线 <see cref="IndexThresholds.AllocDelta"/> 记 FAIL。
    /// 单项缺失（旧基线 AllocBytes=0）跳过，保证向后兼容。</summary>
    private static void JudgeAllocation(
        IndexBaseline baseline, IndexBaselineItem expected, PerfResultItem actual, List<string> failures)
    {
        if (expected.AllocBytes <= 0 || actual.AllocBytes <= 0)
        {
            return;
        }
        double limit = expected.AllocBytes * (1 + baseline.Thresholds.AllocDelta);
        if (actual.AllocBytes <= limit)
        {
            return;
        }
        double over = (double)actual.AllocBytes / expected.AllocBytes - 1;
        failures.Add(string.Create(CultureInfo.InvariantCulture,
            $"分配 {expected.Name}/{expected.Dialect}/{expected.Tier}: "
            + $"{actual.AllocBytes} B vs 基线 {expected.AllocBytes} B"
            + $"（+{over:P1}，阈值 +{baseline.Thresholds.AllocDelta:P0}）"));
    }

    /// <summary>逐键判定（Check 的主体）：比值超限且非分母漂移 → FAIL；分母漂移 → 告警；
    /// 末尾对"全部缺项"单独判失败（只跑冒烟/过滤批次时的防呆）。
    /// <para>判别力弱标注（2026-10-04）：同键三臂最大 ErrorRatio &gt; 5% 的项在末尾汇总——
    /// 只标注不判定，比值超限与否仍走原逻辑。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S3776:CognitiveComplexity",
        Justification = "逐键判定是顺序分派形态（不可比/缺项/比值三分类/分配检查），无嵌套业务逻辑；"
            + "2026-10-24 加入 L0 分配判定后超过 15，继续拆分会把计数与文案状态拆散到多个方法。")]
    private static int JudgeBaseline(
        IndexBaseline baseline, List<PerfResultEnvelope> batches,
        Dictionary<string, (string Harness, PerfResultItem Item)> current,
        bool checkAllocation = false, Dictionary<string, double>? noiseOverride = null)
    {
        var failures = new List<string>();
        var driftWarnings = new List<string>();
        var noiseWarnings = new List<string>();
        // 噪声水位表只取一次：判别力弱清单与"噪声带内不判失败"共用同一口径
        // L0 传入的是"仅本批"的水位（子集批次默认被跳过，L0 判定要靠自身 ErrorRatio 划噪声带）。
        Dictionary<string, double> noise = noiseOverride ?? LoadNoiseLevels(batches);
        List<string> weakWarnings = CollectWeakDiscrimination(baseline, noise);
        // 锚定诊断在判定循环前算好：失败描述要附锚定比值（B175 判读法，见 CrossAnchorDiagnostics）
        Dictionary<string, AnchorLine> anchorNotes = CrossAnchorDiagnostics(batches);
        int compared = 0, missing = 0, skipped = 0, floorDrift = 0, noiseBand = 0;
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
            string key = Key(expected.Harness, expected.Name, expected.Dialect, expected.Tier);
            double limit = expected.Ratio * (1 + baseline.Thresholds.RatioDelta);
            if (actual.Ratio > limit)
            {
                // 分母漂移判定（2026-10-03 把人工定性协议机械化）：比值超限、但被测臂自身中位移动
                // ≤10% 且隐含地板变快 ≥20% 时，超限来自地板窗口而非产品（实测三例：中位 -3.7%/
                // -2.9%/-9.0%，地板 -63.5%/-33.0%/-32.9%）——只告警不 FAIL；真实退化的中位移动会
                // 远超 10%，仍走 FAIL。地板变慢方向只会压低比值，不会触发本门禁，故无须处理。
                switch (JudgeRatio(expected, actual, limit, baseline.Thresholds.RatioDelta,
                    noise.TryGetValue(key, out double level) ? level : 0,
                    AnchorSuffix(key, anchorNotes), out string message))
                {
                    case OverrunVerdict.FloorDrift:
                        floorDrift++;
                        driftWarnings.Add(message);
                        break;
                    case OverrunVerdict.WithinNoise:
                        noiseBand++;
                        noiseWarnings.Add(message);
                        break;
                    default:
                        failures.Add(message);
                        break;
                }
            }

            // 分配判定（仅 L0）：确定性指标，不设噪声带——分配上升 20% 就是回归。
            if (checkAllocation)
            {
                JudgeAllocation(baseline, expected, found.Item, failures);
            }
        }

        ReportSentinel(batches);

        List<string> outliers = OutlierWarnings(batches);

        PrintVerdict(
            (compared, baseline.Items.Count, missing, skipped, floorDrift, noiseBand),
            failures, driftWarnings, noiseWarnings, weakWarnings, outliers,
            [.. anchorNotes.Values.Select(static line => line.Text)]);

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

    /// <summary>分母漂移判定：比值超限、但被测臂自身中位未变慢（≤+10%）且隐含地板变快 ≥20%。
    /// 阈值出处与实测三例见 Check 内注释（2026-10-03）。
    /// <para><b>2026-10-19 修正</b>：原判据取 <c>Math.Abs(medianDelta) &lt;= 10</c>，把"被测臂
    /// 变快"也排除在漂移之外——但被测臂变快同样不是回归，比值被推高纯由地板变快造成。
    /// 实测两例（全量批 2026-10-19）：BulkInsert/MySQL/20000 中位 -27.0%、地板 -44.3%；
    /// BulkUpdate/PG/20000 中位 +5.6%、地板 -52.7%——两项绝对性能均未退化，却因中位变快
    /// 超过 10% 被误判为 FAIL。改为单侧判据：只有"变慢超过 10%"才可能是真回归。</para></summary>
    private static bool IsDenominatorDrift(
        IndexBaselineItem expected, (double Ratio, double MedianUs, double MeanUs) actual)
    {
        (double medianDelta, double floorDelta) = Movement(expected, actual);
        return floorDelta <= -20 && medianDelta <= 10;
    }

    /// <summary>分母漂移告警文本（自解释：两侧比值、被测臂移动、地板移动与处置建议）。</summary>
    private static string DriftText(
        IndexBaselineItem expected, (double Ratio, double MedianUs, double MeanUs) actual, double limit)
    {
        (double medianDelta, double floorDelta) = Movement(expected, actual);
        return string.Create(CultureInfo.InvariantCulture,
            $"{expected.Name}/{expected.Dialect}/{expected.Tier}: 比值 {actual.Ratio:F2} 超基线 "
            + $"{expected.Ratio:F2}（限 {limit:F2}），但被测臂窗口中位仅 {medianDelta:+0.0;-0.0}%、"
            + $"隐含地板 {floorDelta:+0.0;-0.0}%（地板变快推高比值）——判分母漂移，建议复测确认");
    }

    /// <summary>跨批离群告警（只告警、不参与判定与退出码）：最新非子集 perfhub 批次里，某
    /// (操作,方言,档,臂) 键的中位对照其**最近 3 个非子集批次**的中位偏离超 2× 时列出。
    /// <para><b>为什么需要</b>：健康度只看 ADO 臂散布中位数、err 只测批内散布，对参考臂的
    /// 系统性偏移都不敏感（实测：Dapper 臂 BulkDelete/SQLite/2000 120.77ms，历批 21~22ms，
    /// 7.6×，两者都没抓到）。并发族排除（散布由调度支配，同 <see cref="IsIncomparableOperation"/>）。
    /// 合法优化落地的当批也会触发（如 UNNEST 的 −46~−74%），语义即"复测后再归因"。</para>
    /// <para><b>窗口 6→3（2026-10-24）</b>：与判定窗口同源；且历史序列是**时间倒序**追加的，
    /// 原 <c>TakeLast(6)</c> 实际取到的是<b>最旧</b> 6 批，环境切换后会持续刷旧状态告警
    /// （实测 PG 快状态下多项 0.15~0.46× 告警）。改 <c>Take(3)</c> 取最近 3 批。</para></summary>
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

    /// <summary>单键离群判定：最新中位对照最近 3 批的中位，偏离超过 2 倍（两个方向）生成告警文本。</summary>
    private static string? OutlierText(PerfResultItem item, Dictionary<string, List<double>> history)
    {
        if (item.MedianUs <= 0 || IsIncomparableOperation(item.Name)) return null;
        string key = item.Name + "|" + item.Dialect + "|" + item.Tier + "|" + item.Arm;
        if (!history.TryGetValue(key, out List<double>? list) || list.Count == 0) return null;
        // 历史列表按时间倒序追加（MedianHistory 从 batchesDesc.Skip(1) 依次 Add），
        // 故"最近 N 批"用 Take(N)；TakeLast 会取到最旧的 N 批（2026-10-24 修正）。
        List<double> recent = [.. list.Take(3).OrderBy(static x => x)];
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

    /// <summary>同形态锚定对（B175）：ADO 臂跑**同一段代码**的操作族——夹具
    /// <c>Implementations.cs</c> 里 ADO 臂的 GetAllAsync 直接委托 QueryAllAsync
    /// （"地板与 QueryAll 同一写法"），两键的 ADO 地板读数理应一致；分叉即其中一侧
    /// 落入摆动窗口（实测 ADO 地板在 MySQL 单操作可摆 ±20%~2.9×，B175）。</summary>
    private static readonly (string A, string B)[] AnchorPairs = [("QueryAll", "GetAllAsync")];

    /// <summary>锚定事实：按伙伴侧地板重算的比值与两地板的分叉幅度（失败描述引用）。</summary>
    private readonly record struct AnchorFacts(double AnchoredRatio, double FloorDivergence);

    /// <summary>锚定告警行：事实 + 自解释文本（打印侧只管输出）。</summary>
    private sealed record AnchorLine(AnchorFacts Facts, string Text);

    /// <summary>同形态地板分叉的触发线（校准 2026-10-10：仓库 6 个全量批 × 3 方言 × 2 档共 38 观测，
    /// 安静带上界 12.4%（SQLite ≤6.3% / PG ≤12.4% / MySQL 多数 ≤5%），触发带下界 17.8%
    /// （6 例 MySQL + 1 例 PG——后者即 B170 的服务器空闲方差家族），15% 落在两带间隙中点）。</summary>
    private const double AnchorDivergenceLine = 0.15;

    /// <summary>失败描述里追加锚定比值（键有锚定条目时；无锚定返回空串）。</summary>
    private static string AnchorSuffix(
        string key, Dictionary<string, AnchorLine> anchorNotes)
        => anchorNotes.TryGetValue(key, out AnchorLine? line)
            ? string.Create(CultureInfo.InvariantCulture,
                $"｜锚定比值 {line.Facts.AnchoredRatio:F2}（同形态地板分叉 {line.Facts.FloorDivergence:P0}，判读以锚定值为准）")
            : "";

    /// <summary>批内交叉锚定诊断（B175 判读法机械化，2026-10-10）：最新非子集 PerfHub 批次里，
    /// 同形态对的两条 ADO 地板分叉 ≥<see cref="AnchorDivergenceLine"/> 时，两侧各生成一条
    /// "表观 vs 锚定"告警——该状态下表观比值无判别力，判读应以"地板贴近自身跨批中位"的一侧
    /// 为锚（B175：分子稳 + 分母暴摆 = 表观比值失真，借同形态稳定臂抢救分子真值；
    /// 实测 QueryAll/MySQL/2000 表观 1.25、锚定 1.05）。
    /// <para><b>只告警不判定</b>：与 IsDenominatorDrift 的跨批分母漂移互补——那边管"本批 vs 基线"
    /// 的地板移动，这边管"同批内两条同代码地板互相分叉"。判定放松需要跨批稳定性裁决，
    /// 本诊断不改变退出码（与离群/弱同纪律）。</para></summary>
    private static Dictionary<string, AnchorLine> CrossAnchorDiagnostics(
        List<PerfResultEnvelope> batchesDesc)
    {
        var notes = new Dictionary<string, AnchorLine>(StringComparer.Ordinal);
        PerfResultEnvelope? newest = batchesDesc.FirstOrDefault(static b =>
            b.Harness == "perfhub" && !PerfResultWriter.IsSubsetLabel(b.Label));
        if (newest is null)
        {
            return notes;
        }

        var adoFloors = new Dictionary<(string Name, string Dialect, int Tier), double>();
        foreach (PerfResultItem item in newest.Items)
        {
            if (item.Arm == "ADO_NET" && item.MedianUs > 0)
            {
                adoFloors[(item.Name, item.Dialect, item.Tier)] = item.MedianUs;
            }
        }

        foreach ((string a, string b) in AnchorPairs)
        {
            foreach (KeyValuePair<(string Name, string Dialect, int Tier), double> entry in adoFloors)
            {
                if (entry.Key.Name != a
                    || !adoFloors.TryGetValue((b, entry.Key.Dialect, entry.Key.Tier), out double floorB))
                {
                    continue;
                }

                double divergence = (Math.Max(entry.Value, floorB) / Math.Min(entry.Value, floorB)) - 1;
                if (divergence < AnchorDivergenceLine)
                {
                    continue;
                }

                AddAnchorLine(new AnchorContext(newest, batchesDesc, a, b,
                    entry.Key.Dialect, entry.Key.Tier, entry.Value, floorB, divergence), notes);
                AddAnchorLine(new AnchorContext(newest, batchesDesc, b, a,
                    entry.Key.Dialect, entry.Key.Tier, floorB, entry.Value, divergence), notes);
            }
        }

        return notes;
    }

    /// <summary>锚定行的求值输入（避免参数列表超 S107 上限）。</summary>
    private sealed record AnchorContext(
        PerfResultEnvelope Newest, List<PerfResultEnvelope> BatchesDesc,
        string Name, string Partner, string Dialect, int Tier,
        double OwnFloor, double PartnerFloor, double Divergence);

    /// <summary>单键锚定行：PalORM 臂存在且可读时写一条（键重复时后者覆盖——
    /// 每对每 (方言,档) 至多触发一次，实际不会重复）。</summary>
    private static void AddAnchorLine(AnchorContext context, Dictionary<string, AnchorLine> notes)
    {
        PerfResultItem? palorm = context.Newest.Items.FirstOrDefault(i =>
            i.Name == context.Name && i.Dialect == context.Dialect && i.Tier == context.Tier
            && i.Arm == "PalORM" && i.MedianUs > 0);
        if (palorm is null)
        {
            return;
        }

        double anchoredRatio = palorm.MedianUs / context.PartnerFloor;
        double ownHistory = FloorHistoryMedian(
            context.BatchesDesc, context.Newest, context.Name, context.Dialect, context.Tier);
        double partnerHistory = FloorHistoryMedian(
            context.BatchesDesc, context.Newest, context.Partner, context.Dialect, context.Tier);
        string historyHint = ownHistory > 0 && partnerHistory > 0
            ? $"；本项地板近批中位 {ownHistory:F0}µs、伙伴 {partnerHistory:F0}µs——贴近自身中位的一侧可信"
            : "";
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{context.Name}/{context.Dialect}/{context.Tier}: 表观 P/ADO {palorm.Ratio:F2}，但同形态地板分叉 {context.Divergence:P0}"
            + $"（本项 ADO 地板 {context.OwnFloor:F0}µs vs {context.Partner} {context.PartnerFloor:F0}µs）——锚定比值 {anchoredRatio:F2}"
            + $"（以伙伴地板重算，B175 判读法{historyHint}）");
        notes[Key("perfhub", context.Name, context.Dialect, context.Tier)] =
            new AnchorLine(new AnchorFacts(anchoredRatio, context.Divergence), text);
    }

    /// <summary>某键 ADO 地板的跨批中位（µs）：锚定批本身**按引用排除**（不能按位置 Skip(1)——
    /// 最新落盘批次常是子集批，锚定批并不是序列首位，Skip(1) 会把锚定批自己算进历史，
    /// "稳定侧"提示退化为自指），向前取至多 6 个非子集 PerfHub 批次的 ADO_NET 行中位数
    ///（子集批不占名额）。无历史返回 0（告警文本省略历史提示）。</summary>
    private static double FloorHistoryMedian(
        List<PerfResultEnvelope> batchesDesc, PerfResultEnvelope anchorBatch,
        string name, string dialect, int tier)
    {
        var values = new List<double>();
        foreach (PerfResultEnvelope run in batchesDesc)
        {
            // 锚定批自己不计入历史（按引用跳过，其余更老的批次继续扫——序列是时间倒序，
            // "更老"在锚定批之后，不能用 break 截断）
            if (ReferenceEquals(run, anchorBatch))
            {
                continue;
            }

            if (run.Harness != "perfhub" || PerfResultWriter.IsSubsetLabel(run.Label))
            {
                continue;
            }

            foreach (PerfResultItem item in run.Items)
            {
                if (item.Name == name && item.Dialect == dialect && item.Tier == tier
                    && item.Arm == "ADO_NET" && item.MedianUs > 0)
                {
                    values.Add(item.MedianUs);
                    break;
                }
            }

            if (values.Count >= 6)
            {
                break;
            }
        }

        if (values.Count == 0)
        {
            return 0;
        }

        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>门禁输出：一行摘要 + 失败/漂移/判别力弱/离群/锚定五类明细。
    /// <para>判别力弱（2026-10-04）只展开最差几条，总数在摘要里——本批实测 62 项超线，
    /// 全展开会刷屏；它只标注不判定，比值超限与否仍走原逻辑。</para>
    /// <para>第一个参数是计数五元组：比对 / 基线总数 / 缺项 / 非可比跳过 / 分母漂移。</para></summary>
    private static void PrintVerdict(
        (int Compared, int BaselineCount, int Missing, int Skipped, int FloorDrift, int NoiseBand) counts,
        List<string> failures, List<string> driftWarnings, List<string> noiseWarnings,
        List<string> weakWarnings, List<string> outliers, List<string> anchorLines)
    {
        Console.WriteLine($"[PerfGate] 结果库门禁: 比对 {counts.Compared} 项（基线 {counts.BaselineCount} 项，"
            + $"缺项 {counts.Missing}，非可比跳过 {counts.Skipped}，分母漂移 {counts.FloorDrift}，"
            + $"噪声带内 {counts.NoiseBand}），"
            + $"失败 {failures.Count}，判别力弱 {weakWarnings.Count} 项，锚定 {anchorLines.Count} 项");
        // 判定明细统一走 stdout（2026-10-19）：此前明细走 stderr、汇总走 stdout，两者被
        // 编排层重定向进同一个日志文件时按字节级交错，实测出现"超过基线 0.97 × (1，失败 %)"
        // 这类半行拼接——门禁的日志是判读材料，不能自带损坏。失败与否由退出码表达，
        // 不依赖流的分工；stderr 只留给 FATAL（那时还没有汇总行，不存在交错）。
        foreach (string failure in failures) Console.WriteLine("  FAIL " + failure);
        foreach (string drift in driftWarnings) Console.WriteLine("  漂移 " + drift);
        foreach (string noise in noiseWarnings) Console.WriteLine("  噪声 " + noise);
        foreach (string weak in weakWarnings.Take(NoiseWarnPreview)) Console.WriteLine("  弱 " + weak);
        if (weakWarnings.Count > NoiseWarnPreview)
        {
            Console.WriteLine(
                $"  弱 …另有 {weakWarnings.Count - NoiseWarnPreview} 项（ErrorRatio > 5%，比值不作结论）");
        }

        foreach (string outlier in outliers) Console.WriteLine("  离群 " + outlier);
        foreach (string anchor in anchorLines) Console.WriteLine("  锚定 " + anchor);
    }

    /// <summary>判别力弱清单：基线里每个可比项的噪声水位超线则登记一条（标注 + 参与判定：
    /// 超限幅度小于该水位时不判失败，见 <see cref="Classify"/>）。
    /// 独立成一趟而非塞进判定循环——判定循环的认知复杂度已在上限附近，塞进去会触发 S3776。</summary>
    private static List<string> CollectWeakDiscrimination(
        IndexBaseline baseline, Dictionary<string, double> noise)
    {
        var weak = new List<string>();
        foreach (IndexBaselineItem expected in baseline.Items)
        {
            if (expected.Incomparable)
            {
                continue;
            }

            NoteWeakDiscrimination(
                Key(expected.Harness, expected.Name, expected.Dialect, expected.Tier), noise, expected, weak);
        }

        return weak;
    }

    /// <summary>判别力弱标注：该键的噪声水位超线则登记一条（供人看；判定侧由
    /// <see cref="Classify"/> 用同一水位做"噪声带内不判失败"）。</summary>
    private static void NoteWeakDiscrimination(
        string noiseKey, Dictionary<string, double> noise, IndexBaselineItem expected,
        List<string> weakWarnings)
    {
        if (!noise.TryGetValue(noiseKey, out double errorRatio) || errorRatio <= NoiseLine)
        {
            return;
        }

        weakWarnings.Add(string.Create(CultureInfo.InvariantCulture,
            $"{expected.Name}/{expected.Dialect}/{expected.Tier}: 最大 ErrorRatio {errorRatio * 100:0}%（>5%）——比值落在噪声带内，不作结论"));
    }

    /// <summary>逐键的噪声水位：同键三臂里最大的 <c>ErrorRatio</c>（标准误/均值）。
    /// <para>与 <see cref="LoadWindowItems"/> 不同，这里遍历**全部臂**——比值的噪声来自分子与
    /// 分母两侧，只看被测臂会漏掉"地板很噪"的行（实测 SQLite/ADO_NET/Insert/2000 达 54.3%）。
    /// 取各批次的最大值（噪声水位是上界性质，与"窗口取中位数"的取值口径不同，故独立成表）。</para>
    /// <para>子集批次跳过，与其它取数口径一致。未采集该指标的夹具（Benchmarks/DapperSuite）
    /// 其 ErrorRatio 恒 0，不会触发标注。</para></summary>
    private static Dictionary<string, double> LoadNoiseLevels(
        List<PerfResultEnvelope> batches, bool includeSubsets = false)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (PerfResultEnvelope run in batches)
        {
            if (!includeSubsets && PerfResultWriter.IsSubsetLabel(run.Label))
            {
                continue;
            }

            foreach (PerfResultItem item in run.Items)
            {
                string key = Key(run.Harness, item.Name, item.Dialect, item.Tier);
                if (!map.TryGetValue(key, out double seen) || item.ErrorRatio > seen)
                {
                    map[key] = item.ErrorRatio;
                }
            }
        }

        return map;
    }

    /// <summary>窗口读数：逐键取最近 <paramref name="window"/> 个**含该键**的非子集批次的**中位数**。
    /// <para><b>录制与判定共用本方法</b>（2026-10-19 起两侧对称取窗）：录制取窗口中位数进基线，
    /// 判定取窗口中位数作当前值，两边是同一种估计量、同一个窗口长度，比较才有意义。此前判定取
    /// "最新一批"、录制取"中位数"，一侧抗噪一侧不抗噪，结果是每个含停顿的批次都产出一批假 FAIL
    /// （2026-10-19 实测：129 个可比键里 68 个的跨批比值摆动 &gt;1.30×，单批判定的噪声底已超过
    /// 门禁 30% 的判定带）。</para>
    /// <para><b>代价是检测延迟</b>：持续回归需要连续 2 个批次才把窗口中位推过阈值（3 批窗口里
    /// 2 个新样本）。单批的大跳变仍由 <see cref="OutlierWarnings"/> 逐条打印"疑似离群读数"，
    /// 不会静默。这个取舍与门禁的用途一致：它拦的是"代码改动导致的持续回归"，不是单次抖动。</para>
    /// <para>批内仍取最差（同名键重复出现时的安全倾斜，见 <see cref="KeyItems"/> 的 includeZeroRatio）；
    /// 键集合的构造与判定循环完全一致，所以录制与读回不会出现"基线 191 项、比对 171 项"的口径错配。</para>
    /// <para>中位数作用于 <c>Ratio</c>/<c>MedianUs</c>/<c>MeanUs</c> 三个字段；三者同批同源，
    /// 取中位数不破坏"地板 = 中位/比值"的自洽关系（门禁诊断按此反推隐含地板）。</para></summary>
    private static Dictionary<string, (string Harness, PerfResultItem Item)> LoadWindowItems(
        List<PerfResultEnvelope> batches, int window, bool includeSubsets = false)
    {
        var samples = new Dictionary<string, List<(string Harness, PerfResultItem Item)>>(StringComparer.Ordinal);
        foreach (PerfResultEnvelope run in batches)
        {
            // 批内：同名键取最差（安全方向）。includeZeroRatio：比值 0 的键也要占位，
            // 否则"最新批次已判定该项不可比"无法表达，旧批次会把它复活（见 KeyItems 文档）。
            var perBatch = new Dictionary<string, (string Harness, PerfResultItem Item)>(StringComparer.Ordinal);
            foreach ((string harness, PerfResultItem item) in KeyItems([run], includeZeroRatio: true, includeSubsets))
            {
                string key = Key(harness, item.Name, item.Dialect, item.Tier);
                if (!perBatch.TryGetValue(key, out (string Harness, PerfResultItem Item) existing)
                    || item.Ratio > existing.Item.Ratio)
                {
                    perBatch[key] = (harness, item);
                }
            }

            foreach (KeyValuePair<string, (string Harness, PerfResultItem Item)> entry in perBatch)
            {
                if (!samples.TryGetValue(entry.Key, out List<(string Harness, PerfResultItem Item)>? list))
                {
                    list = [];
                    samples[entry.Key] = list;
                }

                // batches 已按时间倒序：先到先得即"最近 window 批"
                if (list.Count < window)
                {
                    list.Add(entry.Value);
                }
            }
        }

        var map = new Dictionary<string, (string Harness, PerfResultItem Item)>(StringComparer.Ordinal);
        foreach ((string key, List<(string Harness, PerfResultItem Item)> list) in samples)
        {
            PerfResultItem newest = list[0].Item;
            if (list.Count == 1)
            {
                map[key] = (list[0].Harness, newest);
                continue;
            }

            var merged = new PerfResultItem
            {
                Name = newest.Name,
                Dialect = newest.Dialect,
                Arm = newest.Arm,
                Tier = newest.Tier,
                AllocBytes = newest.AllocBytes,
                RoundTripsPerOp = newest.RoundTripsPerOp,
                PreparedReuse = newest.PreparedReuse,
                Ratio = Median([.. list.Select(static s => s.Item.Ratio)]),
                MedianUs = Median([.. list.Select(static s => s.Item.MedianUs)]),
                MeanUs = Median([.. list.Select(static s => s.Item.MeanUs)]),
            };
            map[key] = (list[0].Harness, merged);
        }

        return map;
    }

    /// <summary>中位数（偶数个取中间两值平均；空集返回 0，调用方保证非空）。</summary>
    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        Array.Sort(values);
        int middle = values.Length / 2;
        return values.Length % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2;
    }

    /// <summary>录制窗口里实际贡献了数据的批次时间戳（新的在前，最多 <paramref name="window"/> 个）。
    /// <para>只统计 PerfHub：基线条目仅来自 PerfHub 的 PalORM 臂（见 <see cref="KeyItems"/>），
    /// DapperSuite 只作哨兵；不加 harness 过滤会让更新的哨兵批占掉一个名额，把标注指到
    /// 零贡献的批次上（2026-10-20 实测：02:07 DapperSuite 顶替了 01:33 PerfHub）。</para></summary>
    private static List<string> ContributingLabels(List<PerfResultEnvelope> batches, int window)
        => [.. batches
            .Where(static r => r.Harness == "perfhub")
            .Where(static r => !PerfResultWriter.IsSubsetLabel(r.Label))
            .Take(window)
            .Select(static r => r.Timestamp)];

    /// <summary>比值超限后的三分类：真失败 / 地板漂移 / 噪声带内。</summary>
    private enum OverrunVerdict
    {
        /// <summary>超限且无解释——FAIL。</summary>
        Fail,

        /// <summary>地板变快推高比值（跨批口径），只告警。</summary>
        FloorDrift,

        /// <summary>超限幅度小于该键自身的历史测量误差，不能区分回归与噪声，只告警。</summary>
        WithinNoise,
    }

    /// <summary>比值超限后的归因分类（判定循环只负责分流，规则集中在此）。
    /// <para><b>噪声带判据</b>（2026-10-19）：<c>超限幅度 ≤ 该键最大 ErrorRatio</c> 时不判失败。
    /// 依据是门禁自己打印的那句"比值落在噪声带内，不作结论"——既然同一份输出里已声明该行
    /// 不足以支撑结论，就不能同时拿它判失败（自相矛盾的判定比放宽更糟：它让人不再相信红色）。
    /// 量纲推导：ErrorRatio 是各臂的相对标准误，比值的不确定度 ≈ 两臂误差的合成，
    /// 取二者最大值作保守估计（max 略小于合成值，故对"刚好超线"的一侧仍偏向 FAIL 的数量级）。</para>
    /// <para>大跳变不受影响：实测 09:21 批 PG 2000 的 GetAllAsync 比值 3.64、ErrorRatio 11%，
    /// 调整后阈值 1.62 仍远低于实测值——真实退化照样红。</para></summary>
    private static OverrunVerdict Classify(
        IndexBaselineItem expected, (double Ratio, double MedianUs, double MeanUs) actual,
        double limit, double noise)
    {
        if (IsDenominatorDrift(expected, actual))
        {
            return OverrunVerdict.FloorDrift;
        }

        if (noise > NoiseLine && actual.Ratio <= limit * (1 + noise))
        {
            return OverrunVerdict.WithinNoise;
        }

        return OverrunVerdict.Fail;
    }

    /// <summary>噪声带告警文本：把"超限多少、噪声多少、为何不判"三件事一起打印出来。</summary>
    private static string NoiseBandText(
        IndexBaselineItem expected, (double Ratio, double MedianUs, double MeanUs) actual,
        double limit, double noise)
    {
        double overrun = limit > 0 ? ((actual.Ratio / limit) - 1) * 100 : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"{expected.Name}/{expected.Dialect}/{expected.Tier}: 比值 {actual.Ratio:F2} 超基线 "
            + $"{expected.Ratio:F2}（限 {limit:F2}），但该键最大 ErrorRatio {noise:P0}——"
            + $"超限 {overrun:+0;-0}% 在噪声带内，不作结论（复测确认）");
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
            + $"｜本项窗口中位 {expected.MedianUs:F2} → {actual.MedianUs:F2} µs（{medianDelta:+0.0;-0.0}%）"
            + $"｜隐含地板 {floorBase:F2} → {floorNow:F2} µs（{floorDelta:+0.0;-0.0}%）"
            + $"｜窗口均值 {expected.MeanUs:F2} → {actual.MeanUs:F2} µs");
    }

    /// <summary>哨兵报告：DapperSuite 不卡阈值，但它的健康度与地板比值要打印出来供人工判读。</summary>
    private static void ReportSentinel(List<PerfResultEnvelope> latest)
    {
        PerfResultEnvelope? sentinel = latest.Find(static r => r.Harness == "dappersuite");
        if (sentinel is null) return;
        // B123-③ 机械化（2026-10-05）：哨兵新鲜度巡检——dappersuite 哨兵批早于本结果库最新
        // PerfHub 批（本次扫描已有更新结果而哨兵没跟上）即提示登记步骤可能静默空跑。
        // d855b65 修引号过滤缺陷时，唯一旁证就是哨兵停在 09-29 旧批；人工核对时间戳不可靠，
        // 在门禁输出面机械化为每次必打印的告警（时间戳为 "yyyy-MM-dd HH:mm:ss zzz" 定长格式，
        // 序数比较即时间序）。触发路径验证：历史状态回放（哨兵 09-29 vs 批 10-01 时必触发）。
        // 子集批（filtered/recheck）不参与比较：它们只覆盖单方言或单档，本来就不要求
        // 哨兵跟着刷新；不排除时一次复测就会把哨兵报成"陈旧"（2026-10-19 实测）。
        // 同提交复测免报（2026-10-19 实测）：全量后 14 分钟再跑一次 PerfHub，哨兵时间戳
        // 必然早于该复测批，但登记步骤并未空跑。哨兵陈旧的真实语义是"跨版本/长期未刷新"，
        // 故只在提交不同或超过 7 天时告警；同提交的几分钟差属于同一轮测量。
        PerfResultEnvelope? newestPerfHub = latest
            .Where(static r => r.Harness.Equals("perfhub", StringComparison.OrdinalIgnoreCase)
                && !PerfResultWriter.IsSubsetLabel(r.Label))
            .OrderByDescending(static r => r.Timestamp, StringComparer.Ordinal)
            .FirstOrDefault();
        bool olderThanNewest = newestPerfHub is not null
            && string.CompareOrdinal(sentinel.Timestamp, newestPerfHub.Timestamp) < 0;
        bool sameCommit = newestPerfHub is not null
            && string.Equals(sentinel.Commit, newestPerfHub.Commit, StringComparison.OrdinalIgnoreCase);
        bool staleByAge = DateTimeOffset.TryParse(sentinel.Timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTimeOffset sentinelAt)
            && DateTimeOffset.Now - sentinelAt > TimeSpan.FromDays(7);
        if (olderThanNewest && (!sameCommit || staleByAge))
        {
            Console.WriteLine($"[PerfGate] ⚠ 哨兵批次陈旧：DapperSuite {sentinel.Timestamp} 早于本库最新 "
                + $"PerfHub 批（{newestPerfHub!.Timestamp}）——登记步骤可能静默空跑（B123-③），核对该步骤信封更新时间");
        }
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
    /// <see cref="LoadWindowItems"/> 里也必须占位——否则最近批次"知道这个键不可比"这件事
    /// 无法表达，旧批次里该项的高比值会把键复活，基线又被灌进一个已知无判别力的比值
    /// （2026-09-24 实测：PL-4 之后录出的 152 项基线里仍有 12 个 Build 项）。</para>
    /// <para>速览/哨兵这类只给人看的路径传 false——比值 0 的项没有可比信息，列出来是噪声。</para></summary>
    private static List<(string Harness, PerfResultItem Item)> KeyItems(
        List<PerfResultEnvelope> latest, bool includeZeroRatio = false, bool includeSubsets = false)
    {
        var items = new List<(string, PerfResultItem)>();
        foreach (PerfResultEnvelope run in latest)
        {
            // 只有 PerfHub 的 PalORM 臂进基线：微基准由 BDN 门禁覆盖，DapperSuite 只作哨兵
            if (run.Harness != "perfhub") continue;
            if (!includeSubsets && PerfResultWriter.IsSubsetLabel(run.Label))
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
    /// 条目是**逐键取窗口内中位数**合并自多个非子集批次的，故这里列出真正贡献的批次时间戳，
    /// 不说"录制批次"——后者会让人以为整份基线来自单一批次。</summary>
    private static string DescribeEnvironment(PerfResultEnvelope run, List<string> contributors)
        => string.Create(CultureInfo.InvariantCulture,
            $"{run.Environment.Processor} / {run.Environment.Runtime} / GC {run.Environment.GcMode}；"
            + $"条目逐键取 {contributors.Count} 个非子集批次的中位数（{string.Join("、", contributors)}）"
            + $"（最新批健康度 {run.Regime.Health} {run.Regime.HealthRatio:P1}）");

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

    /// <summary>分配恶化容忍度（PalORM 臂 AllocBytes 相对基线的比例）——只用于 L0 快线判定。
    /// 20% 与 BDN 微基准的分配阈值同源（<c>perf-baseline.json</c> 的分配 ±20%）；分配是
    /// <c>GC.GetTotalAllocatedBytes</c> 精确计数，正常复现性远优于该阈值。</summary>
    public double AllocDelta { get; set; } = 0.20;
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

    /// <summary>PalORM 臂的每操作分配字节数（确定性指标）。0 = 旧基线未采集（跳过分配判定）。
    /// <para>2026-10-24 加入：L0 快线要求"分配与比值"两个判定面（作战卡 §6.3），
    /// 而原基线只记比值与中位，分配只在 BDN 门禁里卡。full 判定不启用分配检查，
    /// 只有 L0 用它；旧基线文件缺该字段时按 0 跳过，向后兼容。</para></summary>
    public long AllocBytes { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(IndexBaseline))]
internal sealed partial class IndexBaselineJsonContext : JsonSerializerContext;
