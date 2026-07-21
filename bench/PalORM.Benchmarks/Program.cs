using System.Diagnostics.CodeAnalysis;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Dapper;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

// Dapper.AOT: module 级标注触发源生成——与 PalORM 源生成做公平对照
[assembly: DapperAot]
// 基准测试项目不是库——SuppressMessage 放宽
[assembly: SuppressMessage("Design", "CA1515", Justification = "BenchmarkDotNet requires public types.")]

namespace PalORM.Benchmarks;

/// <summary>
/// 基准测试入口——支持选择性运行：
///   dotnet run -c Release                        # 全部基准
///   dotnet run -c Release -- --filter '*Sqlite*' # 仅 SQLite 竞品对照
///   dotnet run -c Release -- --filter '*SqlBuild*' # 仅 SQL 构建
///   dotnet run -c Release -- --filter '*Provider*' # 仅 PG/MySQL 专有路径
/// </summary>
public static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

// ─── 共享实体（属性名 = 列名，PalORM 和 Dapper 公平映射）───

[Table("bench_orders")]
public sealed partial class BenchOrder
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("status")] public string status { get; set; } = "";
    [Column("total")] public decimal total { get; set; }
    [Column("created_at")] public long created_at { get; set; }
}

// ═══════════════════════════════════════════════════════════════
// Region 1: SQLite 竞品对照（4× Query + 4× Write）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class SqliteBenchmarks : IAsyncDisposable
{
    private const int SeedRows = 1000;
    private const string Cs = "Data Source=bench;Mode=Memory;Cache=Shared";
    private SqliteConnection? _keeper;

    [GlobalSetup]
    public async Task Setup()
    {
        // keeper 持有共享内存连接——关闭后内存库消失
        _keeper = new SqliteConnection(Cs);
        await _keeper.OpenAsync();
        using var cmd = _keeper.CreateCommand();
        cmd.CommandText = "CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)";
        await cmd.ExecuteNonQueryAsync();
        for (int i = 0; i < SeedRows; i++)
        {
            cmd.CommandText = $"INSERT INTO bench_orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    private static SqliteConnection OpenConn()
    {
        var c = new SqliteConnection(Cs);
        c.Open();
        return c;
    }

    // ─── Query 4× 对照 ───

    [Benchmark(Baseline = true)]
    public async Task<List<BenchOrder>> RawAdo_Query1000()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders LIMIT 1000";
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<BenchOrder>(SeedRows);
        while (await r.ReadAsync())
            list.Add(new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) });
        return list;
    }

    [Benchmark]
    public async Task<List<BenchOrder>> Dapper_Query1000()
    {
        using var c = OpenConn();
        var rows = await c.QueryAsync<BenchOrder>("SELECT id, status, total, created_at FROM bench_orders LIMIT 1000");
        return rows.AsList();
    }

    [Benchmark]
    public async Task<List<BenchOrder>> PalORM_Query1000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = Cs });
        return await db.From<BenchOrder>().ToListAsync();
    }

    // ─── Insert 4× 对照 ───

    [Benchmark]
    public async Task<long> RawAdo_Insert()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (status, total, created_at) VALUES ('B', 99, 0); SELECT last_insert_rowid();";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Benchmark]
    public async Task<int> Dapper_Insert()
    {
        using var c = OpenConn();
        return await c.ExecuteAsync("INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at)",
            new BenchOrder { status = "B", total = 99m, created_at = 0 });
    }

    [Benchmark]
    public async Task<BenchOrder> PalORM_Insert()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = Cs });
        return await db.InsertAsync(new BenchOrder { status = "B", total = 99m, created_at = 0 });
    }

    [Benchmark]
    public async Task<long> PalORM_BulkInsert1000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = Cs });
        var items = Enumerable.Range(0, 1000).Select(i => new BenchOrder { status = $"B{i}", total = i * 10m, created_at = 0 }).ToList();
        return await db.BulkInsertAsync(items);
    }
}

// ═══════════════════════════════════════════════════════════════
// Region 2: SQL 构建（零 I/O——证明 struct + ValueStringBuilder 零分配）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class SqlBuildBenchmarks
{
    // 预构造 QueryBuilder（排除构建开销外的 I/O 初始化）
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
        // 传统 StringBuilder 对照——证明 ValueStringBuilder 的零分配优势
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

// ═══════════════════════════════════════════════════════════════
// Region 3: 高级特性开销（缓存 + 观测性）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class FeatureBenchmarks : IAsyncDisposable
{
    private const int SeedRows = 1000;
    private const string Cs = "Data Source=feat;Mode=Memory;Cache=Shared";
    private SqliteConnection? _keeper;

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = new SqliteConnection(Cs);
        await _keeper.OpenAsync();
        using var cmd = _keeper.CreateCommand();
        cmd.CommandText = "CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)";
        await cmd.ExecuteNonQueryAsync();
        for (int i = 0; i < SeedRows; i++)
        {
            cmd.CommandText = $"INSERT INTO bench_orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<List<BenchOrder>> Query_NoCache()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = Cs });
        return await db.From<BenchOrder>().ToListAsync();
    }

    [Benchmark]
    public async Task<List<BenchOrder>> Query_CacheHit()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = Cs });
        // 第一次填充缓存
        await db.From<BenchOrder>().WithCache("feat-bench", TimeSpan.FromMinutes(5)).ToListAsync();
        // 第二次命中缓存——零数据库 I/O
        return await db.From<BenchOrder>().WithCache("feat-bench", TimeSpan.FromMinutes(5)).ToListAsync();
    }

    [Benchmark]
    public async Task<List<BenchOrder>> Query_WithTracing()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = Cs });
        return await db.From<BenchOrder>().WithTracing().ToListAsync();
    }

    [Benchmark]
    public async Task<List<BenchOrder>> Query_WithMetrics()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = Cs });
        return await db.From<BenchOrder>().WithMetrics("feat-bench").ToListAsync();
    }
}
