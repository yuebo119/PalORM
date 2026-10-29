using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>事故族 4 与族 5：租户写作用域（ITM-599，6 条）与会话粘性（5 条）。
/// <para>族 4 判据：租户会话的写在影响行数与 SET 列两个面上都不得越过本租户。
/// 期望值来自裸 ADO 读出的 tenant_id 分布。</para>
/// <para>族 5 判据：契约与复位点。`IgnoreFilters` 在同会话内是粘性且无公开复位 API（契约），
/// 可观测的复位点只有会话归还（`DataSession.cs:835`）。</para></summary>
public sealed class IncidentTenantSessionTests
{
    private const string SelectTenant = """SELECT "Id", "tenant_id", "value" FROM cf_tenant""";

    private static async ValueTask<SharedSqliteDb> SeedTenantAsync(
        string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        try
        {
            await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
            await db.InsertAsync(new CfTenantEntity { TenantId = 1, Value = "own" }, ct);
            await db.InsertAsync(new CfTenantEntity { TenantId = 2, Value = "foreign" }, ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    private static async ValueTask<(long OwnId, long ForeignId)> FindTenantIdsAsync(
        SharedSqliteDb shared, CancellationToken ct = default)
    {
        Dictionary<long, (long TenantId, string Value)> rows = await ReadTenantRowsAsync(shared, ct);
        long ownId = rows.First(pair => pair.Value.Value == "own").Key;
        long foreignId = rows.First(pair => pair.Value.Value == "foreign").Key;
        return (ownId, foreignId);
    }

    private static async ValueTask<Dictionary<long, (long TenantId, string Value)>> ReadTenantRowsAsync(
        SharedSqliteDb shared, CancellationToken ct = default)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await shared.QueryRawAsync(SelectTenant, ct: ct);
        var map = new Dictionary<long, (long TenantId, string Value)>();
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            map[Convert.ToInt64(row["Id"], null)] = (
                Convert.ToInt64(row["tenant_id"], null),
                (string)row["value"]!);
        }
        return map;
    }

    // ── 族 4：租户写作用域（6 条）──────────────────────────

    [Test]
    public async Task Update_InTenantSession_DoesNotMoveRowToAnotherTenant()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("tenant_scope_1");
        (long ownId, _) = await FindTenantIdsAsync(shared);
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        db.WithTenant(1L);
        CfTenantEntity own = (await db.From<CfTenantEntity>().ToListAsync())[0];
        own.TenantId = 999;          // 尝试把行搬到别的租户
        own.Value = "moved-attempt";
        await db.UpdateAsync(own);

        Dictionary<long, (long TenantId, string Value)> rows = await ReadTenantRowsAsync(shared);
        await Assert.That(rows[ownId].TenantId).IsEqualTo(1L);
        await Assert.That(rows[ownId].Value).IsEqualTo("moved-attempt");
    }

    [Test]
    public async Task BulkUpdate_InTenantSession_DoesNotMoveRowToAnotherTenant()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("tenant_scope_2");
        (long ownId, _) = await FindTenantIdsAsync(shared);
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        db.WithTenant(1L);
        List<CfTenantEntity> ownRows = await db.From<CfTenantEntity>().ToListAsync();
        ownRows[0].TenantId = 999;
        ownRows[0].Value = "bulk-moved";
        await db.BulkUpdateAsync(ownRows);

        Dictionary<long, (long TenantId, string Value)> rows = await ReadTenantRowsAsync(shared);
        await Assert.That(rows[ownId].TenantId).IsEqualTo(1L);
        await Assert.That(rows[ownId].Value).IsEqualTo("bulk-moved");
    }

    [Test]
    public async Task Save_ConflictUpdateBranch_KeepsTenantId()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("tenant_scope_3");
        (long ownId, _) = await FindTenantIdsAsync(shared);
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        db.WithTenant(1L);
        CfTenantEntity upsert = new() { Id = ownId, TenantId = 999, Value = "upserted" };
        await db.SaveAsync(upsert);

        Dictionary<long, (long TenantId, string Value)> rows = await ReadTenantRowsAsync(shared);
        await Assert.That(rows[ownId].TenantId).IsEqualTo(1L);
        await Assert.That(rows[ownId].Value).IsEqualTo("upserted");
    }

    [Test]
    public async Task Update_InTenantSession_AffectsOnlyOwnRows()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("tenant_scope_4");
        (long ownId, long foreignId) = await FindTenantIdsAsync(shared);
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        db.WithTenant(1L);
        // 用主键直指另一租户的行：WHERE 里的租户过滤必须把它挡住
        int affected = await db.UpdateColumnsAsync(
            new CfTenantEntity { Id = foreignId, TenantId = 2, Value = "hijacked" }, e => e.Value);

        await Assert.That(affected).IsEqualTo(0);
        Dictionary<long, (long TenantId, string Value)> rows = await ReadTenantRowsAsync(shared);
        await Assert.That(rows[foreignId].Value).IsEqualTo("foreign");
        await Assert.That(rows[ownId].Value).IsEqualTo("own");
    }

    [Test]
    public async Task Delete_InTenantSession_DeletesOnlyOwnRows()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("tenant_scope_5");
        (_, long foreignId) = await FindTenantIdsAsync(shared);
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        db.WithTenant(1L);
        int affected = await db.DeleteAsync<CfTenantEntity>(foreignId);

        await Assert.That(affected).IsEqualTo(0);
        Dictionary<long, (long TenantId, string Value)> rows = await ReadTenantRowsAsync(shared);
        await Assert.That(rows.ContainsKey(foreignId)).IsTrue();
    }

    [Test]
    public async Task BulkDelete_InTenantSession_DeletesOnlyOwnRows()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("tenant_scope_6");
        (long ownId, long foreignId) = await FindTenantIdsAsync(shared);
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        db.WithTenant(1L);
        // 键集里同时给了本租户与别的租户的主键
        await db.BulkDeleteAsync<CfTenantEntity, long>([ownId, foreignId]);

        Dictionary<long, (long TenantId, string Value)> rows = await ReadTenantRowsAsync(shared);
        await Assert.That(rows.ContainsKey(ownId)).IsFalse();
        await Assert.That(rows.ContainsKey(foreignId)).IsTrue();
    }

    // ── 族 5：会话粘性（5 条）──────────────────────────────

    private static async ValueTask<SharedSqliteDb> SeedSoftDeleteAsync(string name, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(name, ct);
        try
        {
            await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive" }, ct);
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "dead", DeletedAt = "2026-01-01" }, ct);
            return shared;
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    [Test]
    public async Task IgnoreFilters_IsStickyWithinSession()
    {
        // 契约（DataSession.cs:497-499）：当前会话内没有公开复位 API。
        // 这条与 L4 的"三分支各用独立会话"互为对照：同会话复用会让后续查询继续忽略过滤。
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("session_sticky_1");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfSoftDeleteEntity> withFilters = await db.From<CfSoftDeleteEntity>().ToListAsync();
        await Assert.That(withFilters.Count).IsEqualTo(1);

        List<CfSoftDeleteEntity> afterIgnore = await db.IgnoreFilters().From<CfSoftDeleteEntity>().ToListAsync();
        await Assert.That(afterIgnore.Count).IsEqualTo(2);

        List<CfSoftDeleteEntity> again = await db.From<CfSoftDeleteEntity>().ToListAsync();
        await Assert.That(again.Count).IsEqualTo(2);
    }

    [Test]
    public async Task PoolRent_AfterReturn_FiltersRestored()
    {
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("session_sticky_2");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);

        DataSession<SqliteProvider> first = await pool.RentAsync();
        first.IgnoreFilters();
        long unfiltered = await first.CountAsync<CfSoftDeleteEntity>();
        await first.DisposeAsync();

        DataSession<SqliteProvider> second = await pool.RentAsync();
        long filtered = await second.CountAsync<CfSoftDeleteEntity>();
        await second.DisposeAsync();

        await Assert.That(unfiltered).IsEqualTo(2L);
        await Assert.That(filtered).IsEqualTo(1L);
    }

    [Test]
    public async Task PoolRent_AfterReturn_IgnoreFiltersResetTwice()
    {
        // 连续两轮租还：每轮都在租期内置瞬态状态，归还后必须各自复位
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("session_sticky_3");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);

        DataSession<SqliteProvider> first = await pool.RentAsync();
        first.IgnoreFilters();
        await first.DisposeAsync();

        DataSession<SqliteProvider> second = await pool.RentAsync();
        long filteredSecond = await second.CountAsync<CfSoftDeleteEntity>();
        second.IgnoreFilters();
        await second.DisposeAsync();

        DataSession<SqliteProvider> third = await pool.RentAsync();
        long filteredThird = await third.CountAsync<CfSoftDeleteEntity>();
        await third.DisposeAsync();

        await Assert.That(filteredSecond).IsEqualTo(1L);
        await Assert.That(filteredThird).IsEqualTo(1L);
    }

    [Test]
    public async Task WithTenant_Switch_AppliesToSubsequentQueries()
    {
        await using SharedSqliteDb shared = await SeedTenantAsync("session_sticky_4");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();

        List<CfTenantEntity> tenantOne = await db.WithTenant(1L).From<CfTenantEntity>().ToListAsync();
        List<CfTenantEntity> tenantTwo = await db.WithTenant(2L).From<CfTenantEntity>().ToListAsync();

        await Assert.That(tenantOne.Count).IsEqualTo(1);
        await Assert.That(tenantOne[0].TenantId).IsEqualTo(1L);
        await Assert.That(tenantTwo.Count).IsEqualTo(1);
        await Assert.That(tenantTwo[0].TenantId).IsEqualTo(2L);
    }

    [Test]
    public async Task PoolRent_AfterReturn_InterceptorReleased()
    {
        await using SharedSqliteDb shared = await SeedSoftDeleteAsync("session_sticky_5");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);

        // COUNT 族不经过拦截器（ITM-582），必须用实体 SELECT 触发 OnBefore
        var counter = new PoolInterceptorCounter();
        DataSession<SqliteProvider> first = await pool.RentAsync();
        first.AddInterceptor(counter);
        await first.From<CfSoftDeleteEntity>().ToListAsync();
        await first.DisposeAsync();

        int callsAfterFirstRent = counter.Calls;
        DataSession<SqliteProvider> second = await pool.RentAsync();
        await second.From<CfSoftDeleteEntity>().ToListAsync();
        await second.DisposeAsync();

        await Assert.That(callsAfterFirstRent).IsGreaterThan(0);
        // 租期内加项在归还时释放：第二次租期的调用数不得增加
        await Assert.That(counter.Calls).IsEqualTo(callsAfterFirstRent);
    }

    private sealed class PoolInterceptorCounter : IQueryInterceptor
    {
        public int Calls { get; private set; }

        public void OnBefore(QueryContext context) => Calls++;

        public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) { }

        public void OnError(QueryContext context, Exception exception) { }
    }
}