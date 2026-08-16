// v5.0 阶段 3.4 装箱基准——手写微基准（BenchmarkDotNet 0.15.8 不兼容 .NET 11 preview SDK）
// r19/ITM-691：分配计数改 GC.GetTotalAllocatedBytes（测量前强制 GC）——原
// GetAllocatedBytesForCurrentThread 跨 await 时异步续体可能换线程，线程级差值失真。

using PalORM;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

/// <summary>v5.0 阶段 3.4 装箱微基准——不依赖 BenchmarkDotNet。
/// 用 GC.GetTotalAllocatedBytes 测每操作分配字节数，回答装箱占比问题。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "S1215",
    Justification = "微基准在测量前强制 GC 是测量方法本身——与 BDN MemoryDiagnoser 的强制回收同原理（r19/ITM-691）。")]
internal static class BoxingMicroBenchmark
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== v5.0 阶段 3.4 装箱微基准（手写，不依赖 BDN）===");
        Console.WriteLine($"运行时: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        var opts = new DbOptions { ConnectionString = "Data Source=:memory:" };

        int[] rowCounts = [1, 100, 1000, 10000];
        foreach (int rowCount in rowCounts)
        {
            await MeasureInsertAsync(opts, rowCount);
            await MeasureBulkInsertAsync(opts, rowCount);
            await MeasureQueryAsync(opts, rowCount);
            Console.WriteLine();
        }

        Console.WriteLine("=== 决策指标 ===");
        Console.WriteLine("装箱占总分配比 > 20% → 3.4 值得实施");
        Console.WriteLine("装箱占总分配比 < 5% → 3.4 不值得做");
    }

    private static async Task MeasureInsertAsync(DbOptions opts, int rowCount)
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(opts);
        await db.ExecuteAsync($"CREATE TABLE IF NOT EXISTS boxing_test (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, value INTEGER, price REAL, active INTEGER)");

        var entities = Enumerable.Range(0, rowCount)
            .Select(i => new BoxingTestEntity { Name = $"row-{i}", Value = i, Price = i * 1.5m, Active = (i % 2) == 0 })
            .ToList();

        // 预热（JIT + 连接池）
        await db.InsertAsync(entities[0]);
        await db.ExecuteAsync($"DELETE FROM boxing_test");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var e in entities)
            await db.InsertAsync(e);
        sw.Stop();
        long after = GC.GetTotalAllocatedBytes(false);

        double allocatedKb = (after - before) / 1024.0;
        double perRowBytes = (after - before) / (double)rowCount;
        Console.WriteLine($"InsertAsync × {rowCount,6}: {allocatedKb,8:F2} KB | {sw.ElapsedMilliseconds,5} ms | {perRowBytes,6:F0} B/行");

        await db.ExecuteAsync($"DROP TABLE boxing_test");
    }

    private static async Task MeasureBulkInsertAsync(DbOptions opts, int rowCount)
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(opts);
        await db.ExecuteAsync($"CREATE TABLE IF NOT EXISTS boxing_test (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, value INTEGER, price REAL, active INTEGER)");

        var entities = Enumerable.Range(0, rowCount)
            .Select(i => new BoxingTestEntity { Name = $"row-{i}", Value = i, Price = i * 1.5m, Active = (i % 2) == 0 })
            .ToList();

        if (rowCount > 0)
            await db.BulkInsertAsync([entities[0]]);
        await db.ExecuteAsync($"DELETE FROM boxing_test");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await db.BulkInsertAsync(entities);
        sw.Stop();
        long after = GC.GetTotalAllocatedBytes(false);

        double allocatedKb = (after - before) / 1024.0;
        double perRowBytes = (after - before) / (double)rowCount;
        Console.WriteLine($"BulkInsertAsync × {rowCount,6}: {allocatedKb,8:F2} KB | {sw.ElapsedMilliseconds,5} ms | {perRowBytes,6:F0} B/行");

        await db.ExecuteAsync($"DROP TABLE boxing_test");
    }

    private static async Task MeasureQueryAsync(DbOptions opts, int rowCount)
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(opts);
        await db.ExecuteAsync($"CREATE TABLE IF NOT EXISTS boxing_test (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, value INTEGER, price REAL, active INTEGER)");
        var entities = Enumerable.Range(0, rowCount)
            .Select(i => new BoxingTestEntity { Name = $"row-{i}", Value = i, Price = i * 1.5m, Active = (i % 2) == 0 })
            .ToList();
        await db.BulkInsertAsync(entities);

        _ = await db.From<BoxingTestEntity>().ToListAsync();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = await db.From<BoxingTestEntity>().ToListAsync();
        sw.Stop();
        long after = GC.GetTotalAllocatedBytes(false);

        double allocatedKb = (after - before) / 1024.0;
        double perRowBytes = rowCount > 0 ? (after - before) / (double)rowCount : 0;
        Console.WriteLine($"QueryAsync × {rowCount,6}: {allocatedKb,8:F2} KB | {sw.ElapsedMilliseconds,5} ms | {perRowBytes,6:F0} B/行 (读取对照组)");

        await db.ExecuteAsync($"DROP TABLE boxing_test");
    }
}
// BoxingTestEntity 已移至 BenchmarkEntities.cs（v5.0 基准体系重构）
