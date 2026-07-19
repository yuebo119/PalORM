using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

namespace PalORM.Testing;

/// <summary>测试数据库 Fixture——三行代码写集成测试，零 Docker。
/// <para>PG/MySQL 连接串通过 <see cref="TestEnvironment"/> 双层解析：
/// 环境变量 <c>PALORM_*_CONNECTION</c> &gt; appsettings.test.json 模板占位符。
/// 配置缺失时显式失败，不静默回退 localhost（ITM-428 凭据卫生）。</para></summary>
public static class TestDb
{
    /// <summary>创建 SQLite :memory: 数据会话。</summary>
    public static async Task<DataSession<SqliteProvider>> SqliteAsync(CancellationToken ct = default)
    {
        var options = new DbOptions { ConnectionString = TestEnvironment.ResolveSqliteConnectionString() };
        return await DataSession<SqliteProvider>.CreateAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>创建 PostgreSQL 数据会话。
    /// 连接串从 <see cref="TestEnvironment.ResolvePostgreSqlConnectionString"/> 解析；
    /// 配置缺失时显式失败——缺省回退系统库（postgres）会让测试 DDL 写进系统库（ITM-428）。</summary>
    public static async Task<DataSession<PostgreSqlProvider>> PostgreSqlAsync(CancellationToken ct = default)
    {
        string cs = TestEnvironment.ResolvePostgreSqlConnectionString();
        return await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions { ConnectionString = cs }, ct).ConfigureAwait(false);
    }

    /// <summary>创建 MySQL 数据会话。同 PG 的配置解析规则（ITM-428）。</summary>
    public static async Task<DataSession<MySqlProvider>> MySqlAsync(CancellationToken ct = default)
    {
        string cs = TestEnvironment.ResolveMySqlConnectionString();
        return await DataSession<MySqlProvider>.CreateAsync(new DbOptions { ConnectionString = cs }, ct).ConfigureAwait(false);
    }

    /// <summary>从内存行创建测试数据源。<b>不经过 RowFactory 物化</b>——原样返回传入实例，
    /// 仅用于构造集合输入；需要验证读写往返时请写入 :memory: SQLite 再读出（ITM-327）。</summary>
    public static IReadOnlyList<T> FromRows<T>(params T[] rows) where T : class, new()
        => rows;
}
