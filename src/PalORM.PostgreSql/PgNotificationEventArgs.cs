namespace PalORM.PostgreSql;

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
