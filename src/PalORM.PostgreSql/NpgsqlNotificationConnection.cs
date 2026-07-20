using Npgsql;
using NpgsqlTypes;

namespace PalORM.PostgreSql;

/// <summary>Npgsql 连接适配器——抽象 PG NOTIFY/LISTEN 的连接契约，
/// 便于 PgNotificationListener 测试（注入 mock 连接验证重连/事件分发）。</summary>
internal interface IPgNotificationConnection : IAsyncDisposable
{
    event Action<string, string>? Notification;
    Task OpenAsync(CancellationToken cancellationToken);
    Task ListenAsync(string quotedChannel, CancellationToken cancellationToken);
    Task WaitAsync(CancellationToken cancellationToken);
}

/// <summary>Npgsql 的 IPgNotificationConnection 实现——Open/Listen/Wait 统一包装 NpgsqlException
/// 为带 PalORM.IsTransient 标记的 InvalidOperationException（与 DataSession 重试链路对齐）。</summary>
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
