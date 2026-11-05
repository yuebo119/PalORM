using System.Data;
using System.Data.Common;
using MySqlConnector;
using PalORM.MySql;

namespace PalORM.Integration.Tests;

/// <summary>MySQL 批量写入的"包装事务"守卫（ITM-740/778，2026-11-05 补覆盖）。
/// <para><b>为什么需要它</b>：<c>MySqlProvider.BulkInsertAsync</c> 的能力探测要把事务挂接到命令上，
/// 非 <c>MySqlTransaction</c> 的包装/装饰事务会让探测抛异常并被吞掉 → 静默降级多值路径
/// （能力检测变成"永远 OFF"）。守卫必须在探测之前把它变成显式失败。
/// 顺带锁定守卫与空列表短路的先后：原顺序下"空列表 + 包装事务"静默返 0、非空则抛，
/// 同参数形态结果不对称。</para>
/// <para><b>不触库</b>：守卫在建立/打开连接之前就抛，用例只需一个未打开的 <c>MySqlConnection</c>
/// 实例，因此不挂 ExternalDatabase 分类，也不受数据库可用性影响。</para></summary>
public sealed class MySqlBulkTransactionGuardTests
{
    private const string UnusedConnectionString =
        "Server=127.0.0.1;Port=1;Database=palorm_none;User=root;ConnectionTimeout=1";

    [Test]
    public async Task WrappedTransaction_OnMySqlConnection_IsRejectedBeforeProbingTheServer()
    {
        await using var connection = new MySqlConnection(UnusedConnectionString);
        await using var wrapped = new WrappedTransaction(connection);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            MySqlProvider.BulkInsertAsync(
                connection, wrapped, [new GuardRow { Name = "x" }],
                batchSize: 100, commandTimeoutSeconds: 30, CancellationToken.None));

        await Assert.That(ex!.Message).Contains("MySqlTransaction");
        await Assert.That(ex.Message).Contains("Wrapped/decorated");
    }

    /// <summary>守卫置于空列表短路之前——否则同一非法入参在"有行/无行"两种形态下行为不一致
    /// （ITM-637 契约校验先于短路口径）。</summary>
    [Test]
    public async Task WrappedTransaction_WithEmptyList_StillThrows_InsteadOfReturningZero()
    {
        await using var connection = new MySqlConnection(UnusedConnectionString);
        await using var wrapped = new WrappedTransaction(connection);

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            MySqlProvider.BulkInsertAsync(
                connection, wrapped, Array.Empty<GuardRow>(),
                batchSize: 100, commandTimeoutSeconds: 30, CancellationToken.None));
    }

    /// <summary>装饰器形态的事务替身：除 <see cref="IsolationLevel"/> 外全部不实现——
    /// 用例只走到守卫的拒绝分支，永远不会 Commit/Rollback。</summary>
    private sealed class WrappedTransaction(DbConnection connection) : DbTransaction
    {
        protected override DbConnection DbConnection => connection;

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        public override void Commit() => throw new NotSupportedException(
            "Test double: the guard must reject this transaction before any commit.");

        public override void Rollback() => throw new NotSupportedException(
            "Test double: the guard must reject this transaction before any rollback.");
    }
}
