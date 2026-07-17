using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

internal static class Program
{
    public static void Main(string[] args) => BenchmarkRunner.Run<PalORMBenchmarks>();
}

[Table("orders")]
internal sealed partial class Order
{
    [Key] public long Id { get; set; }
    [Column("status")] public string Status { get; set; } = "";
    [Column("total")] public decimal Total { get; set; }
    [Column("created_at")] public long CreatedAt { get; set; }
}

[MemoryDiagnoser]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates benchmark instances through reflection.")]
internal sealed class PalORMBenchmarks
{
    private const string CS = "Data Source=:memory:";

    private async Task<DataSession<SqliteProvider>> CreateDB()
    {
        var db = await DataSession<SqliteProvider>.CreateAsync(new DbOptions { ConnectionString = CS });
        await db.MigrateAsync();
        return db;
    }

    [Benchmark(Baseline = true)]
    public async Task RawADO_Query_1000()
    {
        using var conn = new SqliteConnection(CS); await conn.OpenAsync();
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT * FROM orders LIMIT 1000";
        using var r = await cmd.ExecuteReaderAsync(); var list = new List<Order>();
        while (await r.ReadAsync()) list.Add(new Order { Id = r.GetInt64(0), Status = r.GetString(1), Total = r.GetDecimal(2), CreatedAt = r.GetInt64(3) });
    }

    [Benchmark]
    public async Task PalORM_Query_1000()
    {
        await using var db = await CreateDB();
        _ = await db.From<Order>().ToListAsync();
    }

    [Benchmark]
    public async Task PalORM_Insert_Single()
    {
        await using var db = await CreateDB();
        _ = await db.InsertAsync(new Order { Status = "B", Total = 99m, CreatedAt = 0 });
    }

    [Benchmark]
    public async Task PalORM_BulkInsert_100()
    {
        await using var db = await CreateDB();
        var items = Enumerable.Range(0, 100).Select(i => new Order { Status = $"B{i}", Total = i * 10m, CreatedAt = 0 }).ToList();
        await db.BulkInsertAsync(items);
    }
}
