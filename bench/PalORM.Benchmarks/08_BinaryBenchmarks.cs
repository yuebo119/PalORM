using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalORM.Benchmarks;

// ═══════════════════════════════════════════════════════════════
// 二进制列基准——原生 byte[]（BLOB）vs Base64 TEXT（旧行为对照）
// v5.x：byte[] 白名单放行后补齐二进制场景基线。
// Base64 对照计入手工编解码全成本（写入 ToBase64String / 读取 FromBase64String）——
// 这是消费方绕开二进制的真实总成本，不只比列存储差异。
// 注意：Insert 基准随迭代增长表行数（与 01_CrudBenchmarks 同一口径）；
// GetAll 基线受其影响，横向对比时以同批运行结果为准。
// ═══════════════════════════════════════════════════════════════

[MemoryDiagnoser]
[SimpleJob(launchCount: BenchmarkConfig.StandardLaunch, warmupCount: BenchmarkConfig.StandardWarmup, iterationCount: BenchmarkConfig.StandardIterations)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SuppressMessage("Performance", "CA1812", Justification = "BenchmarkDotNet creates instances via reflection.")]
[SuppressMessage("Security", "CA2100", Justification = "Seed data uses compile-time constants.")]
public class BinaryBenchmarks : IAsyncDisposable
{
    private const int SmallSize = 256;
    private const int MediumSize = 64 * 1024;

    private static readonly byte[] Payload256B = new byte[SmallSize];
    private static readonly byte[] Payload64KB = new byte[MediumSize];

    private SqliteConnection? _keeper;
    private readonly DbOptions _options = new() { ConnectionString = BenchmarkConfig.SqliteCs };

    [GlobalSetup]
    public async Task Setup()
    {
        _keeper = BenchmarkConfig.OpenSqlite(BenchmarkConfig.SqliteCs);
        await BenchmarkConfig.ExecSqliteAsync(_keeper!,
            "CREATE TABLE IF NOT EXISTS bench_binary (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, payload BLOB NOT NULL)");
        await BenchmarkConfig.ExecSqliteAsync(_keeper!,
            "CREATE TABLE IF NOT EXISTS bench_binary_text (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, payload TEXT NOT NULL)");
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    // ═══════ 原生 BLOB ═══════

    [Benchmark, BenchmarkCategory("Binary-Blob")]
    public async Task PalORM_Blob_Insert_256B()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        await db.InsertAsync(new BenchBinary { name = "b256", payload = Payload256B });
    }

    [Benchmark, BenchmarkCategory("Binary-Blob")]
    public async Task PalORM_Blob_Insert_64KB()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        await db.InsertAsync(new BenchBinary { name = "b64k", payload = Payload64KB });
    }

    [Benchmark, BenchmarkCategory("Binary-Blob")]
    public async Task<int> PalORM_Blob_GetAll()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        return (await db.GetAllAsync<BenchBinary>()).Count;
    }

    // ═══════ Base64 TEXT 对照（旧行为全成本） ═══════

    [Benchmark, BenchmarkCategory("Binary-Base64Text")]
    public async Task PalORM_Base64Text_Insert_256B()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        await db.InsertAsync(new BenchBinaryText { name = "b256", payload = Convert.ToBase64String(Payload256B) });
    }

    [Benchmark, BenchmarkCategory("Binary-Base64Text")]
    public async Task PalORM_Base64Text_Insert_64KB()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        await db.InsertAsync(new BenchBinaryText { name = "b64k", payload = Convert.ToBase64String(Payload64KB) });
    }

    [Benchmark, BenchmarkCategory("Binary-Base64Text")]
    public async Task<int> PalORM_Base64Text_GetAll_Decoded()
    {
        await using var db = await DataSession<SqliteProvider>.CreateAsync(_options);
        List<BenchBinaryText> rows = await db.GetAllAsync<BenchBinaryText>();
        int totalBytes = 0;
        foreach (BenchBinaryText row in rows)
            totalBytes += Convert.FromBase64String(row.payload).Length;
        return totalBytes;
    }
}
