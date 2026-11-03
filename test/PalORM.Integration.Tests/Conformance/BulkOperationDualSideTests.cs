using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>批量族五个操作的双侧对照（阶段 0b 操作族基线）+ 边界形状。
/// <para>批量族的头号缺陷是数组填充顺序与批量骨架列序（方案 §4.6 权重 28%）。手写侧用最直白的
/// 多值 INSERT、逐行 UPDATE、IN 删除与 SQLite 的 upsert，与 PalORM 的批量路径比对。</para>
/// <para><b>主键显式赋值</b>：实测 <c>BulkInsertAsync</c> 返回影响行数、不回填自增 Id，
/// 实体 Id 保持 0；按 Id 匹配的 BulkUpdate/BulkDelete 会静默 0 行受影响。因此实体用
/// <c>[Key(AutoIncrement = false)]</c>，Id 由测试赋值，两侧看到同一批主键。</para>
/// <para><b>与 TriDialectBulkTests 的分工</b>（2026-11-03 明确）：TriDialectBulkTests 是
/// 方案 §10 阶段 3 的三方言一致性对照，每个操作在 SQLite/PG/MySQL 各跑一次同一份泛型核心，
/// 数据形状固定为 2~3 行的常规形态；本类只跑 SQLite，专攻 §4.6 的<b>形状边界</b>——
/// 空列表、单行、跨批宽（2500 行）、主键乱序。前者回答"三方言是否一致"，后者回答
/// "形状极端时批量骨架是否仍逐行对应"。两者互补，不重复。</para></summary>
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
            """
            INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount")
            VALUES (@p0, @p1, @p2)
            ON CONFLICT("Id") DO UPDATE SET "name" = excluded."name", "amount" = excluded."amount"
            """,
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

    // ─── 边界形状（§4.6）：TriDialectBulkTests 的常规 2~3 行形态覆盖不到 ───

    /// <summary>空列表：五个批量操作都必须返回 0 且不触碰库。
    /// <para>判据是"两侧最终态一致 + 返回值为 0"，不是"不抛异常"——空集合在批量骨架里
    /// 走的是提前返回分支（DataSession_Bulk 的 <c>entities.Count == 0</c> 检查），
    /// 若该分支被误删，会退化成构造空 SQL 执行或抛索引越界。</para></summary>
    [Test]
    public async Task EmptyLists_AllFiveOperations_ReturnZeroAndTouchNothing()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        // 先放一行哨兵，证明空操作不会误删/误改既有行
        CfBulkPlainEntity sentinel = new() { Id = 1, Name = "sentinel", Amount = 1 };
        await db.BulkInsertAsync([sentinel]);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
            [("@p0", 1L), ("@p1", "sentinel"), ("@p2", 1L)]);

        CfBulkPlainEntity[] empty = [];

        await Assert.That(await db.BulkInsertAsync(empty)).IsEqualTo(0L);
        await Assert.That(await db.BulkUpdateAsync(empty)).IsEqualTo(0L);
        await Assert.That(await db.BulkUpdateBatchAsync(empty)).IsEqualTo(0L);
        await Assert.That(await db.BulkMergeAsync(empty)).IsEqualTo(0L);
        await Assert.That(await db.BulkDeleteAsync<CfBulkPlainEntity, long>([])).IsEqualTo(0L);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);
        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
        // 哨兵行仍在（空操作没有误伤）
        await Assert.That(palormRows.Count).IsEqualTo(1);
        await Assert.That(palormRows[0].Name).IsEqualTo("sentinel");
    }

    /// <summary>单行：批量骨架的最小非空形状。
    /// <para>单行时多值 INSERT 只有一个值元组、逐行 UPDATE 只有一次迭代——批宽算式
    /// （<c>min(参数上限, 行数上限) / 列数</c>）在行数为 1 时容易退化成 0 或负，
    /// 是最容易踩"除零/空批"的形状。</para></summary>
    [Test]
    public async Task SingleRow_AllFiveOperations_MatchHandwritten()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfBulkPlainEntity single = new() { Id = 7, Name = "only", Amount = 7 };
        await Assert.That(await db.BulkInsertAsync([single])).IsEqualTo(1L);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
            [("@p0", 7L), ("@p1", "only"), ("@p2", 7L)]);
        await AssertEquivalentAsync(
            await shared.QueryRawAsync(SelectMirror),
            Project(await db.From<CfBulkPlainEntity>().ToListAsync()));

        single.Name = "updated";
        await Assert.That(await db.BulkUpdateAsync([single])).IsEqualTo(1L);
        await shared.ExecuteRawAsync(
            """UPDATE cf_bulk_plain_mirror SET "name" = @p0 WHERE "Id" = @p1""",
            [("@p0", "updated"), ("@p1", 7L)]);
        await AssertEquivalentAsync(
            await shared.QueryRawAsync(SelectMirror),
            Project(await db.From<CfBulkPlainEntity>().ToListAsync()));

        single.Amount = 70;
        await Assert.That(await db.BulkUpdateBatchAsync([single])).IsEqualTo(1L);
        await shared.ExecuteRawAsync(
            """UPDATE cf_bulk_plain_mirror SET "amount" = @p0 WHERE "Id" = @p1""",
            [("@p0", 70L), ("@p1", 7L)]);
        await AssertEquivalentAsync(
            await shared.QueryRawAsync(SelectMirror),
            Project(await db.From<CfBulkPlainEntity>().ToListAsync()));

        // 单键删除
        await Assert.That(await db.BulkDeleteAsync<CfBulkPlainEntity, long>([7L])).IsEqualTo(1L);
        await shared.ExecuteRawAsync(
            """DELETE FROM cf_bulk_plain_mirror WHERE "Id" = @p0""", [("@p0", 7L)]);
        await AssertEquivalentAsync(
            await shared.QueryRawAsync(SelectMirror),
            Project(await db.From<CfBulkPlainEntity>().ToListAsync()));
    }

    /// <summary>跨批宽：2500 行超过 SQLite 的单语句参数上限（999），必须切多批且不丢行。
    /// <para>TriDialectBulkTests 用 3 行，永远在单批内；本用例是唯一验证"批边界处列序不漂移"
    /// 的形状——批间若错位一行，首尾主键映射会整体偏移，逐行比对必然报出差异。</para>
    /// <para>用主键乱序的输入（见下一个用例）与顺序输入分开，避免两个变量同时变化。</para></summary>
    [Test]
    public async Task AboveDialectBatchWidth_2500Rows_KeepsPerRowAlignment()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        const int rowCount = 2500;
        CfBulkPlainEntity[] rows = new CfBulkPlainEntity[rowCount];
        for (int i = 0; i < rowCount; i++)
            rows[i] = new CfBulkPlainEntity { Id = i + 1, Name = $"r{i + 1}", Amount = i + 1 };

        await Assert.That(await db.BulkInsertAsync(rows)).IsEqualTo((long)rowCount);

        // 手写侧同批写入（多值 INSERT 2500 行会超参数上限，逐行插入保持判据独立）
        for (int i = 0; i < rowCount; i++)
        {
            await shared.ExecuteRawAsync(
                """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
                [("@p0", (long)(i + 1)), ("@p1", $"r{i + 1}"), ("@p2", (long)(i + 1))]);
        }

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);
        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
        await Assert.That(palormRows.Count).IsEqualTo(rowCount);
    }

    /// <summary>主键乱序：输入顺序与主键顺序相反，逐行比对必须仍逐位对应。
    /// <para>批量骨架按输入下标填充列值、按主键匹配更新行。若实现内部把实体按主键
    /// 重排后再绑定（常见优化），乱序输入会让"第 i 个实体的值"写进"第 j 行"。
    /// 顺序输入时重排与不重排结果相同，因此这个缺陷只在乱序形状下可见。</para></summary>
    [Test]
    public async Task DescendingKeyOrder_UpdateAndDelete_StayPerRowAligned()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        // 主键 5,4,3,2,1 —— 与插入/读取顺序相反
        CfBulkPlainEntity[] descending =
        [
            new() { Id = 5, Name = "before-5", Amount = 5 },
            new() { Id = 4, Name = "before-4", Amount = 4 },
            new() { Id = 3, Name = "before-3", Amount = 3 },
            new() { Id = 2, Name = "before-2", Amount = 2 },
            new() { Id = 1, Name = "before-1", Amount = 1 }
        ];
        await db.BulkInsertAsync(descending);

        // 手写侧逐行（避免多值 INSERT 的行序掩盖问题）
        foreach (CfBulkPlainEntity row in descending)
        {
            await shared.ExecuteRawAsync(
                """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
                [("@p0", row.Id), ("@p1", row.Name), ("@p2", row.Amount)]);
        }

        // 每个实体写自己的新值（值 = Id * 10，错位立刻可见）
        foreach (CfBulkPlainEntity row in descending)
        {
            row.Name = $"after-{row.Id}";
            row.Amount = row.Id * 10;
        }
        await db.BulkUpdateAsync(descending);
        foreach (CfBulkPlainEntity row in descending)
        {
            await shared.ExecuteRawAsync(
                """UPDATE cf_bulk_plain_mirror SET "name" = @p0, "amount" = @p1 WHERE "Id" = @p2""",
                [("@p0", row.Name), ("@p1", row.Amount), ("@p2", row.Id)]);
        }
        await AssertEquivalentAsync(
            await shared.QueryRawAsync(SelectMirror),
            Project(await db.From<CfBulkPlainEntity>().ToListAsync()));

        // 乱序删中间三个键
        await Assert.That(
            await db.BulkDeleteAsync<CfBulkPlainEntity, long>([3L, 5L, 2L])).IsEqualTo(3L);
        await shared.ExecuteRawAsync(
            """DELETE FROM cf_bulk_plain_mirror WHERE "Id" IN (@p0, @p1, @p2)""",
            [("@p0", 3L), ("@p1", 5L), ("@p2", 2L)]);
        await AssertEquivalentAsync(
            await shared.QueryRawAsync(SelectMirror),
            Project(await db.From<CfBulkPlainEntity>().ToListAsync()));
    }
}