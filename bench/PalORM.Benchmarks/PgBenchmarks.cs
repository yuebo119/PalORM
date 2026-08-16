using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Dapper;
using Npgsql;
using PalORM;
using PalORM.PostgreSql;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 远程 PostgreSQL 基准
// 连接串从环境变量读取，避免硬编码：
//   PALORM_BENCH_PG="Host=...;Port=5432;Username=...;Password=...;Database=palorm_bench"
// v5.0 基准体系重构：拆自原 Program.cs 的 PgBenchmarks，引用 BenchmarkConfig 辅助方法
// v5.0 阶段 4.3b：新增 BulkUpdateBatch_PG（PG UPDATE FROM VALUES 方言验证）
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

    // r19/ITM-688：每次迭代重置为 10K seed——此前 BulkInsert 在 warmup+iteration 持续
    // 插入，表从 10K 膨胀到 ~110K，后期迭代负载失真；批量组为 PalORM 特性单臂
    //（无同 SQL 的 ORM 对照），不产组内 Ratio（BENCHMARKS.md 已声明）。
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

    // ─── 查询 ───
    [Benchmark(Baseline = true), BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> ADO_NET_QueryAll()
    {
        using var c = BenchmarkConfig.OpenPg(Cs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders";
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<BenchOrder>(BenchmarkConfig.SeedRows);
        while (await r.ReadAsync())
            list.Add(new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) });
        return list;
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> Dapper_QueryAll()
    {
        using var c = BenchmarkConfig.OpenPg(Cs);
        return (await c.QueryAsync<BenchOrder>("SELECT id, status, total, created_at FROM bench_orders")).AsList();
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> PalORM_QueryAll()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>().ToListAsync();
    }

    // ─── 主键查询 ───
    [Benchmark(Baseline = true), BenchmarkCategory("GetByKey")]
    public async Task<BenchOrder?> ADO_NET_GetByKey()
    {
        using var c = BenchmarkConfig.OpenPg(Cs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders WHERE id = 5000";
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) } : null;
    }

    [Benchmark, BenchmarkCategory("GetByKey")]
    public async Task<BenchOrder?> PalORM_GetByKey()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(_options);
        return await db.GetAsync<BenchOrder>(5000L);
    }

    // ─── 插入 ───
    [Benchmark(Baseline = true), BenchmarkCategory("Insert")]
    public async Task<long> ADO_NET_Insert()
    {
        using var c = BenchmarkConfig.OpenPg(Cs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (status, total, created_at) VALUES ('X', 1.0, 1) RETURNING id";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> PalORM_Insert()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(_options);
        var e = await db.InsertAsync(new BenchOrder { status = "X", total = 1m, created_at = 1 });
        return e.id;
    }

    // ─── 批量插入（PG 走 Binary COPY）───
    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task PalORM_BulkInsert_10000()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(_options);
        var batch = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"B{i}", total = i, created_at = i }).ToArray();
        await db.BulkInsertAsync(batch, batchSize: 1000);
    }

    // ─── 批量更新（v5.0 阶段 4.3b：PG UPDATE FROM VALUES 方言）───
    [Benchmark, BenchmarkCategory("BulkUpdateBatch")]
    public async Task PalORM_BulkUpdateBatch_PG()
    {
        // v5.0 阶段 4.3b：PG UPDATE FROM VALUES 方言验证
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(_options);
        var entities = Enumerable.Range(1, 1000)
            .Select(i => new BenchOrder { id = i, status = "U", total = i, created_at = i }).ToList();
        await db.BulkUpdateBatchAsync(entities);
    }
}
