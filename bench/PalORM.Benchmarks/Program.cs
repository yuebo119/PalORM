using System.Diagnostics.CodeAnalysis;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Dapper;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

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
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class SqliteBenchmarks : IAsyncDisposable
{
    private const int SeedRows = 10000;
    private const string Cs = "Data Source=bench;Mode=Memory;Cache=Shared";
    private SqliteConnection? _keeper;
    private DbOptions _options = new() { ConnectionString = Cs };

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

    private static Task<DataSession<SqliteProvider>> CreateDb()
        => DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = Cs });

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

    // ═══════ 主键查询 ═══════
    // SQL 统一：SELECT ... WHERE id = 5000

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<BenchOrder?> ADO_NET_GetByKey()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders WHERE id = 5000";
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) };
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<BenchOrder?> Dapper_GetByKey()
    {
        using var c = OpenConn();
        return await c.QueryFirstOrDefaultAsync<BenchOrder>(
            "SELECT id, status, total, created_at FROM bench_orders WHERE id = @id", new { id = 5000L });
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<BenchOrder?> PalORM_GetByKey()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.GetAsync<BenchOrder>(5000L);
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
        return await c.ExecuteScalarAsync<long>(
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
        long id = await c.ExecuteScalarAsync<long>(
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

    [Benchmark, BenchmarkCategory("Bulk")]
    public async Task<int> Dapper_MultiRowInsert_10000()
    {
        using var c = OpenConn();
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"D{i}", total = i * 10m, created_at = 0 }).ToArray();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at)", items);
    }

    [Benchmark, BenchmarkCategory("Bulk")]
    public async Task<long> PalORM_BulkInsert_10000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"B{i}", total = i * 10m, created_at = 0 }).ToList();
        return await db.BulkInsertAsync(items);
    }

    [Benchmark, BenchmarkCategory("Bulk")]
    public async Task<long> PalORM_BulkUpdate_1000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        var items = await db.From<BenchOrder>().Take(1000).ToListAsync();
        foreach (var item in items) { item.status = "BU"; }
        return await db.BulkUpdateAsync(items);
    }

    [Benchmark, BenchmarkCategory("Bulk")]
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
}

// ═══════════════════════════════════════════════════════════════
// SQL 构建（零 I/O）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
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
