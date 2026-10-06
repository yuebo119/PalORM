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
        // B171 探针自建自清：不用 MigrateAsync（它迁移全部已注册实体，CI 并发下与其他
        // 用例的迁移撞建表竞态 42P07 实测）——本探针只需自己的表。列名对齐生成器命名：
        // 主键无 [Column] 按属性名发射带引号 "Id"（小写 id 在 PG 大小写敏感下 42703 实测）
        await sessionA.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS tenant_upsert_dialect_probe (\"Id\" BIGINT PRIMARY KEY, tenant_id BIGINT NOT NULL, value BIGINT NOT NULL)");
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
        // B171 探针自建自清 + 生成器命名对齐（同 PG 侧注释；MySQL 引号形态经反引号但
        // ExecuteAsync 的 FormattableString 直接透传，双引号在 MySQL 默认模式下非标识符引用——
        // 故 MySQL 侧 DDL 用反引号形态）
        await sessionA.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS tenant_upsert_dialect_probe (`Id` BIGINT PRIMARY KEY, tenant_id BIGINT NOT NULL, value BIGINT NOT NULL)");
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
