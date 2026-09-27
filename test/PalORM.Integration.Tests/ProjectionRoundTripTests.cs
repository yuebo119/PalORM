using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// R1（v6.0）：[Projection] DTO 投影的真库物化 round-trip（SQLite 本地全跑；PG/MySQL 由 CI
/// 全量集成覆盖——物化生成物三方言同构，无方言分支）。核心锚点：不实现投影管线时
/// QueryAsync&lt;OrderSummary&gt; 抛 "not registered"（红）。
/// </summary>
public sealed class ProjectionRoundTripTests
{
    [Test]
    public async Task QueryAsync_JoinSql_MaterializesProjection()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new ProjCustomer { Name = "Alice" });
        await db.InsertAsync(new ProjOrder { CustomerId = 1, Total = 250m });

        var rows = await db.QueryAsync<OrderSummary>(
            $"SELECT o.\"Id\" AS \"Id\", c.\"name\" AS \"CustomerName\", o.\"total\" AS \"Total\" FROM proj_orders o JOIN proj_customers c ON o.\"customer_id\" = c.\"Id\"");

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Id).IsEqualTo(1L);
        await Assert.That(rows[0].CustomerName).IsEqualTo("Alice");
        await Assert.That(rows[0].Total).IsEqualTo(250m);
    }

    [Test]
    public async Task QueryFirstAsync_MaterializesProjection()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new ProjCustomer { Name = "Bob" });
        await db.InsertAsync(new ProjOrder { CustomerId = 1, Total = 99m });

        // ADR-A 列序契约：SELECT 列数/列序必须与投影属性声明序一致（Id, CustomerName, Total）
        var summary = await db.QueryFirstAsync<OrderSummary>(
            $"SELECT o.\"Id\" AS \"Id\", c.\"name\" AS \"CustomerName\", o.\"total\" AS \"Total\" FROM proj_orders o JOIN proj_customers c ON o.\"customer_id\" = c.\"Id\"");
        await Assert.That(summary.CustomerName).IsEqualTo("Bob");
        await Assert.That(summary.Total).IsEqualTo(99m);
    }

    [Test]
    public async Task MigrateAsync_DoesNotCreateTableForProjection()
    {
        // T2.5 负例锚点：投影不进 TableNames——MigrateAsync 不为它建表
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        long count = await db.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = {"table"} AND name = {"OrderSummary"}");
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task From_OnProjection_FailsLoudly()
    {
        // 投影无表可查——From<T> 走 TableNames/CommandSql 路径，缺失键必须响亮失败而非静默
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async ()
            => await db.From<OrderSummary>().ToListAsync());
    }
}

#region Test Entities
[Table("proj_customers")]
public partial class ProjCustomer
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

[Table("proj_orders")]
public partial class ProjOrder
{
    [Key] public long Id { get; set; }
    [Column("customer_id")] public long CustomerId { get; set; }
    [Column("total")] public decimal Total { get; set; }
}

[Projection]
public sealed class OrderSummary
{
    public long Id { get; set; }
    public string CustomerName { get; set; } = "";
    public decimal Total { get; set; }
}
#endregion
