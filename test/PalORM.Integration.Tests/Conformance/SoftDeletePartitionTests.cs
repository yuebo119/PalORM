namespace PalORM.Integration.Tests.Conformance;

/// <summary>L4 默认过滤不变式（方案 §4.3，v5 修正版）。
/// <para><b>与 v4 设计的差别</b>：全量期望来自裸 ADO（Hardcoded 表名列名），不来自
/// <c>IgnoreFilters()</c>；三分支各用独立会话，因为 <c>IgnoreFilters</c> 在当前会话内
/// 没有公开复位 API（DataSession.cs:497-499）；并且断言"期望已删集非空"，
/// 否则种子不含已删行时前两条在空集上恒真。</para></summary>
public sealed class SoftDeletePartitionTests
{
    private static async ValueTask<SharedSqliteDb> SeedAsync(CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("soft_delete_partition", ct);
        try
        {
            await using DataSession<PalORM.Sqlite.SqliteProvider> db = await shared.OpenSessionAsync(ct);

            // 有效行：deleted_at 为 NULL
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive-1" }, ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive-2" }, ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive-3" }, ct);
            // 已删行：deleted_at 非空
            await db.InsertAsync(new CfSoftDeleteEntity
            {
                Name = "dead-1",
                DeletedAt = "2026-01-01T00:00:00"
            }, ct);
            await db.InsertAsync(new CfSoftDeleteEntity
            {
                Name = "dead-2",
                DeletedAt = "2026-01-02T00:00:00"
            }, ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    /// <summary>四条判据：有效集相等、已删集相等、不交、期望非空。</summary>
    private static async Task AssertPartitionAsync(
        SharedSqliteDb shared,
        IReadOnlyCollection<long> actualAlive,
        CancellationToken ct = default)
    {
        (HashSet<long> expectedAlive, HashSet<long> expectedDead) =
            await shared.ReadSoftDeletePartitionAsync(ct);

        // 期望侧自检：种子必须含已删行，否则相等断言在空集上恒真
        await Assert.That(expectedDead.Count).IsGreaterThan(0);

        HashSet<long> actualAliveKeys = [.. actualAlive];
        await Assert.That(actualAliveKeys.SetEquals(expectedAlive)).IsTrue();
        // 不交：同一行不能同时出现在有效集与已删集
        await Assert.That(actualAliveKeys.Overlaps(expectedDead)).IsFalse();
    }

    /// <summary>已删集本身也必须与裸 ADO 期望相等（独立会话，IgnoreFilters 单用）。</summary>
    private static async Task AssertDeadSetAsync(
        SharedSqliteDb shared, IReadOnlyCollection<long> actualDead, CancellationToken ct = default)
    {
        (_, HashSet<long> expectedDead) = await shared.ReadSoftDeletePartitionAsync(ct);
        await Assert.That(actualDead.ToHashSet().SetEquals(expectedDead)).IsTrue();
    }

    [Test]
    public async Task GetAllAsync_OnSecondInjectionPath_EqualsAliveSet()
    {
        // 软删过滤有两条运行期注入路径：From<T>() 用共享 ClauseNode（DataSession.Crud.cs），
        // GetAsync/GetAllAsync/CountAsync 用 GetDefaultFilterForms 的缓存条件串。
        // 两条路径必须语义一致，本用例锁第二条。
        await using SharedSqliteDb shared = await SeedAsync();
        await using DataSession<PalORM.Sqlite.SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfSoftDeleteEntity> rows = await db.GetAllAsync<CfSoftDeleteEntity>();
        await AssertPartitionAsync(shared, rows.Select(e => e.Id).ToList());
    }

    [Test]
    public async Task CountAsync_OnSecondInjectionPath_CountsOnlyAliveRows()
    {
        await using SharedSqliteDb shared = await SeedAsync();
        await using DataSession<PalORM.Sqlite.SqliteProvider> db = await shared.OpenSessionAsync();

        (HashSet<long> expectedAlive, _) = await shared.ReadSoftDeletePartitionAsync();
        long count = await db.CountAsync<CfSoftDeleteEntity>();

        await Assert.That(expectedAlive.Count).IsGreaterThan(0);
        await Assert.That(count).IsEqualTo((long)expectedAlive.Count);
    }

    [Test]
    public async Task IgnoreFilters_OnSecondInjectionPath_ReturnsDeletedRows()
    {
        // GetAllAsync/CountAsync 走 GetDefaultFilterForms 的第二注入路径（与 From 的静态 ClauseNode 不是同一处）。
        // IgnoreFilters 必须对两条路径同样生效，否则过滤契约在两条路径间不一致。
        await using SharedSqliteDb shared = await SeedAsync();
        await using DataSession<PalORM.Sqlite.SqliteProvider> db = await shared.OpenSessionAsync();

        (HashSet<long> expectedAlive, HashSet<long> expectedDead) = await shared.ReadSoftDeletePartitionAsync();
        await Assert.That(expectedDead.Count).IsGreaterThan(0);

        List<CfSoftDeleteEntity> all = await db.IgnoreFilters().GetAllAsync<CfSoftDeleteEntity>();
        long allCount = await db.CountAsync<CfSoftDeleteEntity>();

        await Assert.That(all.Count).IsEqualTo(expectedAlive.Count + expectedDead.Count);
        await Assert.That(allCount).IsEqualTo((long)all.Count);
    }

    [Test]
    public async Task PlainQuery_AliveSetEqualsRawAdoExpectation()
    {
        await using SharedSqliteDb shared = await SeedAsync();
        await using DataSession<PalORM.Sqlite.SqliteProvider> db = await shared.OpenSessionAsync();
        await using DataSession<PalORM.Sqlite.SqliteProvider> deadSession = await shared.OpenSessionAsync();

        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>().ToListAsync();
        List<CfSoftDeleteEntity> deadRows = await deadSession.IgnoreFilters()
            .From<CfSoftDeleteEntity>()
            .Where($"deleted_at IS NOT NULL")
            .ToListAsync();

        await Assert.That(rows.Count).IsGreaterThan(0);
        await AssertPartitionAsync(shared, rows.Select(e => e.Id).ToList());
        await AssertDeadSetAsync(shared, deadRows.Select(e => e.Id).ToList());
    }

    [Test]
    public async Task OrWhereChain_DoesNotSwallowDefaultFilter()
    {
        await using SharedSqliteDb shared = await SeedAsync();
        await using DataSession<PalORM.Sqlite.SqliteProvider> db = await shared.OpenSessionAsync();

        // 两个分支的并集覆盖全部行（name 非 NULL），因此结果应等于有效集。
        // ITM-401 的根因正是 OR 优先级把默认过滤吞进分支，这里直接探测该形态。
        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>()
            .Where($"name <> 'zzz'")
            .OrWhere($"name = 'zzz'")
            .ToListAsync();

        await AssertPartitionAsync(shared, rows.Select(e => e.Id).ToList());
    }

    [Test]
    public async Task WhereIn_AllKeys_EqualsAliveSet()
    {
        await using SharedSqliteDb shared = await SeedAsync();
        await using DataSession<PalORM.Sqlite.SqliteProvider> db = await shared.OpenSessionAsync();

        (HashSet<long> expectedAlive, HashSet<long> expectedDead) = await shared.ReadSoftDeletePartitionAsync();
        long[] allKeys = [.. expectedAlive, .. expectedDead];

        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>()
            .WhereIn(e => e.Id, allKeys)
            .ToListAsync();

        // 键集里含已删行主键，但默认过滤必须把它们挡在外面
        await AssertPartitionAsync(shared, rows.Select(e => e.Id).ToList());
    }

    [Test]
    public async Task WhereIn_OverFiveHundredValues_StillHonorsDefaultFilter()
    {
        await using SharedSqliteDb shared = await SeedAsync();
        await using DataSession<PalORM.Sqlite.SqliteProvider> db = await shared.OpenSessionAsync();

        (HashSet<long> expectedAlive, HashSet<long> expectedDead) = await shared.ReadSoftDeletePartitionAsync();
        // 501 个值触发 WhereIn 切批（阈值 500），骨架从 1 起算，覆盖种子主键与大量不存在的键
        long[] keys = [.. Enumerable.Range(1, 501).Select(i => (long)i)];
        await Assert.That(expectedDead.Count).IsGreaterThan(0);

        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>()
            .WhereIn(e => e.Id, keys)
            .ToListAsync();

        await AssertPartitionAsync(shared, rows.Select(e => e.Id).ToList());
    }

    [Test]
    public async Task CteSource_StillHonorsDefaultFilter()
    {
        await using SharedSqliteDb shared = await SeedAsync();
        await using DataSession<PalORM.Sqlite.SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfSoftDeleteEntity> rows = await db.From<CfSoftDeleteEntity>()
            .With("cf_soft_delete_cte", $"SELECT id, name, deleted_at FROM cf_soft_delete")
            .ToListAsync();

        await AssertPartitionAsync(shared, rows.Select(e => e.Id).ToList());
    }
}