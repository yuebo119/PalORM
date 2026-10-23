using PalORM.Sqlite;

namespace PalORM.Core.Tests;

/// <summary>2026-10-22 终审 P1-5：池化拦截器所有权。
/// 池基准拦截器由调用方持有，任一租期归还/池释放都不得 Dispose 共享实例；
/// 租期内 AddInterceptor 加项由会话持有，归还时释放。</summary>
public sealed class PoolInterceptorOwnershipTests
{
    [Test]
    public async Task PoolBaseInterceptor_IsNotDisposedBySessionReturn()
    {
        using var baseInterceptor = new CountingPoolInterceptor();
        var pool = DataSessionPool<SqliteProvider>.Create(new DbOptions
        {
            ConnectionString = "Data Source=:memory:",
            Interceptors = new IQueryInterceptor[] { baseInterceptor }
        }, maxIdleSize: 1);

        var first = await pool.RentAsync();
        var second = await pool.RentAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();   // 池满归还：该会话真实释放

        await Assert.That(baseInterceptor.DisposeCount).IsEqualTo(0);

        var rentedAgain = await pool.RentAsync();
        await rentedAgain.ExecuteAsync($"SELECT 1");   // 基拦截器仍可用
        await rentedAgain.DisposeAsync();
        await pool.DisposeAsync();

        await Assert.That(baseInterceptor.DisposeCount).IsEqualTo(0);
    }

    [Test]
    public async Task SessionAddedInterceptor_IsDisposedOnPoolReturn()
    {
        var pool = DataSessionPool<SqliteProvider>.Create(new DbOptions
        {
            ConnectionString = "Data Source=:memory:"
        }, maxIdleSize: 1);

        using var added = new CountingPoolInterceptor();
        var session = await pool.RentAsync();
        session.AddInterceptor(added);
        await session.DisposeAsync();

        await Assert.That(added.DisposeCount).IsEqualTo(1);
        await Assert.That(added.Disposed).IsTrue();
        await pool.DisposeAsync();
    }
}

internal sealed class CountingPoolInterceptor : IQueryInterceptor, IDisposable
{
    public int DisposeCount { get; private set; }
    public bool Disposed { get; private set; }

    public void OnBefore(QueryContext context)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
    }

    public void OnAfter(QueryContext context, TimeSpan elapsed, int rowCount) { }

    public void OnError(QueryContext context, Exception exception) { }

    public void Dispose()
    {
        if (Disposed) return;   // 幂等：using 兜底与池归还可能各调一次
        DisposeCount++;
        Disposed = true;
    }
}
