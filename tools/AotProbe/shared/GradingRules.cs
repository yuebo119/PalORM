namespace PalORM.AotProbe;

internal enum Grade { Unsupported, Low, Mid, High, Full }

/// <summary>归因标签（决策表定式，评审 2026-10-28）。只挂注、不改档。</summary>
internal enum Attribution
{
    LibraryGuaranteed,
    NeedsRoot,
    MechanismEdge,
    DynamicCodeProblem,
    TrimRescuable,
    TrimUnrescuable,
    /// <summary>JIT 对照臂未过：环境故障或适配器缺陷，非 AOT 结论（该库本轮出局）。</summary>
    EnvironmentFault,
    /// <summary>归因臂数据不全（例如 B 组未跑）。</summary>
    Unknown,
}

internal sealed record LayerStat(int Pass, int Applicable)
{
    public bool AllPass => Applicable > 0 && Pass == Applicable;
    public bool AnyApplicable => Applicable > 0;
}

/// <summary>判定输入：单库（A 组 × Full AOT 主测定形态）的聚合事实。</summary>
internal sealed record GradeInput(
    bool CompileOk, bool IlcOk, bool StartupOk,
    LayerStat P0, LayerStat P1, LayerStat P2, LayerStat P3,
    int UnmitigatedLibWarningCount);

/// <summary>归因输入：升级阶梯 + B 组各臂的 P0 结果。null = 该臂未跑（避免"缺失臂"被
/// 误读成"臂失败"——归因标签必须区分未测与失败）。</summary>
internal sealed record ArmInput(
    bool? AJitP0, bool? AAotP0, bool? TrimJitP0, bool? AotRootP0, bool? BAotP0, bool? BAotRootP0);

internal sealed record GradeResult(Grade Grade, string RuleId, string RuleText);

internal static class GradingRules
{
    public const string RulesVersion = "v1-S0";

    /// <summary>判定模型 v1（冻结评审件）。规则全覆盖无落空（评审发现原设计
    /// "P0-P2 过、P3 败、无文档化缺口"无档可落，本表按序匹配修复）。分母一律 Applicable 口径。
    /// 方法名 Evaluate 以避开与 Grade 枚举的同名冲突（CS0117/CS0119）。</summary>
    internal static GradeResult Evaluate(GradeInput f)
    {
        if (!f.CompileOk)
            return new(Grade.Unsupported, "R1", "编译失败");
        if (!f.IlcOk)
            return new(Grade.Unsupported, "R2", "ILC 发布失败");
        if (!f.StartupOk)
            return new(Grade.Unsupported, "R3", "原生启动失败");
        if (!f.P0.AllPass)
            return new(Grade.Unsupported, "R4", $"P0 有失败（{f.P0.Pass}/{f.P0.Applicable}）");
        if (f.P1.AnyApplicable && !f.P1.AllPass)
            return new(Grade.Low, "R5", $"P1 有失败（{f.P1.Pass}/{f.P1.Applicable}）");
        if (f.P2.AnyApplicable && !f.P2.AllPass)
            return new(Grade.Mid, "R6", $"P2 有失败（{f.P2.Pass}/{f.P2.Applicable}）");
        if ((f.P3.AnyApplicable && !f.P3.AllPass) || f.UnmitigatedLibWarningCount > 0)
            return new(Grade.High, "R7", f.UnmitigatedLibWarningCount > 0
                ? $"P0-P2 过但存在 {f.UnmitigatedLibWarningCount} 条未缓解库归属 IL 告警"
                : $"P3 有失败（{f.P3.Pass}/{f.P3.Applicable}）");
        return new(Grade.Full, "R8", "P0-P3 全过 + 零未缓解库归属告警");
    }

    /// <summary>归因决策表（2026-10-28 定式）：AOT 失败先分动态代码/裁剪，成功再分库级/兜底。
    /// 未跑的臂（null）不得臆断为失败，一律落 Unknown。</summary>
    internal static Attribution Attribute(ArmInput a)
    {
        // JIT 臂失败本身即结论（环境故障/适配器缺陷），不依赖 AOT 臂是否已跑
        if (a.AJitP0 == false)
            return Attribution.EnvironmentFault;
        if (a.AJitP0 is null || a.AAotP0 is null)
            return Attribution.Unknown;
        if (a.AAotP0 == false)
        {
            if (a.TrimJitP0 == true)
                return Attribution.DynamicCodeProblem;
            if (a.AotRootP0 == true)
                return Attribution.TrimRescuable;
            return a.TrimJitP0 is null && a.AotRootP0 is null
                ? Attribution.Unknown
                : Attribution.TrimUnrescuable;
        }
        if (a.BAotP0 is null)
            return Attribution.Unknown;
        if (a.BAotP0 == true)
            return Attribution.LibraryGuaranteed;
        return a.BAotRootP0 switch
        {
            true => Attribution.NeedsRoot,
            false => Attribution.MechanismEdge,
            _ => Attribution.Unknown,
        };
    }

    internal static string Symbol(Grade g) => g switch
    {
        Grade.Full => "✅",
        Grade.High => "🟦",
        Grade.Mid => "🟨",
        Grade.Low => "🟧",
        _ => "🟥",
    };

    internal static string Label(Attribution a) => a switch
    {
        Attribution.LibraryGuaranteed => "库级保障",
        Attribution.NeedsRoot => "需 root（应用引用保底）",
        Attribution.MechanismEdge => "机制边缘：无静态构造调用点即失效",
        Attribution.DynamicCodeProblem => "动态代码问题（裁剪可过）",
        Attribution.TrimRescuable => "裁剪问题（Root 可救）",
        Attribution.TrimUnrescuable => "裁剪问题（Root 不可救，机制不可用）",
        Attribution.EnvironmentFault => "环境故障/适配器缺陷（JIT 未过）",
        _ => "未知（归因臂数据不全）",
    };
}
