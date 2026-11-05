using System.Runtime.CompilerServices;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>多键点读（<c>GetByKeyManyAsync</c>）的真库方言一致性测试（2026-11-05 性能方案 Phase 1）。
/// <para><b>为什么要真库</b>：SQLite 的 <c>json_each</c> 形态由 Core.Tests 锁住，但另两条形态
/// （PG 原生数组 <c>= ANY(@ids)</c>、MySQL 分批 <c>IN (…)</c>）只在真驱动上才能验证
/// ——参数绑定、数组元素类型、批量上限都是驱动/协议层行为，SQLite 无法代理。</para>
/// <para><b>每用例独立实体与表名</b>：类级 <c>[NotInParallel("ExtBulkTable")]</c> 只保证与其他
/// 外部库用例互斥，类内用例仍可并行，共用一张表会撞 <c>42P07 relation already exists</c>。
/// 表名由实体 <c>[Table]</c> 决定，故每个用例配一个实体。</para>
/// <para>清理纪律：try/finally DROP（对齐 ExternalDatabaseFeatureTests 先例）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class GetByKeyManyDialectTests
{
    // 表名内联为字面量：FormattableString 会把插值值参数化成 @p0，`CREATE TABLE @p0` 语法错
    // （BulkDeleteInFormPoolingTests 播种纪律同源）。
    private static async Task SeedAsync<TProvider>(
        DataSession<TProvider> db, string dropSql, string createSql, string insertSql, int count)
        where TProvider : IDbProvider
    {
        // 前置 DROP：上轮失败残留的表会让 CREATE 撞 "already exists"（真库是共享的，
        // 与 SQLite 内存库不同，用例之间不隔离）。
        await db.ExecuteAsync(FormattableStringFactory.Create(dropSql));
        await db.ExecuteAsync(FormattableStringFactory.Create(createSql));
        for (int i = 1; i <= count; i++)
        {
            await db.ExecuteAsync(FormattableStringFactory.Create(insertSql, (long)i, "row" + i));
        }
    }

    private static List<object> Keys(int from, int to)
    {
        var keys = new List<object>(to - from + 1);
        for (long i = from; i <= to; i++) keys.Add(i);
        return keys;
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_GetByKeyMany_AnyArrayForm_ReturnsAllRows()
    {
        await using var db = await TestDb.PostgreSqlAsync();
        try
        {
            await SeedAsync(
                db,
                "DROP TABLE IF EXISTS palorm_rm_pg_any CASCADE",
                "CREATE TABLE palorm_rm_pg_any (id BIGINT PRIMARY KEY, label VARCHAR(32) NOT NULL)",
                "INSERT INTO palorm_rm_pg_any (id, label) VALUES ({0}, {1})",
                2000);

            List<ReadManyPgRow> rows = await db.GetByKeyManyAsync<ReadManyPgRow>(Keys(1, 2000));

            await Assert.That(rows.Count).IsEqualTo(2000);
            await Assert.That(rows.Select(static r => r.Id).Distinct().Count()).IsEqualTo(2000);
            await Assert.That(rows.All(static r => r.Label == "row" + r.Id)).IsTrue();
        }
        finally { await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_rm_pg_any CASCADE"); }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_GetByKeyMany_MissingAndEmptyKeys_BehaveAsDocumented()
    {
        await using var db = await TestDb.PostgreSqlAsync();
        try
        {
            await SeedAsync(
                db,
                "DROP TABLE IF EXISTS palorm_rm_pg_edge CASCADE",
                "CREATE TABLE palorm_rm_pg_edge (id BIGINT PRIMARY KEY, label VARCHAR(32) NOT NULL)",
                "INSERT INTO palorm_rm_pg_edge (id, label) VALUES ({0}, {1})",
                10);

            // 缺键：静默跳过（返回行数 < 键数），不返回 null 占位
            List<ReadManyPgEdgeRow> partial = await db.GetByKeyManyAsync<ReadManyPgEdgeRow>(
                new List<object> { 3L, 999_999L });
            await Assert.That(partial.Count).IsEqualTo(1);
            await Assert.That(partial[0].Id).IsEqualTo(3L);

            // 空集合：返回空列表，不发 SQL（真库侧只要不抛即达成契约）
            List<ReadManyPgEdgeRow> empty = await db.GetByKeyManyAsync<ReadManyPgEdgeRow>(Array.Empty<object>());
            await Assert.That(empty.Count).IsEqualTo(0);
        }
        finally { await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_rm_pg_edge CASCADE"); }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_GetByKeyMany_BatchedInForm_ReturnsAllRows()
    {
        await using var db = await TestDb.MySqlAsync();
        try
        {
            await SeedAsync(
                db,
                "DROP TABLE IF EXISTS palorm_rm_my_batch",
                "CREATE TABLE palorm_rm_my_batch (id BIGINT PRIMARY KEY, label VARCHAR(32) NOT NULL)",
                "INSERT INTO palorm_rm_my_batch (id, label) VALUES ({0}, {1})",
                2000);

            // 2000 键 < MySQL 参数上限（65535）→ 单批；覆盖 IN 形态的驱动绑定路径
            List<ReadManyMyBatchRow> rows = await db.GetByKeyManyAsync<ReadManyMyBatchRow>(Keys(1, 2000));

            await Assert.That(rows.Count).IsEqualTo(2000);
            await Assert.That(rows.Select(static r => r.Id).Distinct().Count()).IsEqualTo(2000);
            await Assert.That(rows.All(static r => r.Label == "row" + r.Id)).IsTrue();
        }
        finally { await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_rm_my_batch"); }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_GetByKeyMany_MultiBatch_CrossesParameterLimit()
    {
        // 超过单批上限（MaxRowsPerBatch=5000）→ 至少两批：跨批结果必须完整、不重不漏
        await using var db = await TestDb.MySqlAsync();
        try
        {
            await SeedAsync(
                db,
                "DROP TABLE IF EXISTS palorm_rm_my_multi",
                "CREATE TABLE palorm_rm_my_multi (id BIGINT PRIMARY KEY, label VARCHAR(32) NOT NULL)",
                "INSERT INTO palorm_rm_my_multi (id, label) VALUES ({0}, {1})",
                6000);

            List<ReadManyMyMultiRow> rows = await db.GetByKeyManyAsync<ReadManyMyMultiRow>(Keys(1, 6000));

            await Assert.That(rows.Count).IsEqualTo(6000);
            await Assert.That(rows.Select(static r => r.Id).Distinct().Count()).IsEqualTo(6000);
        }
        finally { await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_rm_my_multi"); }
    }
}

[Table("palorm_rm_pg_any")]
internal sealed partial class ReadManyPgRow
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("label")]
    public string Label { get; set; } = "";
}

[Table("palorm_rm_pg_edge")]
internal sealed partial class ReadManyPgEdgeRow
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("label")]
    public string Label { get; set; } = "";
}

[Table("palorm_rm_my_batch")]
internal sealed partial class ReadManyMyBatchRow
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("label")]
    public string Label { get; set; } = "";
}

[Table("palorm_rm_my_multi")]
internal sealed partial class ReadManyMyMultiRow
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("label")]
    public string Label { get; set; } = "";
}
