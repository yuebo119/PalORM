using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>B8（2026-10-01 全 API 逐项轮）：BulkUpdateAsync 租户实体的自动路由放开。
/// <para><b>语义现状（放开的依据）</b>：逐条池化路径（ExecuteBulkUpdatePooledAsync）与
/// 单语句批量内核（ExecuteBulkUpdateBatchesAsync）都向 UPDATE 追加租户过滤
/// （<c>AND tenant_id = @__tenant0</c> + 池尾租户参数），两路径对租户实体的行为一致；
/// 放开自动路由（原条件显式排除 HasTenantFilter）是纯性能改善（N 次往返 → 1 次），
/// 不改变任何跨租户可见性。</para>
/// <para><b>本组用例锁定</b>：三方言下"租户会话的 BulkUpdateAsync 只影响本租户行、
/// 跨租户行零触碰"的不变式——路由切换前后都必须满足（改前逐条路径绿、改后批量路径绿）。
/// SQLite 恒走逐条（路由条件按方言排除）；PG/MySQL 满足 entities.Count &gt; 1 后走单语句。</para>
/// <para><b>串行槽</b>：与全库真库测试共享 "ExtBulkTable" 槽（MigrateAsync 建全局注册表集，
/// 独立槽名会让本组与其它测试并行、触发 check-then-act 的 42P07 建表竞态）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class BulkUpdateTenantRoutingTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BulkUpdate_TenantScoped_CrossTenantRowUntouched()
        => await RunAsync(await TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkUpdate_TenantScoped_CrossTenantRowUntouched()
        => await RunAsync(await TestDb.MySqlAsync());

    [Test]
    public async Task Sqlite_BulkUpdate_TenantScoped_CrossTenantRowUntouched()
        => await RunAsync(await TestDb.SqliteAsync());

    /// <summary>两租户各一行；租户 1 会话对"本租户行 + 跨租户行"两个实体执行 BulkUpdate——
    /// 断言受影响行数恰为 1（仅本租户行）且跨租户行值未被触碰。
    /// 行数为 2 即租户过滤缺失（单语句路径 WHERE 未加 tenant_id，或逐条路径丢失后缀）。</summary>
    private static async Task RunAsync<TProvider>(DataSession<TProvider> db)
        where TProvider : IDbProvider
    {
        try
        {
            // 标识符写字面量（值才进 FormattableString 洞）——M2-2 教训口径
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_bulk_tenant_upd");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_bulk_tenant_upd (id INT PRIMARY KEY, tenant_id INT NOT NULL, label VARCHAR(32) NOT NULL)");
            await db.ExecuteAsync(
                $"INSERT INTO palorm_bulk_tenant_upd (id, tenant_id, label) VALUES ({1L}, {1L}, {"mine-seed"})");
            await db.ExecuteAsync(
                $"INSERT INTO palorm_bulk_tenant_upd (id, tenant_id, label) VALUES ({2L}, {2L}, {"theirs-seed"})");

            db.WithTenant(1L);
            var rows = new List<TenantUpdEntity>
            {
                new() { Id = 1, TenantId = 1, Label = "mine-upd" },
                new() { Id = 2, TenantId = 2, Label = "theirs-hacked" },
            };
            long affected = await db.BulkUpdateAsync(rows);
            await Assert.That(affected).IsEqualTo(1);

            db.IgnoreFilters();
            var mine = await db.GetAsync<TenantUpdEntity>(1L);
            var theirs = await db.GetAsync<TenantUpdEntity>(2L);
            await Assert.That(mine!.Label).IsEqualTo("mine-upd");
            await Assert.That(theirs!.Label).IsEqualTo("theirs-seed");
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_bulk_tenant_upd");
            await db.DisposeAsync();
        }
    }
}

[Table("palorm_bulk_tenant_upd")]
[TenantAware]
internal sealed partial class TenantUpdEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("tenant_id")]
    public long TenantId { get; set; }

    [Column("label")]
    public string Label { get; set; } = "";
}
