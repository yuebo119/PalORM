using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>0b 补充：窗口函数与键集续页。
/// <para>窗口值不进实体（`Select` 投影触发 PALORM033），因此窗口函数的可观测面是
/// "加入窗口列后实体行集不变、SQL 语法合法"。键集续页的可观测面是"两页并集等于前 N 行且无重复"。</para></summary>
public sealed class PagingWindowProbeTests
{
    private const string PlainMirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_bulk_plain_mirror (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "name" TEXT NOT NULL,
            "amount" INTEGER NOT NULL
        )
        """;

    private static async ValueTask<SharedSqliteDb> SeedAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        try
        {
            await shared.ExecuteRawAsync(PlainMirrorDdl, ct: ct);
            await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
            await db.BulkInsertAsync(
            [
                new CfBulkPlainEntity { Id = 1, Name = "p1", Amount = 10 },
                new CfBulkPlainEntity { Id = 2, Name = "p2", Amount = 20 },
                new CfBulkPlainEntity { Id = 3, Name = "p3", Amount = 30 },
                new CfBulkPlainEntity { Id = 4, Name = "p4", Amount = 40 },
                new CfBulkPlainEntity { Id = 5, Name = "p5", Amount = 50 }
            ], ct);
            await shared.ExecuteRawAsync(
                """
                INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount")
                VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8), (@p9, @p10, @p11), (@p12, @p13, @p14)
                """,
                [
                    ("@p0", 1L), ("@p1", "p1"), ("@p2", 10L),
                    ("@p3", 2L), ("@p4", "p2"), ("@p5", 20L),
                    ("@p6", 3L), ("@p7", "p3"), ("@p8", 30L),
                    ("@p9", 4L), ("@p10", "p4"), ("@p11", 40L),
                    ("@p12", 5L), ("@p13", "p5"), ("@p14", 50L)
                ], ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    [Test]
    public async Task WindowOver_DoesNotChangeEntityRows()
    {
        await using SharedSqliteDb shared = await SeedAsync("window_probe");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfBulkPlainEntity> plain = await db.From<CfBulkPlainEntity>().ToListAsync();
        List<CfBulkPlainEntity> windowed = await db.From<CfBulkPlainEntity>()
            .UnsafeWindowOver("ROW_NUMBER()", "ORDER BY \"Id\"")
            .ToListAsync();

        // 窗口列不进实体映射：两侧主键集合必须一致，且 SQL 在三方言合法（SQLite 已实测）
        await Assert.That(plain.Count).IsEqualTo(5);
        await Assert.That(
            string.Join(",", windowed.Select(e => e.Id).OrderBy(id => id)))
            .IsEqualTo(string.Join(",", plain.Select(e => e.Id).OrderBy(id => id)));

        // 与手写 SQL（带窗口列但只取实体列）对照
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror""");
        await Assert.That(mirrorRows.Count).IsEqualTo(windowed.Count);
    }

    [Test]
    public async Task ToPageContinuation_SecondPageMatchesHandwrittenKeyset()
    {
        await using SharedSqliteDb shared = await SeedAsync("keyset_probe");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        (List<CfBulkPlainEntity> firstPage, long firstTotal) =
            await db.From<CfBulkPlainEntity>().ToPageAsync(2, e => e.Id, descending: false);
        (List<CfBulkPlainEntity> secondPage, long secondTotal) =
            await db.From<CfBulkPlainEntity>().ToPageAsync(
                2, e => e.Id, lastValue: firstPage[^1].Id, descending: false);

        await Assert.That(firstTotal).IsEqualTo(5L);
        await Assert.That(secondTotal).IsEqualTo(5L);
        await Assert.That(string.Join(",", firstPage.Select(e => e.Id))).IsEqualTo("1,2");
        await Assert.That(string.Join(",", secondPage.Select(e => e.Id))).IsEqualTo("3,4");

        // 手写键集续页：WHERE Id > 上一页末键
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorSecondPage = await shared.QueryRawAsync(
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE "Id" > 2 ORDER BY "Id" LIMIT 2""");
        await Assert.That(mirrorSecondPage.Count).IsEqualTo(secondPage.Count);
        await Assert.That(Convert.ToInt64(mirrorSecondPage[0]["Id"], null)).IsEqualTo(3L);

        // 两页之间不得重复
        HashSet<long> union = [.. firstPage.Select(e => e.Id), .. secondPage.Select(e => e.Id)];
        await Assert.That(union.Count).IsEqualTo(4);
    }
}