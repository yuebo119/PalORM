using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>BulkMergeAsync 集合化的行为验证——v5.6.0 起非默认键行由「N 行 N 次往返」
/// 改为「多行 UPSERT 分批」。本用例锁定的语义契约：
/// <para>① 既有键更新 + 新键插入（UPSERT 本义）；② 混合默认/非默认键分区
/// （默认键走逐条 INSERT 并回填 ID）；③ 返回值 = 处理行数（不依赖 affectedRows）；
/// ④ 重复执行幂等（upsert）；⑤ [ConcurrencyCheck] 实体保持逐条路径的显式拒绝（ITM-503）。</para>
/// <para>跨批上限（900 参数/语句）由 4 列实体 × 200+ 行隐式覆盖（200 行 × 4 列 &gt; 900
/// → 分两批），覆盖多批路径。</para></summary>
[NotInParallel("ExtBulkTable")]
internal sealed class BulkMergeSetBasedTests
{
    [Test]
    public async Task Sqlite_BulkMerge_UpgradesExisting_InsertsNew_ReturnsRowCount()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        // 第一轮：3 行全新键 → 全部走集合化 upsert 的 INSERT 分支
        long first = await db.BulkMergeAsync(
        [
            new MergeEntity { Id = 1, Name = "a", Qty = 1 },
            new MergeEntity { Id = 2, Name = "b", Qty = 2 },
            new MergeEntity { Id = 3, Name = "c", Qty = 3 },
        ]);
        await Assert.That(first).IsEqualTo(3);
        var seeded = await db.GetAllAsync<MergeEntity>();
        await Assert.That(seeded.Count).IsEqualTo(3);

        // 第二轮：2 行既有键更新 + 1 行新键 → UPSERT 本义
        long second = await db.BulkMergeAsync(
        [
            new MergeEntity { Id = 1, Name = "a-updated", Qty = 11 },
            new MergeEntity { Id = 4, Name = "d", Qty = 4 },
        ]);
        await Assert.That(second).IsEqualTo(2);

        var rows = (await db.GetAllAsync<MergeEntity>()).OrderBy(static r => r.Id).ToList();
        await Assert.That(rows.Count).IsEqualTo(4);
        await Assert.That(rows[0].Name).IsEqualTo("a-updated");
        await Assert.That(rows[0].Qty).IsEqualTo(11);
        await Assert.That(rows[3].Name).IsEqualTo("d");
    }

    [Test]
    public async Task Sqlite_BulkMerge_MixedKeys_PartitionsDefaultKeyRows()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        // 混合：自增键默认值（→ 逐条 INSERT + ID 回填）与既有稳定键（→ 集合化 upsert）。
        // 注：AutoIncrement=false 的实体 Id=0 是合法键值而非"默认键"，插入 Id=0 且不回填
        // ——这是改造前后一致的行为（SetIdDelegates 仅对自增键发射），用自增实体测真分区。
        var withGenerated = new MergeGenEntity { Name = "gen", Qty = 1 };
        long merged = await db.BulkMergeAsync<MergeGenEntity>(
        [
            withGenerated,
            new MergeGenEntity { Id = 100, Name = "stable", Qty = 2 },
        ]);
        await Assert.That(merged).IsEqualTo(2);
        await Assert.That(withGenerated.Id).IsGreaterThan(0); // 默认键行 ID 回填契约保持

        var rows = await db.GetAllAsync<MergeGenEntity>();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows.Any(static r => r.Name == "stable")).IsTrue();
        await Assert.That(rows.Any(static r => r.Name == "gen")).IsTrue();
    }

    [Test]
    public async Task Sqlite_BulkMerge_LargeBatch_SpansMultipleStatements()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        // 400 行 × 5 upsert 列 = 2000 参数 > 900 上限 → 至少 3 批；全部新键
        const int rows = 400;
        long merged = await db.BulkMergeAsync(
            [.. Enumerable.Range(1, rows).Select(i => new MergeEntity { Id = i, Name = $"n{i}", Qty = i })]);
        await Assert.That(merged).IsEqualTo(rows);
        await Assert.That(await db.CountAsync<MergeEntity>()).IsEqualTo(rows);

        // 再跑一遍全部既有键 → 幂等（不增行，值刷新）
        long again = await db.BulkMergeAsync(
            [.. Enumerable.Range(1, rows).Select(i => new MergeEntity { Id = i, Name = $"m{i}", Qty = i })]);
        await Assert.That(again).IsEqualTo(rows);
        await Assert.That(await db.CountAsync<MergeEntity>()).IsEqualTo(rows);
        var sample = await db.GetAsync<MergeEntity>(1);
        await Assert.That(sample!.Name).IsEqualTo("m1");
    }

    [Test]
    public async Task Sqlite_BulkMerge_ConcurrencyCheckedEntity_StillRejected()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        // [ConcurrencyCheck] 实体：集合化 upsert 无法尊重乐观锁 → 保持逐条路径（其自身拒绝）
        await Assert.That(async () => await db.BulkMergeAsync(
                [new VersionedMergeEntity { Id = 1, Name = "x", Version = 0 }]))
            .Throws<NotSupportedException>();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task PostgreSql_BulkMerge_SetBased_RoundTrip()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
        });
        await db.ExecuteAsync($"DROP TABLE IF EXISTS merge_entities");
        await db.MigrateAsync();

        long first = await db.BulkMergeAsync(
        [
            new MergeEntity { Id = 1, Name = "pg-a", Qty = 1 },
            new MergeEntity { Id = 2, Name = "pg-b", Qty = 2 },
        ]);
        await Assert.That(first).IsEqualTo(2);

        long second = await db.BulkMergeAsync(
        [
            new MergeEntity { Id = 1, Name = "pg-a-up", Qty = 11 },
            new MergeEntity { Id = 3, Name = "pg-c", Qty = 3 },
        ]);
        await Assert.That(second).IsEqualTo(2);

        var rows = (await db.GetAllAsync<MergeEntity>()).OrderBy(static r => r.Id).ToList();
        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[0].Name).IsEqualTo("pg-a-up");
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    [NotInParallel("ExtBulkTable")]
    public async Task MySql_BulkMerge_SetBased_RoundTrip()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
        });
        await db.ExecuteAsync($"DROP TABLE IF EXISTS merge_entities");
        await db.MigrateAsync();

        long first = await db.BulkMergeAsync(
        [
            new MergeEntity { Id = 1, Name = "my-a", Qty = 1 },
            new MergeEntity { Id = 2, Name = "my-b", Qty = 2 },
        ]);
        await Assert.That(first).IsEqualTo(2);

        long second = await db.BulkMergeAsync(
        [
            new MergeEntity { Id = 1, Name = "my-a-up", Qty = 11 },
            new MergeEntity { Id = 3, Name = "my-c", Qty = 3 },
        ]);
        await Assert.That(second).IsEqualTo(2);

        var rows = (await db.GetAllAsync<MergeEntity>()).OrderBy(static r => r.Id).ToList();
        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[0].Name).IsEqualTo("my-a-up");
    }
}

[Table("merge_entities")]
public partial class MergeEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
}

[Table("merge_gen_entities")]
public partial class MergeGenEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("qty")] public int Qty { get; set; }
}

[Table("versioned_merge_entities")]
public partial class VersionedMergeEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("version")][ConcurrencyCheck] public long Version { get; set; }
}
