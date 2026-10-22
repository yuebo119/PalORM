using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>2026-10-22 终审 P0-2：MySQL 非默认键 UPSERT 不得回填陈旧 LAST_INSERT_ID。
/// 租户护栏把 <c>pk = LAST_INSERT_ID(pk)</c> 包进 IF 假分支时不执行，随后的
/// <c>SELECT LAST_INSERT_ID()</c> 返回会话上一自增值；旧实现把它 set 回实体，
/// 第二次 SaveAsync 会覆盖该自增 ID 对应的无关行。</summary>
[NotInParallel("ExtBulkTable")]
public sealed class MySqlUpsertIdBackfillTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task SaveAsync_CrossTenantConflict_DoesNotBackfillStaleAutoIncrementId()
    {
        await using var db = await TestDb.MySqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS probe_mysql_upsert_id");
            await db.MigrateAsync();

            db.WithTenant(1L);
            MySqlUpsertIdEntity a = await db.InsertAsync(new MySqlUpsertIdEntity { TenantId = 1, Name = "a" });
            await db.InsertAsync(new MySqlUpsertIdEntity { TenantId = 1, Name = "b" });
            db.WithTenant(2L);
            MySqlUpsertIdEntity c = await db.InsertAsync(new MySqlUpsertIdEntity { TenantId = 2, Name = "c" });

            var hijack = new MySqlUpsertIdEntity { Id = a.Id, TenantId = 2, Name = "hijack" };
            await db.SaveAsync(hijack);
            await Assert.That(hijack.Id).IsEqualTo(a.Id);

            await db.SaveAsync(hijack);
            string? rowName = await db.ScalarAsync<string>(
                $"SELECT name FROM probe_mysql_upsert_id WHERE Id = {c.Id}");
            await Assert.That(rowName).IsEqualTo("c");
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS probe_mysql_upsert_id");
        }
    }
}

[Table("probe_mysql_upsert_id")]
[TenantAware]
public sealed class MySqlUpsertIdEntity
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}
