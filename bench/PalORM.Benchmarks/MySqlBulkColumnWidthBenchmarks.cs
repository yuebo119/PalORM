using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Dapper;
using MySqlConnector;
using PalORM;
using PalORM.MySql;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// MySQL 2 列实体批量插入基准——ParameterNameCache 扩容（B1）的真库分配观测
//
// 为什么独立成类而不并进 MySqlBenchmarks：
//   MySqlBenchmarks 有一个**参数级** [IterationSetup]（对类内全部用例生效），每次迭代都
//   DROP+CREATE+重插 10K 行 bench_orders。若把本基准并进去，它的每次迭代都要白付那 10K 行
//   重置（DROP/CREATE/INSERT 各一次往返 + 实体物化），耗时会稀释待测差异、分配会掺入重置
//   本身的垃圾。独立类只重置自己那张 2 列表。
//
// 2 列实体的选择理由见 BenchmarkEntities.BenchOrder2Col 的注释：
//   主键 AutoIncrement=false → 两列都进 InsertColumns → poolSize = 1000×2 = 2000，
//   越界段 976 个占该批参数 49%，参数名分配在总分配中的比例显著高于 4 列实体。
// ═══════════════════════════════════════════════════════════════

/// <summary>MySQL 2 列实体批量插入——Dapper 基线 vs PalORM 多值 INSERT。
/// <para><b>读法</b>：本组的主要价值是 <c>Allocated</c> 字段（BDN MemoryDiagnoser 精确计数），
/// 不是耗时——MySQL 多值 INSERT 的往返与驱动缓冲占绝对主导，参数名那几十 KB在毫秒级方差里
/// 测不出。要看参数名的确定性收益，用隔离微基准（见 CHANGELOG「未发布·性能轮三」B1 条）。</para>
/// <para><b>前提</b>：服务端 <c>local_infile=OFF</c> 时 PalORM 走多值 INSERT 回退路径
/// （才有满批参数池）；若为 ON 则走 MySqlBulkCopy，本基准不适用。</para></summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: BenchmarkConfig.FastLaunch,
           warmupCount: BenchmarkConfig.FastWarmup,
           iterationCount: BenchmarkConfig.FastIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Table/column names are compile-time constants; values are parameterized.")]
public class MySqlBulkColumnWidthBenchmarks : IAsyncDisposable
{
    private const string TableName = "bench_orders_2col";
    private const int RowCount = 10000;
    private const int BatchSize = 1000;

    private static readonly string Cs = Environment.GetEnvironmentVariable("PALORM_BENCH_MYSQL")
        ?? throw new InvalidOperationException("Set PALORM_BENCH_MYSQL env var for MySQL benchmarks.");
    private MySqlConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = Cs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenMySql(Cs);
        await CreateTableAsync(_keeper);
    }

    /// <summary>每次迭代清空——只 DROP+CREATE 本表，不碰 bench_orders。</summary>
    [IterationSetup]
    public async Task IterationSetup()
        => await CreateTableAsync(_keeper!);

    private static async Task CreateTableAsync(MySqlConnection conn)
    {
        await BenchmarkConfig.ExecMySqlAsync(
            conn, $"DROP TABLE IF EXISTS {TableName}");
        // 主键非自增：应用侧赋值（与 BenchOrder2Col 的 AutoIncrement=false 对应）
        await BenchmarkConfig.ExecMySqlAsync(
            conn, $"CREATE TABLE {TableName} (id BIGINT PRIMARY KEY, payload TEXT NOT NULL)");
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null)
        {
            try
            {
                await BenchmarkConfig.ExecMySqlAsync(_keeper, $"DROP TABLE IF EXISTS {TableName}");
            }
            catch (MySqlException)
            {
                // 清理失败不影响基准结果（表由下次 GlobalSetup 重建）
            }
            await _keeper.DisposeAsync();
        }
    }

    // ─── Dapper 基线：同一张表的多行 INSERT ───
    [Benchmark(Baseline = true), BenchmarkCategory("BulkInsert2Col")]
    public async Task<int> Dapper_MultiRowInsert_10000_2Col()
    {
        using var c = BenchmarkConfig.OpenMySql(Cs);
        var items = Enumerable.Range(0, RowCount)
            .Select(i => new BenchOrder2Col { id = i + 1, payload = $"D{i}" }).ToArray();
        return await c.ExecuteAsync(
            $"INSERT INTO {TableName} (id, payload) VALUES (@id, @payload)", items);
    }

    // ─── PalORM：多值 INSERT 满批参数池路径 ───
    [Benchmark, BenchmarkCategory("BulkInsert2Col")]
    public async Task<long> PalORM_BulkInsert_10000_2Col()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(_options);
        var items = Enumerable.Range(0, RowCount)
            .Select(i => new BenchOrder2Col { id = i + 1, payload = $"B{i}" }).ToArray();
        return await db.BulkInsertAsync(items, batchSize: BatchSize);
    }
}
