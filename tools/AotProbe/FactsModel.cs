using System.Text.Json.Serialization;

namespace PalORM.AotProbe;

/// <summary>facts JSON 模型（入库真源：tools/AotProbe/results/aotprobe-&lt;时间戳&gt;.json）。
/// 报告只由本模型生成，禁手改（模板冻结纪律，crossbench 11 处漂移教训）。</summary>
internal sealed record IlWarning(string Code, string Attribution, string Origin, string Text);

/// <summary>单条断言失败记录（期望 vs 观测，报告可追溯性的最小单元）。</summary>
internal sealed record AssertionFailure(string Key, string Kind, string Expected, string Actual);

/// <summary>单探针执行结果。Status：pass | fail（断言不符）| error（探针异常/原生崩溃无标记）| timeout。</summary>
internal sealed record ProbeRunResult(
    string Probe, string Status, int ExitCode, bool MarkerSeen,
    string? ErrorType, string? ErrorMessage,
    IReadOnlyList<ObsField> Fields, IReadOnlyList<AssertionFailure> Assertions, long ElapsedMs);

/// <summary>一个变体臂（库 × 组 × 形态）的完整事实：构建/发布结果、IL 告警、逐探针结果。</summary>
internal sealed record ArmRun(
    string Library, string Group, string Form,
    bool BuildOk, string? BuildError, bool PublishOk, string? PublishError,
    IReadOnlyList<IlWarning> Warnings, IReadOnlyList<ProbeRunResult> Probes)
{
    public bool BinaryReady => BuildOk && PublishOk;

    /// <summary>启动判定：任一探针进程产出 OBS 标记即视为原生启动成功
    /// （无标记 + 非零退出 = 原生崩溃/启动失败，判定模型 R3 的数据源）。</summary>
    public bool StartupOk => Probes.Any(p => p.MarkerSeen);
}

internal sealed record FactsFile(
    string Schema, string GeneratedAtUtc,
    string RulesVersion, string CatalogVersion, string ManifestVersion, string FixtureVersion,
    string Sdk, string Rid, string Os,
    IReadOnlyList<ArmRun> Runs)
{
    public const string CurrentSchema = "aotprobe-facts/1";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FactsFile))]
[JsonSerializable(typeof(ProbeObservation))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;
