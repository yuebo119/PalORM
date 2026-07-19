using System.Text.Json.Serialization;
using PalORM.MySql;

namespace PalORM.AotTest.MySql;

[Table("aot_mysql_test")]
internal sealed partial class AotMySqlEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
    [Column("version")][ConcurrencyCheck] public long Version { get; set; }
}

[SoftDelete]
[Table("aot_mysql_bulk_test")]
internal sealed partial class AotMySqlBulkEntity
{
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
    [Column("created_by")][IgnoreOnInsert] public string CreatedBy { get; set; } = "client";
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }
}

[Table("aot_mysql_json_test")]
internal sealed partial class AotMySqlJsonEntity
{
    [Key] public long Id { get; set; }
    [Column("details")][OwnedJson(typeof(AotMySqlJsonContext))] public AotMySqlDetails Details { get; set; } = new();
}

internal sealed class AotMySqlDetails
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

[JsonSerializable(typeof(AotMySqlDetails), TypeInfoPropertyName = "AotMySqlDetailsInfo")]
internal sealed partial class AotMySqlJsonContext : JsonSerializerContext;

// ITM-419：经 MigrateAsync（源生成 CreateTableSqlByDialect MySQL 产物）建表的实体——
// 此前 MySQL 宿主全部手写 DDL，源生成 MySQL DDL 在 AOT 原生路径零真库验证（E1 残留敞口）
[Table("aot_mysql_migrated")]
[Index("ix_aot_mysql_migrated_label", "label")]
internal sealed partial class AotMySqlMigratedEntity
{
    [Key] public long Id { get; set; }
    [Column("label")] public string Label { get; set; } = "";
    [Column("amount")] public decimal Amount { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
}

internal static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303",
        Justification = "固定英文文本是 Native AOT smoke test 的机器可读成功标记。")]
    internal static async Task Main()
    {
        string connectionString = Environment.GetEnvironmentVariable("PALORM_MYSQL_CONNECTION")
            ?? throw new InvalidOperationException(
                "PALORM_MYSQL_CONNECTION is required. "
                + "Run 'source scripts/set-test-env.sh' after creating .env.test, "
                + "or set PALORM_MYSQL_* variables for appsettings.test.json template expansion.");
        var options = new DbOptions { ConnectionString = connectionString };
        DataSession<MySqlProvider> db = await DataSession<MySqlProvider>.CreateAsync(options).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_mysql_bulk_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_mysql_json_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_mysql_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_mysql_migrated").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_mysql_test (id BIGINT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL, version BIGINT NOT NULL)").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_mysql_json_test (id BIGINT AUTO_INCREMENT PRIMARY KEY, details TEXT NOT NULL)").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_mysql_bulk_test (Id VARCHAR(255) PRIMARY KEY, name TEXT NOT NULL, created_by VARCHAR(64) NOT NULL DEFAULT 'database', deleted_at DATETIME(6))").ConfigureAwait(false);

            AotMySqlEntity inserted = await db.InsertAsync(new AotMySqlEntity
            {
                Name = "AOT MySQL works!",
                Value = 42,
                Version = 0
            }).ConfigureAwait(false);
            if (inserted.Id <= 0)
                throw new InvalidOperationException("MySQL INSERT failed");
            if (await db.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM aot_mysql_test WHERE id = {inserted.Id:N0}")
                    .ConfigureAwait(false) != 1)
                throw new InvalidOperationException("MySQL composite format parameterization failed");

            AotMySqlEntity first = await db.GetAsync<AotMySqlEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("MySQL GET failed");
            AotMySqlEntity stale = await db.GetAsync<AotMySqlEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("MySQL stale GET failed");

            first.Name = "AOT MySQL updated";
            if (await db.UpdateAsync(first).ConfigureAwait(false) != 1 || first.Version != 1)
                throw new InvalidOperationException("MySQL UPDATE failed");

            stale.Name = "stale";
            try
            {
                await db.UpdateAsync(stale).ConfigureAwait(false);
                throw new InvalidOperationException("MySQL concurrency conflict was not detected");
            }
            catch (ConcurrencyConflictException)
            {
                // S108: 测试期望此异常——stale row 必须被并发控制拒绝。
            }

            AotMySqlJsonEntity json = await db.InsertAsync(new AotMySqlJsonEntity
            {
                Details = new AotMySqlDetails { Name = "source-generated", Count = 7 }
            }).ConfigureAwait(false);
            AotMySqlJsonEntity roundTrip = await db.GetAsync<AotMySqlJsonEntity>(json.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("MySQL OwnedJson GET failed");
            if (roundTrip.Details.Name != "source-generated" || roundTrip.Details.Count != 7)
                throw new InvalidOperationException("MySQL OwnedJson round trip failed");

            var bulkEntities = new[]
            {
                new AotMySqlBulkEntity { Id = "first", Name = "first" },
                new AotMySqlBulkEntity { Id = "second", Name = "second" }
            };
            if (await db.BulkInsertAsync(bulkEntities).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("MySQL bulk INSERT failed");
            AotMySqlBulkEntity? bulk = await db.GetAsync<AotMySqlBulkEntity>("first").ConfigureAwait(false);
            if (bulk?.CreatedBy != "database")
                throw new InvalidOperationException("MySQL bulk INSERT metadata failed");
            if (await db.BulkDeleteAsync<AotMySqlBulkEntity>(["first", "second"]).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("MySQL bulk soft DELETE failed");
            AotMySqlBulkEntity? softDeleted = await db.IgnoreFilters()
                .GetAsync<AotMySqlBulkEntity>("first").ConfigureAwait(false);
            if (softDeleted?.DeletedAt is null)
                throw new InvalidOperationException("MySQL bulk soft DELETE metadata failed");

            if (await db.DeleteAsync<AotMySqlEntity>(inserted.Id).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("MySQL DELETE failed");
            await db.DeleteAsync<AotMySqlJsonEntity>(json.Id).ConfigureAwait(false);

            // ITM-419：源生成 MySQL DDL（表 + 索引）经 MigrateAsync 真库执行 + CRUD 往返
            await db.MigrateAsync().ConfigureAwait(false);
            await db.MigrateAsync().ConfigureAwait(false); // 幂等：重名索引 1061 由 IsDuplicateSchemaObject 兜底
            AotMySqlMigratedEntity migrated = await db.InsertAsync(new AotMySqlMigratedEntity
            {
                Label = "migrated",
                Amount = 12.5m,
                CreatedAt = DateTimeOffset.UtcNow
            }).ConfigureAwait(false);
            AotMySqlMigratedEntity migratedBack = await db.GetAsync<AotMySqlMigratedEntity>(migrated.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("MySQL migrated-table GET failed");
            if (migratedBack.Label != "migrated" || migratedBack.Amount != 12.5m)
                throw new InvalidOperationException("MySQL migrated-table round trip failed");
        }

        Console.WriteLine("PalORM AOT MySQL verification PASSED");
    }
}
