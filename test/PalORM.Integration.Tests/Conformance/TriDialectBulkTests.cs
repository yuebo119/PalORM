using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>批量族五操作的三方言双侧对照（方案 §10 阶段 3）。
/// <para>每个操作三个测试（SQLite / PostgreSQL / MySQL），共用同一份泛型核心：PalORM 侧走批量 API，
/// 手写侧走 <see cref="DialectSql"/> 生成的方言 SQL，两侧各写一张同构表再按列名比对。</para>
/// <para>远程方言标 <c>ExternalDatabase</c> 并串行执行：同一容器库上多测试并发会互相清表。</para></summary>
[NotInParallel("ConformanceRemoteTables")]
public sealed class TriDialectBulkTests
{
    private static readonly string[] BulkColumns = ["Id", "name", "amount"];

    private static async Task WithSqliteAsync(
        Func<SqlDialect, DataSession<SqliteProvider>, DbConnection, Task> body)
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("tri_bulk");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await MirrorTable.EnsureAsync(db, SqlDialect.Sqlite);
        await body(SqlDialect.Sqlite, db, db.GetRawConnection());
    }

    private static async Task WithPostgreSqlAsync(
        Func<SqlDialect, DataSession<PostgreSqlProvider>, DbConnection, Task> body)
    {
        await using RemoteConformanceFixture<PostgreSqlProvider> fixture = await ConformanceDialects.PostgreSqlAsync();
        await body(SqlDialect.PostgreSql, fixture.Session, fixture.Raw);
    }

    private static async Task WithMySqlAsync(
        Func<SqlDialect, DataSession<MySqlProvider>, DbConnection, Task> body)
    {
        await using RemoteConformanceFixture<MySqlProvider> fixture = await ConformanceDialects.MySqlAsync();
        await body(SqlDialect.MySql, fixture.Session, fixture.Raw);
    }

    private static CfBulkPlainEntity Plain(long id, string name, long amount)
        => new() { Id = id, Name = name, Amount = amount };

    private static Dictionary<string, object?>[] Project(List<CfBulkPlainEntity> rows)
        => [.. rows.Select(entity => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = entity.Id,
            ["name"] = entity.Name,
            ["amount"] = entity.Amount
        })];

    private static async Task AssertSameAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw, CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync(ct);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await MirrorTable.QueryAsync(
            raw, dialect, DialectSql.SelectAll(dialect, MirrorTable.BulkMirror, BulkColumns), ct: ct);

        IReadOnlyList<RowComparer.Difference> differences = RowComparer.Compare(mirrorRows, Project(palormRows));
        string detail = string.Join(
            " | ",
            differences.Select(d => $"{d.Kind}/{d.Column}: expected={d.Expected} actual={d.Actual}"));
        await Assert.That(detail).IsEmpty();
    }

    private static async Task BulkInsertCoreAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw)
        where TProvider : IDbProvider
    {
        await db.BulkInsertAsync([Plain(1, "a", 1), Plain(2, "b", 2), Plain(3, "c", 3)]);
        await MirrorTable.ExecuteAsync(
            raw, dialect, DialectSql.Insert(dialect, MirrorTable.BulkMirror, BulkColumns, 3),
            [
                ("@p0", 1L), ("@p1", "a"), ("@p2", 1L),
                ("@p3", 2L), ("@p4", "b"), ("@p5", 2L),
                ("@p6", 3L), ("@p7", "c"), ("@p8", 3L)
            ]);
        await AssertSameAsync(dialect, db, raw);
    }

    private static async Task BulkUpdateCoreAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw)
        where TProvider : IDbProvider
    {
        CfBulkPlainEntity[] rows = [Plain(1, "before-a", 1), Plain(2, "before-b", 2)];
        await db.BulkInsertAsync(rows);
        await MirrorTable.ExecuteAsync(
            raw, dialect, DialectSql.Insert(dialect, MirrorTable.BulkMirror, BulkColumns, 2),
            [("@p0", 1L), ("@p1", "before-a"), ("@p2", 1L), ("@p3", 2L), ("@p4", "before-b"), ("@p5", 2L)]);

        rows[0].Name = "after-a";
        rows[1].Name = "after-b";
        await db.BulkUpdateAsync(rows);
        await MirrorTable.ExecuteAsync(raw, dialect,
            DialectSql.UpdateColumn(dialect, MirrorTable.BulkMirror, "name", "Id"), [("@p0", "after-a"), ("@p1", 1L)]);
        await MirrorTable.ExecuteAsync(raw, dialect,
            DialectSql.UpdateColumn(dialect, MirrorTable.BulkMirror, "name", "Id"), [("@p0", "after-b"), ("@p1", 2L)]);

        await AssertSameAsync(dialect, db, raw);
    }

    private static async Task BulkUpdateBatchCoreAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw)
        where TProvider : IDbProvider
    {
        CfBulkPlainEntity[] rows = [Plain(1, "batch-a", 1), Plain(2, "batch-b", 2)];
        await db.BulkInsertAsync(rows);
        await MirrorTable.ExecuteAsync(
            raw, dialect, DialectSql.Insert(dialect, MirrorTable.BulkMirror, BulkColumns, 2),
            [("@p0", 1L), ("@p1", "batch-a"), ("@p2", 1L), ("@p3", 2L), ("@p4", "batch-b"), ("@p5", 2L)]);

        rows[0].Amount = 11;
        rows[1].Amount = 12;
        await db.BulkUpdateBatchAsync(rows);
        await MirrorTable.ExecuteAsync(raw, dialect,
            DialectSql.UpdateColumn(dialect, MirrorTable.BulkMirror, "amount", "Id"), [("@p0", 11L), ("@p1", 1L)]);
        await MirrorTable.ExecuteAsync(raw, dialect,
            DialectSql.UpdateColumn(dialect, MirrorTable.BulkMirror, "amount", "Id"), [("@p0", 12L), ("@p1", 2L)]);

        await AssertSameAsync(dialect, db, raw);
    }

    private static async Task BulkMergeCoreAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw)
        where TProvider : IDbProvider
    {
        await db.BulkInsertAsync([Plain(1, "existing", 1)]);
        await MirrorTable.ExecuteAsync(
            raw, dialect, DialectSql.Insert(dialect, MirrorTable.BulkMirror, BulkColumns, 1),
            [("@p0", 1L), ("@p1", "existing"), ("@p2", 1L)]);

        await db.BulkMergeAsync([Plain(1, "merged", 11), Plain(2, "added", 2)]);
        await MirrorTable.ExecuteAsync(
            raw, dialect, DialectSql.Upsert(dialect, MirrorTable.BulkMirror, BulkColumns, "Id", 2),
            [
                ("@p0", 1L), ("@p1", "merged"), ("@p2", 11L),
                ("@p3", 2L), ("@p4", "added"), ("@p5", 2L)
            ]);

        await AssertSameAsync(dialect, db, raw);
    }

    private static async Task BulkDeleteCoreAsync<TProvider>(
        SqlDialect dialect, DataSession<TProvider> db, DbConnection raw)
        where TProvider : IDbProvider
    {
        await db.BulkInsertAsync([Plain(1, "keep", 1), Plain(2, "remove-1", 1), Plain(3, "remove-2", 1)]);
        await MirrorTable.ExecuteAsync(
            raw, dialect, DialectSql.Insert(dialect, MirrorTable.BulkMirror, BulkColumns, 3),
            [
                ("@p0", 1L), ("@p1", "keep"), ("@p2", 1L),
                ("@p3", 2L), ("@p4", "remove-1"), ("@p5", 1L),
                ("@p6", 3L), ("@p7", "remove-2"), ("@p8", 1L)
            ]);

        await db.BulkDeleteAsync<CfBulkPlainEntity, long>([2L, 3L]);
        await MirrorTable.ExecuteAsync(
            raw, dialect, DialectSql.DeleteByKeys(dialect, MirrorTable.BulkMirror, "Id", 2),
            [("@p0", 2L), ("@p1", 3L)]);

        await AssertSameAsync(dialect, db, raw);
    }

    [Test]
    public async Task Sqlite_BulkInsert() => await WithSqliteAsync(BulkInsertCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkInsert() => await WithPostgreSqlAsync(BulkInsertCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkInsert() => await WithMySqlAsync(BulkInsertCoreAsync);

    [Test]
    public async Task Sqlite_BulkUpdate() => await WithSqliteAsync(BulkUpdateCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkUpdate() => await WithPostgreSqlAsync(BulkUpdateCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkUpdate() => await WithMySqlAsync(BulkUpdateCoreAsync);

    [Test]
    public async Task Sqlite_BulkUpdateBatch() => await WithSqliteAsync(BulkUpdateBatchCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkUpdateBatch() => await WithPostgreSqlAsync(BulkUpdateBatchCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkUpdateBatch() => await WithMySqlAsync(BulkUpdateBatchCoreAsync);

    [Test]
    public async Task Sqlite_BulkMerge() => await WithSqliteAsync(BulkMergeCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkMerge() => await WithPostgreSqlAsync(BulkMergeCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkMerge() => await WithMySqlAsync(BulkMergeCoreAsync);

    [Test]
    public async Task Sqlite_BulkDelete() => await WithSqliteAsync(BulkDeleteCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkDelete() => await WithPostgreSqlAsync(BulkDeleteCoreAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkDelete() => await WithMySqlAsync(BulkDeleteCoreAsync);
}