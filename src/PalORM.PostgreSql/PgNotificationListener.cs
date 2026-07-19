using Npgsql;
using NpgsqlTypes;

namespace PalORM.PostgreSql;

/// <summary>PostgreSQL NOTIFY/LISTEN 异步通知监听器。
/// 每次断线后创建新连接并重新执行全部 LISTEN；不复用已损坏的会话。
/// <para>注意: <see cref="OnNotification"/> 事件在监听后台任务上触发。
/// 订阅者异常会被隔离，不会终止后续订阅者或重连循环。</para></summary>
public sealed partial class PgNotificationListener : IAsyncDisposable
{
    private const int _maxReconnectAttempts = 5;

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Error,
        Message = "PgNotificationListener background loop terminated; notifications will no longer be delivered.")]
    private static partial void LogListenerTerminated(
        Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "PgNotificationListener: failed disposing damaged connection (swallowed).")]
    private static partial void LogDisposeFailed(
        Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "PgNotificationListener: OnError subscriber threw an exception (swallowed).")]
    private static partial void LogOnErrorSubscriberThrew(
        Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "PgNotificationListener: OnNotification subscriber threw an exception (swallowed).")]
    private static partial void LogOnNotificationSubscriberThrew(
        Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "PgNotificationListener: run task was canceled while stopping.")]
    private static partial void LogRunTaskCanceled(
        Microsoft.Extensions.Logging.ILogger logger);

    private readonly Func<IPgNotificationConnection> _connectionFactory;
    private readonly Func<int, TimeSpan> _reconnectDelay;
    private readonly string[] _channels;
    private readonly Lock _lock = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "RunAsync finally owns and disposes the active CancellationTokenSource after the run task exits.")]
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _disposed;

    /// <summary>收到 NOTIFY 时触发。回调在后台监听任务线程上执行——耗时处理请自行转移到其他线程。
    /// 单个订阅者抛出的异常被吞掉,不会阻断其他订阅者,也不会终止监听循环。</summary>
    public event EventHandler<PgNotificationEventArgs>? OnNotification;

    /// <summary>首次启动成功后，后台监听因非取消异常终止时触发。</summary>
    public event EventHandler<PgNotificationErrorEventArgs>? OnError;

    /// <summary>可选兜底日志。未订阅 <see cref="OnError"/> 时，后台监听终止原因
    /// 经此记录，避免监听器静默死亡后 NOTIFY 丢失无痕。</summary>
    public Microsoft.Extensions.Logging.ILogger? Logger { get; set; }

    /// <summary>创建监听器,重连退避为线性递增(第 n 次重连等待 n 秒,上限 5 次)。
    /// 构造不建立连接;调用 <see cref="StartAsync"/> 后才连接并 LISTEN。</summary>
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

    /// <summary>启动后台监听:首次连接成功并对全部 channel 执行 LISTEN 后返回;
    /// 首次连接失败时异常直接抛出(不进入重连)。非幂等——已启动时再次调用抛
    /// <see cref="InvalidOperationException"/>;<see cref="StopAsync"/> 之后可再次启动。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        CancellationTokenSource cts;
        Task runTask;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                // ITM-583：连接释放显式管理——断线后 NpgsqlConnection.DisposeAsync 若抛出，
                // `await using` 的隐式 Dispose 会绕过 transient 重连逻辑直达外层终止监听。
                // 已损坏连接的清理失败无诊断价值，吞掉后按原路径继续重连。
                IPgNotificationConnection connection = _connectionFactory();
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
                    // ITM-567：重连成功（Open+LISTEN 全通过）即清零——此前仅收到 NOTIFY 才清零，
                    // 静默通道 + 周期性断连（LB 空闲切断）下每次成功重连仍累加计数，
                    // 第 N+1 次断开监听器永久死亡。上限语义 = 连续失败次数，与文档直觉一致。
                    reconnectAttempt = 0;

                    while (!owner.IsCancellationRequested)
                    {
                        await connection.WaitAsync(owner.Token).ConfigureAwait(false);
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
                    try { await connection.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception disposeException) when (disposeException is not OperationCanceledException)
                    {
                        // 损坏连接的 Dispose 失败不改变控制流（重连/终止由上方 catch 决定）
                        if (Logger is { } logger1)
                            LogDisposeFailed(logger1, disposeException);
                    }
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
        {
            // 无订阅者时后台监听终止必须留痕——否则 NOTIFY 静默丢失（审计 ERR-02）
            LogListenerTerminated(
                Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                exception);
            return;
        }

        var args = new PgNotificationErrorEventArgs(exception);
        foreach (Delegate candidate in handlers.GetInvocationList())
        {
            var handler = (EventHandler<PgNotificationErrorEventArgs>)candidate;
            try { handler(this, args); }
            catch (Exception ex)
            {
                // 记录但不传播订阅者异常，确保其他订阅者和监听循环不受影响
                if (Logger is { } logger1)
                    LogOnErrorSubscriberThrew(logger1, ex);
            }
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
            catch (Exception ex)
            {
                // 记录但不传播订阅者异常，确保其他订阅者和监听循环不受影响
                if (Logger is { } logger1)
                    LogOnNotificationSubscriberThrew(logger1, ex);
            }
        }
    }

    /// <summary>停止后台监听并等待监听任务结束(取消异常被吞)。幂等——未启动或已停止时直接返回;
    /// 并发调用由释放权移交保证只有一方执行清理。</summary>
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
            catch (OperationCanceledException)
            {
                // 正常的取消路径，记录以便诊断但不抛出
                if (Logger is { } logger1)
                    LogRunTaskCanceled(logger1);
            }
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

    /// <summary>停止监听并标记已释放。幂等;释放后 <see cref="StartAsync"/> 抛 <see cref="ObjectDisposedException"/>。</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
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
    /// <summary>导致后台监听终止的异常。</summary>
    public Exception Exception { get; } = exception;
}

/// <summary>PG 通知事件参数。</summary>
public sealed class PgNotificationEventArgs(string channel, string payload) : EventArgs
{
    /// <summary>触发通知的 channel 名。</summary>
    public string Channel { get; } = channel;

    /// <summary>NOTIFY 携带的 payload;未指定时为空字符串。</summary>
    public string Payload { get; } = payload;
}
