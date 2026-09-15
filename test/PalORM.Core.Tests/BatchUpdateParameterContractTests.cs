using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>批量 UPDATE 的参数池 ↔ SQL 占位符契约测试。
/// <para><b>为什么需要</b>：参数池的消费点在 PG/MySQL 的批量 UPDATE（<c>ExecuteBatchUpdateAsync</c>），
/// 而 SQLite 方言在 <c>BulkUpdateBatchAsync</c> 里按设计回退到逐条路径，本地跑不到该分支。
/// 「池内参数名与顺序必须与 SQL 占位符逐位对应」这条跨 Provider 契约因此只能靠本测试锁定——
/// 名字或顺序一旦漂移，PG/MySQL 上会以"参数未绑定或绑错列"的形式静默出错。</para>
/// <para>池的构造与 SQL 的构造在 SQLite 命令上执行即可：占位符文本由
/// <see cref="BatchUpdateSqlBuilder"/> 按方言产出，与驱动无关。</para></summary>
public sealed class BatchUpdateParameterContractTests
{
    private static readonly string[] SetColumns = ["\"a\"", "\"b\"", "\"c\""];

    [Test]
    [Arguments(SqlDialect.PostgreSql)]
    [Arguments(SqlDialect.MySql)]
    [Arguments(SqlDialect.Sqlite)]
    public async Task ParameterPool_NamesMatchSqlPlaceholders_InOrder(SqlDialect dialect)
    {
        const int rowCount = 4;
        int paramsPerRow = SetColumns.Length + 1;
        int rowParamCount = rowCount * paramsPerRow;

        string sql = BatchUpdateSqlBuilder.Build(
            dialect, "\"t\"", "\"id\"", SetColumns, rowCount,
            hasTenantFilter: false, tenantParameterName: "@__tenant0");

        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using DbCommand cmd = conn.CreateCommand();
        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterPool(
            cmd, rowParamCount, hasTenantFilter: false, "@__tenant0", null,
            (name, value) => new SqliteParameter(name, value));

        List<int> placeholderIndices = [.. ExtractPlaceholders(sql).Distinct().Select(ParseIndex)];

        await Assert.That(pool.Length).IsEqualTo(rowParamCount);
        await Assert.That(cmd.Parameters.Count).IsEqualTo(rowParamCount);
        // 契约：SQL 引用的占位符索引集合恰为 0..rowParamCount-1（CASE WHEN 的引用顺序
        // 不是数值序——每行先引 pk 再引 SET 列，故只能比对集合而非出现顺序）
        await Assert.That(string.Join(",", placeholderIndices.Order()))
            .IsEqualTo(string.Join(",", Enumerable.Range(0, rowParamCount)));
        // 契约：池内参数名按索引升序与 SQL 占位符逐位对应
        await Assert.That(string.Join(",", pool.Select(static p => p.ParameterName)))
            .IsEqualTo(string.Join(",", Enumerable.Range(0, rowParamCount)
                .Select(static i => ParameterNameCache.GetName(i))));
    }

    [Test]
    [Arguments(SqlDialect.PostgreSql)]
    [Arguments(SqlDialect.MySql)]
    public async Task ParameterPool_AppendsTenantParameterLast(SqlDialect dialect)
    {
        const int rowCount = 2;
        const string TenantParam = "@__tenant0";
        int paramsPerRow = SetColumns.Length + 1;
        int rowParamCount = rowCount * paramsPerRow;

        string sql = BatchUpdateSqlBuilder.Build(
            dialect, "\"t\"", "\"id\"", SetColumns, rowCount,
            hasTenantFilter: true, tenantParameterName: TenantParam);

        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using DbCommand cmd = conn.CreateCommand();
        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterPool(
            cmd, rowParamCount, hasTenantFilter: true, TenantParam, 42L,
            (name, value) => new SqliteParameter(name, value));

        await Assert.That(pool.Length).IsEqualTo(rowParamCount + 1);
        await Assert.That(pool[rowParamCount].ParameterName).IsEqualTo(TenantParam);
        await Assert.That(pool[rowParamCount].Value).IsEqualTo(42L);
        await Assert.That(cmd.Parameters[^1].ParameterName).IsEqualTo(TenantParam);
        await Assert.That(sql.TrimEnd().EndsWith(TenantParam, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ParameterPool_RepeatedValueWrites_DoNotAddParameters()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using DbCommand cmd = conn.CreateCommand();
        DbParameter[] pool = BatchUpdateSqlBuilder.CreateParameterPool(
            cmd, 8, hasTenantFilter: false, "@__tenant0", null,
            (name, value) => new SqliteParameter(name, value));

        int countAfterPool = cmd.Parameters.Count;
        // 模拟逐行写值：池化路径只改 Value，参数集合不增长
        for (int row = 0; row < 2; row++)
        {
            for (int c = 0; c < 4; c++)
                pool[(row * 4) + c].Value = (row * 10L) + c;
        }

        await Assert.That(cmd.Parameters.Count).IsEqualTo(countAfterPool);
        await Assert.That((long)pool[5].Value!).IsEqualTo(11L);
    }

    private static List<string> ExtractPlaceholders(string sql)
        => [.. Regex.Matches(sql, @"@[A-Za-z_][A-Za-z0-9_]*").Select(static m => m.Value)];

    /// <summary>从 <c>@p{N}</c> 占位符名解析出序号 N。</summary>
    private static int ParseIndex(string placeholder)
    {
        int separator = placeholder.IndexOf('p', StringComparison.Ordinal);
        return int.Parse(placeholder[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }
}
