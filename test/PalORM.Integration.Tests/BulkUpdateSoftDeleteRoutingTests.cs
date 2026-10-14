using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>B7（2026-10-12 审读重开）：BulkUpdateAsync 软删实体的自动路由放开。
/// <para><b>放开的依据</b>：逐条与单语句批量对软删实体的 SET 列集同源（生成器 UpdateColumns，
/// 含 deleted_at——两路径都从实体绑定该列），行为逐位等价（本组三方言实测一致，含恒逐条的
/// SQLite 基线）。原"语义待厘清"排除纯属性能损失（软删实体被强制 N 次往返）。
/// 真实契约：调用方须在实体上携带自身 deleted_at（置 null 即复活该行，两路径同语义）。</para>
/// <para><b>本组用例锁定</b>：软删行携带原值时如实保留、活跃行更新后默认过滤仍可见。</para>
/// <para><b>串行槽</b>：与全库真库测试共享 "ExtBulkTable" 槽（建表竞态，同
/// BulkUpdateTenantRoutingTests 的登记理由）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class BulkUpdateSoftDeleteRoutingTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BulkUpdate_SoftDeletedRow_UpdatedDeletedAtUntouched()
        => await RunAsync(await TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkUpdate_SoftDeletedRow_UpdatedDeletedAtUntouched()
        => await RunAsync(await TestDb.MySqlAsync());

    [Test]
    public async Task Sqlite_BulkUpdate_SoftDeletedRow_UpdatedDeletedAtUntouched()
        => await RunAsync(await TestDb.SqliteAsync());

    /// <summary>三行：两行活跃、一行已软删（deleted_at 已置值）。BulkUpdate 全部三行——
    /// 断言受影响行数恰为 3（软删行同样被更新，两路径一致）、deleted_at 值原样保留
    /// （批量 SET 不含该列）、活跃行经默认过滤查询仍可见。</summary>
    private static async Task RunAsync<TProvider>(DataSession<TProvider> db)
        where TProvider : IDbProvider
    {
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_bulk_sd_upd");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_bulk_sd_upd (id BIGINT PRIMARY KEY, label VARCHAR(32) NOT NULL, deleted_at VARCHAR(64))");
            await db.ExecuteAsync(
                $"INSERT INTO palorm_bulk_sd_upd (id, label, deleted_at) VALUES ({1L}, {"one-seed"}, {null})");
            await db.ExecuteAsync(
                $"INSERT INTO palorm_bulk_sd_upd (id, label, deleted_at) VALUES ({2L}, {"two-seed"}, {null})");
            await db.ExecuteAsync(
                $"INSERT INTO palorm_bulk_sd_upd (id, label, deleted_at) VALUES ({3L}, {"three-seed"}, {"2026-10-12T00:00:00Z"})");

            // 实体按真实契约携带自身 deleted_at（两路径的 SET 都从实体绑定该列——
            // SQLite 恒逐条与 PG/MySQL 批量对此逐位等价，本组三方言实测一致）：
            // 活跃行 null、软删行保留原值
            var rows = new List<SoftDelUpdEntity>
            {
                new() { Id = 1, Label = "one-upd", DeletedAt = null },
                new() { Id = 2, Label = "two-upd", DeletedAt = null },
                new() { Id = 3, Label = "three-upd", DeletedAt = "2026-10-12T00:00:00Z" },
            };
            long affected = await db.BulkUpdateAsync(rows);
            await Assert.That(affected).IsEqualTo(3L);

            // 活跃行更新后经默认过滤（排除软删）仍可见——必须在 IgnoreFilters() 之前断言：
            // 该方法就地改写会话过滤状态（ITM-568 门禁内），之后的查询恒跨过滤
            List<SoftDelUpdEntity> visible = await db.From<SoftDelUpdEntity>().ToListAsync();
            await Assert.That(visible.Count).IsEqualTo(2);

            // 软删行的 deleted_at 原样保留（实体携带的值如实写回）且 label 已更新（跨过滤直查）
            db.IgnoreFilters();
            SoftDelUpdEntity? check = await db.From<SoftDelUpdEntity>()
                .Where($"id = {3L}").FirstOrDefaultAsync();
            await Assert.That(check!.Label).IsEqualTo("three-upd");
            await Assert.That(check.DeletedAt).IsEqualTo("2026-10-12T00:00:00Z");
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_bulk_sd_upd");
            await db.DisposeAsync();
        }
    }
}

[Table("palorm_bulk_sd_upd")]
[SoftDelete]
internal sealed partial class SoftDelUpdEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("label")]
    public string Label { get; set; } = "";

    [Column("deleted_at")]
    public string? DeletedAt { get; set; }
}
