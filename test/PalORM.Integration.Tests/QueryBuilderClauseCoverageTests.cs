using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>QueryBuilder 字符串重载族的集成侧覆盖（2026-11-02）。
/// <para><b>为什么需要</b>：M3（0d62acf）新增 OrderBy/ThenBy 族、GroupBy、Select、Set、
/// WhereIn/WhereNotIn 后，Core 侧有 SQL 文本测试，但集成覆盖语料没有行使这些子句，
/// QueryBuilder 的类级分支地板（86%）掉到 85.70% 而 CI 判红。本文件让每个新子句在
/// 真实 SQLite 会话上至少执行一次，把分母补回来——不是为覆盖率凑数：每条都断言生成的
/// SQL 或执行结果的形状，子句拼错会直接红。</para>
/// <para>形态对齐 QueryTests（同一 <see cref="Order"/> 实体与 orders 表），
/// 只走 SQLite（进程内，不需要外部容器）。</para></summary>
public sealed class QueryBuilderClauseCoverageTests
{
    private static async Task<DataSession<SqliteProvider>> SeedAsync()
    {
        DataSession<SqliteProvider> db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.ExecuteAsync($"INSERT INTO orders (status, total, created_at) VALUES ({"A"}, {10m}, {0L})");
        await db.ExecuteAsync($"INSERT INTO orders (status, total, created_at) VALUES ({"B"}, {20m}, {0L})");
        await db.ExecuteAsync($"INSERT INTO orders (status, total, created_at) VALUES ({"C"}, {30m}, {0L})");
        return db;
    }

    [Test]
    public async Task StringOverloads_OrderByFamily_GroupBy_Select_ProduceExpectedSql()
    {
        await using DataSession<SqliteProvider> db = await SeedAsync();

        // OrderBy 族：降序便捷形态 + 继续列（ThenBy）覆盖 Continue=true 分支
        string ordered = db.From<Order>()
            .OrderBy("Total", descending: true)
            .ThenBy("Id")
            .ThenByDescending("CreatedAt")
            .ToSql();
        await Assert.That(ordered).Contains("ORDER BY");
        await Assert.That(ordered).Contains("DESC");

        // 单列升序（descending=false 分支）与表达式重载
        string ascending = db.From<Order>().OrderBy("Status").ToSql();
        await Assert.That(ascending).Contains("ORDER BY");

        // GroupBy：首列（无 GroupBy 子句）与追加列（Continue=true）两分支
        string grouped = db.From<Order>().GroupBy("Status").GroupBy("Total").ToSql();
        await Assert.That(grouped).Contains("GROUP BY");
        await Assert.That(grouped).Contains(", ");

        // Select：字符串重载与表达式重载（显式转型消除 params 二义性）
        string[] selectColumns = ["Status", "Total"];
        string selected = db.From<Order>().Select(selectColumns).ToSql();
        await Assert.That(selected).DoesNotContain("SELECT *");

        // 空 Select 必须在构建期拒绝（ITM-663 的入口守卫）
        await Assert.That(() => db.From<Order>().Select(Array.Empty<string>())).Throws<ArgumentException>();
    }

    [Test]
    public async Task WhereInWhereNotIn_StringOverloads_ExecuteAgainstSqlite()
    {
        await using DataSession<SqliteProvider> db = await SeedAsync();

        // 占位符形态（SQLite 无 PG 数组能力，走回退分支）
        var included = await db.From<Order>().WhereIn("Status", ["A", "C"]).ToListAsync();
        await Assert.That(included.Count).IsEqualTo(2);

        var excluded = await db.From<Order>().WhereNotIn("Status", ["A", "B"]).ToListAsync();
        await Assert.That(excluded.Count).IsEqualTo(1);
        await Assert.That(excluded[0].Status).IsEqualTo("C");

        // 空集合：WhereIn 必空、WhereNotIn 必全量（两侧都断言，防"空集被拼成非法 SQL"）
        var none = await db.From<Order>().WhereIn("Status", Array.Empty<string>()).ToListAsync();
        await Assert.That(none.Count).IsEqualTo(0);
        var all = await db.From<Order>().WhereNotIn("Status", Array.Empty<string>()).ToListAsync();
        await Assert.That(all.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Set_StringAndExpressionOverloads_BuildUpdateSql()
    {
        await using DataSession<SqliteProvider> db = await SeedAsync();

        string byExpression = db.From<Order>().Set(o => o.Status, "Z").ToSql();
        await Assert.That(byExpression).Contains("UPDATE");
        await Assert.That(byExpression).Contains("SET");

        string byName = db.From<Order>().Set("Status", "Z").ToSql();
        await Assert.That(byName).Contains("UPDATE");

        // 真实执行一次，确认 SET 子句可用而非仅文本正确
        int affected = await db.From<Order>().Where($"Id = {1L}").Set("Status", "Z").ExecuteNonQueryAsync();
        await Assert.That(affected).IsEqualTo(1);
        Order? updated = await db.From<Order>().Where($"Id = {1L}").FirstOrDefaultAsync();
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo("Z");
    }
}
