using PalORM.Sqlite;

namespace PalORM.Core.Tests.TestSupport.FaultInjection;

/// <summary>FLT-5：池耗尽与归还。
/// <para>池的语义边界由三条守住：容量上限（多还的会话被真实释放，不占位）、
/// 池释放后租用被拒、已租出的会话在池释放后归还时不入队（真实释放）。
/// 每条都断言空闲计数回到基线——只断言「没抛异常」证明不了池计数没漂。</para></summary>
[Property("Category", "FaultInjection")]
public sealed class PoolFaultTests
{
    private static DbOptions Options(string name)
        => new() { ConnectionString = $"Data Source={name};Mode=Memory;Cache=Shared" };

    [Test]
    public async Task ReturningBeyondCapacity_DoesNotGrowIdleQueue()
    {
        string name = $"pool_cap_{Guid.NewGuid():N}";
        await using var pool = DataSessionPool<SqliteProvider>.Create(Options(name), maxIdleSize: 1);

        await using var first = await pool.RentAsync();
        await using var second = await pool.RentAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(0);

        await first.DisposeAsync();
        await Assert.That(pool.IdleCount).IsEqualTo(1);

        await second.DisposeAsync();   // 池满：这一位不被接收
        await Assert.That(pool.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task RentingAfterPoolDispose_IsRejected()
    {
        string name = $"pool_disposed_{Guid.NewGuid():N}";
        var pool = DataSessionPool<SqliteProvider>.Create(Options(name), maxIdleSize: 1);
        await pool.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pool.RentAsync());
        await Assert.That(pool.IdleCount).IsEqualTo(0);
    }

    [Test]
    public async Task ReturnedSessionAfterPoolDispose_IsReleasedInsteadOfEnqueued()
    {
        string name = $"pool_late_return_{Guid.NewGuid():N}";
        var pool = DataSessionPool<SqliteProvider>.Create(Options(name), maxIdleSize: 2);
        DataSession<SqliteProvider> rented = await pool.RentAsync();
        await pool.DisposeAsync();

        await rented.DisposeAsync();   // 池已释放：归还路径不得入队，也不得抛
        await Assert.That(pool.IdleCount).IsEqualTo(0);
    }
}
