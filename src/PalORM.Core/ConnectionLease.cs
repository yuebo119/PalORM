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
        CancellationToken cancellationToken)
    {
        DbConnection connection = connectionFactory();
        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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
