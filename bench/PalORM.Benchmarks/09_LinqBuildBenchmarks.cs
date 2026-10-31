using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 查询构建与 LINQ 管线微基准（2026-10-31 极致性能轮）
//
// 目的：把「From<T> 构建 / Where / OrderBy / 执行」的每查询固定成本
// 拆成可复现的 BDN 条目，用于后续字符串重载、链上哈希、延迟文本、
// 单租约与健康路径扁平化的 A/B 判据。
//
// 口径：shared-cache 内存库（BenchmarkConfig.SqliteCs），SeedRows 种子行；
// Build 组只构 builder 不执行，Query 组执行单行查询，Session 组对比
// per-op 会话与 DataSessionPool 租还。
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[SimpleJob(launchCount: BenchmarkConfig.FastLaunch,
           warmupCount: BenchmarkConfig.FastWarmup,
           iterationCount: BenchmarkConfig.FastIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class LinqBuildBenchmarks : IAsyncDisposable
{
    private SqliteConnection? _keeper;
    private DataSession<SqliteProvider>? _db;
    private DataSessionPool<SqliteProvider>? _pool;
    private readonly DbOptions _options = new() { ConnectionString = BenchmarkConfig.SqliteCs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        await BenchmarkConfig.SeedSqliteAsync(_keeper);
        _db = await DataSession<SqliteProvider>.CreateAsync(_options);
        _pool = DataSessionPool<SqliteProvider>.Create(_options, maxIdleSize: 2);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
        if (_pool is not null) await _pool.DisposeAsync();
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    // ─── 构建链（不触库）───────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Build")]
    public void From_Only()
    {
        var builder = _db!.From<BenchOrder>();
        GC.KeepAlive(builder);
    }

    [Benchmark, BenchmarkCategory("Build")]
    public void From_Where()
    {
        var builder = _db!.From<BenchOrder>().Where($"id = {1}");
        GC.KeepAlive(builder);
    }

    [Benchmark, BenchmarkCategory("Build")]
    public void From_Where_OrderBy_Expression()
    {
        var builder = _db!.From<BenchOrder>().Where($"id = {1}").OrderBy(o => o.id);
        GC.KeepAlive(builder);
    }

    [Benchmark, BenchmarkCategory("Build")]
    public void From_Where_OrderBy_String()
    {
        var builder = _db!.From<BenchOrder>().Where($"id = {1}").OrderBy("id");
        GC.KeepAlive(builder);
    }

    // ─── 执行（单行）──────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Query")]
    public async Task<BenchOrder?> GetAsync_One()
        => await _db!.GetAsync<BenchOrder>(1);

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> ToList_Where()
        => await _db!.From<BenchOrder>().Where($"id = {1}").ToListAsync();

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> ToList_Where_OrderBy()
        => await _db!.From<BenchOrder>().Where($"id = {1}").OrderBy(o => o.id).Take(10).ToListAsync();

    // ─── 会话生命周期（同一查询，两种会话形态）──────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Session")]
    public async Task<BenchOrder?> PerOp_GetAsync()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.GetAsync<BenchOrder>(1);
    }

    [Benchmark, BenchmarkCategory("Session")]
    public async Task<BenchOrder?> Pooled_GetAsync()
    {
        await using var db = await _pool!.RentAsync();
        return await db.GetAsync<BenchOrder>(1);
    }
}
