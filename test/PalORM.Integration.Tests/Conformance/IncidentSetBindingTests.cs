using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>事故族 1：SET 谓词与绑定漂移（ITM-552/642），8 条定向用例。
/// <para>缺陷形态：UPDATE 的 SET 列序与绑定循环漂移，导致值写进相邻列。断言取"逐列独立核对"
/// 与"四列同时改后对照手写 UPDATE"两种形态，前者定位到列、后者保证整体等价。</para></summary>
public sealed class IncidentSetBindingTests
{
    private const string MirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_set_drift_mirror (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "name" TEXT NOT NULL,
            "amount" INTEGER NOT NULL,
            "flag" BOOLEAN NOT NULL,
            "ratio" DECIMAL(18,6) NOT NULL,
            "note" TEXT
        )
        """;

    private const string SelectMirror =
        """SELECT "Id", "name", "amount", "flag", "ratio", "note" FROM cf_set_drift_mirror""";

    private static async ValueTask<SharedSqliteDb> CreateAsync(CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("incident_set_binding", ct);
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

    private static Dictionary<string, object?>[] Project(List<CfSetDriftEntity> rows)
        => [.. rows.Select(entity => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = entity.Id,
            ["name"] = entity.Name,
            ["amount"] = entity.Amount,
            ["flag"] = entity.Flag,
            ["ratio"] = entity.Ratio,
            ["note"] = entity.Note
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

    private static CfSetDriftEntity NewRow(long id) => new()
    {
        Id = id,
        Name = "initial",
        Amount = 1,
        Flag = false,
        Ratio = 1.0m,
        Note = "note"
    };

    private static async ValueTask SeedAsync(SharedSqliteDb shared, DataSession<SqliteProvider> db)
    {
        await db.InsertAsync(NewRow(1));
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_set_drift_mirror ("Id", "name", "amount", "flag", "ratio", "note") VALUES (@p0, @p1, @p2, @p3, @p4, @p5)""",
            [
                ("@p0", 1L), ("@p1", "initial"), ("@p2", 1L),
                ("@p3", false), ("@p4", 1.0m), ("@p5", "note")
            ]);
    }

    [Test]
    public async Task Update_AllColumns_NoCrossColumnContamination()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        CfSetDriftEntity entity = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        entity.Name = "changed";
        entity.Amount = 999;
        entity.Flag = true;
        entity.Ratio = 12.34m;
        entity.Note = null;
        await db.UpdateAsync(entity);

        await shared.ExecuteRawAsync(
            """UPDATE cf_set_drift_mirror SET "name" = @p0, "amount" = @p1, "flag" = @p2, "ratio" = @p3, "note" = @p4 WHERE "Id" = @p5""",
            [("@p0", "changed"), ("@p1", 999L), ("@p2", true), ("@p3", 12.34m), ("@p4", null), ("@p5", 1L)]);

        List<CfSetDriftEntity> palormRows = await db.From<CfSetDriftEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task Update_NullableText_WritesNullNotEmptyString()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        CfSetDriftEntity entity = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        entity.Note = null;
        await db.UpdateAsync(entity);

        CfSetDriftEntity reloaded = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        await Assert.That(reloaded.Note).IsNull();
    }

    [Test]
    public async Task UpdateColumns_TwoOfSix_LeavesOthersIntact()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        CfSetDriftEntity entity = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        entity.Amount = 55;
        entity.Flag = true;
        entity.Name = "should-not-change";
        entity.Ratio = 99.99m;

        int affected = await db.UpdateColumnsAsync(entity, [e => e.Amount, e => e.Flag]);
        await Assert.That(affected).IsEqualTo(1);

        CfSetDriftEntity reloaded = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        await Assert.That(reloaded.Amount).IsEqualTo(55L);
        await Assert.That(reloaded.Flag).IsTrue();
        await Assert.That(reloaded.Name).IsEqualTo("initial");
        await Assert.That(reloaded.Ratio).IsEqualTo(1.0m);
    }

    [Test]
    public async Task Update_LongExtremes_PreservedExactly()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        CfSetDriftEntity entity = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        entity.Amount = long.MinValue;
        await db.UpdateAsync(entity);

        CfSetDriftEntity reloaded = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        await Assert.That(reloaded.Amount).IsEqualTo(long.MinValue);

        entity.Amount = long.MaxValue;
        await db.UpdateAsync(entity);
        reloaded = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        await Assert.That(reloaded.Amount).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task Update_DecimalTrailingZero_PreservedAsDeclaredScale()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        CfSetDriftEntity entity = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        entity.Ratio = 12.340m;
        await db.UpdateAsync(entity);

        CfSetDriftEntity reloaded = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        // 数值必须相等；尾零是否保留取决于列声明（DECIMAL(18,6) 在 SQLite 无真实类型）
        await Assert.That(reloaded.Ratio).IsEqualTo(12.340m);
    }

    [Test]
    public async Task Update_BooleanFlag_RoundTripsBothStates()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await SeedAsync(shared, db);

        CfSetDriftEntity entity = (await db.GetAsync<CfSetDriftEntity>(1L))!;
        entity.Flag = true;
        await db.UpdateAsync(entity);
        await Assert.That((await db.GetAsync<CfSetDriftEntity>(1L))!.Flag).IsTrue();

        entity.Flag = false;
        await db.UpdateAsync(entity);
        await Assert.That((await db.GetAsync<CfSetDriftEntity>(1L))!.Flag).IsFalse();
    }

    [Test]
    public async Task BulkUpdate_FourColumnsPerRow_MatchesHandwritten()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfSetDriftEntity[] rows = [NewRow(1), NewRow(2)];
        rows[1].Name = "second";
        rows[1].Amount = 2;
        await db.BulkInsertAsync(rows);
        await shared.ExecuteRawAsync(
            """INSERT INTO cf_set_drift_mirror ("Id", "name", "amount", "flag", "ratio", "note") VALUES (@p0, @p1, @p2, @p3, @p4, @p5), (@p6, @p7, @p8, @p9, @p10, @p11)""",
            [
                ("@p0", 1L), ("@p1", "initial"), ("@p2", 1L), ("@p3", false), ("@p4", 1.0m), ("@p5", "note"),
                ("@p6", 2L), ("@p7", "second"), ("@p8", 2L), ("@p9", false), ("@p10", 1.0m), ("@p11", "note")
            ]);

        rows[0].Name = "bulk-a";
        rows[0].Amount = 111;
        rows[0].Flag = true;
        rows[0].Ratio = 2.5m;
        rows[1].Name = "bulk-b";
        rows[1].Amount = 222;
        rows[1].Flag = true;
        rows[1].Ratio = 3.5m;
        await db.BulkUpdateAsync(rows);

        await shared.ExecuteRawAsync(
            """UPDATE cf_set_drift_mirror SET "name" = @p0, "amount" = @p1, "flag" = @p2, "ratio" = @p3 WHERE "Id" = @p4""",
            [("@p0", "bulk-a"), ("@p1", 111L), ("@p2", true), ("@p3", 2.5m), ("@p4", 1L)]);
        await shared.ExecuteRawAsync(
            """UPDATE cf_set_drift_mirror SET "name" = @p0, "amount" = @p1, "flag" = @p2, "ratio" = @p3 WHERE "Id" = @p4""",
            [("@p0", "bulk-b"), ("@p1", 222L), ("@p2", true), ("@p3", 3.5m), ("@p4", 2L)]);

        List<CfSetDriftEntity> palormRows = await db.From<CfSetDriftEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(SelectMirror);

        await AssertEquivalentAsync(mirrorRows, Project(palormRows));
    }

    [Test]
    public async Task Update_SoftDeleteEntity_CarriesDeletedAtToResurrect()
    {
        // 契约（B7 文档化）：软删实体的 Update SET 含 deleted_at，实体携带自身值；
        // 置 null 即复活。这条与"SET 不触碰 deleted_at"的直觉相反，是 SET 谓词面的一部分。
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("incident_set_binding_soft_delete");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfSoftDeleteEntity entity = new() { Name = "resurrect-me" };
        await db.InsertAsync(entity);
        await db.DeleteAsync<CfSoftDeleteEntity>(entity.Id);

        long aliveAfterDelete = await db.CountAsync<CfSoftDeleteEntity>();
        await Assert.That(aliveAfterDelete).IsEqualTo(0L);

        // IgnoreFilters 是会话粘性且无公开复位 API：读已删行必须用独立会话，
        // 否则后续 CountAsync 也会跳过过滤，"有效行数为 1"的断言变成"全量为 1"
        await using DataSession<SqliteProvider> inspect = await shared.OpenSessionAsync();
        CfSoftDeleteEntity deleted = (await inspect.IgnoreFilters().From<CfSoftDeleteEntity>().ToListAsync())[0];
        deleted.DeletedAt = null;
        deleted.Name = "resurrected";
        await db.UpdateAsync(deleted);

        long aliveAfterUpdate = await db.CountAsync<CfSoftDeleteEntity>();
        await Assert.That(aliveAfterUpdate).IsEqualTo(1L);
        await Assert.That((await db.From<CfSoftDeleteEntity>().ToListAsync())[0].Name).IsEqualTo("resurrected");
    }
}