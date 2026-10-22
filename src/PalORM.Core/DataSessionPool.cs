namespace PalORM;

/// <summary>数据会话池——"一请求一会话"推荐用法下的固定成本消除器（2026-10-13 二轮方案）。
/// <para><b>为什么需要</b>：每次 <c>DataSession.CreateAsync</c> 付出连接建立 + 拦截器表 +
/// ResilienceExecutor/CircuitBreaker + 读路由委托组的构造税（实测每操作 ~1.5KB 分配差横跨
/// Count/GetByKey/Update 五组基准项）；更重要的是<b>全部惰性晋升复用槽</b>（PL-2 CRUD 槽、
/// A9 查询槽、P1-1 直查槽，阈值 3）在"会话活不过 3 次操作"的形态下结构性永不生效。池化让
/// 会话与连接跨请求存活：构造税归零、复用槽真正激活、租用归还零解析。</para>
/// <para><b>语义契约</b>：租出（<see cref="RentAsync"/>）到归还（会话 DisposeAsync）为一个
/// 租期。归还时<b>瞬态状态全部复位</b>（WithTenant/IgnoreFilters/WithIsolationLevel/
/// WithTimeout/WithRetry/WithCircuitBreaker 的租期内覆写、AddInterceptor 的加项）——下一租期
/// 从池基准配置干净起步；<b>复用槽、读连接、物化容量启发保留</b>（跨租期存活即收益）。
/// 归还门禁：有事务、在飞操作（未消费完的流式枚举器）或并行读作用域未收口时抛出。</para>
/// <para><b>与连接池的分工</b>：本池的是"会话包装"（元数据/槽/配置状态）；驱动物理连接池
/// （MaxPoolSize 等）照常工作——池满（<c>maxIdleSize</c>）时多归还的会话真实释放（物理连接
/// 归还驱动池），不放大连接占用。</para>
/// <para><b>线程安全</b>：租/还/释放全程锁内快照，锁外执行 IO（建连/释放）。</para></summary>
public sealed class DataSessionPool<TProvider> : IAsyncDisposable
    where TProvider : IDbProvider
{
    private readonly DbOptions _options;
    private readonly int _maxIdleSize;
    private readonly Lock _sync = new();
    private readonly Queue<DataSession<TProvider>> _idle = new();
    private bool _disposed;

    private DataSessionPool(DbOptions options, int maxIdleSize)
    {
        _options = options;
        _maxIdleSize = maxIdleSize;
    }

    /// <summary>创建会话池。</summary>
    /// <param name="options">池基准配置——租期内 With* 覆写在归还时复位回此形态；
    /// 拦截器清单（如有）为池级共享：租期内 AddInterceptor 的加项不带入下一租期。</param>
    /// <param name="maxIdleSize">空闲上限（默认 16）：归还时空闲数已达上限的会话被真实释放
    /// （不放大物理连接占用）；租出无上限并发（不足时新建）。</param>
    public static DataSessionPool<TProvider> Create(DbOptions options, int maxIdleSize = 16)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIdleSize);
        options.Validate();
        return new DataSessionPool<TProvider>(options, maxIdleSize);
    }

    /// <summary>空闲会话数——诊断观测点（瞬时值）。</summary>
    public int IdleCount
    {
        get { lock (_sync) return _idle.Count; }
    }

    /// <summary>租出一个会话——空闲优先复用（连接非 Open 的闲置项丢弃重建：池化期间被
    /// 外部关闭的连接不进入租期）；无空闲则新建（含连接建立与初始化）。租出即用
    /// <c>await using</c> 消费，DisposeAsync 即归还。</summary>
    public async ValueTask<DataSession<TProvider>> RentAsync(CancellationToken ct = default)
    {
        DataSession<TProvider>? rented = null;
        List<DataSession<TProvider>>? discarded = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (_idle.Count > 0 && rented is null)
            {
                DataSession<TProvider> candidate = _idle.Dequeue();
                if (candidate.IsConnectionHealthyForRent)
                    rented = candidate;
                else
                    (discarded ??= []).Add(candidate);
            }
        }
        // 锁外释放被丢弃的闲置会话（健康校验失败的连接走真实释放）。加固（2026-10-13）：
        // 清理失败不得打穿租出方——连接已判死，其释放异常无诊断价值（DiscardReadConnectionAsync
        // 同口径静默），租出方不该为一个死资源的清理买单。
        if (discarded is not null)
        {
            foreach (DataSession<TProvider> dead in discarded)
            {
                try { await dead.TearDownFromPoolAsync().ConfigureAwait(false); }
                catch (Exception cleanupException) { _ = cleanupException; }
            }
        }
        if (rented is null)
        {
            rented = await DataSession<TProvider>.CreateAsync(_options, ct).ConfigureAwait(false);
            rented.MarkPooled(this);
        }
        try
        {
            await rented.PrepareForRentAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 连接态复位失败（连接已死/会话 SQL 不可重放）——丢弃该会话，不让坏连接进入租期。
            try { await rented.TearDownFromPoolAsync().ConfigureAwait(false); }
            catch (Exception cleanupException) { _ = cleanupException; }
            throw;
        }
        return rented;
    }

    /// <summary>归还入口（仅由 <see cref="DataSession{TProvider}.DisposeAsync"/> 的池化分支调用，
    /// 会话已 Park + 瞬态复位）。返回 false = 未接收（池满或池已释放），调用方走真实释放。</summary>
    internal bool TryReturn(DataSession<TProvider> session)
    {
        lock (_sync)
        {
            if (_disposed || _idle.Count >= _maxIdleSize)
                return false;
            _idle.Enqueue(session);
            return true;
        }
    }

    /// <summary>释放池：清空并真实释放全部空闲会话；已租出的会话在归还时发现池已释放，
    /// 自行真实释放（不悬空）。幂等。加固（2026-10-13）：逐会话尽力清理——单个会话释放
    /// 失败（主异常保留抛出）不中断其余会话的释放，否则一个死资源的清理失败会泄漏整池连接。</summary>
    public async ValueTask DisposeAsync()
    {
        List<DataSession<TProvider>> drain;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            drain = [.. _idle];
            _idle.Clear();
        }
        Exception? primaryException = null;
        int cleanupIndex = 0;
        foreach (DataSession<TProvider> session in drain)
        {
            try { await session.TearDownFromPoolAsync().ConfigureAwait(false); }
            catch (Exception cleanupException)
            {
                // 首个清理异常为 primaries（抛出），后续挂其 Data 不丢弃（仓库清理约定）
                if (primaryException is null)
                    primaryException = cleanupException;
                else
                    primaryException.Data[$"PalORM.PoolDisposeCleanupException{cleanupIndex}"] = cleanupException;
                cleanupIndex++;
            }
        }
        if (primaryException is not null)
            throw primaryException;
    }
}

/// <summary>池化会话的真实释放扩展（internal）：驻留态/空闲态的会话统一走
/// SessionOperationState.DisposeAsync 完整释放链（等待在飞 → 释放槽/连接）。</summary>
public sealed partial class DataSession<TProvider>
{
    internal async ValueTask TearDownFromPoolAsync()
        => await _operationState.DisposeAsync(DisposeCoreAsync, _interceptors.Count == 0).ConfigureAwait(false);
}
