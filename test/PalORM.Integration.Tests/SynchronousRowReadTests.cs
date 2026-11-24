using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

[Table("palorm_syncrow_probe")]
internal sealed partial class SyncReadProbeRow
{
    [Key(AutoIncrement = false)][Column("id")] public long id { get; set; }
    [Column("payload")] public string payload { get; set; } = "";
}

/// <summary>UseSynchronousRowRead 的 MySQL 真库覆盖：显式开启后 GetAll/链式/原始 SQL 三条
/// 全量物化路径仍返回正确结果（同步逐行读叶子只在 Provider 声明能力且开关开启时生效）。</summary>
[NotInParallel("ExtBulkTable")]
internal sealed class SynchronousRowReadTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task MySql_SynchronousRowRead_ReturnsSameRows()
    {
        await using var session = await DataSession<MySqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString(),
            UseSynchronousRowRead = true
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_syncrow_probe");
        await session.ExecuteAsync(
            $"CREATE TABLE palorm_syncrow_probe (id BIGINT PRIMARY KEY, payload VARCHAR(64) NOT NULL)");
        try
        {
            await session.ExecuteAsync(
                $"INSERT INTO palorm_syncrow_probe (id, payload) VALUES (1, 'a'), (2, 'b'), (3, 'c')");

            List<SyncReadProbeRow> all = await session.GetAllAsync<SyncReadProbeRow>();
            await Assert.That(all.Count).IsEqualTo(3);

            List<SyncReadProbeRow> filtered =
                await session.From<SyncReadProbeRow>().Where($"`id` >= {2L}").ToListAsync();
            await Assert.That(filtered.Select(static row => row.id).ToArray()).IsEquivalentTo([2L, 3L]);

            List<SyncReadProbeRow> raw = await session.QueryAsync<SyncReadProbeRow>(
                $"SELECT id, payload FROM palorm_syncrow_probe ORDER BY id");
            await Assert.That(raw.Select(static row => row.payload).ToArray()).IsEquivalentTo(["a", "b", "c"]);
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_syncrow_probe");
        }
    }

    /// <summary>DIG-E6（2026-11-24）：GridReader.ReadFirstAsync 的同步首行读分流。
    /// 显式开启 UseSynchronousRowRead 后走同步 Read 叶子，结果须与异步分流一致。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task MySql_SynchronousRowRead_GridReaderFirst_ReturnsFirstRow()
    {
        await using var session = await DataSession<MySqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString(),
            UseSynchronousRowRead = true
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_syncrow_probe");
        await session.ExecuteAsync(
            $"CREATE TABLE palorm_syncrow_probe (id BIGINT PRIMARY KEY, payload VARCHAR(64) NOT NULL)");
        try
        {
            await session.ExecuteAsync(
                $"INSERT INTO palorm_syncrow_probe (id, payload) VALUES (1, 'a'), (2, 'b')");

            await using GridReader reader = await session.From<SyncReadProbeRow>().QueryMultipleAsync(
                $"SELECT id, payload FROM palorm_syncrow_probe ORDER BY id; SELECT 1");
            SyncReadProbeRow? first = await reader.ReadFirstAsync<SyncReadProbeRow>();
            await Assert.That(first).IsNotNull();
            await Assert.That(first!.id).IsEqualTo(1);
            await Assert.That(first.payload).IsEqualTo("a");
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE IF EXISTS palorm_syncrow_probe");
        }
    }
}
