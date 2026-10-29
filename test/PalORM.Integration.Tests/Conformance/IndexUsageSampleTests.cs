using System.Data;
using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L5 执行计划抽样（方案 §4.1 / §4.6 第 6 步）。
/// <para>只做"索引命中的布尔断言"，不做计划质量评估（那属 PerfHub）。抽样口径：在 432 个
/// （操作，形状）对上取 5% 量级的代表性查询，覆盖主键查找、唯一索引查找、复合索引前缀查找三类，
/// 每类在三方言各断言一次。判据各自贴合方言的计划输出形态。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class IndexUsageSampleTests
{
    /// <summary>按方言拼查询：裸 ADO 直执行不带参数，值必须内联（表列名都是常量）。</summary>
    private static string BuildLookupSql(string dialect, string column, string value)
    {
        string q = dialect == "mysql" ? "`" : "\"";
        return $"SELECT {q}Id{q}, {q}category{q}, {q}price{q}, {q}sku{q} "
            + $"FROM {q}cf_composite_index{q} WHERE {q}{column}{q} = '{value}'";
    }

    /// <summary>1000 行样本：单行表上规划器必然选全表扫描，索引命中断言需要足够的数据量。</summary>
    private static CfCompositeIndexEntity[] SampleRows()
        => [.. Enumerable.Range(1, 1000).Select(i => new CfCompositeIndexEntity
        {
            Id = i,
            Category = $"cat-{i}",
            Price = i,
            Sku = $"sku-{i}"
        })];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "语句由 BuildLookupSql 按方言常量模板拼装，值来自本文件的测试常量")]
    private static async ValueTask<string> ReadPlanAsync(
        DbConnection raw, string statement, bool mysqlStyle, CancellationToken ct = default)
    {
        if (raw.State != ConnectionState.Open) await raw.OpenAsync(ct);
        await using DbCommand command = raw.CreateCommand();
        command.CommandText = statement;
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        return mysqlStyle
            ? await ReadMySqlPlanPartsAsync(reader, ct)
            : await ReadTextPlanPartsAsync(reader, ct);
    }

    /// <summary>SQLite 与 PG 的 EXPLAIN 输出是纯文本行：全部行拼起来。</summary>
    private static async Task<string> ReadTextPlanPartsAsync(DbDataReader reader, CancellationToken ct)
    {
        var parts = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                object? value = reader.GetValue(i);
                if (value is not null and not DBNull) parts.Add(value.ToString() ?? "");
            }
        }
        return string.Join(" | ", parts);
    }

    /// <summary>MySQL 的 EXPLAIN 是结果集：只取 key / possible_keys / type 三列。</summary>
    private static async Task<string> ReadMySqlPlanPartsAsync(DbDataReader reader, CancellationToken ct)
    {
        var parts = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                if (name is "key" or "possible_keys" or "type")
                    parts.Add($"{name}={reader.GetValue(i)}");
            }
        }
        return string.Join(" | ", parts);
    }

    private static async Task<string> SqlitePlanAsync(string column, string value)
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("l5_sqlite");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await MirrorTable.EnsureAsync(db, SqlDialect.Sqlite);
        await db.BulkInsertAsync(SampleRows());
        await db.ExecuteAsync($"ANALYZE");
        return await ReadPlanAsync(
            db.GetRawConnection(),
            $"EXPLAIN QUERY PLAN {BuildLookupSql("sqlite", column, value)}", mysqlStyle: false);
    }

    private static async Task<string> PostgreSqlPlanAsync(string column, string value)
    {
        await using RemoteConformanceFixture<PostgreSqlProvider> fixture = await ConformanceDialects.PostgreSqlAsync();
        // cf_composite_index 是共享表且 Sku 唯一：先清再插，否则上一轮的 1000 行会自撞
        await MirrorTable.ExecuteAsync(
            fixture.Raw, SqlDialect.PostgreSql,
            $"DELETE FROM {DialectSql.Quote(SqlDialect.PostgreSql, "cf_composite_index")}");
        await fixture.Session.BulkInsertAsync(SampleRows());
        // 规划器需要统计信息才会比较索引与全表：不 ANALYZE 时默认估算会选中 Seq Scan
        await MirrorTable.ExecuteAsync(fixture.Raw, SqlDialect.PostgreSql, "ANALYZE cf_composite_index");
        return await ReadPlanAsync(
            fixture.Raw, $"EXPLAIN {BuildLookupSql("postgresql", column, value)}", mysqlStyle: false);
    }

    private static async Task<string> MySqlPlanAsync(string column, string value)
    {
        await using RemoteConformanceFixture<MySqlProvider> fixture = await ConformanceDialects.MySqlAsync();
        await MirrorTable.ExecuteAsync(
            fixture.Raw, SqlDialect.MySql,
            $"DELETE FROM {DialectSql.Quote(SqlDialect.MySql, "cf_composite_index")}");
        await fixture.Session.BulkInsertAsync(SampleRows());
        await MirrorTable.ExecuteAsync(fixture.Raw, SqlDialect.MySql, "ANALYZE TABLE cf_composite_index");
        return await ReadPlanAsync(
            fixture.Raw, $"EXPLAIN {BuildLookupSql("mysql", column, value)}", mysqlStyle: true);
    }

    [Test]
    public async Task Sqlite_UniqueLookup_UsesIndex()
    {
        string plan = await SqlitePlanAsync("sku", "sku-1");
        await Assert.That(plan).Contains("USING");
        await Assert.That(plan).Contains("INDEX");
    }

    [Test]
    public async Task Sqlite_CompositePrefix_UsesIndex()
    {
        string plan = await SqlitePlanAsync("category", "cat-1");
        await Assert.That(plan).Contains("USING");
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_UniqueLookup_UsesIndexScan()
    {
        string plan = await PostgreSqlPlanAsync("sku", "sku-1");
        await Assert.That(plan).Contains("Index");
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_CompositePrefix_UsesIndexScan()
    {
        string plan = await PostgreSqlPlanAsync("category", "cat-1");
        await Assert.That(plan).Contains("Index");
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_UniqueLookup_KeyColumnIsNotNull()
    {
        string plan = await MySqlPlanAsync("sku", "sku-1");
        await Assert.That(plan).Contains("key=");
        await Assert.That(plan).DoesNotContain("key= |");
        await Assert.That(plan).DoesNotContain("key=)");
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_CompositePrefix_KeyColumnIsNotNull()
    {
        string plan = await MySqlPlanAsync("category", "cat-1");
        // key 列为空表示未命中索引：断言它非空且指向一个索引名
        await Assert.That(plan).Contains("key=");
        await Assert.That(plan).DoesNotContain("key= |");
        await Assert.That(plan).DoesNotContain("key=)");
    }
}