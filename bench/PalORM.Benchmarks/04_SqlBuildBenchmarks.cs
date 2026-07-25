using System.Text;
using BenchmarkDotNet.Attributes;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// SQL 构建（零 I/O）
// v5.0 基准体系重构：拆自原 SqliteBenchmarks 之外的 SqlBuildBenchmarks（独立类）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// P0 修复：SqlBuild 是 nanosecond 级，原 3/5/10 在慢机上 Error/Mean 达 14%。
// 提升至 5/10/15 严格配置 + Throughput 模式（多次调用取均值降低单次抖动）。
[SimpleJob(launchCount: BenchmarkConfig.PrecisionLaunch,
           warmupCount: BenchmarkConfig.PrecisionWarmup,
           iterationCount: BenchmarkConfig.PrecisionIterations,
           invocationCount: BenchmarkConfig.PrecisionInvocations)]
public class SqlBuildBenchmarks
{
    private DataSession<SqliteProvider>? _db;
    private QueryBuilder<BenchOrder> _simpleBuilder;
    private QueryBuilder<BenchOrder> _complexBuilder;

    [GlobalSetup]
    public async Task Setup()
    {
        _db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = "Data Source=:memory:" });
        _simpleBuilder = _db.From<BenchOrder>();
        _complexBuilder = _db.From<BenchOrder>()
            .Where($"status = {"active"}")
            .OrderBy(o => o.id, descending: true)
            .Take(100)
            .Skip(10);
    }

    [Benchmark(Baseline = true)]
    public string StringBuilder_BuildSql()
    {
        var sb = new StringBuilder(256);
        sb.Append("SELECT \"bench_orders\".\"id\", \"bench_orders\".\"status\", \"bench_orders\".\"total\", \"bench_orders\".\"created_at\" ");
        sb.Append("FROM \"bench_orders\" ");
        sb.Append("WHERE \"bench_orders\".\"status\" = @p0 ");
        sb.Append("ORDER BY \"bench_orders\".\"id\" DESC ");
        sb.Append("LIMIT 100 OFFSET 10 ");
        return sb.ToString().TrimEnd();
    }

    [Benchmark]
    public string PalORM_BuildSql_Simple()
        => _simpleBuilder.ToSql();

    [Benchmark]
    public string PalORM_BuildSql_Complex()
        => _complexBuilder.ToSql();
}
