using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>PG 版本化批量更新的真库覆盖（v6.5 单语句 UNNEST 路由）。
/// <para><b>背景</b>：版本化实体的 BulkUpdateAsync 在生成物提供并发令牌数组能力时路由到
/// <c>UPDATE ... FROM UNNEST(...) WHERE pk = ... AND version = ...</c> 单语句（S6 探针
/// 1000 行 3.06×）；能力不全或 SQLite 时回退逐条 DbBatch。冲突检测为计数级，异常类型
/// 与逐条路径一致。</para>
/// <para><b>本组锁</b>：① 跨批边界（250 行）写值全部正确；② 批内 version 不匹配 →
/// ConcurrencyConflictException 且整批回滚；③ ITM-556 延迟回填（成功后才 +1）；
/// ④ 重复主键与缺失行按冲突处理；⑤ 单行输入仍走逐条路径且语义不变。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class PgBatchedUpdatePathTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BatchedPooledUpdate_CrossesBatchBoundaries_WritesCorrectValues()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
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
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BatchedPooledUpdate_StaleVersionInsideBatch_ThrowsConcurrencyConflict()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
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
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched_c");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BatchedPooledUpdate_InMemoryVersionIncrementOnlyOnSuccess()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
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
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched_v");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_VersionedBulkUpdate_DuplicateKeys_ThrowConflictAndRollback()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched_dup");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_pg_batched_dup (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");
            await db.BulkInsertAsync([new PgBatchedRowDup { Id = 1, Name = "seed" }]);

            List<PgBatchedRowDup> updated =
            [
                new() { Id = 1, Name = "a" },
                new() { Id = 1, Name = "b" }
            ];
            // 目标行只更新一次（PG 官方文档），受影响 1 < 输入 2 → 按冲突抛出并回滚
            await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
                await db.BulkUpdateAsync(updated));

            PgBatchedRowDup? row = await db.GetAsync<PgBatchedRowDup>(1);
            await Assert.That(row).IsNotNull();
            await Assert.That(row!.Name).IsEqualTo("seed");
            await Assert.That(row.Version).IsEqualTo(0);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched_dup");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_VersionedBulkUpdate_MissingRow_ThrowConflictAndRollback()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched_miss");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_pg_batched_miss (id INT PRIMARY KEY, name VARCHAR(32) NOT NULL, version INT NOT NULL)");

            var seed = new List<PgBatchedRowMiss>
            {
                new() { Id = 1, Name = "s1" },
                new() { Id = 2, Name = "s2" },
                new() { Id = 3, Name = "s3" }
            };
            await db.BulkInsertAsync(seed);

            List<PgBatchedRowMiss> updated =
            [
                new() { Id = 1, Name = "u1" },
                new() { Id = 2, Name = "u2" },
                new() { Id = 3, Name = "u3" },
                new() { Id = 99, Name = "ghost" }
            ];
            await Assert.ThrowsAsync<ConcurrencyConflictException>(async () =>
                await db.BulkUpdateAsync(updated));

            List<PgBatchedRowMiss> after = await db.From<PgBatchedRowMiss>().ToListAsync();
            await Assert.That(after.Count).IsEqualTo(3);
            foreach (PgBatchedRowMiss row in after)
            {
                await Assert.That(row.Name).IsEqualTo("s" + row.Id);
                await Assert.That(row.Version).IsEqualTo(0);
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_pg_batched_miss");
        }
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

[Table("palorm_pg_batched_dup")]
internal sealed partial class PgBatchedRowDup
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

[Table("palorm_pg_batched_miss")]
internal sealed partial class PgBatchedRowMiss
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
