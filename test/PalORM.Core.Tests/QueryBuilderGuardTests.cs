using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// QueryBuilder 入参防线的守卫测试（ITM-885/886，r24 批次 B）。
/// ITM-885：WhereIn 参数守卫的 LIMIT 余量按 BuildLimitClause 实际建参形态核算——
/// take-only 非字面量时 MySQL/default 均建 2 个参数（take + skip??0 参数化的 0），
/// 修复前只 reserve 1，"存量+增量+1 恰等于上限"时放行实际越界 1 个的 SQL（ITM-514/562/827
/// 要提前拒绝的形态）。SQLite 上限 999：998 个 WhereIn 值 + Take(1) 无 Skip →
/// 修复前 998+1=999 不放行守卫（实际建 998+2=1000 越界）；修复后 998+2=1000 > 999 抛。
/// ITM-886：UnsafeWindowOver 补控制字符防线（与 Raw 同款，ITM-584 NUL 截断向量）——
/// 修复前 NUL 直接进 SQL 文本。
/// </summary>
internal sealed class QueryBuilderGuardTests
{
    [Test]
    public async Task WhereIn_TakeOnly_AtExactLimit_RejectedBeforeProtocol()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        QueryBuilder<GuardProbeEntity> builder = session.From<GuardProbeEntity>();

        long[] values = [.. Enumerable.Range(1, 998).Select(static i => (long)i)];
        // Take 必须先于 WhereIn——守卫在 WhereIn 时点读取 _take/_skip 核算余量。
        // 修复前：998 + reserve 1 = 999 不大于上限 → 放行（执行期实际建 998+2=1000 个参数）；
        // 修复后：reserve 2 → 1000 > 999 构造期拒绝（撤修复此断言变红——Throws 不发生）
        await Assert.That(() => builder.Take(2).WhereIn(static e => e.Id, values))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task WhereIn_TakeOnly_OneBelowLimit_StillAllowed()
    {
        // 守卫不放行过度：余量核算后的合法边界（997 值 + 2 LIMIT 参数 = 999）必须继续放行
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        QueryBuilder<GuardProbeEntity> builder = session.From<GuardProbeEntity>();

        long[] values = [.. Enumerable.Range(1, 997).Select(static i => (long)i)];
        DryRunResult preview = builder.Take(2).WhereIn(static e => e.Id, values).AsDryRun();
        await Assert.That(preview.Sql.Contains("IN (", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task UnsafeWindowOver_ControlCharacter_Rejected()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        QueryBuilder<GuardProbeEntity> builder = session.From<GuardProbeEntity>();

        // 修复前：NUL 与换行直接拼进 SQL 文本（三入口防线不一致——Raw 有 Tag 有，本入口没有）
        await Assert.That(() => builder.UnsafeWindowOver("ROW_NUMBER()", "PARTITION BY \0x ORDER BY id"))
            .Throws<ArgumentException>();
        await Assert.That(() => builder.UnsafeWindowOver("ROW_NUMBER()\n", "PARTITION BY parent_id"))
            .Throws<ArgumentException>();
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        return await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = $"Data Source=guard_probe_{Guid.NewGuid():N};Mode=Memory",
            MaxRetries = 0,
            CircuitBreakerThreshold = 0,
        });
    }
}

[Table("guard_probe")]
internal sealed partial class GuardProbeEntity
{
    [Key] public long Id { get; set; }
    [Column("value")] public long Value { get; set; }
}
