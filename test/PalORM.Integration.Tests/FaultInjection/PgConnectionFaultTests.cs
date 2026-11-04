using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.FaultInjection;

/// <summary>FLT-4：连接被外部重置（真库 PG）。
/// <para>从另一条连接 <c>pg_terminate_backend</c> 杀掉本会话的后端，压两段行为：
/// ①会话级：在途命令失败可见；失败被 <see cref="IDbProvider.IsConnectionFailure"/> 判定为连接判死后，
/// 下一条命令建命令前重建物理连接（2026-11-04 修复），同一条会话自动恢复。
/// ②池级：死会话归还后，池把它当不健康连接丢弃，下一次租用拿到可用连接。</para></summary>
[Property("Category", "ExternalDatabase")]
[Property("Category", "FaultInjection")]
[NotInParallel("FaultInjectionPgConnectionReset")]
public sealed class PgConnectionFaultTests
{
    private const long RowId = 9_200_001;

    [Test]
    public async Task TerminatedBackend_SessionRebuildsAndPoolDiscardsIt()
    {
        var options = new DbOptions { ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString() };

        await using var session = await DataSession<PostgreSqlProvider>.CreateAsync(options);
        await session.MigrateAsync();
        await session.ExecuteAsync($"DELETE FROM \"palorm_fault_row\" WHERE \"Id\" = {RowId}");
        await session.BulkInsertAsync([new FaultInjectionRow { Id = RowId, Name = "a", Amount = 1 }]);

        long backendPid = await session.ScalarAsync<long>($"SELECT pg_backend_pid()");
        await using (var killer = await DataSession<PostgreSqlProvider>.CreateAsync(options))
        {
            await killer.ExecuteAsync($"SELECT pg_terminate_backend(({backendPid})::int)");
        }

        // ① 在途失败可见
        Exception? first = null;
        try
        {
            await session.ScalarAsync<long>($"SELECT amount FROM \"palorm_fault_row\" WHERE \"Id\" = {RowId}");
        }
        catch (Exception ex)
        {
            first = ex;
        }
        await Assert.That(first).IsNotNull();

        // 判死生效（2026-11-04 修复，FLT-4）：下一条命令在建命令前重建物理连接，
        // 同一条会话自动恢复——调用方只需容忍一次失败
        long amount = await session.ScalarAsync<long>($"SELECT amount FROM \"palorm_fault_row\" WHERE \"Id\" = {RowId}");
        await Assert.That(amount).IsEqualTo(1);

        // ② 池级剔除：死会话归还后，下一次租用拿到可用连接
        await using var pool = DataSessionPool<PostgreSqlProvider>.Create(options, maxIdleSize: 2);
        DataSession<PostgreSqlProvider> rented = await pool.RentAsync();
        await rented.MigrateAsync();
        long rentedPid = await rented.ScalarAsync<long>($"SELECT pg_backend_pid()");
        await using (var killer = await DataSession<PostgreSqlProvider>.CreateAsync(options))
        {
            await killer.ExecuteAsync($"SELECT pg_terminate_backend(({rentedPid})::int)");
        }
        // 池租约内不做失败断言：池的租约路径可能已经探活并重建连接（与裸会话不同），
        // 这里只断言池在归还后仍能提供可用连接（下一次租用成功）。
        await rented.DisposeAsync();
        await using DataSession<PostgreSqlProvider> next = await pool.RentAsync();
        await Assert.That(await next.ScalarAsync<long>($"SELECT 1")).IsEqualTo(1);
    }
}
