using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Dapper;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 批量基准——BulkInsert + BulkUpdate + BulkDelete（含固定量 + Params 矩阵）
// v5.0 基准体系重构：拆自原 SqliteBenchmarks（BulkInsert 4 类）+ BulkInsertScaleBenchmarks（2 类）
// v5.0 优化：IterationSetup 用 DROP+CREATE 替代 DELETE（快 100x）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// 标准基准配置——正式报告用，统计可信度中
[SimpleJob(launchCount: BenchmarkConfig.StandardLaunch, warmupCount: BenchmarkConfig.StandardWarmup, iterationCount: BenchmarkConfig.StandardIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class BulkBenchmarks : IAsyncDisposable
{
    private const string CreateTableSql =
        "DROP TABLE IF EXISTS bench_orders; CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)";

    [Params(100, 1000, 10000, 100000)]
    public int RowCount { get; set; }

    private SqliteConnection? _keeper;
    private DbOptions _options = new() { ConnectionString = BenchmarkConfig.SqliteCs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        // 建表（bench_orders 由 BenchOrder 实体映射）
        await BenchmarkConfig.ExecSqliteAsync(_keeper, CreateTableSql);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // v5.0 优化：DROP+CREATE 替代 DELETE FROM——快 100x（DELETE 在大表上要逐页扫描+回收）
        // bench_orders 表由 BenchOrder 实体映射
        using var cmd = _keeper!.CreateCommand();
        cmd.CommandText = CreateTableSql;
        cmd.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    // ═══════ 固定量批量（10000 / 1000 / 500）——源 SqliteBenchmarks 沿用 ═══════

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<int> Dapper_MultiRowInsert_10000()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"D{i}", total = i * 10m, created_at = 0 }).ToArray();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at)", items);
    }

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkInsert_10000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"B{i}", total = i * 10m, created_at = 0 }).ToList();
        return await db.BulkInsertAsync(items);
    }

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkUpdate_1000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = await db.From<BenchOrder>().Take(1000).ToListAsync();
        foreach (var item in items) { item.status = "BU"; }
        return await db.BulkUpdateAsync(items);
    }

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkDelete_500()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = Enumerable.Range(0, 500)
            .Select(i => new BenchOrder { status = $"BD{i}", total = 0m, created_at = 0 }).ToList();
        await db.BulkInsertAsync(items);
        var keys = items.Select(x => (object)x.id).ToList();
        return await db.BulkDeleteAsync<BenchOrder>(keys);
    }

    // ═══════ Params 矩阵缩放（100/1K/10K/100K）——源 BulkInsertScaleBenchmarks ═══════

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkInsert_Scaled()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var batch = Enumerable.Range(0, RowCount)
            .Select(i => new BenchOrder { status = $"B{i}", total = i, created_at = i }).ToArray();
        return await db.BulkInsertAsync(batch, batchSize: 500);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("BulkInsert")]
    public async Task<int> Dapper_MultiRowInsert_Scaled()
    {
        var batch = Enumerable.Range(0, RowCount)
            .Select(i => new { status = $"B{i}", total = (double)i, created_at = (long)i }).ToArray();
        using var c = new SqliteConnection(BenchmarkConfig.SqliteCs);
        await c.OpenAsync();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at)",
            batch);
    }

    // ═══════ v5.0 阶段 4.3b 新增：单语句批量 UPDATE ═══════

    [Benchmark, BenchmarkCategory("BulkUpdateBatch")]
    public async Task<long> PalORM_BulkUpdateBatch_Scaled()
    {
        // v5.0 阶段 4.3b：单语句批量 UPDATE（对比 BulkUpdate 逐条）
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var entities = Enumerable.Range(0, RowCount)
            .Select(i => new BenchOrder { id = i + 1, status = "U", total = i, created_at = i }).ToList();
        return await db.BulkUpdateBatchAsync(entities);
    }
}
