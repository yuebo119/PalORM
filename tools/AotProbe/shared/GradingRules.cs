namespace PalORM.AotProbe;

internal enum Grade { Unsupported, Partial, Low, Mid, High, Full }

/// <summary>归因标签（决策表定式，评审 2026-10-28）。只挂注、不改档。</summary>
internal enum Attribution
{
    LibraryGuaranteed,
    NeedsRoot,
    MechanismEdge,
    DynamicCodeProblem,
    TrimRescuable,
    TrimUnrescuable,
    /// <summary>JIT 对照臂未过：环境故障/适配器缺陷，或库固有行为缺陷（JIT/AOT 同败，非 AOT 特有
    /// 结论——2026-10-28 sqlite-net decimal 精度损坏实例）。需人工判读消歧，该库 B 组不跑。</summary>
    EnvironmentFault,
    /// <summary>归因臂数据不全（例如 B 组未跑）。</summary>
    Unknown,
}

internal sealed record LayerStat(int Pass, int Applicable)
{
    public bool AllPass => Applicable > 0 && Pass == Applicable;
    public bool AnyApplicable => Applicable > 0;
}

/// <summary>判定输入（v2.3）：P0 用 AOT 特有通过率（JIT 同败从分母剔除）；
/// P0Root = A 组 TrimmerRootAssembly 诊断臂（默认形态失败后的配置兜底形态）。</summary>
internal sealed record GradeInput(
    bool CompileOk, bool IlcOk, bool StartupOk,
    LayerStat P0, LayerStat P0Jit, LayerStat P0Root, LayerStat P1, LayerStat P2, LayerStat P3,
    int UnmitigatedLibWarningCount)
{
    /// <summary>AOT 特有 P0 通过率：JIT 通过集合内 AOT 也通过的比例。
    /// JIT 未过的探针不计入分母（库在本平台固有失败，非 AOT 责任）。
    /// JIT 臂本身全败（P0Jit.Pass = 0）时无有效分母，返回 -1 表示"无法判定"。</summary>
    public double AotSpecificP0Rate =>
        P0Jit.Applicable == 0 || P0Jit.Pass == 0
            ? -1
            : Math.Min(1.0, (double)P0.Pass / P0Jit.Pass);

    /// <summary>Root 配置形态通过率（Root 臂未跑时为 -1，不参与判定）。</summary>
    public double RootP0Rate =>
        P0Root.Applicable == 0 ? -1 : Math.Min(1.0, (double)P0Root.Pass / Math.Max(1, P0Jit.Pass));
}

/// <summary>归因输入：升级阶梯 + B 组各臂的 P0 结果。null = 该臂未跑（避免"缺失臂"被
/// 误读成"臂失败"——归因标签必须区分未测与失败）。</summary>
internal sealed record ArmInput(
    bool? AJitP0, bool? AAotP0, bool? TrimJitP0, bool? AotRootP0, bool? BAotP0, bool? BAotRootP0);

internal sealed record GradeResult(Grade Grade, string RuleId, string RuleText);

internal static class GradingRules
{
    public const string RulesVersion = "v2.3-2026-10-29";

    /// <summary>判定模型 v2.3（用户裁决 2026-10-29 三轮收敛）：
    /// <para>v2：P0 分母 = JIT 通过集合（库固有缺陷不扣 AOT 分）。</para>
    /// <para>v2.2：告警只拦 ✅，裁剪得当不降档（用户原话「API 在裁剪，裁剪的好，也算 aot」）。</para>
    /// <para>v2.3（用户追问「还能再宽松点吗」）：默认形态失败但 TrimmerRootAssembly 后可救
    /// 的库，从 🟥 升 🟹（需配置）——Root 是合法的 AOT 部署形态，审计已实测 FreeSql Root 后 9/9、
    /// LiteDB Root 后 7/7。低于此线的宽松（0% 通过率评为部分可用）无证据支撑，不做。</para></summary>
    internal static GradeResult Evaluate(GradeInput f)
    {
        if (!f.CompileOk)
            return new(Grade.Unsupported, "R1", "编译失败");
        if (!f.IlcOk)
            return new(Grade.Unsupported, "R2", "ILC 发布失败");
        if (!f.StartupOk)
            return new(Grade.Unsupported, "R3", "原生启动失败");

        GradeResult? byRate = EvaluateByRate(f);
        if (byRate is not null)
            return byRate;
        return EvaluateFullPass(f);
    }

    /// <summary>通过率分层（R4-R7 + R5c Root 兜底）。</summary>
    private static GradeResult? EvaluateByRate(GradeInput f)
    {
        double rate = f.AotSpecificP0Rate;
        string rateText = $"{f.P0.Pass}/{f.P0Jit.Pass}（AOT 特有）";
        if (rate < 0)
            return new(Grade.Unsupported, "R3b",
                $"JIT 对照臂 P0 全败（{f.P0Jit.Pass}/{f.P0Jit.Applicable}），环境或适配器故障，非 AOT 结论");
        if (rate < 0.25)
        {
            // v2.3：Root 配置形态可救 → 部分实现（需配置），比"不支持"准确
            if (f.RootP0Rate >= 0.75)
            {
                return new(Grade.Partial, "R5c",
                    $"默认形态 {rateText} 不可用，但 TrimmerRootAssembly 后 {f.P0Root.Pass}/{Math.Max(1, f.P0Jit.Pass)} 通过"
                    + "（需配置：库本身可在 AOT 下工作，应用须显式保根）");
            }
            return new(Grade.Unsupported, "R4", $"AOT 特有 P0 通过率 {rateText} < 25%");
        }
        if (rate < 0.50)
            return new(Grade.Partial, "R5", $"AOT 特有 P0 通过率 {rateText}，特定场景可用");
        if (rate < 0.75)
            return new(Grade.Low, "R6", $"AOT 特有 P0 通过率 {rateText}");
        if (rate < 1.0)
            return new(Grade.Mid, "R7", $"AOT 特有 P0 通过率 {rateText}");
        return null;
    }

    /// <summary>P0 全过后的 🟨/🟦/✅ 分层（v2.5 用户裁决 2026-10-29：告警数分级）。
    /// 告警数量近似"未经证明的 API 面"：≥10 条封顶 🟨（SQLite-net 12 条实例），
    /// 1-9 条封顶 🟦（RepoDb 7 条、Dapper.AOT 1 条抑制），零告警才可 ✅。</summary>
    private static GradeResult EvaluateFullPass(GradeInput f)
    {
        int warnings = f.UnmitigatedLibWarningCount;
        if (warnings >= 10)
            return new(Grade.Mid, "R8b",
                $"AOT 特有 P0 全过，但 {warnings} 条库归属 IL 告警（未验证 API 面大，≥10 条封顶中）");
        bool advancedGaps = (f.P1.AnyApplicable && !f.P1.AllPass)
            || (f.P2.AnyApplicable && !f.P2.AllPass)
            || (f.P3.AnyApplicable && !f.P3.AllPass);
        if (warnings > 0 || advancedGaps)
        {
            string detail = warnings > 0
                ? $"{warnings} 条库归属 IL 告警（裁剪兜底需求，已登记注记）"
                : "P1-P3 有缺口";
            return new(Grade.High, "R8", $"AOT 特有 P0 全过，但有{detail}");
        }
        return new(Grade.Full, "R9", "AOT 特有 P0-P3 全过 + 零未缓解库归属告警（无条件支持）");
    }

    /// <summary>归因决策表（2026-10-28 定式）：AOT 失败先分动态代码/裁剪，成功再分库级/兜底。
    /// 未跑的臂（null）不得臆断为失败，一律落 Unknown。</summary>
    internal static Attribution Attribute(ArmInput a)
    {
        // AOT 臂优先分析：JIT 部分败（如 decimal 库固有缺口）不应遮蔽 AOT 特有失败的故事线；
        // JIT 对照失败仅在 AOT 臂未跑时单独定性
        if (a.AAotP0 is null)
            return a.AJitP0 == false ? Attribution.EnvironmentFault : Attribution.Unknown;
        if (a.AJitP0 is null)
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
        Grade.Partial => "🟹",
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
        Attribution.EnvironmentFault => "JIT 对照未过（环境/适配器缺陷或库固有缺陷，非 AOT 特有）",
        _ => "未知（归因臂数据不全）",
    };
}
