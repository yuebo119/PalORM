namespace PalORM.Integration.Tests.Conformance;

/// <summary>收缩器单元测试（方案 §7.3）。
/// <para>收缩器不碰数据库：失败谓词由测试注入纯逻辑，验证五步贪心确实把场景压到最小，
/// 且收缩结果仍满足原失败谓词（否则收缩就等于把缺陷弄丢了）。</para></summary>
public sealed class ShrinkerTests
{
    private static DScenario Scenario(
        long[] values, ConditionKind condition = ConditionKind.None,
        int batchSize = 5, OperationSequence sequence = OperationSequence.InsertThenQuery)
        => new(1, 0, values, [.. values.Select(v => $"n{v}")], condition, batchSize, sequence);

    [Test]
    public async Task Shrink_ReducesValuesToTheOneThatTriggers()
    {
        // 谓词：只有值列表包含 7 才失败
        DScenario scenario = Scenario([1, 2, 3, 7, 9]);
        DScenario shrunk = await ScenarioShrinker.ShrinkAsync(
            scenario, candidate => Task.FromResult(candidate.Values.Contains(7L)));

        await Assert.That(shrunk.Values.Length).IsEqualTo(1);
        await Assert.That(shrunk.Values[0]).IsEqualTo(7L);
    }

    [Test]
    public async Task Shrink_KeepsConditionWhenItIsTheTrigger()
    {
        // 谓词：只有 Mixed 条件才失败，与值无关
        DScenario scenario = Scenario([1, 2, 3, 4], ConditionKind.Mixed, batchSize: 8);
        DScenario shrunk = await ScenarioShrinker.ShrinkAsync(
            scenario, candidate => Task.FromResult(candidate.Condition == ConditionKind.Mixed));

        // 条件不能降级（降级后谓词不再成立），值可以压到 1 个
        await Assert.That(shrunk.Condition).IsEqualTo(ConditionKind.Mixed);
        await Assert.That(shrunk.Values.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Shrink_StopsAtMinimalBatchSizeSatisfyingThePredicate()
    {
        // 谓词：批量大小必须大于 100。减半会跌破阈值，因此应停在 101
        DScenario scenario = Scenario([1], batchSize: 101);
        DScenario shrunk = await ScenarioShrinker.ShrinkAsync(
            scenario, candidate => Task.FromResult(candidate.BatchSize > 100));

        await Assert.That(shrunk.BatchSize).IsEqualTo(101);
    }

    [Test]
    public async Task Shrink_TerminatesWhenEverythingFails()
    {
        // 谓词恒真：收缩会一路降到最小形态后返回，不得无限循环
        DScenario scenario = Scenario([1, 2, 3, 4, 5], ConditionKind.Like, batchSize: 500);
        DScenario shrunk = await ScenarioShrinker.ShrinkAsync(scenario, _ => Task.FromResult(true));

        await Assert.That(shrunk.Values.Length).IsEqualTo(1);
        await Assert.That(shrunk.BatchSize).IsEqualTo(1);
        await Assert.That(shrunk.Condition).IsEqualTo(ConditionKind.None);
        await Assert.That(shrunk.Sequence).IsEqualTo(OperationSequence.InsertThenQuery);
    }

    [Test]
    public async Task Generate_IsDeterministicForSameSeed()
    {
        IReadOnlyList<DScenario> first = DGenerator.Generate(20261029, 6);
        IReadOnlyList<DScenario> second = DGenerator.Generate(20261029, 6);

        for (int i = 0; i < first.Count; i++)
        {
            await Assert.That(first[i].ReplayKey).IsEqualTo(second[i].ReplayKey);
        }
    }

    [Test]
    public async Task Generate_DifferentSeedsProduceDifferentScenarios()
    {
        IReadOnlyList<DScenario> a = DGenerator.Generate(1, 4);
        IReadOnlyList<DScenario> b = DGenerator.Generate(2, 4);
        await Assert.That(
            string.Join("|", a.Select(s => s.ReplayKey)))
            .IsNotEqualTo(string.Join("|", b.Select(s => s.ReplayKey)));
    }
}