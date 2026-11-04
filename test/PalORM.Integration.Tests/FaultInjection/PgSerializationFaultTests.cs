using System.Data;
using System.Data.Common;
using Npgsql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests.FaultInjection;

/// <summary>FLT-8：PG 序列化失败 40001（真库）。
/// <para>两个 SERIALIZABLE 事务读同一行，一方先提交，另一方再更新同一行即触发
/// 「could not serialize access due to concurrent update」。断言三件：SqlState 是 40001、
/// 该异常被 Provider 判为可重试、冲突结束后同一更新能成功。</para>
/// <para>只在全量通道跑（<c>ExternalDatabase</c>）：序列化冲突是 PG 的隔离级别语义，
/// 其他方言与内存形态复现不出来。</para></summary>
[Property("Category", "ExternalDatabase")]
[Property("Category", "FaultInjection")]
[NotInParallel("FaultInjectionPgSerialization")]
public sealed class PgSerializationFaultTests
{
    private const long RowId = 9_100_001;

    [Test]
    public async Task SerializableConflict_Produces40001_AndRetrySucceeds()
    {
        var options = new DbOptions { ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString() };
        await using var writer = (await DataSession<PostgreSqlProvider>.CreateAsync(options))
            .WithIsolationLevel(IsolationLevel.Serializable);
        await using var reader = (await DataSession<PostgreSqlProvider>.CreateAsync(options))
            .WithIsolationLevel(IsolationLevel.Serializable);
        await writer.MigrateAsync();
        // 上一次运行可能留下同一行（真库是持久化的），先清掉再插
        await writer.ExecuteAsync($"DELETE FROM \"palorm_fault_row\" WHERE \"Id\" = {RowId}");
        await writer.BulkInsertAsync(
        [
            new FaultInjectionRow { Id = RowId, Name = "a", Amount = 1 },
        ]);

        using var writerRead = new ManualResetEventSlim(false);
        using var readerRead = new ManualResetEventSlim(false);

        Task writerWork = Task.Run(async () =>
        {
            await using DbTransaction transaction = await writer.BeginTransactionAsync();
            await writer.ScalarAsync<long>($"SELECT COUNT(*) FROM \"palorm_fault_row\" WHERE \"Id\" = {RowId}");
            writerRead.Set();
            await Task.Run(() => readerRead.Wait(TimeSpan.FromSeconds(30)));
            await writer.UpdateAsync(new FaultInjectionRow { Id = RowId, Name = "a", Amount = 2 });
            await transaction.CommitAsync();
        });

        NpgsqlException? conflict = await Assert.ThrowsAsync<NpgsqlException>(async () =>
        {
            await using DbTransaction transaction = await reader.BeginTransactionAsync();
            await reader.ScalarAsync<long>($"SELECT COUNT(*) FROM \"palorm_fault_row\" WHERE \"Id\" = {RowId}");
            readerRead.Set();
            await Task.Run(() => writerRead.Wait(TimeSpan.FromSeconds(30)));
            // writer 提交后再更新同一行：rw-dependency 触发序列化失败
            await Task.Delay(300);
            await reader.UpdateAsync(new FaultInjectionRow { Id = RowId, Name = "a", Amount = 3 });
            await transaction.CommitAsync();
        });

        await Assert.That(conflict).IsNotNull();
        await Assert.That(conflict!.SqlState).IsEqualTo("40001")
            .Because($"SqlState={conflict!.SqlState} msg={conflict.Message}");
        // PG Provider 未覆写 IsTransient，走 IDbProvider 默认：DbException.IsTransient
        await Assert.That(((DbException)conflict!).IsTransient).IsTrue();

        await writerWork;

        await using var retrying = (await DataSession<PostgreSqlProvider>.CreateAsync(options))
            .WithRetry(maxRetries: 3, attempt => TimeSpan.FromMilliseconds(100 * (attempt + 1)));
        await retrying.UpdateAsync(new FaultInjectionRow { Id = RowId, Name = "a", Amount = 5 });
        await Assert.That(await retrying.ScalarAsync<long>($"SELECT amount FROM \"palorm_fault_row\" WHERE \"Id\" = {RowId}"))
            .IsEqualTo(5);
    }
}
