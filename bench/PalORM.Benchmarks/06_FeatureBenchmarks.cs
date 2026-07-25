using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// PalORM 独有特性基准——Transaction / Feature / Advanced / Scale / V5Feature
// v5.0 基准体系重构：拆自原 SqliteBenchmarks（Transaction/Feature/Advanced/Scale 4 类）
// v5.0 新增：V5Feature（SessionSetupSql 开销 + AuditInterceptor 开销）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// 标准基准配置——正式报告用，统计可信度中
[SimpleJob(launchCount: BenchmarkConfig.StandardLaunch, warmupCount: BenchmarkConfig.StandardWarmup, iterationCount: BenchmarkConfig.StandardIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class FeatureBenchmarks : IAsyncDisposable
{
    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = BenchmarkConfig.SqliteCs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        await BenchmarkConfig.SeedSqliteAsync(_keeper!);
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    // ═══════ 事务 ═══════

    [Benchmark, BenchmarkCategory("Transaction")]
    public async Task PalORM_Transaction_Commit()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        await db.WithTransaction(async ct =>
        {
            await db.InsertAsync(new BenchOrder { status = "T1", total = 1m, created_at = 0 }, ct);
            await db.InsertAsync(new BenchOrder { status = "T2", total = 2m, created_at = 0 }, ct);
            await db.InsertAsync(new BenchOrder { status = "T3", total = 3m, created_at = 0 }, ct);
        });
    }

    [Benchmark, BenchmarkCategory("Transaction")]
    public async Task PalORM_Transaction_Rollback()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        try
        {
            await db.WithTransaction(async ct =>
            {
                await db.InsertAsync(new BenchOrder { status = "R1", total = 1m, created_at = 0 }, ct);
                await db.InsertAsync(new BenchOrder { status = "R2", total = 2m, created_at = 0 }, ct);
                throw new InvalidOperationException("bench-rollback");
            });
        }
        catch (InvalidOperationException) { /* 预期回滚 */ }
    }

    [Benchmark, BenchmarkCategory("Transaction")]
    public async Task PalORM_Transaction_Savepoint()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        using var tran = await db.BeginTransactionAsync();
        await db.InsertAsync(new BenchOrder { status = "SP1", total = 1m, created_at = 0 });
        await db.SavepointAsync(tran, "sp1");
        await db.InsertAsync(new BenchOrder { status = "SP2", total = 2m, created_at = 0 });
        await db.RollbackToAsync(tran, "sp1");
        await tran.CommitAsync();
    }

    // ═══════ PalORM 独有特性 ═══════

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchOrder>> PalORM_Query_WhereIn_500()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var ids = Enumerable.Range(1, 500).Select(i => (long)i).ToArray();
        return await db.From<BenchOrder>().WhereIn(o => o.id, ids).ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchSoft>> PalORM_Query_SoftDelete_Filter()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchSoft>().ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchOrder>> PalORM_Query_WithTracing()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>().WithTracing().ToListAsync();
    }

    // ═══════ v4.0 P1 新增：维度覆盖基准 ═══════

    // 多结果集 GridReader（两个结果集，验证多 ResultSet 物化性能）
    [Benchmark, BenchmarkCategory("Advanced")]
    public async Task<int> PalORM_GridReader_TwoResultSets()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        // QueryMultipleAsync 是 QueryBuilder<T> 的扩展——从 From<T>() 发起
        await using var grid = await db.From<BenchOrder>().QueryMultipleAsync(
            $"SELECT id, status, total, created_at FROM bench_orders WHERE id <= 100 ORDER BY id; SELECT id, status, total, created_at FROM bench_orders WHERE id > 9900 ORDER BY id");
        var first = await grid.ReadAsync<BenchOrder>();
        var second = await grid.ReadAsync<BenchOrder>();
        return first.Count + second.Count;
    }

    // WhereIn 跨批次（1500 个值，SQLite 999 参数上限 → 自动分 2 批）
    [Benchmark, BenchmarkCategory("Advanced")]
    public async Task<List<BenchOrder>> PalORM_WhereIn_CrossBatch_1500()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var ids = Enumerable.Range(1, 1500).Select(i => (long)i).ToArray();
        return await db.From<BenchOrder>().WhereIn(a => a.id, ids).ToListAsync();
    }

    // IAsyncEnumerable 流式查询（逐行消费，不缓存全列表）
    [Benchmark, BenchmarkCategory("Advanced")]
    public async Task<int> PalORM_StreamQuery_IAsyncEnumerable()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        int count = 0;
        await foreach (var _ in db.QueryAsyncEnumerable<BenchOrder>(
            $"SELECT id, status, total, created_at FROM bench_orders WHERE id <= 1000"))
            count++;
        return count;
    }

    // 并发查询（8 个并行 DataSession 各自 GetByKey——模拟多连接场景）
    [Benchmark, BenchmarkCategory("Advanced")]
    public async Task<int> PalORM_Concurrent_GetByKey_8x()
    {
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
            return await db.GetAsync<BenchOrder>(5000) is not null ? 1 : 0;
        });
        var results = await Task.WhenAll(tasks);
        return results.Sum();
    }

    // 小数据量冷启动（10 行——测量首次 JIT + 连接建立开销）
    [Benchmark, BenchmarkCategory("Scale")]
    public async Task<List<BenchOrder>> PalORM_QueryAll_Small_10()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>().Take(10).ToListAsync();
    }

    // ═══════ v5.0 阶段 5.2 / 5.4 新增：拦截器与会话设置开销 ═══════

    [Benchmark, BenchmarkCategory("V5Feature")]
    public async Task PalORM_SessionSetupSql_Overhead()
    {
        // v5.0 阶段 5.2：SessionSetupSql 开销（对比无 SET）
        var opts = new DbOptions { ConnectionString = BenchmarkConfig.SqliteCs, SessionSetupSql = "PRAGMA cache_size = -1000" };
        await using var db = await DataSession<SqliteProvider>.CreateAsync(opts);
        _ = await db.From<BenchOrder>().Take(1).ToListAsync();
    }

    [Benchmark, BenchmarkCategory("V5Feature")]
    public async Task PalORM_AuditInterceptor_Overhead()
    {
        // v5.0 阶段 5.4：AuditInterceptor 开销（对比无拦截器）
        var logger = NullLogger.Instance;
        var opts = new DbOptions { ConnectionString = BenchmarkConfig.SqliteCs, Interceptors = [new AuditInterceptor(logger)] };
        await using var db = await DataSession<SqliteProvider>.CreateAsync(opts);
        _ = await db.From<BenchOrder>().Take(1).ToListAsync();
    }
}
