using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// UPSERT 租户护栏行为契约（2026-10-06，10-05 审计 P2"UPSERT 路径缺租户护栏"的锁定测试）。
/// 不变式：带租户会话的 SaveAsync/BulkMergeAsync 冲突更新分支只允许改写本租户的行——
/// 与 UpdateAsync 的租户条件对称（同一行 Update 被护栏挡住而 UPSERT 覆盖即隔离漏洞）。
/// 修复前：SaveAsync 跨租户同键覆盖他租户行（红）；修复后：冲突更新 WHERE 不命中即静默跳过（绿）。
/// MySQL 赋值项 IF 守卫与 PG RETURNING 前插 WHERE 由集成测试覆盖（本文件锁 SQLite 语义与契约）。
/// </summary>
internal sealed class UpsertTenantGuardTests
{
    [Test]
    public async Task SaveAsync_CrossTenantSameKey_DoesNotOverwriteForeignRow()
    {
        using SqliteConnection keeper = await OpenKeeperAsync(Guid.NewGuid().ToString("N"));

        await using DataSession<SqliteProvider> sessionA = await CreateSessionAsync(keeper, 1);
        TenantUpsertProbe original = await sessionA.SaveAsync(new TenantUpsertProbe { Id = 1, TenantId = 1, Value = 100 });

        await using DataSession<SqliteProvider> sessionB = await CreateSessionAsync(keeper, 2);
        await sessionB.SaveAsync(new TenantUpsertProbe { Id = 1, TenantId = 2, Value = 999 });

        var (tenantId, value) = await ReadRow(keeper, 1);
        await Assert.That(tenantId).IsEqualTo(1);
        await Assert.That(value).IsEqualTo(100);
        _ = original;
    }

    [Test]
    public async Task BulkMerge_CrossTenantSameKey_DoesNotOverwriteForeignRow()
    {
        using SqliteConnection keeper = await OpenKeeperAsync(Guid.NewGuid().ToString("N"));

        await using DataSession<SqliteProvider> sessionA = await CreateSessionAsync(keeper, 1);
        await sessionA.SaveAsync(new TenantUpsertProbe { Id = 1, TenantId = 1, Value = 100 });

        await using DataSession<SqliteProvider> sessionB = await CreateSessionAsync(keeper, 2);
        await sessionB.BulkMergeAsync([new TenantUpsertProbe { Id = 1, TenantId = 2, Value = 777 }]);

        var (tenantId, value) = await ReadRow(keeper, 1);
        await Assert.That(tenantId).IsEqualTo(1);
        await Assert.That(value).IsEqualTo(100);
    }

    [Test]
    public async Task SaveAsync_SameTenant_StillUpdates()
    {
        using SqliteConnection keeper = await OpenKeeperAsync(Guid.NewGuid().ToString("N"));

        await using DataSession<SqliteProvider> sessionA = await CreateSessionAsync(keeper, 1);
        await sessionA.SaveAsync(new TenantUpsertProbe { Id = 1, TenantId = 1, Value = 100 });
        await sessionA.SaveAsync(new TenantUpsertProbe { Id = 1, TenantId = 1, Value = 150 });

        var (tenantId, value) = await ReadRow(keeper, 1);
        await Assert.That(tenantId).IsEqualTo(1);
        await Assert.That(value).IsEqualTo(150);
    }

    private static async Task<(long TenantId, long Value)> ReadRow(SqliteConnection keeper, long id)
    {
        await using SqliteCommand cmd = keeper.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, value FROM tenant_upsert_probe WHERE id = 1";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"row {id} vanished (tenant guard bypassed via delete?).");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync(SqliteConnection keeper, long tenantId)
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = keeper.ConnectionString,
            MaxRetries = 0,
            CircuitBreakerThreshold = 0,
        });
        session.WithTenant(tenantId);
        return session;
    }

    private static async Task<SqliteConnection> OpenKeeperAsync(string tag)
    {
        var keeper = new SqliteConnection($"Data Source=tenant_upsert_guard_{tag};Mode=Memory;Cache=Shared");
        await keeper.OpenAsync();
        await using SqliteCommand cmd = keeper.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE tenant_upsert_probe " +
            "(id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, value INTEGER NOT NULL)";
        await cmd.ExecuteNonQueryAsync();
        return keeper;
    }
}

#region Test Entities
[TenantAware]
[Table("tenant_upsert_probe")]
internal sealed partial class TenantUpsertProbe
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("value")] public long Value { get; set; }
}
#endregion
