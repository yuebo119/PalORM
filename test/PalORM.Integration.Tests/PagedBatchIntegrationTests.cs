// PALORM005（N+1 检测）：键集翻页的逐页循环与建行循环是被测行为本身（分页语义），
// 非反模式
#pragma warning disable PALORM005

using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>ToPageAsync 的单往返批路径（COUNT + 页 SELECT 一个 DbBatch）真库行为锁定。
/// <para>PG：无外部事务且默认隔离时 <b>不建事务</b>直接批执行；MySQL：沿用经典事务骨架
/// （RR 快照一致性不变），事务内把两次往返压成一次。</para>
/// <para>覆盖面：① 键集翻页全程（lastValue 续页）与经典路径结果逐位一致（PG 用强隔离强制、
/// MySQL 用拦截器强制）；② 空表零总数；③ 软删默认过滤同时作用于 COUNT 与页行；
/// ④ 外部事务内分页走批量形态且遵守回滚。</para>
/// <para><b>串行槽</b>：ExternalDatabase 用例统一挂 ExtBulkTable 守卫键（并发 CREATE TABLE
/// 会撞 pg_type 系统表唯一索引，B63 家族——ExternalDbIsolationInvariantTests 门禁锁定）。</para></summary>
[NotInParallel("ExtBulkTable")]
public sealed class PagedBatchIntegrationTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_ToPage_BatchedPath_KeysetWalkMatchesClassicPath()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
            await CreateRowsAsync(db, 25);

            // 默认形态（批路径）：25 行 pageSize 10 翻到底，lastValue 续页
            (List<List<long>> batchedPages, long total) = await WalkAllPagesAsync(db, 10);
            await Assert.That(total).IsEqualTo(25);
            await Assert.That(batchedPages.Count).IsEqualTo(3);
            await Assert.That(batchedPages[0].Count).IsEqualTo(10);
            await Assert.That(batchedPages[2].Count).IsEqualTo(5);
            // 降序续页：每页首行严格小于上一页末行
            await Assert.That(batchedPages[1][0]).IsLessThan(batchedPages[0][^1]);

            // 经典路径（强隔离门禁强制）：同一会话同一数据，结果逐位一致
            db.WithIsolationLevel(System.Data.IsolationLevel.RepeatableRead);
            (List<List<long>> classicPages, long classicTotal) = await WalkAllPagesAsync(db, 10);
            await Assert.That(classicTotal).IsEqualTo(total);
            for (int i = 0; i < 3; i++)
                await Assert.That(string.Join(",", classicPages[i])).IsEqualTo(string.Join(",", batchedPages[i]));
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch");
            await db.DisposeAsync();
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_ToPage_BatchedPath_EmptyTable_ReturnsZeroAndNoRows()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_page_batch (id BIGINT PRIMARY KEY, seq BIGINT NOT NULL)");
            (List<PageBatchEntity> rows, long total) = await db.From<PageBatchEntity>()
                .ToPageAsync(10, o => o.Seq, descending: true);
            await Assert.That(total).IsEqualTo(0);
            await Assert.That(rows.Count).IsEqualTo(0);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch");
            await db.DisposeAsync();
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_ToPage_BatchedPath_SoftDeleteFilterAppliesToCountAndRows()
    {
        await using DataSession<PostgreSqlProvider> db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch_sd");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_page_batch_sd (id BIGINT PRIMARY KEY, seq BIGINT NOT NULL, deleted_at TIMESTAMP)");
            for (int i = 1; i <= 6; i++)
                await db.InsertAsync(new PageBatchSoftDeleteEntity { Id = i, Seq = i * 10 });
            await db.DeleteAsync<PageBatchSoftDeleteEntity>(2);   // 软删一行 → 过滤后 5 行

            (List<PageBatchSoftDeleteEntity> rows, long total) = await db.From<PageBatchSoftDeleteEntity>()
                .ToPageAsync(3, o => o.Seq, descending: true);
            await Assert.That(total).IsEqualTo(5);
            await Assert.That(rows.Count).IsEqualTo(3);
            await Assert.That(rows.Any(static r => r.Id == 2)).IsFalse();
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch_sd");
            await db.DisposeAsync();
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_ToPage_InterceptorPresent_FallsBackToClassicPathCorrectly()
    {
        // 门禁反向验证：拦截器在场走经典事务路径（拦截器三段式与事务编排留在经典路径）
        CountingTestInterceptor interceptor = new();
        DbOptions options = new DbOptions
        {
            ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString(),
            Interceptors = [interceptor],
        };
        await using DataSession<PostgreSqlProvider> db = await DataSession<PostgreSqlProvider>.CreateAsync(options);
        try
        {
            await CreateRowsAsync(db, 7);
            (List<PageBatchEntity> rows, long total) = await db.From<PageBatchEntity>()
                .ToPageAsync(3, o => o.Seq, descending: true);
            await Assert.That(total).IsEqualTo(7);
            await Assert.That(rows.Count).IsEqualTo(3);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch");
            await db.DisposeAsync();
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_ToPage_BatchedInTransaction_MatchesClassicPath()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        try
        {
            await CreateRowsAsync(db, 8);
            (List<List<long>> batchedPages, long total) = await WalkAllPagesAsync(db, 3);
            await Assert.That(total).IsEqualTo(8);
            await Assert.That(batchedPages.Count).IsEqualTo(3);

            // 拦截器在场 → 门禁回退经典事务路径；结果必须与批形态逐位一致
            CountingTestInterceptor interceptor = new();
            DbOptions options = new()
            {
                ConnectionString = TestEnvironment.ResolveMySqlConnectionString(),
                Interceptors = [interceptor],
            };
            await using DataSession<MySqlProvider> classic =
                await DataSession<MySqlProvider>.CreateAsync(options);
            (List<List<long>> classicPages, long classicTotal) = await WalkAllPagesAsync(classic, 3);
            await Assert.That(classicTotal).IsEqualTo(total);
            for (int i = 0; i < batchedPages.Count; i++)
                await Assert.That(string.Join(",", classicPages[i])).IsEqualTo(string.Join(",", batchedPages[i]));
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch");
            await db.DisposeAsync();
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_ToPage_WithinExternalTransaction_HonorsRollback()
    {
        await using DataSession<MySqlProvider> db = await TestDb.MySqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch");
            await db.ExecuteAsync(
                $"CREATE TABLE palorm_page_batch (id BIGINT PRIMARY KEY, seq BIGINT NOT NULL)");
            await using (var transaction = await db.BeginTransactionAsync())
            {
                List<PageBatchEntity> seed = [];
                for (int i = 1; i <= 8; i++)
                    seed.Add(new PageBatchEntity { Id = i, Seq = i * 10L });
                await db.BulkInsertAsync(seed);
                // 外部事务在场地：两次往返打进该事务（DbBatch.Transaction = 外部事务）
                (List<PageBatchEntity> rows, long total) = await db.From<PageBatchEntity>()
                    .ToPageAsync(3, o => o.Seq, descending: true);
                await Assert.That(total).IsEqualTo(8);
                await Assert.That(rows.Count).IsEqualTo(3);
                await transaction.RollbackAsync();
            }
            await Assert.That(await db.CountAsync<PageBatchEntity>()).IsEqualTo(0);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch");
            await db.DisposeAsync();
        }
    }

    private static async Task CreateRowsAsync<TProvider>(DataSession<TProvider> db, int count)
        where TProvider : IDbProvider
    {
        await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_page_batch");
        await db.ExecuteAsync(
            $"CREATE TABLE palorm_page_batch (id BIGINT PRIMARY KEY, seq BIGINT NOT NULL)");
        List<PageBatchEntity> seed = [];
        for (int i = 1; i <= count; i++)
            seed.Add(new PageBatchEntity { Id = i, Seq = i * 10L });
        await db.BulkInsertAsync(seed);
    }

    /// <summary>键集翻页到底：返回每页的 seq 键集与首页带回的 Total。</summary>
    private static async Task<(List<List<long>> Pages, long Total)> WalkAllPagesAsync<TProvider>(
        DataSession<TProvider> db, int pageSize)
        where TProvider : IDbProvider
    {
        var pages = new List<List<long>>();
        long? lastValue = null;
        long total = 0;
        bool first = true;
        while (true)
        {
            (List<PageBatchEntity> rows, long pageTotal) = await db.From<PageBatchEntity>()
                .ToPageAsync(pageSize, o => o.Seq, lastValue, descending: true);
            if (first)
            {
                total = pageTotal;
                first = false;
            }
            if (rows.Count == 0)
                break;
            pages.Add([.. rows.Select(static r => r.Seq)]);
            lastValue = rows[^1].Seq;
        }
        return (pages, total);
    }
}

[Table("palorm_page_batch")]
internal sealed partial class PageBatchEntity
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("seq")]
    public long Seq { get; set; }
}

[Table("palorm_page_batch_sd")]
[SoftDelete]
internal sealed partial class PageBatchSoftDeleteEntity
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    public long Id { get; set; }

    [Column("seq")]
    public long Seq { get; set; }

    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
