using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>组合查询与剩余单行操作的双侧对照（阶段 0b）。
/// <para>覆盖 InnerJoin、LeftJoin、CTE、Insert（显式主键）与 ScalarAsync。
/// GroupBy/Having、窗口函数、ToPageAsync 键集分页尚未落地，见文档阶段 0b 的未完成项。</para></summary>
public sealed class QueryShapeDualSideTests
{
    private const string PlainMirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_bulk_plain_mirror (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "name" TEXT NOT NULL,
            "amount" INTEGER NOT NULL
        )
        """;

    private const string IndexMirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_composite_index_mirror (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
            "category" TEXT NOT NULL,
            "price" DECIMAL(18,6) NOT NULL,
            "sku" TEXT NOT NULL
        )
        """;

    private static async ValueTask<SharedSqliteDb> CreateAsync(CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("query_shape_dual_side", ct);
        try
        {
            await shared.ExecuteRawAsync(PlainMirrorDdl, ct: ct);
            await shared.ExecuteRawAsync(IndexMirrorDdl, ct: ct);
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

    private static async ValueTask SeedAsync(SharedSqliteDb shared, DataSession<SqliteProvider> db)
    {
        // 左表三行，右表只有两行：InnerJoin 少一行，LeftJoin 保留三行
        await db.BulkInsertAsync(
        [
            new CfBulkPlainEntity { Id = 1, Name = "one", Amount = 10 },
            new CfBulkPlainEntity { Id = 2, Name = "two", Amount = 20 },
            new CfBulkPlainEntity { Id = 3, Name = "three", Amount = 30 }
        ]);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8)""",
            [
                ("@p0", 1L), ("@p1", "one"), ("@p2", 10L),
                ("@p3", 2L), ("@p4", "two"), ("@p5", 20L),
                ("@p6", 3L), ("@p7", "three"), ("@p8", 30L)
            ]);
        await db.BulkInsertAsync(
        [
            new CfCompositeIndexEntity { Id = 1, Category = "cat-a", Price = 1m, Sku = "sku-1" },
            new CfCompositeIndexEntity { Id = 2, Category = "cat-b", Price = 2m, Sku = "sku-2" }
        ]);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_composite_index_mirror ("Id", "category", "price", "sku") VALUES (@p0, @p1, @p2, @p3), (@p4, @p5, @p6, @p7)""",
            [
                ("@p0", 1L), ("@p1", "cat-a"), ("@p2", 1m), ("@p3", "sku-1"),
                ("@p4", 2L), ("@p5", "cat-b"), ("@p6", 2m), ("@p7", "sku-2")
            ]);
    }

    [Test]
    public async Task InnerJoin_KeepsOnlyMatchedRows()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>()
            .InnerJoin<CfCompositeIndexEntity>(
                $"cf_composite_index.\"Id\" = cf_bulk_plain.\"Id\"")
            .ToListAsync();

        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(
            """SELECT L."Id", L."name", L."amount" FROM cf_bulk_plain_mirror L INNER JOIN cf_composite_index_mirror R ON R."Id" = L."Id" """);

        await Assert.That(palormRows.Count).IsEqualTo(2);
        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task LeftJoin_KeepsAllLeftRows()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>()
            .LeftJoin<CfCompositeIndexEntity>(
                $"cf_composite_index.\"Id\" = cf_bulk_plain.\"Id\"")
            .ToListAsync();

        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(
            """SELECT L."Id", L."name", L."amount" FROM cf_bulk_plain_mirror L LEFT JOIN cf_composite_index_mirror R ON R."Id" = L."Id" """);

        await Assert.That(palormRows.Count).IsEqualTo(3);
        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task CteSource_MatchesHandwrittenSubquery()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>()
            .With("cf_amount_cte", $"SELECT \"Id\", \"name\", \"amount\" FROM cf_bulk_plain WHERE \"amount\" > 15")
            .ToListAsync();

        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE "amount" > 15""");

        await Assert.That(palormRows.Count).IsEqualTo(2);
        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task Insert_WithExplicitKey_MatchesHandwrittenInsert()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.InsertAsync(new CfBulkPlainEntity { Id = 42, Name = "explicit", Amount = 4 });
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2)""",
            [("@p0", 42L), ("@p1", "explicit"), ("@p2", 4L)]);

        CfBulkPlainEntity? palormRow = await db.GetAsync<CfBulkPlainEntity>(42L);
        await Assert.That(palormRow).IsNotNull();

        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror""");
        await Assert.That(mirrorRows.Count).IsEqualTo(1);
        IReadOnlyList<RowComparer.Difference> differences =
            RowComparer.Compare(mirrorRows, [.. Project([palormRow!])]);
        await Assert.That(differences.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GroupByHaving_MatchesHandwrittenGroupCount()
    {
        // GroupBy 返回实体 T，具体取组内哪一行由数据库决定（SQLite bare column 行为不稳定），
        // 因此只判定"组数"与"分组键集合"这两个确定面，不比较行内其余列。
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.BulkInsertAsync(
        [
            new CfBulkPlainEntity { Id = 1, Name = "x", Amount = 10 },
            new CfBulkPlainEntity { Id = 2, Name = "x", Amount = 10 },
            new CfBulkPlainEntity { Id = 3, Name = "y", Amount = 20 },
            new CfBulkPlainEntity { Id = 4, Name = "y", Amount = 20 },
            new CfBulkPlainEntity { Id = 5, Name = "z", Amount = 30 }
        ]);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8), (@p9, @p10, @p11), (@p12, @p13, @p14)""",
            [
                ("@p0", 1L), ("@p1", "x"), ("@p2", 10L),
                ("@p3", 2L), ("@p4", "x"), ("@p5", 10L),
                ("@p6", 3L), ("@p7", "y"), ("@p8", 20L),
                ("@p9", 4L), ("@p10", "y"), ("@p11", 20L),
                ("@p12", 5L), ("@p13", "z"), ("@p14", 30L)
            ]);

        // 只保留出现次数大于 1 的组：x 与 y
        List<CfBulkPlainEntity> palormRows = await db.From<CfBulkPlainEntity>()
            .GroupBy(e => e.Name)
            .Having($"COUNT(*) > 1")
            .ToListAsync();

        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(
            """SELECT "name" FROM cf_bulk_plain_mirror GROUP BY "name" HAVING COUNT(*) > 1""");

        string[] palormNames = [.. palormRows.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)];
        string[] mirrorNames = [.. mirrorRows.Select(r => (string)r["name"]!).OrderBy(n => n, StringComparer.Ordinal)];

        await Assert.That(palormNames.Length).IsEqualTo(2);
        await Assert.That(string.Join(",", palormNames)).IsEqualTo(string.Join(",", mirrorNames));
    }

    [Test]
    public async Task ScalarAsync_MatchesHandwrittenCount()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        // 原始 SQL 入口：默认过滤不适用（ITM-700），两侧都写完整 SQL
        long palormCount = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM cf_bulk_plain");

        IReadOnlyList<IReadOnlyDictionary<string, object?>> raw = await shared.QueryRawAsync(
            """SELECT COUNT(*) AS c FROM cf_bulk_plain_mirror""");

        await Assert.That(palormCount).IsEqualTo(3L);
        await Assert.That(palormCount).IsEqualTo(Convert.ToInt64(raw[0]["c"], null));
    }
}