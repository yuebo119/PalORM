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

/// <summary>一行测量。Ratio 为同方言同批次内相对地板的比值，无地板时留 0。</summary>
internal sealed class PerfResultItem
{
    public string Name { get; set; } = "";
    public string Dialect { get; set; } = "";
    public string Arm { get; set; } = "";
    public int Tier { get; set; }
    public double MeanUs { get; set; }
    public long AllocBytes { get; set; }
    public double Ratio { get; set; }

    /// <summary>维度 8：每操作往返次数（0 = 本夹具未测）。</summary>
    public double RoundTripsPerOp { get; set; }

    /// <summary>维度 8：prepared 语句复用率 0-1（0 = 本夹具未测）。</summary>
    public double PreparedReuse { get; set; }

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
            if (!IsSubsetLabel(envelope.Label) && hasData)
            {
                File.WriteAllText(Path.Combine(dir, "latest-" + envelope.Harness + ".json"), json);
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
    /// <item><c>workload</c>/<c>memory</c>/<c>stability</c>：微基准的三条旁路模式（各只覆盖一个维度）</item>
    /// </list></summary>
    public static bool IsSubsetLabel(string label)
        => label.Contains("quick", StringComparison.OrdinalIgnoreCase)
        || label.Contains("filtered", StringComparison.OrdinalIgnoreCase)
        || label is "workload" or "memory" or "stability";

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
