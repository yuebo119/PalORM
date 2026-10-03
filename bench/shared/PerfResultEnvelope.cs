using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalORM.Bench.Shared;

/// <summary>结果库统一信封（<c>docs/性能基准规范.md</c> v2 §6）——三套夹具每次跑测写一份，
/// 落到 <c>bench/results/&lt;harness&gt;-&lt;时间戳&gt;.json</c> 并刷新 <c>latest-&lt;harness&gt;.json</c>。
/// <para><b>为什么是信封而不是完整明细</b>：各夹具的原生产物（BDN JSON / PerfHub history /
/// DapperSuite 日志）格式各异且各有消费方，强行统一会伤到既有报告；信封只承载
/// "跨夹具可查询的最小集 + 口径登记 + 健康度"，明细路径写进 <see cref="DetailPath"/>。</para>
/// <para><b>单一真源</b>：本文件被三个 bench 项目以 Compile Link 共用——复制成三份会让
/// schema 各自漂移，那正是"结果四处散落"的成因。</para></summary>
internal sealed class PerfResultEnvelope
{
    /// <summary>信封 schema 版本。字段增删必须同时改 <c>bench/results/README.md</c>。</summary>
    public int Schema { get; set; } = 2;

    /// <summary>夹具标识：<c>benchmarks</c> / <c>perfhub</c> / <c>dappersuite</c>。</summary>
    public string Harness { get; set; } = "";

    public string Timestamp { get; set; } = "";

    /// <summary>git 短哈希（取不到为 <c>unknown</c>）——"哪一版测的"是结果可比的前提。</summary>
    public string Commit { get; set; } = "";

    /// <summary>被测版本标识（如 HEAD / v5.5.1）；PerfHub 的 A/B 标签也走这里。</summary>
    public string Version { get; set; } = "";

    /// <summary>批次标签（如 <c>ab/2/pg/2000</c>）；无则空串。</summary>
    public string Label { get; set; } = "";

    public double ElapsedSeconds { get; set; }

    /// <summary>明细产物路径（相对仓库根）；无则空串。</summary>
    public string DetailPath { get; set; } = "";

    public PerfResultEnvironment Environment { get; set; } = new();

    public PerfResultRegime Regime { get; set; } = new();

    public List<PerfResultItem> Items { get; set; } = [];

    /// <summary>非"项 × 臂"形态的测量节（规范 §6 schema 2：负载/长稳/内存曲线）。
    /// 用 kind + 自由指标字典而不是三套并行模型——三条路径的指标集本来就不同，
    /// 强行统一字段名会让每次新增指标都改 schema。</summary>
    public List<PerfResultSection> Sections { get; set; } = [];
}

/// <summary>测量节：<c>kind</c> = load / stability / memory；
/// <c>label</c> 为该节的档位标识（线程档 / 秒数 / 行数）；指标进 <see cref="Metrics"/>。</summary>
internal sealed class PerfResultSection
{
    public string Kind { get; set; } = "";
    public string Dialect { get; set; } = "";
    public string Label { get; set; } = "";
    public Dictionary<string, double> Metrics { get; set; } = [];
    public string Note { get; set; } = "";
}

/// <summary>环境节（规范 §3 要求项的子集 + 工具版本）。</summary>
internal sealed class PerfResultEnvironment
{
    public string Os { get; set; } = "";
    public string Processor { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string Machine { get; set; } = "";
    public string GcMode { get; set; } = "";
    public string Tool { get; set; } = "";
}

/// <summary>口径登记（规范 §4.1 两项）+ 健康度（§4.2）。缺项的数字不得引用。</summary>
internal sealed class PerfResultRegime
{
    /// <summary>连接配置口径：journal/cache/mmap/pooling/会话级 SET 的逐项说明，全臂同值。</summary>
    public string ConnectionConfig { get; set; } = "";

    /// <summary>会话与连接生命周期口径：per-scope（范围入口建一次）或 per-operation（每操作新建）。</summary>
    public string SessionLifecycle { get; set; } = "";

    /// <summary>健康度判词：clean / noisy / unknown。</summary>
    public string Health { get; set; } = "unknown";

    /// <summary>健康度依据的比值（地板行散布；0 表示未采）。</summary>
    public double HealthRatio { get; set; }

    /// <summary>该夹具自报的健康度阈值——必须是自己实测的噪声底上界（规范 §4「噪声底」纪律），
    /// 不能借用别的夹具的阈值（BDN 长跑与自适应短跑的散布不是一个量级）。</summary>
    public double HealthThreshold { get; set; } = 0.10;
}

/// <summary>一行测量。Ratio 为同方言同批次内相对地板的比值，无地板时留 0。
/// <para><b>Ratio 的基数（2026-10-02 定案）</b>：中位数，不是均值。均值对计时离群值极其敏感——
/// 实测终验批 Insert/SQLite/2000 的 ADO 臂中位数 17.1µs、均值 45.6µs（errRatio 0.58），
/// 用它当分母会让该档基线比值录成 0.46（中位口径 1.13），下一个不离群的批次必然假报 FAIL。
/// 报告侧（PerfHub Report.cs）本来就用 MedianNs，此处改中位数后两处口径才一致。
/// <see cref="MeanUs"/> 保留：门禁失败描述要用它分辨"本项退化"与"地板移动"。</para></summary>
internal sealed class PerfResultItem
{
    public string Name { get; set; } = "";
    public string Dialect { get; set; } = "";
    public string Arm { get; set; } = "";
    public int Tier { get; set; }

    /// <summary>中位耗时（µs）——Ratio 的基数，也是门禁比值的分子/分母来源。</summary>
    public double MedianUs { get; set; }

    /// <summary>平均耗时（µs）——仅供门禁失败描述做方向判读，不参与比值。</summary>
    public double MeanUs { get; set; }
    public long AllocBytes { get; set; }
    public double Ratio { get; set; }

    /// <summary>维度 8：每操作往返次数（0 = 本夹具未测）。</summary>
    public double RoundTripsPerOp { get; set; }

    /// <summary>维度 8：prepared 语句复用率 0-1（0 = 本夹具未测）。</summary>
    public double PreparedReuse { get; set; }

    /// <summary>量具自检：Error/Mean（标准误比均值）——该臂读数自身的相对不确定度。
    /// <para><b>判别力弱标注的判据</b>（2026-10-04）：同一键的三臂里最大 ErrorRatio &gt; 5% 时，
    /// 该行的比值落在噪声带内，不足以支撑结论（阈值沿用 <c>Measure.cs</c> 的"&gt;5% 标黄"自检线）。
    /// 比值 = 被测臂 / 地板，两侧噪声都会放大比值的不确定度，故取三臂最大值而非只看被测臂。</para>
    /// <para>未采集该项的夹具（Benchmarks / DapperSuite）恒为 0，读侧须把 0 当"未测"而不是"无噪声"。</para></summary>
    public double ErrorRatio { get; set; }

    public string Note { get; set; } = "";
}

/// <summary>信封落盘与共用取值（仓库根 / git 提交 / 环境 / 健康度判词）。</summary>
internal static class PerfResultWriter
{
    /// <summary>健康度判据（规范 §4.2）：地板行散布超过该夹具自报阈值即判 noisy。</summary>
    public static string Verdict(double ratio, double threshold)
    {
        if (ratio <= 0) return "unknown";
        return ratio <= threshold ? "clean" : "noisy";
    }

    /// <summary>写信封并返回落盘路径；写失败返回 null（基准结果不该因登记失败而丢）。
    /// <para><b>子集批次不顶 latest</b>：label 带 <c>quick</c>/<c>filtered</c> 的跑测只写批次文件，
    /// 不更新 <c>latest-&lt;harness&gt;.json</c>——否则门禁与索引会读到不可引用的子集
    ///（规范 §6：子集批次必须带标记，门禁跳过带标记的批次）。</para></summary>
    public static string? Write(PerfResultEnvelope envelope)
    {
        try
        {
            string dir = Path.Combine(RepoRoot(), "bench", "results");
            Directory.CreateDirectory(dir);
            envelope.Timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(envelope.Commit)) envelope.Commit = GitCommit();
            string json = JsonSerializer.Serialize(envelope, PerfResultJsonContext.Default.PerfResultEnvelope);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, envelope.Harness + "-" + stamp + ".json");
            File.WriteAllText(path, json);
            // latest 只指向"可引用的最近状态"：子集批次（quick/filtered/workload/memory/stability）
            // 与**空批次**（无 items 且无 sections，例如库不可达导致全部 NA 的方言跑）都不顶它。
            // 空批次本身仍然落盘——"这次什么都没测到"是事实，只是不该被当成当前状态。
            bool hasData = envelope.Items.Count > 0 || envelope.Sections.Count > 0;
            string latestTarget = Path.Combine(dir, "latest-" + envelope.Harness + ".json");
            // 覆盖面回退（2026-10-02）：label 是"显式声明"，但同族缺陷已三度复发
            //（ab/ → gate-set → verify-），每次都是"跑了单方言却没声明成子集"。信封侧与
            // PerfHub 原始侧是**两个写入点**——B86 那次只修一侧的教训，必须两侧同真源。
            string? coverageReason = hasData ? DetectCoverageRegression(latestTarget, envelope) : null;
            if (!IsSubsetLabel(envelope.Label) && hasData && coverageReason is null)
            {
                File.WriteAllText(latestTarget, json);
            }
            else if (coverageReason is not null)
            {
                Console.WriteLine($"[PerfResult] 跳过 latest-{envelope.Harness}（{coverageReason}）");
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>子集批次标记（规范 §6）：以下批次不得顶 latest、不得进基线——
    /// 它们不是该夹具的完整矩阵，顶掉 latest 会让索引与门禁读到残缺视图。
    /// <list type="bullet">
    /// <item><c>quick</c>：PerfHub 冒烟（迭代降到 30%）</item>
    /// <item><c>filtered</c>：BDN/官方套件的过滤跑（只跑子集基准）</item>
    /// <item><c>gate-set</c>：`run-full-perf.sh` 的门禁同参集（11 个基准类里只跑
    /// Crud + OrmComparison 两个类）——它自身注释就写着"可复现的操作性子集"，
    /// 但它不带 filtered，曾被当成完整矩阵顶掉 latest</item>
    /// <item><c>ab/&lt;轮&gt;/&lt;方言&gt;/&lt;档位&gt;</c>：交替 A/B 的单个块（一次只跑一个方言的一个档位）。
    /// 它不带任何子集词，曾被当成完整矩阵——而 A/B 正是"块 = 方言 × 档位"的设计，
    /// 单块必然残缺（实测 2026-09-23：不登记时 `record-index` 会挑中 ab 批次而非全量批次）</item>
    /// <item><c>workload</c>/<c>memory</c>/<c>stability</c>：微基准的三条旁路模式（各只覆盖一个维度）</item>
    /// </list></summary>
    /// <summary>子集标记判据：带这些标记的批次是**单方言/单档/单臂**的定向跑测，只写批次文件，
    /// 不顶 <c>latest-*</c>（否则"当前状态"会变成只测到一个方言的读数）。
    /// <para><b>为什么 <c>verify-</c> 也在内（2026-10-02 实测）</b>：加它之前，SQLite 单方言验证批
    /// 因 label 不含任何既有标记而顶掉了 <c>latest-*</c>（434 项 → 128 项，MySQL/PG 整体消失却无提示）。
    /// 与"空批次顶 latest"是同族缺陷：判定只看 label，不看**覆盖面是否缩水**。</para></summary>
    public static bool IsSubsetLabel(string label)
        => label.Contains("quick", StringComparison.OrdinalIgnoreCase)
        || label.Contains("filtered", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("ab/", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("verify-", StringComparison.OrdinalIgnoreCase)
        || label is "gate-set" or "workload" or "memory" or "stability";

    /// <summary>覆盖面回退检测（信封侧）：本批次的方言集是否为上一个可引用信封的**真子集**。
    /// <para>与 PerfHub 原始侧的 <c>DetectCoverageRegression</c> 同一判据、同一动机——
    /// 两个写入点必须都拦住，只修一侧等于没修（B86 的教训）。</para>
    /// <para>返回 null 表示放行，否则返回人类可读的拦截原因。读不到旧信封时放行。</para></summary>
    private static string? DetectCoverageRegression(string latestTarget, PerfResultEnvelope envelope)
    {
        if (!File.Exists(latestTarget))
        {
            return null;
        }

        try
        {
            PerfResultEnvelope? previous = JsonSerializer.Deserialize(
                File.ReadAllText(latestTarget), PerfResultJsonContext.Default.PerfResultEnvelope);
            if (previous is null)
            {
                return null;
            }

            HashSet<string> prevDialects = DialectsOf(previous);
            HashSet<string> newDialects = DialectsOf(envelope);
            if (prevDialects.Count == 0 || newDialects.Count == 0)
            {
                return null;
            }

            if (newDialects.Count < prevDialects.Count && newDialects.IsSubsetOf(prevDialects))
            {
                return $"方言覆盖面缩水（本批 {string.Join('/', newDialects)} ⊂ 上一批 {string.Join('/', prevDialects)}）"
                    + "，请给跑测加子集 label 或补跑其余方言";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 读不到旧信封不是本次写入的错误——放行，不阻塞 latest 更新
            return null;
        }
    }

    /// <summary>信封里出现过的方言集（sections 与 items 合并；无 items 的方言节也算覆盖）。</summary>
    private static HashSet<string> DialectsOf(PerfResultEnvelope envelope)
    {
        // 显式 StringComparer.Ordinal：方言名来自封闭枚举，不需要文化敏感比较。
        // IDE0028 建议的集合表达式无法携带比较器，此处保留显式构造（抑制该建议）。
        // S3267 的 Where 写法在此会引入两轮中间枚举，且本方法只在写 latest 时调用一次
        //（非热路径）——显式循环更直白，抑制该建议。
#pragma warning disable IDE0028, S3267
        HashSet<string> dialects = new(StringComparer.Ordinal);
        foreach (PerfResultItem item in envelope.Items)
        {
            if (!string.IsNullOrEmpty(item.Dialect))
            {
                _ = dialects.Add(item.Dialect);
            }
        }

        foreach (PerfResultSection section in envelope.Sections)
        {
            if (!string.IsNullOrEmpty(section.Dialect))
            {
                _ = dialects.Add(section.Dialect);
            }
        }
#pragma warning restore IDE0028, S3267

        return dialects;
    }

    /// <summary>仓库根：向上找 PalORM.slnx。</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalORM.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>git 短哈希：直接读 <c>.git/HEAD</c>（支持 worktree 的 gitdir 间接文件），
    /// 取不到返回 unknown。不用 <c>git</c> 子进程：不依赖 PATH，也没有 S4036 的路径问题。</summary>
    public static string GitCommit()
    {
        try
        {
            string gitDir = Path.Combine(RepoRoot(), ".git");
            if (File.Exists(gitDir))
            {
                // worktree：.git 是文本文件，内容形如 "gitdir: <路径>"
                string[] lines = File.ReadAllLines(gitDir);
                if (lines.Length == 0 || !lines[0].StartsWith("gitdir:", StringComparison.Ordinal)) return "unknown";
                gitDir = lines[0]["gitdir:".Length..].Trim();
                if (!Path.IsPathRooted(gitDir)) gitDir = Path.Combine(RepoRoot(), gitDir);
            }

            string headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return "unknown";
            string head = File.ReadAllText(headPath).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
            {
                // detached HEAD：HEAD 直接是提交号
                return head.Length >= 7 ? head[..7] : head;
            }

            string refPath = Path.Combine(gitDir, head["ref:".Length..].Trim().Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(refPath)) return "unknown";
            string sha = File.ReadAllText(refPath).Trim();
            return sha.Length >= 7 ? sha[..7] : sha;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "unknown";
        }
    }

    public static PerfResultEnvironment CaptureEnvironment(string tool)
        => new()
        {
            Os = Environment.OSVersion.ToString(),
            Processor = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " 逻辑核",
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Machine = Environment.MachineName,
            GcMode = System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation",
            Tool = tool
        };
}

[JsonSerializable(typeof(PerfResultEnvelope))]
internal sealed partial class PerfResultJsonContext : JsonSerializerContext;
