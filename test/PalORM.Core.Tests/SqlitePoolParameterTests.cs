using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>SQLite 池参数的忽略契约。
/// <para><b>为什么单列一条</b>：原实现在 <c>SqliteProvider.CreateConnection</c> 见
/// <c>DbOptions.PoolExplicitlyConfigured</c> 即抛 <c>NotSupportedException</c>，而
/// <c>DbOptions.Production(...)</c> 内部就调用 <c>WithPool</c>——于是「Production 预设 +
/// SQLite」必然在构造期失败，任何走预设或走 <c>PALORM_MAX_POOL_SIZE</c> 环境变量的
/// SQLite 部署都装不起来。修法是忽略而非抛错（SQLite 无服务端池可调这三个旋钮）。
/// 本用例在修复前必定抛异常，是防止回归的唯一入口。</para></summary>
public sealed class SqlitePoolParameterTests
{
    [Test]
    public async Task ProductionPreset_OnSqlite_CreatesSession()
    {
        // Production 内部 WithPool(maxSize: 100) → PoolExplicitlyConfigured = true
        DbOptions options = DbOptions.Production("Data Source=:memory:");

        await Assert.That(options.PoolExplicitlyConfigured).IsTrue();

        await using var session = await DataSession<SqliteProvider>.CreateAsync(options);
        await Assert.That(session).IsNotNull();
        // T14：裸 IsNotNull 之外补行为断言——会话真实应答 SELECT 1（:memory: 零外部依赖），
        // "Production 预设 + SQLite 可用"由此坐实为运行行为而非仅构造不抛
        PalORM.HealthResult health = await session.HealthCheckAsync();
        await Assert.That(health.IsHealthy).IsTrue();
    }

    [Test]
    public async Task WithPool_OnSqlite_IsIgnored_NotRejected()
    {
        var options = new DbOptions { ConnectionString = "Data Source=:memory:" }
            .WithPool(maxSize: 7, idleTimeoutSeconds: 11, lifetimeMinutes: 13);

        await using var session = await DataSession<SqliteProvider>.CreateAsync(options);
        await Assert.That(session).IsNotNull();

        // 参数被原样保留（字段仍可读），只是对 SQLite 无映射目标
        await Assert.That(options.MaxPoolSize).IsEqualTo(7);
        await Assert.That(options.PoolIdleTimeoutSeconds).IsEqualTo(11);
        await Assert.That(options.PoolLifetimeMinutes).IsEqualTo(13);
    }

    [Test]
    public async Task SqliteSession_WithPoolConfigured_StillExecutesQueries()
    {
        var options = new DbOptions { ConnectionString = "Data Source=:memory:" }.WithPool(maxSize: 4);

        await using var session = await DataSession<SqliteProvider>.CreateAsync(options);
        await session.ExecuteAsync($"CREATE TABLE pool_probe (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        _ = await session.ExecuteAsync($"INSERT INTO pool_probe (id, name) VALUES ({1L}, {"a"})");

        long count = await session.CountAsync<PoolProbeEntity>();
        await Assert.That(count).IsEqualTo(1L);
    }

    [Test]
    public async Task PreWarmAsync_OnSqlite_IsNoOpAndValidatesArguments()
    {
        // C4（v5.7）：SQLite 无连接池，无暖态可留——直接返回不报错（与上方池参数忽略契约同族）。
        // 观察方式：memory: 库连不上也无所谓——方法在建连前就按方言返回，参数校验仍生效。
        var options = new DbOptions { ConnectionString = "Data Source=:memory:" };
        await DataSession<SqliteProvider>.PreWarmAsync(options, count: 3);

        await Assert.That(() => DataSession<SqliteProvider>.PreWarmAsync(options, count: 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => DataSession<SqliteProvider>.PreWarmAsync(null!, count: 1))
            .Throws<ArgumentNullException>();
    }
}

/// <summary>池参数测试用实体。</summary>
[Table("pool_probe")]
internal sealed partial class PoolProbeEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}
