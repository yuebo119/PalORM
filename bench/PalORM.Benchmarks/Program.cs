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

// ─── 实体（属性名 = 列名，不加 PalORM [Column]——让 Dapper 和 PalORM 都按属性名映射）───

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
// 全面基准——增删改查 + 事务 + 批量 + 独有特性（30 个）
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

    // ═══════ 查询（8 个）═══════

    [Benchmark(Baseline = true), BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> RawAdo_QueryAll()
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
        await using var db = await CreateDb();
        return await db.From<BenchOrder>().ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<BenchOrder?> RawAdo_GetByKey()
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
        await using var db = await CreateDb();
        return await db.GetAsync<BenchOrder>(5000L);
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<long> RawAdo_Count()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM bench_orders";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Benchmark, BenchmarkCategory("Query")]
    public async Task<long> PalORM_Count()
    {
        await using var db = await CreateDb();
        return await db.CountAsync<BenchOrder>();
    }

    // ═══════ 写入（12 个）═══════

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<long> RawAdo_Insert()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (status, total, created_at) VALUES ('B', 99, 0); SELECT last_insert_rowid();";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> Dapper_Insert()
    {
        using var c = OpenConn();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at)",
            new BenchOrder { status = "B", total = 99m, created_at = 0 });
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<BenchOrder> PalORM_Insert()
    {
        await using var db = await CreateDb();
        return await db.InsertAsync(new BenchOrder { status = "B", total = 99m, created_at = 0 });
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> RawAdo_Update()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE bench_orders SET status = 'U', total = 999 WHERE id = 5000";
        return await cmd.ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> Dapper_Update()
    {
        using var c = OpenConn();
        return await c.ExecuteAsync(
            "UPDATE bench_orders SET status = @status, total = @total WHERE id = @id",
            new BenchOrder { id = 5000, status = "U", total = 999m, created_at = 0 });
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> PalORM_Update()
    {
        await using var db = await CreateDb();
        var entity = (await db.GetAsync<BenchOrder>(5000L))!;
        entity.status = "U";
        entity.total = 999m;
        return await db.UpdateAsync(entity);
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> PalORM_Update_OptimisticLock()
    {
        await using var db = await CreateDb();
        var entity = (await db.GetAsync<BenchVersioned>(1L))!;
        entity.name = "updated";
        return await db.UpdateAsync(entity);
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> RawAdo_Delete()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (status, total, created_at) VALUES ('DEL', 1, 0); SELECT last_insert_rowid();";
        long id = (long)(await cmd.ExecuteScalarAsync())!;
        cmd.CommandText = $"DELETE FROM bench_orders WHERE id = {id}";
        return await cmd.ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> PalORM_Delete_Physical()
    {
        await using var db = await CreateDb();
        var inserted = await db.InsertAsync(new BenchOrder { status = "DEL", total = 1m, created_at = 0 });
        return await db.DeleteAsync<BenchOrder>(inserted.id);
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> PalORM_Delete_SoftDelete()
    {
        await using var db = await CreateDb();
        var inserted = await db.InsertAsync(new BenchSoft { name = "del" });
        return await db.DeleteAsync<BenchSoft>(inserted.id);
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> RawAdo_Upsert()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (id, status, total, created_at) VALUES (5000, 'UPS', 555, 0) ON CONFLICT(id) DO UPDATE SET status = 'UPS', total = 555";
        return await cmd.ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<int> Dapper_Upsert()
    {
        using var c = OpenConn();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (id, status, total, created_at) VALUES (@id, @status, @total, @created_at) " +
            "ON CONFLICT(id) DO UPDATE SET status = @status, total = @total",
            new BenchOrder { id = 5000, status = "UPS", total = 555m, created_at = 0 });
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task<BenchOrder> PalORM_Save_Upsert()
    {
        await using var db = await CreateDb();
        return await db.SaveAsync(new BenchOrder { id = 5000, status = "UPS", total = 555m, created_at = 0 });
    }

    // ═══════ 批量（4 个）═══════

    [Benchmark, BenchmarkCategory("Bulk")]
    public async Task<int> Dapper_MultiRowInsert_10000()
    {
        using var c = OpenConn();
        // Dapper 的 Execute 逐条 INSERT——对照 PalORM 的多值 INSERT 批量
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"D{i}", total = i * 10m, created_at = 0 }).ToArray();
        return await c.ExecuteAsync(
            "INSERT INTO bench_orders (status, total, created_at) VALUES (@status, @total, @created_at)", items);
    }

    [Benchmark, BenchmarkCategory("Bulk")]
    public async Task<long> PalORM_BulkInsert_10000()
    {
        await using var db = await CreateDb();
        var items = Enumerable.Range(0, 10000)
            .Select(i => new BenchOrder { status = $"B{i}", total = i * 10m, created_at = 0 }).ToList();
        return await db.BulkInsertAsync(items);
    }

    [Benchmark, BenchmarkCategory("Bulk")]
    public async Task<long> PalORM_BulkUpdate_1000()
    {
        await using var db = await CreateDb();
        var items = await db.From<BenchOrder>().Take(1000).ToListAsync();
        foreach (var item in items) { item.status = "BU"; }
        return await db.BulkUpdateAsync(items);
    }

    [Benchmark, BenchmarkCategory("Bulk")]
    public async Task<long> PalORM_BulkDelete_500()
    {
        await using var db = await CreateDb();
        var items = Enumerable.Range(0, 500)
            .Select(i => new BenchOrder { status = $"BD{i}", total = 0m, created_at = 0 }).ToList();
        await db.BulkInsertAsync(items);
        var keys = items.Select(x => (object)x.id).ToList();
        return await db.BulkDeleteAsync<BenchOrder>(keys);
    }

    // ═══════ 事务（3 个）═══════

    [Benchmark, BenchmarkCategory("Transaction")]
    public async Task PalORM_Transaction_Commit()
    {
        await using var db = await CreateDb();
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
        await using var db = await CreateDb();
        try
        {
            await db.WithTransaction(async ct =>
            {
                await db.InsertAsync(new BenchOrder { status = "R1", total = 1m, created_at = 0 }, ct);
                await db.InsertAsync(new BenchOrder { status = "R2", total = 2m, created_at = 0 }, ct);
                throw new InvalidOperationException("bench-rollback");
            });
        }
        catch (InvalidOperationException) { }
    }

    [Benchmark, BenchmarkCategory("Transaction")]
    public async Task PalORM_Transaction_Savepoint()
    {
        await using var db = await CreateDb();
        using var tran = await db.BeginTransactionAsync();
        await db.InsertAsync(new BenchOrder { status = "SP1", total = 1m, created_at = 0 });
        await db.SavepointAsync(tran, "sp1");
        await db.InsertAsync(new BenchOrder { status = "SP2", total = 2m, created_at = 0 });
        await db.RollbackToAsync(tran, "sp1");
        await tran.CommitAsync();
    }

    // ═══════ PalORM 独有特性（4 个）═══════

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchOrder>> PalORM_Query_CacheHit()
    {
        await using var db = await CreateDb();
        await db.From<BenchOrder>().WithCache("hit-bench", TimeSpan.FromMinutes(5)).ToListAsync();
        return await db.From<BenchOrder>().WithCache("hit-bench", TimeSpan.FromMinutes(5)).ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchOrder>> PalORM_Query_WhereIn_500()
    {
        await using var db = await CreateDb();
        var ids = Enumerable.Range(1, 500).Select(i => (long)i).ToArray();
        return await db.From<BenchOrder>().WhereIn(o => o.id, ids).ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchSoft>> PalORM_Query_SoftDelete_Filter()
    {
        await using var db = await CreateDb();
        // [SoftDelete] 自动附加 WHERE deleted_at IS NULL——Dapper 需手写
        return await db.From<BenchSoft>().ToListAsync();
    }

    [Benchmark, BenchmarkCategory("Feature")]
    public async Task<List<BenchOrder>> PalORM_Query_WithTracing()
    {
        await using var db = await CreateDb();
        return await db.From<BenchOrder>().WithTracing().ToListAsync();
    }
}

// ═══════════════════════════════════════════════════════════════
// SQL 构建（零 I/O——证明 struct + ValueStringBuilder 分配优势）
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
