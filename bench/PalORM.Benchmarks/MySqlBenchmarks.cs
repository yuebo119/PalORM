using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using MySqlConnector;
using PalORM;
using PalORM.MySql;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 远程 MySQL 基准——**只保留 PerfHub 无同名覆盖的项**
//
// 2026-09-23 精简：原 9 项里删去 8 项（ADO/Dapper/PalORM 的 QueryAll、GetByKey、Insert
// 与 PalORM_BulkInsert_10000）。理由：
//  · 它们的跨方言覆盖已由 PerfHub 权威承担（规范 §1.1「每个测量项必须有唯一权威夹具」），
//    而 PerfHub 的档位与形状与这里不同，同一件事测两遍只会产生"哪个数字算数"的歧义；
//  · 本类需要真库、不在任何自动路径（CI 与 perf.sh full 都不跑它）、无任何报告引用其结果。
// 保留 PalORM_BulkUpdateBatch_MySql：它是 MySQL 单语句 CASE WHEN 方言路径的**唯一**覆盖
//（PerfHub 的 BulkUpdate 是逐条 UPDATE），PerfHub 无同名项。
//
// 连接串从环境变量读取，避免硬编码：
//   PALORM_BENCH_MYSQL="Server=...;Port=3306;User ID=...;Password=...;Database=palorm_bench"
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// 快速验证配置——远程 DB 基准用（网络延迟 > 统计精度，快速迭代优先）
[SimpleJob(launchCount: BenchmarkConfig.FastLaunch,
           warmupCount: BenchmarkConfig.FastWarmup,
           iterationCount: BenchmarkConfig.FastIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class MySqlBenchmarks : IAsyncDisposable
{
    private static readonly string Cs = Environment.GetEnvironmentVariable("PALORM_BENCH_MYSQL")
        ?? throw new InvalidOperationException("Set PALORM_BENCH_MYSQL env var for MySQL benchmarks.");
    private MySqlConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = Cs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenMySql(Cs);
        await ResetAsync();
    }

    // 每次迭代重置为 10K seed——BulkUpdateBatch 每轮把 1000 行改成 status='U'，
    // 不重置则第二轮起更新的是已改过的行（口径不一致）。
    [IterationSetup]
    public async Task IterationSetup()
        => await ResetAsync();

    private async Task ResetAsync()
    {
        await BenchmarkConfig.ExecMySqlAsync(_keeper!, "DROP TABLE IF EXISTS bench_orders");
        await BenchmarkConfig.ExecMySqlAsync(_keeper!, "CREATE TABLE bench_orders (id BIGINT AUTO_INCREMENT PRIMARY KEY, status TEXT NOT NULL, total DECIMAL(18,6) NOT NULL, created_at BIGINT NOT NULL)");
        var rows = string.Join(", ",
            Enumerable.Range(0, BenchmarkConfig.SeedRows)
                .Select(i => $"('S{i}', {i * 10m}, {i})"));
        await BenchmarkConfig.ExecMySqlAsync(_keeper!,
            $"INSERT INTO bench_orders (status, total, created_at) VALUES {rows}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    // ─── 批量更新（v5.0 阶段 4.3b：MySQL CASE WHEN 方言）───
    [Benchmark, BenchmarkCategory("BulkUpdateBatch")]
    public async Task PalORM_BulkUpdateBatch_MySql()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(_options);
        var entities = Enumerable.Range(1, 1000)
            .Select(i => new BenchOrder { id = i, status = "U", total = i, created_at = i }).ToList();
        await db.BulkUpdateBatchAsync(entities);
    }
}
