using System.Text.Json.Serialization;

namespace PalORM.PerfGate;

/// <summary>BenchmarkDotNet JSON 报告的读取模型（只取判定需要的字段）。
/// <para>BDN 的 <c>JsonExporter</c> 输出 PascalCase 字段（见 fork 的
/// <c>JsonExporterBase.GetDataToSerialize</c>）：顶层 <c>HostEnvironmentInfo</c> +
/// <c>Benchmarks</c>；条目含 <c>FullName</c> / <c>Statistics</c> / <c>Memory</c>。</para>
/// <para>读 JSON 而不是控制台表格是刻意的：表格是格式化文本，列宽与单位随 BDN 版本和
/// 结果量级变化（原门禁 grep <c>'PalORM_QueryAll' | grep 'ms |'</c>，单位一变就恒抓不到，
/// 而"抓不到"又落到 warning 分支放行）。</para></summary>
internal sealed class BdnRoot
{
    /// <summary>宿主环境信息（仅记录，不参与判定）。</summary>
    public BdnHostEnvironment? HostEnvironmentInfo { get; set; }

    /// <summary>本次报告覆盖的基准条目。</summary>
    public List<BdnBenchmark> Benchmarks { get; set; } = [];
}

/// <summary>宿主环境信息子集。</summary>
internal sealed class BdnHostEnvironment
{
    /// <summary>操作系统描述。</summary>
    public string? OsVersion { get; set; }

    /// <summary>处理器描述。</summary>
    public string? ProcessorName { get; set; }

    /// <summary>.NET 运行时版本。</summary>
    public string? RuntimeVersion { get; set; }

    /// <summary>BenchmarkDotNet 版本。</summary>
    public string? BenchmarkDotNetVersion { get; set; }
}

/// <summary>单条基准结果。</summary>
internal sealed class BdnBenchmark
{
    /// <summary>基准全名（含根命名空间，形如 <c>PalORM.Benchmarks.CrudBenchmarks.PalORM_QueryAll</c>）。</summary>
    public string? FullName { get; set; }

    /// <summary>统计量。缺失即视为该条目无效（判定不猜）。</summary>
    public BdnStatistics? Statistics { get; set; }

    /// <summary>内存诊断量。无 <c>[MemoryDiagnoser]</c> 的类不产出本节点。</summary>
    public BdnMemory? Memory { get; set; }
}

/// <summary>统计量子集。</summary>
internal sealed class BdnStatistics
{
    /// <summary>中位耗时（纳秒）。</summary>
    public double? Median { get; set; }

    /// <summary>平均耗时（纳秒）。</summary>
    public double? Mean { get; set; }
}

/// <summary>内存诊断子集。</summary>
internal sealed class BdnMemory
{
    /// <summary>每次操作的分配字节数。无 MemoryDiagnoser 时为 null。</summary>
    public double? BytesAllocatedPerOperation { get; set; }
}

/// <summary>BDN 报告的源生成上下文。大小写不敏感以容忍 fork 的字段风格差异。</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BdnRoot))]
internal sealed partial class BdnJsonContext : JsonSerializerContext;
