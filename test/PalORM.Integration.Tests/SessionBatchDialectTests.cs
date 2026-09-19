using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>SessionBatch 的真 DbBatch 路径契约（PG/MySQL）——与 Core.Tests 的 SQLite 回退
/// 路径互补：同四条断言在真批量驱动上锁定（PG 单往返、MySQL 驱动侧批处理）。</summary>
internal sealed class SessionBatchDialectTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task PostgreSql_Batch_ExecutesAll_RollbackOnFailure()
    {
        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS batch_dialect_rows");
        await session.ExecuteAsync(
            $"CREATE TABLE batch_dialect_rows (id BIGINT PRIMARY KEY, v INTEGER NOT NULL, tag TEXT NOT NULL)");

        // 事务内 10 条一次往返（真 DbBatch）——受影响行数与值都要对
        await session.WithTransaction(async ct =>
        {
            using SessionBatch<PostgreSqlProvider> batch = session.CreateBatch();
            for (int i = 1; i <= 10; i++)
                _ = batch.Append($"INSERT INTO batch_dialect_rows (id, v, tag) VALUES ({(long)i}, {i * 10}, {"t"})");
            int affected = await batch.ExecuteNonQueryAsync(ct);
            await Assert.That(affected).IsEqualTo(10);
            return true;
        });
        await Assert.That(await session.CountAsync<BatchDialectRow>()).IsEqualTo(10);

        // 失败整批回滚（事务原子性）
        await Assert.That(async () => await session.WithTransaction(async ct =>
        {
            using SessionBatch<PostgreSqlProvider> batch = session.CreateBatch();
            await batch
                .Append($"DELETE FROM batch_dialect_rows")
                .Append($"INSERT INTO nonexistent_pg_table (x) VALUES ({1})")
                .ExecuteNonQueryAsync(ct);
            return true;
        })).Throws<Exception>();
        await Assert.That(await session.CountAsync<BatchDialectRow>()).IsEqualTo(10); // DELETE 被回滚
        await session.ExecuteAsync($"DROP TABLE IF EXISTS batch_dialect_rows");
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task MySql_Batch_ExecutesAll()
    {
        await using var session = await DataSession<MySqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
        });
        await session.ExecuteAsync($"DROP TABLE IF EXISTS batch_dialect_rows");
        await session.ExecuteAsync(
            $"CREATE TABLE batch_dialect_rows (id BIGINT PRIMARY KEY, v INTEGER NOT NULL, tag TEXT NOT NULL)");

        using SessionBatch<MySqlProvider> batch = session.CreateBatch();
        int affected = await batch
            .Append($"INSERT INTO batch_dialect_rows (id, v, tag) VALUES ({1L}, {10}, {"a"})")
            .Append($"INSERT INTO batch_dialect_rows (id, v, tag) VALUES ({2L}, {20}, {"b"})")
            .Append($"UPDATE batch_dialect_rows SET v = {11} WHERE id = {1L}")
            .ExecuteNonQueryAsync();

        await Assert.That(affected).IsEqualTo(3);
        await Assert.That(await session.CountAsync<BatchDialectRow>()).IsEqualTo(2);
        var first = await session.GetAsync<BatchDialectRow>(1L);
        await Assert.That(first!.V).IsEqualTo(11);
        await session.ExecuteAsync($"DROP TABLE IF EXISTS batch_dialect_rows");
    }
}

[Table("batch_dialect_rows")]
internal sealed partial class BatchDialectRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("v")] public int V { get; set; }
    [Column("tag")] public string Tag { get; set; } = "";
}
