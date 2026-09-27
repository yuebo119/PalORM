namespace PalORM.Core.Tests;

/// <summary>ITM-714(r20)：BatchUpdateSqlBuilder 参数-占位符映射锁定。
/// <para>背景：BulkUpdateBatchAsync 的 PG/MySQL 批量 SQL 路径在 CI 中零执行覆盖
/// （集成用例全落 SQLite 回退），其参数编号空间由 builder 的 <c>baseIdx+c</c> 与
/// <c>ExecuteBatchUpdateAsync</c> 的 <c>(i-batchStart)*paramsPerRow+c</c> 两套独立推导。
/// 本测试机械锁定"每行 (setCol0..setColN, pk)"的编号契约，漂移即失败——不依赖真库。</para></summary>
public sealed class BatchUpdateSqlBuilderTests
{
    [Test]
    public async Task BuildCaseWhen_PlaceholdersMatchRowMajorColumnOrder()
    {
        // 2 列 SET + 1 PK = 每行 3 参数；2 行 → @p0..@p5
        string sql = BatchUpdateSqlBuilder.Build(
            SqlDialect.MySql, "`t`", "`id`", ["`a`", "`b`"], rowCount: 2,
            hasTenantFilter: false, tenantParameterName: "@__tenant0");

        // 每行 [a=@pN, b=@pN+1, pk=@pN+2]
        await Assert.That(sql).Contains("`a` = CASE `id` WHEN @p2 THEN @p0 WHEN @p5 THEN @p3 END");
        await Assert.That(sql).Contains("`b` = CASE `id` WHEN @p2 THEN @p1 WHEN @p5 THEN @p4 END");
        await Assert.That(sql).Contains("`id` IN (@p2, @p5)");
    }

    [Test]
    public async Task BuildJoinValuesRow_PlaceholdersMatchRowMajorColumnOrder()
    {
        // MySQL-7：UPDATE JOIN table value constructor 形态（MySQL ≥ 8.0.19，探针十五 8.75×）
        // 参数序与 CASE WHEN 逐位一致——每行 ROW(setCol0..setColN, pk)，调用方参数池布局零改动
        string sql = BatchUpdateSqlBuilder.Build(
            SqlDialect.MySql, "`t`", "`id`", ["`a`", "`b`"], rowCount: 2,
            hasTenantFilter: false, tenantParameterName: "@__tenant0",
            form: BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow);

        // 每行 ROW(@p0,@p1,@p2)：2 列 SET + pk 在行尾；ON 等价 JOIN + SET 派生列引用
        await Assert.That(sql).Contains("JOIN (VALUES ROW(@p0,@p1,@p2), ROW(@p3,@p4,@p5)) AS v(c0, c1, pk)");
        await Assert.That(sql).Contains("ON tgt.`id` = v.pk SET");
        await Assert.That(sql).Contains("tgt.`a` = v.c0, tgt.`b` = v.c1");
        // 无 CASE WHEN 残留（形态切换必须彻底，两种形态语义不能混）
        await Assert.That(sql).DoesNotContain("CASE");
    }

    [Test]
    public async Task BuildJoinValuesRow_TenantFilter_AppendsDialectQuotedPredicate()
    {
        // 租户过滤是 ON/SET 之后的 WHERE 谓词（AND），与 CASE WHEN 形态同位置
        string sql = BatchUpdateSqlBuilder.Build(
            SqlDialect.MySql, "`t`", "`id`", ["`a`"], rowCount: 1,
            hasTenantFilter: true, tenantParameterName: "@__tenant0",
            form: BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow);

        await Assert.That(sql).Contains(") AS v(c0, pk) ON tgt.`id` = v.pk SET tgt.`a` = v.c0 AND `tenant_id` = @__tenant0");
    }

    [Test]
    public async Task Build_NonMySqlDialects_IgnoreJoinValuesRowForm()
    {
        // form 只在 MySQL 生效：PG 恒 FROM VALUES、SQLite 恒 CASE WHEN（调用方言既有路径）
        string pg = BatchUpdateSqlBuilder.Build(
            SqlDialect.PostgreSql, "\"t\"", "\"id\"", ["\"a\""], rowCount: 1,
            hasTenantFilter: false, tenantParameterName: "@__tenant0",
            form: BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow);
        string sqlite = BatchUpdateSqlBuilder.Build(
            SqlDialect.Sqlite, "\"t\"", "\"id\"", ["\"a\""], rowCount: 1,
            hasTenantFilter: false, tenantParameterName: "@__tenant0",
            form: BatchUpdateSqlBuilder.BatchUpdateForm.JoinValuesRow);

        await Assert.That(pg).Contains("FROM (VALUES (@p0, @p1))");
        await Assert.That(sqlite).Contains("= CASE \"id\" WHEN @p1 THEN @p0 END");
        await Assert.That(pg).DoesNotContain("JOIN (VALUES ROW");
        await Assert.That(sqlite).DoesNotContain("JOIN (VALUES ROW");
    }

    [Test]
    public async Task BuildPostgreSql_ValueRowsFollowRowMajorOrder()
    {
        string sql = BatchUpdateSqlBuilder.Build(
            SqlDialect.PostgreSql, "\"t\"", "\"id\"", ["\"a\"", "\"b\""], rowCount: 2,
            hasTenantFilter: false, tenantParameterName: "@__tenant0");

        // VALUES 每行 (setCol0, setCol1, pk)，pk 在每行末尾
        await Assert.That(sql).Contains("FROM (VALUES (@p0, @p1, @p2), (@p3, @p4, @p5))");
        await Assert.That(sql).Contains("\"a\" = v.col0, \"b\" = v.col1");
        await Assert.That(sql).Contains("tgt.\"id\" = v.col_pk");
    }

    [Test]
    public async Task Build_TenantFilter_UsesDialectQuote()
    {
        // ITM-529/561 同族：MySQL 反引号 vs PG/SQLite 双引号——硬编码会在 MySQL 下当字符串字面量
        string mysql = BatchUpdateSqlBuilder.Build(
            SqlDialect.MySql, "`t`", "`id`", ["`a`"], rowCount: 1,
            hasTenantFilter: true, tenantParameterName: "@__tenant0");
        string pg = BatchUpdateSqlBuilder.Build(
            SqlDialect.PostgreSql, "\"t\"", "\"id\"", ["\"a\""], rowCount: 1,
            hasTenantFilter: true, tenantParameterName: "@__tenant0");

        await Assert.That(mysql).Contains("AND `tenant_id` = @__tenant0");
        await Assert.That(pg).Contains("AND \"tenant_id\" = @__tenant0");
    }
}
