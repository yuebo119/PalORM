using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

// DIALECT-3（方案 §6.3）：4 条状态不变量下沉 PG/MySQL。SQLite 侧保持原样
// （StateInvariantTests 的 I1/I2/I5/I8 覆盖同族语义），本类只补远程方言证据。

internal static class DialectStateScope
{
    public static async ValueTask<(DataSession<TProvider> Db, IAsyncDisposable Owner)> OpenAsync<TProvider>(
        Func<Task<DataSession<TProvider>>> factory, CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        DataSession<TProvider> db = await factory();
        await DialectScopes.CleanRemoteAsync(db, ct);
        return (db, new SessionOwner<TProvider>(db));
    }

    private sealed class SessionOwner<TProvider> : IAsyncDisposable
        where TProvider : IDbProvider
    {
        private readonly DataSession<TProvider> _db;

        public SessionOwner(DataSession<TProvider> db) => _db = db;

        public async ValueTask DisposeAsync() => await _db.DisposeAsync();
    }
}

[NotInParallel("ExtBulkTable")]
public sealed class DialectStateInvariantTests
{
    // ── 1. 软删分区 ──────────────────────────────────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_SoftDeletePartition_Holds()
        => await SoftDeletePartitionAsync(static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_SoftDeletePartition_Holds()
        => await SoftDeletePartitionAsync(static () => TestDb.MySqlAsync());

    private static async Task SoftDeletePartitionAsync<TProvider>(
        Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        await using DataSession<TProvider> migrate = await factory();
        await migrate.MigrateAsync();

        (DataSession<TProvider> db, IAsyncDisposable owner) = await DialectStateScope.OpenAsync(factory);
        await using (owner)
        {
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive-1" });
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive-2" });
            await db.InsertAsync(new CfSoftDeleteEntity { Name = "dead", DeletedAt = "2026-01-01" });

            // 默认过滤：只看见 2 行；IgnoreFilters：全部 3 行。两侧都断言，避免"都返回 2"时判据同错。
            await Assert.That(await db.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(2L);
            await Assert.That((await db.IgnoreFilters().From<CfSoftDeleteEntity>().ToListAsync()).Count)
                .IsEqualTo(3);
        }
    }

    // ── 2. 租户隔离 ──────────────────────────────────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_TenantIsolation_Holds()
        => await TenantIsolationAsync(static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_TenantIsolation_Holds()
        => await TenantIsolationAsync(static () => TestDb.MySqlAsync());

    private static async Task TenantIsolationAsync<TProvider>(
        Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        await using DataSession<TProvider> migrate = await factory();
        await migrate.MigrateAsync();

        (DataSession<TProvider> seed, IAsyncDisposable owner) = await DialectStateScope.OpenAsync(factory);
        await using (owner)
        {
            await seed.InsertAsync(new CfTenantEntity { TenantId = 1, Value = "t1" });
            await seed.InsertAsync(new CfTenantEntity { TenantId = 2, Value = "t2" });
        }

        // 两个独立会话各带一个租户：任何一侧看到对方的行即隔离失效。
        // 这里不能再走 DialectStateScope.OpenAsync（它会清表），直接与会话工厂建连接。
        await using (DataSession<TProvider> one = await factory())
        {
            one.WithTenant(1L);
            List<CfTenantEntity> rows = await one.GetAllAsync<CfTenantEntity>();
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].TenantId).IsEqualTo(1L);
        }

        await using (DataSession<TProvider> two = await factory())
        {
            two.WithTenant(2L);
            List<CfTenantEntity> rows = await two.GetAllAsync<CfTenantEntity>();
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].TenantId).IsEqualTo(2L);
        }
    }

    // ── 3. 查询缓存键（租户参与键） ──────────────────────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_QueryCacheKeyIncludesTenant_Holds()
        => await CacheKeyAsync(static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_QueryCacheKeyIncludesTenant_Holds()
        => await CacheKeyAsync(static () => TestDb.MySqlAsync());

    private static async Task CacheKeyAsync<TProvider>(
        Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        await using DataSession<TProvider> migrate = await factory();
        await migrate.MigrateAsync();

        (DataSession<TProvider> seed, IAsyncDisposable owner) = await DialectStateScope.OpenAsync(factory);
        await using (owner)
        {
            await seed.InsertAsync(new CfTenantEntity { TenantId = 1, Value = "t1" });
            await seed.InsertAsync(new CfTenantEntity { TenantId = 2, Value = "t2" });
        }

        var cache = new CountingCache();
        string connectionString = typeof(TProvider) == typeof(PostgreSqlProvider)
            ? TestEnvironment.ResolvePostgreSqlConnectionString()
            : TestEnvironment.ResolveMySqlConnectionString();

        await using (DataSession<TProvider> tenantOne = await DataSession<TProvider>.CreateAsync(
            new DbOptions { ConnectionString = connectionString, QueryCache = cache }))
        {
            tenantOne.WithTenant(1L);
            List<CfTenantEntity> rows = await tenantOne.From<CfTenantEntity>()
                .WithCache("shared-key").ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].TenantId).IsEqualTo(1L);
        }

        await using (DataSession<TProvider> tenantTwo = await DataSession<TProvider>.CreateAsync(
            new DbOptions { ConnectionString = connectionString, QueryCache = cache }))
        {
            tenantTwo.WithTenant(2L);
            List<CfTenantEntity> rows = await tenantTwo.From<CfTenantEntity>()
                .WithCache("shared-key").ToListAsync();
            // 缓存键若漏掉租户态，这里会命中租户 1 并返回 t1
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].TenantId).IsEqualTo(2L);
        }
    }

    // ── 4. 池租还复位 ────────────────────────────────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_PoolRentReset_Holds()
        => await PoolResetAsync<PostgreSqlProvider>(TestEnvironment.ResolvePostgreSqlConnectionString());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_PoolRentReset_Holds()
        => await PoolResetAsync<MySqlProvider>(TestEnvironment.ResolveMySqlConnectionString());

    private static async Task PoolResetAsync<TProvider>(string connectionString)
        where TProvider : IDbProvider
    {
        // 共享夹具表：先按当前 DDL 重建（历史库列型可能过期），再重置数据
        await using (DataSession<TProvider> migrate = await DataSession<TProvider>.CreateAsync(
            new DbOptions { ConnectionString = connectionString }))
        {
            await migrate.MigrateAsync();
            await migrate.ExecuteAsync($"DELETE FROM cf_soft_delete");
            await migrate.InsertAsync(new CfSoftDeleteEntity { Name = "alive" });
            await migrate.InsertAsync(new CfSoftDeleteEntity { Name = "dead", DeletedAt = "2026-01-01" });
        }

        await using DataSessionPool<TProvider> pool = DataSessionPool<TProvider>.Create(
            new DbOptions { ConnectionString = connectionString }, maxIdleSize: 1);

        DataSession<TProvider> first = await pool.RentAsync();
        first.IgnoreFilters();
        first.WithTimeout(TimeSpan.FromSeconds(5));
        long unfiltered = await first.CountAsync<CfSoftDeleteEntity>();
        await first.DisposeAsync();

        DataSession<TProvider> second = await pool.RentAsync();
        long filtered = await second.CountAsync<CfSoftDeleteEntity>();
        await second.DisposeAsync();

        await Assert.That(unfiltered).IsEqualTo(2L);
        await Assert.That(filtered).IsEqualTo(1L);
        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    private sealed class CountingCache : IQueryCache
    {
        private readonly Dictionary<string, object> _store = new(StringComparer.Ordinal);

        public bool TryGet<T>(string key, out T? value) where T : class
        {
            if (_store.TryGetValue(key, out object? cached) && cached is T typed)
            {
                value = typed;
                return true;
            }
            value = null;
            return false;
        }

        public void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class
            => _store[key] = value;

        public void Clear() => _store.Clear();
    }
}