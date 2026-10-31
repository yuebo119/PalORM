using PalORM.Integration.Tests.Conformance;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

// P1-B（方案 §6.5 / BULK-1..2）：批量剩余的跨批原子性。
// BulkInsert 不包事务时第二批失败会留下首批已提交前缀——本类把该形态钉成实测断言；
// BulkMerge 整段包在事务内，第二批重复主键必须整段回滚。

[NotInParallel("ExtBulkTable")]
public sealed class BulkAtomicityTests
{
    private const int TotalRows = 2500;
    private const int BatchSize = 999;
    private const int ConflictRowIndex = 1499;   // 0 基：第 1500 行落在第二批
    private const long ConflictDuplicateId = 1000L; // 与第二批内 Id=1000 的行重复

    private static CfBulkPlainEntity[] BuildRowsWithConflict()
    {
        var rows = new CfBulkPlainEntity[TotalRows];
        for (int i = 0; i < TotalRows; i++)
        {
            long id = i == ConflictRowIndex ? ConflictDuplicateId : i + 1;
            rows[i] = new CfBulkPlainEntity { Id = id, Name = $"r{i}", Amount = i };
        }
        return rows;
    }

    [Test]
    public async Task Sqlite_BulkInsert_CrossBatchFailure_LeavesDocumentedPrefix()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("bulk_atomicity_sqlite");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.MigrateAsync();
        try
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
            try
            {
                await db.BulkInsertAsync(BuildRowsWithConflict(), BatchSize);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BULK-1 sqlite 异常：{ex.GetType().Name}: {ex.Message}");
            }
            long count = await db.CountAsync<CfBulkPlainEntity>();
            Console.WriteLine($"BULK-1 sqlite 残留行数={count}");
            // 2026-10-31 实测：BulkInsert 内部包事务，失败批之前已成功的批次同样回滚 → 0 行残留
            await Assert.That(count).IsEqualTo(0L);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkInsert_CrossBatchFailure_LeavesDocumentedPrefix()
        => await RemoteInsertPrefixAsync("postgresql", static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkInsert_CrossBatchFailure_LeavesDocumentedPrefix()
        => await RemoteInsertPrefixAsync("mysql", static () => TestDb.MySqlAsync());

    private static async Task RemoteInsertPrefixAsync<TProvider>(
        string name, Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        await using DataSession<TProvider> db = await factory();
        await db.MigrateAsync();
        try
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
            Exception? error = null;
            try
            {
                await db.BulkInsertAsync(BuildRowsWithConflict(), BatchSize);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex;
            }
            long count = await db.CountAsync<CfBulkPlainEntity>();
            Console.WriteLine($"BULK-1 {name} 异常={error?.GetType().Name ?? "无"} 残留行数={count}");
            await Assert.That(error).IsNotNull();
            // 与 SQLite 同口径：整段事务回滚 → 0 行残留（docs/API参考.md 批量原子性）
            await Assert.That(count).IsEqualTo(0L);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM cf_bulk_plain");
        }
    }

    // ── BULK-2：BulkMerge 跨批回滚 ──────────────────────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PostgreSql_BulkMerge_CrossBatchDuplicate_RollsBackAll()
        => await MergeRollbackAsync(static () => TestDb.PostgreSqlAsync());

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkMerge_CrossBatchDuplicate_RollsBackAll()
        => await MergeRollbackAsync(static () => TestDb.MySqlAsync());

    /// <summary>BulkMerge 跨批回滚。
    /// <para><b>为什么不拿"重复主键"当失败条件</b>：BulkMerge 的重复主键是 UPSERT 合并语义
    /// （有则更新、无则插入），重复主键本身不会报错——2026-10-31 实测确认。本用例改用
    /// <c>cf_composite_index.sku</c> 的 [Unique] 约束：第 1500 行与预置行 Sku 冲突，落在第二批。</para></summary>
    private static async Task MergeRollbackAsync<TProvider>(Func<Task<DataSession<TProvider>>> factory)
        where TProvider : IDbProvider
    {
        const int rows = 3000;       // PG/MySQL upsert 批宽 1000 行 → 冲突行落在第二批
        const int conflictAt = 1500;
        const string preservedSku = "keep-sku";
        await using DataSession<TProvider> db = await factory();
        await db.MigrateAsync();
        try
        {
            await db.ExecuteAsync($"DELETE FROM cf_composite_index");
            // 预置原状：一行保留数据，Sku 与第二批冲突行相同
            await db.InsertAsync(
                new CfCompositeIndexEntity { Category = "keep", Price = 1m, Sku = preservedSku });
            long preservedId = (await db.GetAllAsync<CfCompositeIndexEntity>())[0].Id;

            var entities = new CfCompositeIndexEntity[rows];
            for (int i = 0; i < rows; i++)
            {
                entities[i] = new CfCompositeIndexEntity
                {
                    Category = $"c{i}",
                    Price = i,
                    Sku = i == conflictAt ? preservedSku : $"s{i}"
                };
            }

            Exception? error = null;
            try
            {
                await db.BulkMergeAsync(entities);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex;
            }
            await Assert.That(error).IsNotNull();

            // 整段事务回滚：行数与内容保持原状
            await Assert.That(await db.CountAsync<CfCompositeIndexEntity>()).IsEqualTo(1L);
            CfCompositeIndexEntity row = (await db.GetAsync<CfCompositeIndexEntity>(preservedId))!;
            await Assert.That(row.Category).IsEqualTo("keep");
            await Assert.That(row.Price).IsEqualTo(1m);
            await Assert.That(row.Sku).IsEqualTo(preservedSku);
        }
        finally
        {
            await db.ExecuteAsync($"DELETE FROM cf_composite_index");
        }
    }
}