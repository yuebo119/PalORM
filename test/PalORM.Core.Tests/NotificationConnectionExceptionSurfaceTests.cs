using PalORM.PostgreSql;

namespace PalORM.Core.Tests;

/// <summary>ITM-840（v6.1）与 2026-10-22 审计 N1：通知连接 Wait/Open 的异常面——
/// 非 NpgsqlException 的断线形态（ObjectDisposedException / IOException）必须获得
/// PalORM.IsTransient 标记，否则监听器的重连判定（只认该标记）把裸异常当终态，永久终止
/// 不重连（与 A2 意图相反）。
/// 形态：DisposeAsync 后调用目标方法（对已释放连接的操作）产生真实 ODE，不依赖网络。
/// Listen/Probe 在弃置连接上由 Npgsql 抛普通 InvalidOperationException（非 IO/ODE），
/// 该形态不在本测试的 transient 包装面内；其 IO/ODE 包装保留为代码审查项。</summary>
internal sealed class NotificationConnectionExceptionSurfaceTests
{
    private const string ProbeConnectionString =
        "Host=127.0.0.1;Port=1;Username=probe;Database=probe;Timeout=1";

    [Test]
    public async Task WaitAsync_AfterDispose_WrappedWithTransientMarker()
    {
        var connection = new NpgsqlNotificationConnection(ProbeConnectionString);
        await connection.DisposeAsync();   // 内部 NpgsqlConnection 已释放 → WaitAsync 抛 ODE

        InvalidOperationException? thrown = await CaptureAsync(
            () => connection.WaitAsync(CancellationToken.None));
        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Data["PalORM.IsTransient"] is true).IsTrue();
    }

    [Test]
    public async Task OpenAsync_AfterDispose_WrappedWithTransientMarker()
    {
        var connection = new NpgsqlNotificationConnection(ProbeConnectionString);
        await connection.DisposeAsync();

        InvalidOperationException? thrown = await CaptureAsync(
            () => connection.OpenAsync(CancellationToken.None));
        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Data["PalORM.IsTransient"] is true).IsTrue();
    }

    private static async Task<InvalidOperationException?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }
}
