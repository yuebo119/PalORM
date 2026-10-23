using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>2026-10-22 终审 P1-1：TenantAware 实体在 PK/tenant 之外没有可 UPSERT 列时，
/// 生成器发 <c>ON CONFLICT (pk) DO NOTHING RETURNING …</c>；租户护栏曾在 RETURNING 前
/// 强插 WHERE，产出 <c>DO NOTHING WHERE …</c> 语法错误。DO NOTHING 不更新行，守卫应跳过。</summary>
public sealed class DoNothingTenantGuardTests
{
    [Test]
    public async Task SaveAsync_OnlyIgnoreOnInsertColumns_DoesNotEmitIllegalDoNothingWhere()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = "Data Source=:memory:"
        });
        await db.MigrateAsync();

        db.WithTenant(7L);
        var saved = await db.SaveAsync(new DoNothingTenantProbe { Id = 1, TenantId = 7, Note = null });
        await Assert.That(saved.Id).IsEqualTo(1L);
        long count = await db.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM do_nothing_tenant_probe WHERE Id = 1 AND tenant_id = 7");
        await Assert.That(count).IsEqualTo(1L);

        // 跨租户同主键：DO NOTHING 静默跳过，原行租户不变
        db.WithTenant(8L);
        await db.SaveAsync(new DoNothingTenantProbe { Id = 1, TenantId = 8, Note = "other" });
        long tenant = await db.ScalarAsync<long>(
            $"SELECT tenant_id FROM do_nothing_tenant_probe WHERE Id = 1");
        await Assert.That(tenant).IsEqualTo(7L);
    }
}

[Table("do_nothing_tenant_probe")]
[TenantAware]
public sealed class DoNothingTenantProbe
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [IgnoreOnInsert, Column("note")] public string? Note { get; set; }
}
