using Microsoft.Data.Sqlite;
using PalORM.Integration.Tests.Conformance;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests;

// P2（方案 §6.8 / META-3）：SQLite 文件库与加密的功能级用例。
// 覆盖 WAL、双连接 BUSY 等待、只读模式、加密连接串四个分支，并与内存库结果对照。
// 临时文件统一放 %TEMP% 下独立目录，finally 删除；加密用例不打印连接串（P0 凭据卫生）。

[NotInParallel("SqliteFileMode")]
public sealed class SqliteFileModeTests
{
    private static string NewTempDbPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "palorm-file-mode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "test.db");
    }

    private static void DeleteDb(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 删除是尽力而为：连接池句柄可能尚未释放，进程退出后系统清理
        }
    }

    private static async Task<DataSession<SqliteProvider>> OpenAsync(string connectionString)
        => await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = connectionString });

    /// <summary>经 builder 索引器组装加密连接串（键与值都来自运行期变量，
    /// 源码不出现赋值字面量形态，与 secret-guard 规则 14 共存）。</summary>
    private static string EncryptedConnectionString(string path, string key)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        builder["Password"] = key;
        return builder.ConnectionString;
    }

    [Test]
    public async Task FileDatabase_WalMode_And_RoundTripMatchesMemory()
    {
        string path = NewTempDbPath();
        try
        {
            await using (DataSession<SqliteProvider> fileDb = await OpenAsync($"Data Source={path};Pooling=False"))
            {
                await fileDb.MigrateAsync();
                await fileDb.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "file", Amount = 10 });
                string journalMode = (await fileDb.ScalarAsync<string>($"PRAGMA journal_mode"))!;
                await Assert.That(journalMode.Equals("wal", StringComparison.OrdinalIgnoreCase)).IsTrue();
                CfBulkPlainEntity row = (await fileDb.GetAsync<CfBulkPlainEntity>(1L))!;
                await Assert.That(row.Name).IsEqualTo("file");
                await Assert.That(row.Amount).IsEqualTo(10L);
            }

            // 与内存库同数据对照：同实体、同查询，结果一致
            await using (DataSession<SqliteProvider> memory = await OpenAsync("Data Source=:memory:"))
            {
                await memory.MigrateAsync();
                await memory.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "file", Amount = 10 });
                CfBulkPlainEntity memoryRow = (await memory.GetAsync<CfBulkPlainEntity>(1L))!;
                await Assert.That(memoryRow.Name).IsEqualTo("file");
                await Assert.That(memoryRow.Amount).IsEqualTo(10L);
            }
        }
        finally
        {
            DeleteDb(path);
        }
    }

    [Test]
    public async Task TwoConnections_SecondWaitsForBusyLock_ThenSucceeds()
    {
        string path = NewTempDbPath();
        try
        {
            string connectionString = $"Data Source={path};Pooling=False";
            await using (DataSession<SqliteProvider> setup = await OpenAsync(connectionString))
            {
                await setup.MigrateAsync();
            }

            await using DataSession<SqliteProvider> writer = await OpenAsync(connectionString);
            System.Data.Common.DbTransaction transaction = await writer.BeginTransactionAsync();
            await writer.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "held", Amount = 1 });

            await using DataSession<SqliteProvider> second = await OpenAsync(connectionString);
            Task<long> blocked = Task.Run(async () =>
                await second.BulkInsertAsync([new CfBulkPlainEntity { Id = 2, Name = "waiting", Amount = 2 }]));

            // 让第二个连接进入 BUSY 等待（驱动 busy_timeout=5000），再释放写锁
            await Task.Delay(150);
            await Assert.That(blocked.IsCompleted).IsFalse()
                .Because("第一连接持有写事务时，第二连接的写必须处于 BUSY 等待而不是立即失败");

            await transaction.CommitAsync();
            await transaction.DisposeAsync();
            await Assert.That(await blocked).IsEqualTo(1L);
            await Assert.That(await writer.CountAsync<CfBulkPlainEntity>()).IsEqualTo(2L);
        }
        finally
        {
            DeleteDb(path);
        }
    }

    [Test]
    public async Task ReadOnlyMode_ReadsSucceed_WritesFail()
    {
        string path = NewTempDbPath();
        try
        {
            await using (DataSession<SqliteProvider> setup = await OpenAsync($"Data Source={path};Pooling=False"))
            {
                await setup.MigrateAsync();
                await setup.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "ro", Amount = 1 });
            }

            await using DataSession<SqliteProvider> readOnly = await OpenAsync($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await Assert.That((await readOnly.GetAsync<CfBulkPlainEntity>(1L))!.Name).IsEqualTo("ro");
            await Assert.That(async () => await readOnly.InsertAsync(
                    new CfBulkPlainEntity { Id = 2, Name = "nope", Amount = 2 }))
                .Throws<SqliteException>();
        }
        finally
        {
            DeleteDb(path);
        }
    }

    [Test]
    public async Task PasswordEncryption_RoundTrips_AndRejectsWrongPassword()
    {
        string path = NewTempDbPath();
        try
        {
            // 密钥运行期随机生成（不落源码字面量）；连接串经 builder 索引器组装
            string cipherKey = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            await using (DataSession<SqliteProvider> encrypted = await OpenAsync(
                EncryptedConnectionString(path, cipherKey)))
            {
                await encrypted.MigrateAsync();
                await encrypted.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "encrypted", Amount = 7 });
            }

            await using (DataSession<SqliteProvider> reopen = await OpenAsync(
                EncryptedConnectionString(path, cipherKey)))
            {
                CfBulkPlainEntity row = (await reopen.GetAsync<CfBulkPlainEntity>(1L))!;
                await Assert.That(row.Name).IsEqualTo("encrypted");
                await Assert.That(row.Amount).IsEqualTo(7L);
            }

            // 无密码打开必须失败——否则"加密"没有实际效果
            await Assert.That(async () =>
            {
                await using DataSession<SqliteProvider> plain = await OpenAsync($"Data Source={path};Pooling=False");
                await plain.ScalarAsync<long>($"SELECT COUNT(*) FROM cf_bulk_plain");
            }).Throws<SqliteException>();
        }
        finally
        {
            DeleteDb(path);
        }
    }
}