using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// R2（v6.0）：[DefaultValue] DDL 落地的真库验证（SQLite 本地全跑；PG/MySQL 由 CI 全量集成覆盖——
/// DEFAULT 子句的三方言 DDL 文本形态由 SnapshotTests 锁定，此处验证语义生效）。
/// 契约：DDL-only——实体属性值照常写入，DEFAULT 仅对未显式插入该列的场景（Raw SQL 省略列、
/// 外部系统写表）兜底。
/// </summary>
public sealed class MigrationDefaultTests
{
    [Test]
    public async Task Ddl_ContainsDefaultClause()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        string ddl = (await db.ScalarAsync<string>(
            $"SELECT sql FROM sqlite_master WHERE type = {"table"} AND name = {"defaulted_orders"}"))!;
        await Assert.That(ddl).Contains("DEFAULT 'pending'");
    }

    [Test]
    public async Task RawInsert_OmittingColumn_ReadsBackDefaultValue()
    {
        // 锚点：撤掉 MigrationEmitter 的 DEFAULT 追加，此测试红（DB 无默认值可回退，读回 NULL）
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        await db.ExecuteAsync($"INSERT INTO defaulted_orders (amount) VALUES (100)");
        string status = (await db.ScalarAsync<string>(
            $"SELECT status FROM defaulted_orders WHERE amount = 100"))!;
        await Assert.That(status).IsEqualTo("pending");
    }

    [Test]
    public async Task EntityInsert_PropertyValueWinsOverDefault()
    {
        // DDL-only 契约：实体路径属性值照写，DEFAULT 不参与插入值决策
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        var order = new DefaultedOrder { Amount = 200m, Status = "shipped" };
        await db.InsertAsync(order);

        string status = (await db.ScalarAsync<string>(
            $"SELECT status FROM defaulted_orders WHERE amount = 200"))!;
        await Assert.That(status).IsEqualTo("shipped");
    }

    [Test]
    public async Task MigrateAsync_Idempotent_WithDefaultColumn()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.MigrateAsync();

        string ddl = (await db.ScalarAsync<string>(
            $"SELECT sql FROM sqlite_master WHERE type = {"table"} AND name = {"defaulted_orders"}"))!;
        await Assert.That(ddl).Contains("DEFAULT 'pending'");
    }
}

#region Test Entities
[Table("defaulted_orders")]
public partial class DefaultedOrder
{
    [Key] public long Id { get; set; }
    [Column("amount")] public decimal Amount { get; set; }
    [Column("status")]
    [DefaultValue("'pending'")]
    public string Status { get; set; } = "";
}
#endregion
