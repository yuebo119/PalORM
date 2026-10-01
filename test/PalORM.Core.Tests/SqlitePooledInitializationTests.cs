using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>SQLite 池化连接初始化（2026-10-02）：整组 PRAGMA 每个物理连接（池化原生句柄）只执行一次、
/// <c>foreign_keys</c> 每会话重设、会话 SQL 作废已初始化登记、不池化时每会话完整初始化。
/// 每个用例用独立临时文件库，连接池按连接串隔离，互不串扰。</summary>
public sealed class SqlitePooledInitializationTests
{
    [Test]
    public async Task ReusedPooledHandle_KeepsTuningPragmas_ButReappliesForeignKeys()
    {
        await WithTempDatabaseAsync(async cs =>
        {
            var options = new DbOptions { ConnectionString = cs };
            await using (var first = await DataSession<SqliteProvider>.CreateAsync(options))
            {
                await first.ExecuteAsync($"PRAGMA cache_size = -1234");
                await first.ExecuteAsync($"PRAGMA foreign_keys = OFF");
            }

            await using var second = await DataSession<SqliteProvider>.CreateAsync(options);
            // 调优项保持上一会话的值：复用的是已初始化句柄，整组 PRAGMA 没有重跑
            await Assert.That(await second.ScalarAsync<long>($"PRAGMA cache_size")).IsEqualTo(-1234);
            // 外键是完整性约束，每会话重设（STD-CONC-008），不随句柄带入
            await Assert.That(await second.ScalarAsync<long>($"PRAGMA foreign_keys")).IsEqualTo(1);
        });
    }

    [Test]
    public async Task SessionSetupSql_InvalidatesHandle_NextSessionFullyReinitialized()
    {
        await WithTempDatabaseAsync(async cs =>
        {
            await using (var customized = await DataSession<SqliteProvider>.CreateAsync(
                new DbOptions { ConnectionString = cs, SessionSetupSql = "PRAGMA cache_size = -5000" }))
            {
                await Assert.That(await customized.ScalarAsync<long>($"PRAGMA cache_size")).IsEqualTo(-5000);
            }

            await using var plain = await DataSession<SqliteProvider>.CreateAsync(
                new DbOptions { ConnectionString = cs });
            await Assert.That(await plain.ScalarAsync<long>($"PRAGMA cache_size")).IsEqualTo(-65536);
        });
    }

    [Test]
    public async Task PoolingDisabled_EverySessionFullyInitialized()
    {
        await WithTempDatabaseAsync(async cs =>
        {
            var options = new DbOptions { ConnectionString = cs + ";Pooling=False" };
            await using (var first = await DataSession<SqliteProvider>.CreateAsync(options))
            {
                await first.ExecuteAsync($"PRAGMA cache_size = -1234");
            }

            await using var second = await DataSession<SqliteProvider>.CreateAsync(options);
            await Assert.That(await second.ScalarAsync<long>($"PRAGMA cache_size")).IsEqualTo(-65536);
        });
    }

    private static async Task WithTempDatabaseAsync(Func<string, Task> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"palorm-poolinit-{Guid.NewGuid():N}.db");
        string cs = $"Data Source={path}";
        try
        {
            await body(cs);
        }
        finally
        {
            using (var pooled = new SqliteConnection(cs))
            {
                SqliteConnection.ClearPool(pooled);
            }

            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
            {
                // 尽力清理临时库文件；被占用/缺失时忽略（临时目录，非被测资产）
                try { File.Delete(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _ = exception;
                }
            }
        }
    }
}
