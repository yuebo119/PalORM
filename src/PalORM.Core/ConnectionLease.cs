using System.Data;
using System.Data.Common;

namespace PalORM;

/// <summary>一次命令执行使用的连接租约。会话主连接不归租约所有，临时读连接由租约释放。</summary>
internal sealed class ConnectionLease : IAsyncDisposable
{
    private readonly bool _ownsConnection;
    // r19/ITM-694：int + Interlocked 一次性守卫——此前 check-then-act 在并发双释放下
    // 两个调用都能读到 false 并对 owned 连接重复 DisposeAsync。
    private int _disposeState;

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
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        if (_ownsConnection)
        {
            // ITM-635：await using 语法下，主查询异常先抛时此处的释放异常会覆盖主异常
            // （丢失原始失败）。释放失败几乎总伴随连接已死的主异常——本类型无日志通道，
            // 取舍为静默吞释放异常以保主异常（同 TransactionCleanup 成功路径的文档化决策）。
            try { await Connection.DisposeAsync().ConfigureAwait(false); }
            catch { /* 主异常保留优先——见上方注释 */ }
        }
    }
}
