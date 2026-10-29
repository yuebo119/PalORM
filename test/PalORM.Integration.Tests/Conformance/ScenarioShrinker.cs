namespace PalORM.Integration.Tests.Conformance;

/// <summary>失败场景收缩器（方案 §7.3）。
/// <para>五步贪心：值列表减半、逐项删除、值替换为最简、条件降级为 None、操作序列降级为
/// InsertThenQuery。每步只在"收缩后仍然失败"时才接受，因此收缩结果仍能复现原失败。
/// 迭代上限防止在"简化不改变失败性"的退化路径上打转。</para>
/// <para>收缩器本身不碰数据库：失败谓词由调用方注入，单元测试用纯逻辑谓词验证。</para></summary>
internal static class ScenarioShrinker
{
    private const int MaxIterations = 80;

    public static async Task<DScenario> ShrinkAsync(
        DScenario scenario, Func<DScenario, Task<bool>> stillFails)
    {
        DScenario current = scenario;
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            DScenario? next = await TryShrinkOnceAsync(current, stillFails);
            if (next is null) return current;
            current = next;
        }
        return current;
    }

    private static async Task<DScenario?> TryShrinkOnceAsync(
        DScenario current, Func<DScenario, Task<bool>> stillFails)
        => await ShrinkBatchSizeAsync(current, stillFails)
            ?? await ShrinkValuesByHalfAsync(current, stillFails)
            ?? await ShrinkByRemovalAsync(current, stillFails)
            ?? await ShrinkValueSimplificationAsync(current, stillFails)
            ?? await ShrinkConditionAsync(current, stillFails)
            ?? await ShrinkSequenceAsync(current, stillFails);

    /// <summary>批量大小减半：大小是四个随机维度之一，失败与它相关时必须能压下来。</summary>
    private static async Task<DScenario?> ShrinkBatchSizeAsync(
        DScenario current, Func<DScenario, Task<bool>> stillFails)
    {
        if (current.BatchSize <= 1) return null;
        DScenario candidate = current with { BatchSize = Math.Max(1, current.BatchSize / 2) };
        return await stillFails(candidate) ? candidate : null;
    }

    private static async Task<DScenario?> ShrinkValuesByHalfAsync(
        DScenario current, Func<DScenario, Task<bool>> stillFails)
    {
        if (current.Values.Length <= 1) return null;
        int half = current.Values.Length / 2;
        for (int start = 0; start < 2; start++)
        {
            DScenario candidate = Slice(current, start * half, half);
            if (await stillFails(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<DScenario?> ShrinkByRemovalAsync(
        DScenario current, Func<DScenario, Task<bool>> stillFails)
    {
        for (int i = 0; i < current.Values.Length && current.Values.Length > 1; i++)
        {
            DScenario candidate = RemoveAt(current, i);
            if (await stillFails(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<DScenario?> ShrinkValueSimplificationAsync(
        DScenario current, Func<DScenario, Task<bool>> stillFails)
    {
        for (int i = 0; i < current.Values.Length; i++)
        {
            if (current.Values[i] != 0L)
            {
                long[] values = [.. current.Values];
                values[i] = 0L;
                DScenario candidate = current with { Values = values };
                if (await stillFails(candidate)) return candidate;
            }
            if (current.Names[i].Length > 0)
            {
                string[] names = [.. current.Names];
                names[i] = "";
                DScenario candidate = current with { Names = names };
                if (await stillFails(candidate)) return candidate;
            }
        }
        return null;
    }

    private static async Task<DScenario?> ShrinkConditionAsync(
        DScenario current, Func<DScenario, Task<bool>> stillFails)
    {
        if (current.Condition == ConditionKind.None) return null;
        DScenario candidate = current with { Condition = ConditionKind.None };
        return await stillFails(candidate) ? candidate : null;
    }

    private static async Task<DScenario?> ShrinkSequenceAsync(
        DScenario current, Func<DScenario, Task<bool>> stillFails)
    {
        if (current.Sequence == OperationSequence.InsertThenQuery) return null;
        DScenario candidate = current with { Sequence = OperationSequence.InsertThenQuery };
        return await stillFails(candidate) ? candidate : null;
    }

    private static DScenario Slice(DScenario scenario, int start, int count)
    {
        count = Math.Min(count, scenario.Values.Length - start);
        return scenario with
        {
            Values = scenario.Values[start..(start + count)],
            Names = scenario.Names[start..(start + count)]
        };
    }

    private static DScenario RemoveAt(DScenario scenario, int index)
    {
        var values = new long[scenario.Values.Length - 1];
        var names = new string[scenario.Names.Length - 1];
        Array.Copy(scenario.Values, 0, values, 0, index);
        Array.Copy(scenario.Values, index + 1, values, index, values.Length - index);
        Array.Copy(scenario.Names, 0, names, 0, index);
        Array.Copy(scenario.Names, index + 1, names, index, names.Length - index);
        return scenario with { Values = values, Names = names };
    }
}