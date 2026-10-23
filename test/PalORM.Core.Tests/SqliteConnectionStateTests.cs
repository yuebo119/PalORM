using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>SQLite 连接态脏标记与池化复位：原生 SQL 关掉 foreign_keys 后，
/// 下一次会话/下一次池租用必须恢复；干净句柄路径零额外 PRAGMA（由初始化探针另行量化）。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "连接生命周期移交 DataSession/池；临时文件在 finally 清理。")]
public sealed class SqliteConnectionStateTests
{
    [Test]
    public async Task RawSql_DisablingForeignKeys_IsRestoredByNextSession()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"palorm_state_{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={dbPath}";
        try
        {
            var options = new DbOptions { ConnectionString = connectionString };

            await using (var first = await DataSession<SqliteProvider>.CreateAsync(options))
            {
                await first.ExecuteAsync($"CREATE TABLE state_probe (id INTEGER PRIMARY KEY)");
                await first.ExecuteAsync($"PRAGMA foreign_keys = OFF");
            }

            await using var second = await DataSession<SqliteProvider>.CreateAsync(options);
            long? foreignKeys = await second.ScalarAsync<long>($"PRAGMA foreign_keys");
            await Assert.That(foreignKeys).IsEqualTo(1L);
        }
        finally
        {
            // 只清本用例连接串的池——ClearAllPools 会打断并行用例的句柄复用（实测污染
            // SqlitePooledInitializationTests 的 cache_size 断言）。
            using (var pooled = new SqliteConnection(connectionString))
                SqliteConnection.ClearPool(pooled);
            foreach (string path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }
                .Where(File.Exists))
            {
                File.Delete(path);
            }
        }
    }

    [Test]
    public async Task PooledRent_RestoresForeignKeysAfterRawSql()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"palorm_state_pool_{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={dbPath}";
        try
        {
            var options = new DbOptions { ConnectionString = connectionString };
            await using var pool = DataSessionPool<SqliteProvider>.Create(options, maxIdleSize: 1);

            DataSession<SqliteProvider> first = await pool.RentAsync();
            await first.ExecuteAsync($"CREATE TABLE pool_state_probe (id INTEGER PRIMARY KEY)");
            await first.ExecuteAsync($"PRAGMA foreign_keys = OFF");
            await first.DisposeAsync();

            DataSession<SqliteProvider> second = await pool.RentAsync();
            long? foreignKeys = await second.ScalarAsync<long>($"PRAGMA foreign_keys");
            await second.DisposeAsync();
            await Assert.That(foreignKeys).IsEqualTo(1L);
        }
        finally
        {
            using (var pooled = new SqliteConnection(connectionString))
                SqliteConnection.ClearPool(pooled);
            foreach (string path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }
                .Where(File.Exists))
            {
                File.Delete(path);
            }
        }
    }
}
