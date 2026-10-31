using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests.Conformance;

// DIALECT-2（方案 §6.3）：边界契约按 provider 参数化。8 条在三方言通过，
// 或在本文件的 NotApplicable 清单里显式登记差异（带理由），不静默跳过。

/// <summary>一条方言边界契约；NotApplicable 按方言登记差异理由。</summary>
internal sealed record DialectBoundary(
    string Id,
    string Precondition,
    string Postcondition,
    string Source,
    IReadOnlyDictionary<SqlDialect, string> NotApplicable,
    Func<DialectScope, Task> Verify);

internal static class DialectBoundaryCatalog<TProvider>
    where TProvider : IDbProvider
{
    private static CfBulkPlainEntity Plain(long id, string name = "n", long amount = 1)
        => new() { Id = id, Name = name, Amount = amount };

    public static IReadOnlyList<DialectBoundary> Boundaries { get; } =
    [
        new("DB-TAKE0", "Take(0)", "构建期抛 ArgumentOutOfRangeException（不静默返回空集）",
            "API参考.md 查询构建器（Take）+ 方言差异表",
            new Dictionary<SqlDialect, string>(),
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await Assert.That(async () => await db.From<CfBulkPlainEntity>().Take(0).ToListAsync())
                    .Throws<ArgumentOutOfRangeException>();
            }),

        new("DB-SKIP-PAGE", "builder 上带 Skip() 再调 ToPageAsync",
            "构建期抛 InvalidOperationException（键集分页不接受 OFFSET）",
            "API参考.md 执行方法（ToPageAsync）",
            new Dictionary<SqlDialect, string>(),
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await Assert.That(async () => await db.From<CfBulkPlainEntity>()
                    .Skip(1).ToPageAsync(2, e => e.Id)).Throws<InvalidOperationException>();
            }),

        new("DB-LOCK-SQLITE", "SQLite 上调用 ForUpdate/ForShare",
            "构建期抛 NotSupportedException（SQLite 无行级锁）",
            "reviewed: 2026-10-29 SQLite 无行级锁，构建期拒绝优于执行期语法错（ITM-639 终态）",
            new Dictionary<SqlDialect, string>
            {
                [SqlDialect.PostgreSql] = "行锁在 PG 由 DB-LOCK-REMOTE 覆盖",
                [SqlDialect.MySql] = "行锁在 MySQL 由 DB-LOCK-REMOTE 覆盖"
            },
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await Assert.That(() => db.From<CfBulkPlainEntity>().ForUpdate()).Throws<NotSupportedException>();
                await Assert.That(() => db.From<CfBulkPlainEntity>().ForShare()).Throws<NotSupportedException>();
            }),

        new("DB-LOCK-REMOTE", "PG/MySQL 事务内 ForUpdate",
            "构建并执行成功，行集可见；提交后事务释放",
            "API参考.md 查询构建器（ForUpdate）",
            new Dictionary<SqlDialect, string>
            {
                [SqlDialect.Sqlite] = "SQLite 无行级锁，拒绝形态由 DB-LOCK-SQLITE 覆盖"
            },
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.InsertAsync(Plain(1, "locked"));
                System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
                try
                {
                    List<CfBulkPlainEntity> rows = await db.From<CfBulkPlainEntity>()
                        .ForUpdate().ToListAsync();
                    await Assert.That(rows.Count).IsEqualTo(1);
                    await Assert.That(rows[0].Name).IsEqualTo("locked");
                }
                finally
                {
                    await transaction.CommitAsync();
                    await transaction.DisposeAsync();
                }
            }),

        new("DB-VERSION-BULK", "并发令牌实体：外部写入者已把 version 加 1",
            "BulkUpdateAsync 抛 ConcurrencyConflictException，冲突行不被覆盖",
            "ADR-P 版本化批量更新 + API参考.md 写入 / 批量",
            new Dictionary<SqlDialect, string>(),
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                // BulkInsert 不回填自增主键（C-BULKIN 契约），版本化批量更新需要实体带 Id，
                // 因此这里逐行 InsertAsync 取回填后的 Id。
                DlVersionedEntity first = await db.InsertAsync(new DlVersionedEntity { Id = 1, Name = "a" });
                DlVersionedEntity second = await db.InsertAsync(new DlVersionedEntity { Id = 2, Name = "b" });
                await db.ExecuteAsync(
                    System.Runtime.CompilerServices.FormattableStringFactory.Create(
                        $"UPDATE dl_versioned SET {scope.Q("version")} = {scope.Q("version")} + 1 "
                        + $"WHERE {scope.Q("Id")} = {{0}}",
                        first.Id));
                await Assert.That(async () => await db.BulkUpdateAsync([first, second]))
                    .Throws<ConcurrencyConflictException>();
                DlVersionedEntity row = (await db.GetAsync<DlVersionedEntity>(first.Id))!;
                await Assert.That(row.Version).IsEqualTo(1L);
                await Assert.That(row.Name).IsEqualTo("a");
            }),

        new("DB-SAVE-VERSION", "带 [ConcurrencyCheck] 的实体作为新行 SaveAsync",
            "编译期 PALORM054 拒绝（UPSERT 无法遵守乐观锁）；触发与静默两个方向由 SourceGen 诊断测试锁定",
            "PALORM054 + AnalyzerDiagnosticsTests.PALORM054_SaveAsyncOnVersionedEntity_ReportsError",
            new Dictionary<SqlDialect, string>
            {
                [SqlDialect.Sqlite] = "编译期拒绝，无运行时调用点；PALORM054 诊断测试覆盖",
                [SqlDialect.PostgreSql] = "编译期拒绝，无运行时调用点；PALORM054 诊断测试覆盖",
                [SqlDialect.MySql] = "编译期拒绝，无运行时调用点；PALORM054 诊断测试覆盖"
            },
            _ => throw new InvalidOperationException("DB-SAVE-VERSION 为编译期契约，运行时不可达")),

        new("DB-PAGE-CONTINUE", "五行、pageSize 2 连续取三页",
            "各页并集等于全量，页间无重复，末页 1 行",
            "API参考.md 执行方法（ToPageAsync 键集续页）",
            new Dictionary<SqlDialect, string>(),
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                await db.BulkInsertAsync([Plain(1), Plain(2), Plain(3), Plain(4), Plain(5)]);
                (List<CfBulkPlainEntity> page1, long total) = await db.From<CfBulkPlainEntity>()
                    .ToPageAsync(2, e => e.Id, descending: false);
                (List<CfBulkPlainEntity> page2, _) = await db.From<CfBulkPlainEntity>()
                    .ToPageAsync(2, e => e.Id, lastValue: page1[^1].Id, descending: false);
                (List<CfBulkPlainEntity> page3, _) = await db.From<CfBulkPlainEntity>()
                    .ToPageAsync(2, e => e.Id, lastValue: page2[^1].Id, descending: false);
                HashSet<long> union =
                    [.. page1.Select(e => e.Id), .. page2.Select(e => e.Id), .. page3.Select(e => e.Id)];
                await Assert.That(total).IsEqualTo(5L);
                await Assert.That(union.Count).IsEqualTo(5);
                await Assert.That(page3.Count).IsEqualTo(1);
            }),

        new("DB-SOFTDEL-BYPASS", "软删实体：删一行后分别走默认查询与 IgnoreFilters",
            "默认查询 0 行，IgnoreFilters 1 行（行仍在库中）",
            "API参考.md 直查 / CRUD + [SoftDelete] 注解说明",
            new Dictionary<SqlDialect, string>(),
            async scope =>
            {
                DataSession<TProvider> db = scope.Cast<DataSession<TProvider>>();
                CfSoftDeleteEntity entity = new() { Name = "gone" };
                await db.InsertAsync(entity);
                await db.DeleteAsync<CfSoftDeleteEntity>(entity.Id);
                await Assert.That(await db.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(0L);
                await Assert.That((await db.IgnoreFilters().From<CfSoftDeleteEntity>().ToListAsync()).Count)
                    .IsEqualTo(1);
            }),
    ];
}

internal static class DialectBoundaryRunner
{
    public static async Task RunAsync(
        IReadOnlyList<DialectBoundary> contracts,
        SqlDialect dialect,
        Func<string, CancellationToken, ValueTask<(DialectScope Scope, IAsyncDisposable Owner)>> openAsync,
        CancellationToken ct = default)
    {
        List<string> failures = [];
        int executed = 0;
        int skipped = 0;

        foreach (DialectBoundary contract in contracts)
        {
            if (contract.NotApplicable.ContainsKey(dialect))
            {
                skipped++;
                continue;
            }

            (DialectScope scope, IAsyncDisposable owner) = await openAsync($"dialect_{contract.Id}", ct);
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

        await Assert.That(string.Join("\n", failures)).IsEmpty();
        await Assert.That(executed + skipped).IsEqualTo(contracts.Count);
    }
}

[NotInParallel("ExtBulkTable")]
public sealed class DialectBoundaryContractTests
{
    private static async ValueTask PrepareRemoteAsync()
    {
        await using DataSession<PostgreSqlProvider> pg = await TestDb.PostgreSqlAsync();
        await pg.MigrateAsync();
        await using DataSession<MySqlProvider> my = await TestDb.MySqlAsync();
        await my.MigrateAsync();
    }

    [Test]
    public async Task Sqlite_BoundaryContracts_Hold()
        => await DialectBoundaryRunner.RunAsync(
            DialectBoundaryCatalog<SqliteProvider>.Boundaries, SqlDialect.Sqlite, DialectScopes.SqliteAsync);

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BoundaryContracts_Hold()
    {
        await PrepareRemoteAsync();
        await DialectBoundaryRunner.RunAsync(
            DialectBoundaryCatalog<PostgreSqlProvider>.Boundaries, SqlDialect.PostgreSql,
            DialectScopes.PostgreSqlAsync);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BoundaryContracts_Hold()
    {
        await PrepareRemoteAsync();
        await DialectBoundaryRunner.RunAsync(
            DialectBoundaryCatalog<MySqlProvider>.Boundaries, SqlDialect.MySql, DialectScopes.MySqlAsync);
    }
}

/// <summary>边界契约专用版本化实体：显式主键（InsertAsync 不需要回填）+ 并发令牌。
/// 独立表避免与 Conformance 套件的 cf_versioned 交错清表。</summary>
[Table("dl_versioned")]
public partial class DlVersionedEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("version")] [ConcurrencyCheck] public long Version { get; set; }
}
