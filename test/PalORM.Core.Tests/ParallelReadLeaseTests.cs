using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>ARCH-001（2026-09-23）：并行读租约与作用域。
/// <para><b>语义</b>：作用域内只读操作允许并发（各自持独立连接，从作用域局部池取、用完归还）；
/// 写/事务/Bulk 仍走单活动门禁——遇在飞只读租约即拒绝（"只读并行、写与事务串行"的非对称语义）。
/// 作用域外与既有门禁逐位一致。</para>
/// <para>状态级断言全部确定性（无时序依赖）：并发租约获准、独占被拒、上限生效、作用域外回退既有语义。</para></summary>
internal sealed class ParallelReadLeaseTests
{
    [Test]
    public async Task ParallelReadScope_AdmitsConcurrentReadLeases_AndRejectsExclusive()
    {
        var state = new SessionOperationState();
        state.EnterParallelReadScope();

        using SessionOperationState.SessionOperationLease first = state.EnterReadOnly();
        using SessionOperationState.SessionOperationLease second = state.EnterReadOnly();

        // 两个只读租约同时持有 ✓；此时独占操作（写/事务/Bulk）必须被拒绝
        Exception? thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = state.Enter();
            return Task.CompletedTask;
        });
        await Assert.That(thrown!.Message).Contains("active database operation");

        // 只读租约释放后，独占操作恢复可用
        await first.DisposeAsync();
        await second.DisposeAsync();
        using SessionOperationState.SessionOperationLease exclusive = state.Enter();
        state.ExitParallelReadScope();
    }

    [Test]
    public async Task ParallelReadScope_EnforcesLeaseLimit()
    {
        var state = new SessionOperationState();
        state.EnterParallelReadScope();

        var leases = new List<SessionOperationState.SessionOperationLease>();
        for (int i = 0; i < SessionOperationState.MaxParallelReads; i++)
            leases.Add(state.EnterReadOnly());

        Exception? thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = state.EnterReadOnly();
            return Task.CompletedTask;
        });
        await Assert.That(thrown!.Message).Contains("Parallel read lease limit");

        foreach (SessionOperationState.SessionOperationLease lease in leases)
            await lease.DisposeAsync();
        state.ExitParallelReadScope();
    }

    [Test]
    public async Task OutsideScope_ReadOnlyLeaseFallsBackToExclusiveSemantics()
    {
        // 无作用域时 EnterReadOnly 与既有单活动门禁逐位一致（第二次进入被拒绝）
        var state = new SessionOperationState();
        using SessionOperationState.SessionOperationLease first = state.EnterReadOnly();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = state.EnterReadOnly();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task ForParallelReads_ConcurrentQueriesOnOneSession_Succeed()
    {
        // 端到端烟测：作用域内并发只读查询各自取得独立连接（未配置读路由时回落主连接串）
        await using DataSession<SqliteProvider> session = await DataSession<SqliteProvider>.CreateAsync(
            new DbOptions { ConnectionString = $"Data Source=par_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" });
        await session.ExecuteAsync($"CREATE TABLE parallel_rows (Id INTEGER PRIMARY KEY, v TEXT NOT NULL)");
        await session.ExecuteAsync($"INSERT INTO parallel_rows (Id, v) VALUES (1, 'a')");

        List<ParallelRow>[] results;
        await using (session.ForParallelReads())
        {
            Task<List<ParallelRow>> first = session.From<ParallelRow>().ToListAsync().AsTask();
            Task<List<ParallelRow>> second = session.From<ParallelRow>().ToListAsync().AsTask();
            Task<List<ParallelRow>> third = session.From<ParallelRow>().ToListAsync().AsTask();
            results = await Task.WhenAll(first, second, third);
        }

        foreach (List<ParallelRow> rows in results)
        {
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].V).IsEqualTo("a");
        }
        // 作用域退出后的门禁回退由 OutsideScope_ReadOnlyLeaseFallsBackToExclusiveSemantics 确定性覆盖
        // （此处不做重叠断言：SQLite 本地查询太快，无法稳定制造在飞操作）。
    }
}

[Table("parallel_rows")]
internal sealed partial class ParallelRow
{
    [Key] public long Id { get; set; }
    [Column("v")] public string V { get; set; } = "";
}
