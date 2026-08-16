using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Dapper;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 批量基准——BulkInsert + BulkUpdate + BulkDelete（固定量 + Params 矩阵）
// v5.0 基准体系重构：拆自原 SqliteBenchmarks（BulkInsert 4 类）+ BulkInsertScaleBenchmarks（2 类）
// v5.0 优化：IterationSetup 用 DROP+CREATE 替代 DELETE（快 100x）
// r19/ITM-687/689：
//  - 固定量方法与 Params 矩阵拆为两个类——类级 [Params] 不再让固定量重复跑 4 组相同负载
//  - 更新/删除类基准的 IterationSetup 预插数据（此前空表恒更新/删除 0 行，测的是空操作）
//  - category 名实对齐：BulkUpdate_1000 → BulkUpdate；BulkDelete_500 → BulkDelete
// ═══════════════════════════════════════════════════════════════

/// <summary>固定量批量基准（10K 插入 / 1K 更新 / 500 删除）——无参数矩阵。</summary>
[MemoryDiagnoser]
// 标准基准配置——正式报告用，统计可信度中
[SimpleJob(launchCount: BenchmarkConfig.StandardLaunch, warmupCount: BenchmarkConfig.StandardWarmup, iterationCount: BenchmarkConfig.StandardIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class BulkBenchmarksFixed : IAsyncDisposable
{
    private const string CreateTableSql =
        "DROP TABLE IF EXISTS bench_orders; CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)";

    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = BenchmarkConfig.SqliteCs };
    private List<object>? _deleteKeys;

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        await BenchmarkConfig.ExecSqliteAsync(_keeper, CreateTableSql);
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    private void ResetTable()
    {
        // v5.0 优化：DROP+CREATE 替代 DELETE FROM——快 100x
        using var cmd = _keeper!.CreateCommand();
        cmd.CommandText = CreateTableSql;
        cmd.ExecuteNonQuery();
    }

    private async Task InsertFixedRowsAsync(int count)
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = Enumerable.Range(0, count)
            .Select(i => new BenchOrder { status = $"S{i}", total = i * 10m, created_at = 0 })
            .ToList();
        await db.BulkInsertAsync(items);
    }

    [IterationSetup(Target = nameof(Dapper_MultiRowInsert_10000))]
    public void DapperMultiRowInsertSetup() => ResetTable();

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<int> Dapper_MultiRowInsert_10000()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"D{i}", total = i * 10m, created_at = 0 }).ToArray();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at)", items);
    }

    [IterationSetup(Target = nameof(PalORM_BulkInsert_10000))]
    public void PalORMBulkInsertSetup() => ResetTable();

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkInsert_10000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"B{i}", total = i * 10m, created_at = 0 }).ToList();
        return await db.BulkInsertAsync(items);
    }

    [IterationSetup(Target = nameof(PalORM_BulkUpdate_1000))]
    public async Task BulkUpdateSetup() => await InsertFixedRowsAsync(1000);

    [Benchmark, BenchmarkCategory("BulkUpdate")]
    public async Task<long> PalORM_BulkUpdate_1000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = await db.From<BenchOrder>().Take(1000).ToListAsync();
        foreach (var item in items) { item.status = "BU"; }
        return await db.BulkUpdateAsync(items);
    }

    [IterationSetup(Target = nameof(PalORM_BulkDelete_500))]
    public async Task BulkDeleteSetup()
    {
        await InsertFixedRowsAsync(500);
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = await db.From<BenchOrder>().Take(500).ToListAsync();
        _deleteKeys = [.. items.Select(x => (object)x.id)];
    }

    [Benchmark, BenchmarkCategory("BulkDelete")]
    public async Task<long> PalORM_BulkDelete_500()
    {
        // r19/ITM-687：数据布置移入 IterationSetup——测量体只含被测删除操作
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.BulkDeleteAsync<BenchOrder>(_deleteKeys!);
    }
}

/// <summary>Params 矩阵缩放（100/1K/10K/100K）——源 BulkInsertScaleBenchmarks。</summary>
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
    private readonly DbOptions _options = new() { ConnectionString = BenchmarkConfig.SqliteCs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        await BenchmarkConfig.ExecSqliteAsync(_keeper, CreateTableSql);
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    private void ResetTable()
    {
        using var cmd = _keeper!.CreateCommand();
        cmd.CommandText = CreateTableSql;
        cmd.ExecuteNonQuery();
    }

    private async Task InsertRowsAsync(int count)
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = Enumerable.Range(0, count)
            .Select(i => new BenchOrder { status = $"S{i}", total = i * 10m, created_at = 0 })
            .ToList();
        await db.BulkInsertAsync(items);
    }

    [IterationSetup(Target = nameof(PalORM_BulkInsert_Scaled))]
    public void PalORMBulkInsertScaledSetup() => ResetTable();

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkInsert_Scaled()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var batch = Enumerable.Range(0, RowCount)
            .Select(i => new BenchOrder { status = $"B{i}", total = i, created_at = i }).ToArray();
        return await db.BulkInsertAsync(batch, batchSize: 500);
    }

    [IterationSetup(Target = nameof(Dapper_MultiRowInsert_Scaled))]
    public void DapperMultiRowInsertScaledSetup() => ResetTable();

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
    // r19/ITM-688：单臂（无 ORM 对照同 SQL）——不产 Ratio，BENCHMARKS.md 已声明

    [IterationSetup(Target = nameof(PalORM_BulkUpdateBatch_Scaled))]
    public async Task BulkUpdateBatchScaledSetup() => await InsertRowsAsync(RowCount);

    [Benchmark, BenchmarkCategory("BulkUpdateBatch")]
    public async Task<long> PalORM_BulkUpdateBatch_Scaled()
    {
        // v5.0 阶段 4.3b：单语句批量 UPDATE（对比 BulkUpdate 逐条）
        // r19/ITM-687：id=1..RowCount 由 IterationSetup 预插——不再更新空表
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var entities = Enumerable.Range(0, RowCount)
            .Select(i => new BenchOrder { id = i + 1, status = "U", total = i, created_at = i }).ToList();
        return await db.BulkUpdateBatchAsync(entities);
    }
}
