using Npgsql;
using PalORM;
using PalORM.PostgreSql;
using MySqlConnector;
using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>T10/step7（R47）：连接串显式设成驱动默认值的旋钮不被静默改写。
/// <para><b>背景</b>：R47 曾记录"当前值 == 驱动默认值"判据把用户显式意图静默改写
/// （MaxAutoPrepare=0 / ConnectionLifetime=3600 / ServerRedirectionMode=Disabled）。
/// PROV-001（2026-09-23）已修为 <c>HasExplicitKey</c>（Keys 集合只含显式出现的键）双条件
/// （ContainsKey 管意图 + 值比对管驱动默认漂移）。本组把该契约钉死，防回归。</para>
/// <para>可观测面：<c>CreateConnection</c> 返回的连接其 <c>ConnectionString</c> 保留显式设置的
/// 键（探针八实测：显式设成默认值的键重建后仍在串中，未设置的键不出现）。</para>
/// <para>B63 同族（2026-10-03）：PG/MySQL 真库为共享库，建表 DDL 与其它外部库用例争同一
/// 系统目录——必须与 <c>ExtBulkTable</c> 组串行，否则并发建表触发 42P07/42710。</para></summary>
[NotInParallel("ExtBulkTable")]
[Property("Category", "ExternalDatabase")]
public sealed class ConnectionStringOverrideTests
{
    private static string BaseCs() => TestEnvironment.ResolvePostgreSqlConnectionString();

    [Test]
    public async Task ExplicitDriverDefaultValues_AreNotSilentlyOverwritten()
    {
        // 三个显式 = 驱动默认值的旋钮：调优策略必须让步
        var cs = BaseCs() + ";Max Auto Prepare=0;Connection Lifetime=3600;No Reset On Close=false";
        await using var conn = PostgreSqlProvider.CreateConnection(cs, new DbOptions { ConnectionString = cs });
        var round = new NpgsqlConnectionStringBuilder(conn.ConnectionString);

        await Assert.That(round.MaxAutoPrepare).IsEqualTo(0);
        await Assert.That(round.ConnectionLifetime).IsEqualTo(3600);
        await Assert.That(round.NoResetOnClose).IsFalse();
    }

    [Test]
    public async Task UnsetKnobs_GetTuningValues()
    {
        // 反向对照：未显式给出的旋钮仍应用 PalORM 调优推荐值（防"全都不覆盖"式回退）
        await using var conn = PostgreSqlProvider.CreateConnection(BaseCs(), new DbOptions { ConnectionString = BaseCs() });
        var round = new NpgsqlConnectionStringBuilder(conn.ConnectionString);

        await Assert.That(round.MaxAutoPrepare).IsEqualTo(100);
        await Assert.That(round.AutoPrepareMinUsages).IsEqualTo(2);
        await Assert.That(round.NoResetOnClose).IsTrue();
        await Assert.That(round.ReadBufferSize).IsEqualTo(16384);
        await Assert.That(round.Enlist).IsFalse();
    }

    [Test]
    public async Task ExplicitTuningValue_WinsOverDbOptionsDefault()
    {
        // 用户显式给非默认调优值：不应被 DbOptions 的默认值改写（ITM-612 同族）
        var cs = BaseCs() + ";Maximum Pool Size=500";
        await using var conn = PostgreSqlProvider.CreateConnection(
            cs, new DbOptions { ConnectionString = cs, MaxPoolSize = 100 });
        var round = new NpgsqlConnectionStringBuilder(conn.ConnectionString);

        await Assert.That(round.MaxPoolSize).IsEqualTo(500);
    }

}

/// <summary>MySQL-6/T10 移植：MySQL 侧连接串覆盖契约（与 PG 侧三态同构）。</summary>
[NotInParallel("ExtBulkTable")]
[Property("Category", "ExternalDatabase")]
public sealed class MySqlConnectionStringOverrideTests
{
    private static string BaseCs() => TestEnvironment.ResolveMySqlConnectionString();

    [Test]
    public async Task ExplicitDriverDefaultValues_AreNotSilentlyOverwritten()
    {
        var cs = BaseCs() + ";Connection Lifetime=0;Connection Reset=true";
        await using var conn = MySqlProvider.CreateConnection(cs, new DbOptions { ConnectionString = cs });
        var round = new MySqlConnectionStringBuilder(conn.ConnectionString);

        await Assert.That(round.ConnectionLifeTime).IsEqualTo(0u);
        await Assert.That(round.ConnectionReset).IsTrue();
    }

    [Test]
    public async Task UnsetKnobs_GetTuningValues()
    {
        await using var conn = MySqlProvider.CreateConnection(BaseCs(), new DbOptions { ConnectionString = BaseCs() });
        var round = new MySqlConnectionStringBuilder(conn.ConnectionString);

        await Assert.That(round.AutoEnlist).IsFalse();
        await Assert.That(round.ConnectionReset).IsFalse();
        await Assert.That(round.CancellationTimeout).IsEqualTo(5);
        await Assert.That(round.ServerRedirectionMode).IsEqualTo(MySqlServerRedirectionMode.Preferred);
    }

    [Test]
    public async Task ExplicitTuningValue_WinsOverDbOptionsDefault()
    {
        var cs = BaseCs() + ";Maximum Pool Size=500";
        await using var conn = MySqlProvider.CreateConnection(
            cs, new DbOptions { ConnectionString = cs, MaxPoolSize = 100 });
        var round = new MySqlConnectionStringBuilder(conn.ConnectionString);

        await Assert.That(round.MaximumPoolSize).IsEqualTo(500u);
    }
}
