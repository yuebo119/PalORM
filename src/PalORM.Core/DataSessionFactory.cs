namespace PalORM;

/// <summary>S7：会话池的 DI 友好抽象——Web/API "一请求一会话"形态把本接口注册为单例，
/// 请求作用域内 <see cref="RentAsync"/> 租出、<c>await using</c> 归还。不依赖任何 DI 容器
/// 包；用 <see cref="Microsoft.Extensions.DependencyInjection"/> 时按普通单例注册即可。
/// <para><b>为什么需要它</b>：直接注册 <see cref="DataSessionPool{TProvider}"/> 也能用，
/// 但接口把"租出会话"从池实现里抽出来，便于替换/测试，也避免业务代码依赖池的具体类型。
/// 池的语义不变：归还时复位瞬态配置，复用槽/读连接/容量启发跨租期保留。</para></summary>
public interface IDataSessionFactory<TProvider> where TProvider : IDbProvider
{
    /// <summary>空闲会话数（诊断观测点）。</summary>
    int IdleCount { get; }

    /// <summary>租出一个会话；归还即 <c>DisposeAsync</c>。用法：
    /// <c>await using DataSession&lt;P&gt; session = await factory.RentAsync(ct);</c></summary>
    ValueTask<DataSession<TProvider>> RentAsync(CancellationToken ct = default);
}

/// <summary>基于 <see cref="DataSessionPool{TProvider}"/> 的默认工厂实现。</summary>
public sealed class DataSessionFactory<TProvider> : IDataSessionFactory<TProvider>, IAsyncDisposable
    where TProvider : IDbProvider
{
    private readonly DataSessionPool<TProvider> _pool;

    /// <summary>创建工厂（内部即创建会话池）。<paramref name="maxIdleSize"/> 为池空闲上限。</summary>
    public DataSessionFactory(DbOptions options, int maxIdleSize = 16)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pool = DataSessionPool<TProvider>.Create(options, maxIdleSize);
    }

    /// <inheritdoc />
    public int IdleCount => _pool.IdleCount;

    /// <inheritdoc />
    public ValueTask<DataSession<TProvider>> RentAsync(CancellationToken ct = default)
        => _pool.RentAsync(ct);

    /// <summary>释放工厂与池内全部空闲会话。已租出的会话在归还时发现池已释放，自行真实释放。</summary>
    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}
