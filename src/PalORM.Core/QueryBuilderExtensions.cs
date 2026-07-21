using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;

namespace PalORM;

/// <summary>QueryBuilder 执行扩展方法——从 struct 分离避免装箱。</summary>
public static class QueryBuilderExtensions
{
    /// <summary>执行查询并返回全部实体列表。
    /// <para>配置 <c>WithCache</c> 时先查缓存：命中返回新 List，但元素是共享实体实例（浅拷贝契约，见 WithCache 文档）；
    /// 未命中则执行查询并将副本写入缓存。</para></summary>
    public static async ValueTask<List<T>> ToListAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operationLease =
            builder._operationState.Enter();
        // 缓存命中返回列表副本——List 本身隔离，但元素是共享实体实例（浅拷贝，ITM-308）：
        // 调用方修改命中实体会污染缓存与其他调用方。契约声明见 WithCache 文档。
        if (builder._cacheKey is not null && builder._queryCache.TryGet(builder._cacheKey, out List<T>? cached) && cached is not null)
            return new List<T>(cached);

        return await ExecuteQueryAsync(
            builder, ct, operationLease.Owner).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "查询执行管线的 try/catch/finally 三段式（执行/拦截器错误通知/观测性收尾）"
            + "是异步 IO 资源管理的必然形态。已抽出 NotifyInterceptorsOnError，余下分支是观测性收尾本身。")]
    private static async ValueTask<List<T>> ExecuteQueryAsync<T>(
        QueryBuilder<T> builder,
        CancellationToken ct,
        object? operationOwner = null) where T : class, new()
    {
        if (builder._selectColumns is not null)
            throw new NotSupportedException("实体查询不能执行部分 Select 投影；请使用完整实体查询或显式 QueryAsync 投影类型。");
        string sql = builder.BuildSql();
        IReadOnlyList<DbParameter> parameters = builder.GetQueryParameters();
        var context = new QueryContext(sql, parameters);
        const string operation = "select";
        string provider = builder._dialect.GetName();
        bool observed = builder._tracing || builder._metrics;
        Activity? activity = builder._tracing ? PalORMMetrics.StartActivity(operation, provider) : null;
        // v3.1：Stopwatch 延迟创建——仅当 Tracing/Metrics/拦截器任一启用时才分配（拦截器 OnAfter 需要 Elapsed）。
        // 默认配置（无观测性 + 无拦截器）的热路径省一次 StartNew + Stop（~150ns）。
        List<IQueryInterceptor> interceptors = builder._interceptors;
        bool needStopwatch = observed || interceptors.Count > 0;
        Stopwatch? sw = needStopwatch ? Stopwatch.StartNew() : null;
        string outcome = "error";
        try
        {
            using SessionOperationState.SessionOperationLease operationLease =
                builder._operationState.Enter(operationOwner);
            await using ConnectionLease lease = await builder.AcquireConnectionLeaseAsync(false, ct).ConfigureAwait(false);
            await using DbCommand cmd = lease.Connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(builder._commandTimeout);
            cmd.Transaction = builder.GetActiveTransaction();
            AddParameters(cmd, builder, parameters);
            // v3.1：拦截器空列表跳过——默认会话无拦截器，foreach 迭代空 List 仍有方法调用开销。
            NotifyInterceptorsOnBefore(interceptors, context);
            await PrepareCommandAsync(cmd, builder._prepared, ct).ConfigureAwait(false);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            // v4.0 优化 D：默认 Capacity 16 起步——避免 []（=0）在 10K 行场景的 14 次扩容（每次 2x 复制数组）。
            // 16 是经验值：小型查询（< 16 行）零扩容，大型查询（10K 行）扩容次数从 14 降至 10。
            List<T> list = builder._take.HasValue ? new(builder._take.Value) : new List<T>(16);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(builder._factory(reader));
            NotifyInterceptorsOnAfter(interceptors, context, sw, list.Count);
            // 缓存存入列表副本：列表结构隔离；实体实例与首个调用方共享（浅拷贝语义）。
            if (builder._cacheKey is not null) builder._queryCache.Set(builder._cacheKey, new List<T>(list), builder._cacheTtl);
            outcome = "success";
            return list;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                outcome = "cancelled";
            NotifyInterceptorsOnError(builder._interceptors, context, exception);
            throw;
        }
        finally
        {
            sw?.Stop();
            PalORMMetrics.CompleteActivity(activity, outcome);
            if (builder._metrics && sw is not null)
                PalORMMetrics.Record(operation, provider, outcome, sw.Elapsed);
        }
    }

    /// <summary>通知所有拦截器 OnError——单个拦截器抛出的异常被吞掉，
    /// 不覆盖原始执行异常、不阻断其他拦截器或后续资源清理。</summary>
    private static void NotifyInterceptorsOnError(
        List<IQueryInterceptor> interceptors, QueryContext context, Exception exception)
    {
        // v3.1：默认会话无拦截器——空列表直接返回，避免 foreach 迭代与方法调用开销。
        if (interceptors.Count == 0) return;
        foreach (IQueryInterceptor interceptor in interceptors)
        {
            try { interceptor.OnError(context, exception); }
            catch { /* 拦截器不能覆盖原始执行异常，也不能阻断资源清理。 */ }
        }
    }

    /// <summary>触发所有拦截器的 OnBefore——v3.1 抽出辅助，让 SELECT/UPDATE 管线共用并保留"空列表跳过"优化。</summary>
    private static void NotifyInterceptorsOnBefore(
        List<IQueryInterceptor> interceptors, QueryContext context)
    {
        if (interceptors.Count == 0) return;
        foreach (IQueryInterceptor interceptor in interceptors) interceptor.OnBefore(context);
    }

    /// <summary>触发所有拦截器的 OnAfter——v3.1 抽出辅助，让 SELECT/UPDATE 管线共用并保留"空列表跳过"优化。
    /// Stopwatch 由调用方传入，仅当拦截器非空时才会读取 Elapsed（调用方需保证拦截器非空时 sw 也非 null）。</summary>
    private static void NotifyInterceptorsOnAfter(
        List<IQueryInterceptor> interceptors, QueryContext context, Stopwatch? sw, int count)
    {
        if (interceptors.Count == 0) return;
        // 调用方契约：interceptors.Count > 0 时 sw 必非 null（needStopwatch = observed || interceptors.Count > 0）。
        TimeSpan elapsed = sw!.Elapsed;
        foreach (IQueryInterceptor interceptor in interceptors)
            interceptor.OnAfter(context, elapsed, count);
    }

    /// <summary>返回第一行实体；无结果抛 <see cref="InvalidOperationException"/>。
    /// <para>内部限制 Take(1) 并跳过缓存写入——截断结果写入用户缓存键会导致同键 ToListAsync 静默丢行。</para></summary>
    public static async ValueTask<T> FirstAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        T? result = await FirstOrDefaultAsync(builder, ct).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Sequence contains no elements.");
    }

    /// <summary>返回第一行实体，无结果返回 null。
    /// <para>内部限制 Take(1) 并跳过缓存写入——截断结果写入用户缓存键会导致同键 ToListAsync 静默丢行。</para></summary>
    public static async ValueTask<T?> FirstOrDefaultAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        var limited = builder;
        limited._take = 1;
        // First/Single 族的 _take 截断列表不得写入用户缓存键——后续同键 ToListAsync
        // 会命中截断数据静默丢行（ITM-406）
        limited._cacheKey = null;
        List<T> results = await ExecuteQueryAsync(limited, ct).ConfigureAwait(false);
        return results.Count == 0 ? default : results[0];
    }

    /// <summary>返回恰好一行实体；无结果或多于一行均抛 <see cref="InvalidOperationException"/>。
    /// <para>内部限制 Take(2) 检测多行并跳过缓存写入（同 FirstOrDefaultAsync 的截断防护）。</para></summary>
    public static async ValueTask<T> SingleAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        var limited = builder;
        limited._take = 2;
        limited._cacheKey = null;
        List<T> results = await ExecuteQueryAsync(limited, ct).ConfigureAwait(false);
        if (results.Count == 1) return results[0];
        throw new InvalidOperationException(results.Count == 0 ? "Empty." : "More than one.");
    }

    /// <summary>返回至多一行实体：无结果返回 null，多于一行抛 <see cref="InvalidOperationException"/>。
    /// <para>内部限制 Take(2) 检测多行并跳过缓存写入（同 FirstOrDefaultAsync 的截断防护）。</para></summary>
    public static async ValueTask<T?> SingleOrDefaultAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        var limited = builder;
        limited._take = 2;
        limited._cacheKey = null;
        List<T> results = await ExecuteQueryAsync(limited, ct).ConfigureAwait(false);
        return results.Count <= 1 ? results.FirstOrDefault() : throw new InvalidOperationException("More than one.");
    }

    /// <summary>键集（keyset）分页：返回一页数据与总行数。
    /// <para>COUNT 与页查询在同一事务内执行保证一致性快照——无外部事务时自动开启并提交/回滚。</para>
    /// <para>lastValue 为上一页末行的 orderBy 键值：非默认值时生成 <c>orderBy &lt; lastValue</c>（降序）
    /// 或 <c>&gt;</c>（升序）续页条件；首页传 default。键值为 default 的行无法作为续页锚点。</para>
    /// <para>ITM-582: ① COUNT 查询走 Tracing/Metrics 但<b>不经过</b> IQueryInterceptor（页查询经过）——
    /// 审计型拦截器不会看到 COUNT SQL；② builder 上已设的 <c>Skip()</c> 被忽略（键集分页以
    /// lastValue 锚点续页，OFFSET 语义不适用）。</para></summary>
    public static async ValueTask<(List<T> Rows, long Total)> ToPageAsync<T, TKey>(this QueryBuilder<T> builder,
        int pageSize, Expression<Func<T, TKey>> orderBy, TKey? lastValue = default, bool descending = true, CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operationLease =
            builder._operationState.Enter();
        var paged = builder.CloneForExecution();
        paged._useReadRoute = false;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        paged._take = pageSize;
        paged._skip = null;
        DbTransaction? existingTransaction = paged.GetActiveTransaction();
        // 注意：此处直接在会话主连接上开启事务而不经 PublishTransaction 登记——
        // 全程持有操作租约、事务只赋给克隆体并在 finally 自行提交/回滚/释放，自包含成立。
        // 若未来门禁逻辑依赖 SessionOperationState 的事务登记状态，此路径需改走 BeginTransactionCoreAsync。
        DbTransaction transaction = existingTransaction
            ?? await paged._conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        bool ownsTransaction = existingTransaction is null;
        Exception? primaryException = null;
        paged._transaction = transaction;
        try
        {
            string countSql = paged.BuildCountSql();

            await using var countCommand = paged._conn.CreateCommand();
            countCommand.Transaction = transaction;
            countCommand.CommandText = countSql;
            countCommand.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(paged._commandTimeout);
            AddParameters(countCommand, paged, paged.GetCountParameters());
            object? countResult = await ExecuteScalarObservedAsync(
                countCommand, paged, "count", ct).ConfigureAwait(false);
            long total = countResult is long count ? count : Convert.ToInt64(countResult);

            string operation = descending ? "<" : ">";
            if (lastValue is not null && !EqualityComparer<TKey>.Default.Equals(lastValue, default))
                paged.AddWhereComparison(orderBy, operation, lastValue);
            paged.AddOrderBy(orderBy, descending);
            List<T> rows = await ExecuteQueryAsync(
                paged, ct, operationLease.Owner).ConfigureAwait(false);
            if (ownsTransaction)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (rows, total);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await TransactionCleanup.RollbackPreservingAsync(transaction, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (ownsTransaction)
                await TransactionCleanup.DisposeTransactionPreservingAsync(transaction, primaryException).ConfigureAwait(false);
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
        if (builder.CountUserSubstantiveClauses() > 0)
            throw new InvalidOperationException(
                "QueryMultipleAsync executes the provided SQL verbatim and ignores builder clauses. " +
                "Call it on a bare From<T>() (no Where/OrderBy/etc.), or embed conditions in the SQL itself.");
        const string operation = "query_multiple";
        QueryObservation? observation = StartObservation(builder, operation);
        ConnectionLease? lease = null;
        DbCommand? command = null;
        GridReader? grid = null;
        SessionOperationState.SessionOperationLease operationLease =
            builder._operationState.Enter();
        bool operationTransferred = false;
        try
        {
            lease = await builder.AcquireConnectionLeaseAsync(false, ct).ConfigureAwait(false);
            command = lease.Connection.CreateCommand();
            command.Transaction = builder.GetActiveTransaction();
            command.CommandText = QueryBuilder<T>.FormatFormattableSql(sql, 0);
            command.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(builder._commandTimeout);
            for (int i = 0; i < sql.ArgumentCount; i++)
                command.Parameters.Add(builder._paramFactory($"@p{i}", sql.GetArgument(i)));
            await PrepareCommandAsync(command, builder._prepared, ct).ConfigureAwait(false);
            DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            grid = new GridReader(
                reader, command, lease, observation, operationLease,
                builder._validateColumnOrder);
            operationTransferred = true;
            builder._operationState.RegisterTransactionResource(grid);
            return grid;
        }
        catch (Exception exception)
        {
            observation?.Complete(exception is OperationCanceledException && ct.IsCancellationRequested
                ? "cancelled"
                : "error");
            await CleanupQueryResourcesAsync(grid, command, lease, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!operationTransferred)
                await operationLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>构建可观测性追踪点——仅当 tracing 或 metrics 任一启用时创建。</summary>
    private static QueryObservation? StartObservation<T>(QueryBuilder<T> builder, string operation) where T : class, new()
        => builder._tracing || builder._metrics
            ? new QueryObservation(builder._tracing, builder._metrics, operation, builder._dialect.GetName())
            : null;

    /// <summary>查询失败时的资源清理——按"grid 已建/未建"两路径释放。
    /// 异常挂 Data 键不替换原始失败：GridCleanupException / CommandCleanupException / ConnectionCleanupException。
    /// grid 已建时其 DisposeAsync 内部级联释放 command + lease。</summary>
    private static async ValueTask CleanupQueryResourcesAsync(
        GridReader? grid, DbCommand? command, ConnectionLease? lease, Exception primaryException)
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
        if (lease is not null)
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupException) { primaryException.Data["PalORM.ConnectionCleanupException"] = cleanupException; }
        }
    }

    /// <summary>构建并执行 UPDATE 语句，返回受影响行数。
    /// <para>要求至少一个 <c>Set</c> 子句；WHERE 段含默认过滤（软删/租户）。写操作恒走主连接，不受 ForRead 影响。</para></summary>
    public static async ValueTask<int> ExecuteNonQueryAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        const string operation = "update";
        string provider = builder._dialect.GetName();
        List<IQueryInterceptor> interceptors = builder._interceptors;
        bool observed = builder._tracing || builder._metrics;
        Activity? activity = builder._tracing ? PalORMMetrics.StartActivity(operation, provider) : null;
        // v3.1：Stopwatch 延迟创建——与 ExecuteQueryAsync 同构（拦截器 OnAfter 需要 Elapsed）。
        Stopwatch? sw = observed || interceptors.Count > 0 ? Stopwatch.StartNew() : null;
        string outcome = "error";
        // ITM-513: UPDATE 执行管线补齐拦截器，与 SELECT 一致覆盖 OnBefore/OnAfter/OnError
        string sql = builder.BuildUpdateSql();
        IReadOnlyList<DbParameter> updateParameters = builder.GetUpdateParameters();
        var context = new QueryContext(sql, updateParameters);
        try
        {
            using SessionOperationState.SessionOperationLease operationLease =
                builder._operationState.Enter();
            await using ConnectionLease lease = await builder.AcquireConnectionLeaseAsync(true, ct).ConfigureAwait(false);
            await using DbCommand command = lease.Connection.CreateCommand();
            command.Transaction = builder.GetActiveTransaction();
            command.CommandText = sql;
            command.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(builder._commandTimeout);
            AddParameters(command, builder, updateParameters);
            NotifyInterceptorsOnBefore(interceptors, context);
            await PrepareCommandAsync(command, builder._prepared, ct).ConfigureAwait(false);
            int affectedRows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            NotifyInterceptorsOnAfter(interceptors, context, sw, affectedRows);
            outcome = "success";
            return affectedRows;
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
            sw?.Stop();
            PalORMMetrics.CompleteActivity(activity, outcome);
            if (builder._metrics && sw is not null)
                PalORMMetrics.Record(operation, provider, outcome, sw.Elapsed);
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
            ? new QueryObservation(builder._tracing, builder._metrics, operation, provider)
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

    private static void AddParameters<T>(DbCommand command, QueryBuilder<T> builder,
        IReadOnlyList<DbParameter> parameters) where T : class, new()
    {
        foreach (DbParameter parameter in parameters)
            command.Parameters.Add(builder._paramFactory(parameter.ParameterName, parameter.Value));
    }
}
