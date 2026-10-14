using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>B6 落地（2026-10-12 全量审读）：BulkMerge 默认键行集合化——RETURNING 方言多行
/// INSERT..RETURNING 按行序回填，N 次往返收敛为 N/批（SQLite 2 列实体 = 499 行/批）。
/// 锁定：跨批回填正确、ID 唯一、混合默认键/既有键行各自语义保持。</summary>
public sealed class BulkMergeDefaultKeyBatchTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSeededAsync(int seedRows)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = $"Data Source=bmbatch_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
        });
        await session.ExecuteAsync(
            $"CREATE TABLE bm_merge_items (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Qty INTEGER NOT NULL)");
        var seed = new List<BmMergeItem>(seedRows);
        for (int i = 1; i <= seedRows; i++)
            seed.Add(new BmMergeItem { Name = $"seed-{i}", Qty = i });
        if (seedRows > 0)
            await session.BulkInsertAsync(seed);
        return session;
    }

    [Test]
    public async Task DefaultKeyRows_AcrossBatchBoundaries_AllIdsBackfilledUnique()
    {
        // 600 行 × 2 列 = 1200 参数 > SQLite 999 上限 → 499+101 两批：跨批回填与批内
        // 回填都必须正确对位（错位即静默写错实体 ID）。
        await using DataSession<SqliteProvider> session = await CreateSeededAsync(0);
        var rows = new List<BmMergeItem>(600);
        for (int i = 0; i < 600; i++)
            rows.Add(new BmMergeItem { Name = $"new-{i}", Qty = i });

        long affected = await session.BulkMergeAsync(rows);

        await Assert.That(affected).IsEqualTo(600);
        await Assert.That(rows.All(r => r.Id > 0)).IsTrue();
        await Assert.That(rows.Select(r => r.Id).Distinct().Count()).IsEqualTo(600);
        List<BmMergeItem> all = await session.GetAllAsync<BmMergeItem>();
        await Assert.That(all.Count).IsEqualTo(600);
    }

    [Test]
    public async Task MixedDefaultAndExistingKeys_InsertsAndUpdatesBothApplied()
    {
        await using DataSession<SqliteProvider> session = await CreateSeededAsync(20);
        var existing = await session.GetAllAsync<BmMergeItem>();
        BmMergeItem toUpdate = existing[5];
        toUpdate.Name = "updated-by-merge";
        toUpdate.Qty = 999;
        var batch = new List<BmMergeItem> { new() { Name = "fresh-a", Qty = 1 }, toUpdate, new() { Name = "fresh-b", Qty = 2 } };

        long affected = await session.BulkMergeAsync(batch);

        await Assert.That(affected).IsEqualTo(3);
        await Assert.That(batch[0].Id).IsGreaterThan(0);
        await Assert.That(batch[2].Id).IsGreaterThan(0);
        // 既有键行走 UPSERT 更新（非重复插入）
        BmMergeItem? reloaded = await session.GetAsync<BmMergeItem>(toUpdate.Id);
        await Assert.That(reloaded!.Name).IsEqualTo("updated-by-merge");
        await Assert.That(reloaded.Qty).IsEqualTo(999);
        List<BmMergeItem> all = await session.GetAllAsync<BmMergeItem>();
        await Assert.That(all.Count).IsEqualTo(22);
    }
}

[Table("bm_merge_items")]
internal sealed partial class BmMergeItem
{
    [Key] public long Id { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
    [Column("Qty")] public int Qty { get; set; }
}
