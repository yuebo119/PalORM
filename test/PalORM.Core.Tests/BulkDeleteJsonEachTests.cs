using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>E 方案（ADR-R，2026-10-25）：SQLite json_each 批删路径的锁定测试。
/// <para>覆盖：大键集单语句往返、软删路径、租户守卫、字符串主键回退 IN 形态的正确性、
/// 劣质键 fail-fast 原子性（json_each 单语句）、IN 形态的中途失败整批回滚（字符串主键
/// + 多批 + 批内坏键，顺带关掉删除侧原子性无锁定测试的缺口）、不存在键零影响。</para></summary>
internal sealed class BulkDeleteJsonEachTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=jsondel_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync(
            $"CREATE TABLE json_del_rows (Id INTEGER PRIMARY KEY, v INTEGER NOT NULL)");
        await session.ExecuteAsync(
            $"CREATE TABLE json_del_soft (Id INTEGER PRIMARY KEY, v INTEGER NOT NULL, deleted_at TEXT)");
        await session.ExecuteAsync(
            $"CREATE TABLE json_del_string (Id TEXT PRIMARY KEY, v INTEGER NOT NULL)");
        await session.ExecuteAsync(
            $"CREATE TABLE json_del_tenant (Id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, v INTEGER NOT NULL)");
        return session;
    }

    private static List<JsonDelRow> SeedRows(int count)
    {
        var rows = new List<JsonDelRow>(count);
        for (int i = 1; i <= count; i++)
            rows.Add(new JsonDelRow { Id = i, V = i });
        return rows;
    }

    [Test]
    public async Task NumericKeys_LargeList_DeletesAllInSingleStatement()
    {
        // 2500 键 > 旧 IN 形态任何单语句批宽（999 参数）——json_each 单参数无此约束
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.BulkInsertAsync(SeedRows(2500));

        var keys = new List<object>(2500);
        for (int i = 1; i <= 2500; i++) keys.Add((long)i);
        long deleted = await session.BulkDeleteAsync<JsonDelRow>(keys);

        await Assert.That(deleted).IsEqualTo(2500);
        await Assert.That((await session.GetAllAsync<JsonDelRow>()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task NumericKeys_PartialOverlap_DeletesOnlyMatching()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.BulkInsertAsync(SeedRows(100));

        long deleted = await session.BulkDeleteAsync<JsonDelRow>(new List<object> { 10L, 20L, 999_999L });

        await Assert.That(deleted).IsEqualTo(2);
        List<JsonDelRow> rest = await session.GetAllAsync<JsonDelRow>();
        await Assert.That(rest.Count).IsEqualTo(98);
        await Assert.That(rest.Any(static r => r.Id is 10 or 20)).IsFalse();
    }

    [Test]
    public async Task SoftDelete_MarksDeletedAt_AndHidesFromDefaultFilter()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var rows = new List<JsonDelSoftRow>(50);
        for (int i = 1; i <= 50; i++) rows.Add(new JsonDelSoftRow { Id = i, V = i });
        await session.BulkInsertAsync(rows);

        var keys = new List<object>(50);
        for (int i = 1; i <= 50; i++) keys.Add((long)i);
        long deleted = await session.BulkDeleteAsync<JsonDelSoftRow>(keys);

        await Assert.That(deleted).IsEqualTo(50);
        await Assert.That((await session.GetAllAsync<JsonDelSoftRow>()).Count).IsEqualTo(0);
        JsonDelSoftRow? soft = await session.IgnoreFilters().GetAsync<JsonDelSoftRow>(1);
        await Assert.That(soft).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(soft!.DeletedAt)).IsFalse();
    }

    [Test]
    public async Task TenantGuard_CrossTenantKey_IsNoOp()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        // 租户 2 的行（Insert 不过滤租户，实体自带 tenant_id 列值）
        await session.InsertAsync(new JsonDelTenantRow { Id = 1, TenantId = 2, V = 42 });

        DataSession<SqliteProvider> scoped = session.WithTenant(1);
        long deleted = await scoped.BulkDeleteAsync<JsonDelTenantRow>(new List<object> { 1L });

        await Assert.That(deleted).IsEqualTo(0);
        JsonDelTenantRow? foreign = await session.IgnoreFilters().GetAsync<JsonDelTenantRow>(1);
        await Assert.That(foreign).IsNotNull();
        await Assert.That(foreign!.V).IsEqualTo(42);
    }

    [Test]
    public async Task RogueKeyMidList_ThrowsFastAndNothingDeleted()
    {
        // json_each 形态：JSON 构建期 fail-fast（首键 long 定型形态，第 500 位混入字符串）
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.BulkInsertAsync(SeedRows(1000));

        var keys = new List<object>(1000);
        for (int i = 1; i <= 1000; i++) keys.Add((long)i);
        keys[499] = "not-a-number";

        Exception? actual = await Assert.ThrowsAsync<FormatException>(async () =>
            await session.BulkDeleteAsync<JsonDelRow>(keys));

        // ConvertDeleteKey 先于 JSON 构建抛 FormatException（"not-a-number" → Int64）——
        // 语义与 IN 形态一致：事务提交前抛出，全有或全无
        await Assert.That(actual).IsNotNull();
        await Assert.That((await session.GetAllAsync<JsonDelRow>()).Count).IsEqualTo(1000);
    }

    [Test]
    public async Task StringPk_FallsBackToInForm_StillCorrect()
    {
        // 字符串主键不进 json_each（首键非整型），走 IN 形态——正确性不回退
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var rows = new List<JsonDelStringRow>(2500);
        for (int i = 1; i <= 2500; i++) rows.Add(new JsonDelStringRow { Id = $"k{i}", V = i });
        await session.BulkInsertAsync(rows);

        var keys = new List<object>(2500);
        for (int i = 1; i <= 2500; i++) keys.Add($"k{i}");
        long deleted = await session.BulkDeleteAsync<JsonDelStringRow>(keys);

        await Assert.That(deleted).IsEqualTo(2500);
        await Assert.That((await session.GetAllAsync<JsonDelStringRow>()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task StringPk_MidBatchRogueKey_RollsBackAllBatches()
    {
        // IN 形态原子性锁定（关掉删除侧"中途失败整批回滚"无测试的缺口）：字符串主键
        // 2500 键 = 999/批 ×3 批；第 1500 位（第 2 批内）混入 long → 批内绑定抛出
        // → 事务回滚 → 首批已删的 999 行必须回来
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        var rows = new List<JsonDelStringRow>(2500);
        for (int i = 1; i <= 2500; i++) rows.Add(new JsonDelStringRow { Id = $"k{i}", V = i });
        await session.BulkInsertAsync(rows);

        var keys = new List<object>(2500);
        for (int i = 1; i <= 2500; i++) keys.Add($"k{i}");
        keys[1499] = 424242L;

        bool threw = false;
        try
        {
            await session.BulkDeleteAsync<JsonDelStringRow>(keys);
        }
        catch
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That((await session.GetAllAsync<JsonDelStringRow>()).Count).IsEqualTo(2500);
    }

    [Test]
    public async Task EmptyAndNonexistentKeys_ReturnZeroWithoutError()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await session.BulkInsertAsync(SeedRows(10));

        await Assert.That(await session.BulkDeleteAsync<JsonDelRow>(Array.Empty<object>())).IsEqualTo(0);
        await Assert.That(await session.BulkDeleteAsync<JsonDelRow>(new List<object> { 777_777L, 888_888L }))
            .IsEqualTo(0);
        await Assert.That((await session.GetAllAsync<JsonDelRow>()).Count).IsEqualTo(10);
    }
}

[Table("json_del_rows")]
internal sealed partial class JsonDelRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("v")] public int V { get; set; }
}

[Table("json_del_soft")]
[SoftDelete]
internal sealed partial class JsonDelSoftRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("v")] public int V { get; set; }
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}

[Table("json_del_string")]
internal sealed partial class JsonDelStringRow
{
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("v")] public int V { get; set; }
}

[Table("json_del_tenant")]
[TenantAware]
internal sealed partial class JsonDelTenantRow
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("tenant_id")] [Required] public long TenantId { get; set; }
    [Column("v")] public int V { get; set; }
}
