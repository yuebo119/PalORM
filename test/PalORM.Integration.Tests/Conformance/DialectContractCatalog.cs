using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

// DIALECT-1（方案 §6.3）：能力契约按 provider 参数化。20 条方言敏感契约在三方言各跑一遍。
// 与 ContractCatalog（SQLite 固定签名）的分工：那边保持原样用于 SQLite 深判据回归，
// 这边只挑"SQL 形态随方言变化"的 20 条，要求三方言行为一致。
// 不支持项写进 Unsupported 显式清单（当前为空），无静默跳过。

/// <summary>一条方言契约：前置、后置、依据，以及在指定会话上执行的验证体。
/// <para>后置取自 docs/API参考.md 与 XML doc 的承诺（同 ContractCatalog 口径），
/// "代码实际如此"不作为契约依据。</para></summary>
internal sealed record DialectContract(
    string Id,
    string Precondition,
    string Postcondition,
    string Source,
    Func<DialectScope, Task> Verify);

/// <summary>会话 + 方言标识 + 标识符引用。契约体只用这三样，因此同一份代码跑三方言。</summary>
internal sealed class DialectScope
{
    public DialectScope(object session, SqlDialect dialect)
    {
        Session = session;
        Dialect = dialect;
    }

    /// <summary>具体类型由工厂创建（<see cref="DataSession{TProvider}"/>），契约体经 Cast 取回。</summary>
    private object Session { get; }

    public SqlDialect Dialect { get; }

    public TProviderSession Cast<TProviderSession>() => (TProviderSession)Session;

    public string Q(string identifier)
        => Dialect == SqlDialect.MySql ? $"`{identifier}`" : $"\"{identifier}\"";
}

/// <summary>方言作用域的创建与清理：SQLite 每契约独立内存库；PG/MySQL 每契约独立会话 + 清表。</summary>
internal static class DialectScopes
{
    public static async ValueTask<(DialectScope Scope, IAsyncDisposable Owner)> SqliteAsync(
        string libraryName, CancellationToken ct = default)
    {
        SharedSqliteDb shared = await SharedSqliteDb.CreateAsync(libraryName, ct);
        try
        {
            DataSession<SqliteProvider> db = await shared.OpenSessionAsync(ct);
            return (new DialectScope(db, SqlDialect.Sqlite), new SqliteOwner(shared, db));
        }
        catch
        {
            await shared.DisposeAsync();
            throw;
        }
    }

    public static async ValueTask<(DialectScope Scope, IAsyncDisposable Owner)> PostgreSqlAsync(
        string _, CancellationToken ct = default)
    {
        DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync(ct);
        await CleanRemoteAsync(db, ct);
        return (new DialectScope(db, SqlDialect.PostgreSql), new SessionOwner<PostgreSqlProvider>(db));
    }

    public static async ValueTask<(DialectScope Scope, IAsyncDisposable Owner)> MySqlAsync(
        string _, CancellationToken ct = default)
    {
        DataSession<MySqlProvider> db = await TestDb.MySqlAsync(ct);
        await CleanRemoteAsync(db, ct);
        return (new DialectScope(db, SqlDialect.MySql), new SessionOwner<MySqlProvider>(db));
    }

    /// <summary>远程契约共用表：每契约开场前清空，保证契约之间互不影响。</summary>
    public static async Task CleanRemoteAsync<TProvider>(DataSession<TProvider> db, CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        await db.ExecuteAsync($"DELETE FROM cf_bulk_plain", ct);
        await db.ExecuteAsync($"DELETE FROM cf_soft_delete", ct);
        await db.ExecuteAsync($"DELETE FROM cf_tenant", ct);
        await db.ExecuteAsync($"DELETE FROM cf_versioned", ct);
        await db.ExecuteAsync($"DELETE FROM cf_composite_index", ct);
        await db.ExecuteAsync($"DELETE FROM dl_versioned", ct);
        await db.ExecuteAsync($"DELETE FROM ti_lock_probe", ct);
    }

    private sealed class SqliteOwner : IAsyncDisposable
    {
        private readonly SharedSqliteDb _shared;
        private readonly DataSession<SqliteProvider> _db;

        public SqliteOwner(SharedSqliteDb shared, DataSession<SqliteProvider> db)
        {
            _shared = shared;
            _db = db;
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _shared.DisposeAsync();
        }
    }

    private sealed class SessionOwner<TProvider> : IAsyncDisposable
        where TProvider : IDbProvider
    {
        private readonly DataSession<TProvider> _db;

        public SessionOwner(DataSession<TProvider> db) => _db = db;

        public async ValueTask DisposeAsync() => await _db.DisposeAsync();
    }
}

/// <summary>显式不支持清单：契约 ID + 方言 → 理由。当前为空；
/// 将来若确认为合理方言差异，必须在此登记理由并在契约体断言拒绝形态（不静默跳过）。</summary>
internal static class DialectUnsupported
{
    public static IReadOnlyDictionary<(string Id, SqlDialect Dialect), string> Table { get; } =
        new Dictionary<(string, SqlDialect), string>();
}

/// <summary>20 条方言敏感能力契约（方案 §6.3 清单）。</summary>
internal static class DialectContractCatalog<TProvider>
    where TProvider : IDbProvider
{
    private static CfBulkPlainEntity Plain(long id, string name = "n", long amount = 1)
        => new() { Id = id, Name = name, Amount = amount };

    public static IReadOnlyList<DialectContract> Capabilities { get; } =
    [
        new("D-INSERT-01", "显式主键实体", "InsertAsync 返回同一实例且 Id 保持调用方给的值",
            "API参考.md 直查 / CRUD",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                CfBulkPlainEntity entity = Plain(7);
                CfBulkPlainEntity returned = await db.InsertAsync(entity);
                await Assert.That(ReferenceEquals(returned, entity)).IsTrue();
                await Assert.That(returned.Id).IsEqualTo(7L);
            }),

        new("D-INSERT-02", "自增主键实体", "InsertAsync 后实体 Id 被回填为正数",
            "API参考.md 直查 / CRUD",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                CfSoftDeleteEntity entity = new() { Name = "auto" };
                await db.InsertAsync(entity);
                await Assert.That(entity.Id).IsGreaterThan(0L);
            }),

        new("D-SAVE-01", "主键不存在", "SaveAsync 插入新行",
            "API参考.md 写入 / 批量（InsertOrUpdate 单次往返 UPSERT）",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.SaveAsync(Plain(1, "inserted"));
                CfBulkPlainEntity row = (await db.GetAsync<CfBulkPlainEntity>(1L))!;
                await Assert.That(row.Name).IsEqualTo("inserted");
            }),

        new("D-SAVE-02", "主键已存在", "SaveAsync 走冲突更新分支，不新增行",
            "API参考.md 写入 / 批量（InsertOrUpdate 单次往返 UPSERT）",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.InsertAsync(Plain(1, "before", 1));
                await db.SaveAsync(Plain(1, "after", 9));
                await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
                CfBulkPlainEntity row = (await db.GetAsync<CfBulkPlainEntity>(1L))!;
                await Assert.That(row.Name).IsEqualTo("after");
                await Assert.That(row.Amount).IsEqualTo(9L);
            }),

        new("D-UPDATE-01", "实体存在或不存在", "UpdateAsync 返回受影响行数：存在 1、不存在 0",
            "API参考.md 直查 / CRUD",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.InsertAsync(Plain(1));
                await Assert.That(await db.UpdateAsync(Plain(1, "updated"))).IsEqualTo(1);
                await Assert.That(await db.UpdateAsync(Plain(999, "ghost"))).IsEqualTo(0);
            }),

        new("D-UPDCOLS-01", "实体与单列表达式", "UpdateColumnsAsync 只写指定列，其余列保持原值",
            "API参考.md 直查 / CRUD（部分更新）",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.InsertAsync(Plain(1, "keep", 1));
                int affected = await db.UpdateColumnsAsync(
                    new CfBulkPlainEntity { Id = 1, Name = "ignored", Amount = 42 }, e => e.Amount);
                CfBulkPlainEntity row = (await db.GetAsync<CfBulkPlainEntity>(1L))!;
                await Assert.That(affected).IsEqualTo(1);
                await Assert.That(row.Name).IsEqualTo("keep");
                await Assert.That(row.Amount).IsEqualTo(42L);
            }),

        new("D-DELETE-01", "非软删实体", "DeleteAsync 物理删除，行不再存在",
            "API参考.md 直查 / CRUD",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.InsertAsync(Plain(1));
                await Assert.That(await db.DeleteAsync<CfBulkPlainEntity>(1L)).IsEqualTo(1);
                await Assert.That(await db.GetAsync<CfBulkPlainEntity>(1L)).IsNull();
            }),

        new("D-DELETE-02", "软删实体", "DeleteAsync 只写 deleted_at；默认查询不可见，IgnoreFilters 可见",
            "API参考.md 直查 / CRUD + [SoftDelete] 注解说明",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                CfSoftDeleteEntity entity = new() { Name = "soft" };
                await db.InsertAsync(entity);
                await Assert.That(await db.DeleteAsync<CfSoftDeleteEntity>(entity.Id)).IsEqualTo(1);
                await Assert.That(await db.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(0L);
                await Assert.That((await db.IgnoreFilters().From<CfSoftDeleteEntity>().ToListAsync()).Count)
                    .IsEqualTo(1);
            }),

        new("D-GET-01", "主键存在与不存在", "GetAsync 命中返回实体、未命中返回 null",
            "API参考.md 直查 / CRUD",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.InsertAsync(Plain(1, "found"));
                CfBulkPlainEntity? hit = await db.GetAsync<CfBulkPlainEntity>(1L);
                await Assert.That(hit).IsNotNull();
                await Assert.That(hit!.Name).IsEqualTo("found");
                await Assert.That(await db.GetAsync<CfBulkPlainEntity>(2L)).IsNull();
            }),

        new("D-GETALL-01", "三行含一行已软删", "GetAllAsync 只返回有效行",
            "API参考.md 直查 / CRUD",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.InsertAsync(new CfSoftDeleteEntity { Name = "a" });
                await db.InsertAsync(new CfSoftDeleteEntity { Name = "b" });
                await db.InsertAsync(new CfSoftDeleteEntity { Name = "c", DeletedAt = "2026-01-01" });
                await Assert.That((await db.GetAllAsync<CfSoftDeleteEntity>()).Count).IsEqualTo(2);
            }),

        new("D-QUERY-01", "四行数据", "ToListAsync 条件过滤生效",
            "API参考.md 查询构建器 / 执行方法",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.BulkInsertAsync([Plain(1, "a", 10), Plain(2, "b", 20), Plain(3, "c", 30)]);
                await Assert.That((await db.From<CfBulkPlainEntity>().Where($"amount >= 20").ToListAsync()).Count)
                    .IsEqualTo(2);
            }),

        new("D-AGG-01", "三行 amount 10/20/30", "Count/Sum/Max/Min/Avg 与手写 SQL 一致",
            "API参考.md 聚合",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.BulkInsertAsync([Plain(1, "a", 10), Plain(2, "b", 20), Plain(3, "c", 30)]);
                await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(3L);
                await Assert.That(await db.SumAsync<CfBulkPlainEntity>($"amount")).IsEqualTo(60m);
                await Assert.That(await db.MaxAsync<CfBulkPlainEntity, long>($"amount")).IsEqualTo(30L);
                await Assert.That(await db.MinAsync<CfBulkPlainEntity, long>($"amount")).IsEqualTo(10L);
                await Assert.That(await db.AvgAsync<CfBulkPlainEntity>($"amount")).IsEqualTo(20d);
            }),

        new("D-SCALAR-01", "原始 SQL 入口", "ScalarAsync 返回与裸 ADO 相同的标量",
            "API参考.md 执行方法（原始 SQL 入口，默认过滤不适用，ITM-700）",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.BulkInsertAsync([Plain(1), Plain(2)]);
                await Assert.That(await db.ScalarAsync<long>($"SELECT COUNT(*) FROM cf_bulk_plain"))
                    .IsEqualTo(2L);
            }),

        new("D-BULKIN-01", "三行显式主键", "BulkInsertAsync 返回影响行数且不改写主键",
            "API参考.md 写入 / 批量 + 实测 2026-10-29（B11）",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                CfBulkPlainEntity[] rows = [Plain(1, "a"), Plain(2, "b"), Plain(3, "c")];
                await Assert.That(await db.BulkInsertAsync(rows)).IsEqualTo(3L);
                await Assert.That(rows[0].Id).IsEqualTo(1L);
                await Assert.That(rows[2].Id).IsEqualTo(3L);
            }),

        new("D-BULKUP-01", "两行已插入", "BulkUpdateAsync 按主键逐行更新",
            "API参考.md 写入 / 批量",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                CfBulkPlainEntity[] rows = [Plain(1, "a"), Plain(2, "b")];
                await db.BulkInsertAsync(rows);
                rows[0].Name = "a2";
                rows[1].Name = "b2";
                await Assert.That(await db.BulkUpdateAsync(rows)).IsEqualTo(2L);
                await Assert.That((await db.GetAsync<CfBulkPlainEntity>(1L))!.Name).IsEqualTo("a2");
            }),

        new("D-BULKDEL-01", "三行删除两个主键", "BulkDeleteAsync 返回 2，剩余 1 行",
            "API参考.md 写入 / 批量",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.BulkInsertAsync([Plain(1), Plain(2), Plain(3)]);
                await Assert.That(await db.BulkDeleteAsync<CfBulkPlainEntity, long>([2L, 3L])).IsEqualTo(2L);
                await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
            }),

        new("D-BULKMERGE-01", "主键 1 已存在、2 不存在", "BulkMergeAsync 对已存在键更新、新键插入",
            "API参考.md 写入 / 批量",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.BulkInsertAsync([Plain(1, "existing", 1)]);
                await db.BulkMergeAsync([Plain(1, "merged", 11), Plain(2, "added", 2)]);
                await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(2L);
                await Assert.That((await db.GetAsync<CfBulkPlainEntity>(1L))!.Name).IsEqualTo("merged");
                await Assert.That((await db.GetAsync<CfBulkPlainEntity>(2L))!.Name).IsEqualTo("added");
            }),

        new("D-TX-01", "显式事务内插入", "提交后行可见",
            "API参考.md 事务",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
                await db.InsertAsync(Plain(1, "committed"));
                await transaction.CommitAsync();
                // 先释放事务再继续用会话：Npgsql 在 Commit 后 Dispose 前仍返回 Connection，
                // 残留事务会被当成可用事务绑到后续命令并抛 "Transaction is already completed"。
                await transaction.DisposeAsync();
                await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
            }),

        new("D-TX-02", "显式事务内插入后回滚", "回滚后行不可见",
            "API参考.md 事务",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
                await db.InsertAsync(Plain(1, "rolled-back"));
                await transaction.RollbackAsync();
                await transaction.DisposeAsync();
                await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
            }),

        new("D-PAGE-01", "五行、pageSize 2", "ToPageAsync 升序续页无重无漏；Skip 构建期拒绝",
            "API参考.md 执行方法（ToPageAsync）+ 实测 2026-10-29",
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.BulkInsertAsync([Plain(1), Plain(2), Plain(3), Plain(4), Plain(5)]);
                (List<CfBulkPlainEntity> first, long total) = await db.From<CfBulkPlainEntity>()
                    .ToPageAsync(2, e => e.Id, descending: false);
                (List<CfBulkPlainEntity> second, _) = await db.From<CfBulkPlainEntity>()
                    .ToPageAsync(2, e => e.Id, lastValue: first[^1].Id, descending: false);
                await Assert.That(total).IsEqualTo(5L);
                await Assert.That(string.Join(",", first.Select(e => e.Id))).IsEqualTo("1,2");
                await Assert.That(string.Join(",", second.Select(e => e.Id))).IsEqualTo("3,4");
                await Assert.That(async () => await db.From<CfBulkPlainEntity>()
                    .Skip(1).ToPageAsync(2, e => e.Id)).Throws<InvalidOperationException>();
            }),
    ];
}

/// <summary>三方言执行器：20 条契约 × 3 方言，任何一条失败即报出 ID；显式不支持项跳过并记录。</summary>
internal static class DialectContractRunner
{
    public static async Task<int> RunAsync(
        IReadOnlyList<DialectContract> contracts,
        SqlDialect dialect,
        Func<string, CancellationToken, ValueTask<(DialectScope Scope, IAsyncDisposable Owner)>> openAsync,
        CancellationToken ct = default)
    {
        List<string> failures = [];
        List<string> skipped = [];
        int executed = 0;

        foreach (DialectContract contract in contracts)
        {
            if (DialectUnsupported.Table.TryGetValue((contract.Id, dialect), out string? reason))
            {
                skipped.Add($"{contract.Id}: {reason}");
                continue;
            }

            (DialectScope scope, IAsyncDisposable owner) =
                await openAsync($"dialect_{contract.Id}", ct);
            await using (owner)
            {
                try
                {
                    await contract.Verify(scope);
                    executed++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{contract.Id}/{dialect}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // 有效负载下限：20 条契约在三方言必须都执行（除非显式登记不支持）
        string report = failures.Count == 0 ? "" : string.Join("\n", failures);
        await Assert.That(report).IsEmpty();
        await Assert.That(executed).IsEqualTo(contracts.Count - skipped.Count);
        return executed;
    }
}

[NotInParallel("ExtBulkTable")]
public sealed class DialectCapabilityContractTests
{
    private static async ValueTask PrepareRemoteAsync()
    {
        // 远程容器缺表时 Migrate 一次；SQLite 由每个 SharedSqliteDb 自带迁移。
        await using DataSession<PostgreSqlProvider> pg = await TestDb.PostgreSqlAsync();
        await pg.MigrateAsync();
        await using DataSession<MySqlProvider> my = await TestDb.MySqlAsync();
        await my.MigrateAsync();
    }

    [Test]
    public async Task Sqlite_CapabilityContracts_Hold()
    {
        // S3 守卫（2026-11-03）：ContractCatalog 的 18 条 SQLite 重复条目删除后，
        // D 层成为 SQLite 侧的唯一兜底。若某条 D 契约在 SQLite 上被登记为不支持，
        // SQLite 侧就再无覆盖——本断言把"SQLite 必须全覆盖"钉成机械约束。
        foreach ((string id, SqlDialect dialect) in DialectUnsupported.Table.Keys)
        {
            if (dialect != SqlDialect.Sqlite) continue;
            await Assert.That(id)
                .IsNull()
                .Because($"契约 {id} 在 SQLite 上被登记为不支持，但 ContractCatalog 的 SQLite 兜底已移除——SQLite 侧将失去该契约覆盖");
        }

        int executed = await RunAsync(SqlDialect.Sqlite, DialectScopes.SqliteAsync);
        await Assert.That(executed).IsEqualTo(DialectContractCatalog<SqliteProvider>.Capabilities.Count);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_CapabilityContracts_Hold()
    {
        await PrepareRemoteAsync();
        int executed = await RunAsync(SqlDialect.PostgreSql, DialectScopes.PostgreSqlAsync);
        // 行为断言（2026-11-02）：断言在 helper 内，扫描器看不到；这里补一条"至少执行过 1 条契约"，
        // 同时防 helper 静默空转（B189 无声 no-op 同族）。
        await Assert.That(executed).IsGreaterThan(0);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_CapabilityContracts_Hold()
    {
        await PrepareRemoteAsync();
        int executed = await RunAsync(SqlDialect.MySql, DialectScopes.MySqlAsync);
        await Assert.That(executed).IsGreaterThan(0);
    }

    private static Task<int> RunAsync(
        SqlDialect dialect,
        Func<string, CancellationToken, ValueTask<(DialectScope Scope, IAsyncDisposable Owner)>> openAsync)
    {
        return dialect switch
        {
            SqlDialect.Sqlite => DialectContractRunner.RunAsync(
                DialectContractCatalog<SqliteProvider>.Capabilities, dialect, openAsync),
            SqlDialect.PostgreSql => DialectContractRunner.RunAsync(
                DialectContractCatalog<PostgreSqlProvider>.Capabilities, dialect, openAsync),
            _ => DialectContractRunner.RunAsync(
                DialectContractCatalog<MySqlProvider>.Capabilities, dialect, openAsync),
        };
    }
}