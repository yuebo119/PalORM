using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>
/// 生命周期与存在性 API 的行为契约（2026-11-06 新增四项）：
/// <see cref="DataSession{TProvider}.HardDeleteAsync{T}"/>、
/// <see cref="DataSession{TProvider}.RestoreAsync{T}"/>、
/// <see cref="DataSession{TProvider}.AnyAsync{T}(FormattableString?, CancellationToken)"/>、
/// <see cref="QueryBuilderExtensions.ExecuteDeleteAsync{T}"/>。
///
/// <para><b>为什么需要这一层</b>：Restore 与软删 Delete 是同一语义的两个方向，任一处条件写反
/// （IS NULL / IS NOT NULL）都表现为"删了再恢复拿不到"或"重复恢复刷新时间戳"；
/// ExecuteDelete 的软删改写如果退化成物理 DELETE，会静默销毁数据。这些都不是编译期能挡的。</para>
/// </summary>
[NotInParallel("LifecycleAndExistence")]
public sealed class LifecycleAndExistenceApiTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
        => await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = "Data Source=:memory:" });

    /// <summary>手写 DDL：MigrateAsync 无参版遍历整个注册表，测试程序集含旧版片段实体。</summary>
    private static async Task SeedAsync(DataSession<SqliteProvider> db)
    {
        await db.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS lifecycle_probe (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, status INTEGER NOT NULL)");
        await db.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS lifecycle_soft (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, status INTEGER NOT NULL, deleted_at TEXT NULL)");
        await db.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS lifecycle_tenant (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, tenant_id TEXT NOT NULL, deleted_at TEXT NULL)");
    }

    // ─── RestoreAsync ────────────────────────────────────────────

    [Test]
    public async Task RestoreAsync_BringsSoftDeletedRowBack()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleSoft { Name = "gone", Status = 0 });

        await Assert.That(await db.DeleteAsync<LifecycleSoft>(1L)).IsEqualTo(1);
        await Assert.That(await db.GetAsync<LifecycleSoft>(1L)).IsNull();

        await Assert.That(await db.RestoreAsync<LifecycleSoft>(1L)).IsEqualTo(1);

        LifecycleSoft? row = await db.GetAsync<LifecycleSoft>(1L);
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Name).IsEqualTo("gone");
    }

    [Test]
    public async Task RestoreAsync_IsIdempotent_SecondCallReturnsZero()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleSoft { Name = "x", Status = 0 });
        await db.DeleteAsync<LifecycleSoft>(1L);

        await Assert.That(await db.RestoreAsync<LifecycleSoft>(1L)).IsEqualTo(1);
        // 第二次：deleted_at 已是 NULL，WHERE ... IS NOT NULL 不匹配，返回 0（不刷新时间戳）
        await Assert.That(await db.RestoreAsync<LifecycleSoft>(1L)).IsEqualTo(0);
    }

    [Test]
    public async Task RestoreAsync_OnNonSoftDeleteEntity_ThrowsNotSupported()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);

        await Assert.That(async () => await db.RestoreAsync<LifecycleProbe>(1L))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task RestoreAsync_RespectsTenantScope()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleTenant { Name = "a-row", TenantId = "a" });
        db.WithTenant("a");
        await db.DeleteAsync<LifecycleTenant>(1L);

        // 跨租户会话恢复同一主键：租户条件不匹配，返回 0
        db.WithTenant("b");
        await Assert.That(await db.RestoreAsync<LifecycleTenant>(1L)).IsEqualTo(0);

        db.WithTenant("a");
        await Assert.That(await db.RestoreAsync<LifecycleTenant>(1L)).IsEqualTo(1);
    }

    // ─── HardDeleteAsync ────────────────────────────────────────

    [Test]
    public async Task HardDeleteAsync_RemovesSoftDeletedRowPhysically()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleSoft { Name = "purge", Status = 0 });
        await db.DeleteAsync<LifecycleSoft>(1L);

        await Assert.That(await db.HardDeleteAsync<LifecycleSoft>(1L)).IsEqualTo(1);

        // 行真的消失：IgnoreFilters 也看不到，恢复也无行可恢复
        long remaining = await db.IgnoreFilters().CountAsync<LifecycleSoft>();
        await Assert.That(remaining).IsEqualTo(0);
        await Assert.That(await db.RestoreAsync<LifecycleSoft>(1L)).IsEqualTo(0);
    }

    [Test]
    public async Task HardDeleteAsync_OnPlainEntity_MatchesDeleteSemantics()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleProbe { Name = "plain", Status = 0 });

        await Assert.That(await db.HardDeleteAsync<LifecycleProbe>(1L)).IsEqualTo(1);
        await Assert.That(await db.CountAsync<LifecycleProbe>()).IsEqualTo(0);
        // 重复硬删返回 0
        await Assert.That(await db.HardDeleteAsync<LifecycleProbe>(1L)).IsEqualTo(0);
    }

    // ─── AnyAsync ───────────────────────────────────────────────

    [Test]
    public async Task AnyAsync_ReflectsRowExistence()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);

        await Assert.That(await db.AnyAsync<LifecycleProbe>()).IsFalse();

        await db.InsertAsync(new LifecycleProbe { Name = "a", Status = 1 });
        await Assert.That(await db.AnyAsync<LifecycleProbe>()).IsTrue();
    }

    [Test]
    public async Task AnyAsync_HonorsWherePredicate()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleProbe { Name = "a", Status = 1 });

        await Assert.That(await db.AnyAsync<LifecycleProbe>($"Status = {1}")).IsTrue();
        await Assert.That(await db.AnyAsync<LifecycleProbe>($"Status = {2}")).IsFalse();
    }

    [Test]
    public async Task AnyAsync_HonorsSoftDeleteFilter()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleSoft { Name = "a", Status = 0 });

        await Assert.That(await db.AnyAsync<LifecycleSoft>()).IsTrue();
        await db.DeleteAsync<LifecycleSoft>(1L);

        // 已软删行不算"存在"（默认过滤生效）
        await Assert.That(await db.AnyAsync<LifecycleSoft>()).IsFalse();
        // 显式忽略过滤后可看见
        await Assert.That(await db.IgnoreFilters().AnyAsync<LifecycleSoft>()).IsTrue();
    }

    [Test]
    public async Task AnyAsync_HonorsTenantFilter()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleTenant { Name = "b-row", TenantId = "b" });

        db.WithTenant("a");
        await Assert.That(await db.AnyAsync<LifecycleTenant>()).IsFalse();

        db.WithTenant("b");
        await Assert.That(await db.AnyAsync<LifecycleTenant>()).IsTrue();
    }

    // ─── ExecuteDeleteAsync ─────────────────────────────────────

    [Test]
    public async Task ExecuteDeleteAsync_DeletesOnlyMatchingRows()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleProbe { Name = "keep", Status = 1 });
        await db.InsertAsync(new LifecycleProbe { Name = "drop", Status = 2 });

        int deleted = await db.From<LifecycleProbe>().Where($"Status = {2}").ExecuteDeleteAsync();

        await Assert.That(deleted).IsEqualTo(1);
        List<LifecycleProbe> rest = await db.From<LifecycleProbe>().ToListAsync();
        await Assert.That(rest.Count).IsEqualTo(1);
        await Assert.That(rest[0].Name).IsEqualTo("keep");
    }

    [Test]
    public async Task ExecuteDeleteAsync_OnSoftDeleteEntity_MarksInsteadOfDeleting()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleSoft { Name = "logical", Status = 0 });

        int affected = await db.From<LifecycleSoft>().Where($"Status = {0}").ExecuteDeleteAsync();

        await Assert.That(affected).IsEqualTo(1);
        // 行仍在（逻辑删除）：忽略过滤后可见且 deleted_at 已置值
        await Assert.That(await db.CountAsync<LifecycleSoft>()).IsEqualTo(0);
        LifecycleSoft? raw = await db.IgnoreFilters().GetAsync<LifecycleSoft>(1L);
        await Assert.That(raw).IsNotNull();
        await Assert.That(raw!.DeletedAt).IsNotNull();
    }

    [Test]
    public async Task ExecuteDeleteAsync_IgnoreFilters_StillHonorsSoftDeleteCondition()
    {
        // M6 回归：IgnoreFilters 下 ExecuteDeleteAsync 仍保留 deleted_at IS NULL（与 DeleteAsync 同语义），
        // 不刷新已删行的时间戳
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleSoft { Name = "deleted", Status = 0 });

        int first = await db.From<LifecycleSoft>().Where($"Status = {0}").ExecuteDeleteAsync();
        await Assert.That(first).IsEqualTo(1);

        int second = await db.IgnoreFilters().From<LifecycleSoft>()
            .Where($"Status = {0}").ExecuteDeleteAsync();
        await Assert.That(second).IsEqualTo(0)
            .Because("软删条件恒保留，IgnoreFilters 不刷新已删行");
    }

    [Test]
    public async Task ExecuteDeleteAsync_WithoutWhere_Throws()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);

        await Assert.That(async () => await db.From<LifecycleProbe>().ExecuteDeleteAsync())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ExecuteDeleteAsync_WithSetClause_Throws()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);

        await Assert.That(async () => await db.From<LifecycleProbe>()
                .Set(x => x.Name, "n")
                .Where($"Status = {1}")
                .ExecuteDeleteAsync())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ExecuteDeleteAsync_WithTakeOrSkip_Throws()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);

        await Assert.That(async () => await db.From<LifecycleProbe>()
                .Where($"Status = {1}")
                .Take(10)
                .ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task ExecuteDeleteAsync_WithSelectSplitQueryOrCache_Throws()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);

        // L2 锁定（c0409c3）：DELETE 拒绝 Select/AsSplitQuery/WithCache——原守卫只查 Take/Skip，
        // 这三个修饰符会被静默丢弃并扩大影响面。撤掉 HasIgnoredExecutionModifiers 复用即红。
        await Assert.That(async () => await db.From<LifecycleProbe>()
                .Where($"Status = {1}").Select(x => x.Status).ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
        await Assert.That(async () => await db.From<LifecycleProbe>()
                .Where($"Status = {1}").AsSplitQuery().ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
        await Assert.That(async () => await db.From<LifecycleProbe>()
                .Where($"Status = {1}").WithCache("probe").ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task ExecuteDeleteAsync_RespectsTenantScope()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleTenant { Name = "a-row", TenantId = "a" });
        await db.InsertAsync(new LifecycleTenant { Name = "b-row", TenantId = "b" });

        db.WithTenant("a");
        int affected = await db.From<LifecycleTenant>().Where($"name = {"b-row"}").ExecuteDeleteAsync();

        // 跨租户条件删不到任何行（租户条件自动附加）
        await Assert.That(affected).IsEqualTo(0);
        await Assert.That(await db.IgnoreFilters().CountAsync<LifecycleTenant>()).IsEqualTo(2);
    }

    /// <summary>DEC-1（ADR-X 裁决 A）锁定：IgnoreFilters 对写路径同样生效——写操作跳过租户条件。
    /// 这是当前唯一能清空租户过滤的入口（<c>WithTenant(null)</c> 被 ITM-532 拒绝），跨租户批量运维依赖它。
    /// 若改为「写侧恒租户过滤」，本用例会红，届时须同时提供跨租户写的显式出口。</summary>
    [Test]
    public async Task IgnoreFilters_WritePath_AlsoSkipsTenantFilter()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleTenant { Name = "a-row", TenantId = "a" });
        await db.InsertAsync(new LifecycleTenant { Name = "b-row", TenantId = "b" });

        db.WithTenant("a");
        // 正常：租户条件自动附加，删不到 b 的行（同 ExecuteDeleteAsync_RespectsTenantScope）
        await Assert.That(
            await db.From<LifecycleTenant>().Where($"name = {"b-row"}").ExecuteDeleteAsync())
            .IsEqualTo(0);

        // IgnoreFilters：写路径同样跳过租户条件 → 跨租户命中
        await Assert.That(
            await db.IgnoreFilters().From<LifecycleTenant>().Where($"name = {"b-row"}").ExecuteDeleteAsync())
            .IsEqualTo(1);

        // LifecycleTenant 是软删实体：ExecuteDeleteAsync 改写 deleted_at（行仍在）；
        // IgnoreFilters 下读得到全部行，可验证跨租户写确实命中了 b 的行
        List<LifecycleTenant> all = await db.IgnoreFilters().From<LifecycleTenant>().ToListAsync();
        await Assert.That(all.Count).IsEqualTo(2);
        await Assert.That(all.Single(r => r.Name == "b-row").DeletedAt).IsNotNull()
            .Because("b 的行被跨租户软删（正常租户条件下这次删除应为 0 行）");
    }

    [Test]
    public async Task ExecuteDeleteAsync_RejectsOrderGroupSkip()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);

        // 这些子句在 DELETE 构建输出里不存在，静默丢弃会扩大影响面，故构建期拒绝
        await Assert.That(async () => await db.From<LifecycleProbe>()
                .Where($"Status = {1}").OrderBy("Status").ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
        await Assert.That(async () => await db.From<LifecycleProbe>()
                .Where($"Status = {1}").GroupBy("Status").ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
        await Assert.That(async () => await db.From<LifecycleProbe>()
                .Where($"Status = {1}").Skip(1).ExecuteDeleteAsync())
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task AnyAsync_WithWhereAndSoftDeleteFilter_UsesComposedSql()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleSoft { Name = "a", Status = 1 });

        // 软删实体 + 用户条件：SQL 走 "WHERE <软删过滤> AND (<用户条件>) LIMIT 1"
        await Assert.That(await db.AnyAsync<LifecycleSoft>($"Status = {1}")).IsTrue();
        await Assert.That(await db.AnyAsync<LifecycleSoft>($"Status = {2}")).IsFalse();

        await db.DeleteAsync<LifecycleSoft>(1L);
        await Assert.That(await db.AnyAsync<LifecycleSoft>($"Status = {1}")).IsFalse();
    }

    [Test]
    public async Task AnyAsync_RepeatedCalls_ReuseCommandSlot()
    {
        await using DataSession<SqliteProvider> db = await CreateSessionAsync();
        await SeedAsync(db);
        await db.InsertAsync(new LifecycleProbe { Name = "a", Status = 1 });

        // 第 3 次同形态操作触发复用槽晋升，稳态走就地写值路径（Lessons B98 的惰性晋升）
        for (int i = 0; i < 5; i++)
            await Assert.That(await db.AnyAsync<LifecycleProbe>($"Status = {1}")).IsTrue();
    }
}

[Table("lifecycle_probe")]
internal sealed partial class LifecycleProbe
{
    [Key]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("status")]
    public int Status { get; set; }
}

[SoftDelete]
[Table("lifecycle_soft")]
internal sealed partial class LifecycleSoft
{
    [Key]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("status")]
    public int Status { get; set; }

    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}

[SoftDelete]
[TenantAware]
[Table("lifecycle_tenant")]
internal sealed partial class LifecycleTenant
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
