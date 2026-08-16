using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 纯速度基准（无 MemoryDiagnoser）
// MemoryDiagnoser 每次 GC.Collect + WaitForPendingFinalizers 阻碍 JIT 内联。
// 本类用于交叉验证：如果纯速度显著快（>5%）说明 MemoryDiagnoser 干扰了测量。
// v5.0 基准体系重构：拆自原 SqliteSpeedBenchmarks，引用 BenchmarkConfig.SpeedSqliteCs
// 注意：Speed 类只建 bench_orders 表（无 versioned/soft），不调 SeedSqliteAsync（那个建 3 表+RepoDB）
// ═══════════════════════════════════════════════════════════════

// 注意：无 [MemoryDiagnoser]——纯速度测量，消除 GC.Collect 干扰（刻意保留）
// T-P3-05（T8 配置理由）：复用 BenchmarkConfig.StandardLaunch/Warmup/Iterations 三常量
// 而非本类硬编码——与 01/02/03/06 各基准同配置，结果可跨类比较；纯速度类仅刻意
// 去掉 MemoryDiagnoser（GC.Collect 阻碍 JIT 内联，见类头说明）。
[SimpleJob(launchCount: BenchmarkConfig.StandardLaunch,
           warmupCount: BenchmarkConfig.StandardWarmup,
           iterationCount: BenchmarkConfig.StandardIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class SqliteSpeedBenchmarks : IAsyncDisposable
{
    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = BenchmarkConfig.SpeedSqliteCs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SpeedSqliteCs);
        // Speed 类简化 setup：只建 bench_orders（不调 SeedSqliteAsync 以避免建 3 表 + RepoDB 初始化）
        await BenchmarkConfig.ExecSqliteAsync(_keeper!,
            "CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)");
        for (int i = 0; i < BenchmarkConfig.SeedRows; i++)
            await BenchmarkConfig.ExecSqliteAsync(_keeper!,
                $"INSERT INTO bench_orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})");
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Speed")]
    public async Task<List<BenchOrder>> ADO_NET_QueryAll_Speed()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SpeedSqliteCs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders";
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<BenchOrder>(BenchmarkConfig.SeedRows);
        while (await r.ReadAsync())
            list.Add(new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) });
        return list;
    }

    [Benchmark, BenchmarkCategory("Speed")]
    public async Task<List<BenchOrder>> PalORM_QueryAll_Speed()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>().ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Speed")]
    public async Task<BenchOrder?> PalORM_GetByKey_Speed()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.GetAsync<BenchOrder>(5000L);
    }

    [Benchmark, BenchmarkCategory("Speed")]
    public async Task<long> PalORM_Insert_Speed()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var e = await db.InsertAsync(new BenchOrder { status = "X", total = 1m, created_at = 1 });
        return e.id;
    }
}
