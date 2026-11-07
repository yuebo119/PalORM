using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L4 SQLite 文件库 WAL 争用（修订版方案 §5 CS-23）。
/// <para><b>并发边界</b>：用 4 条独立连接（<c>Pooling=False</c>）而非 4 个进程。SQLite 的
/// 写锁与 WAL 索引是<b>文件级</b>资源，同进程多连接与多进程在锁语义上等价；真正的多进程编排
/// 会引入进程启动/退出噪声，对判据没有增量。进程边界不参与「谁先拿到写锁」的判定。</para>
/// <para><b>判据</b>：4 写者各批量写 50 行后总行数恰为 200；BUSY/LOCKED 计数有界（每 worker
/// 至多一次，超出即说明 busy_timeout 没生效）；<c>PRAGMA integrity_check == ok</c>（无损坏）；
/// <c>journal_mode == wal</c>（确实跑在 WAL 上，而不是回退到 delete 模式）。</para></summary>
[NotInParallel("SqliteFileWal")]
public sealed class ConcurrencyFileWalTests
{
    private const int Writers = 4;
    private const int RowsPerWriter = 50;

    private static string NewTempDbPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "palorm-cs23", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "wal.db");
    }

    private static void DeleteDb(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 删除尽力而为：连接池句柄可能尚未释放，进程退出后系统清理
        }
    }

    [Test]
    public async Task CS23_FileDatabase_ConcurrentBulkWrites_IntegrityCheckIsOk()
    {
        string path = NewTempDbPath();
        try
        {
            string connectionString = $"Data Source={path};Pooling=False;Default Timeout=10";
            var options = new DbOptions { ConnectionString = connectionString };
            await using (DataSession<SqliteProvider> setup =
                await DataSession<SqliteProvider>.CreateAsync(options))
            {
                await setup.MigrateAsync();
                await setup.ExecuteAsync($"DELETE FROM cf_bulk_plain");
            }

            var busy = new ConcurrentBag<string>();
            var errors = new ConcurrentBag<string>();
            await using ConcurrencyHarness harness = ConcurrencyHarness.Create(TimeSpan.FromMinutes(2));

            await harness.RunAsync(Writers, async (worker, ct) =>
            {
                await using DataSession<SqliteProvider> session =
                    await DataSession<SqliteProvider>.CreateAsync(options, ct).ConfigureAwait(false);
                var batch = new List<CfBulkPlainEntity>(RowsPerWriter);
                for (int i = 0; i < RowsPerWriter; i++)
                {
                    batch.Add(new CfBulkPlainEntity
                    {
                        Id = worker * RowsPerWriter + i + 1,
                        Name = $"w{worker}-{i}",
                        Amount = i,
                    });
                }
                try
                {
                    await session.BulkInsertAsync(batch, ct: ct).ConfigureAwait(false);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
                {
                    // 有界：每个 worker 至多一次 BUSY/LOCKED；更多说明 busy_timeout 未生效
                    busy.Add($"worker={worker}: code={exception.SqliteErrorCode}");
                }
                catch (Exception exception)
                {
                    errors.Add($"worker={worker}: {exception.GetType().Name}: {exception.Message}");
                }
            }).ConfigureAwait(false);

            await Assert.That(string.Join(" | ", errors.Take(3))).IsEmpty();
            await Assert.That(busy.Count).IsLessThanOrEqualTo(Writers);

            await using DataSession<SqliteProvider> verify =
                await DataSession<SqliteProvider>.CreateAsync(options);
            long count = await verify.CountAsync<CfBulkPlainEntity>();
            await Assert.That(count).IsEqualTo((long)Writers * RowsPerWriter);

            // 逐值核对：每行 Name 与自己所属 writer 的分区一致（无串行）
            List<CfBulkPlainEntity> rows = await verify.From<CfBulkPlainEntity>().ToListAsync();
            List<CfBulkPlainEntity> mismatches = rows
                .Where(row => row.Name != $"w{(row.Id - 1) / RowsPerWriter}-{(row.Id - 1) % RowsPerWriter}")
                .ToList();
            await Assert.That(mismatches.Count).IsEqualTo(0);

            string journalMode = (await verify.ScalarAsync<string>($"PRAGMA journal_mode"))!;
            await Assert.That(journalMode.Equals("wal", StringComparison.OrdinalIgnoreCase)).IsTrue();

            string integrity = (await verify.ScalarAsync<string>($"PRAGMA integrity_check"))!;
            await Assert.That(integrity).IsEqualTo("ok");
        }
        finally
        {
            DeleteDb(path);
        }
    }
}
