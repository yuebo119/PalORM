using System.Text.Json.Serialization;

namespace PalORM.PerfGate;

/// <summary>基线文件模型（schema 1）。
/// <para>阈值只覆盖**分配字节数**与**相对同轮手写对照的比值**，不含绝对耗时：
/// 实测同机两次运行的 ADO.NET 地板可从 6.97ms 漂到 10.0ms（43%），绝对时间做门禁必然误报。
/// 比值在同一次运行内计算，把机器与运行时差异约掉，因此跨机器可比。</para></summary>
internal sealed class PerfBaseline
{
    /// <summary>模型版本——不匹配即拒绝判定（宁可失败也不按错误格式解读）。</summary>
    public int Schema { get; set; }

    /// <summary>被测版本。</summary>
    public string Version { get; set; } = "";

    /// <summary>录制日期（yyyy-MM-dd）。</summary>
    public string Date { get; set; } = "";

    /// <summary>覆盖范围说明（哪些基准类、哪个方言）。</summary>
    public string Scope { get; set; } = "";

    /// <summary>录制环境（仅作记录，不参与判定）。</summary>
    public BaselineEnvironment Environment { get; set; } = new();

    /// <summary>阈值档位。</summary>
    public BaselineThresholds Thresholds { get; set; } = new();

    /// <summary>录制说明与已知取舍。</summary>
    public string Notes { get; set; } = "";

    /// <summary>基准条目。</summary>
    public List<BaselineEntry> Benchmarks { get; set; } = [];
}

/// <summary>录制环境快照。</summary>
internal sealed class BaselineEnvironment
{
    /// <summary>操作系统描述。</summary>
    public string? Os { get; set; }

    /// <summary>处理器描述。</summary>
    public string? Processor { get; set; }

    /// <summary>.NET 运行时版本。</summary>
    public string? Runtime { get; set; }

    /// <summary>BenchmarkDotNet 版本。</summary>
    public string? BenchmarkDotNet { get; set; }
}

/// <summary>阈值档位（百分比）。</summary>
internal sealed class BaselineThresholds
{
    /// <summary>单基准分配字节数的允许增幅。</summary>
    public double AllocatedPct { get; set; } = 20.0;

    /// <summary>相对同轮手写对照的分配比允许增幅。</summary>
    public double AllocRatioPct { get; set; } = 10.0;

    /// <summary>相对同轮手写对照的耗时比允许增幅。
    /// <para>比分配阈值宽：门禁用 1 launch / 3 warmup / 5 iteration 的短 job，
    /// 中位数本身有可观方差，卡太紧会把抖动判成回归。它挡的是量级性 CPU 退化，
    /// 不是几个百分点的波动。</para></summary>
    public double TimeRatioPct { get; set; } = 30.0;
}

/// <summary>单条基准的基线值。</summary>
internal sealed class BaselineEntry
{
    /// <summary>基准全名（BDN 的 FullName，含根命名空间）。</summary>
    public string Name { get; set; } = "";

    /// <summary>每次操作的分配字节数（确定性指标，与迭代次数无关）。</summary>
    public double AllocatedBytes { get; set; }

    /// <summary>中位耗时（纳秒）。**仅供本地参考，门禁不对其设阈值**——见类文档。</summary>
    public double MedianNs { get; set; }

    /// <summary>同轮手写对照的基准全名（优先 ADO.NET，回退 Dapper）；无对照时为 null。</summary>
    public string? RatioVs { get; set; }

    /// <summary>分配比 = 本基准分配 / 对照分配（基线录制时的值）。</summary>
    public double? AllocRatio { get; set; }

    /// <summary>耗时比 = 本基准中位数 / 对照中位数（基线录制时的值）。</summary>
    public double? TimeRatio { get; set; }
}

/// <summary>基线 JSON 的源生成上下文——避免反射序列化（与仓库 AOT 纪律一致）。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PerfBaseline))]
internal sealed partial class BaselineJsonContext : JsonSerializerContext;
