using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AotProbe.Adapters;

/// <summary>原生 ADO 旁路（SQL 库变体链接；litedb 不链接——它用第二个 LiteDatabase 连接当旁路）。
/// 用途：B 组数据引导（不经 ORM 构造实体）与存储形态观测（独立于 ORM 物化器读回）。
/// 值一律参数化（Name 含引号/CJK，拼接即注入）。DDL 列形态与各库 ORM 映射约定一致
///（Id INTEGER PRIMARY KEY AUTOINCREMENT，name/category TEXT，amount TEXT 保 decimal 精度）。</summary>
internal static class RawChannel
{
    public static async Task EnsureOrderTableAsync(string dbPath) => await ExecAsync(dbPath,
        "CREATE TABLE IF NOT EXISTS ap_order (Id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, amount TEXT NOT NULL, category TEXT NOT NULL)").ConfigureAwait(false);

    public static async Task EnsureCatalogTableAsync(string dbPath) => await ExecAsync(dbPath,
        "CREATE TABLE IF NOT EXISTS ap_catalog (Id INTEGER PRIMARY KEY, label TEXT NOT NULL)").ConfigureAwait(false);

    /// <summary>属性同形态 DDL（repodb 用）：RepoDb 1.17 参数绑定委托按字段名与属性名**精确同形**匹配，
    /// 大小写不一致（Name↔name）即"Must add values for parameters"（最小复现定案 2026-10-28）。
    /// SQLite 列标识符大小写不敏感，其余 SQL（SELECT name / INSERT (name,...)）对本形态依然合法。</summary>
    public static async Task EnsureOrderTableCamelAsync(string dbPath) => await ExecAsync(dbPath,
        "CREATE TABLE IF NOT EXISTS ap_order (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Amount TEXT NOT NULL, Category TEXT NOT NULL)").ConfigureAwait(false);

    public static async Task EnsureCatalogTableCamelAsync(string dbPath) => await ExecAsync(dbPath,
        "CREATE TABLE IF NOT EXISTS ap_catalog (Id BIGINT PRIMARY KEY, Label TEXT NOT NULL)").ConfigureAwait(false);
    // 上行用 BIGINT 而非 INTEGER：SQLite 仅 "INTEGER PRIMARY KEY" 是 rowid 别名（隐式自增），
    // RepoDb 据此把 Id 判为 identity 丢弃显式值（实测行拿 Id=1）；BIGINT 主键为普通唯一索引，显式插入成立

    public static async Task SeedOrderAsync(string dbPath, string name, string amount, string category)
        => await ExecAsync(dbPath,
            "INSERT INTO ap_order (name, amount, category) VALUES (@p0, @p1, @p2)",
            ("@p0", name), ("@p1", amount), ("@p2", category)).ConfigureAwait(false);

    public static async Task SeedCatalogAsync(string dbPath, long id, string label)
        => await ExecAsync(dbPath,
            "INSERT OR REPLACE INTO ap_catalog (Id, label) VALUES (@p0, @p1)",
            ("@p0", id), ("@p1", label)).ConfigureAwait(false);

    public static async Task<long> TableCountAsync(string dbPath)
        => long.Parse(await ScalarStringAsync(dbPath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('ap_order', 'ap_catalog')",
            []).ConfigureAwait(false), CultureInfo.InvariantCulture);

    public static async Task<long> CountAsync(string dbPath, string table, string whereName, string nameValue)
        => long.Parse(await ScalarStringAsync(dbPath,
            $"SELECT COUNT(*) FROM {table} WHERE {whereName} = @p0", ("@p0", nameValue)).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

    public static async Task<string> ScalarStringAsync(string dbPath, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            _ = cmd.Parameters.AddWithValue(name, value);
        return Convert.ToString(await cmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "";
    }

    public static async Task ExecAsync(string dbPath, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            _ = cmd.Parameters.AddWithValue(name, value);
        _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
