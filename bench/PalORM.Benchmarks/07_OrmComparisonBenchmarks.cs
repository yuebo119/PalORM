using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Dapper;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// Dapper Cache Impact 专项（对齐 Dapper 官方 DapperCacheImpact.cs）
//
// PalORM 卖点：源生成 RowFactory，零运行时反射、零 IL 缓存查找。
// Dapper：首次（或参数形状变化时）需构建 + 缓存 IL 物化代码。
// 本基准对照三种参数形状下两者的查询延迟，证明源生成路径的稳定性。
//
// v5.0 基准体系重构：拆自原 DapperCacheImpactBenchmarks → 重命名为 OrmComparisonBenchmarks
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// 快速验证配置——SQLite 本地对照基准，参数形状差异用多迭代摊平抖动（对照结论而非绝对数值）
[SimpleJob(launchCount: BenchmarkConfig.FastLaunch,
           warmupCount: BenchmarkConfig.FastWarmup,
           iterationCount: BenchmarkConfig.FastIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class OrmComparisonBenchmarks : IAsyncDisposable
{
    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = BenchmarkConfig.CacheSqliteCs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenSqlite(BenchmarkConfig.CacheSqliteCs);
        await BenchmarkConfig.ExecSqliteAsync(_keeper!,
            "CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)");
        for (int i = 0; i < BenchmarkConfig.SeedRows; i++)
            await BenchmarkConfig.ExecSqliteAsync(_keeper!,
                $"INSERT INTO bench_orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})");
        // 给 status 加索引——消除 VaryingShape 场景的全表扫描主导，分离 ORM cache miss 代价
        await BenchmarkConfig.ExecSqliteAsync(_keeper!,
            "CREATE INDEX ix_bench_orders_status ON bench_orders(status)");
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    // ─── 场景 1：稳定参数形状（Dapper 缓存命中后的稳态） ───
    // Dapper：第 N 次（N > 1）后 IL 缓存命中；PalORM：源生成，恒定。

    [Benchmark, BenchmarkCategory("StableShape")]
    public async Task<BenchOrder?> Dapper_StableShape()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.CacheSqliteCs);
        // 固定参数形状 {id, status}——Dapper 复用已缓存的 IL 物化器
        return await c.QueryFirstOrDefaultAsync<BenchOrder>(
            "SELECT id, status, total, created_at FROM bench_orders WHERE id = @id AND status = @status",
            new { id = 1L, status = "S1" });
    }

    [Benchmark, BenchmarkCategory("StableShape")]
    public async Task<BenchOrder?> PalORM_StableShape()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>()
            .Where($"id = {1L} AND status = {"S1"}")
            .FirstOrDefaultAsync();
    }

    // ─── 场景 2：变化参数形状（Dapper 每次缓存 miss，重 build IL） ───
    // 每次迭代不同的 status 字段长度 → Dapper 哈希键变化 → 缓存未命中
    // PalORM 源生成路径不受参数形状影响

    private int _shape;
    [Benchmark, BenchmarkCategory("VaryingShape")]
    public async Task<BenchOrder?> Dapper_VaryingShape()
    {
        // 每次迭代 status 不同长度——Dapper 参数形状哈希变化，缓存 miss
        var status = new string('X', (++_shape % 32) + 1);
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.CacheSqliteCs);
        return await c.QueryFirstOrDefaultAsync<BenchOrder>(
            "SELECT id, status, total, created_at FROM bench_orders WHERE status = @status",
            new { status });
    }

    [Benchmark, BenchmarkCategory("VaryingShape")]
    public async Task<BenchOrder?> PalORM_VaryingShape()
    {
        var status = new string('X', (++_shape % 32) + 1);
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>()
            .Where($"status = {status}")
            .FirstOrDefaultAsync();
    }
}
