using Npgsql;
using NpgsqlTypes;
using PalORM.PostgreSql;

namespace PalORM.Core.Tests;

[NotInParallel]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "Each fake connection is transferred to PgNotificationListener and disposal is asserted explicitly.")]
public sealed class PgNotificationListenerTests
{
    [Test]
    public async Task StartAsync_InitialFailure_CanStartAgain()
    {
        var failed = new FakePgNotificationConnection
        {
            OpenException = new InvalidOperationException("open failed")
        };
        var recovered = new FakePgNotificationConnection();
        var connections = new Queue<IPgNotificationConnection>([failed, recovered]);
        await using var listener = new PgNotificationListener(
            connections.Dequeue,
            ["events"],
            _ => TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await listener.StartAsync());
        await listener.StartAsync();
        await listener.StopAsync();

        await Assert.That(failed.DisposeCount).IsEqualTo(1);
        await Assert.That(recovered.ListenedChannels).Contains("\"events\"");
        await Assert.That(recovered.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task StartAsync_Cancellation_PropagatesAndDisposesConnection()
    {
        var connection = new FakePgNotificationConnection { BlockOpen = true };
        await using var listener = new PgNotificationListener(
            () => connection,
            ["events"],
            _ => TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();

        Task start = listener.StartAsync(cancellation.Token);
        await connection.OpenEntered.Task;
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await start);
        await Assert.That(connection.DisposeCount).IsEqualTo(1);
    }

    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "Fake connection ownership is transferred to the listener factory and verified through DisposeCount.")]
    public async Task TransientWaitFailure_CreatesNewConnectionAndRelistens()
    {
        var disconnected = new FakePgNotificationConnection();
        disconnected.WaitSteps.Enqueue(_ => Task.FromException(CreateTransientException()));
        var reconnected = new FakePgNotificationConnection();
        var connections = new Queue<IPgNotificationConnection>([disconnected, reconnected]);
        await using var listener = new PgNotificationListener(
            connections.Dequeue,
            ["events", "audit"],
            _ => TimeSpan.Zero);

        await listener.StartAsync();
        await reconnected.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await listener.StopAsync();

        await Assert.That(disconnected.DisposeCount).IsEqualTo(1);
        await Assert.That(reconnected.ListenedChannels).IsEquivalentTo(["\"events\"", "\"audit\""]);
        await Assert.That(reconnected.DisposeCount).IsEqualTo(1);
    }

    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "Fake connection ownership is transferred to the listener and verified through listener disposal.")]
    public async Task NotificationSubscriberFailure_DoesNotBlockLaterSubscriber()
    {
        var connection = new FakePgNotificationConnection();
        await using var listener = new PgNotificationListener(
            () => connection,
            ["events"],
            _ => TimeSpan.Zero);
        int delivered = 0;
        listener.OnNotification += (_, _) => throw new InvalidOperationException("subscriber failed");
        listener.OnNotification += (_, args) =>
        {
            if (args.Channel == "events" && args.Payload == "payload") delivered++;
        };

        await listener.StartAsync();
        connection.Emit("events", "payload");
        await listener.StopAsync();

        await Assert.That(delivered).IsEqualTo(1);
    }

    [Test]
    public async Task BackgroundFailure_RaisesOnError()
    {
        var connection = new FakePgNotificationConnection();
        connection.WaitSteps.Enqueue(_ => Task.FromException(new InvalidOperationException("terminal")));
        await using var listener = new PgNotificationListener(
            () => connection,
            ["events"],
            _ => TimeSpan.Zero);
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.OnError += (_, args) => error.TrySetResult(args.Exception);

        await listener.StartAsync();
        Exception reported = await error.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(reported.Message).IsEqualTo("terminal");
    }

    [Test]
    public async Task BackgroundFailure_WithoutOnErrorSubscriber_LogsTermination()
    {
        // ERR-02：无 OnError 订阅者时后台终止必须经 Logger 留痕，不得静默死亡
        var connection = new FakePgNotificationConnection();
        connection.WaitSteps.Enqueue(_ => Task.FromException(new InvalidOperationException("terminal")));
        var logged = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new PgNotificationListener(
            () => connection,
            ["events"],
            _ => TimeSpan.Zero)
        {
            Logger = new CapturingLogger(logged),
        };

        await listener.StartAsync();
        Exception? captured = await logged.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Message).IsEqualTo("terminal");
    }

    [Test]
    public async Task BackgroundFailure_AllOnErrorSubscribersThrow_LogsRootCause()
    {
        // r19/ITM-702：订阅者全部抛异常时，订阅者异常只有 Debug 级——监听终止根因
        // 必须 Error 级留痕（与无订阅者路径同口径）。
        var connection = new FakePgNotificationConnection();
        connection.WaitSteps.Enqueue(_ => Task.FromException(new InvalidOperationException("terminal")));
        var logged = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new PgNotificationListener(
            () => connection,
            ["events"],
            _ => TimeSpan.Zero)
        {
            Logger = new TerminalCapturingLogger(logged),
        };
        listener.OnError += (_, _) => throw new InvalidOperationException("subscriber boom");

        await listener.StartAsync();
        Exception? captured = await logged.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Message).IsEqualTo("terminal");
    }

    private sealed class CapturingLogger(TaskCompletionSource<Exception?> sink)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.TrySetResult(exception);
    }

    /// <summary>只捕获根因（Message == "terminal"）的日志——跳过订阅者异常（Debug 级）与重连噪音。</summary>
    private sealed class TerminalCapturingLogger(TaskCompletionSource<Exception?> sink)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception?.Message == "terminal")
                sink.TrySetResult(exception);
        }
    }

    [Test]
    public async Task StopAsync_DuringReconnectDelay_DoesNotRaiseOnError()
    {
        var connection = new FakePgNotificationConnection();
        connection.WaitSteps.Enqueue(_ => Task.FromException(CreateTransientException()));
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new PgNotificationListener(
            () => connection,
            ["events"],
            _ =>
            {
                delayEntered.TrySetResult();
                return TimeSpan.FromMinutes(1);
            });
        int errorCount = 0;
        listener.OnError += (_, _) => errorCount++;

        await listener.StartAsync();
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await listener.StopAsync();

        await Assert.That(errorCount).IsEqualTo(0);
    }

    [Test]
    public async Task ConfigureNotifyParameters_NullPayload_UsesTextType()
    {
        await using var command = new NpgsqlCommand();

        PgNotificationListener.ConfigureNotifyParameters(command, "events", null);

        await Assert.That(command.Parameters[0].NpgsqlDbType).IsEqualTo(NpgsqlDbType.Text);
        await Assert.That(command.Parameters[1].NpgsqlDbType).IsEqualTo(NpgsqlDbType.Text);
        await Assert.That(command.Parameters[1].Value).IsEqualTo(DBNull.Value);
    }

    private static InvalidOperationException CreateTransientException()
    {
        var exception = new InvalidOperationException("transient");
        exception.Data["PalORM.IsTransient"] = true;
        return exception;
    }
}

internal sealed class FakePgNotificationConnection : IPgNotificationConnection
{
    internal Exception? OpenException { get; init; }
    internal bool BlockOpen { get; init; }
    internal Queue<Func<CancellationToken, Task>> WaitSteps { get; } = new();
    internal List<string> ListenedChannels { get; } = [];
    internal TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int DisposeCount { get; private set; }

    public event Action<string, string>? Notification;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        OpenEntered.TrySetResult();
        if (OpenException is not null) throw OpenException;
        if (BlockOpen) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public Task ListenAsync(string quotedChannel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListenedChannels.Add(quotedChannel);
        return Task.CompletedTask;
    }

    // B7：合并 LISTEN——Fake 记录全部 channel（契约与逐条版一致：都进 ListenedChannels）
    public Task ListenAllAsync(IReadOnlyList<string> quotedChannels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string channel in quotedChannels) ListenedChannels.Add(channel);
        return Task.CompletedTask;
    }

    // A2：保活探测——Fake 默认成功；测试可经 ProbeException 制造探测失败
    internal Exception? ProbeException { get; init; }

    // NOTIF-001（2026-09-22）：真实探测是一次网络往返（非零延迟）。零延迟会掩盖心跳循环的
    // 定时器竞态——CTS 重臂与下一轮 delayTask 落在同一 tick 时延时会赢，真实往返下 CTS 先到期。
    internal TimeSpan ProbeDelay { get; init; }

    private int _pendingWaits;
    private int _maxWaitConcurrency;
    private readonly Lock _waitGate = new();

    /// <summary>并发等待峰值——心跳循环若遗留未取消的 WaitAsync，该值会 &gt; 1。</summary>
    internal int MaxWaitConcurrency
    {
        get { lock (_waitGate) return _maxWaitConcurrency; }
    }

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ProbeDelay > TimeSpan.Zero)
            await Task.Delay(ProbeDelay, cancellationToken).ConfigureAwait(false);
        if (ProbeException is not null) throw ProbeException;
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        WaitEntered.TrySetResult();
        lock (_waitGate)
        {
            _pendingWaits++;
            if (_pendingWaits > _maxWaitConcurrency) _maxWaitConcurrency = _pendingWaits;
        }
        try
        {
            if (WaitSteps.Count > 0)
            {
                await WaitSteps.Dequeue()(cancellationToken);
                return;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            lock (_waitGate) _pendingWaits--;
        }
    }

    internal void Emit(string channel, string payload) => Notification?.Invoke(channel, payload);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>B6/B7/A2（2026-09-20）：通知监听器的分配与保活改造。
/// <para><b>B6</b>：调用列表缓存——分发路径不再每次 <c>GetInvocationList()</c>。</para>
/// <para><b>B7</b>：多 channel 合并为一次 LISTEN 往返 + 引用名构造期预计算。</para>
/// <para><b>A2</b>：心跳探测——静默断线（WaitAsync 不返回也不抛错）时主动发现。</para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
    Justification = "Fake connection ownership is transferred to the listener factory and verified through DisposeCount.")]
[NotInParallel]
public sealed class PgNotificationListenerHotPathTests
{
    [Test]
    public async Task B7_ListenAll_SendsEveryChannel()
    {
        var connection = new FakePgNotificationConnection();
        await using var listener = new PgNotificationListener(
            () => connection, ["events", "audit", "metrics"], _ => TimeSpan.Zero);

        await listener.StartAsync();
        await listener.StopAsync();

        // B7：合并往返不改变契约——全部 channel 仍被 LISTEN
        await Assert.That(connection.ListenedChannels)
            .IsEquivalentTo(["\"events\"", "\"audit\"", "\"metrics\""]);
    }

    [Test]
    public async Task B6_MultipleSubscribers_AllReceiveNotification()
    {
        var connection = new FakePgNotificationConnection();
        await using var listener = new PgNotificationListener(
            () => connection, ["events"], _ => TimeSpan.Zero);

        int firstCalls = 0, secondCalls = 0;
        listener.OnNotification += (_, _) => Interlocked.Increment(ref firstCalls);
        listener.OnNotification += (_, _) => Interlocked.Increment(ref secondCalls);

        await listener.StartAsync();
        await connection.WaitEntered.Task;
        connection.Emit("events", "payload-1");
        connection.Emit("events", "payload-2");
        await listener.StopAsync();

        // B6：缓存的调用列表快照必须包含全部订阅者（不能只存最后一个）
        await Assert.That(firstCalls).IsEqualTo(2);
        await Assert.That(secondCalls).IsEqualTo(2);
    }

    [Test]
    public async Task B6_Unsubscribe_StopsDeliveryToThatHandler()
    {
        var connection = new FakePgNotificationConnection();
        await using var listener = new PgNotificationListener(
            () => connection, ["events"], _ => TimeSpan.Zero);

        int calls = 0;
        void Handler(object? sender, PgNotificationEventArgs args)
        {
            Interlocked.Increment(ref calls);
        }
        listener.OnNotification += Handler;
        await listener.StartAsync();
        await connection.WaitEntered.Task;

        connection.Emit("events", "before");
        listener.OnNotification -= Handler;
        connection.Emit("events", "after");
        await listener.StopAsync();

        // B6：remove 后快照必须同步更新（否则取消订阅失效）
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task A2_KeepaliveFailure_TriggersReconnect()
    {
        // A2：连接被静默掐断时 WaitAsync 永不返回——只有心跳探测能发现
        var dead = new FakePgNotificationConnection { ProbeException = new IOException("connection reset") };
        var recovered = new FakePgNotificationConnection();
        var connections = new Queue<IPgNotificationConnection>([dead, recovered]);
        await using var listener = new PgNotificationListener(
            connections.Dequeue, ["events"], _ => TimeSpan.Zero,
            keepaliveInterval: TimeSpan.FromMilliseconds(50));

        await listener.StartAsync();
        await recovered.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await listener.StopAsync();

        await Assert.That(dead.DisposeCount).IsEqualTo(1);
        await Assert.That(recovered.ListenedChannels).Contains("\"events\"");
        await Assert.That(recovered.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task A2_KeepaliveSuccess_KeepsSameConnection()
    {
        var connection = new FakePgNotificationConnection();
        await using var listener = new PgNotificationListener(
            () => connection, ["events"], _ => TimeSpan.Zero,
            keepaliveInterval: TimeSpan.FromMilliseconds(50));

        await listener.StartAsync();
        await connection.WaitEntered.Task;
        // 心跳成功不应触发重连—— DisposeCount 恒为 1（StopAsync 释放的那次）
        await Task.Delay(400);
        await listener.StopAsync();

        await Assert.That(connection.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task A2_Keepalive_ListenerSurvivesMultipleIdleHeartbeatIntervals()
    {
        // 2026-09-22 审计 R1（NOTIF-001）：空闲通道是通知监听器的常态，而原心跳循环里
        // keepaliveCts 只能取消一次（CancelAfter 在已取消的 CTS 上是 no-op），waitTask 的内部
        // OCE 又没有任何 catch filter 接住（owner 未取消 + 无 PalORM.IsTransient 标记）——
        // 心跳先于通知到期时 OCE 落到外层 RaiseError，监听器在 1~2 个心跳周期内静默死亡。
        // 本用例锁定"跨多个空闲心跳周期后仍存活且仍能收通知"：旧实现的死亡路径会在 finally
        // 退订连接并触发 OnError，两条断言都失败。
        var connection = new FakePgNotificationConnection();
        var errors = new List<Exception>();
        await using var listener = new PgNotificationListener(
            () => connection, ["events"], _ => TimeSpan.Zero,
            keepaliveInterval: TimeSpan.FromMilliseconds(50));
        listener.OnError += (_, args) => errors.Add(args.Exception);
        int delivered = 0;
        listener.OnNotification += (_, _) => Interlocked.Increment(ref delivered);

        await listener.StartAsync();
        await connection.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 空闲跨越 8 个心跳周期（每轮心跳触发一次探测，Fake 默认成功）
        await Task.Delay(400);

        await Assert.That(errors).IsEmpty();

        // 行为证据：心跳之后到达的通知仍能送达（死亡路径已退订连接，收不到）
        connection.Emit("events", "after-idle");
        await Assert.That(delivered).IsEqualTo(1);
    }

    [Test]
    public async Task A2_Keepalive_ListenerSurvivesIdleHeartbeats_WhenProbeHasLatency()
    {
        // NOTIF-001（2026-09-22）：本用例是心跳循环的判别性判据。零延迟探测下 CTS 重臂与
        // 下一轮 delayTask 落在同一 tick，延时分支赢，掩盖了竞态；真实探测有网络往返
        // （这里注入 20ms），于是每轮 CTS 都比下一轮 delayTask 早到期：CTS 取消 waitTask 后，
        // 内部 OCE 没有 catch filter 接住（owner 未取消、无 PalORM.IsTransient 标记），
        // 旧实现落到外层 RaiseError，监听器在第二个心跳周期静默死亡。
        // 同时锁定"不遗留未取消的 WaitAsync"（旧实现每轮遗留一个，峰值并发 > 1）。
        var connection = new FakePgNotificationConnection { ProbeDelay = TimeSpan.FromMilliseconds(20) };
        var errors = new List<Exception>();
        await using var listener = new PgNotificationListener(
            () => connection, ["events"], _ => TimeSpan.Zero,
            keepaliveInterval: TimeSpan.FromMilliseconds(100));
        listener.OnError += (_, args) => errors.Add(args.Exception);
        int delivered = 0;
        listener.OnNotification += (_, _) => Interlocked.Increment(ref delivered);

        await listener.StartAsync();
        await connection.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 空闲跨越约 6 个心跳周期（每轮 = 100ms 等待 + 20ms 探测）
        await Task.Delay(800);

        await Assert.That(errors).IsEmpty();
        await Assert.That(connection.MaxWaitConcurrency).IsEqualTo(1);

        connection.Emit("events", "after-idle");
        await Assert.That(delivered).IsEqualTo(1);
    }
}