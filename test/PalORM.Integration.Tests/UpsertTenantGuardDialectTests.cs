using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// UPSERT 租户护栏的三方言收口（2026-10-06，Core 侧 UpsertTenantGuardTests 的真库配套）。
/// 覆盖两条方言手术路：PG RETURNING 前插 WHERE（单行 Save + BulkMerge 批路径）与
/// MySQL ODKU 赋值项 IF 守卫。行为契约：跨租户同键冲突更新静默跳过，他租户行原样保留。
/// </summary>
public sealed class UpsertTenantGuardDialectTests
{
    [Test]
    public async Task Pg_SaveAsync_CrossTenantSameKey_DoesNotOverwriteForeignRow()
    {
        await using var sessionA = await TestDb.PostgreSqlAsync();
        await sessionA.MigrateAsync();
        await sessionA.ExecuteAsync($"DELETE FROM tenant_upsert_dialect_probe");

        sessionA.WithTenant(1);
        await sessionA.SaveAsync(new TenantUpsertDialectProbe { Id = 1, TenantId = 1, Value = 100 });

        await using var sessionB = await TestDb.PostgreSqlAsync();
        sessionB.WithTenant(2);
        await sessionB.SaveAsync(new TenantUpsertDialectProbe { Id = 1, TenantId = 2, Value = 999 });
        await sessionB.BulkMergeAsync([new TenantUpsertDialectProbe { Id = 2, TenantId = 2, Value = 200 }]);

        TenantUpsertDialectProbe? row = await sessionA.GetAsync<TenantUpsertDialectProbe>(1);
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.TenantId).IsEqualTo(1);
        await Assert.That(row.Value).IsEqualTo(100);
    }

    [Test]
    public async Task MySql_SaveAsync_CrossTenantSameKey_DoesNotOverwriteForeignRow()
    {
        await using var sessionA = await TestDb.MySqlAsync();
        await sessionA.MigrateAsync();
        await sessionA.ExecuteAsync($"DELETE FROM tenant_upsert_dialect_probe");

        sessionA.WithTenant(1);
        await sessionA.SaveAsync(new TenantUpsertDialectProbe { Id = 1, TenantId = 1, Value = 100 });

        await using var sessionB = await TestDb.MySqlAsync();
        sessionB.WithTenant(2);
        await sessionB.SaveAsync(new TenantUpsertDialectProbe { Id = 1, TenantId = 2, Value = 999 });
        await sessionB.BulkMergeAsync([new TenantUpsertDialectProbe { Id = 2, TenantId = 2, Value = 200 }]);

        TenantUpsertDialectProbe? row = await sessionA.GetAsync<TenantUpsertDialectProbe>(1);
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.TenantId).IsEqualTo(1);
        await Assert.That(row.Value).IsEqualTo(100);
    }
}

#region Test Entities
[TenantAware]
[Table("tenant_upsert_dialect_probe")]
public partial class TenantUpsertDialectProbe
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("value")] public long Value { get; set; }
}
#endregion
