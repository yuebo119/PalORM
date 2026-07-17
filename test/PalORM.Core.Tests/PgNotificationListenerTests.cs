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

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        WaitEntered.TrySetResult();
        if (WaitSteps.Count > 0)
        {
            await WaitSteps.Dequeue()(cancellationToken);
            return;
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    internal void Emit(string channel, string payload) => Notification?.Invoke(channel, payload);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
