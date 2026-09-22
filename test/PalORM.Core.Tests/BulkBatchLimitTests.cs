using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>BULK-001（2026-09-23）：批量路径的批大小改为"方言参数上限 × 单批行数上限"取小。
/// <para><b>SQLite 侧可验证面</b>：批大小从 500（单语句 IN 片段的约束，误用）提到方言参数上限
/// 999（与 <c>SqliteProvider</c> 的 BulkContext 同值）后，多批删除与多批 upsert 的正确性——
/// 若 999 越界，会在这些用例里先炸。</para>
/// <para><b>PG/MySQL 侧的往返数收益</b>（删除 500→5000、upsert 900→65535）需真库断言，
/// 属 ExternalDatabase 环境，本机不可达。</para></summary>
internal sealed class BulkBatchLimitTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=bulklimit_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE bulk_limit_rows (Id TEXT PRIMARY KEY, v INTEGER NOT NULL, tag TEXT NOT NULL)");
        return session;
    }

    private static List<BulkLimitRow> BuildRows(int count, string tag)
    {
        var entities = new List<BulkLimitRow>(count);
        for (int i = 1; i <= count; i++)
            entities.Add(new BulkLimitRow { Id = $"k{i}", V = i, Tag = tag });
        return entities;
    }

    [Test]
    public async Task BulkDelete_AboveDialectBatchSize_DeletesEveryRow()
    {
        // 2500 键 > 999（SQLite 方言参数上限）→ 至少 3 批；批大小越界会在此失败
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        List<BulkLimitRow> entities = BuildRows(2500, "t");
        await session.BulkInsertAsync(entities);

        var keys = entities.Select(static e => (object)e.Id).ToList();
        long deleted = await session.BulkDeleteAsync<BulkLimitRow>(keys);

        await Assert.That(deleted).IsEqualTo(2500);
        await Assert.That((await session.GetAllAsync<BulkLimitRow>()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task BulkMerge_MultiBatchUpsert_InsertsThenUpdates()
    {
        // 400 行 × 3 列：批大小 = min(999, 5000×3) / 3 = 333 → 2 批
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        List<BulkLimitRow> entities = BuildRows(400, "a");

        await session.BulkMergeAsync(entities);
        await Assert.That((await session.GetAllAsync<BulkLimitRow>()).Count).IsEqualTo(400);

        // 再跑一遍改值：走 upsert 更新分支，行数不增、值被覆盖
        foreach (BulkLimitRow entity in entities) entity.Tag = "b";
        await session.BulkMergeAsync(entities);

        List<BulkLimitRow> rows = await session.GetAllAsync<BulkLimitRow>();
        await Assert.That(rows.Count).IsEqualTo(400);
        await Assert.That(rows.All(static row => row.Tag == "b")).IsTrue();
    }
}

[Table("bulk_limit_rows")]
internal sealed partial class BulkLimitRow
{
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("v")] public int V { get; set; }
    [Column("tag")] public string Tag { get; set; } = "";
}
