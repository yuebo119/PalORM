using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// 独立审计 2026-09-19 M3-5（T7）——默认过滤路由的<b>行为</b>防线，与
/// ArchitectureInvariantTests 的源码扫描互补：扫描挡"结构消失"，本测试挡"结构在但行为漂移"。
/// 行为契约：软删实体 + 租户会话下，受过滤入口与等价手写过滤 SQL 产出<b>同一结果集</b>——
/// 过滤被绕过（OrWhere 穿透/路由断裂/条件写反）即刻表现为结果集差。
/// </summary>
[NotInParallel("DefaultFilterBehavior")]
public sealed class DefaultFilterBehaviorTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

    private static async Task SeedAsync(DataSession<SqliteProvider> db)
    {
        // 手写 DDL（SessionBatchTests 先例）：MigrateAsync 无参版会遍历注册表全部实体，
        // 本测试程序集含旧版片段实体（Mutable/DuplicateFragment）无方言 DDL 会先炸
        await db.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS filter_probe (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, tenant_id TEXT NOT NULL, deleted_at TEXT NULL)");
        // 覆盖四象限：软删×租户——只有"未删 + 本租户"应可见
        await db.InsertAsync(new FilterProbe { Name = "keep-a", TenantId = "a", DeletedAt = null });
        await db.InsertAsync(new FilterProbe { Name = "del-a", TenantId = "a", DeletedAt = DateTime.UtcNow });
        await db.InsertAsync(new FilterProbe { Name = "keep-b", TenantId = "b", DeletedAt = null });
        await db.InsertAsync(new FilterProbe { Name = "del-b", TenantId = "b", DeletedAt = DateTime.UtcNow });
    }

    [Test]
    public async Task GetAllAsync_MatchesHandwrittenFilteredSql()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        db.WithTenant("a");

        var viaOrm = await db.GetAllAsync<FilterProbe>();
        var viaRaw = await db.QueryAsync<FilterProbe>(
            $"SELECT Id, name, tenant_id, deleted_at FROM filter_probe WHERE tenant_id = {"a"} AND deleted_at IS NULL ORDER BY Id");

        await Assert.That(string.Join(",", viaOrm.Select(static x => x.Name))).IsEqualTo("keep-a");
        await Assert.That(string.Join(",", viaRaw.Select(static x => x.Name))).IsEqualTo("keep-a");
    }

    [Test]
    public async Task OrWhere_CannotBypassDefaultFilters()
    {
        // ITM-401 的行为面：用户 OR 组被括号隔离，无法与默认过滤同层
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        db.WithTenant("a");

        var rows = await db.From<FilterProbe>()
            .OrWhere($"Name = {"del-a"}").ToListAsync();

        // OR 只作用于用户条件组；软删 + 租户行仍被排除
        await Assert.That(rows.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CountAsync_MatchesFilteredRowCount()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        db.WithTenant("b");

        long count = await db.CountAsync<FilterProbe>();
        var list = await db.From<FilterProbe>().ToListAsync();

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list[0].Name).IsEqualTo("keep-b");
    }
}

[SoftDelete]
[TenantAware]
[Table("filter_probe")]
internal sealed partial class FilterProbe
{
    [Key]
    public long Id { get; set; }
    [Column("name")]
    public string Name { get; set; } = "";
    [Column("tenant_id")]
    public string TenantId { get; set; } = "";
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
