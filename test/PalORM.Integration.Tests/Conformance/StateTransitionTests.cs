using PalORM.Sqlite;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>C 层状态机转移表（方案 §6.2）。
/// <para>状态取可观察事实的组合，事件取公开 API 调用。80 格（10 状态 × 8 事件）每格要么指向一个
/// 已实现的断言方法，要么写明不适用的理由；<see cref="TransitionTable_IsFullyClassified"/> 用反射
/// 核对断言方法真实存在，因此"有断言或显式不适用"是机械可查的。</para></summary>
public sealed class StateTransitionTests
{
    internal sealed record Cell(string State, string Event, string? Assertion, string? NotApplicable);

    internal static IReadOnlyList<Cell> Cells { get; } =
    [
        // ── 未租出 ──
        new("未租出", "租出", nameof(Rent_FromIdle_ReturnsSession), null),
        new("未租出", "查询", null, "无会话可执行查询"),
        new("未租出", "开启事务", null, "无会话可开启事务"),
        new("未租出", "提交", null, "无事务可提交"),
        new("未租出", "回滚", null, "无事务可回滚"),
        new("未租出", "取消", null, "无在飞操作可取消"),
        new("未租出", "异常", null, "无在飞操作可异常"),
        new("未租出", "归还", null, "未租出即未持有，归还无意义"),

        // ── 租出空闲 ──
        new("租出空闲", "租出", null, "同一会话不可重复租出，租出只从池发起"),
        new("租出空闲", "查询", nameof(Query_OnIdleSession_StaysUsable), null),
        new("租出空闲", "开启事务", nameof(BeginTransaction_FromIdle_EntersTransaction), null),
        new("租出空闲", "提交", null, "无事务可提交"),
        new("租出空闲", "回滚", null, "无事务可回滚"),
        new("租出空闲", "取消", nameof(Cancel_OnIdleSession_LeavesSessionUsable), null),
        new("租出空闲", "异常", nameof(Exception_OnIdleSession_KeepsSessionUsable), null),
        new("租出空闲", "归还", nameof(Return_FromIdle_ReturnsToPool), null),

        // ── 租出带瞬态 ──
        new("租出带瞬态", "租出", null, "同租出空闲，租出只从池发起"),
        new("租出带瞬态", "查询", nameof(Query_OnTransientSession_HonorsTransientState), null),
        new("租出带瞬态", "开启事务", null, "瞬态与事务可共存，组合形态由 I1 的复位断言覆盖，不在此格重复"),
        new("租出带瞬态", "提交", null, "无事务可提交"),
        new("租出带瞬态", "回滚", null, "无事务可回滚"),
        new("租出带瞬态", "取消", null, "取消语义与瞬态无关，由租出空闲行的取消格覆盖"),
        new("租出带瞬态", "异常", null, "异常语义与瞬态无关，由租出空闲行的异常格覆盖"),
        new("租出带瞬态", "归还", nameof(Return_FromTransient_ResetsThenPooled), null),

        // ── 事务中 ──
        new("事务中", "租出", null, "会话已在租期内"),
        new("事务中", "查询", nameof(Query_InTransaction_SeesOwnWrites), null),
        new("事务中", "开启事务", nameof(BeginTransaction_WhileActive_IsRejectedOrAllowed), null),
        new("事务中", "提交", nameof(Commit_FromTransaction_BecomesCommitted), null),
        new("事务中", "回滚", nameof(Rollback_FromTransaction_BecomesRolledBack), null),
        new("事务中", "取消", nameof(Cancel_InTransaction_LeavesSessionPredictable), null),
        new("事务中", "异常", null, "异常后的可见性由回滚格覆盖（未提交的事务在异常路径上不可见）"),
        new("事务中", "归还", null, "带活动事务归还需池级契约，当前实现未暴露该形态，见文档 §6.2 说明"),

        // ── 已提交 / 已回滚 ──
        new("已提交", "租出", null, "会话已在租期内"),
        new("已提交", "查询", nameof(Query_AfterCommit_SeesPersistedWrites), null),
        new("已提交", "开启事务", nameof(BeginTransaction_AfterCommit_StartsNewTransaction), null),
        new("已提交", "提交", null, "无活动事务"),
        new("已提交", "回滚", null, "无活动事务"),
        new("已提交", "取消", null, "无在飞操作"),
        new("已提交", "异常", null, "无在飞操作"),
        new("已提交", "归还", nameof(Return_AfterCommit_ReturnsToPool), null),

        new("已回滚", "租出", null, "会话已在租期内"),
        new("已回滚", "查询", nameof(Query_AfterRollback_DiscardsWrites), null),
        new("已回滚", "开启事务", null, "与已提交行的开启事务同形态，由该格覆盖"),
        new("已回滚", "提交", null, "无活动事务"),
        new("已回滚", "回滚", null, "无活动事务"),
        new("已回滚", "取消", null, "无在飞操作"),
        new("已回滚", "异常", null, "无在飞操作"),
        new("已回滚", "归还", null, "与已提交行的归还同形态，由该格覆盖"),

        // ── 已取消 ──
        new("已取消", "租出", null, "会话已在租期内"),
        new("已取消", "查询", nameof(Query_AfterCancel_IsRejectedOrAllowed), null),
        new("已取消", "开启事务", null, "取消后的会话状态按文档取值，本方案取「会话可继续」一侧"),
        new("已取消", "提交", null, "无有效事务"),
        new("已取消", "回滚", null, "无有效事务"),
        new("已取消", "取消", null, "已取消状态重复取消无额外语义"),
        new("已取消", "异常", null, "无在飞操作"),
        new("已取消", "归还", null, "取消后归由 I8 的 IdleCount 断言覆盖"),

        // ── 故障 ──
        new("故障", "租出", null, "会话已在租期内"),
        new("故障", "查询", nameof(Query_AfterFault_IsRejectedOrAllowed), null),
        new("故障", "开启事务", null, "故障态下开启事务无意义"),
        new("故障", "提交", null, "无有效事务"),
        new("故障", "回滚", null, "无有效事务"),
        new("故障", "取消", null, "无在飞操作"),
        new("故障", "异常", null, "已在故障态，重复异常无额外语义"),
        new("故障", "归还", null, "故障归还需池级重置，当前实现未暴露该形态，见文档 §6.2 说明"),

        // ── 熔断打开 / 半开 ──
        new("熔断打开", "租出", null, "熔断是会话级弹性态，与池租出无关"),
        new("熔断打开", "查询", null, "实测（2026-10-29）：弹性执行器只对瞬时错误计数，SQL 错误不驱动熔断，该状态在当前实现下不可达（见 I10 与文档 §8.1）"),
        new("熔断打开", "开启事务", null, "同上，状态不可达"),
        new("熔断打开", "提交", null, "同上，状态不可达"),
        new("熔断打开", "回滚", null, "同上，状态不可达"),
        new("熔断打开", "取消", null, "同上，状态不可达"),
        new("熔断打开", "异常", null, "同上，状态不可达"),
        new("熔断打开", "归还", null, "同上，状态不可达"),

        new("熔断半开", "租出", null, "同熔断打开行，状态不可达"),
        new("熔断半开", "查询", null, "同熔断打开行，状态不可达"),
        new("熔断半开", "开启事务", null, "同熔断打开行，状态不可达"),
        new("熔断半开", "提交", null, "同熔断打开行，状态不可达"),
        new("熔断半开", "回滚", null, "同熔断打开行，状态不可达"),
        new("熔断半开", "取消", null, "同熔断打开行，状态不可达"),
        new("熔断半开", "异常", null, "同熔断打开行，状态不可达"),
        new("熔断半开", "归还", null, "同熔断打开行，状态不可达"),
    ];

    [Test]
    public async Task TransitionTable_IsFullyClassified()
    {
        List<string> problems = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Cell cell in Cells)
        {
            string key = $"{cell.State}/{cell.Event}";
            if (!seen.Add(key)) problems.Add($"{key}: 重复格");

            bool hasAssertion = !string.IsNullOrWhiteSpace(cell.Assertion);
            bool hasReason = !string.IsNullOrWhiteSpace(cell.NotApplicable);
            if (hasAssertion == hasReason)
                problems.Add($"{key}: 必须恰好有一种处置（有断言 或 有理由）");

            if (hasAssertion && GetType().GetMethod(cell.Assertion!) is null)
                problems.Add($"{key}: 断言方法 {cell.Assertion} 不存在");
        }

        if (Cells.Count != 80)
            problems.Add($"格数为 {Cells.Count}，10 状态 × 8 事件应为 80");

        int asserted = Cells.Count(c => !string.IsNullOrWhiteSpace(c.Assertion));
        await Assert.That(string.Join(" | ", problems)).IsEmpty();
        await Assert.That(asserted).IsGreaterThanOrEqualTo(15);
    }

    // ── 断言方法（被转移表按名字引用）─────────────────

    [Test]
    public async Task Rent_FromIdle_ReturnsSession()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_rent");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);
        await Assert.That(pool.IdleCount).IsEqualTo(0);
        DataSession<SqliteProvider> session = await pool.RentAsync();
        await Assert.That(session).IsNotNull();
        await session.DisposeAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task Query_OnIdleSession_StaysUsable()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_query_idle");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
    }

    [Test]
    public async Task BeginTransaction_FromIdle_EntersTransaction()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_begin");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
        // 行为断言（不用 IsNotNull）：事务内写入对当前会话可见，回滚后不留下行。
        // 这条同时验证 ITM-649 的事务登记——写入若未加入该事务，回滚后行仍在。
        await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "tx", Amount = 1 });
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
        await transaction.RollbackAsync();
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
    }

    [Test]
    public async Task Cancel_OnIdleSession_LeavesSessionUsable()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_cancel_idle");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        try
        {
            await db.CountAsync<CfBulkPlainEntity>(ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 预期形态
        }
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>(ct: CancellationToken.None)).IsEqualTo(0L);
    }

    [Test]
    public async Task Exception_OnIdleSession_KeepsSessionUsable()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_error_idle");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await Assert.That(async () => await db.ExecuteAsync($"SELECT * FROM no_such_table"))
            .Throws<Exception>();
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
    }

    [Test]
    public async Task Return_FromIdle_ReturnsToPool()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_return_idle");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);
        DataSession<SqliteProvider> session = await pool.RentAsync();
        await session.DisposeAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task Query_OnTransientSession_HonorsTransientState()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_transient_query");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await db.InsertAsync(new CfSoftDeleteEntity { Name = "dead", DeletedAt = "2026-01-01" });
        await Assert.That(await db.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(0L);
        db.IgnoreFilters();
        await Assert.That(await db.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(1L);
    }

    [Test]
    public async Task Return_FromTransient_ResetsThenPooled()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_transient_return");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);
        await using (DataSession<SqliteProvider> seed = await shared.OpenSessionAsync())
        {
            await seed.InsertAsync(new CfSoftDeleteEntity { Name = "dead", DeletedAt = "2026-01-01" });
        }

        DataSession<SqliteProvider> first = await pool.RentAsync();
        first.IgnoreFilters();
        await Assert.That(await first.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(1L);
        await first.DisposeAsync();

        DataSession<SqliteProvider> second = await pool.RentAsync();
        await Assert.That(await second.CountAsync<CfSoftDeleteEntity>()).IsEqualTo(0L);
        await second.DisposeAsync();
    }

    [Test]
    public async Task Query_InTransaction_SeesOwnWrites()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_tx_query");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
        await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "in-tx" });
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
        await transaction.RollbackAsync();
    }

    [Test]
    public async Task BeginTransaction_WhileActive_IsRejectedOrAllowed()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_nested_tx");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using System.Data.Common.DbTransaction first = await db.BeginTransactionAsync();

        // 按文档取一侧：嵌套开启要么被拒绝，要么返回独立事务；两者都不允许静默复用
        try
        {
            using System.Data.Common.DbTransaction second = await db.BeginTransactionAsync();
            await Assert.That(ReferenceEquals(first, second)).IsFalse();
            await second.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // 拒绝嵌套亦为合规形态
        }
        await first.RollbackAsync();
    }

    [Test]
    public async Task Commit_FromTransaction_BecomesCommitted()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_commit");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
        await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "committed" });
        await transaction.CommitAsync();
        await using DataSession<SqliteProvider> fresh = await shared.OpenSessionAsync();
        await Assert.That(await fresh.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
    }

    [Test]
    public async Task Rollback_FromTransaction_BecomesRolledBack()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_rollback");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
        await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "rolled-back" });
        await transaction.RollbackAsync();
        await using DataSession<SqliteProvider> fresh = await shared.OpenSessionAsync();
        await Assert.That(await fresh.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
    }

    [Test]
    public async Task Cancel_InTransaction_LeavesSessionPredictable()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_cancel_tx");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        try
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "cancelled" }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 预期形态
        }
        try
        {
            await transaction.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // 取消可能已使事务失效
        }
        await transaction.DisposeAsync();

        await using DataSession<SqliteProvider> fresh = await shared.OpenSessionAsync();
        await Assert.That(await fresh.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
    }

    [Test]
    public async Task Query_AfterCommit_SeesPersistedWrites()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_after_commit");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using (System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync())
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 1, Name = "persisted" });
            await transaction.CommitAsync();
        }
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(1L);
    }

    [Test]
    public async Task BeginTransaction_AfterCommit_StartsNewTransaction()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_tx_again");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using (System.Data.Common.DbTransaction first = await db.BeginTransactionAsync())
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 1 });
            await first.CommitAsync();
        }
        using (System.Data.Common.DbTransaction second = await db.BeginTransactionAsync())
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 2 });
            await second.CommitAsync();
        }
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(2L);
    }

    [Test]
    public async Task Return_AfterCommit_ReturnsToPool()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_return_commit");
        await using DataSessionPool<SqliteProvider> pool = shared.CreatePool(maxIdleSize: 1);
        DataSession<SqliteProvider> session = await pool.RentAsync();
        using (System.Data.Common.DbTransaction transaction = await session.BeginTransactionAsync())
        {
            await transaction.CommitAsync();
        }
        await session.DisposeAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task Query_AfterRollback_DiscardsWrites()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_after_rollback");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using (System.Data.Common.DbTransaction transaction = await db.BeginTransactionAsync())
        {
            await db.InsertAsync(new CfBulkPlainEntity { Id = 1 });
            await transaction.RollbackAsync();
        }
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>()).IsEqualTo(0L);
    }

    [Test]
    public async Task Query_AfterCancel_IsRejectedOrAllowed()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_after_cancel");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        try
        {
            await db.CountAsync<CfBulkPlainEntity>(ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 预期形态
        }
        await Assert.That(await db.CountAsync<CfBulkPlainEntity>(ct: CancellationToken.None)).IsEqualTo(0L);
    }

    [Test]
    public async Task Query_AfterFault_IsRejectedOrAllowed()
    {
        await using SharedSqliteDb shared = await SharedSqliteDb.CreateAsync("state_after_fault");
        await using DataSession<SqliteProvider> db = await shared.OpenSessionAsync();
        await Assert.That(async () => await db.ExecuteAsync($"SELECT * FROM no_such_table"))
            .Throws<Exception>();

        // 按文档取一侧：会话要么明确拒绝，要么可继续；不允许静默返回错误数据
        long count = await db.CountAsync<CfBulkPlainEntity>();
        await Assert.That(count).IsEqualTo(0L);
    }
}