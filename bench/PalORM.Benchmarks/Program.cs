using System.Diagnostics.CodeAnalysis;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using PalORM.Sqlite;
using PalORM.PostgreSql;
using PalORM.MySql;
// RepoDb（含 Sqlite 扩展，全部位于 RepoDb 命名空间下：SqliteGlobalConfiguration.UseSqlite）
using RepoDb;

[assembly: DapperAot]
[assembly: SuppressMessage("Design", "CA1515", Justification = "BenchmarkDotNet requires public types.")]

namespace PalORM.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

// ─── 实体（属性名 = 列名，不加 [Column]——让 Dapper 和 PalORM 都按属性名映射）───

[Table("bench_orders")]
public sealed partial class BenchOrder
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("status")] public string status { get; set; } = "";
    [Column("total")] public decimal total { get; set; }
    [Column("created_at")] public long created_at { get; set; }
}

[Table("bench_versioned")]
public sealed partial class BenchVersioned
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("version")] [ConcurrencyCheck] public long version { get; set; }
}

[SoftDelete]
[Table("bench_soft")]
public sealed partial class BenchSoft
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("deleted_at")] public string? deleted_at { get; set; }
}

// ═══════════════════════════════════════════════════════════════
// 公平对照基准——同一 SQL / 同一数据 / 每个 ORM 最优路径
// 统一基线：ADO.NET（原名 RawAdo → 改为 ADO_NET）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// 标准基准配置——正式报告用，统计可信度中
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class SqliteBenchmarks : IAsyncDisposable
{
    private const int SeedRows = 10000;
    private const string Cs = "Data Source=bench;Mode=Memory;Cache=Shared";
    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = Cs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = new SqliteConnection(Cs);
        await _keeper.OpenAsync();
        await Exec($"CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)");
        for (int i = 0; i < SeedRows; i++)
            await Exec($"INSERT INTO bench_orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})");
        await Exec("CREATE TABLE bench_versioned (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 0)");
        await Exec("INSERT INTO bench_versioned (name, version) VALUES ('seed', 0)");
        await Exec("CREATE TABLE bench_soft (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, deleted_at TEXT)");
        await Exec("INSERT INTO bench_soft (name) VALUES ('seed')");
        // RepoDB 全局初始化（仅一次，Sqlite provider 探测）
        GlobalConfiguration.Setup().UseSqlite();
    }

    private async Task Exec(string sql)
    {
        using var cmd = _keeper!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
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

    // Dapper 官方 benchmarks/Dapper.Tests.Performance 的 Step() 等价实现：
    // 每次迭代查不同 id，避免同一数据页被 SQLite page cache 命中导致测量失真。
    // 仅用于单点查询（GetByKey）；Update/Upsert 保留固定 id 以保证语义正确。
    // 返回 long：对齐 BenchOrder.id（long）——源生成器 BindDelete 要求 key 类型精确匹配
    private long _counter;
    private long NextId() => (Interlocked.Increment(ref _counter) % SeedRows) + 1;  // 1..SeedRows

    // ═══════ 查询全表 10000 行 ═══════
    // SQL 统一：SELECT id, status, total, created_at FROM bench_orders
    // 所有 ORM 物化为 List<BenchOrder>

    [Benchmark(Baseline = true), BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> ADO_NET_QueryAll()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders";
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<BenchOrder>(SeedRows);
        while (await r.ReadAsync())
            list.Add(new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) });
        return list;
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> Dapper_QueryAll()
    {
        using var c = OpenConn();
        var rows = await c.QueryAsync<BenchOrder>("SELECT id, status, total, created_at FROM bench_orders");
        return rows.AsList();
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> PalORM_QueryAll()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>().ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> RepoDb_QueryAll()
    {
        using var c = OpenConn();
        // 显式表名——BenchOrder 同时有 PalORM [Table]，避免与 RepoDB [Map] 注解冲突
        return (await c.QueryAllAsync<BenchOrder>("bench_orders")).AsList();
    }

    // ═══════ 主键查询 ═══════
    // 使用 NextId() 轮询 id（1..10000）——避免 SQLite page cache 命中导致测量失真
    // 对齐 Dapper 官方 benchmarks/Dapper.Tests.Performance 的 Step() 机制

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<BenchOrder?> ADO_NET_GetByKey()
    {
        var id = NextId();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) };
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<BenchOrder?> Dapper_GetByKey()
    {
        var id = NextId();
        using var c = OpenConn();
        return await c.QueryFirstOrDefaultAsync<BenchOrder>(
            "SELECT id, status, total, created_at FROM bench_orders WHERE id = @id", new { id });
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<BenchOrder?> PalORM_GetByKey()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.GetAsync<BenchOrder>(NextId());
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<BenchOrder?> RepoDb_GetByKey()
    {
        var id = NextId();
        using var c = OpenConn();
        // Lambda + 显式表名重载——避免类上 RepoDB [Map] 注解
        var rows = await c.QueryAsync<BenchOrder>("bench_orders", e => e.id == id);
        return rows.FirstOrDefault();
    }

    // ═══════ 插入（每个 ORM 用最优路径）═══════
    // 公平：都执行同样的 INSERT SQL 并取回自增 ID

    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> ADO_NET_Insert()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (status, total, created_at) VALUES ('B', 99, 0); SELECT last_insert_rowid();";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> Dapper_Insert()
    {
        using var c = OpenConn();
        // Dapper 最优：ExecuteScalar 取回 ID（不用 Execute + query）
        // 显式 SqlMapper 调用——RepoDB 也定义了 ExecuteScalarAsync 扩展，二义性
        return await Dapper.SqlMapper.ExecuteScalarAsync<long>(c,
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at); SELECT last_insert_rowid();",
            new { status = "B", total = 99m, created_at = 0L });
    }

    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> PalORM_Insert()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var entity = await db.InsertAsync(new BenchOrder { status = "B", total = 99m, created_at = 0 });
        return entity.id;
    }

    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> RepoDb_Insert()
    {
        using var c = OpenConn();
        // 显式表名——BenchOrder 同时有 PalORM [Table]，避免与 RepoDB [Map] 注解冲突
        return Convert.ToInt64(
            await c.InsertAsync("bench_orders", new { status = "B", total = 99m, created_at = 0L }),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    // ═══════ 更新（每个 ORM 用最优单步路径）═══════
    // 公平：都执行 UPDATE ... SET status='U', total=999 WHERE id=5000（一次往返）

    [Benchmark, BenchmarkCategory("Update")]
    public async Task<int> ADO_NET_Update()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE bench_orders SET status = 'U', total = 999 WHERE id = 5000";
        return await cmd.ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Update")]
    public async Task<int> Dapper_Update()
    {
        using var c = OpenConn();
        return await c.ExecuteAsync(
            "UPDATE bench_orders SET status = @status, total = @total WHERE id = @id",
            new { status = "U", total = 999m, id = 5000L });
    }

    [Benchmark, BenchmarkCategory("Update")]
    public async Task<int> PalORM_Update()
    {
        // PalORM 最优：Set().Where().ExecuteNonQueryAsync() 单步更新（不做 Get+Update 两步）
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>()
            .Set(o => o.status, "U")
            .Set(o => o.total, 999m)
            .Where($"id = {5000L}")
            .ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Update")]
    public async Task<int> PalORM_Update_OptimisticLock()
    {
        // PalORM 独有：[ConcurrencyCheck] 自动 version 检查（最优路径同 Update）
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchVersioned>()
            .Set(o => o.name, "updated")
            .Where($"id = {1L}")
            .ExecuteNonQueryAsync();
    }

    // ═══════ 删除（统一：先插入一行再删除——保证幂等）═══════

    [Benchmark, BenchmarkCategory("Delete")]
    public async Task<int> ADO_NET_Delete()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (status, total, created_at) VALUES ('DEL', 1, 0); SELECT last_insert_rowid();";
        long id = (long)(await cmd.ExecuteScalarAsync())!;
        cmd.CommandText = $"DELETE FROM bench_orders WHERE id = {id}";
        return await cmd.ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Delete")]
    public async Task<int> Dapper_Delete()
    {
        using var c = OpenConn();
        // 显式 SqlMapper 调用——RepoDB 同名扩展冲突
        long id = await Dapper.SqlMapper.ExecuteScalarAsync<long>(c,
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at); SELECT last_insert_rowid();",
            new { status = "DEL", total = 1m, created_at = 0L });
        return await c.ExecuteAsync("DELETE FROM bench_orders WHERE id = @id", new { id });
    }

    [Benchmark, BenchmarkCategory("Delete")]
    public async Task<int> PalORM_Delete_Physical()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var inserted = await db.InsertAsync(new BenchOrder { status = "DEL", total = 1m, created_at = 0 });
        return await db.DeleteAsync<BenchOrder>(inserted.id);
    }

    [Benchmark, BenchmarkCategory("Delete")]
    public async Task<int> PalORM_Delete_SoftDelete()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var inserted = await db.InsertAsync(new BenchSoft { name = "del" });
        return await db.DeleteAsync<BenchSoft>(inserted.id);
    }

    // ═══════ UPSERT（统一 ON CONFLICT 语法）═══════

    [Benchmark, BenchmarkCategory("Upsert")]
    public async Task<int> ADO_NET_Upsert()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (id, status, total, created_at) VALUES (5000, 'UPS', 555, 0) ON CONFLICT(id) DO UPDATE SET status = 'UPS', total = 555";
        return await cmd.ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Upsert")]
    public async Task<int> Dapper_Upsert()
    {
        using var c = OpenConn();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (id, status, total, created_at) VALUES (@id, @status, @total, @created_at) " +
            "ON CONFLICT(id) DO UPDATE SET status = @status, total = @total",
            new { id = 5000L, status = "UPS", total = 555m, created_at = 0L });
    }

    [Benchmark, BenchmarkCategory("Upsert")]
    public async Task<BenchOrder> PalORM_Save_Upsert()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.SaveAsync(new BenchOrder { id = 5000, status = "UPS", total = 555m, created_at = 0 });
    }

    // ═══════ 批量（10000 行）═══════

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<int> Dapper_MultiRowInsert_10000()
    {
        using var c = OpenConn();
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"D{i}", total = i * 10m, created_at = 0 }).ToArray();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at)", items);
    }

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkInsert_10000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"B{i}", total = i * 10m, created_at = 0 }).ToList();
        return await db.BulkInsertAsync(items);
    }

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkUpdate_1000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = await db.From<BenchOrder>().Take(1000).ToListAsync();
        foreach (var item in items) { item.status = "BU"; }
        return await db.BulkUpdateAsync(items);
    }

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkDelete_500()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = Enumerable.Range(0, 500)
            .Select(i => new BenchOrder { status = $"BD{i}", total = 0m, created_at = 0 }).ToList();
        await db.BulkInsertAsync(items);
        var keys = items.Select(x => (object)x.id).ToList();
        return await db.BulkDeleteAsync<BenchOrder>(keys);
    }

    // ═══════ 事务 ═══════

    [Benchmark, BenchmarkCategory("Transaction")]
    public async Task PalORM_Transaction_Commit()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        await db.WithTransaction(async ct =>
        {
            await db.InsertAsync(new BenchOrder { status = "T1", total = 1m, created_at = 0 }, ct);
            await db.InsertAsync(new BenchOrder { status = "T2", total = 2m, created_at = 0 }, ct);
            await db.InsertAsync(new BenchOrder { status = "T3", total = 3m, created_at = 0 }, ct);
        });
    }

    [Benchmark, BenchmarkCategory("Transaction")]
    public async Task PalORM_Transaction_Rollback()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        try
        {
            await db.WithTransaction(async ct =>
            {
                await db.InsertAsync(new BenchOrder { status = "R1", total = 1m, created_at = 0 }, ct);
                await db.InsertAsync(new BenchOrder { status = "R2", total = 2m, created_at = 0 }, ct);
                throw new InvalidOperationException("bench-rollback");
            });
        }
        catch (InvalidOperationException) { /* 预期回滚 */ }
    }

    [Benchmark, BenchmarkCategory("Transaction")]
    public async Task PalORM_Transaction_Savepoint()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        using var tran = await db.BeginTransactionAsync();
        await db.InsertAsync(new BenchOrder { status = "SP1", total = 1m, created_at = 0 });
        await db.SavepointAsync(tran, "sp1");
        await db.InsertAsync(new BenchOrder { status = "SP2", total = 2m, created_at = 0 });
        await db.RollbackToAsync(tran, "sp1");
        await tran.CommitAsync();
    }

    // ═══════ PalORM 独有特性 ═══════

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchOrder>> PalORM_Query_WhereIn_500()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var ids = Enumerable.Range(1, 500).Select(i => (long)i).ToArray();
        return await db.From<BenchOrder>().WhereIn(o => o.id, ids).ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchSoft>> PalORM_Query_SoftDelete_Filter()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchSoft>().ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchOrder>> PalORM_Query_WithTracing()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>().WithTracing().ToListAsync();
    }

    // ═══════ v4.0 P1 新增：维度覆盖基准 ═══════

    // 多结果集 GridReader（两个结果集，验证多 ResultSet 物化性能）
    [Benchmark, BenchmarkCategory("Advanced")]
    public async Task<int> PalORM_GridReader_TwoResultSets()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        // QueryMultipleAsync 是 QueryBuilder<T> 的扩展——从 From<T>() 发起
        await using var grid = await db.From<BenchOrder>().QueryMultipleAsync(
            $"SELECT id, status, total, created_at FROM bench_orders WHERE id <= 100 ORDER BY id; SELECT id, status, total, created_at FROM bench_orders WHERE id > 9900 ORDER BY id");
        var first = await grid.ReadAsync<BenchOrder>();
        var second = await grid.ReadAsync<BenchOrder>();
        return first.Count + second.Count;
    }

    // WhereIn 跨批次（1500 个值，SQLite 999 参数上限 → 自动分 2 批）
    [Benchmark, BenchmarkCategory("Advanced")]
    public async Task<List<BenchOrder>> PalORM_WhereIn_CrossBatch_1500()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var ids = Enumerable.Range(1, 1500).Select(i => (long)i).ToArray();
        return await db.From<BenchOrder>().WhereIn(a => a.id, ids).ToListAsync();
    }

    // IAsyncEnumerable 流式查询（逐行消费，不缓存全列表）
    [Benchmark, BenchmarkCategory("Advanced")]
    public async Task<int> PalORM_StreamQuery_IAsyncEnumerable()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        int count = 0;
        await foreach (var _ in db.QueryAsyncEnumerable<BenchOrder>(
            $"SELECT id, status, total, created_at FROM bench_orders WHERE id <= 1000"))
            count++;
        return count;
    }

    // 并发查询（8 个并行 DataSession 各自 GetByKey——模拟多连接场景）
    [Benchmark, BenchmarkCategory("Advanced")]
    public async Task<int> PalORM_Concurrent_GetByKey_8x()
    {
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
            return await db.GetAsync<BenchOrder>(5000) is not null ? 1 : 0;
        });
        var results = await Task.WhenAll(tasks);
        return results.Sum();
    }

    // 小数据量冷启动（10 行——测量首次 JIT + 连接建立开销）
    [Benchmark, BenchmarkCategory("Scale")]
    public async Task<List<BenchOrder>> PalORM_QueryAll_Small_10()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>().Take(10).ToListAsync();
    }
}

// ═══════════════════════════════════════════════════════════════
// BulkInsert 拐点扫描——Params 矩阵（100/1K/10K/100K 行）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// 快速验证配置——远程 DB 基准用（网络延迟 > 统计精度，快速迭代优先）
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
public class BulkInsertScaleBenchmarks : IAsyncDisposable
{
    [Params(100, 1000, 10000, 100000)]
    public int RowCount { get; set; }

    private const string Cs = "Data Source=bench;Mode=Memory;Cache=Shared";
    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = Cs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = new SqliteConnection(Cs);
        await _keeper.OpenAsync();
        // 建表（bench_orders 由 BenchOrder 实体映射）
        using var cmd = _keeper.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS bench_orders; CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)";
        await cmd.ExecuteNonQueryAsync();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // 每次迭代前清表——bench_orders 表由 BenchOrder 实体映射
        using var cmd = _keeper!.CreateCommand();
        cmd.CommandText = "DELETE FROM bench_orders";
        cmd.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task<long> PalORM_BulkInsert_Scaled()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var batch = Enumerable.Range(0, RowCount)
            .Select(i => new BenchOrder { status = $"B{i}", total = i, created_at = i }).ToArray();
        return await db.BulkInsertAsync(batch, batchSize: 500);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("BulkInsert")]
    public async Task<int> Dapper_MultiRowInsert_Scaled()
    {
        var batch = Enumerable.Range(0, RowCount)
            .Select(i => new { status = $"B{i}", total = (double)i, created_at = (long)i }).ToArray();
        using var c = new SqliteConnection(Cs);
        await c.OpenAsync();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at)",
            batch);
    }
}

// ═══════════════════════════════════════════════════════════════
// SQL 构建（零 I/O）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// P0 修复：SqlBuild 是 nanosecond 级，原 3/5/10 在慢机上 Error/Mean 达 14%。
// 提升至 5/10/15 严格配置 + Throughput 模式（多次调用取均值降低单次抖动）。
[SimpleJob(launchCount: 5, warmupCount: 10, iterationCount: 15, invocationCount: 4096)]
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

// ═══════════════════════════════════════════════════════════════
// 远程 PostgreSQL / MySQL 基准（v4.0 新增）
// 连接串从环境变量读取，避免硬编码：
//   PALORM_BENCH_PG="Host=...;Port=5432;Username=...;Password=...;Database=palorm_bench"
//   PALORM_BENCH_MYSQL="Server=...;Port=3306;User ID=...;Password=...;Database=palorm_bench"
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// 快速验证配置——远程 DB 基准用（网络延迟 > 统计精度，快速迭代优先）
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class PgBenchmarks : IAsyncDisposable
{
    private const int SeedRows = 10000;
    private static readonly string Cs = Environment.GetEnvironmentVariable("PALORM_BENCH_PG")
        ?? throw new InvalidOperationException("Set PALORM_BENCH_PG env var for PostgreSQL benchmarks.");
    private NpgsqlConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = Cs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = new NpgsqlConnection(Cs);
        await _keeper.OpenAsync();
        await Exec("DROP TABLE IF EXISTS bench_orders");
        await Exec("CREATE TABLE bench_orders (id BIGSERIAL PRIMARY KEY, status TEXT NOT NULL, total NUMERIC(18,6) NOT NULL, created_at BIGINT NOT NULL)");
        for (int i = 0; i < SeedRows; i++)
            await Exec($"INSERT INTO bench_orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})");
    }

    private async Task Exec(string sql)
    {
        await using var cmd = _keeper!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    private static NpgsqlConnection OpenConn()
    {
        var c = new NpgsqlConnection(Cs);
        c.Open();
        return c;
    }

    // ─── 查询 ───
    [Benchmark(Baseline = true), BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> ADO_NET_QueryAll()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders";
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<BenchOrder>(SeedRows);
        while (await r.ReadAsync())
            list.Add(new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) });
        return list;
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> Dapper_QueryAll()
    {
        using var c = OpenConn();
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
        using var c = OpenConn();
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
        using var c = OpenConn();
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
}

[MemoryDiagnoser]
// 快速验证配置——远程 DB 基准用（网络延迟 > 统计精度，快速迭代优先）
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class MySqlBenchmarks : IAsyncDisposable
{
    private const int SeedRows = 10000;
    private static readonly string Cs = Environment.GetEnvironmentVariable("PALORM_BENCH_MYSQL")
        ?? throw new InvalidOperationException("Set PALORM_BENCH_MYSQL env var for MySQL benchmarks.");
    private MySqlConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = Cs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = new MySqlConnection(Cs);
        await _keeper.OpenAsync();
        await Exec("DROP TABLE IF EXISTS bench_orders");
        await Exec("CREATE TABLE bench_orders (id BIGINT AUTO_INCREMENT PRIMARY KEY, status TEXT NOT NULL, total DECIMAL(18,6) NOT NULL, created_at BIGINT NOT NULL)");
        for (int i = 0; i < SeedRows; i++)
            await Exec($"INSERT INTO bench_orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})");
    }

    private async Task Exec(string sql)
    {
        await using var cmd = _keeper!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    private static MySqlConnection OpenConn()
    {
        var c = new MySqlConnection(Cs);
        c.Open();
        return c;
    }

    // ─── 查询 ───
    [Benchmark(Baseline = true), BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> ADO_NET_QueryAll()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders";
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<BenchOrder>(SeedRows);
        while (await r.ReadAsync())
            list.Add(new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) });
        return list;
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> Dapper_QueryAll()
    {
        using var c = OpenConn();
        return (await c.QueryAsync<BenchOrder>("SELECT id, status, total, created_at FROM bench_orders")).AsList();
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> PalORM_QueryAll()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(_options);
        return await db.From<BenchOrder>().ToListAsync();
    }

    // ─── 主键查询 ───
    [Benchmark(Baseline = true), BenchmarkCategory("GetByKey")]
    public async Task<BenchOrder?> ADO_NET_GetByKey()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders WHERE id = 5000";
        using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) } : null;
    }

    [Benchmark, BenchmarkCategory("GetByKey")]
    public async Task<BenchOrder?> PalORM_GetByKey()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(_options);
        return await db.GetAsync<BenchOrder>(5000L);
    }

    // ─── 插入 ───
    [Benchmark(Baseline = true), BenchmarkCategory("Insert")]
    public async Task<long> ADO_NET_Insert()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (status, total, created_at) VALUES ('X', 1.0, 1); SELECT LAST_INSERT_ID();";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> PalORM_Insert()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(_options);
        var e = await db.InsertAsync(new BenchOrder { status = "X", total = 1m, created_at = 1 });
        return e.id;
    }

    // ─── 批量插入（MySQL 走多值 INSERT）───
    [Benchmark, BenchmarkCategory("BulkInsert")]
    public async Task PalORM_BulkInsert_10000()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(_options);
        var batch = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"B{i}", total = i, created_at = i }).ToArray();
        await db.BulkInsertAsync(batch, batchSize: 1000);
    }
}

// ═══════════════════════════════════════════════════════════════
// 纯速度基准（无 MemoryDiagnoser）
// MemoryDiagnoser 每次 GC.Collect + WaitForPendingFinalizers 阻碍 JIT 内联。
// 本类用于交叉验证：如果纯速度显著快（>5%）说明 MemoryDiagnoser 干扰了测量。
// ═══════════════════════════════════════════════════════════════

[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class SqliteSpeedBenchmarks : IAsyncDisposable
{
    // 注意：无 [MemoryDiagnoser]——纯速度测量，消除 GC.Collect 干扰
    private const int SeedRows = 10000;
    private const string Cs = "Data Source=bench_speed;Mode=Memory;Cache=Shared";
    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = Cs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = new SqliteConnection(Cs);
        await _keeper.OpenAsync();
        await Exec("CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)");
        for (int i = 0; i < SeedRows; i++)
            await Exec($"INSERT INTO bench_orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})");
    }

    private async Task Exec(string sql)
    {
        using var cmd = _keeper!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
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

    [Benchmark(Baseline = true), BenchmarkCategory("Speed")]
    public async Task<List<BenchOrder>> ADO_NET_QueryAll_Speed()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders";
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<BenchOrder>(SeedRows);
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

// ═══════════════════════════════════════════════════════════════
// Dapper Cache Impact 专项（对齐 Dapper 官方 DapperCacheImpact.cs）
//
// PalORM 卖点：源生成 RowFactory，零运行时反射、零 IL 缓存查找。
// Dapper：首次（或参数形状变化时）需构建 + 缓存 IL 物化代码。
// 本基准对照三种参数形状下两者的查询延迟，证明源生成路径的稳定性。
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class DapperCacheImpactBenchmarks : IAsyncDisposable
{
    private const int SeedRows = 10000;
    private const string Cs = "Data Source=bench_cache;Mode=Memory;Cache=Shared";
    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = Cs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = new SqliteConnection(Cs);
        await _keeper.OpenAsync();
        using (var cmd = _keeper.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)";
            await cmd.ExecuteNonQueryAsync();
        }
        for (int i = 0; i < SeedRows; i++)
        {
            using var cmd = _keeper.CreateCommand();
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

    // ─── 场景 1：稳定参数形状（Dapper 缓存命中后的稳态） ───
    // Dapper：第 N 次（N > 1）后 IL 缓存命中；PalORM：源生成，恒定。

    [Benchmark, BenchmarkCategory("StableShape")]
    public async Task<BenchOrder?> Dapper_StableShape()
    {
        using var c = OpenConn();
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
        using var c = OpenConn();
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
