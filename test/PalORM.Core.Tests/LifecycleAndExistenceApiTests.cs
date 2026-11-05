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
