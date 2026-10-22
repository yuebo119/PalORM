using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;

namespace PalORM;

/// <summary>QueryBuilder 执行扩展方法——从 struct 分离避免装箱。</summary>
public static class QueryBuilderExtensions
{
    /// <summary>结果列表的预分配容量上限（ITM-712）。Take/分页大小是查询结果上界而非预期行数，
    /// 无上限的预分配可被单个超大值放大为进程级 OOM；超出部分由 <see cref="ResultListReader"/> 池化收集。</summary>
    private const int MaxPreallocatedCapacity = 4096;

    /// <summary>实际缓存 key 组装（ADR-L 结构性隔离）：租户作用域非空时前缀化——
    /// <c>__t:{tenantId}:{userKey}</c>（租户过滤查询）或 <c>__all__:{userKey}</c>（多租户会话的
    /// IgnoreFilters / 非 TenantAware 实体，全量数据独立命名空间）；单租户会话（作用域 null）
    /// key 原样。调用方仍按自己的 userKey 推理缓存，前缀是框架内部命名空间。</summary>
    private static string EffectiveCacheKey<T>(in QueryBuilder<T> builder) where T : class, new()
        => builder._cacheTenantScope is { } scope
            ? $"{scope}:{builder._cacheKey}"
            : builder._cacheKey!;

    /// <summary>S4：查询管线的显式执行状态——只携带真正需要的字段，避免整只 QueryBuilder
    /// 被本地函数闭包与异步状态机捕获。连接归还在每次尝试的 finally 内完成（重试中间连接
    /// 立即归还，不再等到操作结束）。</summary>
    internal readonly record struct QueryExecutionState<T>(
        DbConnection PrimaryConnection,
        bool UseReadRoute,
        SessionOperationState OperationState,
        Func<CancellationToken, ValueTask<DbConnection>>? ReadConnProvider,
        ReusableQuerySlot? QuerySlot,
        Func<DbDataReader, T> Factory,
        int? Take,
        bool Prepared,
        TimeSpan CommandTimeout,
        IQueryCache QueryCache,
        string? CacheKey,
        TimeSpan? CacheTtl,
        string? CacheTenantScope,
        string Sql,
        IReadOnlyList<DbParameter> Parameters,
        QueryContext Context,
        List<IQueryInterceptor> Interceptors,
        long? SwStart,
        DbTransaction? BoundTransaction,
        Func<DbConnection, bool, ValueTask>? ReadConnReturner) where T : class, new();

    /// <summary>S4：弹性编排的只读上下文（从 builder 一次投影，helper 状态机不再持有 builder）。</summary>
    internal readonly record struct ResilienceContext(
        bool Configured,
        bool DirectFirst,
        ResilienceExecutor? Existing,
        IQuerySessionResilience? Session,
        int Generation,
        DbOptions? OptionsSnapshot,
        Func<Exception, bool>? IsTransient,
        Type? ProviderType)
    {
        internal ResilienceExecutor Resolve()
        {
            if (Existing is not null) return Existing;
            if (Session is { } session && session.ResilienceGeneration == Generation)
                return session.GetResilienceExecutor();
            if (OptionsSnapshot is not null && IsTransient is not null)
                return new ResilienceExecutor(OptionsSnapshot, IsTransient, ProviderType);
            return Session!.GetResilienceExecutor();
        }

        internal bool IsTransientException(Exception exception)
            => IsTransient?.Invoke(exception) ?? Existing?.IsTransient(exception) ?? false;
    }

    /// <summary>S4：弹性编排（首发直路 + 失败后续跑）。core 是 static 方法组，状态按值传入，
    /// 调用点不产生闭包；builder 不进入本方法的状态机。</summary>
    private static async ValueTask<TResult> ExecuteWithResilienceAsync<TState, TResult>(
        ResilienceContext context, Func<TState, CancellationToken, Task<TResult>> core,
        TState state, DbTransaction? boundTransaction, CancellationToken ct)
    {
        if (boundTransaction is not null || !context.Configured)
            return await core(state, ct).ConfigureAwait(false);
        ResilienceExecutor? executor = context.Existing;
        if (executor is null && context.DirectFirst)
        {
            try
            {
                return await core(state, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && context.IsTransientException(ex))
            {
                executor = context.Resolve();
                if (executor.CanRetry)
                {
                    return await executor.ExecuteAfterFirstFailureAsync(core, state, ct)
                        .ConfigureAwait(false);
                }
                executor.RecordDirectFailure(ex);
                throw;
            }
        }
        executor ??= context.Resolve();
        if (executor.IsPassThrough)
            return await core(state, ct).ConfigureAwait(false);
        return await executor.ExecuteAsync(core, state, ct).ConfigureAwait(false);
    }

    /// <summary>S4：连接获取（与 QueryBuilder.AcquireExecutionConnectionAsync 同语义，
    /// 但只依赖状态字段，不捕获 builder）。</summary>
    private static ValueTask<DbConnection> AcquireExecutionConnectionAsync<T>(
        in QueryExecutionState<T> state, CancellationToken token) where T : class, new()
        => state.BoundTransaction is not null
            || (!state.UseReadRoute && !state.OperationState.ParallelReadsEnabled)
            ? ValueTask.FromResult(state.PrimaryConnection)
            : state.ReadConnProvider!(token);

    /// <summary>S4：ToList 单次尝试内核——复用命令、物化、缓存写入、连接归还。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "单次尝试内核的复用/新建命令分支、参数绑定、物化、缓存与连接归还为顺序语义；"
            + "拆分需跨方法传递命令所有权与连接健康标记，状态面更大。")]
    private static async Task<List<T>> ExecuteListCoreAsync<T>(
        QueryExecutionState<T> state, string? effectiveCacheKey, CancellationToken token)
        where T : class, new()
    {
        DbConnection connection = await AcquireExecutionConnectionAsync(state, token).ConfigureAwait(false);
        bool healthy = false;
        DbCommand? ownedCommand = null;
        try
        {
            DbCommand? reusable = state.QuerySlot is not null && !state.OperationState.ParallelReadsEnabled
                ? state.QuerySlot.TryAcquire(connection, state.Sql)
                : null;
            DbCommand cmd;
            bool parametersBound;
            bool usingReusable;
            if (reusable is null
                || (reusable.Parameters.Count > 0 && !TryCopyParameterValues(reusable, state.Parameters)))
            {
                cmd = connection.CreateCommand();
                ownedCommand = cmd;
                parametersBound = false;
                usingReusable = false;
            }
            else
            {
                cmd = reusable;
                parametersBound = reusable.Parameters.Count > 0;
                usingReusable = true;
            }

            if (!string.Equals(cmd.CommandText, state.Sql, StringComparison.Ordinal))
                cmd.CommandText = state.Sql;
            cmd.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(state.CommandTimeout);
            cmd.Transaction = state.BoundTransaction;
            if (!parametersBound) AddParameters(cmd, state.Parameters);
            await EnsureReusablePreparedAsync(state.QuerySlot, usingReusable, token).ConfigureAwait(false);
            NotifyInterceptorsOnBefore(state.Interceptors, state.Context);
            await PrepareCommandAsync(cmd, state.Prepared, token).ConfigureAwait(false);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            List<T> list = await ResultListReader.ReadAllAsync(
                reader, state.Factory,
                state.Take.HasValue ? Math.Min(state.Take.Value, MaxPreallocatedCapacity) : 16,
                token).ConfigureAwait(false);
            NotifyInterceptorsOnAfter(state.Interceptors, state.Context, state.SwStart, list.Count);
            string? key = effectiveCacheKey;
            if (key is null && state.CacheKey is not null)
            {
                key = state.CacheTenantScope is { } scope
                    ? $"{scope}:{state.CacheKey}"
                    : state.CacheKey;
            }
            if (key is not null)
                state.QueryCache.Set(key, new List<T>(list), state.CacheTtl);
            healthy = true;
            return list;
        }
        finally
        {
            if (ownedCommand is not null)
                await ownedCommand.DisposeAsync().ConfigureAwait(false);
            if (state.ReadConnReturner is not null)
                await state.ReadConnReturner(connection, healthy).ConfigureAwait(false);
        }
    }

    /// <summary>S4：ForEach 单次尝试内核——不物化列表，逐行回调。</summary>
    private static async Task<long> ExecuteForEachCoreAsync<T>(
        QueryExecutionState<T> state, Func<T, CancellationToken, ValueTask> action, CancellationToken token)
        where T : class, new()
    {
        DbConnection connection = await AcquireExecutionConnectionAsync(state, token).ConfigureAwait(false);
        bool healthy = false;
        DbCommand? ownedCommand = null;
        try
        {
            DbCommand cmd = connection.CreateCommand();
            ownedCommand = cmd;
            cmd.CommandText = state.Sql;
            cmd.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(state.CommandTimeout);
            cmd.Transaction = state.BoundTransaction;
            AddParameters(cmd, state.Parameters);
            NotifyInterceptorsOnBefore(state.Interceptors, state.Context);
            await PrepareCommandAsync(cmd, state.Prepared, token).ConfigureAwait(false);
            long rowCount = 0;
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                await action(state.Factory(reader), token).ConfigureAwait(false);
                rowCount++;
            }
            NotifyInterceptorsOnAfter(state.Interceptors, state.Context, state.SwStart, (int)rowCount);
            healthy = true;
            return rowCount;
        }
        finally
        {
            if (ownedCommand is not null)
                await ownedCommand.DisposeAsync().ConfigureAwait(false);
            if (state.ReadConnReturner is not null)
                await state.ReadConnReturner(connection, healthy).ConfigureAwait(false);
        }
    }

    /// <summary>S4：First/Single 单次尝试内核——最多 maxRows 行直读，不建 List。</summary>
    private static async Task<(T? Row, int Rows)> ExecuteRowsCoreAsync<T>(
        QueryExecutionState<T> state, int maxRows, CancellationToken token)
        where T : class, new()
    {
        DbConnection connection = await AcquireExecutionConnectionAsync(state, token).ConfigureAwait(false);
        bool healthy = false;
        DbCommand? ownedCommand = null;
        try
        {
            DbCommand? reusable = state.QuerySlot is not null && !state.OperationState.ParallelReadsEnabled
                ? state.QuerySlot.TryAcquire(connection, state.Sql)
                : null;
            DbCommand cmd;
            bool parametersBound;
            bool usingReusable;
            if (reusable is null
                || (reusable.Parameters.Count > 0 && !TryCopyParameterValues(reusable, state.Parameters)))
            {
                cmd = connection.CreateCommand();
                ownedCommand = cmd;
                parametersBound = false;
                usingReusable = false;
            }
            else
            {
                cmd = reusable;
                parametersBound = reusable.Parameters.Count > 0;
                usingReusable = true;
            }

            if (!string.Equals(cmd.CommandText, state.Sql, StringComparison.Ordinal))
                cmd.CommandText = state.Sql;
            cmd.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(state.CommandTimeout);
            cmd.Transaction = state.BoundTransaction;
            if (!parametersBound) AddParameters(cmd, state.Parameters);
            await EnsureReusablePreparedAsync(state.QuerySlot, usingReusable, token).ConfigureAwait(false);
            NotifyInterceptorsOnBefore(state.Interceptors, state.Context);
            await PrepareCommandAsync(cmd, state.Prepared, token).ConfigureAwait(false);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            T? first = null;
            int rows = 0;
            while (rows < maxRows && await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (rows == 0)
                    first = state.Factory(reader);
                rows++;
            }
            NotifyInterceptorsOnAfter(state.Interceptors, state.Context, state.SwStart, rows);
            healthy = true;
            return (first, rows);
        }
        finally
        {
            if (ownedCommand is not null)
                await ownedCommand.DisposeAsync().ConfigureAwait(false);
            if (state.ReadConnReturner is not null)
                await state.ReadConnReturner(connection, healthy).ConfigureAwait(false);
        }
    }

    /// <summary>复用命令晋升后的首次执行前预编译（条件内聚到本助手，调用点不新增分支）。</summary>
    private static ValueTask EnsureReusablePreparedAsync(
        ReusableQuerySlot? slot, bool usingReusable, CancellationToken token)
        => usingReusable && slot is not null ? slot.EnsurePreparedAsync(token) : default;

    /// <summary>执行查询并返回全部实体列表。
    /// <para>配置 <c>WithCache</c> 时先查缓存：命中返回新 List，但元素是共享实体实例（浅拷贝契约，见 WithCache 文档）；
    /// 未命中则执行查询并将副本写入缓存。</para></summary>
    public static ValueTask<List<T>> ToListAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        // R4（2026-10-12）：读预热（写路径 step16-P1 同型）——Enter 的 AsyncLocal 写落在
        // 调用方持久 EC，体内守卫命中免写（每读省一次 EC 拷贝，探针 readprewarm A/B -72B/op）
        if (builder._operationState.CompletedOps >= SessionOperationState.PrewarmThreshold)
            builder._operationState.PrewarmCurrentOperationOwner();
        return ToListEntryAsync(builder, ct);
    }

    private static async ValueTask<List<T>> ToListEntryAsync<T>(QueryBuilder<T> builder, CancellationToken ct) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operationLease =
            builder._operationState.EnterReadOnly();
        // 缓存命中返回列表副本——List 本身隔离，但元素是共享实体实例（浅拷贝，ITM-308）：
        // 调用方修改命中实体会污染缓存与其他调用方。契约声明见 WithCache 文档。
        // ADR-L：实际 key 经租户作用域前缀组装（跨租户命中结构性不可能）。
        // A8（2026-10-01 全 API 逐项轮）：key 单次组装贯穿 TryGet 与 Set——原实现未命中
        // 路径（TryGet 一次 + Set 一次）在租户会话下每次查询拼两次前缀串。
        string? effectiveCacheKey = builder._cacheKey is not null ? EffectiveCacheKey(builder) : null;
        if (effectiveCacheKey is not null
            && builder._queryCache.TryGet(effectiveCacheKey, out List<T>? cached) && cached is not null)
            return new List<T>(cached);

        return await ExecuteQueryAsync(
            builder, ct, operationLease.Owner, effectiveCacheKey).ConfigureAwait(false);
    }

    /// <summary>流式消费查询结果——每行经回调处理，<b>不物化列表</b>。
    /// <para><b>与 ToListAsync 的取舍</b>：大结果集（10 万行级）下省去整表 List 分配与
    /// 实体存活内存（实测 100K 行：分配 15.5MB/存活 10.8MB → 回调形态仅剩每行实体本身）；
    /// 小结果集二者等价。回调逐行同步执行——回调里的 DB 调用会触发 PALORM005（那是正确的）。</para>
    /// <para><b>语义契约</b>：① 不写 <c>WithCache</c> 缓存（流式结果没有可缓存的列表，
    /// 与 First 族的截断防护同理）；② 拦截器语义与 ToListAsync 一致——OnBefore 按尝试触发、
    /// OnAfter 携带实际行数、OnError 通知；③ 弹性管线覆盖与 ToListAsync 同口径
    /// （无事务、非直通）；④ 回调抛出的异常原样上抛并终止枚举（reader/连接经 using 释放）。</para></summary>
    public static async ValueTask<long> ForEachAsync<T>(
        this QueryBuilder<T> builder,
        Func<T, CancellationToken, ValueTask> action,
        CancellationToken ct = default) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(action);
        using SessionOperationState.SessionOperationLease operationLease =
            builder._operationState.EnterReadOnly();
        return await ExecuteForEachAsync(builder, action, ct, operationLease.Owner).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "与 ExecuteQueryAsync 同构的管线形态（执行/拦截器/观测性三段式）——"
            + "流式变体必须保持同一语义结构，抽公共化会为两处调用各引入一层间接。")]
    private static async ValueTask<long> ExecuteForEachAsync<T>(
        QueryBuilder<T> builder,
        Func<T, CancellationToken, ValueTask> action,
        CancellationToken ct,
        object? operationOwner = null) where T : class, new()
    {
        if (builder._selectColumns is not null)
            throw new NotSupportedException(
                "Partial Select projection is not supported for entity queries; use the full entity query or an explicit QueryAsync projection type.");
        string sql = builder.BuildSql();
        IReadOnlyList<DbParameter> parameters = builder.GetQueryParameters();
        var context = new QueryContext(sql, parameters, builder._sensitiveMasks);
        const string operation = "select";
        string provider = builder._dialect.GetName();
        bool observed = builder._tracing || builder._metrics;
        Activity? activity = builder._tracing ? PalORMMetrics.StartActivity(operation, provider) : null;
        List<IQueryInterceptor> interceptors = builder._interceptors;
        bool needStopwatch = observed || interceptors.Count > 0;
        long? swStart = needStopwatch ? Stopwatch.GetTimestamp() : null;
        SessionOperationState operationState = builder._operationState;
        DbTransaction? boundTransaction = builder.GetActiveTransaction();
        bool metrics = builder._metrics;
        string? metricsName = builder._metricsName;
        Func<ValueTask>? readConnInvalidator = builder._readConnInvalidator;
        ResilienceContext resilience = builder.CreateResilienceContext();
        var state = new QueryExecutionState<T>(
            builder._conn, builder._useReadRoute, operationState,
            builder._readConnProvider, builder._querySlot, builder._factory,
            builder._take, builder._prepared, builder._commandTimeout, builder._queryCache,
            builder._cacheKey, builder._cacheTtl, builder._cacheTenantScope,
            sql, parameters, context, interceptors, swStart, boundTransaction,
            builder._readConnReturner);
        string outcome = "error";
        try
        {
            // ITM-811（r22 登记，r23 实修）：内层租约与 ExecuteQueryAsync 同型走 EnterReadOnly
            // ——作用域内复用外层读租约（SessionOperationState 的重入不双计语义），不再
            // 以独占 Enter(null) 击穿（原形态使 ForParallelReads 作用域内 ForEachAsync
            // 结构性必抛，AuditProbeTests.Probe1 锁定）。
            using SessionOperationState.SessionOperationLease operationStateLease =
                operationState.EnterReadOnly(operationOwner);
            long count = await ExecuteWithResilienceAsync(
                resilience,
                static (s, token) => ExecuteForEachCoreAsync(s.State, s.Action, token),
                (State: state, Action: action),
                boundTransaction,
                ct).ConfigureAwait(false);
            outcome = "success";
            return count;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                outcome = "cancelled";
            // READ-001（2026-09-23）：读路由下瞬时故障即丢弃会话缓存的读连接。
            if (readConnInvalidator is not null && resilience.IsTransientException(exception))
                await readConnInvalidator().ConfigureAwait(false);
            NotifyInterceptorsOnError(interceptors, context, exception);
            throw;
        }
        finally
        {
            PalORMMetrics.CompleteActivity(activity, outcome);
            if (metrics && swStart is { } metricStart)
                PalORMMetrics.Record(operation, provider, outcome, Stopwatch.GetElapsedTime(metricStart), metricsName);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "查询执行管线的 try/catch/finally 三段式（执行/拦截器错误通知/观测性收尾）"
            + "是异步 IO 资源管理的必然形态。已抽出 NotifyInterceptorsOnError，余下分支是观测性收尾本身。")]
    private static async ValueTask<List<T>> ExecuteQueryAsync<T>(
        QueryBuilder<T> builder,
        CancellationToken ct,
        object? operationOwner = null,
        string? effectiveCacheKey = null) where T : class, new()
    {
        if (builder._selectColumns is not null)
            throw new NotSupportedException(
                "Partial Select projection is not supported for entity queries; use the full entity query or an explicit QueryAsync projection type.");
        string sql = builder.BuildSql();
        IReadOnlyList<DbParameter> parameters = builder.GetQueryParameters();
        var context = new QueryContext(sql, parameters, builder._sensitiveMasks);
        const string operation = "select";
        string provider = builder._dialect.GetName();
        bool observed = builder._tracing || builder._metrics;
        Activity? activity = builder._tracing ? PalORMMetrics.StartActivity(operation, provider) : null;
        List<IQueryInterceptor> interceptors = builder._interceptors;
        bool needStopwatch = observed || interceptors.Count > 0;
        long? swStart = needStopwatch ? Stopwatch.GetTimestamp() : null;
        SessionOperationState operationState = builder._operationState;
        DbTransaction? boundTransaction = builder.GetActiveTransaction();
        bool metrics = builder._metrics;
        string? metricsName = builder._metricsName;
        Func<ValueTask>? readConnInvalidator = builder._readConnInvalidator;
        ResilienceContext resilience = builder.CreateResilienceContext();
        var state = new QueryExecutionState<T>(
            builder._conn, builder._useReadRoute, operationState,
            builder._readConnProvider, builder._querySlot, builder._factory,
            builder._take, builder._prepared, builder._commandTimeout, builder._queryCache,
            builder._cacheKey, builder._cacheTtl, builder._cacheTenantScope,
            sql, parameters, context, interceptors, swStart, boundTransaction,
            builder._readConnReturner);
        string outcome = "error";
        try
        {
            using SessionOperationState.SessionOperationLease operationLease =
                operationState.EnterReadOnly(operationOwner);
            // 操作门禁横跨全部重试尝试持有——一次用户操作仍是一次门禁占用。
            List<T> list = await ExecuteWithResilienceAsync(
                resilience,
                static (s, token) => ExecuteListCoreAsync(s.State, s.CacheKey, token),
                (State: state, CacheKey: effectiveCacheKey),
                boundTransaction,
                ct).ConfigureAwait(false);
            outcome = "success";
            return list;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                outcome = "cancelled";
            // READ-001（2026-09-23）：读路由下瞬时故障即丢弃会话缓存的读连接。
            if (readConnInvalidator is not null && resilience.IsTransientException(exception))
                await readConnInvalidator().ConfigureAwait(false);
            NotifyInterceptorsOnError(interceptors, context, exception);
            throw;
        }
        finally
        {
            PalORMMetrics.CompleteActivity(activity, outcome);
            if (metrics && swStart is { } metricStart)
                PalORMMetrics.Record(operation, provider, outcome, Stopwatch.GetElapsedTime(metricStart), metricsName);
        }
    }

    /// <summary>通知所有拦截器 OnError——单个拦截器抛出的异常被吞掉，
    /// 不覆盖原始执行异常、不阻断其他拦截器或后续资源清理；
    /// 吞掉的同时计入 <see cref="PalORMMetrics.InterceptorOnErrorFailures"/>（审计 ERR-010：
    /// 原"零痕迹吞"使审计拦截器自身 bug 永不可见）。internal 供契约测试直调。</summary>
    internal static void NotifyInterceptorsOnError(
        List<IQueryInterceptor> interceptors, QueryContext context, Exception exception)
    {
        // v3.1：默认会话无拦截器——空列表直接返回，避免 foreach 迭代与方法调用开销。
        if (interceptors.Count == 0) return;
        foreach (IQueryInterceptor interceptor in interceptors)
        {
            try { interceptor.OnError(context, exception); }
            catch { PalORMMetrics.RecordInterceptorOnErrorFailure(); }
        }
    }

    /// <summary>触发所有拦截器的 OnBefore——v3.1 抽出辅助，让 SELECT/UPDATE 管线共用并保留"空列表跳过"优化。
    /// R3（v5.6.0）改 internal：DataSession.ExecuteAsync（原始 DDL/DML）接入同一三段式。</summary>
    internal static void NotifyInterceptorsOnBefore(
        List<IQueryInterceptor> interceptors, QueryContext context)
    {
        if (interceptors.Count == 0) return;
        foreach (IQueryInterceptor interceptor in interceptors) interceptor.OnBefore(context);
    }

    /// <summary>触发所有拦截器的 OnAfter——v3.1 抽出辅助，让 SELECT/UPDATE 管线共用并保留"空列表跳过"优化。
    /// 起始时间戳由调用方传入，仅当拦截器非空时才会经 GetElapsedTime 现算 Elapsed
    /// （调用方需保证拦截器非空时 swStart 也非 null）。
    /// R3（v5.6.0）改 internal：DataSession.ExecuteAsync（原始 DDL/DML）接入。
    /// A7（2026-10-01 全 API 逐项轮）：参数由 <c>Stopwatch?</c> 改 <c>long?</c> 起始时间戳——
    /// 消每查询的 Stopwatch 对象分配；Elapsed 语义（单调差值）逐位等价。</summary>
    internal static void NotifyInterceptorsOnAfter(
        List<IQueryInterceptor> interceptors, QueryContext context, long? swStart, int count)
    {
        if (interceptors.Count == 0) return;
        // 调用方契约：interceptors.Count > 0 时 swStart 必非 null（needStopwatch = observed || interceptors.Count > 0）。
        TimeSpan elapsed = Stopwatch.GetElapsedTime(swStart!.Value);
        foreach (IQueryInterceptor interceptor in interceptors)
            interceptor.OnAfter(context, elapsed, count);
    }

    /// <summary>返回第一行实体；无结果抛 <see cref="InvalidOperationException"/>。
    /// <para>内部限制 Take(1) 并跳过缓存写入——截断结果写入用户缓存键会导致同键 ToListAsync 静默丢行。</para>
    /// <para>G1（2026-10-13 五轮全量重读微项包）：截断族改走 <see cref="ExecuteQueryRowsAsync{T}"/>
    /// 单行物化内核——不再为最多 2 行的结果建 List（约 −56B/次），管线语义（拦截器时序/
    /// 观测性/弹性覆盖/A9 复用槽/截断防护）与 ToListAsync 逐位一致。</para></summary>
    public static async ValueTask<T> FirstAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        T? result = await FirstOrDefaultAsync(builder, ct).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Sequence contains no elements.");
    }

    /// <summary>返回第一行实体，无结果返回 null。
    /// <para>内部限制 Take(1) 并跳过缓存写入——截断结果写入用户缓存键会导致同键 ToListAsync 静默丢行。</para></summary>
    public static ValueTask<T?> FirstOrDefaultAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        if (builder._operationState.CompletedOps >= SessionOperationState.PrewarmThreshold)
            builder._operationState.PrewarmCurrentOperationOwner();
        return FirstOrDefaultEntryAsync(builder, ct);
    }

    private static async ValueTask<T?> FirstOrDefaultEntryAsync<T>(QueryBuilder<T> builder, CancellationToken ct) where T : class, new()
    {
        var limited = builder;
        limited._take = 1;
        limited._takeLiteral = true;   // SQLite 上内联成 LIMIT 1（理由与实测见 QueryBuilder.LiteralTakeValue）
        // First/Single 族的 _take 截断列表不得写入用户缓存键——后续同键 ToListAsync
        // 会命中截断数据静默丢行（ITM-406）
        limited._cacheKey = null;
        (T? row, _) = await ExecuteQueryRowsAsync(limited, maxRows: 1, ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>返回恰好一行实体；无结果或多于一行均抛 <see cref="InvalidOperationException"/>。
    /// <para>内部限制 Take(2) 检测多行并跳过缓存写入（同 FirstOrDefaultAsync 的截断防护）。</para></summary>
    public static async ValueTask<T> SingleAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        var limited = builder;
        limited._take = 2;
        limited._takeLiteral = true;   // SQLite 上内联成 LIMIT 2（同 FirstOrDefaultAsync）
        limited._cacheKey = null;
        (T? row, int count) = await ExecuteQueryRowsAsync(limited, maxRows: 2, ct).ConfigureAwait(false);
        if (count == 1) return row!;
        throw new InvalidOperationException(count == 0 ? "Empty." : "More than one.");
    }

    /// <summary>返回至多一行实体：无结果返回 null，多于一行抛 <see cref="InvalidOperationException"/>。
    /// <para>内部限制 Take(2) 检测多行并跳过缓存写入（同 FirstOrDefaultAsync 的截断防护）。</para></summary>
    public static async ValueTask<T?> SingleOrDefaultAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        var limited = builder;
        limited._take = 2;
        limited._takeLiteral = true;   // SQLite 上内联成 LIMIT 2（同 FirstOrDefaultAsync）
        limited._cacheKey = null;
        (T? row, int count) = await ExecuteQueryRowsAsync(limited, maxRows: 2, ct).ConfigureAwait(false);
        return count <= 1 ? row : throw new InvalidOperationException("More than one.");
    }

    /// <summary>G1（2026-10-13 五轮全量重读微项包）：First/Single 截断族的单行物化执行内核——
    /// 与 <see cref="ExecuteQueryAsync{T}"/>（ToList 管线）<b>语义逐位一致</b>（拦截器
    /// OnBefore → OnAfter(实际行数) → OnError、观测性、弹性覆盖、A9 复用槽、A1 并行池归还、
    /// READ-001 读连接失效），差异仅在物化：最多 maxRows 行直读进返回元组，不建 List
    ///（截断族原先为 1~2 行结果付 List + 元素数组约 56B，且 OnAfter 行数口径不变）。
    /// <para><b>缓存写入结构性跳过</b>：三个调用方入口在进入前清空 _cacheKey（ITM-406 截断防护），
    /// 本内核无缓存写入面。maxRows 恒等于已设的 Take 值（1 或 2）——SQL 的 LIMIT 已在服务端
    /// 截断，循环上限是双保险而非额外扫描。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "与 ExecuteQueryAsync/ExecuteForEachAsync 同构的管线形态（执行/拦截器/观测性三段式）——"
            + "单行变体必须保持同一语义结构，抽公共化会为各处引入一层间接（PipelineParityContractTests 的既有取舍）。")]
    private static async ValueTask<(T? Row, int Rows)> ExecuteQueryRowsAsync<T>(
        QueryBuilder<T> builder, int maxRows, CancellationToken ct, object? operationOwner = null)
        where T : class, new()
    {
        if (builder._selectColumns is not null)
            throw new NotSupportedException(
                "Partial Select projection is not supported for entity queries; use the full entity query or an explicit QueryAsync projection type.");
        string sql = builder.BuildSql();
        IReadOnlyList<DbParameter> parameters = builder.GetQueryParameters();
        var context = new QueryContext(sql, parameters, builder._sensitiveMasks);
        const string operation = "select";
        string provider = builder._dialect.GetName();
        bool observed = builder._tracing || builder._metrics;
        Activity? activity = builder._tracing ? PalORMMetrics.StartActivity(operation, provider) : null;
        List<IQueryInterceptor> interceptors = builder._interceptors;
        bool needStopwatch = observed || interceptors.Count > 0;
        long? swStart = needStopwatch ? Stopwatch.GetTimestamp() : null;
        SessionOperationState operationState = builder._operationState;
        DbTransaction? boundTransaction = builder.GetActiveTransaction();
        bool metrics = builder._metrics;
        string? metricsName = builder._metricsName;
        Func<ValueTask>? readConnInvalidator = builder._readConnInvalidator;
        ResilienceContext resilience = builder.CreateResilienceContext();
        var state = new QueryExecutionState<T>(
            builder._conn, builder._useReadRoute, operationState,
            builder._readConnProvider, builder._querySlot, builder._factory,
            builder._take, builder._prepared, builder._commandTimeout, builder._queryCache,
            builder._cacheKey, builder._cacheTtl, builder._cacheTenantScope,
            sql, parameters, context, interceptors, swStart, boundTransaction,
            builder._readConnReturner);
        string outcome = "error";
        try
        {
            using SessionOperationState.SessionOperationLease operationLease =
                operationState.EnterReadOnly(operationOwner);
            (T? row, int rows) result = await ExecuteWithResilienceAsync(
                resilience,
                static (s, token) => ExecuteRowsCoreAsync(s.State, s.MaxRows, token),
                (State: state, MaxRows: maxRows),
                boundTransaction,
                ct).ConfigureAwait(false);
            outcome = "success";
            return result;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                outcome = "cancelled";
            // READ-001（2026-09-23）：读路由下瞬时故障即丢弃会话缓存的读连接。
            if (readConnInvalidator is not null && resilience.IsTransientException(exception))
                await readConnInvalidator().ConfigureAwait(false);
            NotifyInterceptorsOnError(interceptors, context, exception);
            throw;
        }
        finally
        {
            PalORMMetrics.CompleteActivity(activity, outcome);
            if (metrics && swStart is { } metricStart)
                PalORMMetrics.Record(operation, provider, outcome, Stopwatch.GetElapsedTime(metricStart), metricsName);
        }
    }

    /// <summary>键集（keyset）分页：返回一页数据与总行数。
    /// <para><b>P0-1（2026-10-12）PG 单往返默认路径</b>：PG 方言 + 默认 READ COMMITTED +
    /// 无外部事务 + 无拦截器/观测性时，COUNT 与页查询打包为一个 <c>DbBatch</c> 单次协议往返
    ///（原形态 BEGIN+COUNT+SELECT+COMMIT 四次往返）。该形态下快照语义与原先逐位一致——
    /// READ COMMITTED 的事务内两条语句本就<b>各取快照</b>（Total 与页行可能不一致），
    /// 需要强一致快照请 <see cref="DataSession{TProvider}.WithIsolationLevel"/>(Snapshot/Serializable)
    /// 或外部事务（两者都走经典事务路径）。MySQL（RR 快照有真实一致性）与 SQLite 保持
    /// 经典事务路径。</para></summary>
    /// <para><b>各方言经典路径的快照语义</b>（决策 MySQL 不入批路径的依据，2026-10-12 复核）：
    /// MySQL InnoDB 默认 REPEATABLE READ 下，事务内 COUNT 与页查询<b>共享同一快照</b>
    ///（快照在事务首次读取时建立）——Total 与页行保证一致；SQLite 延迟读事务下两语句
    /// 看到同一一致性读视图；PG 默认 READ COMMITTED 下两语句各取快照（Total 与页行可能
    /// 不一致，需强一致用 Snapshot/Serializable 隔离）。批路径（仅 PG 默认形态）以
    /// "两快照"为契约，与 PG 经典路径逐位等价。</para>
    /// <para>lastValue 为上一页末行的 orderBy 键值：非默认值时生成 <c>orderBy &lt; lastValue</c>（降序）
    /// 或 <c>&gt;</c>（升序）续页条件；首页传 default。键值为 default 的行无法作为续页锚点。</para>
    /// <para>ITM-582: ① COUNT 查询走 Tracing/Metrics 但<b>不经过</b> IQueryInterceptor（页查询经过）——
    /// 审计型拦截器不会看到 COUNT SQL；② builder 上已设的 <c>Skip()</c> 被忽略（键集分页以
    /// lastValue 锚点续页，OFFSET 语义不适用）；③ 用户先行的 <c>Take()</c> 同被
    /// pageSize 覆写（r9-S-B/r10-N2 补交付——双覆写契约显式化）。</para>
    public static ValueTask<(List<T> Rows, long Total)> ToPageAsync<T, TKey>(this QueryBuilder<T> builder,
        int pageSize, Expression<Func<T, TKey>> orderBy, TKey? lastValue = default, bool descending = true, CancellationToken ct = default) where T : class, new()
    {
        if (builder._operationState.CompletedOps >= SessionOperationState.PrewarmThreshold)
            builder._operationState.PrewarmCurrentOperationOwner();
        return ToPageEntryAsync(builder, pageSize, orderBy, lastValue, descending, ct);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "分页事务骨架（自开/复用、提交/回滚/还原/释放）叠加 PG 无事务批路径与 "
            + "MySQL 事务内批路径分支；拆分需跨方法传递事务生命周期与提交标志，状态面更大。")]
    private static async ValueTask<(List<T> Rows, long Total)> ToPageEntryAsync<T, TKey>(QueryBuilder<T> builder,
        int pageSize, Expression<Func<T, TKey>> orderBy, TKey? lastValue, bool descending, CancellationToken ct) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operationLease =
            builder._operationState.EnterReadOnly();
        var paged = builder.CloneForExecution();
        paged._useReadRoute = false;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        paged._take = pageSize;
        paged._takeLiteral = false;   // pageSize 由调用方给、值域无界：必须参数化，保住 SHAPE-010 的有限形状集
        paged._skip = null;
        // r9-S-A(P1)：页截断结果不得写入用户缓存键——同键 ToListAsync 将静默命中单页子集
        // （ITM-406 同型面唯一漏口，First/Single/SingleOrDefault 三入口均已清）
        paged._cacheKey = null;
        DbTransaction? existingTransaction = paged.GetActiveTransaction();
        // P0-1（2026-10-12 全量重读）：PG 默认形态的单往返分页——COUNT 与页 SELECT 打进一个
        // DbBatch 单次协议往返。PG 默认 READ COMMITTED 下事务内两条语句<b>各取快照</b>
        //（ToPageAsync 注释既有登记：Total 与页行可能不一致），原路径 BEGIN+COUNT+SELECT+COMMIT
        // 四次往返中 BEGIN/COMMIT 两次买不到任何一致性，纯属开销；批内两条语句的快照语义
        // 与原形态逐位一致。其余形态（强隔离/外部事务/SQLite/MySQL——RR 快照有真实一致性
        // 价值）走下方经典事务路径；门禁细则见 CanUseBatchedPage。
        if (CanUseBatchedPage(paged, existingTransaction))
        {
            return await ExecuteBatchedPageAsync(paged, pageSize, orderBy, lastValue, descending, ct)
                .ConfigureAwait(false);
        }
        // ITM-649：自有事务经 PublishTransaction 登记进 SessionOperationState——会话 Dispose
        // 与事务门禁可见该事务（此前直接 BeginTransactionAsync 自包含但登记缺席；若 Dispose/
        // 门禁依赖登记状态则为盲区）。外部事务已由其创建方登记，此处不重复发布。
        // r5-S2：自开事务 honoring 会话 WithIsolationLevel——原裸调 BeginTransactionAsync
        // 恒驱动默认，Serializable/Snapshot 会话的分页一致性快照被静默降级。
        // 隔离实口径（2026-10-12 注明）：事务使 COUNT 与页查询在 MySQL RR / SQLite 延迟读
        // 下看到同一快照；PG 默认 READ COMMITTED 下两条语句各取快照，Total 与页行可能
        // 不一致——需要强一致请 WithIsolationLevel(Snapshot/Serializable)。
        DbTransaction transaction = existingTransaction
            ?? (paged._isolationLevel is { } iso
                ? await paged._conn.BeginTransactionAsync(iso, ct).ConfigureAwait(false)
                : await paged._conn.BeginTransactionAsync(ct).ConfigureAwait(false));
        bool ownsTransaction = existingTransaction is null;
        Exception? primaryException = null;
        // T1（v5.6.0）：同 WithTransaction——提交尝试标志区分提交失败与查询失败
        bool commitAttempted = false;
        // ITM-793(r21)：登记两步移入 try——PublishTransaction 可抛（ObjectDisposed，窄窗口），
        // 原位置抛出会让刚开启的自有事务无 rollback 无 dispose（连接持开事务）。finally 已按
        // ownsTransaction 处置，登记失败同样走该路径。
        try
        {
            if (ownsTransaction)
                paged._operationState.PublishTransaction(transaction, operationLease.Owner);
            paged._transaction = transaction;
            string countSql = paged.BuildCountSql();
            long total;
            List<T> rows;
            if (CanUseBatchedPageInTransaction(paged))
            {
                // S6：COUNT 与页 SELECT 打包为一个 DbBatch，单次协议往返；事务与隔离级别
                // 沿用经典路径，MySQL 的快照一致性语义不变。
                IReadOnlyList<DbParameter> countParameters = paged.GetCountParameters();
                string pageOperation = descending ? "<" : ">";
                string pageOrderByColumn = paged.ResolveColumn(orderBy);
                if (lastValue is not null && !EqualityComparer<TKey>.Default.Equals(lastValue, default))
                    paged.AddWhereComparison(pageOrderByColumn, pageOperation, lastValue);
                paged.AddOrderBy(pageOrderByColumn, descending);
                (rows, total) = await ExecuteBatchedPageCoreAsync(
                    paged, pageSize, countSql, countParameters,
                    paged.BuildSql(), paged.GetQueryParameters(), transaction, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await using var countCommand = paged._conn.CreateCommand();
                countCommand.Transaction = transaction;
                countCommand.CommandText = countSql;
                countCommand.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(paged._commandTimeout);
                AddParameters(countCommand, paged.GetCountParameters());
                object? countResult = await ExecuteScalarObservedAsync(
                    countCommand, paged, "count", ct).ConfigureAwait(false);
                // ITM-637：COUNT 结果理论恒非 null——驱动异常形态下显式报错比 NRE 近根因
                if (countResult is null)
                    throw new InvalidOperationException(
                        "COUNT query returned null scalar — the ADO.NET driver behaved unexpectedly.");
                total = countResult is long count ? count : Convert.ToInt64(countResult);

                string operation = descending ? "<" : ">";
                // C16：orderBy 列解析一次复用（原 AddWhereComparison + AddOrderBy 各解析一次）
                string orderByColumn = paged.ResolveColumn(orderBy);
                if (lastValue is not null && !EqualityComparer<TKey>.Default.Equals(lastValue, default))
                    paged.AddWhereComparison(orderByColumn, operation, lastValue);
                // review R3：如果 builder 已有 OrderBy，AddOrderBy 追加为次级排序键。
                // keyset 续页条件始终锚定 orderBy 参数列——用户预设排序不影响续页正确性。
                paged.AddOrderBy(orderByColumn, descending);
                rows = await ExecuteQueryAsync(
                    paged, ct, operationLease.Owner).ConfigureAwait(false);
            }
            if (ownsTransaction)
            {
                // T1（v5.6.0）：提交尝试标志——裁决依据见 TransactionCleanup.TrySkipRollbackAfterCommitFailure
                commitAttempted = true;
                await TransactionCleanup.CommitWithTimeoutAsync(
                    transaction, DbOptions.ToCommandTimeoutSeconds(paged._commandTimeout), ct)
                    .ConfigureAwait(false);
            }
            return (rows, total);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction
                && (!commitAttempted
                    || !TransactionCleanup.TrySkipRollbackAfterCommitFailure(
                        builder._dialect, exception)))
            {
                await TransactionCleanup.RollbackPreservingAsync(
                    transaction, exception,
                    DbOptions.ToCommandTimeoutSeconds(paged._commandTimeout), ct)
                    .ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (ownsTransaction)
            {
                // 与 WithTransaction 同序：先还原登记状态，再释放事务本体（ITM-649）。
                paged._operationState.RestoreTransaction(transaction, existingTransaction);
                await TransactionCleanup.DisposeTransactionPreservingAsync(transaction, primaryException).ConfigureAwait(false);
            }
        }
    }

    /// <summary>P0-1：PG 单往返分页的门禁判定（从 ToPageEntryAsync 抽出，降 S3776）——
    /// 无外部事务、PG 方言、默认隔离（RC）、无拦截器/观测性（三段式编排留在经典路径）、
    /// 非 AsPrepared、连接具备 DbBatch 能力（第三方 PG 方言 Provider 探测失败回退）。</summary>
    private static bool CanUseBatchedPage<T>(QueryBuilder<T> paged, DbTransaction? existingTransaction)
        where T : class, new()
        => existingTransaction is null
            && paged._dialect == SqlDialect.PostgreSql
            && paged._isolationLevel is null or System.Data.IsolationLevel.ReadCommitted
            && paged._interceptors.Count == 0
            && !paged._tracing && !paged._metrics && !paged._prepared
            && BatchedPageConnectionCapable(paged._conn);

    /// <summary>S6：MySQL 事务内单往返分页——COUNT 与页 SELECT 在同一个 DbBatch 里执行。
    /// 事务与隔离级别沿用经典路径（RR/RC 快照语义不变），只把两次往返压成一次；拦截器/
    /// 观测性/AsPrepared 在场时回退经典路径（三段式编排留在原处）。</summary>
    private static bool CanUseBatchedPageInTransaction<T>(QueryBuilder<T> paged)
        where T : class, new()
        => paged._dialect == SqlDialect.MySql
            && paged._interceptors.Count == 0
            && !paged._tracing && !paged._metrics && !paged._prepared
            && BatchedPageConnectionCapable(paged._conn);

    /// <summary>P0-1：连接类型 → DbBatch 能力缓存（第三方 PG 方言 Provider 的连接未覆写
    /// CreateBatch 时基类抛 NotSupportedException——探测一次按类型记忆，稳态一次字典查询）。
    /// 与 SessionBatch.TryCreateBatch 的方言静态判定 + try/catch 兜底同纪律；本处分页路径
    /// 按 PG 方言门控后仍需面对自定义连接，探测结果缓存避免每页一次异常抛出成本。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> BatchedPageCapability = new();

    private static bool BatchedPageConnectionCapable(DbConnection connection)
    {
        Type connectionType = connection.GetType();
        if (BatchedPageCapability.TryGetValue(connectionType, out bool capable))
            return capable;
        try
        {
            using DbBatch probe = connection.CreateBatch();
            capable = true;
        }
        catch (NotSupportedException)
        {
            capable = false;
        }
        BatchedPageCapability.TryAdd(connectionType, capable);
        return capable;
    }

    /// <summary>P0-1：PG 单往返分页——COUNT 与页查询的语句/参数在批执行内核外构建一次，
    /// 重试经会话弹性策略包裹（幂等读，每次尝试重建批与读取器；经典路径的页查询同受
    /// 弹性覆盖，COUNT 原直连——批形态两条语句同享覆盖是收窄而非放松）。</summary>
    private static async ValueTask<(List<T> Rows, long Total)> ExecuteBatchedPageAsync<T, TKey>(
        QueryBuilder<T> paged, int pageSize, Expression<Func<T, TKey>> orderBy,
        TKey? lastValue, bool descending, CancellationToken ct)
        where T : class, new()
    {
        // COUNT 先行构建（键集续页条件尚未注入——与经典路径的构建次序逐位一致，
        // COUNT 不含续页条件是 keyset 语义本体）
        string countSql = paged.BuildCountSql();
        IReadOnlyList<DbParameter> countParameters = paged.GetCountParameters();
        string operation = descending ? "<" : ">";
        // C16：orderBy 列解析一次复用
        string orderByColumn = paged.ResolveColumn(orderBy);
        if (lastValue is not null && !EqualityComparer<TKey>.Default.Equals(lastValue, default))
            paged.AddWhereComparison(orderByColumn, operation, lastValue);
        paged.AddOrderBy(orderByColumn, descending);
        string pageSql = paged.BuildSql();
        IReadOnlyList<DbParameter> pageParameters = paged.GetQueryParameters();

        ResilienceExecutor resilience = paged.GetResilience();
        if (resilience.IsPassThrough)
            return await ExecuteBatchedPageCoreAsync<T>(
                paged, pageSize, countSql, countParameters, pageSql, pageParameters,
                transaction: null, ct).ConfigureAwait(false);
        // F3（2026-10-13 全量重读微项包）：R4-λ 清扫的漏网点——原形态捕获 6 个局部变量的闭包
        // 在非直通（默认）配置下每分页查询分配一个 display class。static lambda 按调用点缓存，
        // 值元组状态按值传递，语义逐位一致。
        return await resilience.ExecuteAsync(
            static (state, token) => ExecuteBatchedPageCoreAsync<T>(
                state.Paged, state.PageSize, state.CountSql, state.CountParameters,
                state.PageSql, state.PageParameters, transaction: null, token),
            (Paged: paged, PageSize: pageSize, CountSql: countSql, CountParameters: countParameters,
             PageSql: pageSql, PageParameters: pageParameters),
            ct).ConfigureAwait(false);
    }

    /// <summary>P0-1：批执行内核——COUNT 结果集 + 页结果集经一个 DbDataReader 顺序消费。
    /// <para><b>参数归属</b>：COUNT 与页查询的参数列表可能引用同一批子句参数对象
    ///（Where/DefaultFilter 的参数同时进两个语句），同一 DbParameter 实例不得挂进两个
    /// DbBatchCommand——COUNT 侧经 <see cref="AddBatchParameterCopies"/> 拷贝（参数数 ≤ 过滤
    /// 参数量，典型 0~2 个），页侧独占原始对象。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S107:MethodsShouldNotHaveTooManyParameters",
        Justification = "批分页内核的输入是两条语句的 SQL/参数 + 事务 + 页大小 + 取消令牌；"
            + "聚合对象会为每个调用点多一次分配，而本方法每次分页调用一次。")]
    private static async Task<(List<T> Rows, long Total)> ExecuteBatchedPageCoreAsync<T>(
        QueryBuilder<T> paged, int pageSize,
        string countSql, IReadOnlyList<DbParameter> countParameters,
        string pageSql, IReadOnlyList<DbParameter> pageParameters,
        DbTransaction? transaction, CancellationToken ct)
        where T : class, new()
    {
        await using DbBatch batch = paged._conn.CreateBatch();
        batch.Transaction = transaction;
        // DbBatch.Timeout 是 int 秒（SessionBatch 的 BATCH-001 口径；不设则驱动默认 30s，
        // 会话 WithTimeout 在此静默失效）
        batch.Timeout = DbOptions.ToCommandTimeoutSeconds(paged._commandTimeout);

        DbBatchCommand countCommand = batch.CreateBatchCommand();
        countCommand.CommandText = countSql;
        AddBatchParameterCopies(countCommand, countParameters);
        batch.BatchCommands.Add(countCommand);

        DbBatchCommand pageCommand = batch.CreateBatchCommand();
        pageCommand.CommandText = pageSql;
        for (int i = 0; i < pageParameters.Count; i++)
            pageCommand.Parameters.Add(pageParameters[i]);
        batch.BatchCommands.Add(pageCommand);

        await using DbDataReader reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
        // 第一结果集：COUNT 标量（ITM-637 同型面：null/零行显式报错不静默 0）
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "COUNT query returned no row — the ADO.NET driver behaved unexpectedly.");
        object? countResult = reader.GetValue(0);
        if (countResult is null)
            throw new InvalidOperationException(
                "COUNT query returned null scalar — the ADO.NET driver behaved unexpectedly.");
        long total = countResult is long count ? count : Convert.ToInt64(countResult);

        // 第二结果集：页行（ITM-712 容量封顶与经典路径同口径）
        if (!await reader.NextResultAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Paged query result set is missing from the batch response.");
        List<T> rows = await ResultListReader.ReadAllAsync(
            reader, paged._factory, Math.Min(pageSize, MaxPreallocatedCapacity), ct).ConfigureAwait(false);
        return (rows, total);
    }

    /// <summary>P0-1：把参数拷贝进 COUNT 批命令（名称/类型/值三件套；源对象归页命令或子句所有，
    /// 不得共享实例——DbBatchCommand.Parameters 是每命令独立集合）。</summary>
    private static void AddBatchParameterCopies(DbBatchCommand command, IReadOnlyList<DbParameter> source)
    {
        for (int i = 0; i < source.Count; i++)
        {
            DbParameter original = source[i];
            DbParameter copy = command.CreateParameter();
            copy.ParameterName = original.ParameterName;
            copy.DbType = original.DbType;
            copy.Value = original.Value;
            command.Parameters.Add(copy);
        }
    }

    /// <summary>多结果集查询——执行调用方提供的完整 SQL。builder 仅供连接/方言/参数工厂，
    /// 已构建的 Where/OrderBy 等子句不参与执行；含子句时明确失败防误用（ITM-332）。
    /// <para><b>ITM-572 警告</b>: SQL 逐字执行，<b>默认过滤（[SoftDelete]/[TenantAware]）不适用</b>——
    /// 租户会话经此入口可读到全部租户与已软删数据。多租户场景必须在 SQL 中自行携带
    /// tenant_id/deleted_at 条件，或改用受过滤保护的常规查询入口。</para>
    /// <para>ITM-548: 流式多结果集<b>不经过</b> IQueryInterceptor——GridReader 无单一 rowCount，
    /// 异常发生在调用方逐集读取阶段（本方法已返回），单端 OnBefore 会让 begin/end 配对型
    /// 拦截器泄漏。可观测性用 WithTracing/WithMetrics（QueryObservation 随 GridReader.DisposeAsync 收尾）。</para></summary>
    public static async ValueTask<GridReader> QueryMultipleAsync<T>(this QueryBuilder<T> builder, FormattableString sql, CancellationToken ct = default) where T : class, new()
    {
        // ITM-523: 守卫只统计"用户实质子句"——Tag/TagWithCaller 产生的 Comment 类别与
        // From<T>() 注入的 DefaultFilter 均应豁免，否则加个 Tag 就误触误用异常。
        // ITM-715(r20)：Take/Skip/Select/AsSplitQuery/WithCache 是字段不是子句，
        // CountUserSubstantiveClauses 看不见——它们同样会被静默忽略，必须一并拒绝。
        if (builder.CountUserSubstantiveClauses() > 0 || builder.HasIgnoredExecutionModifiers)
            throw new InvalidOperationException(
                "QueryMultipleAsync executes the provided SQL verbatim and ignores builder clauses. " +
                "Call it on a bare From<T>() (no Where/OrderBy/etc.), or embed conditions in the SQL itself.");
        const string operation = "query_multiple";
        QueryObservation? observation = StartObservation(builder, operation);
        DbCommand? command = null;
        GridReader? grid = null;
        SessionOperationState.SessionOperationLease operationLease =
            builder._operationState.EnterReadOnly();
        bool operationTransferred = false;
        try
        {
            DbConnection connection = await builder.AcquireExecutionConnectionAsync(false, ct).ConfigureAwait(false);
            // 调用方提供的完整 SQL——可能含多语句/状态改写，标脏交给 Provider 复位。
            builder._markStateUnknown?.Invoke(connection);
            command = connection.CreateCommand();
            command.Transaction = builder.GetActiveTransaction();
            command.CommandText = QueryBuilder<T>.FormatFormattableSql(sql, 0);
            command.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(builder._commandTimeout);
            for (int i = 0; i < sql.ArgumentCount; i++)
                command.Parameters.Add(builder._paramFactory(QueryBuilder<T>.GetParameterName(i), sql.GetArgument(i)));
            await PrepareCommandAsync(command, builder._prepared, ct).ConfigureAwait(false);
            DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            grid = new GridReader(
                reader, command, observation, operationLease,
                builder._validateColumnOrder, builder._operationState);
            operationTransferred = true;
            builder._operationState.RegisterTransactionResource(grid);
            return grid;
        }
        catch (Exception exception)
        {
            observation?.Complete(exception is OperationCanceledException && ct.IsCancellationRequested
                ? "cancelled"
                : "error");
            // READ-001（2026-09-23）：同 SELECT 管线——读路由瞬时故障丢弃会话缓存的读连接
            if (builder.GetResilience().IsTransient(exception))
                await builder.InvalidateReadConnectionAsync().ConfigureAwait(false);
            await CleanupQueryResourcesAsync(grid, command, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!operationTransferred)
                await operationLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>构建可观测性追踪点——仅当 tracing 或 metrics 任一启用时创建。
    /// R5（v6.0）：WithMetrics(name) 的业务名贯通全部观测路径（含 ToPage/QueryMultiple/
    /// ExecuteScalar 等经 QueryObservation 的路径——非仅三个直接 Record 调用点）。</summary>
    private static QueryObservation? StartObservation<T>(QueryBuilder<T> builder, string operation) where T : class, new()
        => builder._tracing || builder._metrics
            ? new QueryObservation(builder._tracing, builder._metrics, operation, builder._dialect.GetName(),
                builder._metricsName)
            : null;

    /// <summary>查询失败时的资源清理——按"grid 已建/未建"两路径释放。
    /// 异常挂 Data 键不替换原始失败：GridCleanupException / CommandCleanupException。
    /// grid 已建时其 DisposeAsync 内部级联释放 command。连接为借用语义（M3-6：租约已退场，
    /// 主连接与会话级复用的读连接均由 DataSession 持有并释放），本清理不触连接。</summary>
    private static async ValueTask CleanupQueryResourcesAsync(
        GridReader? grid, DbCommand? command, Exception primaryException)
    {
        if (grid is not null)
        {
            try { await grid.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupException)
            {
                primaryException.Data["PalORM.GridCleanupException"] = cleanupException;
            }
            return;
        }
        if (command is not null)
        {
            try { await command.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupException) { primaryException.Data["PalORM.CommandCleanupException"] = cleanupException; }
        }
    }

    /// <summary>构建并执行 UPDATE 语句，返回受影响行数。
    /// <para>要求至少一个 <c>Set</c> 子句；WHERE 段含默认过滤（软删/租户）。写操作恒走主连接，不受 ForRead 影响。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "UPDATE 管线的命令复用/新建分支与拦截器/观测性三段式是必然复杂度；"
            + "拆分需跨方法传递命令所有权与参数绑定状态。")]
    public static async ValueTask<int> ExecuteNonQueryAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        const string operation = "update";
        string provider = builder._dialect.GetName();
        List<IQueryInterceptor> interceptors = builder._interceptors;
        bool observed = builder._tracing || builder._metrics;
        // ITM-625：SQL 先行（同 ExecuteQueryAsync 顺序）——BuildUpdateSql 守卫抛异常时
        // 已创建的 Activity 无人 Dispose，污染 Activity.Current 链。
        string sql = builder.BuildUpdateSql();
        Activity? activity = builder._tracing ? PalORMMetrics.StartActivity(operation, provider) : null;
        // v3.1：Stopwatch 延迟创建——与 ExecuteQueryAsync 同构（拦截器 OnAfter 需要 Elapsed）。
        // A7（2026-10-01 全 API 逐项轮）：同改时间戳形态。
        long? swStart = observed || interceptors.Count > 0 ? Stopwatch.GetTimestamp() : null;
        string outcome = "error";
        // ITM-513: UPDATE 执行管线补齐拦截器，与 SELECT 一致覆盖 OnBefore/OnAfter/OnError
        IReadOnlyList<DbParameter> updateParameters = builder.GetUpdateParameters();
        var context = new QueryContext(sql, updateParameters, builder._sensitiveMasks);
        bool metrics = builder._metrics;
        string? metricsName = builder._metricsName;
        bool prepared = builder._prepared;
        TimeSpan commandTimeout = builder._commandTimeout;
        try
        {
            using SessionOperationState.SessionOperationLease operationLease =
                builder._operationState.Enter();
            DbConnection connection = await builder.AcquireExecutionConnectionAsync(true, ct).ConfigureAwait(false);
            builder._markStateUnknown?.Invoke(connection);
            DbTransaction? transaction = builder.GetActiveTransaction();
            // S5：写形态接查询槽复用。槽晋升时命令参数集合为空，本次 AddParameters 后
            // 集合持久持有；后续同形状只就地写 Value（R-UNNESTB 同纪律）。
            DbCommand? reusable = builder.TryAcquireReusableCommand(connection, sql);
            DbCommand command;
            bool ownsCommand;
            bool parametersBound;
            if (reusable is not null
                && (reusable.Parameters.Count == 0 || TryCopyParameterValues(reusable, updateParameters)))
            {
                command = reusable;
                ownsCommand = false;
                parametersBound = reusable.Parameters.Count > 0;
            }
            else
            {
                command = connection.CreateCommand();
                ownsCommand = true;
                parametersBound = false;
            }
            try
            {
                if (!ReferenceEquals(command.Transaction, transaction))
                    command.Transaction = transaction;
                int timeoutSeconds = DbOptions.ToCommandTimeoutSeconds(commandTimeout);
                if (command.CommandTimeout != timeoutSeconds)
                    command.CommandTimeout = timeoutSeconds;
                if (!string.Equals(command.CommandText, sql, StringComparison.Ordinal))
                    command.CommandText = sql;
                if (!parametersBound) AddParameters(command, updateParameters);
                await EnsureReusablePreparedAsync(builder._querySlot, reusable is not null, ct)
                    .ConfigureAwait(false);
                NotifyInterceptorsOnBefore(interceptors, context);
                await PrepareCommandAsync(command, prepared, ct).ConfigureAwait(false);
                int affectedRows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                NotifyInterceptorsOnAfter(interceptors, context, swStart, affectedRows);
                outcome = "success";
                return affectedRows;
            }
            finally
            {
                if (ownsCommand) await command.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // ITM-513: 与 SELECT 管线一致——取消归类 cancelled，任何异常都通知拦截器 OnError
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                outcome = "cancelled";
            NotifyInterceptorsOnError(interceptors, context, exception);
            throw;
        }
        finally
        {
            PalORMMetrics.CompleteActivity(activity, outcome);
            if (metrics && swStart is { } metricStart)
                PalORMMetrics.Record(operation, provider, outcome, Stopwatch.GetElapsedTime(metricStart), metricsName);
        }
    }

    private static async ValueTask<object?> ExecuteScalarObservedAsync<T>(
        DbCommand command,
        QueryBuilder<T> builder,
        string operation,
        CancellationToken cancellationToken)
        where T : class, new()
    {
        string provider = builder._dialect.GetName();
        QueryObservation? observation = builder._tracing || builder._metrics
            ? new QueryObservation(builder._tracing, builder._metrics, operation, provider, builder._metricsName)
            : null;
        try
        {
            await PrepareCommandAsync(command, builder._prepared, cancellationToken).ConfigureAwait(false);
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            observation?.Complete("success");
            return result;
        }
        catch (Exception exception)
        {
            observation?.Complete(exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? "cancelled"
                : "error");
            throw;
        }
    }

    internal static Task PrepareCommandAsync(
        DbCommand command,
        bool prepared,
        CancellationToken cancellationToken)
        => prepared ? command.PrepareAsync(cancellationToken) : Task.CompletedTask;

    // v4.1 极致降内存：直接复用 GetQueryParameters 已创建的 DbParameter，避免 _paramFactory 重建 N 个参数。
    // r18：builder 参数自 v4.1 起未再使用——删除并消除 S1172 抑制（refine R-P2-01）。
    private static void AddParameters(DbCommand command,
        IReadOnlyList<DbParameter> parameters)
    {
        // R3（2026-10-12）：for + 索引替代 foreach——经 IReadOnlyList 接口枚举 List 会
        // 装箱枚举器（每查询约 32B）；数组/自建 List 的索引访问零分配且同为 O(1)。
        for (int i = 0; i < parameters.Count; i++)
            command.Parameters.Add(parameters[i]);
    }

    /// <summary>R-UNNESTB（2026-10-02）：复用命令的就地参数赋值——逐位置把本次查询的
    /// Value 写进持久持有的参数实例（<b>不 Clear、不 Add 新实例</b>：PG 连接的 auto-prepare
    /// 调优下「Clear + Add 新实例」会让驱动沿用 prepare 时的绑定值，探针 mergearray 实测）。
    /// <para>形状守卫：个数不一致或任一位置的 DbType 与晋升时不同 → 返回 false，调用方回退
    /// 新建命令。同型循环（同一查询形状、值不同——晋升的主要受益场景）恒走就地写。</para></summary>
    private static bool TryCopyParameterValues(DbCommand command,
        IReadOnlyList<DbParameter> parameters)
    {
        if (command.Parameters.Count != parameters.Count)
            return false;
        // 先全量校验形状再写值——半写状态对中断的复用尝试无害但不整洁
        for (int i = 0; i < parameters.Count; i++)
        {
            if (command.Parameters[i].DbType != parameters[i].DbType)
                return false;
        }
        for (int i = 0; i < parameters.Count; i++)
            command.Parameters[i].Value = parameters[i].Value;
        return true;
    }
}
