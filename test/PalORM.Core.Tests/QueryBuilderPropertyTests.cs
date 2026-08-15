using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// QueryBuilder 生成式性质测试（ITM-306/307 类缺陷的机械防线——"框架自产非法 SQL"根因类）。
/// 随机组合 Where/OrWhere/WhereIn/OrderBy/ThenBy/Skip/Take/GroupBy/Having 共 500 个种子序列，
/// 对每个产出 SQL 断言结构性质：括号平衡、无双 ORDER BY/WHERE/LIMIT、子句顺序合法、
/// 用户 OR 条件必须被括号包裹（软删过滤对 OR 分支不失效）。
/// 种子固定：失败时输出种子号与调用序列，可精确复现。
/// </summary>
public sealed class QueryBuilderPropertyTests
{
    private const int _iterations = 500;

    private static readonly string[] _uniqueClauses = ["ORDER BY", "WHERE", "LIMIT", "GROUP BY", "HAVING"];

    private static readonly string[] _clauseOrder = ["WHERE", "GROUP BY", "HAVING", "ORDER BY", "LIMIT"];

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

    [Test]
    public async Task RandomOperationSequences_ProduceStructurallyValidSql()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var violations = new List<string>();

        for (int seed = 0; seed < _iterations; seed++)
        {
            // 确定性伪随机（测试复现性需求，非安全用途——CA5394 不适用场景，用 LCG 自实现规避）
            var random = new DeterministicSequence(seed);
            var applied = new List<string>();
#pragma warning disable PALORM005 // 性质测试逐种子新建独立查询，非 N+1 场景
            QueryBuilder<PropertyProbeEntity> builder = session.From<PropertyProbeEntity>();
#pragma warning restore PALORM005

            int operationCount = random.Next(1, 8);
            for (int step = 0; step < operationCount; step++)
                builder = ApplyRandomOperation(builder, random, applied);

            string sql;
            try
            {
                sql = builder.ToSql();
            }
            catch (Exception exception)
            {
                violations.Add($"seed={seed} [{string.Join(" → ", applied)}] 构建抛异常: {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            foreach (string violation in CheckStructuralProperties(sql))
                violations.Add($"seed={seed} [{string.Join(" → ", applied)}] {violation}\n  SQL: {sql}");
        }

        await Assert.That(string.Join("\n", violations)).IsEmpty();
    }

    [Test]
    public async Task DefaultFilter_IsNeverBypassedByOrConditions()
    {
        // 性质 4 专项（ITM-307 残留变体，本性质测试首轮发现并修复）：
        // 软删实体上首个用户子句用 OrWhere 时，OR 不得与默认过滤同层——
        // 否则 WHERE deleted_at IS NULL OR (user) 使已删除行重新可见
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var violations = new List<string>();

        for (int seed = 0; seed < 100; seed++)
        {
            var random = new DeterministicSequence(seed);
#pragma warning disable PALORM005 // 性质测试逐种子新建独立查询，非 N+1 场景
            QueryBuilder<SoftDeleteValueSemanticsEntity> builder =
                session.From<SoftDeleteValueSemanticsEntity>();
#pragma warning restore PALORM005
            int operationCount = random.Next(1, 5);
            for (int step = 0; step < operationCount; step++)
            {
                builder = random.Next(2) == 0
                    ? builder.Where($"name = {seed}")
                    : builder.OrWhere($"name = {seed} OR name = {step}");
            }

            string sql = builder.ToSql();
            if (sql.Contains("IS NULL OR", StringComparison.Ordinal))
                violations.Add($"seed={seed} 默认过滤被 OR 绕过\n  SQL: {sql}");
        }

        await Assert.That(string.Join("\n", violations)).IsEmpty();
    }

    private static QueryBuilder<PropertyProbeEntity> ApplyRandomOperation(
        QueryBuilder<PropertyProbeEntity> builder, DeterministicSequence random, List<string> applied)
    {
        int choice = random.Next(9);
        switch (choice)
        {
            case 0:
                applied.Add("Where");
                return builder.Where($"price > {random.Next(1000)}");
            case 1:
                applied.Add("OrWhere");
                return builder.OrWhere($"stock = {random.Next(100)} OR price < {random.Next(10)}");
            case 2:
                applied.Add("WhereIn");
                return builder.WhereIn(static e => e.Stock,
                    Enumerable.Range(0, random.Next(1, 5)).Select(static i => (long)i));
            case 3:
                applied.Add("OrderBy");
                return builder.OrderBy(static e => e.Price, descending: random.Next(2) == 0);
            case 4:
                // ThenBy 契约要求前置 OrderBy（守卫抛 InvalidOperationException 属正确行为）
                if (!applied.Contains("OrderBy"))
                {
                    applied.Add("OrderBy");
                    return builder.OrderBy(static e => e.Price);
                }
                applied.Add("ThenBy");
                return builder.ThenBy(static e => e.Stock);
            case 5:
                applied.Add("Skip");
                return builder.Skip(random.Next(1, 100));
            case 6:
                applied.Add("Take");
                return builder.Take(random.Next(1, 100));
            case 7:
                applied.Add("GroupBy");
                return builder.GroupBy(static e => e.Name);
            default:
                applied.Add("Having");
                return builder.Having($"COUNT(*) > {random.Next(5)}");
        }
    }

    private static IEnumerable<string> CheckStructuralProperties(string sql)
    {
        // 性质 1：括号平衡
        int depth = 0;
        foreach (char c in sql)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (depth < 0) { yield return "括号在某处提前闭合"; break; }
        }
        if (depth > 0) yield return $"未闭合括号 {depth} 个";

        // 性质 2：关键子句最多出现一次（ITM-306 双 ORDER BY 教训）
        foreach (string keyword in _uniqueClauses)
        {
            int count = CountOccurrences(sql, keyword);
            if (count > 1)
                yield return $"{keyword} 出现 {count} 次";
        }

        // 性质 3：子句相对顺序 WHERE < GROUP BY < HAVING < ORDER BY < LIMIT
        var order = _clauseOrder
            .Select(k => (Keyword: k, Index: sql.IndexOf(k, StringComparison.Ordinal)))
            .Where(static pair => pair.Index >= 0)
            .ToArray();
        for (int i = 1; i < order.Length; i++)
        {
            if (order[i].Index < order[i - 1].Index)
                yield return $"{order[i].Keyword} 出现在 {order[i - 1].Keyword} 之前";
        }

        // 性质 4 移至 DefaultFilter_IsNeverBypassedByOrConditions 专项：
        // 无默认过滤实体上的顶层 (a) OR (b) 是用户显式语义，合法

        // 性质 5：无空 WHERE/悬挂 AND
        if (sql.Contains("WHERE  ", StringComparison.Ordinal)
            || sql.Contains("WHERE AND", StringComparison.Ordinal)
            || sql.TrimEnd().EndsWith("AND", StringComparison.Ordinal)
            || sql.TrimEnd().EndsWith("WHERE", StringComparison.Ordinal))
        {
            yield return "空 WHERE 或悬挂 AND";
        }
    }

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    /// <summary>确定性 xorshift32 序列——固定种子可复现；非安全场景。
    /// ITM-414：LCG 低位周期退化使 Next(2) 严格 0/1 交替，"连续两次 OrWhere"零覆盖——
    /// xorshift 全位混合后按高位取模，序列分布由 SequenceGenerator_CoversConsecutiveChoices 断言。</summary>
    private sealed class DeterministicSequence(int seed)
    {
        private uint _state = (uint)((seed * 2654435761u) + 1) | 1u;

        public int Next(int maxExclusive) => Next(0, maxExclusive);

        public int Next(int minInclusive, int maxExclusive)
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            // 乘法-移位取高位，避免低位偏差
            uint range = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(((ulong)_state * range) >> 32);
        }
    }

    [Test]
    public async Task SequenceGenerator_CoversConsecutiveChoices()
    {
        // ITM-414 分布断言：100 个种子内必须出现"连续两次相同二元选择"（旧 LCG 恒交替，永不出现）
        bool consecutiveSame = false;
        for (int seed = 0; seed < 100 && !consecutiveSame; seed++)
        {
            var random = new DeterministicSequence(seed);
            int previous = random.Next(2);
            for (int i = 0; i < 10; i++)
            {
                int current = random.Next(2);
                if (current == previous) { consecutiveSame = true; break; }
                previous = current;
            }
        }
        await Assert.That(consecutiveSame).IsTrue();
    }
}

#region Test Entities
[Table("property_probe")]
internal sealed partial class PropertyProbeEntity
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = string.Empty;
    [Column("price")]
    public decimal Price { get; set; }
    [Column("stock")]
    public long Stock { get; set; }
}
#endregion
