using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Npgsql;
using PalORM;
using PalORM.PostgreSql;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 远程 PostgreSQL 基准——**只保留 PerfHub 无同名覆盖的项**
//
// 2026-09-23 精简：原 9 项里删去 8 项（ADO/Dapper/PalORM 的 QueryAll、GetByKey、Insert
// 与 PalORM_BulkInsert_10000）。理由与 MySqlBenchmarks 同：跨方言覆盖已由 PerfHub 权威承担
//（规范 §1.1），本类需要真库、不在任何自动路径、无报告引用其结果。
// 保留 PalORM_BulkUpdateBatch_PG：它是 PG 单语句 UPDATE FROM VALUES 方言路径的**唯一**覆盖
//（PerfHub 的 BulkUpdate 是逐条 UPDATE），PerfHub 无同名项。
//
// 连接串从环境变量读取，避免硬编码：
//   PALORM_BENCH_PG="Host=...;Port=5432;Username=...;Password=...;Database=palorm_bench"
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// 快速验证配置——远程 DB 基准用（网络延迟 > 统计精度，快速迭代优先）
[SimpleJob(launchCount: BenchmarkConfig.FastLaunch,
           warmupCount: BenchmarkConfig.FastWarmup,
           iterationCount: BenchmarkConfig.FastIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class PgBenchmarks : IAsyncDisposable
{
    private static readonly string Cs = Environment.GetEnvironmentVariable("PALORM_BENCH_PG")
        ?? throw new InvalidOperationException("Set PALORM_BENCH_PG env var for PostgreSQL benchmarks.");
    private NpgsqlConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = Cs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenPg(Cs);
        await ResetAsync();
    }

    // 每次迭代重置为 10K seed——BulkUpdateBatch 每轮把 1000 行改成 status='U'，
    // 不重置则第二轮起更新的是已改过的行（口径不一致）。
    [IterationSetup]
    public async Task IterationSetup()
        => await ResetAsync();

    private async Task ResetAsync()
    {
        await BenchmarkConfig.ExecPgAsync(_keeper!, "DROP TABLE IF EXISTS bench_orders");
        await BenchmarkConfig.ExecPgAsync(_keeper!, "CREATE TABLE bench_orders (id BIGSERIAL PRIMARY KEY, status TEXT NOT NULL, total NUMERIC(18,6) NOT NULL, created_at BIGINT NOT NULL)");
        var rows = string.Join(", ",
            Enumerable.Range(0, BenchmarkConfig.SeedRows)
                .Select(i => $"('S{i}', {i * 10m}, {i})"));
        await BenchmarkConfig.ExecPgAsync(_keeper!,
            $"INSERT INTO bench_orders (status, total, created_at) VALUES {rows}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    // ─── 批量更新（v5.0 阶段 4.3b：PG UPDATE FROM VALUES 方言）───
    [Benchmark, BenchmarkCategory("BulkUpdateBatch")]
    public async Task PalORM_BulkUpdateBatch_PG()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(_options);
        var entities = Enumerable.Range(1, 1000)
            .Select(i => new BenchOrder { id = i, status = "U", total = i, created_at = i }).ToList();
        await db.BulkUpdateBatchAsync(entities);
    }
}
