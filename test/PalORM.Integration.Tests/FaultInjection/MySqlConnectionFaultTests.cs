using PalORM.MySql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.FaultInjection;

/// <summary>FLT-4：连接被外部重置（真库 MySQL）。
/// <para>与 PG 版同构：另一条连接执行 <c>KILL</c> 掉本会话的连接，压两段行为：
/// ①会话级：在途命令失败可见；连接判死后下一条命令自动重建（2026-11-04 修复）；
/// ②池级：归还后池仍能提供可用连接。</para></summary>
[Property("Category", "ExternalDatabase")]
[Property("Category", "FaultInjection")]
[NotInParallel("FaultInjectionMySqlConnectionReset")]
public sealed class MySqlConnectionFaultTests
{
    private const long RowId = 9_300_001;

    [Test]
    public async Task KilledConnection_SessionRebuildsAndPoolDiscardsIt()
    {
        var options = new DbOptions { ConnectionString = TestEnvironment.ResolveMySqlConnectionString() };

        await using var session = await DataSession<MySqlProvider>.CreateAsync(options);
        await session.MigrateAsync();
        await session.ExecuteAsync($"DELETE FROM palorm_fault_row WHERE Id = {RowId}");
        await session.BulkInsertAsync([new FaultInjectionRow { Id = RowId, Name = "a", Amount = 1 }]);

        long connectionId = await session.ScalarAsync<long>($"SELECT CONNECTION_ID()");
        await using (var killer = await DataSession<MySqlProvider>.CreateAsync(options))
        {
            await killer.ExecuteAsync($"KILL {connectionId}");
        }

        Exception? first = null;
        try
        {
            await session.ScalarAsync<long>($"SELECT amount FROM palorm_fault_row WHERE Id = {RowId}");
        }
        catch (Exception ex)
        {
            first = ex;
        }
        await Assert.That(first).IsNotNull();

        // 判死生效（2026-11-04 修复，FLT-4）：下一条命令建命令前重建连接，会话自动恢复
        long amount = await session.ScalarAsync<long>($"SELECT amount FROM palorm_fault_row WHERE Id = {RowId}");
        await Assert.That(amount).IsEqualTo(1);

        await using var pool = DataSessionPool<MySqlProvider>.Create(options, maxIdleSize: 2);
        DataSession<MySqlProvider> rented = await pool.RentAsync();
        await rented.MigrateAsync();
        long rentedConnectionId = await rented.ScalarAsync<long>($"SELECT CONNECTION_ID()");
        await using (var killer = await DataSession<MySqlProvider>.CreateAsync(options))
        {
            await killer.ExecuteAsync($"KILL {rentedConnectionId}");
        }
        await rented.DisposeAsync();

        await using DataSession<MySqlProvider> next = await pool.RentAsync();
        await Assert.That(await next.ScalarAsync<long>($"SELECT 1")).IsEqualTo(1);
    }
}
