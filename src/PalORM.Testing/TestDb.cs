using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.Testing;

/// <summary>测试数据库 Fixture——三行代码写集成测试，零 Docker。
/// PG/MySQL 连接串从环境变量读取，未设置时使用默认值。</summary>
public static class TestDb
{
    /// <summary>创建 SQLite :memory: 数据会话。</summary>
    public static async Task<DataSession<SqliteProvider>> SqliteAsync(CancellationToken ct = default)
    {
        var options = new DbOptions { ConnectionString = "Data Source=:memory:" };
        return await DataSession<SqliteProvider>.CreateAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>创建 PostgreSQL 数据会话。
    /// 连接串从环境变量 PALORM_PG_CONNECTION 读取。</summary>
    public static async Task<DataSession<PostgreSqlProvider>> PostgreSqlAsync(CancellationToken ct = default)
    {
        string cs = Environment.GetEnvironmentVariable("PALORM_PG_CONNECTION")
            ?? "Host=localhost;Username=postgres;Password=;Database=postgres";
        return await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions { ConnectionString = cs }, ct).ConfigureAwait(false);
    }

    /// <summary>创建 MySQL 数据会话。
    /// 连接串从环境变量 PALORM_MYSQL_CONNECTION 读取。</summary>
    public static async Task<DataSession<MySqlProvider>> MySqlAsync(CancellationToken ct = default)
    {
        string cs = Environment.GetEnvironmentVariable("PALORM_MYSQL_CONNECTION")
            ?? "Server=localhost;User=root;Password=;Database=mysql";
        return await DataSession<MySqlProvider>.CreateAsync(new DbOptions { ConnectionString = cs }, ct).ConfigureAwait(false);
    }

    /// <summary>从内存行创建测试数据源——使用真实 RowFactory 物化，避免 Mock 假绿。</summary>
    public static IReadOnlyList<T> FromRows<T>(params T[] rows) where T : class, new()
        => rows;
}
