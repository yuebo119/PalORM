using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

[assembly: SuppressMessage("Design", "CA1515",
    Justification = "BenchmarkDotNet requires public types; this is an executable, not a library.")]

namespace PalORM.Benchmarks;

public static class Program
{
    public static void Main(string[] args) => BenchmarkRunner.Run<PalORMBenchmarks>();
}

[Table("orders")]
public sealed partial class Order
{
    [Key] public long Id { get; set; }
    [Column("status")] public string Status { get; set; } = "";
    [Column("total")] public decimal Total { get; set; }
    [Column("created_at")] public long CreatedAt { get; set; }
}

[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
[SuppressMessage("Performance", "CA1812",
    Justification = "BenchmarkDotNet creates benchmark instances through reflection.")]
[SuppressMessage("Security", "CA2100",
    Justification = "Seed data uses compile-time constants; no user input involved.")]
[SuppressMessage("Performance", "CA1852",
    Justification = "BenchmarkDotNet generates derived classes from this type.")]
public class PalORMBenchmarks : IAsyncDisposable
{
    private SqliteConnection? _sharedConnection;
    private const int SeedRows = 1000;
    private const string ConnectionString = "Data Source=benchmarks;Mode=Memory;Cache=Shared";

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        // 创建共享内存数据库并 seed 精确 1000 行
        _sharedConnection = new SqliteConnection(ConnectionString);
        await _sharedConnection.OpenAsync();
        using var seedCmd = _sharedConnection.CreateCommand();
        seedCmd.CommandText =
            "CREATE TABLE orders (id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)";
        await seedCmd.ExecuteNonQueryAsync();
        for (int i = 0; i < SeedRows; i++)
        {
            seedCmd.CommandText =
                $"INSERT INTO orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})";
            await seedCmd.ExecuteNonQueryAsync();
        }
    }

    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        if (_sharedConnection is not null)
            await _sharedConnection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)]
    public async Task<List<Order>> RawADO_Query_1000()
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, status, total, created_at FROM orders LIMIT 1000";
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Order>(SeedRows);
        while (await r.ReadAsync())
            list.Add(new Order
            {
                Id = r.GetInt64(0),
                Status = r.GetString(1),
                Total = r.GetDecimal(2),
                CreatedAt = r.GetInt64(3)
            });
        return list;
    }

    [Benchmark]
    public async Task<List<Order>> PalORM_Query_1000()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = ConnectionString });
        return await db.From<Order>().ToListAsync();
    }

    [Benchmark]
    public async Task<Order> PalORM_Insert_Single()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = ConnectionString });
        return await db.InsertAsync(
            new Order { Status = "B", Total = 99m, CreatedAt = 0 });
    }

    [Benchmark]
    public async Task<long> PalORM_BulkInsert_100()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = ConnectionString });
        var items = Enumerable.Range(0, 100)
            .Select(i => new Order
            {
                Status = $"B{i}",
                Total = i * 10m,
                CreatedAt = 0
            })
            .ToList();
        return await db.BulkInsertAsync(items);
    }
}
