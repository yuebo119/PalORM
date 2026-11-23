using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>LINQ 栈连接判死锁定（FLT-5，2026-11-23）。
/// <para><b>背景</b>：LINQ 栈（<c>From&lt;T&gt;()</c> 管线）用裸连接建命令
/// （<c>connection.CreateCommand()</c>），既不经过会话的 <c>CreateCommand</c>（判死重建的挂接点），
/// 其执行路径也没有任何 <c>MarkConnectionPoisoned</c> 调用点——两条路都断，外部 kill 连接后
/// 同一会话的 LINQ 查询持续失败，而直查 API（<c>GetAllAsync</c> 等）下一次调用即自愈。</para>
/// <para><b>夹具口径</b>：故障只在命令序号 1 上注入一次，包装层不真的断连——若产品重建了连接，
/// 第二条命令落到新连接、包装层计数不增长；未重建则仍走包装层旧连接、计数增长。两臂判定因此
/// 只依赖「重建是否发生」。注入异常取 SQLite 的真实判死消息（<c>SqliteProvider.IsConnectionFailure</c>
/// 的匹配片段），且不是 <c>SqliteException</c> 的 5/6 瞬时码，弹性层不会重试——
/// 单次执行即抛出，故障路径确定。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class LinqConnectionFailureTests
{
    private static InvalidOperationException Poisoned()
        => new("ExecuteNonQuery can only be called when the connection is open.");

    /// <summary>读路径：<c>ToListAsync</c> 首次判死 → 下一次查询自动重建并成功。</summary>
    [Test]
    public async Task LinqToList_ConnectionPoisoning_RebuildsAndRecovers()
    {
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnceOnCommand(1, Poisoned()));
        await using DataSession<SqliteProvider> session = created.Session;

        // 数据经 keeper 直写：不占用包装层的命令序号（序号口径同 ConnectionReuseFaultTests）
        await using (var seed = created.Keeper.CreateCommand())
        {
            seed.CommandText = "INSERT INTO fault_row (Name, Qty) VALUES ('seed', 1)";
            await seed.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.From<FaultRow>().ToListAsync());

        long commandsAfterFailure = created.Wrapper.CommandsCreated;
        List<FaultRow> rows = await session.From<FaultRow>().ToListAsync();

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(created.Wrapper.CommandsCreated).IsEqualTo(commandsAfterFailure)
            .Because("判死标记应触发重建，重建出的新连接不经包装层；"
                + "未置位则第二次查询仍用包装层旧连接，计数会增长");
    }

    /// <summary>写路径：<c>ExecuteDeleteAsync</c> 首次判死 → 下一次写自动重建并成功。</summary>
    [Test]
    public async Task LinqExecuteDelete_ConnectionPoisoning_RebuildsAndRecovers()
    {
        var created = await FaultInjectionFixtures.CreateAsync(
            keeper => new FaultyConnection(keeper).FailOnceOnCommand(1, Poisoned()));
        await using DataSession<SqliteProvider> session = created.Session;

        await using (var seed = created.Keeper.CreateCommand())
        {
            seed.CommandText = "INSERT INTO fault_row (Name, Qty) VALUES ('victim', 7)";
            await seed.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.From<FaultRow>().Where($"Qty = {7}").ExecuteDeleteAsync());

        long commandsAfterFailure = created.Wrapper.CommandsCreated;
        int deleted = await session.From<FaultRow>().Where($"Qty = {7}").ExecuteDeleteAsync();

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(created.Wrapper.CommandsCreated).IsEqualTo(commandsAfterFailure)
            .Because("写路径判死同样要触发重建；未置位则第二次写仍用包装层旧连接，计数会增长");
    }
}
