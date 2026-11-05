using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

/// <summary>2026-10-22 终审 P2-2：池化空闲会话被 NAT/LB 静默掐断后 State 仍可能为 Open，
/// 旧实现反复租出死连接。空闲超过 PoolConnectionValidationInterval 时租出前 SELECT 1 探活，
/// 失败由池丢弃并换新，调用方不收到过期连接。</summary>
/// <para><b>2026-11-04 补确定性覆盖</b>：下面两条用例的探活分支命中与否不同——被杀连接的
/// <c>State</c> 若报 Closed，池直接换新、不经 <c>ValidateConnectionAsync</c>；只有报 Open 才走探活。
/// 实测同一代码在 CI 两个 run 给出 51.28% / 52.05%（差 6 行全在该函数），双模态使 1pp
/// 自收紧上限无解。故补一条用<b>健康连接</b>制造空闲窗口的用例：连接未被杀、State 恒 Open，
/// 探活必然执行，读数确定。</para>
[NotInParallel("ExtBulkTable")]
public sealed class PoolHealthTests
{
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task IdleHealthyConnection_IsValidatedOnRent()
    {
        string cs = TestEnvironment.ResolvePostgreSqlConnectionString();
        var pool = DataSessionPool<PostgreSqlProvider>.Create(new DbOptions
        {
            ConnectionString = cs,
            PoolConnectionValidationInterval = TimeSpan.FromMilliseconds(1),
        }, maxIdleSize: 1);
        try
        {
            DataSession<PostgreSqlProvider> first = await pool.RentAsync();
            await Assert.That(await first.ScalarAsync<int>($"SELECT 1")).IsEqualTo(1);
            await first.DisposeAsync();
            await Task.Delay(20);   // 空闲超过 1ms 的探活阈值

            // 连接未被杀，State 恒 Open → 本次租出必然经 ValidateConnectionAsync（SELECT 1）
            DataSession<PostgreSqlProvider> second = await pool.RentAsync();
            await Assert.That(await second.ScalarAsync<int>($"SELECT 1")).IsEqualTo(1);
            await second.DisposeAsync();
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    /// <summary>MySQL 同形覆盖（2026-11-05）：<c>MySqlProvider.ValidateConnectionAsync</c> 此前
    /// 在 CI 读数恒为 0%——探活分支只在 PG 侧有用例。两条 Provider 的探活实现各自独立
    /// （MySQL 走 <c>MySqlConnection</c> + 超时透传），PG 的用例证明不了 MySQL 的。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_IdleHealthyConnection_IsValidatedOnRent()
    {
        string cs = TestEnvironment.ResolveMySqlConnectionString();
        var pool = DataSessionPool<MySqlProvider>.Create(new DbOptions
        {
            ConnectionString = cs,
            PoolConnectionValidationInterval = TimeSpan.FromMilliseconds(1),
        }, maxIdleSize: 1);
        try
        {
            DataSession<MySqlProvider> first = await pool.RentAsync();
            await Assert.That(await first.ScalarAsync<int>($"SELECT 1")).IsEqualTo(1);
            await first.DisposeAsync();
            await Task.Delay(20);   // 空闲超过 1ms 的探活阈值

            // 连接未被杀，State 恒 Open → 本次租出必然经 ValidateConnectionAsync（SELECT 1）
            DataSession<MySqlProvider> second = await pool.RentAsync();
            await Assert.That(await second.ScalarAsync<int>($"SELECT 1")).IsEqualTo(1);
            await second.DisposeAsync();
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task IdleBrokenConnection_IsReplacedOnRent()
    {
        string cs = TestEnvironment.ResolvePostgreSqlConnectionString();
        var pool = DataSessionPool<PostgreSqlProvider>.Create(new DbOptions
        {
            ConnectionString = cs,
            PoolConnectionValidationInterval = TimeSpan.FromMilliseconds(1),
        }, maxIdleSize: 1);
        try
        {
            DataSession<PostgreSqlProvider> first = await pool.RentAsync();
            int backendPid = await first.ScalarAsync<int>($"SELECT pg_backend_pid()");
            await first.DisposeAsync();
            await Task.Delay(20);

            await using (var killer = await DataSession<PostgreSqlProvider>.CreateAsync(
                new DbOptions { ConnectionString = cs }))
            {
                bool terminated = await killer.ScalarAsync<bool>(
                    $"SELECT pg_terminate_backend({backendPid})");
                await Assert.That(terminated).IsTrue();
            }

            DataSession<PostgreSqlProvider> second = await pool.RentAsync();
            await Assert.That(await second.ScalarAsync<int>($"SELECT 1")).IsEqualTo(1);
            await second.DisposeAsync();
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
}
