using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>D2 回归：批量插入的自增主键回填路径（方案 §4.6 B11 的补充入口）。
/// <para><c>BulkInsertAsync</c> 走各方言最快的写入形态（PG Binary COPY / MySQL LOAD DATA 或
/// 多值 INSERT / SQLite 行预编译），三者都拿不到生成的主键，因此不回填。需要回填时用
/// <c>BulkInsertReturningAsync</c>：PG 与 SQLite 走多行 INSERT ... RETURNING 并按行序回填，
/// MySQL 因 LAST_INSERT_ID 只保证首值而明确拒绝。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class BulkInsertReturningTests
{
    private static CfSoftDeleteEntity[] NewRows()
        => [new() { Name = "a" }, new() { Name = "b" }, new() { Name = "c" }];

    private static async Task<SharedSqliteDb> SeedSqliteAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        try
        {
            await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
            await db.MigrateAsync(ct);
            await db.ExecuteAsync($"DELETE FROM \"cf_soft_delete\"", ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertBackfilledAsync<TProvider>(
        DataSession<TProvider> db, CfSoftDeleteEntity[] rows, CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        List<CfSoftDeleteEntity> stored = await db.GetAllAsync<CfSoftDeleteEntity>(ct);
        List<long> storedIds = [.. stored.Select(e => e.Id).OrderBy(id => id)];
        List<long> returnedIds = [.. rows.Select(e => e.Id)];

        await Assert.That(stored.Count).IsEqualTo(3);
        await Assert.That(returnedIds.All(id => id > 0L)).IsTrue();
        // 自增键按插入顺序递增；RETURNING 输出行序任意，实现按主键升序重建映射
        await Assert.That(returnedIds.SequenceEqual(returnedIds.OrderBy(id => id))).IsTrue();
        await Assert.That(string.Join(",", returnedIds)).IsEqualTo(string.Join(",", storedIds));
    }

    [Test]
    public async Task Sqlite_BulkInsertReturning_BackfillsGeneratedKeys()
    {
        await using SharedSqliteDb shared = await SeedSqliteAsync("bulk_returning_sqlite");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfSoftDeleteEntity[] rows = NewRows();
        long affected = await db.BulkInsertReturningAsync(rows);

        await Assert.That(affected).IsEqualTo(3L);
        await AssertBackfilledAsync(db, rows);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkInsertReturning_BackfillsGeneratedKeys()
    {
        await using RemoteConformanceFixture<PostgreSqlProvider> fixture = await ConformanceDialects.PostgreSqlAsync();
        await MirrorTable.ExecuteAsync(
            fixture.Raw, SqlDialect.PostgreSql, """DELETE FROM "cf_soft_delete" """);

        CfSoftDeleteEntity[] rows = NewRows();
        long affected = await fixture.Session.BulkInsertReturningAsync(rows);

        await Assert.That(affected).IsEqualTo(3L);
        await AssertBackfilledAsync(fixture.Session, rows);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkInsertReturning_IsRejectedExplicitly()
    {
        await using RemoteConformanceFixture<MySqlProvider> fixture = await ConformanceDialects.MySqlAsync();
        await MirrorTable.ExecuteAsync(
            fixture.Raw, SqlDialect.MySql, "DELETE FROM `cf_soft_delete`");

        CfSoftDeleteEntity[] rows = NewRows();
        await Assert.That(async () => await fixture.Session.BulkInsertReturningAsync(rows))
            .Throws<NotSupportedException>();

        // 拒绝发生在写入前：表保持为空
        await Assert.That(await fixture.Session.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(0L);
    }

    [Test]
    public async Task Sqlite_EmptyList_ReturnsZeroWithoutThrowing()
    {
        await using SharedSqliteDb shared = await SeedSqliteAsync("bulk_returning_empty");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        long affected = await db.BulkInsertReturningAsync(Array.Empty<CfSoftDeleteEntity>());
        await Assert.That(affected).IsEqualTo(0L);
    }
}