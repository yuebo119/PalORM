using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>单行族第二批双侧对照（阶段 0b 操作族基线）。
/// <para>覆盖 SaveAsync 的两个冲突分支、UpdateColumns 部分列、GetAsync 按主键读取、
/// 五个聚合函数与软删 Delete 的排除语义。</para>
/// <para>软删 Delete 只比较"被排除的行集合"，不比较 deleted_at 的时间戳文本：
/// PalORM 自动生成时间戳，手写侧无法预测；标记列的格式另有专门断言负责。</para></summary>
public sealed class SingleRowDualSideTests
{
    private const string PlainMirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_bulk_plain_mirror (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "name" TEXT NOT NULL,
            "amount" INTEGER NOT NULL
        )
        """;

    private const string SoftDeleteMirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_soft_delete_mirror (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
            "name" TEXT NOT NULL,
            "deleted_at" TEXT
        )
        """;

    private const string SelectPlainMirror = """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror""";

    private static async ValueTask<SharedSqliteDb> CreateAsync(CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("single_row_dual_side", ct);
        try
        {
            await shared.ExecuteRawAsync(PlainMirrorDdl, ct: ct);
            await shared.ExecuteRawAsync(SoftDeleteMirrorDdl, ct: ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static Dictionary<string, object?> Project(CfBulkPlainEntity entity) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = entity.Id,
        ["name"] = entity.Name,
        ["amount"] = entity.Amount
    };

    [Test]
    public async Task Save_InsertBranch_MatchesHandwrittenInsert()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.SaveAsync(new CfBulkPlainEntity { Id = 10, Name = "saved-by-upsert", Amount = 5 });
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
            [("@p0", 10L), ("@p1", "saved-by-upsert"), ("@p2", 5L)]);

        CfBulkPlainEntity? palormRow = await db.GetAsync<CfBulkPlainEntity>(10L);
        await Assert.That(palormRow).IsNotNull();

        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectPlainMirror);
        await Assert.That(mirrorRows.Count).IsEqualTo(1);
        IReadOnlyList<RowComparer.Difference> differences = RowComparer.Compare(mirrorRows, [Project(palormRow!)]);
        await Assert.That(differences.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Save_UpdateBranch_MatchesHandwrittenUpsert()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.InsertAsync(new CfBulkPlainEntity { Id = 20, Name = "before", Amount = 1 });
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
            [("@p0", 20L), ("@p1", "before"), ("@p2", 1L)]);

        // 同主键的 SaveAsync 走冲突更新分支
        await db.SaveAsync(new CfBulkPlainEntity { Id = 20, Name = "after", Amount = 9 });
        await shared.ExecuteRawAsync(
            """
            INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount")
            VALUES (@p0, @p1, @p2)
            ON CONFLICT("Id") DO UPDATE SET "name" = excluded."name", "amount" = excluded."amount"
            """,
            [("@p0", 20L), ("@p1", "after"), ("@p2", 9L)]);

        CfBulkPlainEntity? palormRow = await db.GetAsync<CfBulkPlainEntity>(20L);
        await Assert.That(palormRow).IsNotNull();

        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectPlainMirror);
        await Assert.That(mirrorRows.Count).IsEqualTo(1);
        IReadOnlyList<RowComparer.Difference> differences = RowComparer.Compare(mirrorRows, [Project(palormRow!)]);
        await Assert.That(differences.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateColumns_MatchesHandwrittenPartialUpdate()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.InsertAsync(new CfBulkPlainEntity { Id = 30, Name = "keep-name", Amount = 1 });
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
            [("@p0", 30L), ("@p1", "keep-name"), ("@p2", 1L)]);

        // 只更新 amount：name 必须保持原值
        int affected = await db.UpdateColumnsAsync(
            new CfBulkPlainEntity { Id = 30, Name = "ignored", Amount = 77 }, e => e.Amount);
        await shared.ExecuteRawAsync(
            """UPDATE cf_bulk_plain_mirror SET "amount" = @p0 WHERE "Id" = @p1""",
            [("@p0", 77L), ("@p1", 30L)]);

        await Assert.That(affected).IsEqualTo(1);
        CfBulkPlainEntity? palormRow = await db.GetAsync<CfBulkPlainEntity>(30L);
        await Assert.That(palormRow).IsNotNull();
        await Assert.That(palormRow!.Name).IsEqualTo("keep-name");

        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectPlainMirror);
        IReadOnlyList<RowComparer.Difference> differences = RowComparer.Compare(mirrorRows, [Project(palormRow)]);
        await Assert.That(differences.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Aggregates_MatchHandwrittenSql()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfBulkPlainEntity[] rows =
        [
            new() { Id = 1, Name = "a", Amount = 10 },
            new() { Id = 2, Name = "b", Amount = 20 },
            new() { Id = 3, Name = "c", Amount = 30 }
        ];
        await db.BulkInsertAsync(rows);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8)""",
            [
                ("@p0", 1L), ("@p1", "a"), ("@p2", 10L),
                ("@p3", 2L), ("@p4", "b"), ("@p5", 20L),
                ("@p6", 3L), ("@p7", "c"), ("@p8", 30L)
            ]);

        long count = await db.CountAsync<CfBulkPlainEntity>();
        decimal sum = await db.SumAsync<CfBulkPlainEntity>($"amount");
        long? max = await db.MaxAsync<CfBulkPlainEntity, long>($"amount");
        long? min = await db.MinAsync<CfBulkPlainEntity, long>($"amount");
        double avg = await db.AvgAsync<CfBulkPlainEntity>($"amount");

        IReadOnlyList<IReadOnlyDictionary<string, object?>> raw = await shared.QueryRawAsync(
            """SELECT COUNT(*) AS c, SUM("amount") AS s, MAX("amount") AS mx, MIN("amount") AS mn, AVG("amount") AS av FROM cf_bulk_plain_mirror""");
        await Assert.That(raw.Count).IsEqualTo(1);

        await Assert.That(count).IsEqualTo(Convert.ToInt64(raw[0]["c"], null));
        await Assert.That(sum).IsEqualTo(Convert.ToDecimal(raw[0]["s"], null));
        await Assert.That(max).IsEqualTo(Convert.ToInt64(raw[0]["mx"], null));
        await Assert.That(min).IsEqualTo(Convert.ToInt64(raw[0]["mn"], null));
        // 聚合容差：方案 §4.7 允许 Sum/Avg 相对 1e-9
        double expectedAvg = Convert.ToDouble(raw[0]["av"], null);
        await Assert.That(Math.Abs(avg - expectedAvg) <= Math.Abs(expectedAvg) * 1e-9).IsTrue();
    }

    [Test]
    public async Task SoftDelete_ExcludesSameRowAsHandwrittenFlag()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfSoftDeleteEntity keep = new() { Name = "keep" };
        CfSoftDeleteEntity remove = new() { Name = "remove" };
        await db.InsertAsync(keep);
        await db.InsertAsync(remove);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_soft_delete_mirror ("name", "deleted_at") VALUES (@p0, NULL), (@p1, NULL)""",
            [("@p0", "keep"), ("@p1", "remove")]);

        await db.DeleteAsync<CfSoftDeleteEntity>(remove.Id);
        // 手写侧用固定时间戳：只校验"该行被排除"，不比较时间戳文本
        await shared.ExecuteRawAsync(
            """UPDATE cf_soft_delete_mirror SET "deleted_at" = '2026-01-01T00:00:00' WHERE "name" = @p0""",
            [("@p0", "remove")]);

        List<CfSoftDeleteEntity> palormAlive = await db.From<CfSoftDeleteEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorAlive = await shared.QueryRawAsync(
            """SELECT "Id", "name", "deleted_at" FROM cf_soft_delete_mirror WHERE "deleted_at" IS NULL""");

        // 只比 name（时间戳文本按方言与实现生成，不在本用例判定范围）
        string[] palormNames = [.. palormAlive.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)];
        string[] mirrorNames = [.. mirrorAlive.Select(r => (string)r["name"]!).OrderBy(n => n, StringComparer.Ordinal)];

        await Assert.That(palormNames.Length).IsEqualTo(1);
        await Assert.That(string.Join(",", palormNames)).IsEqualTo(string.Join(",", mirrorNames));
    }
}