using System.Collections;
using System.Reflection;
using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>A1（2026-10-12 审读修复）：并行读作用域内，失败的读操作不得把池连接归还到空闲栈。
/// <para>失败路径下连接状态不可信（静默断开时 <c>State</c> 仍为 Open），归还即让同作用域后续读复用死连接。
/// 语义：仅成功完成的读才归还；失败路径丢弃池连接，后续读从池外新建。</para></summary>
public sealed class ParallelReadFailureDiscardTests
{
    private static async Task<DataSession<SqliteProvider>> CreateSessionWithRowAsync()
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(new DbOptions
        {
            ConnectionString = $"Data Source=prfd_{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
        });
        await session.ExecuteAsync($"CREATE TABLE gkcr_items (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)");
        await session.InsertAsync(new GkcrItem { Id = 1, Name = "row-1" });
        return session;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3011:AccessibilityBypass",
        Justification = "池登记数是私有实现细节，无公开观测面；这里只读取计数，不写入也不改变任何状态。")]
    private static int PooledCount(DataSession<SqliteProvider> session)
    {
        FieldInfo field = typeof(DataSession<SqliteProvider>)
            .GetField("_parallelReadConnections", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pool = (ICollection?)field.GetValue(session);
        return pool?.Count ?? 0;
    }

    [Test]
    public async Task GetAsync_FailureInsideScope_DiscardsPooledConnection()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionWithRowAsync();
        await using (session.ForParallelReads())
        {
            _ = await session.GetAsync<GkcrItem>(1L);
            await Assert.That(PooledCount(session)).IsEqualTo(1);

            // 非法键类型：绑定阶段确定性失败（非瞬时，不重试）
            await Assert.ThrowsAsync<Exception>(async () => await session.GetAsync<GkcrItem>("not-a-key"));
            await Assert.That(PooledCount(session)).IsEqualTo(0);
        }
    }

    [Test]
    public async Task QueryAsync_FailureInsideScope_DiscardsPooledConnection()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionWithRowAsync();
        await using (session.ForParallelReads())
        {
            _ = await session.GetAsync<GkcrItem>(1L);
            await Assert.That(PooledCount(session)).IsEqualTo(1);

            await Assert.ThrowsAsync<Exception>(async () =>
                await session.QueryAsync<GkcrItem>($"SELECT nope FROM gkcr_items"));
            await Assert.That(PooledCount(session)).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetAsync_SuccessInsideScope_ReturnsConnectionToPool()
    {
        await using DataSession<SqliteProvider> session = await CreateSessionWithRowAsync();
        await using (session.ForParallelReads())
        {
            _ = await session.GetAsync<GkcrItem>(1L);
            _ = await session.GetAsync<GkcrItem>(1L);
            // 成功路径保持池复用：同一条连接被归还并再次取用，池登记数仍为 1
            await Assert.That(PooledCount(session)).IsEqualTo(1);
        }
    }

    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Bugprone", "S1751",
        Justification = "「循环内 break」是有意复刻 First/Single 族的早终止消费形态——只消费首行即退出。")]
    public async Task QueryAsyncEnumerable_EarlyBreakInsideScope_ReturnsConnectionToPool()
    {
        // 早终止消费（First/Single 族形态）不是失败：池连接照常归还，不得按失败丢弃
        // （远程方言上每次点查重建连接是真实回归）。
        await using DataSession<SqliteProvider> session = await CreateSessionWithRowAsync();
        await session.ExecuteAsync($"INSERT INTO gkcr_items (Id, Name) VALUES (2, 'row-2')");
        await using (session.ForParallelReads())
        {
            await foreach (GkcrItem _ in session.QueryAsyncEnumerable<GkcrItem>($"SELECT Id, Name FROM gkcr_items"))
                break;
            await Assert.That(PooledCount(session)).IsEqualTo(1);
        }
    }
}
