using System.Data.Common;

namespace PalORM;

/// <summary>一次命令执行使用的连接租约。<b>v5.6 起恒为借用语义</b>：主连接由会话持有，
/// 读路由连接由会话级复用持有，租约只标记"本次执行正在使用哪个连接"，不承担释放职责。
/// 原 <c>OpenOwnedAsync</c>（临时读连接由租约释放）已随读连接会话级复用一并移除——
/// 读连接的创建与释放在 <c>DataSession</c> 的持有者一侧完成。
/// <para><see cref="IAsyncDisposable"/> 保留为使调用点的 <c>await using</c> 形态统一；
/// <see cref="DisposeAsync"/> 是文档化的 no-op（借用语义下无资源可释放）。</para></summary>
internal sealed class ConnectionLease : IAsyncDisposable
{
    private ConnectionLease(DbConnection connection)
    {
        Connection = connection;
    }

    internal DbConnection Connection { get; }

    internal static ConnectionLease Borrow(DbConnection connection) => new(connection);

    /// <summary>借用语义下无资源可释放——连接归会话所有，由会话 DisposeAsync 统一释放。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
