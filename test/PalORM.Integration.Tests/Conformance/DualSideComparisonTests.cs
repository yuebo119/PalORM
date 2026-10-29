using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>L2 双侧对照：S02 形状的四个操作加 S04 的硬删。
/// <para><b>两侧隔离</b>：PalORM 侧写 <c>cf_soft_delete</c>（MigrateAsync 建表），手写侧写同构镜像表
/// <c>cf_soft_delete_mirror</c>（手写 DDL）。独立会话不等于数据隔离，同表两侧会互相污染
/// （方案 §4.2）。</para>
/// <para><b>期望值独立性</b>：手写侧走裸 ADO 常量 SQL，不经过 From&lt;T&gt;、RowFactory 或
/// PalORM_Runtime；PalORM 侧走完整 API 路径（含参数绑定与物化），因此本组同时覆盖 P4 与 P6。</para></summary>
public sealed class DualSideComparisonTests
{
    // 手写 DDL 与被测实体生成的 DDL 同构：列名逐字一致，类型取 SQLite 亲和类型。
    private const string SoftDeleteMirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_soft_delete_mirror (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
            "name" TEXT NOT NULL,
            "deleted_at" TEXT
        )
        """;

    private const string VersionedMirrorDdl = """
        CREATE TABLE IF NOT EXISTS cf_versioned_mirror (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
            "name" TEXT NOT NULL,
            "version" INTEGER NOT NULL
        )
        """;

    private const string SelectSoftDeleteMirror = """SELECT "Id", "name", "deleted_at" FROM cf_soft_delete_mirror""";

    private const string SelectVersionedMirror = """SELECT "Id", "name", "version" FROM cf_versioned_mirror""";

    private const string InsertSoftDeleteMirror =
        """INSERT INTO cf_soft_delete_mirror ("name", "deleted_at") VALUES (@p0, @p1)""";

    private const string InsertVersionedMirror =
        """INSERT INTO cf_versioned_mirror ("name", "version") VALUES (@p0, @p1)""";

    private static async ValueTask<SharedSqliteDb> CreateAsync(CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("dual_side", ct);
        try
        {
            await shared.ExecuteRawAsync(SoftDeleteMirrorDdl, ct: ct);
            await shared.ExecuteRawAsync(VersionedMirrorDdl, ct: ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static Dictionary<string, object?> ProjectSoftDelete(CfSoftDeleteEntity entity) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = entity.Id,
        ["name"] = entity.Name,
        ["deleted_at"] = entity.DeletedAt
    };

    private static Dictionary<string, object?>[] ProjectSoftDelete(List<CfSoftDeleteEntity> rows)
        => [.. rows.Select(ProjectSoftDelete)];

    private static Dictionary<string, object?>[] ProjectVersioned(List<CfVersionedEntity> rows)
        => [.. rows.Select(entity => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = entity.Id,
            ["name"] = entity.Name,
            ["version"] = entity.Version
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
    public async Task Insert_BothSidesProduceSameRow()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.InsertAsync(new CfSoftDeleteEntity { Name = "row-1" });
        await shared.ExecuteRawAsync(InsertSoftDeleteMirror, [("@p0", "row-1"), ("@p1", null)]);

        List<CfSoftDeleteEntity> palormRows = await db.From<CfSoftDeleteEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows =
            await shared.QueryRawAsync(SelectSoftDeleteMirror);

        await AssertEquivalentAsync(mirrorRows, ProjectSoftDelete(palormRows));
    }

    [Test]
    public async Task GetAll_BothSidesProduceSameRows()
    {
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.InsertAsync(new CfSoftDeleteEntity { Name = "a" });
        await db.InsertAsync(new CfSoftDeleteEntity { Name = "b" });
        await shared.ExecuteRawAsync(InsertSoftDeleteMirror, [("@p0", "a"), ("@p1", null)]);
        await shared.ExecuteRawAsync(InsertSoftDeleteMirror, [("@p0", "b"), ("@p1", null)]);

        List<CfSoftDeleteEntity> palormRows = await db.GetAllAsync<CfSoftDeleteEntity>();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows =
            await shared.QueryRawAsync(SelectSoftDeleteMirror);

        await AssertEquivalentAsync(mirrorRows, ProjectSoftDelete(palormRows));
    }

    [Test]
    public async Task WhereAndOrWhereChain_BothSidesProduceSameRows()
    {
        // R4 回放基座：Where(A).OrWhere(B).Where(C) 的括号形态一旦回归，本用例的集合会不同。
        // 数据与谓词必须让两种求值产生不同结果，否则判据无判别力：
        //   正确 ((A) OR (B)) AND (C) = {b}；错误 (A) OR ((B) AND (C)) = {a, b}
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        await db.InsertAsync(new CfSoftDeleteEntity { Name = "a" });
        await db.InsertAsync(new CfSoftDeleteEntity { Name = "b" });
        await shared.ExecuteRawAsync(InsertSoftDeleteMirror, [("@p0", "a"), ("@p1", null)]);
        await shared.ExecuteRawAsync(InsertSoftDeleteMirror, [("@p0", "b"), ("@p1", null)]);

        List<CfSoftDeleteEntity> palormRows = await db.From<CfSoftDeleteEntity>()
            .Where($"name = 'a'")
            .OrWhere($"name = 'b'")
            .Where($"name <> 'a'")
            .ToListAsync();

        // 手写侧按方法文档承诺的形态展开：((A) OR (B)) AND (C)，再加默认过滤
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows = await shared.QueryRawAsync(
            """SELECT "Id", "name", "deleted_at" FROM cf_soft_delete_mirror WHERE (("name" = 'a') OR ("name" = 'b')) AND ("name" <> 'a') AND "deleted_at" IS NULL""");

        await Assert.That(palormRows.Count).IsEqualTo(1);
        await AssertEquivalentAsync(mirrorRows, ProjectSoftDelete(palormRows));
    }

    [Test]
    public async Task Update_BothSidesProduceSameRow()
    {
        // R2 回放基座：SET 列序与绑定循环漂移会让 name 与 deleted_at 互换
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfSoftDeleteEntity entity = new() { Name = "before" };
        await db.InsertAsync(entity);
        await shared.ExecuteRawAsync(InsertSoftDeleteMirror, [("@p0", "before"), ("@p1", null)]);

        entity.Name = "after";
        await db.UpdateAsync(entity);
        await shared.ExecuteRawAsync(
            """UPDATE cf_soft_delete_mirror SET "name" = @p0, "deleted_at" = @p1 WHERE "Id" = @p2""",
            [("@p0", "after"), ("@p1", null), ("@p2", 1L)]);

        List<CfSoftDeleteEntity> palormRows = await db.From<CfSoftDeleteEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows =
            await shared.QueryRawAsync(SelectSoftDeleteMirror);

        await AssertEquivalentAsync(mirrorRows, ProjectSoftDelete(palormRows));
    }

    [Test]
    public async Task HardDelete_BothSidesProduceSameRemainingRows()
    {
        // S04 形状：无默认过滤，DeleteAsync 走真实物理删除
        await using SharedSqliteDb shared = await CreateAsync();
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        CfVersionedEntity first = new() { Name = "keep", Version = 1 };
        CfVersionedEntity second = new() { Name = "remove", Version = 1 };
        await db.InsertAsync(first);
        await db.InsertAsync(second);
        await shared.ExecuteRawAsync(InsertVersionedMirror, [("@p0", "keep"), ("@p1", 1L)]);
        await shared.ExecuteRawAsync(InsertVersionedMirror, [("@p0", "remove"), ("@p1", 1L)]);

        await db.DeleteAsync<CfVersionedEntity>(second.Id);
        await shared.ExecuteRawAsync(
            """DELETE FROM cf_versioned_mirror WHERE "Id" = @p0""", [("@p0", 2L)]);

        List<CfVersionedEntity> palormRows = await db.From<CfVersionedEntity>().ToListAsync();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> mirrorRows =
            await shared.QueryRawAsync(SelectVersionedMirror);

        await AssertEquivalentAsync(mirrorRows, ProjectVersioned(palormRows));
    }
}