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

    [Test]
    public async Task BulkMerge_NonTenantSession_DoesNotMoveTenantId_RegardlessOfCacheWarmupOrder()
    {
        using SqliteConnection keeper = await OpenKeeperAsync(Guid.NewGuid().ToString("N"));

        // A：非租户会话首次调用，shape 缓存以 include-tenant_id 形态入缓存
        await using (DataSession<SqliteProvider> plainA = await CreatePlainSessionAsync(keeper))
        {
            await plainA.BulkMergeAsync([new TenantUpsertNonTenantProbe { Id = 1, TenantId = 1, Value = 100 }]);
            await plainA.BulkMergeAsync([new TenantUpsertNonTenantProbe { Id = 1, TenantId = 2, Value = 200 }]);
        }
        var (tenantA, valueA) = await ReadRowAsync(keeper, "tenant_upsert_non_tenant_probe", 1);

        // B：租户会话先预热 shape 缓存，再由非租户会话执行同一调用
        await using (DataSession<SqliteProvider> tenantB = await CreateSessionAsync(keeper, 1))
        {
            await tenantB.BulkMergeAsync([new TenantUpsertCacheProbe { Id = 1, TenantId = 1, Value = 100 }]);
        }
        await using (DataSession<SqliteProvider> plainB = await CreatePlainSessionAsync(keeper))
        {
            await plainB.BulkMergeAsync([new TenantUpsertCacheProbe { Id = 1, TenantId = 2, Value = 200 }]);
        }
        var (tenantBResult, valueB) = await ReadRowAsync(keeper, "tenant_upsert_cache_probe", 1);

        // 修复前：A=2（include 形态改写 tenant_id），B=1（exclude 形态偶然一致）——
        // 同一非租户操作的结果依赖进程内预热顺序。
        await Assert.That(tenantA).IsEqualTo(1);
        await Assert.That(valueA).IsEqualTo(200);
        await Assert.That(tenantBResult).IsEqualTo(1);
        await Assert.That(valueB).IsEqualTo(200);
    }

    private static async Task<(long TenantId, long Value)> ReadRowAsync(
        SqliteConnection keeper, string table, long id)
    {
        // 白名单表名 + 参数化主键：测试专用，避免把表名当 SQL 注入面（S2077/CA2100）。
        string sql = table switch
        {
            "tenant_upsert_probe" =>
                "SELECT tenant_id, value FROM tenant_upsert_probe WHERE id = $id",
            "tenant_upsert_non_tenant_probe" =>
                "SELECT tenant_id, value FROM tenant_upsert_non_tenant_probe WHERE id = $id",
            "tenant_upsert_cache_probe" =>
                "SELECT tenant_id, value FROM tenant_upsert_cache_probe WHERE id = $id",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "unknown test table"),
        };
        await using SqliteCommand cmd = keeper.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"row {id} vanished (tenant guard bypassed via delete?).");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<DataSession<SqliteProvider>> CreatePlainSessionAsync(SqliteConnection keeper)
        => await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = keeper.ConnectionString,
            MaxRetries = 0,
            CircuitBreakerThreshold = 0,
        });

    private static async Task<(long TenantId, long Value)> ReadRow(SqliteConnection keeper, long id)
        => await ReadRowAsync(keeper, "tenant_upsert_probe", id);

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
        cmd.CommandText =
            "CREATE TABLE tenant_upsert_non_tenant_probe " +
            "(id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, value INTEGER NOT NULL)";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText =
            "CREATE TABLE tenant_upsert_cache_probe " +
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

[TenantAware]
[Table("tenant_upsert_non_tenant_probe")]
internal sealed partial class TenantUpsertNonTenantProbe
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("value")] public long Value { get; set; }
}

[TenantAware]
[Table("tenant_upsert_cache_probe")]
internal sealed partial class TenantUpsertCacheProbe
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("value")] public long Value { get; set; }
}
#endregion
