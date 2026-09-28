using PalORM.Testing;

namespace PalORM.Integration.Tests;

public sealed class MigrationIndexTests
{
    [Test]
    public async Task MigrateAsync_CreatesDeclaredIndexes()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        long composite = await db.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = {"idx_indexed_products_cat_price"}");
        long unique = await db.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = {"ux_indexed_products_sku"}");

        await Assert.That(composite).IsEqualTo(1);
        await Assert.That(unique).IsEqualTo(1);
    }

    [Test]
    public async Task MigrateAsync_Idempotent_SecondRunSucceeds()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.MigrateAsync();

        long count = await db.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = {"ux_indexed_products_sku"}");
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task UniqueIndex_EnforcedByDatabase()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new IndexedProduct { Category = "a", Price = 1m, Sku = "SKU-1" });

        // v6.0 R4：唯一冲突统一翻译为 UniqueConstraintViolationException（Inner 保真驱动原生异常）
        UniqueConstraintViolationException? thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(async () =>
            await db.InsertAsync(new IndexedProduct { Category = "b", Price = 2m, Sku = "SKU-1" }));
        await Assert.That(thrown!.InnerException).IsTypeOf<Microsoft.Data.Sqlite.SqliteException>();
    }
}

#region Test Entities
[Table("indexed_products")]
[Index("idx_indexed_products_cat_price", "category", "price")]
public partial class IndexedProduct
{
    [Key] public long Id { get; set; }
    [Column("category")] public string Category { get; set; } = "";
    [Column("price")] public decimal Price { get; set; }
    [Column("sku")]
    [Unique]
    public string Sku { get; set; } = "";
}
#endregion
