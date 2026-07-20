using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

// 实体1: Order (已存在)
// 实体2: Product
[Table("products")]
public partial class Product
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("price")] public decimal Price { get; set; }
    [Column("stock")] public int Stock { get; set; }
}

// 实体3: Customer
[Table("customers")]
public partial class Customer
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("email")] public string? Email { get; set; }
}

// 实体4: OrderItem (关联 Order)
[Table("order_items")]
public partial class OrderItem
{
    [Key] public long Id { get; set; }
    [Column("order_id")] public long OrderId { get; set; }
    [Column("product_id")] public long ProductId { get; set; }
    [Column("quantity")] public int Quantity { get; set; }
}

// 实体5: AuditLog
[Table("audit_logs")]
public partial class AuditLog
{
    [Key] public long Id { get; set; }
    [Column("action")] public string Action { get; set; } = "";
    [Column("entity_type")] public string EntityType { get; set; } = "";
    [Column("entity_id")] public long EntityId { get; set; }
}

// 实体6: OwnedJson 测试
public class Address { public string Street { get; set; } = ""; public string City { get; set; } = ""; }
[Table("json_entity")]
public partial class JsonEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("metadata")] [OwnedJson] public string Metadata { get; set; } = "{}";
}

public readonly record struct BinaryId(Guid Value);

public sealed class BinaryIdConverter : IValueConverter<BinaryId, string>
{
    public string ToProvider(BinaryId value) => value.Value.ToString("D");
    public BinaryId FromProvider(string value) => new(Guid.Parse(value));
}

[Table("computed_converted_entities")]
public partial class ComputedConvertedEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("computed_name")]
    [Computed("name || '-computed'")]
    public string ComputedName { get; set; } = "";
    [Column("external_id")]
    [Converter(typeof(BinaryIdConverter))]
    public BinaryId ExternalId { get; set; }
}

[Table("converted_key_entities")]
public partial class ConvertedKeyEntity
{
    [Key(AutoIncrement = false)]
    [Converter(typeof(BinaryIdConverter))]
    public BinaryId Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";
}

[SoftDelete]
[Table("converted_soft_delete_entities")]
public partial class ConvertedSoftDeleteEntity
{
    [Key(AutoIncrement = false)]
    [Converter(typeof(BinaryIdConverter))]
    public BinaryId Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("deleted_at")]
    public string? DeletedAt { get; set; }
}

[Table("upsert_shape_entities")]
public partial class UpsertShapeEntity
{
    [Key(AutoIncrement = false)]
    public string Id { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";
}

[Table("order")]
public partial class ReservedIdentifierEntity
{
    [Key]
    public long Id { get; set; }

    [Column("select")]
    public string Value { get; set; } = "";
}

public sealed class MultiEntityTests
{
    [Test]
    public async Task FiveEntities_AllMigrateAndCRUD()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        // Product
        var p = await db.InsertAsync(new Product { Name = "Widget", Price = 9.99m, Stock = 100 });
        await Assert.That(p.Id).IsGreaterThan(0);
        var pg = await db.GetAsync<Product>(p.Id);
        await Assert.That(pg!.Name).IsEqualTo("Widget");

        // Customer
        var c = await db.InsertAsync(new Customer { Name = "Alice", Email = "a@b.com" });
        await Assert.That(c.Id).IsGreaterThan(0);
        var cg = await db.GetAsync<Customer>(c.Id);
        await Assert.That(cg!.Name).IsEqualTo("Alice");

        // OrderItem
        var oi = await db.InsertAsync(new OrderItem { OrderId = 1, ProductId = p.Id, Quantity = 3 });
        await Assert.That(oi.Id).IsGreaterThan(0);

        // AuditLog
        var al = await db.InsertAsync(new AuditLog { Action = "CREATE", EntityType = "Order", EntityId = 1 });
        await Assert.That(al.Id).IsGreaterThan(0);

        // Count all
        var products = await db.GetAllAsync<Product>();
        var customers = await db.GetAllAsync<Customer>();
        await Assert.That(products.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(customers.Count).IsGreaterThanOrEqualTo(1);
    }


    [Test]
    public async Task ComputedAndConvertedColumns_RoundTripThroughSqlite()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var externalId = new BinaryId(
            Guid.Parse("4baf3f10-b80f-4479-a4b7-49af31d466b1"));

        ComputedConvertedEntity inserted = await db.InsertAsync(
            new ComputedConvertedEntity
            {
                Name = "item",
                ExternalId = externalId
            });
        ComputedConvertedEntity? loaded =
            await db.GetAsync<ComputedConvertedEntity>(inserted.Id);

        await Assert.That(inserted.ComputedName)
            .IsEqualTo("item-computed");
        await Assert.That(inserted.ExternalId).IsEqualTo(externalId);
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.ComputedName)
            .IsEqualTo("item-computed");
        await Assert.That(loaded.ExternalId).IsEqualTo(externalId);
    }

    [Test]
    public async Task ConvertedPrimaryKey_DeleteUsesProviderValue()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var id = new BinaryId(
            Guid.Parse("ef0f459f-780f-49bb-a9ea-733dba329613"));
        await db.InsertAsync(new ConvertedKeyEntity
        {
            Id = id,
            Name = "delete"
        });

        ConvertedKeyEntity? beforeDelete =
            await db.GetAsync<ConvertedKeyEntity>(id);
        int affected = await db.DeleteAsync<ConvertedKeyEntity>(id);
        ConvertedKeyEntity? afterDelete =
            await db.GetAsync<ConvertedKeyEntity>(id);

        await Assert.That(beforeDelete?.Name).IsEqualTo("delete");
        await Assert.That(affected).IsEqualTo(1);
        await Assert.That(afterDelete).IsNull();
    }

    [Test]
    public async Task ConvertedPrimaryKey_SoftDeleteUsesProviderValue()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var id = new BinaryId(
            Guid.Parse("65f58db9-16ed-4265-95a9-0743ed078154"));
        await db.InsertAsync(new ConvertedSoftDeleteEntity
        {
            Id = id,
            Name = "soft-delete"
        });

        int affected = await db.DeleteAsync<ConvertedSoftDeleteEntity>(id);
        ConvertedSoftDeleteEntity? visible =
            await db.GetAsync<ConvertedSoftDeleteEntity>(id);
        ConvertedSoftDeleteEntity? deleted =
            await db.IgnoreFilters()
                .GetAsync<ConvertedSoftDeleteEntity>(id);

        await Assert.That(affected).IsEqualTo(1);
        await Assert.That(visible).IsNull();
        await Assert.That(deleted?.DeletedAt).IsNotNull();
    }

    [Test]
    public async Task EntityQueries_UseGeneratedModelColumnOrder()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.ExecuteAsync(
            $"CREATE TABLE upsert_shape_entities (extra TEXT, name TEXT NOT NULL, Id TEXT PRIMARY KEY)");

        UpsertShapeEntity saved = await db.SaveAsync(new UpsertShapeEntity
        {
            Id = "shape-1",
            Name = "item"
        });
        UpsertShapeEntity? byId = await db.GetAsync<UpsertShapeEntity>("shape-1");
        List<UpsertShapeEntity> all = await db.GetAllAsync<UpsertShapeEntity>();
        var query = db.From<UpsertShapeEntity>();
        List<UpsertShapeEntity> queried = await query.ToListAsync();

        await Assert.That(saved.Id).IsEqualTo("shape-1");
        await Assert.That(saved.Name).IsEqualTo("item");
        await Assert.That(byId?.Id).IsEqualTo("shape-1");
        await Assert.That(byId?.Name).IsEqualTo("item");
        await Assert.That(all.Single().Id).IsEqualTo("shape-1");
        await Assert.That(all.Single().Name).IsEqualTo("item");
        await Assert.That(queried.Single().Id).IsEqualTo("shape-1");
        await Assert.That(queried.Single().Name).IsEqualTo("item");
        await Assert.That(query.AsDryRun().Sql).DoesNotContain("SELECT *");
    }

    [Test]
    public async Task ReservedIdentifiers_MigrateAndCrudThroughSqlite()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        ReservedIdentifierEntity inserted = await db.InsertAsync(
            new ReservedIdentifierEntity { Value = "reserved" });
        inserted.Value = "updated";
        int updated = await db.UpdateAsync(inserted);
        ReservedIdentifierEntity? loaded =
            await db.GetAsync<ReservedIdentifierEntity>(inserted.Id);
        int deleted = await db.DeleteAsync<ReservedIdentifierEntity>(inserted.Id);

        await Assert.That(inserted.Id).IsGreaterThan(0);
        await Assert.That(updated).IsEqualTo(1);
        await Assert.That(loaded?.Value).IsEqualTo("updated");
        await Assert.That(deleted).IsEqualTo(1);
    }

    [Test]
    public async Task OrderItem_JoinsWithProduct()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var p = await db.InsertAsync(new Product { Name = "Gadget", Price = 5m, Stock = 50 });
        var oi = await db.InsertAsync(new OrderItem { OrderId = 1, ProductId = p.Id, Quantity = 2 });

        // JOIN query
        var result = await db.From<OrderItem>()
            .InnerJoin<Product>($"order_items.product_id = products.id")
            .Where($"products.name = {"Gadget"}")
            .ToListAsync();
        await Assert.That(result.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WhereIn_EmptyList_ReturnsZero()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var r = await db.From<Product>().WhereIn(p => p.Id, []).ToListAsync();
        await Assert.That(r.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LeftJoin_ReturnsAllLeftRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var p = await db.InsertAsync(new Product { Name = "L", Price = 1m, Stock = 0 });
        var r = await db.From<Product>().LeftJoin<OrderItem>($"products.id = order_items.product_id").ToListAsync();
        await Assert.That(r.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task GridReader_SingleResult_Works()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "G1", Price = 1m, Stock = 0 });
        await using var gr = await db.From<Product>().QueryMultipleAsync($"SELECT * FROM products LIMIT 1");
        var products = await gr.ReadAsync<Product>();
        await Assert.That(products.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Include_GeneratesJoin()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var p = await db.InsertAsync(new Product { Name = "Inc", Price = 1m, Stock = 0 });
        var oi = await db.InsertAsync(new OrderItem { OrderId = 1, ProductId = p.Id, Quantity = 1 });
        var dry = db.From<OrderItem>().Include<Product>(oi => oi.ProductId, p => p.Id).AsDryRun();
        await Assert.That(dry.Sql).Contains("JOIN");
    }

    [Test]
    public async Task BulkUpdate_ModifiesMultipleRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var p1 = await db.InsertAsync(new Product { Name = "U1", Price = 1m, Stock = 10 });
        var p2 = await db.InsertAsync(new Product { Name = "U2", Price = 2m, Stock = 20 });
        p1.Price = 99m; p2.Price = 99m;
        long n = await db.BulkUpdateAsync([p1, p2]);
        await Assert.That(n).IsEqualTo(2);
    }

    [Test]
    public async Task BulkDelete_RemovesMultipleRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var p1 = await db.InsertAsync(new Product { Name = "D1", Price = 1m, Stock = 0 });
        var p2 = await db.InsertAsync(new Product { Name = "D2", Price = 2m, Stock = 0 });
        long n = await db.BulkDeleteAsync<Product>([p1.Id, p2.Id]);
        await Assert.That(n).IsEqualTo(2);
    }

    [Test]
    public async Task SoftDelete_PhysicalDelete_RemovesRow()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var p = await db.InsertAsync(new Product { Name = "PD", Price = 1m, Stock = 0 });
        await db.DeleteAsync<Product>(p.Id);
        var gone = await db.GetAsync<Product>(p.Id);
        await Assert.That(gone).IsNull();
    }

    // ─── ConcurrencyCheck ────────────────────────────────
    [Test]
    public async Task ConcurrencyCheck_UpdateSucceeds()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var p = await db.InsertAsync(new Product { Name = "CC", Price = 1m, Stock = 0 });
        p.Price = 99m;
        int rows = await db.UpdateAsync(p);
        await Assert.That(rows).IsEqualTo(1);
    }

    // ─── ValidateSchema ──────────────────────────────────

    [Test]
    public async Task ValidateSchema_MatchingSqliteTable_ReturnsNoIssues()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        var issues = await db.ValidateSchemaAsync<Product>();

        await Assert.That(issues).IsEmpty();
    }

    [Test]
    public async Task BulkInsert_InvalidBatchSize_FailsBeforeDatabaseAccess()
    {
        await using var db = await TestDb.SqliteAsync();

        await Assert.That(async () => await db.BulkInsertAsync(Array.Empty<Product>(), 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await db.BulkInsertAsync(
            [new Product { Name = "invalid", Price = 1m, Stock = 1 }], -1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task QueryStructuralNames_AreQuotedOrRejected()
    {
        await using var db = await TestDb.SqliteAsync();

        var cte = db.From<Product>().With("cte\"name", $"SELECT * FROM products").AsDryRun();
        await Assert.That(cte.Sql).Contains("WITH \"cte\"\"name\"");
        await Assert.That(() => db.From<Product>().Tag("safe */ SELECT 1"))
            .Throws<ArgumentException>();
        await Assert.That(() => db.From<Product>().WithMetrics("safe */ SELECT 1"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ReadRoute_OpensAtExecutionAndForWriteReturnsToPrimary()
    {
        const string connectionString = "Data Source=route_test;Mode=Memory;Cache=Shared";
        var options = new DbOptions
        {
            ConnectionString = connectionString,
            ReadConnectionString = connectionString
        };
        await using var db = await DataSession<SqliteProvider>.CreateAsync(options);
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "route", Price = 1m, Stock = 1 });

        var readBuilder = db.From<Product>().ForRead();
        await Assert.That(readBuilder.AsDryRun().Sql).Contains("products");
        var readRows = await readBuilder.ToListAsync();
        var writeRows = await db.From<Product>().ForRead().ForWrite().ToListAsync();

        await Assert.That(readRows.Count).IsEqualTo(1);
        await Assert.That(writeRows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WithTransaction_RollsBackDataSessionCrud()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        await Assert.That(async () => await db.WithTransaction(async ct =>
        {
            await db.InsertAsync(new Product { Name = "rollback", Price = 1m, Stock = 1 }, ct);
            throw new InvalidOperationException("rollback");
        })).Throws<InvalidOperationException>();

        await Assert.That(await db.CountAsync<Product>()).IsEqualTo(0);
    }

    [Test]
    public async Task QueryMultiple_InterpolatedValue_IsParameterized()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "parameter", Price = 1m, Stock = 1 });

        await using var reader = await db.From<Product>()
            .QueryMultipleAsync($"SELECT * FROM products WHERE name = {"parameter"}");
        var rows = await reader.ReadAsync<Product>();

        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task QueryBuilder_WithParameters_CanExecuteTwice()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "repeat", Price = 1m, Stock = 1 });
        var query = db.From<Product>().Where($"name = {"repeat"}");

        var first = await query.ToListAsync();
        var second = await query.ToListAsync();

        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(second.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Join_InterpolatedValue_IsBoundAndOrderedBeforeWhere()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var product = await db.InsertAsync(new Product { Name = "joined", Price = 1m, Stock = 1 });
        await db.InsertAsync(new OrderItem { OrderId = 1, ProductId = product.Id, Quantity = 2 });

        var rows = await db.From<OrderItem>()
            .Where($"order_items.quantity > {0}")
            .InnerJoin<Product>($"order_items.product_id = products.id AND products.name = {"joined"}")
            .ToListAsync();

        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Include_SplitQuery_RemovesJoinWithoutDroppingWhere()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var product = await db.InsertAsync(new Product { Name = "split", Price = 1m, Stock = 1 });
        await db.InsertAsync(new OrderItem { OrderId = 1, ProductId = product.Id, Quantity = 2 });

        var query = db.From<OrderItem>()
            .Include<Product>(item => item.ProductId, product => product.Id)
            .Where($"quantity = {2}")
            .AsSplitQuery();
        var dryRun = query.AsDryRun();
        var rows = await query.ToListAsync();

        await Assert.That(dryRun.Sql).DoesNotContain("JOIN");
        await Assert.That(dryRun.Sql).Contains("WHERE (quantity = @p0)");
        await Assert.That(dryRun.Parameters.Count).IsEqualTo(1);
        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SplitQuery_DropsJoinParameters()
    {
        await using var db = await TestDb.SqliteAsync();
        var dryRun = db.From<OrderItem>()
            .InnerJoin<Product>($"order_items.product_id = products.id AND products.name = {"unused"}")
            .Where($"quantity = {2}")
            .AsSplitQuery()
            .AsDryRun();

        await Assert.That(dryRun.Sql).DoesNotContain("JOIN");
        await Assert.That(dryRun.Parameters.Count).IsEqualTo(1);
        await Assert.That(dryRun.Parameters[0].Value).IsEqualTo(2);
    }

    [Test]
    public async Task QueryMultiple_CompositeFormat_RemainsParameterized()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "format", Price = 12.5m, Stock = 1 });

        await using var reader = await db.From<Product>()
            .QueryMultipleAsync($"SELECT * FROM products WHERE price = {12.5m:N1}");
        var rows = await reader.ReadAsync<Product>();

        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ThenInclude_RequiresBothJoinKeys()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var product = await db.InsertAsync(new Product { Name = "nested", Price = 1m, Stock = 1 });
        await db.InsertAsync(new OrderItem { OrderId = 1, ProductId = product.Id, Quantity = 1 });

        var query = db.From<OrderItem>()
            .ThenInclude<Product, OrderItem>(item => item.Id, parent => parent.ProductId);
        var dryRun = query.AsDryRun();
        var rows = await query.ToListAsync();

        await Assert.That(dryRun.Sql).DoesNotContain("...");
        await Assert.That(dryRun.Sql).Contains("products");
        await Assert.That(dryRun.Sql).Contains("order_items");
        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JoinPagination_CountPreservesJoinAndParameters()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        var product = await db.InsertAsync(new Product { Name = "page", Price = 1m, Stock = 1 });
        await db.InsertAsync(new OrderItem { OrderId = 1, ProductId = product.Id, Quantity = 2 });

        var (rows, total) = await db.From<OrderItem>()
            .InnerJoin<Product>($"order_items.product_id = products.id AND products.name = {"page"}")
            .Where($"order_items.quantity = {2}")
            .ToPageAsync(10, item => item.Id);

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(total).IsEqualTo(1);
    }

    [Test]
    public async Task QueryMultiple_ReadRoute_ReleasesOwnedConnection()
    {
        const string connectionString = "Data Source=grid_route_test;Mode=Memory;Cache=Shared";
        var options = new DbOptions
        {
            ConnectionString = connectionString,
            ReadConnectionString = connectionString
        };
        await using var db = await DataSession<SqliteProvider>.CreateAsync(options);
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "grid", Price = 1m, Stock = 1 });

        await using (var reader = await db.From<Product>().ForRead().QueryMultipleAsync($"SELECT * FROM products"))
        {
            var rows = await reader.ReadAsync<Product>();
            await Assert.That(rows.Count).IsEqualTo(1);
        }

        var rowsAfterDispose = await db.From<Product>().ForRead().ToListAsync();
        await Assert.That(rowsAfterDispose.Count).IsEqualTo(1);
    }
}
