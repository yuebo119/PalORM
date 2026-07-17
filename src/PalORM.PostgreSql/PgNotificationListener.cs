using Npgsql;
using NpgsqlTypes;

namespace PalORM.PostgreSql;

/// <summary>PostgreSQL NOTIFY/LISTEN 异步通知监听器。
/// 每次断线后创建新连接并重新执行全部 LISTEN；不复用已损坏的会话。
/// <para>注意: <see cref="OnNotification"/> 事件在监听后台任务上触发。
/// 订阅者异常会被隔离，不会终止后续订阅者或重连循环。</para></summary>
public sealed class PgNotificationListener : IAsyncDisposable
{
    private const int _maxReconnectAttempts = 5;

    private readonly Func<IPgNotificationConnection> _connectionFactory;
    private readonly Func<int, TimeSpan> _reconnectDelay;
    private readonly string[] _channels;
    private readonly Lock _lock = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "RunAsync finally owns and disposes the active CancellationTokenSource after the run task exits.")]
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _disposed;

    public event EventHandler<PgNotificationEventArgs>? OnNotification;

    /// <summary>首次启动成功后，后台监听因非取消异常终止时触发。</summary>
    public event EventHandler<PgNotificationErrorEventArgs>? OnError;

    public PgNotificationListener(string connectionString, params string[] channels)
        : this(() => new NpgsqlNotificationConnection(connectionString),
            channels,
            attempt => TimeSpan.FromSeconds(attempt))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    internal PgNotificationListener(
        Func<IPgNotificationConnection> connectionFactory,
        string[] channels,
        Func<int, TimeSpan>? reconnectDelay = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length == 0) throw new ArgumentException("至少指定一个 channel", nameof(channels));
        foreach (string channel in channels)
            ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        _connectionFactory = connectionFactory;
        _channels = (string[])channels.Clone();
        _reconnectDelay = reconnectDelay ?? (attempt => TimeSpan.FromSeconds(attempt));
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        CancellationTokenSource cts;
        Task runTask;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runTask is not null) throw new InvalidOperationException("Listener is already started.");
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            runTask = RunAsync(cts, started);
            _runTask = runTask;
        }

        try
        {
            await started.Task.ConfigureAwait(false);
        }
        catch
        {
            await runTask.ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunAsync(CancellationTokenSource owner, TaskCompletionSource started)
    {
        // 先完成 _runTask 发布，避免同步 Open 失败的 finally 被随后赋值覆盖。
        await Task.Yield();
        int reconnectAttempt = 0;
        bool initialConnection = true;
        try
        {
            while (!owner.IsCancellationRequested)
            {
                await using IPgNotificationConnection connection = _connectionFactory();
                connection.Notification += OnConnectionNotification;
                try
                {
                    await connection.OpenAsync(owner.Token).ConfigureAwait(false);
                    foreach (string channel in _channels)
                    {
                        string safeChannel = PostgreSqlProvider.QuoteIdentifier(channel);
                        await connection.ListenAsync(safeChannel, owner.Token).ConfigureAwait(false);
                    }

                    if (initialConnection)
                    {
                        initialConnection = false;
                        started.TrySetResult();
                    }

                    while (!owner.IsCancellationRequested)
                    {
                        await connection.WaitAsync(owner.Token).ConfigureAwait(false);
                        reconnectAttempt = 0;
                    }
                }
                catch (OperationCanceledException) when (owner.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (IsTransient(exception) && !initialConnection)
                {
                    if (++reconnectAttempt > _maxReconnectAttempts)
                        throw;
                    await Task.Delay(_reconnectDelay(reconnectAttempt), owner.Token).ConfigureAwait(false);
                    continue;
                }
                finally
                {
                    connection.Notification -= OnConnectionNotification;
                }
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            if (initialConnection)
                started.TrySetCanceled(owner.Token);
        }
        catch (Exception exception)
        {
            if (initialConnection)
                started.TrySetException(exception);
            else
                RaiseError(exception);
        }
        finally
        {
            started.TrySetCanceled(owner.Token);
            bool ownsDispose;
            lock (_lock)
            {
                // 引用仍指向 owner = 自然退出（无 Stop 在飞），本方法负责释放；
                // 引用已被 StopCoreAsync 清空 = Stop 已接管释放权——其 CancelAsync 可能未完成，
                // 此处 Dispose 会与之并发（CTS 不支持），改由 Stop 在 runTask 结束后释放。
                ownsDispose = ReferenceEquals(_cts, owner);
                if (ownsDispose)
                {
                    _cts = null;
                    _runTask = null;
                }
            }
            if (ownsDispose)
                owner.Dispose();
        }
    }

    private static bool IsTransient(Exception exception)
        => exception.Data["PalORM.IsTransient"] is true;

    private void RaiseError(Exception exception)
    {
        EventHandler<PgNotificationErrorEventArgs>? handlers = OnError;
        if (handlers is null)
            return;

        var args = new PgNotificationErrorEventArgs(exception);
        foreach (Delegate candidate in handlers.GetInvocationList())
        {
            var handler = (EventHandler<PgNotificationErrorEventArgs>)candidate;
            try { handler(this, args); }
            catch { /* 错误订阅者不能替换原始后台故障。 */ }
        }
    }

    private void OnConnectionNotification(string channel, string payload)
    {
        EventHandler<PgNotificationEventArgs>? handlers = OnNotification;
        if (handlers is null)
            return;

        var args = new PgNotificationEventArgs(channel, payload);
        foreach (Delegate candidate in handlers.GetInvocationList())
        {
            var handler = (EventHandler<PgNotificationEventArgs>)candidate;
            try { handler(this, args); }
            catch { /* 一个订阅者不能阻断其他订阅者或监听循环。 */ }
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _cts; }
        if (cts is not null)
            await StopCoreAsync(cts).ConfigureAwait(false);
    }

    private async Task StopCoreAsync(CancellationTokenSource owner)
    {
        Task? runTask;
        Task cancellation;
        lock (_lock)
        {
            if (!ReferenceEquals(_cts, owner))
                return;
            // 先清引用声明释放权：RunAsync 的 finally 看到引用非 owner 即不再 Dispose，
            // 消除 Dispose 与未完成 CancelAsync 的并发（CTS 不支持该并发）。
            _cts = null;
            runTask = _runTask;
            _runTask = null;
            cancellation = owner.CancelAsync();
        }

        await cancellation.ConfigureAwait(false);
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // CancelAsync 已完成、runTask 已结束——此刻 Dispose 无并发窗口。
        owner.Dispose();
    }

    /// <summary>发送 NOTIFY（参数化 pg_notify()，零 SQL 注入风险）。</summary>
    public static async Task NotifyAsync(string connectionString, string channel, string? payload = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
        ConfigureNotifyParameters(cmd, channel, payload);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static void ConfigureNotifyParameters(NpgsqlCommand command, string channel, string? payload)
    {
        command.Parameters.AddWithValue("@channel", NpgsqlDbType.Text, channel);
        command.Parameters.AddWithValue("@payload", NpgsqlDbType.Text, (object?)payload ?? DBNull.Value);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock) { if (_disposed) return; _disposed = true; }
        await StopAsync().ConfigureAwait(false);
    }
}

internal interface IPgNotificationConnection : IAsyncDisposable
{
    event Action<string, string>? Notification;
    Task OpenAsync(CancellationToken cancellationToken);
    Task ListenAsync(string quotedChannel, CancellationToken cancellationToken);
    Task WaitAsync(CancellationToken cancellationToken);
}

internal sealed class NpgsqlNotificationConnection(string connectionString) : IPgNotificationConnection
{
    private readonly NpgsqlConnection _connection = new(connectionString);

    public event Action<string, string>? Notification;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            _connection.Notification += OnNotification;
        }
        catch (NpgsqlException exception)
        {
            throw WrapConnectionException(exception);
        }
    }

    public async Task ListenAsync(string quotedChannel, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand($"LISTEN {quotedChannel}", _connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException exception)
        {
            throw WrapConnectionException(exception);
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connection.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException exception)
        {
            throw WrapConnectionException(exception);
        }
    }

    private static InvalidOperationException WrapConnectionException(NpgsqlException exception)
    {
        var wrapped = new InvalidOperationException("PostgreSQL notification connection failed.", exception);
        wrapped.Data["PalORM.IsTransient"] = exception.IsTransient;
        return wrapped;
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
        => Notification?.Invoke(args.Channel, args.Payload);

    public async ValueTask DisposeAsync()
    {
        _connection.Notification -= OnNotification;
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>PG 通知监听后台错误事件参数。</summary>
public sealed class PgNotificationErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

/// <summary>PG 通知事件参数。</summary>
public sealed class PgNotificationEventArgs(string channel, string payload) : EventArgs
{
    public string Channel { get; } = channel;
    public string Payload { get; } = payload;
}
