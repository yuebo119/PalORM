using System.Data;
using System.Data.Common;

namespace PalORM.Core.Tests;

/// <summary>T1/R1/R3（2026-09-20）：失败提交后的回滚裁决与有界回滚。
/// <para><b>背景</b>：原裁决是"方言非 SQLite 就跳过回滚"，依据只覆盖 PG/MySQL 文档的
/// "COMMIT 出错即服务端回滚"。但 COMMIT 因连接断开/取消/超时失败时，服务端最终状态未知，
/// 跳过回滚会让服务端事务悬置到连接归还，继续占锁与 undo 日志。裁决条件收紧为
/// "失败看起来是服务端错误"；同时回滚本身从无界等待改为有界（网络黑洞下不卡死调用方）。</para>
/// <para><b>为什么用假事务</b>：真实连接断开无法在单测里稳定制造，且裁决逻辑是纯异常形态
/// 分类；回滚的有界性用可编程延迟的假事务验证（延迟超过超时即应跳转而非无限等待）。</para></summary>
public sealed class TransactionCleanupTests
{
    // ─── R1：裁决只跳过"服务端已处理"的失败 ────────────────────

    [Test]
    public async Task ServerSideCommitError_OnPg_IsSkipped()
    {
        var primary = new InvalidOperationException("commit failed at server");
        bool skipped = TransactionCleanup.TrySkipRollbackAfterCommitFailure(
            SqlDialect.PostgreSql, primary);

        await Assert.That(skipped).IsTrue();
        await Assert.That(primary.Data.Contains(TransactionCleanup.RollbackSkippedDataKey)).IsTrue();
    }

    [Test]
    public async Task ServerSideCommitError_OnMySql_IsSkipped()
            => await Assert.That(TransactionCleanup.TrySkipRollbackAfterCommitFailure(
                SqlDialect.MySql, new InvalidOperationException("server error"))).IsTrue();

    [Test]
    [Arguments(typeof(OperationCanceledException))]
    [Arguments(typeof(TimeoutException))]
    [Arguments(typeof(System.IO.IOException))]
    [Arguments(typeof(System.Net.Sockets.SocketException))]
    public async Task TransportOrCancellationCommitFailure_OnPg_IsRolledBack(Type failureType)
    {
        // R1：连接类失败下服务端状态未知——必须尝试回滚而不是跳过
        Exception failure = (Exception)Activator.CreateInstance(failureType)!;
        var primary = new InvalidOperationException("commit failed", failure);

        bool skipped = TransactionCleanup.TrySkipRollbackAfterCommitFailure(
            SqlDialect.PostgreSql, primary);

        await Assert.That(skipped).IsFalse();
        await Assert.That(primary.Data.Contains(TransactionCleanup.RollbackSkippedDataKey)).IsFalse();
    }

    [Test]
    public async Task WrappedTransportFailure_InsideDriverException_IsRolledBack()
    {
        // 驱动常把底层 IO 错误包在数据库异常里（NpgsqlException { Inner = IOException }）——
        // 只看外层类型会把"连接断裂"误判为"服务端错误"
        var primary = new InvalidOperationException(
            "commit failed", new System.IO.IOException("connection reset"));

        await Assert.That(TransactionCleanup.TrySkipRollbackAfterCommitFailure(
            SqlDialect.PostgreSql, primary)).IsFalse();
    }

    [Test]
    public async Task Sqlite_AlwaysRollsBack()
    {
        // SQLite 的失败 COMMIT（如 SQLITE_BUSY）保留活动事务，必须回滚释放写锁——即使
        // 失败形态看起来是服务端错误也不跳过
        await Assert.That(TransactionCleanup.TrySkipRollbackAfterCommitFailure(
            SqlDialect.Sqlite, new InvalidOperationException("busy"))).IsFalse();
    }

    [Test]
    public async Task DialectFreeVariant_TransportFailure_IsRolledBack()
    {
        // 多值骨架不带方言信息，只按异常形态裁决
        var primary = new OperationCanceledException();
        await Assert.That(TransactionCleanup.TrySkipRollbackAfterCommitFailureForException(primary))
            .IsFalse();
    }

    [Test]
    public async Task DialectFreeVariant_ServerError_IsSkipped()
    {
        var primary = new InvalidOperationException("deadlock detected");
        await Assert.That(TransactionCleanup.TrySkipRollbackAfterCommitFailureForException(primary))
            .IsTrue();
    }

    // ─── R3：回滚有界 ────────────────────────────────

    [Test]
    public async Task Rollback_Bounded_RecordsTimeoutInsteadOfHanging()
    {
        // 回滚永不返回时，超时上限必须把它变成主异常 Data 上的 TimeoutException，
        // 而不是让 finally 永久挂起
        var primary = new InvalidOperationException("original failure");
        await using var transaction = new SlowRollbackTransaction(delaySeconds: 30);

        await TransactionCleanup.RollbackPreservingAsync(transaction, primary, rollbackTimeoutSeconds: 1);

        await Assert.That(primary.Data.Contains("PalORM.RollbackTimeoutException")).IsTrue();
        await Assert.That(primary.Data.Contains("PalORM.RollbackException")).IsFalse();
    }

    [Test]
    public async Task Rollback_CompletesWithinBounds_NoTimeoutRecorded()
    {
        var primary = new InvalidOperationException("original failure");
        await using var transaction = new SlowRollbackTransaction(delaySeconds: 0);

        await TransactionCleanup.RollbackPreservingAsync(transaction, primary, rollbackTimeoutSeconds: 5);

        await Assert.That(primary.Data.Contains("PalORM.RollbackTimeoutException")).IsFalse();
        await Assert.That(primary.Data.Contains("PalORM.RollbackException")).IsFalse();
    }

    [Test]
    public async Task Rollback_Failure_IsPreservedOnPrimaryException()
    {
        var primary = new InvalidOperationException("original failure");
        await using var transaction = new FailingRollbackTransaction();

        await TransactionCleanup.RollbackPreservingAsync(transaction, primary, rollbackTimeoutSeconds: 5);

        await Assert.That(primary.Data["PalORM.RollbackException"]).IsTypeOf<InvalidOperationException>();
    }

    [Test]
    public async Task Rollback_ZeroTimeout_IsUnbounded()
    {
        // Zero = 调用方显式要求无限等待（CommandTimeout Zero 契约同口径）
        var primary = new InvalidOperationException("original failure");
        await using var transaction = new SlowRollbackTransaction(delaySeconds: 0);

        await TransactionCleanup.RollbackPreservingAsync(transaction, primary, rollbackTimeoutSeconds: 0);

        await Assert.That(primary.Data.Contains("PalORM.RollbackTimeoutException")).IsFalse();
    }
}

/// <summary>回滚延迟可编程的假事务——用于验证有界回滚（真实连接断开无法稳定制造）。</summary>
internal sealed class SlowRollbackTransaction(int delaySeconds) : DbTransaction
{
    private readonly SlowRollbackConnection _connection = new();
    public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
    protected override DbConnection DbConnection => _connection;

    public override void Commit() { }
    public override void Rollback() { }
    public override Task RollbackAsync(CancellationToken cancellationToken = default)
        => delaySeconds <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class SlowRollbackConnection : DbConnection
{
    private string _connectionString = "";

    // CS8765：基类 ConnectionString 的 setter 形参标注为非空；测试夹具显式接受 null 并归一
#pragma warning disable CS8765
    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? "";
    }
#pragma warning restore CS8765
    public override string Database => "";
    public override string DataSource => "";
    public override string ServerVersion => "";
    public override ConnectionState State => ConnectionState.Open;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }
    public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override Task CloseAsync() => Task.CompletedTask;
    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => throw new NotSupportedException();
}
/// <summary>回滚必定失败的假事务——验证清理失败只挂 Data、不替换主异常。</summary>
internal sealed class FailingRollbackTransaction : DbTransaction
{
    private readonly SlowRollbackConnection _connection = new();
    public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
    protected override DbConnection DbConnection => _connection;

    public override void Commit() { }
    public override void Rollback() => throw new InvalidOperationException("rollback failed");
    public override Task RollbackAsync(CancellationToken cancellationToken = default)
        => Task.FromException(new InvalidOperationException("rollback failed"));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
