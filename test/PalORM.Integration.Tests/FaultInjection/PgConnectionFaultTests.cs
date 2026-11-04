using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.FaultInjection;

/// <summary>FLT-4：连接被外部重置（真库 PG）。
/// <para>从另一条连接 <c>pg_terminate_backend</c> 杀掉本会话的后端，压两段行为：
/// ①会话级：在途命令失败可见，且同一条会话的后续命令继续失败（<c>InvalidOperationException:
/// Connection is not open</c>）。原因是 <c>DbConnection.State</c> 在后端被终止后仍报 Open，
/// PalORM 的健康检测（读连接的 <c>State == Open</c> 判据）看不到后端已死，因此不会自动重建，
/// 调用方必须换会话。这是实测到的当前契约，改进方向（后端探活）待裁决。
/// ②池级：死会话归还后，池把它当不健康连接丢弃，下一次租用拿到可用连接。</para></summary>
[Property("Category", "ExternalDatabase")]
[Property("Category", "FaultInjection")]
[NotInParallel("FaultInjectionPgConnectionReset")]
public sealed class PgConnectionFaultTests
{
    private const long RowId = 9_200_001;

    [Test]
    public async Task TerminatedBackend_SessionIsPoisoned_AndPoolDiscardsIt()
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

        // 同一条会话不会自愈：State 仍是 Open，PalORM 检测不到后端已死
        Exception? second = null;
        try
        {
            await session.ScalarAsync<long>($"SELECT amount FROM \"palorm_fault_row\" WHERE \"Id\" = {RowId}");
        }
        catch (Exception ex)
        {
            second = ex;
        }
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.Message).Contains("Connection is not open");

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
