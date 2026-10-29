using System.Data.Common;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

// 本文件测的是逐条用户动作（逐键回读、逐页更新、分页遍历），循环内的数据库调用就是被测路径本身。
#pragma warning disable PALORM005

namespace PalORM.Integration.Tests.Conformance;

/// <summary>端到端用户路径（2026-10-29 追加，D1 暴露的盲区）。
/// <para>现有 193 个用例测的是"单个操作在各形状与方言上是否正确"，D1 落在两个操作的接缝上：
/// 从库里读出来的实体能不能原样写回去。本类专测这类连续动作，覆盖读-改-写、跨方言迁移、
/// 事务内先读后写、软删复活、批量写后按主键回读、分页遍历更新、并发令牌冲突、删后重建同主键。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class EndToEndPathTests
{
    private static async Task CleanAsync<TProvider>(
        DataSession<TProvider> db, string table, CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        await db.ExecuteAsync(
            System.Runtime.CompilerServices.FormattableStringFactory.Create(
                $"DELETE FROM {DialectSql.Quote(TProvider.Dialect, table)}"), ct);
    }

    // ── 1. 读-改-写（D1 的直接来源）──────────────────────

    private static async Task AssertReadModifyWriteAsync<TProvider>(
        DataSession<TProvider> db, CancellationToken ct = default)
        where TProvider : IDbProvider
    {
        await CleanAsync(db, MirrorTable.BulkTable, ct);
        await db.BulkInsertAsync([new CfBulkPlainEntity { Id = 1, Name = "before", Amount = 1 }], ct);

        CfBulkPlainEntity row = (await db.GetAllAsync<CfBulkPlainEntity>(ct))[0];
        row.Name = "after";
        row.Amount = 99;
        await db.UpdateAsync(row, ct);

        CfBulkPlainEntity reloaded = (await db.GetAllAsync<CfBulkPlainEntity>(ct))[0];
        await Assert.That(reloaded.Name).IsEqualTo("after");
        await Assert.That(reloaded.Amount).IsEqualTo(99L);
    }

    [Test]
    public async Task Sqlite_ReadModifyWrite_RoundTripWorks()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("e2e_rmw_sqlite");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        await AssertReadModifyWriteAsync(db);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_ReadModifyWrite_RoundTripWorks()
    {
        await using RemoteConformanceFixture<PostgreSqlProvider> fixture = await ConformanceDialects.PostgreSqlAsync();
        await AssertReadModifyWriteAsync(fixture.Session);
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_ReadModifyWrite_RoundTripWorks()
    {
        await using RemoteConformanceFixture<MySqlProvider> fixture = await ConformanceDialects.MySqlAsync();
        await AssertReadModifyWriteAsync(fixture.Session);
    }

    // ── 2. 跨方言迁移（SQLite → PostgreSQL）──────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task CrossDialectMigration_SqliteToPostgreSql_PreservesRows()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("e2e_migration_sqlite");
        await using DataSession<SqliteProvider> source = await shared.OpenSessionAsync();
        await source.MigrateAsync();
        await CleanAsync(source, MirrorTable.BulkTable);

        CfBulkPlainEntity[] original =
        [
            new() { Id = 1, Name = "alpha", Amount = 0 },
            new() { Id = 2, Name = "", Amount = long.MaxValue },
            new() { Id = 3, Name = "emoji-🙂", Amount = long.MinValue },
            new() { Id = 4, Name = "quote'inside", Amount = -1 }
        ];
        await source.BulkInsertAsync(original);
        List<CfBulkPlainEntity> read = await source.GetAllAsync<CfBulkPlainEntity>();

        await using RemoteConformanceFixture<PostgreSqlProvider> target = await ConformanceDialects.PostgreSqlAsync();
        await CleanAsync(target.Session, MirrorTable.BulkTable);
        await target.Session.BulkInsertAsync(read);
        List<CfBulkPlainEntity> migrated = await target.Session.GetAllAsync<CfBulkPlainEntity>();

        string Render(IEnumerable<CfBulkPlainEntity> rows)
            => string.Join("|", rows.OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.Name}:{r.Amount}"));

        await Assert.That(migrated.Count).IsEqualTo(original.Length);
        await Assert.That(Render(migrated)).IsEqualTo(Render(original));
    }

    // ── 3. 事务内先读后写 ─────────────────────────────

    [Test]
    public async Task TransactionReadThenWrite_SeesOldValueInsideAndNewValueAfterCommit()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("e2e_tx_readwrite");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        await CleanAsync(db, MirrorTable.BulkTable);
        await db.BulkInsertAsync([new CfBulkPlainEntity { Id = 1, Name = "old", Amount = 1 }]);

        string insideAfterWrite;
        using (DbTransaction transaction = await db.BeginTransactionAsync())
        {
            CfBulkPlainEntity row = (await db.GetAllAsync<CfBulkPlainEntity>())[0];
            await Assert.That(row.Name).IsEqualTo("old");
            row.Name = "new";
            await db.UpdateAsync(row);
            insideAfterWrite = (await db.GetAllAsync<CfBulkPlainEntity>())[0].Name;
            await transaction.CommitAsync();
        }

        await using DataSession<SqliteProvider> fresh = await shared.OpenSessionAsync();
        string afterCommit = (await fresh.GetAllAsync<CfBulkPlainEntity>())[0].Name;

        await Assert.That(insideAfterWrite).IsEqualTo("new");
        await Assert.That(afterCommit).IsEqualTo("new");
    }

    // ── 4. 软删复活再更新 ─────────────────────────────

    [Test]
    public async Task SoftDeleteThenResurrect_BecomesVisibleAndUpdatableAgain()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("e2e_resurrect");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        await CleanAsync(db, MirrorTable.SoftDeleteTable);

        CfSoftDeleteEntity entity = await db.InsertAsync(new CfSoftDeleteEntity { Name = "alive" });
        await db.DeleteAsync<CfSoftDeleteEntity>(entity.Id);
        await Assert.That(await db.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(0L);

        await using DataSession<SqliteProvider> inspect = await shared.OpenSessionAsync();
        CfSoftDeleteEntity deleted = (await inspect.IgnoreFilters()
            .From<CfSoftDeleteEntity>().ToListAsync())[0];

        deleted.DeletedAt = null;
        deleted.Name = "resurrected";
        await db.UpdateAsync(deleted);

        List<CfSoftDeleteEntity> visible = await db.From<CfSoftDeleteEntity>().ToListAsync();
        await Assert.That(visible.Count).IsEqualTo(1);
        await Assert.That(visible[0].Name).IsEqualTo("resurrected");
    }

    // ── 5. 批量写后按主键回读 ─────────────────────────

    [Test]
    public async Task BulkInsertThenReadByEachKey_MatchesWrittenValues()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("e2e_bulk_readback");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        await CleanAsync(db, MirrorTable.BulkTable);

        CfBulkPlainEntity[] rows =
        [
            .. Enumerable.Range(1, 5).Select(i => new CfBulkPlainEntity { Id = i, Name = $"n{i}", Amount = i * 10 })
        ];
        await db.BulkInsertAsync(rows);
        List<CfBulkPlainEntity> stored = await db.GetAllAsync<CfBulkPlainEntity>();

        foreach (CfBulkPlainEntity row in stored)
        {
            CfBulkPlainEntity? single = await db.GetAsync<CfBulkPlainEntity>(row.Id);
            await Assert.That(single).IsNotNull();
            await Assert.That(single!.Name).IsEqualTo(row.Name);
            await Assert.That(single.Amount).IsEqualTo(row.Amount);
        }
        await Assert.That(stored.Count).IsEqualTo(5);
    }

    // ── 6. 分页遍历后逐页更新 ─────────────────────────

    private static async Task UpdateAllPagesAsync(DataSession<SqliteProvider> db, CancellationToken ct = default)
    {
        long? lastValue = null;
        var visited = new List<long>();
        while (true)
        {
            (List<CfBulkPlainEntity> page, _) = lastValue is null
                ? await db.From<CfBulkPlainEntity>().ToPageAsync(10, e => e.Id, descending: false, ct: ct)
                : await db.From<CfBulkPlainEntity>().ToPageAsync(10, e => e.Id, lastValue, descending: false, ct: ct);
            if (page.Count == 0) break;
            visited.AddRange(page.Select(e => e.Id));
            foreach (CfBulkPlainEntity row in page)
            {
                row.Name = "touched";
                await db.UpdateAsync(row, ct);
            }
            lastValue = page[^1].Id;
            if (page.Count < 10) break;
        }
        await Assert.That(visited.Count).IsEqualTo(25);
        await Assert.That(visited.Distinct().Count()).IsEqualTo(25);
    }

    [Test]
    public async Task PageThroughAllRowsThenUpdateEach_TouchesEveryRow()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("e2e_paging_update");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        await CleanAsync(db, MirrorTable.BulkTable);
        await db.BulkInsertAsync(
            [.. Enumerable.Range(1, 25).Select(i => new CfBulkPlainEntity { Id = i, Name = "orig", Amount = i })]);

        await UpdateAllPagesAsync(db);

        List<CfBulkPlainEntity> all = await db.GetAllAsync<CfBulkPlainEntity>();
        await Assert.That(all.Count).IsEqualTo(25);
        await Assert.That(all.All(e => e.Name == "touched")).IsTrue();
    }

    // ── 7. 并发令牌：读改写后旧实体写入必须冲突 ────────

    [Test]
    public async Task ConcurrencyToken_ReadModifyWriteThenStaleWriteConflicts()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("e2e_token");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        await db.ExecuteAsync($"DELETE FROM \"cf_versioned\"");

        CfVersionedEntity inserted = await db.InsertAsync(new CfVersionedEntity { Name = "v0", Version = 0 });
        CfVersionedEntity first = (await db.GetAsync<CfVersionedEntity>(inserted.Id))!;
        CfVersionedEntity stale = (await db.GetAsync<CfVersionedEntity>(inserted.Id))!;

        first.Name = "v1-read-write";
        await db.UpdateAsync(first);
        await Assert.That(first.Version).IsGreaterThan(0L);

        stale.Name = "stale-write";
        await Assert.That(async () => await db.UpdateAsync(stale))
            .Throws<ConcurrencyConflictException>();
        await Assert.That((await db.GetAsync<CfVersionedEntity>(inserted.Id))!.Name)
            .IsEqualTo("v1-read-write");
    }

    // ── 8. 删除后重建同主键 ───────────────────────────

    [Test]
    public async Task DeleteThenReinsertSameKey_Succeeds()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("e2e_reinsert");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        await CleanAsync(db, MirrorTable.BulkTable);

        await db.InsertAsync(new CfBulkPlainEntity { Id = 7, Name = "first", Amount = 1 });
        await db.DeleteAsync<CfBulkPlainEntity>(7L);
        await Assert.That(await db.GetAsync<CfBulkPlainEntity>(7L)).IsNull();

        await db.InsertAsync(new CfBulkPlainEntity { Id = 7, Name = "second", Amount = 2 });
        CfBulkPlainEntity? recreated = await db.GetAsync<CfBulkPlainEntity>(7L);
        await Assert.That(recreated).IsNotNull();
        await Assert.That(recreated!.Name).IsEqualTo("second");
    }
}