using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Dapper;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;
// RepoDb（含 Sqlite 扩展，全部位于 RepoDb 命名空间下：SqliteGlobalConfiguration.UseSqlite）
using RepoDb;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 公平对照基准——同一 SQL / 同一数据 / 每个 ORM 最优路径
// 统一基线：ADO.NET（原名 RawAdo → 改为 ADO_NET）
// v5.0 基准体系重构：拆自原 SqliteBenchmarks（Query/Insert/Update/Delete/Upsert 5 类）
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
// 标准基准配置——正式报告用，统计可信度中
[SimpleJob(launchCount: BenchmarkConfig.StandardLaunch, warmupCount: BenchmarkConfig.StandardWarmup, iterationCount: BenchmarkConfig.StandardIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class CrudBenchmarks : IAsyncDisposable
{
    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = BenchmarkConfig.SqliteCs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        await BenchmarkConfig.SeedSqliteAsync(_keeper!);
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    // Dapper 官方 benchmarks/Dapper.Tests.Performance 的 Step() 等价实现：
    // 每次迭代查不同 id，避免同一数据页被 SQLite page cache 命中导致测量失真。
    // 仅用于单点查询（GetByKey）；Update/Upsert 保留固定 id 以保证语义正确。
    // 返回 long：对齐 BenchOrder.id（long）——源生成器 BindDelete 要求 key 类型精确匹配
    private long _counter;

    // ═══════ 查询全表 10000 行 ═══════
    // SQL 统一：SELECT id, status, total, created_at FROM bench_orders
    // 所有 ORM 物化为 List<BenchOrder>

    [Benchmark(Baseline = true), BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> ADO_NET_QueryAll()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
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
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
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
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        // 显式表名——BenchOrder 同时有 PalORM [Table]，避免与 RepoDB [Map] 注解冲突
        return (await c.QueryAllAsync<BenchOrder>("bench_orders")).AsList();
    }

    // ═══════ 主键查询 ═══════
    // 使用 NextId() 轮询 id（1..10000）——避免 SQLite page cache 命中导致测量失真
    // 对齐 Dapper 官方 benchmarks/Dapper.Tests.Performance 的 Step() 机制
    // T-P3-06：category 全局统一 GetByKey（Pg/MySql 同标签）——不再并入 Query 组

    [Benchmark(Baseline = true), BenchmarkCategory("GetByKey")]
    public async Task<BenchOrder?> ADO_NET_GetByKey()
    {
        var id = BenchmarkConfig.NextId(ref _counter);
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM bench_orders WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new BenchOrder { id = r.GetInt64(0), status = r.GetString(1), total = r.GetDecimal(2), created_at = r.GetInt64(3) };
    }

    [Benchmark, BenchmarkCategory("GetByKey")]
    public async Task<BenchOrder?> Dapper_GetByKey()
    {
        var id = BenchmarkConfig.NextId(ref _counter);
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        return await c.QueryFirstOrDefaultAsync<BenchOrder>(
            "SELECT id, status, total, created_at FROM bench_orders WHERE id = @id", new { id });
    }

    [Benchmark, BenchmarkCategory("GetByKey")]
    public async Task<BenchOrder?> PalORM_GetByKey()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return await db.GetAsync<BenchOrder>(BenchmarkConfig.NextId(ref _counter));
    }

    [Benchmark, BenchmarkCategory("GetByKey")]
    public async Task<BenchOrder?> RepoDb_GetByKey()
    {
        var id = BenchmarkConfig.NextId(ref _counter);
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        // Lambda + 显式表名重载——避免类上 RepoDB [Map] 注解
        var rows = await c.QueryAsync<BenchOrder>("bench_orders", e => e.id == id);
        return rows.FirstOrDefault();
    }

    // ═══════ 插入（每个 ORM 用最优路径）═══════
    // 公平：都执行同样的 INSERT SQL 并取回自增 ID

    [Benchmark(Baseline = true), BenchmarkCategory("Insert")]
    public async Task<long> ADO_NET_Insert()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (status, total, created_at) VALUES ('B', 99, 0); SELECT last_insert_rowid();";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> Dapper_Insert()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
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
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        // 显式表名——BenchOrder 同时有 PalORM [Table]，避免与 RepoDB [Map] 注解冲突
        return Convert.ToInt64(
            await c.InsertAsync("bench_orders", new { status = "B", total = 99m, created_at = 0L }),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    // ═══════ 更新（每个 ORM 用最优单步路径）═══════
    // 公平：都执行 UPDATE ... SET status='U', total=999 WHERE id=5000（一次往返）

    [Benchmark(Baseline = true), BenchmarkCategory("Update")]
    public async Task<int> ADO_NET_Update()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE bench_orders SET status = 'U', total = 999 WHERE id = 5000";
        return await cmd.ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Update")]
    public async Task<int> Dapper_Update()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
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

    [Benchmark(Baseline = true), BenchmarkCategory("Delete")]
    public async Task<int> ADO_NET_Delete()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (status, total, created_at) VALUES ('DEL', 1, 0); SELECT last_insert_rowid();";
        long id = (long)(await cmd.ExecuteScalarAsync())!;
        cmd.CommandText = $"DELETE FROM bench_orders WHERE id = {id}";
        return await cmd.ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Delete")]
    public async Task<int> Dapper_Delete()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
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

    [Benchmark(Baseline = true), BenchmarkCategory("Upsert")]
    public async Task<int> ADO_NET_Upsert()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO bench_orders (id, status, total, created_at) VALUES (5000, 'UPS', 555, 0) ON CONFLICT(id) DO UPDATE SET status = 'UPS', total = 555";
        return await cmd.ExecuteNonQueryAsync();
    }

    [Benchmark, BenchmarkCategory("Upsert")]
    public async Task<int> Dapper_Upsert()
    {
        using var c = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
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
}
