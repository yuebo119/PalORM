using Microsoft.Data.Sqlite;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>A1（2026-09-20）：事务前置校验的行为锁定。
/// <para><b>背景</b>：<c>pg_advisory_xact_lock</c> 是事务级锁，事务外调用会获得锁但立即释放，
/// 方法却正常返回——调用方以为临界区已持锁，跨进程互斥形同虚设且无任何错误信号。
/// 这类缺陷只在并发竞态导致数据损坏时才暴露，因此在库内显式失败。</para>
/// <para><b>为什么用 SQLite 会话测</b>：校验逻辑是「会话是否有活动事务」，与方言无关；
/// 真库档在 Integration.Tests（需 PG）。</para></summary>
public sealed class TransactionGuardTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionAsync()
    {
        DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=guard_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        return session;
    }

    [Test]
    public async Task IsInTransaction_FalseWithoutTransaction()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await Assert.That(session.IsInTransaction).IsFalse();
    }

    [Test]
    public async Task IsInTransaction_TrueInsideTransaction()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await using var tran = await session.BeginTransactionAsync();
        await Assert.That(session.IsInTransaction).IsTrue();
        await tran.CommitAsync();
        await Assert.That(session.IsInTransaction).IsFalse();
    }

    [Test]
    public async Task IsInTransaction_TrueWithExternalTransaction()
    {
        // UseTransaction 契约：事务必须属于会话自身的主连接（逃生舱 GetRawConnection）
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await using var tran = await session.GetRawConnection().BeginTransactionAsync();

        session.UseTransaction(tran);
        await Assert.That(session.IsInTransaction).IsTrue();

        session.UseTransaction(null);
        await Assert.That(session.IsInTransaction).IsFalse();
    }

    [Test]
    public async Task IsInTransaction_FalseAfterRollback()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await using var tran = await session.BeginTransactionAsync();
        await Assert.That(session.IsInTransaction).IsTrue();
        await tran.RollbackAsync();
        await Assert.That(session.IsInTransaction).IsFalse();
    }

    [Test]
    public async Task IsInTransaction_InsideWithTransaction()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        bool? observed = null;
        await session.WithTransaction(_ =>
        {
            observed = session.IsInTransaction;
            return Task.CompletedTask;
        });
        await Assert.That(observed).IsTrue();
        await Assert.That(session.IsInTransaction).IsFalse();
    }

    [Test]
    public async Task IsInTransaction_FalseAfterExternalTransactionDisposed()
    {
        // API-002（2026-09-22）：外部设入的事务被外部 Dispose 后，执行路径继续响亮失败
        // （ITM-640/767，另由 SessionConcurrencyTests 锁定），但本属性是事实查询，必须返回
        // false——原实现直接调 GetActiveTransaction，与本属性 XML doc 的承诺矛盾（文档说 false，
        // 实现抛异常），并让 EnsureInTransaction 之类的自查拿到无关的 ITM-640 异常。
        await using DataSession<SqliteProvider> session = await CreateSessionAsync();
        await using var tran = await session.GetRawConnection().BeginTransactionAsync();
        session.UseTransaction(tran);

        await tran.DisposeAsync();

        await Assert.That(session.IsInTransaction).IsFalse();
    }
}
