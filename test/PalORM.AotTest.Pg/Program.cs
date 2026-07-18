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
    [Key] public string Id { get; set; } = "";
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
            ?? throw new InvalidOperationException("PALORM_PG_CONNECTION is required");
        var options = new DbOptions { ConnectionString = connectionString };
        DataSession<PostgreSqlProvider> db = await DataSession<PostgreSqlProvider>.CreateAsync(options).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_bulk_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_json_test").ConfigureAwait(false);
            await db.ExecuteAsync($"DROP TABLE IF EXISTS aot_pg_test").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_pg_test (\"Id\" BIGSERIAL PRIMARY KEY, name VARCHAR(100) NOT NULL, value INT NOT NULL, version BIGINT NOT NULL)").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_pg_json_test (\"Id\" BIGSERIAL PRIMARY KEY, details TEXT NOT NULL)").ConfigureAwait(false);
            await db.ExecuteAsync($"CREATE TABLE aot_pg_bulk_test (\"Id\" TEXT PRIMARY KEY, name TEXT NOT NULL, created_by TEXT NOT NULL DEFAULT 'database', deleted_at TIMESTAMPTZ)").ConfigureAwait(false);

            AotPgEntity inserted = await db.InsertAsync(new AotPgEntity
            {
                Name = "AOT PG works!",
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
        }

        Console.WriteLine("PalORM AOT PG verification PASSED");
    }
}
