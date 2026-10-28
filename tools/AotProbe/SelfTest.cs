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
            [], AdapterReady: true, OfficialAotPath: "selftest-only", VerifiedMaintained: "-",
            Note: "变异注入用例，不在清单");

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

        var dead1 = new GradeInput(false, true, true, new LayerStat(8, 8), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 0);
        bad += Check(GradingRules.Evaluate(dead1), Grade.Unsupported, "R1");

        var dead2 = new GradeInput(true, true, true, new LayerStat(6, 8), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 0);
        bad += Check(GradingRules.Evaluate(dead2), Grade.Unsupported, "R4");

        var high = new GradeInput(true, true, true, new LayerStat(8, 8), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(2, 3), 0);
        bad += Check(GradingRules.Evaluate(high), Grade.High, "R7");

        var warn = new GradeInput(true, true, true, new LayerStat(8, 8), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 2);
        bad += Check(GradingRules.Evaluate(warn), Grade.High, "R7");

        var full = new GradeInput(true, true, true, new LayerStat(8, 8), new LayerStat(6, 6), new LayerStat(6, 6), new LayerStat(3, 3), 0);
        bad += Check(GradingRules.Evaluate(full), Grade.Full, "R8");

        bad += CheckAttr(new ArmInput(null, null, null, null, null, null), Attribution.Unknown);
        bad += CheckAttr(new ArmInput(false, null, null, null, null, null), Attribution.EnvironmentFault);
        bad += CheckAttr(new ArmInput(true, false, null, null, null, null), Attribution.Unknown);
        bad += CheckAttr(new ArmInput(true, false, true, null, null, null), Attribution.DynamicCodeProblem);
        bad += CheckAttr(new ArmInput(true, false, false, true, null, null), Attribution.TrimRescuable);
        bad += CheckAttr(new ArmInput(true, false, false, false, null, null), Attribution.TrimUnrescuable);
        bad += CheckAttr(new ArmInput(true, true, null, null, true, null), Attribution.LibraryGuaranteed);
        bad += CheckAttr(new ArmInput(true, true, null, null, false, true), Attribution.NeedsRoot);
        bad += CheckAttr(new ArmInput(true, true, null, null, false, false), Attribution.MechanismEdge);

        Console.WriteLine(bad == 0 ? "PASS: 5 grade rules + 9 attribution branches" : $"FAIL: {bad} rule mismatches");
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
