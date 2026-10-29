using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>事故族 2 与族 3：OR 吞过滤（ITM-401，7 条）与链式 WHERE/OR 优先级（C16，7 条）。
/// <para>族 2 的判据是集合关系：用户 OR 链不得让默认过滤（软删、租户）失效。
/// 期望集合来自裸 ADO 读出的划分，不来自 PalORM 的查询结果。</para>
/// <para>族 3 的判据是手写 SQL 对照：按方法文档承诺的形态 <c>((A) OR (B)) AND (C)</c> 展开，
/// 数据与谓词都构造成让两种求值产生不同集合。</para></summary>
public sealed class IncidentFilterPrecedenceTests
{
    private const string PlainMirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_bulk_plain_mirror (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "name" TEXT NOT NULL,
            "amount" INTEGER NOT NULL
        )
        """;

    private static async ValueTask<SharedSqliteDb> CreateAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        try
        {
            await shared.ExecuteRawAsync(PlainMirrorDdl, ct: ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static async ValueTask<SharedSqliteDb> SeedSoftDeleteAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await CreateAsync(name, ct);
        try
        {
            await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive-a" }, ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive-b" }, ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive-c" }, ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "dead-a", DeletedAt = "2026-01-01" }, ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "dead-b", DeletedAt = "2026-01-02" }, ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertOnlyAliveAsync(
        SharedSqliteDb shared, IReadOnlyCollection<CfSoftDeleteEntity> rows, CancellationToken ct = default)
    {
        (HashSet<long> expectedAlive, HashSet<long> expectedDead) = await shared.ReadSoftDeletePartitionAsync(ct);
        await Assert.That(expectedDead.Count).IsGreaterThan(0);

        HashSet<long> actual = [.. rows.Select(e => e.Id)];
        // 子集关系：查询结果只能是有效行，绝不能含已删行
        await Assert.That(actual.IsSubsetOf(expectedAlive)).IsTrue();
        await Assert.That(actual.Overlaps(expectedDead)).IsFalse();
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

    // ── 族 2：OR 吞过滤（7 条）──────────────────────────────

    [Test]
    public async Task SoftDelete_WhereThenOrWhere_ExcludesDeleted()
    {
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("or_swallow_1");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        // 两个分支覆盖 "alive-a" 与 "dead-*"：若 DEFAULT FILTER 被 OR 吞掉，已删行会出现
        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>()
            .Where($"name = 'alive-a'")
            .OrWhere($"name LIKE 'dead-%'")
            .ToListAsync();

        await AssertOnlyAliveAsync(shared, rows);
        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SoftDelete_PureOrWhereChain_ExcludesDeleted()
    {
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("or_swallow_2");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>()
            .Where($"name = 'alive-b'")
            .OrWhere($"name = 'alive-c'")
            .OrWhere($"name = 'dead-a'")
            .ToListAsync();

        await AssertOnlyAliveAsync(shared, rows);
        await Assert.That(rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SoftDelete_WhereInWithOrWhere_ExcludesDeleted()
    {
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("or_swallow_3");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        (HashSet<long> expectedAlive, HashSet<long> expectedDead) = await shared.ReadSoftDeletePartitionAsync();
        long[] allKeys = [.. expectedAlive, .. expectedDead];

        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>()
            .WhereIn(e => e.Id, allKeys)
            .OrWhere($"name = 'dead-b'")
            .ToListAsync();

        await AssertOnlyAliveAsync(shared, rows);
    }

    [Test]
    public async Task Tenant_OrWhere_DoesNotBypassTenantFilter()
    {
        await using SharedSqliteDb shared = await CreateAsync("or_swallow_tenant");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.InsertAsync(new CfTenantEntity { TenantId = 1, Value = "t1-a" });
        await db.InsertAsync(new CfTenantEntity { TenantId = 1, Value = "t1-b" });
        await db.InsertAsync(new CfTenantEntity { TenantId = 2, Value = "t2-a" });

        db.WithTenant(1L);
        List<CfTenantEntity> rows = await db.From<CfTenantEntity>()
            .Where($"value = 't1-a'")
            .OrWhere($"value = 't2-a'")
            .ToListAsync();

        // OR 分支命中了别的租户，但租户过滤必须仍然生效
        await Assert.That(rows.All(e => e.TenantId == 1L)).IsTrue();

        Dictionary<long, HashSet<long>> byTenant = await shared.ReadTenantPartitionAsync();
        HashSet<long> expectedTenant1 = byTenant[1L];
        await Assert.That(rows.Select(e => e.Id).ToHashSet().IsSubsetOf(expectedTenant1)).IsTrue();
    }

    [Test]
    public async Task Combo_OrWhere_ExcludesDeletedAndForeignTenant()
    {
        await using SharedSqliteDb shared = await CreateAsync("or_swallow_combo");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.InsertAsync(new CfComboFilterEntity { TenantId = 1, Name = "t1-alive" });
        await db.InsertAsync(new CfComboFilterEntity { TenantId = 1, Name = "t1-dead", DeletedAt = "2026-01-01" });
        await db.InsertAsync(new CfComboFilterEntity { TenantId = 2, Name = "t2-alive" });

        db.WithTenant(1L);
        List<CfComboFilterEntity> rows = await db.From<CfComboFilterEntity>()
            .Where($"name = 't1-alive'")
            .OrWhere($"name = 't1-dead'")
            .OrWhere($"name = 't2-alive'")
            .ToListAsync();

        await Assert.That(rows.All(e => e.TenantId == 1L)).IsTrue();
        await Assert.That(rows.All(e => e.DeletedAt is null)).IsTrue();
        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SoftDelete_OrWhereWithLargeIn_ExcludesDeleted()
    {
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("or_swallow_large_in");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        (_, HashSet<long> expectedDead) = await shared.ReadSoftDeletePartitionAsync();
        long[] keys = [.. Enumerable.Range(1, 501).Select(i => (long)i)];

        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>()
            .WhereIn(e => e.Id, keys)
            .OrWhere($"name LIKE 'dead-%'")
            .ToListAsync();

        await AssertOnlyAliveAsync(shared, rows);
        await Assert.That(rows.Any(e => expectedDead.Contains(e.Id))).IsFalse();
    }

    [Test]
    public async Task SoftDelete_OrWhereWithCte_ExcludesDeleted()
    {
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("or_swallow_cte");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>()
            .With("cf_or_cte", $"SELECT \"Id\", \"name\", \"deleted_at\" FROM cf_soft_delete")
            .Where($"name = 'alive-a'")
            .OrWhere($"name = 'dead-a'")
            .ToListAsync();

        await AssertOnlyAliveAsync(shared, rows);
        await Assert.That(rows.Count).IsEqualTo(1);
    }

    // ── 族 3：链式 WHERE/OR 优先级（7 条）──────────────────────

    private static async ValueTask<SharedSqliteDb> SeedPlainAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await CreateAsync(name, ct);
        try
        {
            await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
            await db.BulkInsertAsync(
            [
                new CfBulkPlainEntity { Id = 1, Name = "a", Amount = 1 },
                new CfBulkPlainEntity { Id = 2, Name = "b", Amount = 2 },
                new CfBulkPlainEntity { Id = 3, Name = "c", Amount = 3 },
                new CfBulkPlainEntity { Id = 4, Name = "d", Amount = 4 }
            ], ct);
            await shared.ExecuteRawAsync(
                """INSERT INTO cf_bulk_plain_mirror ("Id", "name", "amount") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5), (@p6, @p7, @p8), (@p9, @p10, @p11)""",
                [
                    ("@p0", 1L), ("@p1", "a"), ("@p2", 1L),
                    ("@p3", 2L), ("@p4", "b"), ("@p5", 2L),
                    ("@p6", 3L), ("@p7", "c"), ("@p8", 3L),
                    ("@p9", 4L), ("@p10", "d"), ("@p11", 4L)
                ], ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertChainEquivalentAsync(
        SharedSqliteDb shared, List<CfBulkPlainEntity> palormRows, string handmadeSql)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(handmadeSql);
        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task Chain_WhereOrWhereWhere_MatchesHandwritten()
    {
        await using SharedSqliteDb shared = await SeedPlainAsync("chain_1");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>()
            .Where($"name = 'a'")
            .OrWhere($"name = 'b'")
            .Where($"name <> 'a'")
            .ToListAsync();

        // ((a) OR (b)) AND (name <> 'a') = {b}；错误形态 a OR (b AND ...) = {a,b}
        await Assert.That(rows.Count).IsEqualTo(1);
        await AssertChainEquivalentAsync(shared, rows,
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE (("name" = 'a') OR ("name" = 'b')) AND ("name" <> 'a')""");
    }

    [Test]
    public async Task Chain_WhereWhereOrWhere_MatchesHandwritten()
    {
        await using SharedSqliteDb shared = await SeedPlainAsync("chain_2");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>()
            .Where($"amount >= 2")
            .Where($"amount <= 3")
            .OrWhere($"name = 'd'")
            .Where($"name <> 'c'")
            .ToListAsync();

        // ((amount>=2 AND amount<=3) OR name='d') AND name<>'c' = {b, d}
        await Assert.That(rows.Count).IsEqualTo(2);
        await AssertChainEquivalentAsync(shared, rows,
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE ((("amount" >= 2) AND ("amount" <= 3)) OR ("name" = 'd')) AND ("name" <> 'c')""");
    }

    [Test]
    public async Task Chain_OrOrThenWhere_MatchesHandwritten()
    {
        await using SharedSqliteDb shared = await SeedPlainAsync("chain_3");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>()
            .Where($"name = 'a'")
            .OrWhere($"name = 'b'")
            .OrWhere($"name = 'c'")
            .Where($"amount > 1")
            .ToListAsync();

        // ((a OR b) OR c) AND amount>1 = {b, c}
        await Assert.That(rows.Count).IsEqualTo(2);
        await AssertChainEquivalentAsync(shared, rows,
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE ((("name" = 'a') OR ("name" = 'b')) OR ("name" = 'c')) AND ("amount" > 1)""");
    }

    [Test]
    public async Task Chain_WhereInMixedWithOrWhere_MatchesHandwritten()
    {
        await using SharedSqliteDb shared = await SeedPlainAsync("chain_4");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        long[] keys = [1L, 2L];
        List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>()
            .WhereIn(e => e.Id, keys)
            .OrWhere($"name = 'd'")
            .Where($"amount > 1")
            .ToListAsync();

        // ((Id IN (1,2)) OR name='d') AND amount>1 = {2, d}
        await Assert.That(rows.Count).IsEqualTo(2);
        await AssertChainEquivalentAsync(shared, rows,
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE ((("Id" IN (1, 2))) OR ("name" = 'd')) AND ("amount" > 1)""");
    }

    [Test]
    public async Task Chain_ManyOrThenSingleWhere_MatchesHandwritten()
    {
        await using SharedSqliteDb shared = await SeedPlainAsync("chain_5");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>()
            .Where($"name = 'a'")
            .OrWhere($"name = 'b'")
            .Where($"name = 'b'")
            .OrWhere($"name = 'c'")
            .Where($"amount <> 3")
            .ToListAsync();

        // ((a OR b) AND (b OR c)) AND amount<>3 = {b}
        await Assert.That(rows.Count).IsEqualTo(1);
        await AssertChainEquivalentAsync(shared, rows,
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE ((("name" = 'a') OR ("name" = 'b')) AND (("name" = 'b') OR ("name" = 'c'))) AND ("amount" <> 3)""");
    }

    [Test]
    public async Task Chain_NestedOrInsideSingleClause_MatchesHandwritten()
    {
        await using SharedSqliteDb shared = await SeedPlainAsync("chain_6");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        // 单条 Where 子句内部自带 OR：外层与后续 AND 的关系由用户括号决定
        List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>()
            .Where($"name = 'a' OR name = 'b'")
            .Where($"amount > 1")
            .ToListAsync();

        await Assert.That(rows.Count).IsEqualTo(1);
        await AssertChainEquivalentAsync(shared, rows,
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE (("name" = 'a' OR "name" = 'b') AND ("amount" > 1))""");
    }

    [Test]
    public async Task Chain_PureOrNoTrailingAnd_MatchesHandwritten()
    {
        await using SharedSqliteDb shared = await SeedPlainAsync("chain_7");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        // 纯 OR 链无外层包裹：与手写的最直白 OR 形态一致
        List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>()
            .Where($"name = 'a'")
            .OrWhere($"name = 'd'")
            .ToListAsync();

        await Assert.That(rows.Count).IsEqualTo(2);
        await AssertChainEquivalentAsync(shared, rows,
            """SELECT "Id", "name", "amount" FROM cf_bulk_plain_mirror WHERE ("name" = 'a' OR "name" = 'd')""");
    }
}