using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>事务与分页的双侧对照（阶段 0b）。
/// <para>事务：提交、回滚、Savepoint 回滚三态。手写侧用 SQLite 的 BEGIN / COMMIT / ROLLBACK
/// 与 SAVEPOINT / ROLLBACK TO。分页：`ToPageAsync` 键集分页的首页与总数。</para></summary>
public sealed class TransactionAndPagingDualSideTests
{
    private const string MirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_bulk_plain_mirror (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "name" TEXT NOT NULL,
            "amount" INTEGER NOT NULL
        )
        """;

    private const string InsertMirror =
        """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""";

    private const string SelectMirror =
        """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror""";

    private static async ValueTask<SharedSqliteDb> CreateAsync(CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("transaction_dual_side", ct);
        try
        {
            await shared.ExecuteRawAsync(MirrorDdl, ct: ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static Dictionary<string, object?>[] Project(List<CfBulkPlainEntity> rows)
        => [.. rows.Select(entity => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = entity.Id,
            ["name"] = entity.Name,
            ["amount"] = entity.Amount
        })];

    private static async Task AssertEquivalentAsync(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> expected,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> actual)
    {
        IReadOnlyList<RowComparer.Difference> differences = RowComparer.Compare(expected, actual);
        string detail = string.Join(
            " | ",
            differences.Select(d => $"{d.Kind}/{d.Column}: expected={d.Expected} actual={d.Actual}"));
        await Assert.That(detail).IsEmpty();
    }

    [Test]
    public async Task TransactionCommit_BothSidesPersistRow()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.WithTransaction(async ct =>
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "committed", Amount = 1 }, ct);
        });

        await shared.ExecuteRawAsync("BEGIN", ct: default);
        await shared.ExecuteRawAsync(InsertMirror, [("@p0", 1L), ("@p1", "committed"), ("@p2", 1L)]);
        await shared.ExecuteRawAsync("COMMIT", ct: default);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await Assert.That(palormRows.Count).IsEqualTo(1);
        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task TransactionRollback_BothSidesDiscardRow()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await Assert.That(async () => await db.WithTransaction(async ct =>
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 2, Name = "rolled-back", Amount = 2 }, ct);
            throw new InvalidOperationException("forced rollback");
        })).Throws<InvalidOperationException>();

        await shared.ExecuteRawAsync("BEGIN", ct: default);
        await shared.ExecuteRawAsync(InsertMirror, [("@p0", 2L), ("@p1", "rolled-back"), ("@p2", 2L)]);
        await shared.ExecuteRawAsync("ROLLBACK", ct: default);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await Assert.That(palormRows.Count).IsEqualTo(0);
        await Assert.That(mirrorRows.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SavepointRollback_BothSidesKeepOnlyEarlierRow()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        using (System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync())
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "sp1", Amount = 1 });
            await db.SavepointAsync(transaction, "sp1");
            await db.InsertAsync(new CfBulkPlainEntity { Id = 2, Name = "sp2", Amount = 2 });
            await db.RollbackToAsync(transaction, "sp1");
            await transaction.CommitAsync();
        }

        await shared.ExecuteRawAsync("BEGIN", ct: default);
        await shared.ExecuteRawAsync(InsertMirror, [("@p0", 1L), ("@p1", "sp1"), ("@p2", 1L)]);
        await shared.ExecuteRawAsync("SAVEPOINT sp1", ct: default);
        await shared.ExecuteRawAsync(InsertMirror, [("@p0", 2L), ("@p1", "sp2"), ("@p2", 2L)]);
        await shared.ExecuteRawAsync("ROLLBACK TO sp1", ct: default);
        await shared.ExecuteRawAsync("COMMIT", ct: default);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await Assert.That(palormRows.Count).IsEqualTo(1);
        await Assert.That(palormRows[0].Name).IsEqualTo("sp1");
        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task ToPage_FirstPageAndTotal_MatchHandwrittenLimitCount()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.BulkInsertAsync(
        [
            new CfBulkPlainEntity { Id = 1, Name = "p1", Amount = 1 },
            new CfBulkPlainEntity { Id = 2, Name = "p2", Amount = 2 },
            new CfBulkPlainEntity { Id = 3, Name = "p3", Amount = 3 },
            new CfBulkPlainEntity { Id = 4, Name = "p4", Amount = 4 },
            new CfBulkPlainEntity { Id = 5, Name = "p5", Amount = 5 }
        ]);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8), (@p9, @p10, @p11), (@p12, @p13, @p14)""",
            [
                ("@p0", 1L), ("@p1", "p1"), ("@p2", 1L),
                ("@p3", 2L), ("@p4", "p2"), ("@p5", 2L),
                ("@p6", 3L), ("@p7", "p3"), ("@p8", 3L),
                ("@p9", 4L), ("@p10", "p4"), ("@p11", 4L),
                ("@p12", 5L), ("@p13", "p5"), ("@p14", 5L)
            ]);

        // 显式关掉降序：ToPageAsync 的 descending 默认 true（键集分页默认从最大值续页），
        // 手写侧按升序写就必须显式对齐，否则比较的是两种不同语义
        (List<CfBulkPlainEntity> page, long total) =
            await db.From<CfBulkPlainEntity>().ToPageAsync(2, item => item.Id, descending: false);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorPage = await shared.QueryRawAsync(
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror ORDER BY "Id" LIMIT 2 OFFSET 0""");
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rawTotal = await shared.QueryRawAsync(
            """SELECT COUNT(*) AS c FROM cf_bulk_plain_mirror""");

        await Assert.That(total).IsEqualTo(5L);
        await Assert.That(total).IsEqualTo(Convert.ToInt64(rawTotal[0]["c"], null));
        await Assert.That(page.Count).IsEqualTo(2);
        await AssertEquivalentAsync(mirrorPage, Project(page));
    }
}