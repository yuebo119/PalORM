using PalORM.PostgreSql;

namespace PalORM.Core.Tests;

/// <summary>ITM-840（v6.1）：通知连接 WaitAsync 的异常面——非 NpgsqlException 的断线形态
///（ObjectDisposedException 等 IO 族）必须获得 PalORM.IsTransient 标记，否则监听器的
/// 重连判定（只认该标记）把裸异常当终态，永久终止不重连（与 A2 意图相反）。
/// 形态：DisposeAsync 后 WaitAsync（对已释放连接的等待）产生真实 ODE，不依赖网络。</summary>
internal sealed class NotificationConnectionExceptionSurfaceTests
{
    [Test]
    public async Task WaitAsync_AfterDispose_WrappedWithTransientMarker()
    {
        var connection = new NpgsqlNotificationConnection(
            "Host=127.0.0.1;Port=1;Username=probe;Database=probe;Timeout=1");
        await connection.DisposeAsync();   // 内部 NpgsqlConnection 已释放 → WaitAsync 抛 ODE

        InvalidOperationException? thrown = null;
        try
        {
            await connection.WaitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Data["PalORM.IsTransient"] is true).IsTrue();
    }
}
