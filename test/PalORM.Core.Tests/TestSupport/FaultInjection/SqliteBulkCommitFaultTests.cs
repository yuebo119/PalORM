using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>A3（2026-11-24）：SQLite 批量骨架（RowPreparedBulkInsert）此前调用
/// <b>无方言版</b>的「提交失败跳过回滚」裁决——SQLITE_BUSY 的 COMMIT 失败会被误判为
/// 「服务端已终止事务」，跳过回滚使事务悬置、写锁持有到连接释放。带方言版对 SQLite
/// 明确 <c>return false</c>（失败 COMMIT 保留活动事务，必须回滚）。
/// <para><b>为什么不用包装事务注入</b>：Microsoft.Data.Sqlite 的 <c>SqliteCommand.DbTransaction</c>
/// setter 要求真实 <c>SqliteTransaction</c>，包装层会抛 InvalidCastException（实测），
/// 故改在裁决函数这一层锁定语义：SQLite 输入恒 false，其余方言按异常形态判定。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class SqliteBulkCommitFaultTests
{
    [Test]
    public async Task TrySkipRollback_SqliteDialect_NeverSkips()
    {
        // 形态覆盖「看起来像服务端错误」的驱动异常：SQLite 的 COMMIT 失败（BUSY/LOCKED）
        // 不终止服务端事务，必须回滚。
        var busy = new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
        await Assert.That(TransactionCleanup.TrySkipRollbackAfterCommitFailure(SqlDialect.Sqlite, busy))
            .IsFalse()
            .Because("SQLite 失败 COMMIT 保留活动事务；跳过回滚会让写锁持有到连接释放");
        await Assert.That(busy.Data.Contains(TransactionCleanup.RollbackSkippedDataKey)).IsFalse();
    }

    [Test]
    public async Task TrySkipRollback_PostgreSqlServerError_Skips()
    {
        // 对照臂：PG 的服务端错误确实已终止事务，跳过回滚（既有语义不变）
        var serverError = new Npgsql.PostgresException("commit failed", "ERROR", "ERROR", "40000");
        await Assert.That(TransactionCleanup.TrySkipRollbackAfterCommitFailure(SqlDialect.PostgreSql, serverError))
            .IsTrue();
    }

    [Test]
    public async Task TrySkipRollback_PostgreSqlTransportFailure_RollsBack()
    {
        // 对照臂：传输类失败时服务端状态未知，必须尝试回滚
        var transport = new System.IO.IOException("connection reset");
        await Assert.That(TransactionCleanup.TrySkipRollbackAfterCommitFailure(SqlDialect.PostgreSql, transport))
            .IsFalse();
    }
}
