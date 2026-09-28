using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// R4（v6.0）：跨方言唯一冲突异常——InsertAsync/SaveAsync（新增）撞唯一键抛
/// UniqueConstraintViolationException，InnerException 保真；非唯一约束违规不误伤。
/// 锚点：撤掉 FinishInsertAsync 的 catch-when 包装 → 测试红（抛的是原生驱动异常）。
/// </summary>
public sealed class UniqueViolationTests
{
    [Test]
    public async Task InsertAsync_DuplicateUniqueKey_ThrowsUnifiedException()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new UniqueProbeEntity { Sku = "SKU-1" });

        UniqueConstraintViolationException? thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            async () => await db.InsertAsync(new UniqueProbeEntity { Sku = "SKU-1" }));
        await Assert.That(thrown!.InnerException).IsTypeOf<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Test]
    public async Task InsertAsync_NotNullViolation_DoesNotThrowUnifiedException()
    {
        // 不误伤锚点：NOT NULL 违规（SQLite 主码同 19，扩展码 1299）不得翻译为唯一冲突
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            async () => await db.ExecuteAsync(
                $"INSERT INTO unique_probe (sku) VALUES (NULL)"));
    }

    [Test]
    public async Task Pg_InsertAsync_DuplicateUniqueKey_ThrowsUnifiedException()
    {
        await using var db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new UniqueProbeEntity { Sku = "SKU-1" });

        UniqueConstraintViolationException? thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            async () => await db.InsertAsync(new UniqueProbeEntity { Sku = "SKU-1" }));
        await Assert.That(thrown!.InnerException).IsTypeOf<Npgsql.PostgresException>();
    }

    [Test]
    public async Task MySql_InsertAsync_DuplicateUniqueKey_ThrowsUnifiedException()
    {
        await using var db = await TestDb.MySqlAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new UniqueProbeEntity { Sku = "SKU-1" });

        UniqueConstraintViolationException? thrown = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            async () => await db.InsertAsync(new UniqueProbeEntity { Sku = "SKU-1" }));
        await Assert.That(thrown!.InnerException).IsTypeOf<MySqlConnector.MySqlException>();
    }
}

#region Test Entities
[Table("unique_probe")]
public partial class UniqueProbeEntity
{
    [Key] public long Id { get; set; }
    [Column("sku")]
    [Unique]
    public string Sku { get; set; } = "";
}
#endregion
