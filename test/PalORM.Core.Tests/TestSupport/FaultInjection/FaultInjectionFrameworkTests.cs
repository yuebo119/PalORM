using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>FLT-1 框架自身的五条单测：注入按序号精确生效、未命中序号逐位透传、
/// 延迟与取消可观测、reader 行级故障能造出来。
/// <para>为什么先测框架：阶段 2 的事务/批量/连接故障用例全部建立在「故障确实只发生在
/// 我指定的那一条命令、那一行」之上。框架漏一格，后面每条用例的结论都不可信，
/// 而且失效形态是假绿（没注入成功 = 什么都没测到）。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class FaultInjectionFrameworkTests
{
    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using DbCommand setup = connection.CreateCommand();
        setup.CommandText = "CREATE TABLE probe(id INTEGER PRIMARY KEY, name TEXT)";
        await setup.ExecuteNonQueryAsync();
        return connection;
    }

    [Test]
    public async Task NoPlan_BehavesExactlyLikeInnerConnection()
    {
        await using SqliteConnection inner = await OpenAsync();
        await using var faulty = new FaultyConnection(inner);

        await using DbCommand insert = faulty.CreateCommand();
        insert.CommandText = "INSERT INTO probe(name) VALUES ('a')";
        await Assert.That(await insert.ExecuteNonQueryAsync()).IsEqualTo(1);

        await using DbCommand select = faulty.CreateCommand();
        select.CommandText = "SELECT COUNT(*) FROM probe";
        await Assert.That(await select.ExecuteScalarAsync()).IsEqualTo(1L);

        await Assert.That(faulty.CommandsCreated).IsEqualTo(2);
        await Assert.That(faulty.Plans.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExceptionOnSecondCommand_FiresOnlyAtThatOrdinal()
    {
        await using SqliteConnection inner = await OpenAsync();
        var boom = new InvalidOperationException("注入：第二条命令失败");
        await using var faulty = new FaultyConnection(inner).FailOnCommand(2, boom);

        await using DbCommand first = faulty.CreateCommand();
        first.CommandText = "SELECT 1";
        await Assert.That(await first.ExecuteScalarAsync()).IsEqualTo(1L);   // 第一条不受影响

        await using DbCommand second = faulty.CreateCommand();
        second.CommandText = "SELECT 2";
        InvalidOperationException? thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await second.ExecuteScalarAsync());
        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).IsEqualTo(boom.Message);
        await Assert.That(thrown).IsSameReferenceAs(boom);
    }

    [Test]
    public async Task DelayOnFirstCommand_IsObservable()
    {
        await using SqliteConnection inner = await OpenAsync();
        TimeSpan delay = TimeSpan.FromMilliseconds(150);
        await using var faulty = new FaultyConnection(inner).DelayOnCommand(1, delay);

        await using DbCommand command = faulty.CreateCommand();
        command.CommandText = "SELECT 1";
        long start = Stopwatch.GetTimestamp();
        await command.ExecuteScalarAsync();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        await Assert.That(elapsed >= delay).IsTrue()
            .Because($"实际耗时 {elapsed.TotalMilliseconds:0}ms 应不小于注入的 {delay.TotalMilliseconds:0}ms");
    }

    [Test]
    public async Task CancelOnCommand_ThrowsOperationCanceled()
    {
        await using SqliteConnection inner = await OpenAsync();
        await using var faulty = new FaultyConnection(inner).CancelOnCommand(1);

        await using DbCommand command = faulty.CreateCommand();
        command.CommandText = "SELECT 1";
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await command.ExecuteScalarAsync());
    }

    [Test]
    public async Task ReaderFaultAtSecondRow_ThrowsOnThatRead()
    {
        await using SqliteConnection inner = await OpenAsync();
        await using (DbCommand seed = inner.CreateCommand())
        {
            seed.CommandText = "INSERT INTO probe(name) VALUES ('a'), ('b'), ('c')";
            await seed.ExecuteNonQueryAsync();
        }

        var boom = new IOException("注入：结果集读到一半断开");
        await using var faulty = new FaultyConnection(inner).FailReaderAtRow(commandOrdinal: 1, row: 2, boom);

        await using DbCommand command = faulty.CreateCommand();
        command.CommandText = "SELECT name FROM probe ORDER BY id";
        await using DbDataReader reader = await command.ExecuteReaderAsync();

        await Assert.That(await reader.ReadAsync()).IsTrue();   // 第一行正常
        IOException? thrown = await Assert.ThrowsAsync<IOException>(
            async () => await reader.ReadAsync());              // 第二行注入失败
        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).IsEqualTo(boom.Message);
    }
}
