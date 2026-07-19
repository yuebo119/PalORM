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
        Activity? activity = builder._tracing ? PalORMMetrics.StartActivity(operation, provider) : null;
        var sw = Stopwatch.StartNew();
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
            foreach (IQueryInterceptor interceptor in builder._interceptors) interceptor.OnBefore(context);
            await PrepareCommandAsync(cmd, builder._prepared, ct).ConfigureAwait(false);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            List<T> list = builder._take.HasValue ? new(builder._take.Value) : [];
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(builder._factory.Read(reader));
            foreach (IQueryInterceptor interceptor in builder._interceptors) interceptor.OnAfter(context, sw.Elapsed, list.Count);
            // 缓存存入列表副本：列表结构隔离；实体实例与首个调用方共享（浅拷贝语义）。
            if (builder._cacheKey is not null) builder._queryCache.Set(builder._cacheKey, new List<T>(list), builder._cacheTtl);
            outcome = "success";
            return list;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                outcome = "cancelled";
            foreach (IQueryInterceptor interceptor in builder._interceptors)
            {
                try { interceptor.OnError(context, exception); }
                catch { /* 拦截器不能覆盖原始执行异常，也不能阻断资源清理。 */ }
            }
            throw;
        }
        finally
        {
            sw.Stop();
            PalORMMetrics.CompleteActivity(activity, outcome);
            if (builder._metrics)
                PalORMMetrics.Record(operation, provider, outcome, sw.Elapsed);
        }
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
        return results.Count == 1 ? results[0] : throw new InvalidOperationException(results.Count == 0 ? "Empty." : "More than one.");
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
    /// 或 <c>&gt;</c>（升序）续页条件；首页传 default。键值为 default 的行无法作为续页锚点。</para></summary>
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
        string provider = builder._dialect.GetName();
        QueryObservation? observation = builder._tracing || builder._metrics
            ? new QueryObservation(builder._tracing, builder._metrics, operation, provider)
            : null;
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
            if (grid is not null)
            {
                try { await grid.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupException)
                {
                    exception.Data["PalORM.GridCleanupException"] =
                        cleanupException;
                }
            }
            else
            {
                if (command is not null)
                {
                    try { await command.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupException) { exception.Data["PalORM.CommandCleanupException"] = cleanupException; }
                }
                if (lease is not null)
                {
                    try { await lease.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupException) { exception.Data["PalORM.ConnectionCleanupException"] = cleanupException; }
                }
            }
            throw;
        }
        finally
        {
            if (!operationTransferred)
                await operationLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>构建并执行 UPDATE 语句，返回受影响行数。
    /// <para>要求至少一个 <c>Set</c> 子句；WHERE 段含默认过滤（软删/租户）。写操作恒走主连接，不受 ForRead 影响。</para></summary>
    public static async ValueTask<int> ExecuteNonQueryAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        const string operation = "update";
        string provider = builder._dialect.GetName();
        Activity? activity = builder._tracing ? PalORMMetrics.StartActivity(operation, provider) : null;
        var stopwatch = Stopwatch.StartNew();
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
            foreach (IQueryInterceptor interceptor in builder._interceptors) interceptor.OnBefore(context);
            await PrepareCommandAsync(command, builder._prepared, ct).ConfigureAwait(false);
            int affectedRows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            foreach (IQueryInterceptor interceptor in builder._interceptors) interceptor.OnAfter(context, stopwatch.Elapsed, affectedRows);
            outcome = "success";
            return affectedRows;
        }
        catch (Exception exception)
        {
            // ITM-513: 与 SELECT 管线一致——取消归类 cancelled，任何异常都通知拦截器 OnError
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                outcome = "cancelled";
            foreach (IQueryInterceptor interceptor in builder._interceptors)
            {
                try { interceptor.OnError(context, exception); }
                catch { /* 拦截器不能覆盖原始执行异常，也不能阻断资源清理。 */ }
            }
            throw;
        }
        finally
        {
            stopwatch.Stop();
            PalORMMetrics.CompleteActivity(activity, outcome);
            if (builder._metrics)
                PalORMMetrics.Record(operation, provider, outcome, stopwatch.Elapsed);
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
