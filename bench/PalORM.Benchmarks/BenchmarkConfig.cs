using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using RepoDb;

namespace PalORM.Benchmarks;

/// <summary>v5.0 基准体系统一配置——消除 magic number，所有 benchmark 类引用这些常量。
/// <para>对齐 Dapper 官方 benchmarks/ 的设计：
/// <list type="bullet">
/// <item>ADO.NET 基线——每个 category 的 ADO_NET_* 标 [Benchmark(Baseline = true)]</item>
/// <item>多 ORM 对照——Dapper（含 DapperAot）+ RepoDb（同类 micro-ORM）</item>
/// <item>NextId 轮询——单点查询用 1..SeedRows 轮询，避免 SQLite page cache 命中</item>
/// </list></para></summary>
internal static class BenchmarkConfig
{
    // ── 连接串 ──
    public const string SqliteCs = "Data Source=bench;Mode=Memory;Cache=Shared";
    public const string SpeedSqliteCs = "Data Source=bench_speed;Mode=Memory;Cache=Shared";
    public const string CacheSqliteCs = "Data Source=bench_cache;Mode=Memory;Cache=Shared";
    public const int SeedRows = 10000;

    // ── Job 配置（消除 magic number 散落各处）──
    // 标准：正式报告用，统计可信度高（Adam Sitnik 推荐 ≥15 迭代总量）
    public const int StandardLaunch = 3, StandardWarmup = 5, StandardIterations = 10;
    // 快速：远程 DB 基准用（网络延迟 > 统计精度，快速迭代优先）
    public const int FastLaunch = 1, FastWarmup = 3, FastIterations = 5;
    // 高精度：纳秒级 SQL 构建（原 3/5/10 在慢机上 Error/Mean 达 14%）
    public const int PrecisionLaunch = 5, PrecisionWarmup = 10, PrecisionIterations = 15;
    public const int PrecisionInvocations = 4096;

    // ── 回归阈值（v4.0→v5.0 对比，超出则标注"回归"）──
    public const double RegressionMedianPct = 10.0;
    public const double RegressionAllocatedPct = 20.0;

    // ── 共享辅助方法（从各 benchmark 类提取，避免重复）──

    /// <summary>Dapper 官方 Step() 等价：轮询 1..SeedRows，避免 page cache 命中。</summary>
    public static long NextId(ref long counter)
        => (Interlocked.Increment(ref counter) % SeedRows) + 1;

    /// <summary>SQLite 连接打开（共享内存库）。</summary>
    public static SqliteConnection OpenSqlite(string cs)
    {
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    /// <summary>PG 连接打开。</summary>
    public static NpgsqlConnection OpenPg(string cs)
    {
        var c = new NpgsqlConnection(cs);
        c.Open();
        return c;
    }

    /// <summary>MySQL 连接打开。</summary>
    public static MySqlConnection OpenMySql(string cs)
    {
        var c = new MySqlConnection(cs);
        c.Open();
        return c;
    }

    /// <summary>SQLite DDL 执行辅助。</summary>
    public static async Task ExecSqliteAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>SQLite seed：建 bench_orders + bench_versioned + bench_soft + 10K 行。</summary>
    public static async Task SeedSqliteAsync(SqliteConnection conn)
    {
        await ExecSqliteAsync(conn, "CREATE TABLE bench_orders (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, total REAL NOT NULL, created_at INTEGER NOT NULL)");
        for (int i = 0; i < SeedRows; i++)
            await ExecSqliteAsync(conn, $"INSERT INTO bench_orders (status, total, created_at) VALUES ('S{i}', {i * 10m}, {i})");
        await ExecSqliteAsync(conn, "CREATE TABLE bench_versioned (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 0)");
        await ExecSqliteAsync(conn, "INSERT INTO bench_versioned (name, version) VALUES ('seed', 0)");
        await ExecSqliteAsync(conn, "CREATE TABLE bench_soft (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, deleted_at TEXT)");
        await ExecSqliteAsync(conn, "INSERT INTO bench_soft (name) VALUES ('seed')");
        // RepoDB 全局初始化（仅一次）
        GlobalConfiguration.Setup().UseSqlite();
    }

    /// <summary>PG DDL 执行辅助。</summary>
    public static async Task ExecPgAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>MySQL DDL 执行辅助。</summary>
    public static async Task ExecMySqlAsync(MySqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
