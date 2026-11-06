using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>条件删除（<c>ExecuteDeleteAsync</c>）的跨方言行为与构建期守卫。
///
/// <para><b>为什么放在 Integration 而不是 Core.Tests</b>：<c>QueryBuilder&lt;T&gt;</c> 的类级覆盖率
/// 地板（META-1）只读 Integration 通道的覆盖率文件。DELETE 构建路径若只由 Core.Tests 覆盖，
/// 该通道看不见，类级读数会被新增代码摊薄。</para></summary>
[NotInParallel("ExecuteDeleteIntegration")]
public sealed class ExecuteDeleteIntegrationTests
{
    [Test]
    public async Task Sqlite_DeletesOnlyMatchingRows()
    {
        await using DataSession<SqliteProvider> db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "keep", Price = 1m, Stock = 1 });
        await db.InsertAsync(new Product { Name = "drop", Price = 2m, Stock = 2 });

        int deleted = await db.From<Product>().Where($"name = {"drop"}").ExecuteDeleteAsync();

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(1L);
    }

    [Test]
    public async Task Sqlite_SoftDeleteEntity_MarksInsteadOfDeleting()
    {
        await using DataSession<SqliteProvider> db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new DeleteDriftProbe { Name = "logical" });

        int affected = await db.From<DeleteDriftProbe>().Where($"name = {"logical"}").ExecuteDeleteAsync();

        // 软删实体改写为 UPDATE deleted_at：行仍在，默认查询不可见
        await Assert.That(affected).IsEqualTo(1);
        await Assert.That(await db.CountAsync<DeleteDriftProbe>()).IsEqualTo(0L);
        DeleteDriftProbe? raw = await db.IgnoreFilters().GetAsync<DeleteDriftProbe>(1L);
        await Assert.That(raw).IsNotNull();
        await Assert.That(raw!.DeletedAt).IsNotNull();
    }

    [Test]
    public async Task Sqlite_RejectsUnsupportedClauses()
    {
        await using DataSession<SqliteProvider> db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        // 表级清空必须显式表达
        await Assert.That(async () => await db.From<Product>().ExecuteDeleteAsync())
            .Throws<InvalidOperationException>();
        // DELETE 没有 SET 段
        await Assert.That(async () => await db.From<Product>()
                .Set(x => x.Name, "n").Where($"stock = {1}").ExecuteDeleteAsync())
            .Throws<InvalidOperationException>();
        // 这些子句在 DELETE 构建输出里不存在，静默丢弃会扩大影响面
        await Assert.That(async () => await db.From<Product>()
                .Where($"stock = {1}").OrderBy("Name").ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
        await Assert.That(async () => await db.From<Product>()
                .Where($"stock = {1}").Take(5).ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
        await Assert.That(async () => await db.From<Product>()
                .Where($"stock = {1}").GroupBy("Name").ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_DeletesOnlyMatchingRows()
    {
        await using var db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        await db.IgnoreFilters().ExecuteAsync($"DELETE FROM products");
        await db.InsertAsync(new Product { Name = "keep", Price = 1m, Stock = 1 });
        await db.InsertAsync(new Product { Name = "drop", Price = 2m, Stock = 2 });

        int deleted = await db.From<Product>().Where($"name = {"drop"}").ExecuteDeleteAsync();

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(1L);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_DeletesOnlyMatchingRows()
    {
        await using var db = await TestDb.MySqlAsync();
        await db.MigrateAsync();
        await db.IgnoreFilters().ExecuteAsync($"DELETE FROM products");
        await db.InsertAsync(new Product { Name = "keep", Price = 1m, Stock = 1 });
        await db.InsertAsync(new Product { Name = "drop", Price = 2m, Stock = 2 });

        int deleted = await db.From<Product>().Where($"name = {"drop"}").ExecuteDeleteAsync();

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(1L);
    }
}

[SoftDelete]
[Table("delete_drift_probe")]
internal sealed partial class DeleteDriftProbe
{
    [Key]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("deleted_at")]
    public string? DeletedAt { get; set; }
}
