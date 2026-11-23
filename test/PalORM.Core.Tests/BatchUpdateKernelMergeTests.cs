namespace PalORM.Core.Tests;

/// <summary>版本化内核合一的形状锁定（D3，2026-11-23）。
/// <para>背景：<c>ExecuteBulkUpdate*BatchesVersionedAsync</c> 原为与 <c>*BatchesAsync</c> 逐字同构的
/// 两份内核，合并后只有本体 + 可选版本槽。合并的危险点在于「槽位偏移错一格」——少绑或多绑版本令牌
/// 不会在编译期暴露，只会在驱动侧以「参数未被使用 / 缺少参数」报错，或在乐观锁上静默失效。
/// 本用例锁定可分式的不变量：<b>每行恰好多一个参数</b>，且版本化形态出现自增表达式、非版本化不出现。</para></summary>
public sealed class BatchUpdateKernelMergeTests
{
    private static readonly string[] s_setColumns = ["\"a\""];

    /// <summary>PG 数组形态（UNNEST）：版本化只允许该形态（Builder 对其它形态抛 NotSupported）。
    /// 参数名为 <c>@uN</c>（每列一个），故按 <c>@u</c> 计数。</summary>
    [Test]
    public async Task PostgreSql_VersionedShape_AddsExactlyOneParameterPerRow()
    {
        string plain = BatchUpdateSqlBuilder.Build(
            SqlDialect.PostgreSql, "\"t\"", "\"id\"", s_setColumns, rowCount: 2,
            hasTenantFilter: false, tenantParameterName: "@__tenant0",
            form: BatchUpdateSqlBuilder.BatchUpdateForm.UnnestArrays);
        string versioned = BatchUpdateSqlBuilder.Build(
            SqlDialect.PostgreSql, "\"t\"", "\"id\"", s_setColumns, rowCount: 2,
            hasTenantFilter: false, tenantParameterName: "@__tenant0",
            form: BatchUpdateSqlBuilder.BatchUpdateForm.UnnestArrays,
            quotedConcurrencyColumn: "\"version\"");

        await Assert.That(Count(versioned, "@u") - Count(plain, "@u")).IsEqualTo(1)
            .Because("数组形态每列一个参数，与行数无关：版本化恰好多一列参数（版本令牌数组）");
        await Assert.That(plain.Contains("\"version\"", StringComparison.Ordinal)).IsFalse()
            .Because("非版本化形态不得引用版本列");
        await Assert.That(versioned.Contains("\"version\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(versioned.Contains("+ 1", StringComparison.Ordinal)).IsTrue()
            .Because("版本化形态必须带自增表达式（乐观锁推进）");
    }

    /// <summary>MySQL JoinValuesRow 形态（8.0.19+ 走 UPDATE JOIN 值构造器）。</summary>
    [Test]
    public async Task MySql_JoinValuesRow_VersionedShape_AddsExactlyOneParameterPerRow()
    {
        string plain = BatchUpdateSqlBuilder.Build(
            SqlDialect.MySql, "`t`", "`id`", ["`a`"], rowCount: 3,
            hasTenantFilter: false, tenantParameterName: "@__tenant0",
            form: BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow);
        string versioned = BatchUpdateSqlBuilder.Build(
            SqlDialect.MySql, "`t`", "`id`", ["`a`"], rowCount: 3,
            hasTenantFilter: false, tenantParameterName: "@__tenant0",
            form: BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow,
            quotedConcurrencyColumn: "`version`");

        await Assert.That(Count(versioned, "@p") - Count(plain, "@p")).IsEqualTo(3)
            .Because("版本化形态每行恰好多一个版本令牌参数（3 行 → 多 3 个）");
        await Assert.That(plain.Contains("`version`", StringComparison.Ordinal)).IsFalse();
        await Assert.That(versioned.Contains("`version`", StringComparison.Ordinal)).IsTrue();
    }

    private static int Count(string text, string token)
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
}
