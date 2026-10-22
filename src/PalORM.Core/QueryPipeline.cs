using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

namespace PalORM;

/// <summary>请求级只读查询管道——把多条彼此独立的查询合并为一次数据库往返。
/// <para><b>为什么需要</b>：远程数据库的请求延迟由往返次数主导。本机实测 PostgreSQL 4 条
/// 独立单键查询，顺序执行 232~340 µs/条，经 <c>NpgsqlBatch</c> 单次往返降到 73~118 µs/条
/// （约 3 倍）。批量写已有 <c>SessionBatch</c>，本类型补的是读侧且返回类型化结果。</para>
/// <para><b>语义契约</b>：① 只读（Query/QueryFirst/Count），写操作仍走 <c>ExecuteNonQueryAsync</c>/
/// <c>SessionBatch</c>；② 结果按入队顺序存放，<c>ExecuteAsync</c> 完成后经各句柄的
/// <c>Value</c> 读取；③ 每个 builder 只允许入队一次，入队即视为被消费（参数对象会挂到批命令上，
/// 不得再执行同一 builder）；④ 活动事务绑定到整批；⑤ 默认过滤/租户条件随 builder 的
/// SQL 一起进入批命令；⑥ 拦截器按逻辑查询逐个通知（OnBefore 在执行前一次、OnAfter 携带该
/// 结果集行数、OnError 覆盖整批失败）。</para>
/// <para><b>能力门禁</b>：Provider 未声明 <see cref="IDbProvider.SupportsQueryPipeline"/> 时回退
/// 顺序执行（SQLite 当前即此路径，语义一致、无往返收益）。<c>WithCache</c>/<c>AsPrepared</c>/
/// <c>WithTracing</c>/<c>WithMetrics</c>/<c>ForRead</c>/builder 级 <c>WithTransaction</c> 在管道内
/// 不受支持——入队时明确拒绝，避免静默丢弃。</para>
/// <para><b>线程安全</b>：实例非线程安全（入队/执行/释放应在单异步流上顺序进行）。</para></summary>
/// <typeparam name="TProvider">数据库 Provider 类型。</typeparam>
public sealed class QueryPipeline<TProvider> : IDisposable
    where TProvider : IDbProvider
{
    private readonly DataSession<TProvider> _session;
    private readonly List<PipelineItem> _items = [];
    private object?[] _results = [];
    private bool _executed;
    private bool _disposed;

    internal QueryPipeline(DataSession<TProvider> session) => _session = session;

    /// <summary>入队一条返回实体列表的查询。</summary>
    /// <typeparam name="T">实体或投影类型。</typeparam>
    /// <param name="builder">由 <c>From&lt;T&gt;()</c> 创建的查询构建器（入队后不得再次执行）。</param>
    /// <returns>执行完成前为未就绪状态的类型化结果句柄。</returns>
    public PipelineQuery<T, TProvider> Query<T>(QueryBuilder<T> builder)
        where T : class, new()
    {
        int index = Add(builder, PipelineKind.List);
        return new PipelineQuery<T, TProvider>(this, index);
    }

    /// <summary>入队一条只取首行的查询（SQL 自动追加 <c>Take(1)</c>，避免服务端多传行）。</summary>
    /// <typeparam name="T">实体或投影类型。</typeparam>
    /// <param name="builder">由 <c>From&lt;T&gt;()</c> 创建的查询构建器（入队后不得再次执行）。</param>
    /// <returns>执行完成前为未就绪状态的类型化结果句柄；无行时 <c>Value</c> 为 null。</returns>
    public PipelineFirst<T, TProvider> QueryFirst<T>(QueryBuilder<T> builder)
        where T : class, new()
    {
        QueryBuilder<T> limited = builder.Take(1);
        int index = Add(limited, PipelineKind.First);
        return new PipelineFirst<T, TProvider>(this, index);
    }

    /// <summary>入队一条 COUNT 查询（复用 builder 的过滤/分组/Join 语义）。</summary>
    /// <typeparam name="T">实体或投影类型。</typeparam>
    /// <param name="builder">由 <c>From&lt;T&gt;()</c> 创建的查询构建器（入队后不得再次执行）。</param>
    /// <returns>执行完成前为未就绪状态的计数句柄。</returns>
    public PipelineScalar<TProvider> Count<T>(QueryBuilder<T> builder)
        where T : class, new()
    {
        int index = Add(builder, PipelineKind.Count);
        return new PipelineScalar<TProvider>(this, index);
    }

    /// <summary>执行全部已入队的查询。只能调用一次；未入队任何查询时为空操作。</summary>
    /// <param name="ct">取消令牌（整批生效）。</param>
    /// <exception cref="InvalidOperationException">重复执行、与并行读作用域重叠或 Provider
    /// 批处理返回的结果集数量不足。</exception>
    /// <exception cref="ObjectDisposedException">管道已释放。</exception>
    public async ValueTask ExecuteAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_executed)
            throw new InvalidOperationException("This query pipeline has already been executed.");
        _executed = true;
        if (_items.Count == 0)
            return;
        if (_session.PipelineParallelReadScopeActive)
            throw new InvalidOperationException(
                "A query pipeline cannot run inside a parallel-read scope; complete that scope first.");

        using SessionOperationState.SessionOperationLease lease = _session.EnterPipelineRead();
        DbTransaction? transaction = _session.PipelineTransaction;
        _results = new object?[_items.Count];
        long? stopwatchStart = HasInterceptors() ? Stopwatch.GetTimestamp() : null;
        try
        {
            if (TProvider.SupportsQueryPipeline)
                await ExecuteBatchedAsync(transaction, stopwatchStart, ct).ConfigureAwait(false);
            else
                await ExecuteSequentialAsync(transaction, stopwatchStart, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            foreach (PipelineItem item in _items)
                QueryBuilderExtensions.NotifyInterceptorsOnError(item.Interceptors, item.Context, exception);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _items.Clear();
        _results = [];
    }

    /// <summary>执行已完成的结果取用（句柄内部使用）。</summary>
    internal object? GetResult(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_executed)
            throw new InvalidOperationException(
                "ExecuteAsync must complete before reading pipeline results.");
        return _results[index];
    }

    private bool HasInterceptors()
    {
        foreach (PipelineItem item in _items)
        {
            if (item.Interceptors.Count > 0)
                return true;
        }
        return false;
    }

    private int Add<T>(QueryBuilder<T> builder, PipelineKind kind)
        where T : class, new()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_executed)
            throw new InvalidOperationException(
                "Queries cannot be added after ExecuteAsync has been called.");
        if (builder._cacheKey is not null)
            throw new NotSupportedException("WithCache is not supported inside a query pipeline.");
        if (builder._prepared)
            throw new NotSupportedException("AsPrepared is not supported inside a query pipeline.");
        if (builder._tracing || builder._metrics)
            throw new NotSupportedException(
                "WithTracing/WithMetrics are not supported inside a query pipeline; observe the batch instead.");
        if (builder._useReadRoute)
            throw new NotSupportedException("ForRead is not supported inside a query pipeline.");
        if (builder._transaction is not null)
            throw new NotSupportedException(
                "Builder-level WithTransaction is not supported inside a query pipeline; use the session transaction.");
        if (TProvider.SupportsQueryPipeline && _items.Count >= TProvider.MaxPipelineCommands)
            throw new InvalidOperationException(
                $"A query pipeline supports at most {TProvider.MaxPipelineCommands} commands for this provider.");

        string sql = kind == PipelineKind.Count ? builder.BuildCountSql() : builder.BuildSql();
        IReadOnlyList<DbParameter> parameters =
            kind == PipelineKind.Count ? builder.GetCountParameters() : builder.GetQueryParameters();
        var context = new QueryContext(sql, parameters, builder._sensitiveMasks);
        PipelineItem item = kind switch
        {
            PipelineKind.List => new PipelineList<T>(builder._factory, _session.PreferSynchronousRowRead),
            PipelineKind.First => new PipelineFirst<T>(builder._factory),
            _ => new PipelineCount(),
        };
        item.Sql = sql;
        item.Parameters = parameters;
        item.Context = context;
        item.Interceptors = builder._interceptors;
        item.Timeout = builder._commandTimeout;
        _items.Add(item);
        return _items.Count - 1;
    }

    private async ValueTask ExecuteBatchedAsync(
        DbTransaction? transaction, long? stopwatchStart, CancellationToken ct)
    {
        DbConnection connection = _session.PipelineConnection;
        await using DbBatch batch = connection.CreateBatch();
        batch.Transaction = transaction;
        batch.Timeout = DbOptions.ToCommandTimeoutSeconds(ResolveBatchTimeout());
        foreach (PipelineItem item in _items)
        {
            DbBatchCommand command = batch.CreateBatchCommand();
            command.CommandText = item.Sql;
            AddParameters(command, item.Parameters);
            batch.BatchCommands.Add(command);
            QueryBuilderExtensions.NotifyInterceptorsOnBefore(item.Interceptors, item.Context);
        }

        await using DbDataReader reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
        for (int index = 0; index < _items.Count; index++)
        {
            if (index > 0 && !await reader.NextResultAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Query pipeline batch returned only {index} result set(s) for {_items.Count} command(s).");
            }
            PipelineItem item = _items[index];
            PipelineMaterialization materialization =
                await item.MaterializeAsync(reader, ct).ConfigureAwait(false);
            _results[index] = materialization.Payload;
            QueryBuilderExtensions.NotifyInterceptorsOnAfter(
                item.Interceptors, item.Context, stopwatchStart, materialization.Rows);
        }
    }

    private async ValueTask ExecuteSequentialAsync(
        DbTransaction? transaction, long? stopwatchStart, CancellationToken ct)
    {
        for (int index = 0; index < _items.Count; index++)
        {
            PipelineItem item = _items[index];
            await using DbCommand command = _session.CreatePipelineCommand();
            command.CommandText = item.Sql;
            command.Transaction = transaction;
            command.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(item.Timeout);
            foreach (DbParameter parameter in item.Parameters)
                command.Parameters.Add(parameter);
            QueryBuilderExtensions.NotifyInterceptorsOnBefore(item.Interceptors, item.Context);
            await using DbDataReader reader =
                await DataSession<TProvider>.ExecuteReaderAsync(command, ct).ConfigureAwait(false);
            PipelineMaterialization materialization =
                await item.MaterializeAsync(reader, ct).ConfigureAwait(false);
            _results[index] = materialization.Payload;
            QueryBuilderExtensions.NotifyInterceptorsOnAfter(
                item.Interceptors, item.Context, stopwatchStart, materialization.Rows);
        }
    }

    /// <summary>整批超时取各逻辑查询请求值与会话默认值的最大者（批级只有一个超时面）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S3267:LoopsShouldBeSimplifiedWithLinq",
        Justification = "批级超时聚合在每次管道执行上：LINQ Max 每次分配委托与迭代器，手写循环零分配。")]
    private TimeSpan ResolveBatchTimeout()
    {
        TimeSpan timeout = _session.PipelineCommandTimeout;
        foreach (PipelineItem item in _items)
        {
            if (item.Timeout > timeout)
                timeout = item.Timeout;
        }
        return timeout;
    }

    /// <summary>批命令参数直接挂接原参数实例——builder 入队即视为被消费（见类型文档契约③）。
    /// 不克隆：Npgsql 的数组参数类型信息（<c>NpgsqlDbType.Array | …</c>）不在 <see cref="DbParameter"/>
    /// 公共面上，克隆/属性复制会丢失它。</summary>
    private static void AddParameters(DbBatchCommand command, IReadOnlyList<DbParameter> parameters)
    {
        for (int index = 0; index < parameters.Count; index++)
            command.Parameters.Add(parameters[index]);
    }

    private enum PipelineKind
    {
        List,
        First,
        Count,
    }

    private abstract class PipelineItem
    {
        internal string Sql = "";
        internal IReadOnlyList<DbParameter> Parameters = [];
        internal QueryContext Context;
        internal List<IQueryInterceptor> Interceptors = [];
        internal TimeSpan Timeout;
        internal abstract ValueTask<PipelineMaterialization> MaterializeAsync(
            DbDataReader reader, CancellationToken ct);
    }

    private sealed class PipelineList<T>(Func<DbDataReader, T> factory, bool syncRead) : PipelineItem
        where T : class, new()
    {
        internal override async ValueTask<PipelineMaterialization> MaterializeAsync(
            DbDataReader reader, CancellationToken ct)
        {
            List<T> list = await ResultListReader.ReadAllAsync(
                reader, factory, 16, ct, syncRead: syncRead).ConfigureAwait(false);
            return new PipelineMaterialization(list, list.Count);
        }
    }

    private sealed class PipelineFirst<T>(Func<DbDataReader, T> factory) : PipelineItem
        where T : class, new()
    {
        internal override async ValueTask<PipelineMaterialization> MaterializeAsync(
            DbDataReader reader, CancellationToken ct)
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return new PipelineMaterialization(null, 0);
            return new PipelineMaterialization(factory(reader), 1);
        }
    }

    private sealed class PipelineCount : PipelineItem
    {
        internal override async ValueTask<PipelineMaterialization> MaterializeAsync(
            DbDataReader reader, CancellationToken ct)
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return new PipelineMaterialization(0L, 0);
            object value = reader.GetValue(0);
            long count = value is long result
                ? result
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return new PipelineMaterialization(count, 1);
        }
    }

    private readonly record struct PipelineMaterialization(object? Payload, int Rows);
}

/// <summary>管道中"实体列表"查询的结果句柄——<c>ExecuteAsync</c> 完成后读 <see cref="Value"/>。</summary>
/// <typeparam name="T">实体或投影类型。</typeparam>
/// <typeparam name="TProvider">数据库 Provider 类型。</typeparam>
public readonly struct PipelineQuery<T, TProvider>
    where T : class, new()
    where TProvider : IDbProvider
{
    private readonly QueryPipeline<TProvider>? _pipeline;
    private readonly int _index;

    internal PipelineQuery(QueryPipeline<TProvider> pipeline, int index)
    {
        _pipeline = pipeline;
        _index = index;
    }

    /// <summary>查询结果列表（执行完成前访问会抛 <see cref="InvalidOperationException"/>）。</summary>
    public List<T> Value => _pipeline is null
        ? throw new InvalidOperationException("This pipeline result handle is uninitialized.")
        : (List<T>)_pipeline.GetResult(_index)!;
}

/// <summary>管道中"首行"查询的结果句柄——<c>ExecuteAsync</c> 完成后读 <see cref="Value"/>。</summary>
/// <typeparam name="T">实体或投影类型。</typeparam>
/// <typeparam name="TProvider">数据库 Provider 类型。</typeparam>
public readonly struct PipelineFirst<T, TProvider>
    where T : class, new()
    where TProvider : IDbProvider
{
    private readonly QueryPipeline<TProvider>? _pipeline;
    private readonly int _index;

    internal PipelineFirst(QueryPipeline<TProvider> pipeline, int index)
    {
        _pipeline = pipeline;
        _index = index;
    }

    /// <summary>首行实体；无行时为 null（执行完成前访问会抛 <see cref="InvalidOperationException"/>）。</summary>
    public T? Value => _pipeline is null
        ? throw new InvalidOperationException("This pipeline result handle is uninitialized.")
        : _pipeline.GetResult(_index) as T;
}

/// <summary>管道中 COUNT 查询的结果句柄——<c>ExecuteAsync</c> 完成后读 <see cref="Value"/>。</summary>
/// <typeparam name="TProvider">数据库 Provider 类型。</typeparam>
public readonly struct PipelineScalar<TProvider>
    where TProvider : IDbProvider
{
    private readonly QueryPipeline<TProvider>? _pipeline;
    private readonly int _index;

    internal PipelineScalar(QueryPipeline<TProvider> pipeline, int index)
    {
        _pipeline = pipeline;
        _index = index;
    }

    /// <summary>计数值（执行完成前访问会抛 <see cref="InvalidOperationException"/>）。</summary>
    public long Value => _pipeline is null
        ? throw new InvalidOperationException("This pipeline result handle is uninitialized.")
        : (long)_pipeline.GetResult(_index)!;
}
