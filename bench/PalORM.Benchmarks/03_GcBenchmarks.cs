using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

/// <summary>v5.0 阶段 3.4 GC 装箱专项基准——用 BenchmarkDotNet 正式基准替代手写微基准。
/// <para><b>目标</b>：测量不同操作路径的每行分配字节 + Gen0 频率，估算装箱占比。
/// 装箱发生在 DbParameter.Value = (object)valueType 赋值时。</para>
/// <para><b>装箱估算方法</b>：QueryAsync 路径不经 DbParameter.Value（RowFactory 用 GetInt32/GetDateTime），
/// 其 bytes/row 作为"非装箱基线"。其他操作的 bytes/row 减去基线 = 装箱估算值。</para>
/// <para><b>BoxingTestEntity</b>：4 个值类型列（long/int/decimal/bool），每行装箱精确 128B
/// （long 32B + int 24B + decimal 48B + bool 24B，含对象头 + 对齐填充）。</para>
/// <para><b>3.4 决策指标</b>：装箱占总分配 &gt;20% → 值得做 NpgsqlParameter&lt;T&gt;；&lt;5% → 不做。
/// 实测结果见 docs/boxing-benchmark-design.md。</para></summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: BenchmarkConfig.StandardLaunch, warmupCount: BenchmarkConfig.StandardWarmup, iterationCount: BenchmarkConfig.StandardIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class GcBenchmarks : IAsyncDisposable
{
    [Params(1, 100, 1000, 10000)]
    public int RowCount { get; set; }

    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = BenchmarkConfig.SqliteCs };
    private List<BoxingTestEntity> _entities = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = new SqliteConnection(BenchmarkConfig.SqliteCs);
        await _keeper.OpenAsync();
        await EnsureTableAsync();
    }

    [IterationSetup]
    public async Task IterationSetup()
    {
        // 每次迭代重建表 + 准备实体（DROP+CREATE 比 DELETE 快 100x）
        await BenchmarkConfig.ExecSqliteAsync(_keeper!,
            "DROP TABLE IF EXISTS boxing_test; " +
            "CREATE TABLE boxing_test (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, value INTEGER NOT NULL, price REAL NOT NULL, active INTEGER NOT NULL)");
        _entities = Enumerable.Range(0, RowCount)
            .Select(i => new BoxingTestEntity { Name = $"row-{i}", Value = i, Price = i * 1.5m, Active = (i % 2) == 0 })
            .ToList();
    }

    // ─── InsertAsync：逐条装箱路径 ───
    [Benchmark, BenchmarkCategory("Insert")]
    public async Task Insert_OneByOne()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        foreach (var e in _entities)
            await db.InsertAsync(e);
    }

    // ─── BulkInsertAsync：BindInsertValues 复用路径（仍装箱但省 CreateParameter）───
    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> BulkInsert()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.BulkInsertAsync(_entities);
    }

    // ─── QueryAsync：无装箱对照组（RowFactory 用 GetInt32/GetDecimal，不装箱）───
    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BoxingTestEntity>> Query_NoBoxing_Baseline()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BoxingTestEntity>().ToListAsync();
    }

    // ─── BulkUpdateAsync：逐条 UPDATE 装箱路径 ───
    [Benchmark, BenchmarkCategory("BulkUpdate")]
    public async Task<long> BulkUpdate_OneByOne()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.BulkUpdateAsync(_entities);
    }

    // ─── BulkUpdateBatchAsync：v5.0 单语句批量（CASE WHEN 装箱）───
    [Benchmark, BenchmarkCategory("BulkUpdateBatch")]
    public async Task<long> BulkUpdateBatch_SingleStatement()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.BulkUpdateBatchAsync(_entities);
    }

    private async Task EnsureTableAsync()
    {
        await BenchmarkConfig.ExecSqliteAsync(_keeper!,
            "DROP TABLE IF EXISTS boxing_test; " +
            "CREATE TABLE boxing_test (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, value INTEGER NOT NULL, price REAL NOT NULL, active INTEGER NOT NULL)");
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }
}
