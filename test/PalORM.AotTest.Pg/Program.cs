using System.Text.Json.Serialization;
using PalORM.PostgreSql;

namespace PalORM.AotTest.Pg;

[Table("aot_pg_test")]
internal sealed partial class AotPgEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
    [Column("version")][ConcurrencyCheck] public long Version { get; set; }
}

[SoftDelete]
[Table("aot_pg_bulk_test")]
internal sealed partial class AotPgBulkEntity
{
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
    [Column("created_by")][IgnoreOnInsert] public string CreatedBy { get; set; } = "client";
    [Column("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }
}

[Table("aot_pg_json_test")]
internal sealed partial class AotPgJsonEntity
{
    [Key] public long Id { get; set; }
    [Column("details")][OwnedJson(typeof(AotPgJsonContext))] public AotPgDetails Details { get; set; } = new();
}

// ITM-419：经 MigrateAsync（源生成 CreateTableSqlByDialect PG 产物）建表的实体——
// 此前 PG 宿主全部手写 DDL，源生成 PG DDL 在 AOT 原生路径零真库验证（E1 残留敞口）
[Table("aot_pg_migrated")]
[Index("ix_aot_pg_migrated_label", "label")]
internal sealed partial class AotPgMigratedEntity
{
    [Key] public long Id { get; set; }
    [Column("label")] public string Label { get; set; } = "";
    [Column("amount")] public decimal Amount { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class AotPgDetails
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

[JsonSerializable(typeof(AotPgDetails), TypeInfoPropertyName = "AotPgDetailsInfo")]
internal sealed partial class AotPgJsonContext : JsonSerializerContext;

internal static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303",
        Justification = "固定英文文本是 Native AOT smoke test 的机器可读成功标记。")]
    internal static async Task Main()
    {
        string connectionString = Environment.GetEnvironmentVariable("PALORM_PG_CONNECTION")
            ?? throw new InvalidOperationException(
                "PALORM_PG_CONNECTION is required. "
                + "Run 'source scripts/set-test-env.sh' after creating .env.test, "
                + "or set PALORM_PG_* variables for appsettings.test.json template expansion.");
        var options = new DbOptions { ConnectionString = connectionString };
        DataSession<PostgreSqlProvider> db = await DataSession<PostgreSqlProvider>.CreateAsync(options).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_bulk_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_json_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_migrated").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_pg_test (\"Id\" BIGSERIAL PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL, version BIGINT NOT NULL)").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_pg_json_test (\"Id\" BIGSERIAL PRIMARY KEY, details TEXT NOT NULL)").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_pg_bulk_test (\"Id\" TEXT PRIMARY KEY, name TEXT NOT NULL, created_by TEXT NOT NULL DEFAULT 'database', deleted_at TIMESTAMPTZ)").ConfigureAwait(false);

            AotPgEntity inserted = await db.InsertAsync(new AotPgEntity
            {                Name = "AOT PG works!",
                Value = 42,
                Version = 0
            }).ConfigureAwait(false);
            if (inserted.Id <= 0)
                throw new InvalidOperationException("PostgreSQL INSERT failed");
            if (await db.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM aot_pg_test WHERE \"Id\" = {inserted.Id:N0}")
                    .ConfigureAwait(false) != 1)
                throw new InvalidOperationException("PostgreSQL composite format parameterization failed");

            AotPgEntity first = await db.GetAsync<AotPgEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL GET failed");
            AotPgEntity stale = await db.GetAsync<AotPgEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL stale GET failed");

            first.Name = "AOT PG updated";
            if (await db.UpdateAsync(first).ConfigureAwait(false) != 1 || first.Version != 1)
                throw new InvalidOperationException("PostgreSQL UPDATE failed");

            stale.Name = "stale";
            try
            {
                await db.UpdateAsync(stale).ConfigureAwait(false);
                throw new InvalidOperationException("PostgreSQL concurrency conflict was not detected");
            }
            catch (ConcurrencyConflictException)
            {
                // S108: 测试期望此异常——stale row 必须被并发控制拒绝。
            }

            AotPgJsonEntity json = await db.InsertAsync(new AotPgJsonEntity
            {
                Details = new AotPgDetails { Name = "source-generated", Count = 7 }
            }).ConfigureAwait(false);
            AotPgJsonEntity roundTrip = await db.GetAsync<AotPgJsonEntity>(json.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL OwnedJson GET failed");
            if (roundTrip.Details.Name != "source-generated" || roundTrip.Details.Count != 7)
                throw new InvalidOperationException("PostgreSQL OwnedJson round trip failed");

            var bulkEntities = new[]
            {
                new AotPgBulkEntity { Id = "first", Name = "first" },
                new AotPgBulkEntity { Id = "second", Name = "second" }
            };
            if (await db.BulkInsertAsync(bulkEntities).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("PostgreSQL bulk INSERT failed");
            AotPgBulkEntity? bulk = await db.GetAsync<AotPgBulkEntity>("first").ConfigureAwait(false);
            if (bulk?.CreatedBy != "database")
                throw new InvalidOperationException("PostgreSQL bulk INSERT metadata failed");
            if (await db.BulkDeleteAsync<AotPgBulkEntity>(["first", "second"]).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("PostgreSQL bulk soft DELETE failed");
            AotPgBulkEntity? softDeleted = await db.IgnoreFilters()
                .GetAsync<AotPgBulkEntity>("first").ConfigureAwait(false);
            if (softDeleted?.DeletedAt is null)
                throw new InvalidOperationException("PostgreSQL bulk soft DELETE metadata failed");

            if (await db.DeleteAsync<AotPgEntity>(inserted.Id).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("PostgreSQL DELETE failed");
            await db.DeleteAsync<AotPgJsonEntity>(json.Id).ConfigureAwait(false);

            // ITM-419：源生成 PG DDL（表 + 索引）经 MigrateAsync 真库执行 + CRUD 往返
            await db.MigrateAsync().ConfigureAwait(false);
            await db.MigrateAsync().ConfigureAwait(false); // 幂等：IF NOT EXISTS 第二次不抛
            AotPgMigratedEntity migrated = await db.InsertAsync(new AotPgMigratedEntity
            {
                Label = "migrated",
                Amount = 12.5m,
                CreatedAt = DateTimeOffset.UtcNow
            }).ConfigureAwait(false);
            AotPgMigratedEntity migratedBack = await db.GetAsync<AotPgMigratedEntity>(migrated.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL migrated-table GET failed");
            if (migratedBack.Label != "migrated" || migratedBack.Amount != 12.5m)
                throw new InvalidOperationException("PostgreSQL migrated-table round trip failed");
        }

        Console.WriteLine("PalORM AOT PG verification PASSED");
    }
}
