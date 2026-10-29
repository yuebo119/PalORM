using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>批量族五个操作的双侧对照（阶段 0b 操作族基线）。
/// <para>批量族的头号缺陷是数组填充顺序与批量骨架列序（方案 §4.6 权重 28%）。手写侧用最直白的
/// 多值 INSERT、逐行 UPDATE、IN 删除与 SQLite 的 upsert，与 PalORM 的批量路径比对。</para>
/// <para><b>主键显式赋值</b>：实测 <c>BulkInsertAsync</c> 返回影响行数、不回填自增 Id，
/// 实体 Id 保持 0；按 Id 匹配的 BulkUpdate/BulkDelete 会静默 0 行受影响。因此实体用
/// <c>[Key(AutoIncrement = false)]</c>，Id 由测试赋值，两侧看到同一批主键。</para></summary>
public sealed class BulkOperationDualSideTests
{
    private const string MirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_bulk_plain_mirror (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "name" TEXT NOT NULL,
            "amount" INTEGER NOT NULL
        )
        """;

    private const string SelectMirror = """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror""";

    private static async ValueTask<SharedSqliteDb> CreateAsync(CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("bulk_dual_side", ct);
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
    public async Task BulkInsert_BothSidesProduceSameRows()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfBulkPlainEntity[] rows =
        [
            new() { Id = 1, Name = "row-a", Amount = 1 },
            new() { Id = 2, Name = "row-b", Amount = 2 },
            new() { Id = 3, Name = "row-c", Amount = 3 }
        ];
        await db.BulkInsertAsync(rows);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8)""",
            [
                ("@p0", 1L), ("@p1", "row-a"), ("@p2", 1L),
                ("@p3", 2L), ("@p4", "row-b"), ("@p5", 2L),
                ("@p6", 3L), ("@p7", "row-c"), ("@p8", 3L)
            ]);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task BulkUpdate_BothSidesProduceSameRows()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfBulkPlainEntity[] rows =
        [
            new() { Id = 1, Name = "before-a", Amount = 1 },
            new() { Id = 2, Name = "before-b", Amount = 2 }
        ];
        await db.BulkInsertAsync(rows);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)""",
            [("@p0", 1L), ("@p1", "before-a"), ("@p2", 1L), ("@p3", 2L), ("@p4", "before-b"), ("@p5", 2L)]);

        rows[0].Name = "after-a";
        rows[1].Name = "after-b";
        await db.BulkUpdateAsync(rows);
        // 手写侧最直白写法：逐行 UPDATE
        await shared.ExecuteRawAsync(
            """UPDATE cf_bulk_plain_mirror SET "name" = @p0 WHERE "Id" = @p1""",
            [("@p0", "after-a"), ("@p1", 1L)]);
        await shared.ExecuteRawAsync(
            """UPDATE cf_bulk_plain_mirror SET "name" = @p0 WHERE "Id" = @p1""",
            [("@p0", "after-b"), ("@p1", 2L)]);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task BulkUpdateBatch_BothSidesProduceSameRows()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfBulkPlainEntity[] rows =
        [
            new() { Id = 1, Name = "batch-a", Amount = 1 },
            new() { Id = 2, Name = "batch-b", Amount = 2 }
        ];
        await db.BulkInsertAsync(rows);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5)""",
            [("@p0", 1L), ("@p1", "batch-a"), ("@p2", 1L), ("@p3", 2L), ("@p4", "batch-b"), ("@p5", 2L)]);

        rows[0].Amount = 11;
        rows[1].Amount = 12;
        await db.BulkUpdateBatchAsync(rows);
        await shared.ExecuteRawAsync(
            """UPDATE cf_bulk_plain_mirror SET "amount" = @p0 WHERE "Id" = @p1""",
            [("@p0", 11L), ("@p1", 1L)]);
        await shared.ExecuteRawAsync(
            """UPDATE cf_bulk_plain_mirror SET "amount" = @p0 WHERE "Id" = @p1""",
            [("@p0", 12L), ("@p1", 2L)]);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task BulkMerge_BothSidesProduceSameRows()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfBulkPlainEntity first = new() { Id = 1, Name = "existing", Amount = 1 };
        await db.BulkInsertAsync([first]);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
            [("@p0", 1L), ("@p1", "existing"), ("@p2", 1L)]);

        // 同主键更新 + 新主键插入
        CfBulkPlainEntity existing = new() { Id = 1, Name = "existing-updated", Amount = 11 };
        CfBulkPlainEntity inserted = new() { Id = 2, Name = "new-row", Amount = 2 };
        await db.BulkMergeAsync([existing, inserted]);

        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2) ON CONFLICT("Id") DO UPDATE SET "name" = excluded."name", "amount" = excluded."amount" """,
            [("@p0", 1L), ("@p1", "existing-updated"), ("@p2", 11L)]);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
            [("@p0", 2L), ("@p1", "new-row"), ("@p2", 2L)]);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task BulkDelete_BothSidesProduceSameRemainingRows()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfBulkPlainEntity[] rows =
        [
            new() { Id = 1, Name = "keep", Amount = 1 },
            new() { Id = 2, Name = "remove-1", Amount = 1 },
            new() { Id = 3, Name = "remove-2", Amount = 1 }
        ];
        await db.BulkInsertAsync(rows);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8)""",
            [
                ("@p0", 1L), ("@p1", "keep"), ("@p2", 1L),
                ("@p3", 2L), ("@p4", "remove-1"), ("@p5", 1L),
                ("@p6", 3L), ("@p7", "remove-2"), ("@p8", 1L)
            ]);

        long[] removeKeys = [2L, 3L];
        await db.BulkDeleteAsync<CfBulkPlainEntity, long>(removeKeys);
        await shared.ExecuteRawAsync(
            """DELETE FROM cf_bulk_plain_mirror WHERE "Id" IN (@p0, @p1)""",
            [("@p0", 2L), ("@p1", 3L)]);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }
}