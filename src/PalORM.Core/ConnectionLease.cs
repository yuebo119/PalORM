using System.Data;
using System.Data.Common;

namespace PalORM;

/// <summary>一次命令执行使用的连接租约。会话主连接不归租约所有，临时读连接由租约释放。</summary>
internal sealed class ConnectionLease : IAsyncDisposable
{
    private readonly bool _ownsConnection;
    private bool _disposed;

    private ConnectionLease(DbConnection connection, bool ownsConnection)
    {
        Connection = connection;
        _ownsConnection = ownsConnection;
    }

    internal DbConnection Connection { get; }

    internal static ConnectionLease Borrow(DbConnection connection) => new(connection, false);

    internal static async ValueTask<ConnectionLease> OpenOwnedAsync(
        Func<DbConnection> connectionFactory,
        CancellationToken cancellationToken,
        Func<DbConnection, CancellationToken, Task>? initializeConnection = null)
    {
        DbConnection connection = connectionFactory();
        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Provider 初始化钩子对读路由连接同样生效（ITM-207）——
            // 主连接在 CreateAsync 初始化，读连接在此处补齐同一契约。
            if (initializeConnection is not null)
                await initializeConnection(connection, cancellationToken).ConfigureAwait(false);
            return new ConnectionLease(connection, true);
        }
        catch (Exception exception)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupException) { exception.Data["PalORM.ConnectionCleanupException"] = cleanupException; }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsConnection)
            await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
