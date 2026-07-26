using PalORM.Testing;

namespace PalORM.Integration.Tests;

[Table("bulk_insert_defaults")]
public partial class BulkInsertDefaultEntity
{
    [Key(AutoIncrement = false)]
    public string Id { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("created_by")]
    [IgnoreOnInsert]
    public string CreatedBy { get; set; } = "client";
}

[Table("bulk_generated_only")]
// PALORM023/024：测试专用的"仅 Key + Generated"实体——验证 BulkInsert 对生成列的处理
#pragma warning disable PALORM023, PALORM024
public partial class BulkGeneratedOnlyEntity
#pragma warning restore PALORM023, PALORM024
{
    [Key]
    public long Id { get; set; }
}

[Table("bulk_key_only")]
// PALORM023/024：测试专用的"仅 Key"实体——验证 BulkInsert 在最小列集下的行为
#pragma warning disable PALORM023, PALORM024
public partial class BulkKeyOnlyEntity
#pragma warning restore PALORM023, PALORM024
{
    [Key(AutoIncrement = false)]
    public string Id { get; set; } = "";
}

public sealed class BulkDataTests
{
    [Test]
    public async Task BulkDelete_SoftDeleteEntity_UpdatesDeletedAt()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        SoftDeletableEntity first = await db.InsertAsync(
            new SoftDeletableEntity { Name = "first" });
        SoftDeletableEntity second = await db.InsertAsync(
            new SoftDeletableEntity { Name = "second" });

        db.IgnoreFilters();
        long affected = await db.BulkDeleteAsync<SoftDeletableEntity>(
            [first.Id, second.Id]);
        List<SoftDeletableEntity> deleted = await db
            .From<SoftDeletableEntity>()
            .OrderBy(entity => entity.Id)
            .ToListAsync();

        await Assert.That(affected).IsEqualTo(2);
        await Assert.That(deleted.Count).IsEqualTo(2);
        await Assert.That(deleted.All(entity => entity.DeletedAt is not null)).IsTrue();
    }

    [Test]
    public async Task BulkDelete_ConverterPrimaryKeys_UsesProviderValues()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        BinaryId firstId = new(Guid.NewGuid());
        BinaryId secondId = new(Guid.NewGuid());
        await db.InsertAsync(new ConvertedSoftDeleteEntity
        {
            Id = firstId,
            Name = "first"
        });
        await db.InsertAsync(new ConvertedSoftDeleteEntity
        {
            Id = secondId,
            Name = "second"
        });

        long affected = await db.BulkDeleteAsync<ConvertedSoftDeleteEntity>(
            [firstId, secondId]);
        List<ConvertedSoftDeleteEntity> deleted = await db
            .IgnoreFilters()
            .From<ConvertedSoftDeleteEntity>()
            .ToListAsync();

        await Assert.That(affected).IsEqualTo(2);
        await Assert.That(deleted.Count).IsEqualTo(2);
        await Assert.That(deleted.All(entity => entity.DeletedAt is not null)).IsTrue();
    }

    // v5.0 阶段 4.3b：BulkUpdateBatchAsync 测试——验证批量 UPDATE 单次 RTT 完成多行更新。
    [Test]
    public async Task BulkUpdateBatch_UpdatesMultipleRowsInSingleStatement()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        Product[] toInsert =
        [
            new Product { Name = "p1", Price = 1m, Stock = 1 },
            new Product { Name = "p2", Price = 2m, Stock = 2 },
            new Product { Name = "p3", Price = 3m, Stock = 3 }
        ];
        await db.BulkInsertAsync(toInsert);
        List<Product> inserted = await db.From<Product>().ToListAsync();

        foreach (Product p in inserted) p.Price *= 100;
        long affected = await db.BulkUpdateBatchAsync(inserted);
        await Assert.That(affected).IsEqualTo(3);

        List<Product> updated = await db.From<Product>().ToListAsync();
        await Assert.That(updated[0].Price).IsEqualTo(100m);
        await Assert.That(updated[1].Price).IsEqualTo(200m);
        await Assert.That(updated[2].Price).IsEqualTo(300m);
    }

    [Test]
    public async Task BulkUpdateBatch_N1_StillBatches_NoThreshold()
    {
        // 方案 Y 严格版——N=1 也走批量（无内部阈值）
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "single", Price = 1m, Stock = 1 });
        List<Product> list = await db.From<Product>().ToListAsync();
        list[0].Price = 999m;

        long affected = await db.BulkUpdateBatchAsync([list[0]]);
        await Assert.That(affected).IsEqualTo(1);
        Product loaded = (await db.From<Product>().ToListAsync())[0];
        await Assert.That(loaded.Price).IsEqualTo(999m);
    }

    [Test]
    public async Task BulkUpdateBatch_EmptyList_ReturnsZero()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        long affected = await db.BulkUpdateBatchAsync<Product>([]);
        await Assert.That(affected).IsEqualTo(0);
    }

    [Test]
    public async Task BulkDelete_MoreThanOneBatch_UsesAmbientTransaction()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var products = Enumerable.Range(0, 501)
            .Select(index => new Product
            {
                Name = $"product-{index}",
                Price = index,
                Stock = index
            })
            .ToArray();
        await db.BulkInsertAsync(products);
        object[] keys =
        [
            .. (await db.From<Product>().ToListAsync())
                .Select(product => (object)product.Id)
        ];
        await using var transaction = await db.BeginTransactionAsync();

        long affected = await db.BulkDeleteAsync<Product>(keys);
        await transaction.RollbackAsync();

        await Assert.That(affected).IsEqualTo(501);
        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(501);
    }

    [Test]
    public async Task BulkInsert_UsesGeneratedInsertColumnsAndDatabaseDefault()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.ExecuteAsync(
            $"CREATE TABLE bulk_insert_defaults (Id TEXT PRIMARY KEY, name TEXT NOT NULL, created_by TEXT NOT NULL DEFAULT 'database')");

        long affected = await db.BulkInsertAsync<BulkInsertDefaultEntity>(
        [
            new BulkInsertDefaultEntity { Id = "a", Name = "first" },
            new BulkInsertDefaultEntity { Id = "b", Name = "second" }
        ]);
        IReadOnlyList<BulkInsertDefaultEntity> inserted = await db
            .From<BulkInsertDefaultEntity>()
            .OrderBy(entity => entity.Id)
            .ToListAsync();

        await Assert.That(affected).IsEqualTo(2);
        await Assert.That(inserted.Select(entity => entity.Id))
            .IsEquivalentTo(["a", "b"]);
        await Assert.That(inserted.All(entity => entity.CreatedBy == "database"))
            .IsTrue();
    }

    [Test]
    public async Task BulkInsert_ZeroInsertColumns_FailsBeforeDatabaseAccess()
    {
        await using var db = await TestDb.SqliteAsync();

        Exception? exception = await Assert.That(async () =>
            await db.BulkInsertAsync([new BulkGeneratedOnlyEntity()]))
            .Throws<InvalidOperationException>();

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message)
            .Contains("has no generated insert metadata");
    }

    [Test]
    public async Task Update_KeyOnlyEntity_FailsBeforeDatabaseAccess()
    {
        await using var db = await TestDb.SqliteAsync();

        Exception? exception = await Assert.That(async () =>
            await db.UpdateAsync(new BulkKeyOnlyEntity { Id = "only" }))
            .Throws<InvalidOperationException>();

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message)
            .Contains("has no updatable columns");
    }

    [Test]
    public async Task Save_KeyOnlyEntity_PerformsIdempotentInsert()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var entity = new BulkKeyOnlyEntity { Id = "only" };

        await db.SaveAsync(entity);
        await db.SaveAsync(entity);

        await Assert.That(await db.CountAsync<BulkKeyOnlyEntity>()).IsEqualTo(1);
    }
}
