using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

// 三个测试共用 ext_bulk_entities 表（DROP/CREATE），必须串行
[NotInParallel("ExtBulkTable")]
public sealed class ExternalDatabaseBulkTests
{
    private static DbOptions PgOpts => new()
    {
        ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
    };

    private static DbOptions MySqlOpts => new()
    {
        ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
    };

    private static ExtBulkEntity[] SampleRows() =>
    [
        new()
        {
            Code = "A1", Note = "note-a", Amount = 12.345678m,
            CreatedAt = new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc), OptionalCount = 7
        },
        // 可空列全 null 行——PG COPY 的 DBNull→NpgsqlDbType 推断疑点（ITM-318）
        new()
        {
            Code = "B2", Note = null, Amount = 0.000001m,
            CreatedAt = new DateTime(2026, 7, 18, 11, 30, 0, DateTimeKind.Utc), OptionalCount = null
        },
    ];

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_MigrateAndBinaryCopy_NullableAndUtcDateTime_RoundTrip()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities");
        await db.MigrateAsync();
        try
        {
            long inserted = await db.BulkInsertAsync(SampleRows());
            await Assert.That(inserted).IsEqualTo(2);

            var rows = (await db.GetAllAsync<ExtBulkEntity>()).OrderBy(r => r.Code).ToList();
            await Assert.That(rows.Count).IsEqualTo(2);
            await Assert.That(rows[0].Note).IsEqualTo("note-a");
            await Assert.That(rows[0].Amount).IsEqualTo(12.345678m);
            await Assert.That(rows[0].CreatedAt).IsEqualTo(new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc));
            await Assert.That(rows[1].Note).IsNull();
            await Assert.That(rows[1].OptionalCount).IsNull();
            await Assert.That(rows[1].Amount).IsEqualTo(0.000001m);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_MigrateWithUniqueIndexOnString_AndDecimalPrecision_RoundTrip()
    {
        // ITM-201 真库验证：被索引 string 列 VARCHAR(255)，首次迁移不得报 1170；
        // ITM-303 真库验证：DECIMAL(18,6) 下小数不被截断为整数
        await using var db = await DataSession<MySqlProvider>.CreateAsync(MySqlOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities");
        await db.MigrateAsync();
        try
        {
            // 迁移幂等：二次执行经 1061 兜底不抛
            await db.MigrateAsync();

            long inserted = await db.BulkInsertAsync(SampleRows());
            await Assert.That(inserted).IsEqualTo(2);

            var rows = (await db.GetAllAsync<ExtBulkEntity>()).OrderBy(r => r.Code).ToList();
            await Assert.That(rows[0].Amount).IsEqualTo(12.345678m);
            await Assert.That(rows[1].Amount).IsEqualTo(0.000001m);
            await Assert.That(rows[1].Note).IsNull();
            await Assert.That(rows[1].OptionalCount).IsNull();

            // 唯一索引真实生效 + IsUniqueViolation 统一判定（ITM-314 真库验证）
            try
            {
                await db.InsertAsync(new ExtBulkEntity
                {
                    Code = "A1", Amount = 1m,
                    CreatedAt = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc)
                });
                throw new InvalidOperationException("重复 code 应触发唯一约束冲突");
            }
            catch (Exception ex)
            {
                await Assert.That(MySqlProvider.IsUniqueViolation(ex)).IsTrue();
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_UniqueViolation_IsUniformlyDetected()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities");
        await db.MigrateAsync();
        try
        {
            await db.InsertAsync(new ExtBulkEntity
            {
                Code = "DUP", Amount = 1m,
                CreatedAt = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc)
            });
            try
            {
                await db.InsertAsync(new ExtBulkEntity
                {
                    Code = "DUP", Amount = 2m,
                    CreatedAt = new DateTime(2026, 7, 18, 13, 0, 0, DateTimeKind.Utc)
                });
                throw new InvalidOperationException("重复 code 应触发唯一约束冲突");
            }
            catch (Exception ex)
            {
                await Assert.That(PostgreSqlProvider.IsUniqueViolation(ex)).IsTrue();
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities");
        }
    }
}

#region Test Entities
// ITM-317/318 真库验证实体：可空列 + UTC DateTime + decimal 精度 + 唯一索引 string 列。
// PG Binary COPY 对 NpgsqlDbType 推断的两个疑点（DBNull 无法推断 / UTC DateTime 与
// TIMESTAMP 列错配）与 MySQL 索引 DDL(1170)/DECIMAL 截断都在此覆盖。
[Table("ext_bulk_entities")]
[Index("ux_ext_bulk_entities_code", "code", Unique = true)]
public partial class ExtBulkEntity
{
    [Key] public long Id { get; set; }
    [Column("code")] [Required] public string Code { get; set; } = "";
    [Column("note")] public string? Note { get; set; }
    [Column("amount")] public decimal Amount { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("optional_count")] public int? OptionalCount { get; set; }
}
#endregion
