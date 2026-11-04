namespace PalORM.Integration.Tests.Conformance;

/// <summary>D 层场景模型与生成器（方案 §7.1，FUZ-1 扩维）。
/// <para>四个随机维度：值、条件形态、批量大小、操作序列；值面从 2 种列类型扩到 6 种
/// （long、string、int、short、double、bool），新增四种各有边界值池。生成完全由种子决定，
/// 同种子同场景；每轮打印实际种子，失败样本可独立重放。</para>
/// <para>宽表（<c>cf_fuzz_wide</c>）与三列表（<c>cf_bulk_plain</c>）在同一场景里一起跑：
/// 三列表覆盖批量与条件形态，宽表覆盖类型往返，两侧任一路径不一致都算该场景失败。</para></summary>
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
    int[] Quantities,
    short[] Scores,
    double[] Ratios,
    bool[] Flags,
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

    // FUZ-1：新增四种列类型的边界值池。整数取极值与 499/500/501 批宽边界；
    // double 取极值与 ±0（符号位不同，按位比较应判不等，正好压差分比较器的位比较语义）；
    // bool 只有两值，池子的意义在于让每行的 flag 都有确定性分布。
    private static readonly int[] BoundaryQuantities =
    [
        int.MinValue, -1, 0, 1, 42, 499, 500, 501, int.MaxValue
    ];

    private static readonly short[] BoundaryScores =
    [
        short.MinValue, -1, 0, 1, 42, short.MaxValue
    ];

    private static readonly double[] BoundaryRatios =
    [
        double.MinValue, -1.5, -0.0, 0d, double.Epsilon, 1.5, 499.5, double.MaxValue
    ];

    private static readonly bool[] BoundaryFlags = [false, true];

    /// <summary>按种子填一组值：每种列类型各有「边界池 1/3 + 常规区间 2/3」的确定性分布。
    /// 抽成方法是为了让 <see cref="Generate"/> 的认知复杂度留在限内（S3776）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394",
        Justification = "随机化测试用确定性伪随机数：需要同种子可复现，不能用加密随机源")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2245",
        Justification = "同上：同种子可复现是 D 层的核心要求，加密随机源恰好不能用")]
    private static (long[] Values, string[] Names, int[] Quantities, short[] Scores, double[] Ratios, bool[] Flags)
        FillValues(Random random, int count)
    {
        var values = new long[count];
        var names = new string[count];
        var quantities = new int[count];
        var scores = new short[count];
        var ratios = new double[count];
        var flags = new bool[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = random.Next(0, 3) == 0
                ? BoundaryNumbers[random.Next(BoundaryNumbers.Length)]
                : random.Next(-1000, 1001);
            names[i] = random.Next(0, 3) == 0
                ? BoundaryNames[random.Next(BoundaryNames.Length)]
                : $"n{random.Next(0, 1000)}";
            quantities[i] = random.Next(0, 3) == 0
                ? BoundaryQuantities[random.Next(BoundaryQuantities.Length)]
                : random.Next(-1000, 1001);
            scores[i] = random.Next(0, 3) == 0
                ? BoundaryScores[random.Next(BoundaryScores.Length)]
                : (short)random.Next(short.MinValue, short.MaxValue);
            ratios[i] = random.Next(0, 3) == 0
                ? BoundaryRatios[random.Next(BoundaryRatios.Length)]
                : random.NextDouble() * 2000d - 1000d;
            flags[i] = random.Next(0, 3) == 0
                ? BoundaryFlags[random.Next(BoundaryFlags.Length)]
                : random.Next(0, 2) == 0;
        }
        return (values, names, quantities, scores, ratios, flags);
    }

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

            // 场景内值样本数不超过 32，避免单场景过慢
            (long[] values, string[] names, int[] quantities, short[] scores, double[] ratios, bool[] flags) =
                FillValues(random, Math.Min(batchSize, 32));

            ConditionKind condition = (ConditionKind)random.Next(0, 8);
            OperationSequence sequence = (OperationSequence)random.Next(0, 4);
            scenarios.Add(new DScenario(
                seed, index, values, names, quantities, scores, ratios, flags, condition, batchSize, sequence));
        }
        return scenarios;
    }
}
