namespace PalORM.Integration.Tests.Conformance;

/// <summary>D 层场景模型与生成器（方案 §7.1）。
/// <para>四个随机维度：值、条件形态、批量大小、操作序列。生成完全由种子决定，同种子同场景；
/// 每轮打印实际种子，失败样本可独立重放。</para>
/// <para>类型面只取 long 与 string 两列（`cf_bulk_plain` 的 amount 与 name）：D 层的作用是把
/// A 层固定网格放大到网格之外，不是复刻 S01 的 16 类型矩阵（那由 A 层承担）。</para></summary>
internal enum ConditionKind
{
    None,
    Single,
    AndChain,
    OrChain,
    Mixed,
    InEmpty,
    InFull,
    Like
}

internal enum OperationSequence
{
    InsertThenQuery,
    InsertThenDeleteThenQuery,
    QueryThenInsertThenQuery,
    UpdateThenQuery
}

internal sealed record DScenario(
    int Seed,
    int Index,
    long[] Values,
    string[] Names,
    ConditionKind Condition,
    int BatchSize,
    OperationSequence Sequence)
{
    public string Id => $"seed-{this.Seed}/case-{this.Index}";

    /// <summary>可独立重放的三元组（种子、场景 ID、值），落盘用。</summary>
    public string ReplayKey
        => $"{this.Seed}|{this.Id}|{string.Join(",", this.Values)}|{this.Condition}|{this.BatchSize}|{this.Sequence}";
}

internal static class DGenerator
{
    /// <summary>值池：覆盖 null 语义之外的边界（0、负数、long 极值、空串、Unicode、超长、引号转义）。</summary>
    private static readonly long[] BoundaryNumbers =
    [
        0L, 1L, -1L, 42L, long.MinValue, long.MaxValue, 499L, 500L, 501L
    ];

    private static readonly string[] BoundaryNames =
    [
        "", "a", "p-prefix", "q-prefix", "quote'inside", "slash\\inside", "emoji-🙂", "rtl-שלום", new string('x', 200)
    ];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394",
        Justification = "随机化测试用确定性伪随机数：需要同种子可复现，不能用加密随机源")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2245",
        Justification = "同上：同种子可复现是 D 层的核心要求，加密随机源恰好不能用")]
    public static IReadOnlyList<DScenario> Generate(int seed, int count)
    {
        var random = new Random(seed);
        var scenarios = new List<DScenario>(count);
        for (int index = 0; index < count; index++)
        {
            // 批量大小：偏置到边界值（1/2/499/500/501）与常规区间
            int batchSize = random.Next(0, 5) switch
            {
                0 => 1,
                1 => 2,
                2 => 499,
                3 => 500,
                _ => random.Next(1, 40)
            };

            int valueCount = Math.Min(batchSize, 32);   // 场景内值样本数不超过 32，避免单场景过慢
            var values = new long[valueCount];
            var names = new string[valueCount];
            for (int i = 0; i < valueCount; i++)
            {
                values[i] = random.Next(0, 3) == 0
                    ? BoundaryNumbers[random.Next(BoundaryNumbers.Length)]
                    : random.Next(-1000, 1001);
                names[i] = random.Next(0, 3) == 0
                    ? BoundaryNames[random.Next(BoundaryNames.Length)]
                    : $"n{random.Next(0, 1000)}";
            }

            ConditionKind condition = (ConditionKind)random.Next(0, 8);
            OperationSequence sequence = (OperationSequence)random.Next(0, 4);
            scenarios.Add(new DScenario(seed, index, values, names, condition, batchSize, sequence));
        }
        return scenarios;
    }
}