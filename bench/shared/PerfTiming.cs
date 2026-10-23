// 阶段 0 计时旁车模型（性能测试系统提效 · 作战卡 §4）。
// 单一事实源：PerfHub 写原始阶段计时，PerfCli 读并合并编排层固定开销——
// 两侧共用同一份定义，避免字段两处漂移（同 PerfResultEnvelope 的共享方式）。
// 本文件的模型只承载计时，不参与任何门禁判定：写坏它不影响结果库与基线。
namespace PalORM.Bench.Shared;

/// <summary>单测量的阶段计时（秒）。各阶段之和 ≤ TotalSeconds（探针前的循环开销未单独记）。
/// <para>口径：全部用 <c>Stopwatch.GetTimestamp()</c> 取单调时钟，禁止 DateTime；
/// prepare（重置/播种）在预热前与计时前各一次，两者分开记——批量族的时间大头常在 reset。</para></summary>
internal sealed class PerfStageTiming
{
    public string Dialect { get; set; } = "";
    public int Rows { get; set; }
    public string Arm { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Group { get; set; } = "";
    /// <summary>false = 交错读的丢弃 pass（时间真实消耗，但不是可引用测量）。</summary>
    public bool Recorded { get; set; } = true;
    public int Iterations { get; set; }

    public double TotalSeconds { get; set; }
    public double PrepareBeforeSeconds { get; set; }
    public double WarmupSeconds { get; set; }
    public double PrepareAfterSeconds { get; set; }
    public double ProbeSeconds { get; set; }
    public double PreGcSeconds { get; set; }
    public double TimedSeconds { get; set; }
    public double PostSeconds { get; set; }
    /// <summary>测量外层：results.Add + 进度行打印（含 ETA 计算），由 PerfHub 补记。</summary>
    public double WriteResultSeconds { get; set; }
}

/// <summary>测量序列之外的相位（秒）。<c>Dialect/Rows</c> 为归属维度，非测量维度。
/// <para>重叠关系：<c>dialect-total</c> 覆盖其下全部相位与测量，<c>arm-block</c> 覆盖该臂的单测量，
/// 汇总时必须排除重叠相位，否则总墙钟会被重复计算。</para></summary>
internal sealed class PerfPhaseTiming
{
    public string Name { get; set; } = "";
    public string Dialect { get; set; } = "";
    public int Rows { get; set; }
    public double Seconds { get; set; }
}

/// <summary>PerfHub 一次 run 的计时旁车（<c>bench/reports/perf-timing-&lt;stamp&gt;.json</c>）。</summary>
internal sealed class PerfTimingReport
{
    public int Schema { get; set; } = 1;
    public string Timestamp { get; set; } = "";
    public string Commit { get; set; } = "";
    public double RunTotalSeconds { get; set; }
    public double SaveSeconds { get; set; }
    public double ReportBuildSeconds { get; set; }
    public List<PerfStageTiming> Items { get; set; } = [];
    public List<PerfPhaseTiming> Phases { get; set; } = [];
}

/// <summary>编排层单个内部步骤的耗时（FullPerf 的 [0/6]…[5/6]）。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "PerfHub 只链接模型（反序列化/读取），实例化发生在 PerfCli 的 FullPerf；两个程序集共用同一份定义。")]
internal sealed class PerfInnerStep
{
    public string Name { get; set; } = "";
    public double Seconds { get; set; }
}
