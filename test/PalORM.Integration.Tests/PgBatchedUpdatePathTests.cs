using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>PG-7（2026-09-27）：MySQL-8 DbBatch 打包平移到 PG 的真库覆盖。
/// <para><b>背景</b>：乐观锁（[ConcurrencyCheck]）实体的 BulkUpdateAsync 走逐条路径
/// （N 行 = N 次协议往返）。MySQL-8 起 MySQL dialect 改用 DbBatch 打包（100 命令/批
/// 一次往返，探针十六 3.55×）；本轮按同一机制移植到 PG——探针二十八实测 PG 远程库
/// 同族对照 = 7.85×（200 命令逐条 118.7ms vs DbBatch 15.1ms），远程 RTT 越高省得越多。
/// SQLite 不移植（探针二十五：进程内无 RTT，DbBatch vs 逐条 = 1.02×，噪声）。</para>
/// <para><b>本组锁三件事</b>：① 批路径跨批边界（250 行 = 3 批）写值全部正确；
/// ② 批内 version 不匹配 → ConcurrencyConflictException（总和 &lt; 批大小）；
/// ③ ITM-556 延迟回填语义（仅批成功后内存 version +1）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class PgBatchedUpdatePathTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BatchedPooledUpdate_CrossesBatchBoundaries_WritesCorrectValues()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched");
        await db.ExecuteAsync(
            $"CREATE TABLE palorm_pg_batched (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");

        const int rows = 250;
        var seed = new List<PgBatchedRow>(rows);
        for (int i = 1; i <= rows; i++)
            seed.Add(new PgBatchedRow { Id = i, Name = "s" + i });
        await db.BulkInsertAsync(seed);

        List<PgBatchedRow> updated = [.. seed.Select(e => new PgBatchedRow { Id = e.Id, Name = "u" + e.Id })];
        long affected = await db.BulkUpdateAsync(updated);

        await Assert.That(affected).IsEqualTo(rows);
        List<PgBatchedRow> after = await db.From<PgBatchedRow>().ToListAsync();
        await Assert.That(after.Count).IsEqualTo(rows);
        foreach (PgBatchedRow row in after)
        {
            await Assert.That(row.Name).IsEqualTo("u" + row.Id);
            await Assert.That(row.Version).IsEqualTo(1);
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BatchedPooledUpdate_StaleVersionInsideBatch_ThrowsConcurrencyConflict()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched_c");
        await db.ExecuteAsync(
            $"CREATE TABLE palorm_pg_batched_c (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");

        const int rows = 150;
        var seed = new List<PgBatchedRowC>(rows);
        for (int i = 1; i <= rows; i++)
            seed.Add(new PgBatchedRowC { Id = i, Name = "s" + i });
        await db.BulkInsertAsync(seed);

        List<PgBatchedRowC> updated = [.. seed.Select(e => new PgBatchedRowC { Id = e.Id, Name = "u" + e.Id })];

        // 第 150 行（第二/末批内）version 陈旧——批总和 < 批大小必须抛冲突
        updated[^1].Version = 99;
        await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
            await db.BulkUpdateAsync(updated));

        // 整批回滚：DB 内应全部保持种子原状
        List<PgBatchedRowC> after = await db.From<PgBatchedRowC>().ToListAsync();
        await Assert.That(after.Count).IsEqualTo(rows);
        foreach (PgBatchedRowC row in after)
        {
            await Assert.That(row.Name).IsEqualTo("s" + row.Id);
            await Assert.That(row.Version).IsEqualTo(0);
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BatchedPooledUpdate_InMemoryVersionIncrementOnlyOnSuccess()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched_v");
        await db.ExecuteAsync(
            $"CREATE TABLE palorm_pg_batched_v (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");

        var entity = new PgBatchedRowV { Id = 1, Name = "a" };
        await db.BulkInsertAsync([entity]);

        entity.Name = "b";
        long affected = await db.BulkUpdateAsync([entity]);
        await Assert.That(affected).IsEqualTo(1L);
        await Assert.That(entity.Version).IsEqualTo(1);
    }
}

[Table("palorm_pg_batched")]
internal sealed partial class PgBatchedRow
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}

[Table("palorm_pg_batched_c")]
internal sealed partial class PgBatchedRowC
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}

[Table("palorm_pg_batched_v")]
internal sealed partial class PgBatchedRowV
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; }
}
