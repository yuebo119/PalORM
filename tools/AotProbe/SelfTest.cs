namespace PalORM.AotProbe;

/// <summary>selftest 变异电池（验证验证者，最小两用例）：
/// 用例 1 说谎适配器（不做任何 DB 工作、返回貌似合理的观测）必须被 Runner 判 fail——
/// 若判 pass，断言反转架构已失效；
/// 用例 2 判定规则表对合成死配置必须给出 🟥 及正确规则号（含归因决策表分支抽查）。</summary>
internal static class SelfTest
{
    internal static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        int failures = 0;
        failures += await CaseBadObservationAsync().ConfigureAwait(false);
        failures += CaseGradingRules();
        Console.WriteLine(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures} case(s))");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> CaseBadObservationAsync()
    {
        Console.WriteLine("== selftest case 1: bad observation must FAIL ==");
        string repoRoot = MatrixRunner.FindRepoRoot();
        var fake = new AotProbeManifest.Library("selftest-bad", AotProbeManifest.Tier.Watch,
            [new AotProbeManifest.PkgRef("Microsoft.Data.Sqlite", "10.0.14")], AdapterReady: true,
            OfficialAotPath: "selftest-only", VerifiedMaintained: "-",
            Note: "变异注入用例，不在清单（包引用仅为 RawChannel 的编译依赖）");

        ArmRun arm = await MatrixRunner.RunArmAsync(repoRoot, fake, ProbeGroup.A, ProbeForm.Jit, "unused-rid").ConfigureAwait(false);
        ProbeRunResult? probe = arm.Probes.FirstOrDefault(p => p.Probe == nameof(ProbeId.P0InsertAutoPk));

        bool rejected = probe is { Status: "fail" } && probe.Assertions.Count > 0;
        Console.WriteLine(rejected
            ? "PASS: liar adapter rejected (status=fail with assertion evidence)"
            : $"FAIL: harness accepted bad observation (status={probe?.Status.ToString() ?? "absent"})");
        return rejected ? 0 : 1;
    }

    private static int CaseGradingRules()
    {
        Console.WriteLine("== selftest case 2: grading rules on synthetic facts ==");
        int bad = 0;

        // v2.2 判定（2026-10-29）：P0 用 AOT 特有通过率；告警只拦 ✅（裁剪得当不降档）
        var dead1 = new GradeInput(false, true, true, new LayerStat(8, 8), new LayerStat(8, 8), new LayerStat(0, 0), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 0);
        bad += Check(GradingRules.Evaluate(dead1), Grade.Unsupported, "R1");

        var dead2 = new GradeInput(true, true, true, new LayerStat(1, 8), new LayerStat(8, 8), new LayerStat(0, 0), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 0);
        bad += Check(GradingRules.Evaluate(dead2), Grade.Unsupported, "R4");

        var partial = new GradeInput(true, true, true, new LayerStat(2, 8), new LayerStat(8, 8), new LayerStat(0, 0), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 0);
        bad += Check(GradingRules.Evaluate(partial), Grade.Partial, "R5");

        var low = new GradeInput(true, true, true, new LayerStat(4, 8), new LayerStat(8, 8), new LayerStat(0, 0), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 0);
        bad += Check(GradingRules.Evaluate(low), Grade.Low, "R6");

        var mid = new GradeInput(true, true, true, new LayerStat(7, 8), new LayerStat(8, 8), new LayerStat(0, 0), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 0);
        bad += Check(GradingRules.Evaluate(mid), Grade.Mid, "R7");

        // v2.2 关键回归：P0 全过 + 告警 → 🟦（裁剪得当不降档，SQLite-net / RepoDb 实例）
        var warnHigh = new GradeInput(true, true, true, new LayerStat(8, 8), new LayerStat(8, 8), new LayerStat(0, 0), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 7);
        bad += Check(GradingRules.Evaluate(warnHigh), Grade.High, "R8");

        // v2.5 关键回归：告警 ≥10 条封顶 🟨（SQLite-net 12 条实例）
        var warnMid = new GradeInput(true, true, true, new LayerStat(8, 8), new LayerStat(8, 8), new LayerStat(0, 0), new LayerStat(0, 0), new LayerStat(0, 0), new LayerStat(0, 0), 12);
        bad += Check(GradingRules.Evaluate(warnMid), Grade.Mid, "R8b");

        // P1-P3 缺口同样 🟦
        var advHigh = new GradeInput(true, true, true, new LayerStat(8, 8), new LayerStat(8, 8), new LayerStat(0, 0), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(2, 3), 0);
        bad += Check(GradingRules.Evaluate(advHigh), Grade.High, "R8");

        // 零告警全过 = 无条件支持
        var full = new GradeInput(true, true, true, new LayerStat(8, 8), new LayerStat(8, 8), new LayerStat(0, 0), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 0);
        bad += Check(GradingRules.Evaluate(full), Grade.Full, "R9");

        // JIT 同败剔除回归：P0 AOT 6/8、JIT 6/8 = AOT 特有 100% + 12 告警 → 按 v2.5 封顶 🟨
        var jitExcluded = new GradeInput(true, true, true, new LayerStat(6, 8), new LayerStat(6, 8), new LayerStat(0, 0), new LayerStat(0, 0), new LayerStat(0, 0), new LayerStat(0, 0), 12);
        bad += Check(GradingRules.Evaluate(jitExcluded), Grade.Mid, "R8b");

        // v2.3 关键回归：默认形态不可用但 Root 后可救 → 部分实现（需配置）
        var rootRescue = new GradeInput(true, true, true, new LayerStat(0, 8), new LayerStat(8, 8), new LayerStat(6, 6), new LayerStat(0, 0), new LayerStat(0, 0), new LayerStat(0, 0), 0);
        bad += Check(GradingRules.Evaluate(rootRescue), Grade.Partial, "R5c");

        bad += CheckAttr(new ArmInput(null, null, null, null, null, null), Attribution.Unknown);
        bad += CheckAttr(new ArmInput(false, null, null, null, null, null), Attribution.EnvironmentFault);
        bad += CheckAttr(new ArmInput(true, false, null, null, null, null), Attribution.Unknown);
        bad += CheckAttr(new ArmInput(true, false, true, null, null, null), Attribution.DynamicCodeProblem);
        bad += CheckAttr(new ArmInput(true, false, false, true, null, null), Attribution.TrimRescuable);
        bad += CheckAttr(new ArmInput(true, false, false, false, null, null), Attribution.TrimUnrescuable);
        bad += CheckAttr(new ArmInput(true, true, null, null, true, null), Attribution.LibraryGuaranteed);
        bad += CheckAttr(new ArmInput(true, true, null, null, false, true), Attribution.NeedsRoot);
        bad += CheckAttr(new ArmInput(true, true, null, null, false, false), Attribution.MechanismEdge);

        Console.WriteLine(bad == 0 ? "PASS: 9 grade rules (v2.5) + 9 attribution branches" : $"FAIL: {bad} rule mismatches");
        return bad;
    }

    private static int Check(GradeResult actual, Grade expectedGrade, string expectedRule)
    {
        bool ok = actual.Grade == expectedGrade && actual.RuleId == expectedRule;
        if (!ok)
            Console.WriteLine($"  mismatch: expected {expectedGrade}/{expectedRule}, got {actual.Grade}/{actual.RuleId} ({actual.RuleText})");
        return ok ? 0 : 1;
    }

    private static int CheckAttr(ArmInput input, Attribution expected)
    {
        Attribution actual = GradingRules.Attribute(input);
        bool ok = actual == expected;
        if (!ok)
            Console.WriteLine($"  attribution mismatch: expected {expected}, got {actual}");
        return ok ? 0 : 1;
    }
}
