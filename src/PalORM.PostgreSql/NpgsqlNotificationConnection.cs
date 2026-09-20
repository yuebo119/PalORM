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

    /// <summary>B7：一次往返执行多条 LISTEN——N 个 channel 的启动/重连延迟从 N × RTT
    /// 降为 1 × RTT。</summary>
    Task ListenAllAsync(IReadOnlyList<string> quotedChannels, CancellationToken cancellationToken);

    /// <summary>A2：保活探测——LISTEN 连接绝大部分时间静默，是 dead-peer 检测的最典型受害者。</summary>
    Task ProbeAsync(CancellationToken cancellationToken);
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
            // ITM-638：Open 失败（含订阅事件前的半开形态）自清理——不依赖调用方
            // 记得 Dispose 一个从未 Open 成功的连接（调用方重连循环每次 factory 新建）。
            // A4：清理失败必须被吞掉——dispose 在半开连接上抛异常时会 (a) 顶掉原始
            // NpgsqlException 使根因湮灭，(b) 更严重的是它不带 PalORM.IsTransient 标记，
            // 会被 PgNotificationListener.IsTransient 判为非瞬时，把本可重连的瞬时开锁
            // 失败升级为监听器永久死亡。同文件 PgNotificationListener:211-217 对同样场景
            // 已有「吞掉 + Debug 留痕」契约，此处补齐同口径。
            await DisposeSwallowingAsync().ConfigureAwait(false);
            throw WrapConnectionException(exception);
        }
        catch
        {
            // 非 NpgsqlException 路径同 ITM-638：Open 失败自清理后重抛（保留原始异常，
            // 不吞不换型——与上方 catch 分支同一清理契约，仅异常类型过滤不同）。
            await DisposeSwallowingAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A4：清理失败静默——保证原始异常按原路径传播，不被 dispose 异常顶掉。</summary>
    private async ValueTask DisposeSwallowingAsync()
    {
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"PalORM: notification connection dispose failed during Open cleanup: {exception}");
        }
    }

    /// <summary>执行 LISTEN。channel <b>必须已经 QuoteIdentifier 引用</b>（本适配器不再
    /// 二次引用，双重引用会改变通道名）——当前唯一调用方 PgNotificationListener 已保证，
    /// 新调用方必须遵守（ITM-638 契约显式化）。
    /// <para><b>B7：多 channel 合并为单次往返</b>——原实现每 channel 一次
    /// <c>ExecuteNonQueryAsync</c>（各建一个 NpgsqlCommand），N 个 channel 的启动/重连
    /// 延迟 = N × RTT。PG 的 LISTEN 是工具语句，可在一条命令里用 <c>;</c> 拼接多条，
    /// N 个 RTT 压成 1 个（跨地域部署 RTT 50ms+ 时，8 channel 省 350ms）。</para></summary>
    public async Task ListenAsync(string quotedChannel, CancellationToken cancellationToken)
        => await ListenAllAsync([quotedChannel], cancellationToken).ConfigureAwait(false);

    /// <summary>B7：一次往返执行多条 LISTEN（分号拼接的多语句命令）。</summary>
    public async Task ListenAllAsync(IReadOnlyList<string> quotedChannels, CancellationToken cancellationToken)
    {
        if (quotedChannels.Count == 0) return;
        try
        {
            await using var command = new NpgsqlCommand(
                string.Join("; ", quotedChannels.Select(static c => $"LISTEN {c}")), _connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException exception)
        {
            throw WrapConnectionException(exception);
        }
    }

    /// <summary>A2：保活探测——<c>SELECT 1</c> 验证连接仍活着。LISTEN 连接绝大部分时间静默，
    /// 是 dead-peer 检测的最典型受害者（NAT/LB 静默掐断不产生 FIN/RST）。</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT 1", _connection)
            {
                CommandTimeout = 10,
            };
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
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
