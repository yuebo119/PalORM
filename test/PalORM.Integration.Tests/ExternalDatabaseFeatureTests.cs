using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>
/// 真库特性测试——审计 2026-09-19 TEST-010/011 补齐 + SHAPE-010 参数化 LIMIT 的真库执行验证。
/// <para>① <see cref="StoredProcBuilder"/> 的 happy-path（建 proc → 输入参数绑定 → 输出参数回读，
/// PG/MySQL 各一；此前只有 SQLite 边界测试，输出参数行为被注释为"由 CI 覆盖"而无处落地）；
/// ② <see cref="PgNotificationListener"/> 真连接 LISTEN/NOTIFY（此前只有 Fake 连接的协议层验证）；
/// ③ Take/Skip 参数化 LIMIT 在 PG/MySQL 的分页正确性（此前 15 个真库测试无 Take/Skip 路径）。</para>
/// <para>清理纪律：try/finally DROP（对齐 PostgreSqlIntegrationTests 先例）；超时护栏用
/// WaitAsync + CancellationTokenSource，零 Sleep/轮询。坏地址实验（连接串指向 port 1）已证
/// 本组测试真实连库，非静默跳过。</para></summary>
public sealed class ExternalDatabaseFeatureTests
{
    // M2-1：统一走 TestDb 方言夹具（复活死代码 + 三行代码写真库测试的设计意图）

    // ─── SHAPE-010 参数化 LIMIT 的真库分页执行 ───────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_ParameterizedLimit_TakeSkip_ReturnsCorrectPage()
    {
        await using var db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_limit_probe CASCADE");
            await db.ExecuteAsync($"CREATE TABLE palorm_limit_probe (id BIGINT PRIMARY KEY, label VARCHAR(32) NOT NULL)");
            for (int i = 1; i <= 5; i++)
                await db.ExecuteAsync($"INSERT INTO palorm_limit_probe (id, label) VALUES ({(long)i}, {"row" + i})");

            var page = await db.From<LimitProbeEntity>().OrderBy(x => x.Id).Skip(2).Take(3).ToListAsync();

            // 值断言（非仅行数）：Skip 2 Take 3 → id 3,4,5，占位符 LIMIT 绑定错值即刻暴露
            await Assert.That(string.Join(",", page.Select(static r => r.Id))).IsEqualTo("3,4,5");
        }
        finally { await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_limit_probe CASCADE"); }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_ParameterizedLimit_TakeSkip_ReturnsCorrectPage()
    {
        await using var db = await TestDb.MySqlAsync();
        try
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_limit_probe");
            await db.ExecuteAsync($"CREATE TABLE palorm_limit_probe (id BIGINT PRIMARY KEY, label VARCHAR(32) NOT NULL)");
            for (int i = 1; i <= 5; i++)
                await db.ExecuteAsync($"INSERT INTO palorm_limit_probe (id, label) VALUES ({(long)i}, {"row" + i})");

            // MySQL 方言形态：LIMIT @skip, @take（位置参数）
            var page = await db.From<LimitProbeEntity>().OrderBy(x => x.Id).Skip(2).Take(3).ToListAsync();

            await Assert.That(string.Join(",", page.Select(static r => r.Id))).IsEqualTo("3,4,5");
        }
        finally { await db.ExecuteAsync($"DROP TABLE IF EXISTS palorm_limit_probe"); }
    }

    // ─── TEST-010：存储过程 happy-path（输入/输出参数往返） ───────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_StoredProcedure_InOutParams_RoundTrip()
    {
        await using var db = await TestDb.PostgreSqlAsync();
        try
        {
            await db.ExecuteAsync(
                $"CREATE PROCEDURE palorm_sp_probe(IN p_in INT, OUT p_out INT) LANGUAGE plpgsql AS $$ BEGIN p_out := p_in * 2; END $$");
            var sp = db.StoredProc("palorm_sp_probe")
                .WithParam("p_in", 21)
                .WithOutputParam<int>("p_out");

            await sp.ExecuteAsync();

            await Assert.That(sp.GetOutputValue<int>("p_out")).IsEqualTo(42);
        }
        finally
        {
            await db.ExecuteAsync($"DROP PROCEDURE IF EXISTS palorm_sp_probe");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_StoredProcedure_InOutParams_RoundTrip()
    {
        await using var db = await TestDb.MySqlAsync();
        try
        {
            await db.ExecuteAsync(
                $"CREATE PROCEDURE palorm_sp_probe(IN p_in INT, OUT p_out INT) BEGIN SET p_out = p_in * 2; END");
            var sp = db.StoredProc("palorm_sp_probe")
                .WithParam("p_in", 21)
                .WithOutputParam<int>("p_out");

            await sp.ExecuteAsync();

            await Assert.That(sp.GetOutputValue<int>("p_out")).IsEqualTo(42);
        }
        finally
        {
            await db.ExecuteAsync($"DROP PROCEDURE IF EXISTS palorm_sp_probe");
        }
    }

    // ─── TEST-011：LISTEN/NOTIFY 真连接冒烟 ───────────────

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_ListenNotify_RealConnection_ReceivesPayload()
    {
        var received = new TaskCompletionSource<(string Channel, string Payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new PgNotificationListener(TestEnvironment.ResolvePostgreSqlConnectionString(), "palorm_notify_probe");
        listener.OnNotification += (_, e) => received.TrySetResult((e.Channel, e.Payload));
        try
        {
            await listener.StartAsync();

            // LISTEN 就绪后经独立会话发通知（payload 为 NOTIFY 语法字面量，不可参数化）
            await using var db = await TestDb.PostgreSqlAsync();
            await db.ExecuteAsync($"NOTIFY palorm_notify_probe, 'probe-payload'");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            (string channel, string payload) = await received.Task.WaitAsync(timeout.Token);

            await Assert.That(channel).IsEqualTo("palorm_notify_probe");
            await Assert.That(payload).IsEqualTo("probe-payload");
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }
}

[Table("palorm_limit_probe")]
internal sealed partial class LimitProbeEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }
    [Column("label")]
    public string Label { get; set; } = "";
}
