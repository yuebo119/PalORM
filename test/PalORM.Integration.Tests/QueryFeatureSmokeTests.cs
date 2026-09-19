using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>查询特性冒烟——窗口函数 / CTE / SplitQuery / 命令超时（原 FinalTests 拆分，审计 TEST-012）。
/// 本组无全局 listener 状态，无需并行组隔离。</summary>
public sealed class QueryFeatureSmokeTests
{
    [Test]
    public async Task WindowOver_Execution_ReturnsRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "W1", Price = 10m, Stock = 0 });
        var r = await db.From<Product>().UnsafeWindowOver("ROW_NUMBER()", "ORDER BY price DESC").ToListAsync();
        await Assert.That(r.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WithCommandTimeout_ExecutesSuccessfully()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var r = await db.From<Product>().WithCommandTimeout(30).ToListAsync();
        await Assert.That(r.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CTE_SimpleQuery_ReturnsRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "C", Price = 10m, Stock = 0 });
        var r = await db.From<Product>().With("c", $"SELECT * FROM products WHERE price > {5m}").ToListAsync();
        await Assert.That(r.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AsSplitQuery_ExecutesWithoutJoin()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "S", Price = 1m, Stock = 0 });
        var r = await db.From<Product>().AsSplitQuery().ToListAsync();
        await Assert.That(r.Count).IsEqualTo(1);
    }
}
