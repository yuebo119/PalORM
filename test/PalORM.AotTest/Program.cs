using System.Text.Json.Serialization;
using PalORM.AotModels.First;
using PalORM.AotModels.Second;
using PalORM.Sqlite;

namespace PalORM.AotTest;

[Table("aot_test")]
internal sealed partial class AotEntity
{
    [Key] public long Id { get; set; }
    [Column("value")] public string Value { get; set; } = "";
    [Column("version")][ConcurrencyCheck] public long Version { get; set; }
    // CA1819 误报：ORM 实体列需要可变数组读写
#pragma warning disable CA1819
    [Column("blob")] public byte[] Blob { get; set; } = [];
#pragma warning restore CA1819
    [Column("preview")] public byte[]? Preview { get; set; }
}

[SoftDelete]
[Table("aot_bulk_test")]
internal sealed partial class AotBulkEntity
{
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
    [Column("created_by")][IgnoreOnInsert] public string CreatedBy { get; set; } = "client";
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}

[Table("aot_json_test")]
internal sealed partial class AotJsonEntity
{
    [Key] public long Id { get; set; }
    [Column("details")][OwnedJson(typeof(AotJsonContext))] public AotDetails Details { get; set; } = new();
}

internal sealed class AotDetails
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

[JsonSerializable(typeof(AotDetails), TypeInfoPropertyName = "AotDetailsInfo")]
internal sealed partial class AotJsonContext : JsonSerializerContext;

internal static class Program
{
    internal static async Task Main()
    {
        // 文件型数据库而非 :memory:（ITM-317）——WAL journal_mode 仅对文件库生效，
        // native sqlite3mc 加载 + PRAGMA 初始化钩子在 AOT 下的完整路径只有文件库能验证
        string dbPath = Path.Combine(Path.GetTempPath(), $"palorm-aot-{Environment.ProcessId}.db");
        var options = new DbOptions { ConnectionString = $"Data Source={dbPath}" };
        try
        {
            await RunAsync(options).ConfigureAwait(false);
        }
        finally
        {
            // Windows 下连接池持有文件句柄，File.Delete 抛 IOException（Linux 可 unlink
            // 开启句柄的文件故 CI 无此问题）——清池后再删，保证本地与 CI 行为一致
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            File.Delete(dbPath + "-wal");
            File.Delete(dbPath + "-shm");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303",
        Justification = "固定英文文本是 Native AOT smoke test 的机器可读成功标记。")]
    private static async Task RunAsync(DbOptions options)
    {
        DataSession<SqliteProvider> db = await DataSession<SqliteProvider>.CreateAsync(options).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.MigrateAsync().ConfigureAwait(false);
            await db.ExecuteAsync(
                $"DROP TABLE aot_bulk_test").ConfigureAwait(false);
            await db.ExecuteAsync(
                $"CREATE TABLE aot_bulk_test (Id TEXT PRIMARY KEY, name TEXT NOT NULL, created_by TEXT NOT NULL DEFAULT 'database', deleted_at TEXT)")
                .ConfigureAwait(false);

            AotEntity inserted = await db.InsertAsync(new AotEntity
            {
                Value = "AOT works!",
                Version = 0,
                Blob = [0x00, 0x01, 0xFF, 0x00],
                Preview = null
            }).ConfigureAwait(false);
            if (inserted.Id <= 0)
                throw new InvalidOperationException("INSERT failed");
            if (await db.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM aot_test WHERE id = {inserted.Id:N0}")
                    .ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Composite format parameterization failed");

            AotEntity first = await db.GetAsync<AotEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("GET failed");
            await VerifyBinaryColumnAsync(db, inserted, first).ConfigureAwait(false);
            AotEntity stale = await db.GetAsync<AotEntity>(inserted.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Stale GET failed");

            first.Value = "AOT updated";
            int updated = await db.UpdateAsync(first).ConfigureAwait(false);
            if (updated != 1 || first.Version != 1)
                throw new InvalidOperationException("UPDATE failed");

            stale.Value = "stale";
            try
            {
                await db.UpdateAsync(stale).ConfigureAwait(false);
                throw new InvalidOperationException("Concurrency conflict was not detected");
            }
            catch (ConcurrencyConflictException)
            {
                // S108: 测试期望此异常——stale row 必须被并发控制拒绝。空 catch 即断言成功。
            }

            AotJsonEntity json = await db.InsertAsync(new AotJsonEntity
            {
                Details = new AotDetails { Name = "source-generated", Count = 7 }
            }).ConfigureAwait(false);
            AotJsonEntity jsonRoundTrip = await db.GetAsync<AotJsonEntity>(json.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OwnedJson GET failed");
            if (jsonRoundTrip.Details.Name != "source-generated" || jsonRoundTrip.Details.Count != 7)
                throw new InvalidOperationException("OwnedJson round trip failed");

            FirstEntity firstModel = await db.InsertAsync(new FirstEntity { Name = "first assembly" }).ConfigureAwait(false);
            SecondEntity secondModel = await db.InsertAsync(new SecondEntity { Value = 2 }).ConfigureAwait(false);
            if (await db.GetAsync<FirstEntity>(firstModel.Id).ConfigureAwait(false) is null
                || await db.GetAsync<SecondEntity>(secondModel.Id).ConfigureAwait(false) is null)
                throw new InvalidOperationException("Cross-assembly registry merge failed");

            var bulkEntities = new[]
            {
                new AotBulkEntity { Id = "first", Name = "first" },
                new AotBulkEntity { Id = "second", Name = "second" }
            };
            if (await db.BulkInsertAsync(bulkEntities).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("Bulk INSERT failed");
            AotBulkEntity? bulk = await db.GetAsync<AotBulkEntity>("first").ConfigureAwait(false);
            if (bulk?.CreatedBy != "database")
                throw new InvalidOperationException("Bulk INSERT metadata failed");
            if (await db.BulkDeleteAsync<AotBulkEntity>(["first", "second"]).ConfigureAwait(false) != 2)
                throw new InvalidOperationException("Bulk soft DELETE failed");
            AotBulkEntity? softDeleted = await db.IgnoreFilters()
                .GetAsync<AotBulkEntity>("first").ConfigureAwait(false);
            if (softDeleted?.DeletedAt is null)
                throw new InvalidOperationException("Bulk soft DELETE metadata failed");

            int deleted = await db.DeleteAsync<AotEntity>(inserted.Id).ConfigureAwait(false);
            if (deleted != 1 || await db.GetAsync<AotEntity>(inserted.Id).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("DELETE failed");
        }

        Console.WriteLine("PalORM AOT verification PASSED");
    }

    /// <summary>byte[] 列的 AOT 全链验证：参数化等值过滤（含 0x00 字节）+ 物化往返 + 可空列 NULL 守卫。</summary>
    private static async Task VerifyBinaryColumnAsync(
        DataSession<SqliteProvider> db, AotEntity inserted, AotEntity fetched)
    {
        if (await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM aot_test WHERE blob = {inserted.Blob}")
                .ConfigureAwait(false) != 1)
            throw new InvalidOperationException("byte[] equality parameterization failed");
        if (!fetched.Blob.AsSpan().SequenceEqual((ReadOnlySpan<byte>)[0x00, 0x01, 0xFF, 0x00])
            || fetched.Preview is not null)
            throw new InvalidOperationException("byte[] round trip failed");
    }
}
