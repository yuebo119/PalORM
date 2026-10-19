using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using PalORM;
using PalORM.Bench.Shared;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

/// <summary>连接与池 churn 探针（维度 6）——会话池租还、新建会话、裸连接建连/释放。
/// <para>结果库 <c>pool</c> 节供统一报告维度 6 引用；不卡阈值（与长稳同为观测面）。</para>
/// <para>SQLite 默认走共享内存库；PG/MySQL 需设置 <c>PALORM_PG_CONNECTION</c> /
/// <c>PALORM_MYSQL_CONNECTION</c>（与 PerfHub 同口径），远程建连成本约 8.5 ms/条。</para></summary>
internal static class PoolProbe
{
    public static Task RunAsync(string dialect) => dialect switch
    {
        "pg" or "postgresql" => RunAsync<PostgreSqlProvider>("PostgreSQL", Require("PALORM_PG_CONNECTION")),
        "mysql" => RunAsync<MySqlProvider>("MySQL", Require("PALORM_MYSQL_CONNECTION")),
        _ => RunAsync<SqliteProvider>("SQLite", BenchmarkConfig.SqliteCs)
    };

    private static string Require(string name)
        => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"{name} 未设置（--pool pg/mysql 需要）");

    private static async Task RunAsync<TProvider>(string dialect, string connectionString)
        where TProvider : IDbProvider
    {
        var options = new DbOptions
        {
            ConnectionString = connectionString,
            MaxRetries = 0,
            CircuitBreakerThreshold = 0
        };
        await using var pool = DataSessionPool<TProvider>.Create(options, maxIdleSize: 4);

        // 预热：让池填满空闲会话，后续租还走命中路径。
        for (int i = 0; i < 200; i++)
        {
            await using DataSession<TProvider> warm = await pool.RentAsync().ConfigureAwait(false);
            _ = await warm.ScalarAsync<long>(FormattableStringFactory.Create("SELECT 1")).ConfigureAwait(false);
        }

        (double poolRentUs, long poolRentAlloc) = await MeasureAsync(2000, async () =>
        {
            await using DataSession<TProvider> session = await pool.RentAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        (double poolRentQueryUs, long poolRentQueryAlloc) = await MeasureAsync(2000, async () =>
        {
            await using DataSession<TProvider> session = await pool.RentAsync().ConfigureAwait(false);
            _ = await session.ScalarAsync<long>(FormattableStringFactory.Create("SELECT 1")).ConfigureAwait(false);
        }).ConfigureAwait(false);

        (double newSessionUs, long newSessionAlloc) = await MeasureAsync(500, async () =>
        {
            await using DataSession<TProvider> session = await DataSession<TProvider>.CreateAsync(options).ConfigureAwait(false);
        }).ConfigureAwait(false);

        (double rawOpenUs, long rawOpenAlloc) = await MeasureAsync(500, async () =>
        {
            await using DbConnection connection = TProvider.CreateConnection(connectionString, options);
            await connection.OpenAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[pool] {dialect}: rent={poolRentUs:F2} µs（{poolRentAlloc} B） · "
            + $"rent+query={poolRentQueryUs:F2} µs（{poolRentQueryAlloc} B） · "
            + $"new session={newSessionUs:F2} µs（{newSessionAlloc} B） · "
            + $"raw open={rawOpenUs:F2} µs（{rawOpenAlloc} B） · idle={pool.IdleCount}"));

        var envelope = new PerfResultEnvelope
        {
            Harness = "benchmarks",
            Version = Environment.GetEnvironmentVariable("PALORM_BENCH_VERSION") ?? "HEAD",
            Label = "pool",
            DetailPath = "（连接/池 churn 探针：控制台明细）",
            Environment = PerfResultWriter.CaptureEnvironment("PalORM.Benchmarks --pool"),
            Regime = new PerfResultRegime
            {
                ConnectionConfig = dialect == "SQLite"
                    ? BenchmarkConfig.RegimeBdnMemory
                    : "pg/mysql: 驱动默认（pool 探针；远程建连成本按条计）",
                SessionLifecycle = "pool rent/return vs new session vs raw open",
                Health = "unknown"
            }
        };
        envelope.Sections.Add(new PerfResultSection
        {
            Kind = "pool",
            Dialect = dialect,
            Label = dialect,
            Note = "会话池租还 / 带查询租还 / 新建会话 / 裸连接 churn（仅观测，不卡阈值）",
            Metrics = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["poolRentUs"] = poolRentUs,
                ["poolRentAllocB"] = poolRentAlloc,
                ["poolRentQueryUs"] = poolRentQueryUs,
                ["poolRentQueryAllocB"] = poolRentQueryAlloc,
                ["newSessionUs"] = newSessionUs,
                ["newSessionAllocB"] = newSessionAlloc,
                ["rawOpenUs"] = rawOpenUs,
                ["rawOpenAllocB"] = rawOpenAlloc,
                ["idleCount"] = pool.IdleCount
            }
        });
        string? path = PerfResultWriter.Write(envelope);
        Console.WriteLine(path is null
            ? "[pool] 结果库信封写入失败（不影响本次观测）"
            : $"[pool] 结果库信封已写入 {path}");
    }

    private static async Task<(double Us, long AllocB)> MeasureAsync(int iterations, Func<ValueTask> action)
    {
#pragma warning disable S1215 // 分配测量前强制回收是量具步骤（与 MemoryProbe 同口径）
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            await action().ConfigureAwait(false);
        }
        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;
#pragma warning restore S1215
        return (sw.Elapsed.TotalMicroseconds / iterations, alloc / iterations);
    }
}
