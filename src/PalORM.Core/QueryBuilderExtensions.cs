using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;

namespace PalORM;

/// <summary>QueryBuilder 执行扩展方法——从 struct 分离避免装箱。</summary>
public static class QueryBuilderExtensions
{
    /// <summary>结果列表的预分配容量上限（ITM-712）。Take/分页大小是查询结果上界而非预期行数，
    /// 无上限的预分配可被单个超大值放大为进程级 OOM；封顶后超出部分依赖 List 均摊 O(1) 扩容。</summary>
    private const int MaxPreallocatedCapacity = 4096;

    /// <summary>实际缓存 key 组装（ADR-L 结构性隔离）：租户作用域非空时前缀化——
    /// <c>__t:{tenantId}:{userKey}</c>（租户过滤查询）或 <c>__all__:{userKey}</c>（多租户会话的
    /// IgnoreFilters / 非 TenantAware 实体，全量数据独立命名空间）；单租户会话（作用域 null）
    /// key 原样。调用方仍按自己的 userKey 推理缓存，前缀是框架内部命名空间。</summary>
    private static string EffectiveCacheKey<T>(in QueryBuilder<T> builder) where T : class, new()
        => builder._cacheTenantScope is { } scope
            ? $"{scope}:{builder._cacheKey}"
            : builder._cacheKey!;
    /// <summary>执行查询并返回全部实体列表。
    /// <para>配置 <c>WithCache</c> 时先查缓存：命中返回新 List，但元素是共享实体实例（浅拷贝契约，见 WithCache 文档）；
    /// 未命中则执行查询并将副本写入缓存。</para></summary>
    public static async ValueTask<List<T>> ToListAsync<T>(this QueryBuilder<T> builder, CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operationLease =
            builder._operationState.Enter();
        // 缓存命中返回列表副本——List 本身隔离，但元素是共享实体实例（浅拷贝，ITM-308）：
        // 调用方修改命中实体会污染缓存与其他调用方。契约声明见 WithCache 文档。
        // ADR-L：实际 key 经租户作用域前缀组装（跨租户命中结构性不可能）
        if (builder._cacheKey is not null
            && builder._queryCache.TryGet(EffectiveCacheKey(builder), out List<T>? cached) && cached is not null)
            return new List<T>(cached);

        return await ExecuteQueryAsync(
            builder, ct, operationLease.Owner).ConfigureAwait(false);
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
            builder._operationState.Enter();
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
        Stopwatch? sw = needStopwatch ? Stopwatch.StartNew() : null;
        string outcome = "error";
        DbTransaction? boundTransaction = builder.GetActiveTransaction();
        ResilienceExecutor resilience = builder._resilience;
        bool resilient = boundTransaction is null && !resilience.IsPassThrough;

        // 单次尝试内核——每行经回调消费，不物化列表；拦截器语义与 ToListAsync 一致
        async Task<long> ExecuteCoreAsync(CancellationToken token)
        {
            DbConnection connection = await builder.AcquireExecutionConnectionAsync(false, token).ConfigureAwait(false);
            await using DbCommand cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(builder._commandTimeout);
            cmd.Transaction = boundTransaction;
            AddParameters(cmd, parameters);
            NotifyInterceptorsOnBefore(interceptors, context);
            await PrepareCommandAsync(cmd, builder._prepared, token).ConfigureAwait(false);
            long rowCount = 0;
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                await action(builder._factory(reader), token).ConfigureAwait(false);
                rowCount++;
            }
            NotifyInterceptorsOnAfter(interceptors, context, sw, (int)rowCount);
            return rowCount;
        }

        try
        {
            using SessionOperationState.SessionOperationLease operationStateLease =
                builder._operationState.Enter(operationOwner);
            long count = resilient
                ? await resilience.ExecuteAsync(ExecuteCoreAsync, ct).ConfigureAwait(false)
                : await ExecuteCoreAsync(ct).ConfigureAwait(false);
            outcome = "success";
            return count;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                outcome = "cancelled";
            // READ-001（2026-09-23）：读路由下瞬时故障即丢弃会话缓存的读连接——静默掐断
            // （NAT/LB，无 FIN/RST）后 State 仍为 Open，不丢弃则后续查询复用死连接。
            // 确定性失败不丢弃（连接是健康的，丢弃只会白付一次重建）。
            if (builder._resilience.IsTransient(exception))
                await builder.InvalidateReadConnectionAsync().ConfigureAwait(false);
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
            throw new NotSupportedException(
                "Partial Select projection is not supported for entity queries; use the full entity query or an explicit QueryAsync projection type.");
        string sql = builder.BuildSql();
        IReadOnlyList<DbParameter> parameters = builder.GetQueryParameters();
        var context = new QueryContext(sql, parameters, builder._sensitiveMasks);
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
        // v5.4 弹性接入（评审 P1-a）：WithRetry/WithCircuitBreaker 此前对内置管线无效。
        // 只读 SELECT 管线现经会话弹性策略执行。接入条件：
        // ① 命令不带事务——事务内重试会在语句失败后二次失败（如 PG aborted transaction）
        //   并以次生异常掩盖根因，且跨尝试的快照语义不成立；
        // ② 策略非直通（MaxRetries=0 且 CircuitBreakerThreshold=0，即 Testing 预设）。
        //    注意：默认配置（MaxRetries=3 / 熔断阈值 5）**不是**直通——只读查询默认就走执行器，
        //    此前这里"默认直通路径零开销"的说法不成立（v5.6 实测更正）。
        //    实测常数开销 ≈272 B/查询，与结果行数无关（单行查询 +8% 分配，千行 +0.2%）。
        //    三项独立测量之和与 in-situ A/B 之差逐字节吻合（168+56+48 = 272，实测 272~278）：
        //      · 超时 CTS + CancelAfter 定时器 ≈168 B，每次尝试一份——这正是 CommandTimeout
        //        语义本身，且覆盖连接获取与读取器迭代，不是驱动侧 CommandTimeout 的子集，不能省
        //      · 调用点把单次尝试内核转成委托 1 次 = 56 B（内核是捕获 builder 的 async 局部函数，
        //        目标实例每次不同，无法缓存委托）
        //      · 执行器机械（熔断进出 + 异步状态机 ≈48 B），量级最小且与重试/熔断语义耦合
        //    后两项合计 104 B 是唯一可剥的部分，须把只读内核从 async 局部函数改成 struct 内核 +
        //    泛型约束（顺带消掉两分支共有的 ~250 B display class）；实测耗时无变化，未做。
        //    直通配置下三项都不发生，但同时也失去超时包装：慢命令抛驱动自身异常，
        //    不再是带 PalORM.InfrastructureTimeout 标记的 TimeoutException。
        // 写入路径（ExecuteNonQueryAsync/Bulk/StoredProc/原始 SQL 家族）维持直连：
        // 重试非幂等写有重复执行风险（ITM-310 契约），显式需求请用 ExecuteWithResilience 包裹。
        DbTransaction? boundTransaction = builder.GetActiveTransaction();
        ResilienceExecutor resilience = builder._resilience;
        bool resilient = boundTransaction is null && !resilience.IsPassThrough;

        // 单次尝试内核——每次重试重建连接租约/命令/读取器；缓存写入仅在成功尝试发生；
        // 拦截器 OnBefore/OnError 按尝试触发（失败的尝试确实发生了），OnAfter 仅成功尝试。
        async Task<List<T>> ExecuteCoreAsync(CancellationToken token)
        {
            DbConnection connection = await builder.AcquireExecutionConnectionAsync(false, token).ConfigureAwait(false);
            await using DbCommand cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(builder._commandTimeout);
            cmd.Transaction = boundTransaction;
            AddParameters(cmd, parameters);
            // v3.1：拦截器空列表跳过——默认会话无拦截器，foreach 迭代空 List 仍有方法调用开销。
            NotifyInterceptorsOnBefore(interceptors, context);
            await PrepareCommandAsync(cmd, builder._prepared, token).ConfigureAwait(false);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            // v4.0 优化 D：默认 Capacity 16 起步——避免 []（=0）在 10K 行场景的 14 次扩容（每次 2x 复制数组）。
            // 16 是经验值：小型查询（< 16 行）零扩容，大型查询（10K 行）扩容次数从 14 降至 10。
            // ITM-712：Take 是"最多 N 行"的上界而非预期行数——直接作为容量会让 Take(1_000_000_000)
            // 触发巨量分配/OOM（ToPageAsync 的 pageSize 经 paged._take 同源）。封顶后由均摊 O(1) 扩容兜底。
            List<T> list = builder._take.HasValue
                ? new List<T>(Math.Min(builder._take.Value, MaxPreallocatedCapacity))
                : new List<T>(16);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) list.Add(builder._factory(reader));
            NotifyInterceptorsOnAfter(interceptors, context, sw, list.Count);
            // 缓存存入列表副本：列表结构隔离；实体实例与首个调用方共享（浅拷贝语义）。
            // ADR-L：实际 key 经租户作用域前缀组装（与 TryGet 消费点同源）
            if (builder._cacheKey is not null)
                builder._queryCache.Set(EffectiveCacheKey(builder), new List<T>(list), builder._cacheTtl);
            return list;
        }

        try
        {
            using SessionOperationState.SessionOperationLease operationLease =
                builder._operationState.Enter(operationOwner);
            // 操作门禁横跨全部重试尝试持有——一次用户操作仍是一次门禁占用，
            // 与 SessionOperationState 的单活动操作契约一致。
            List<T> list = resilient
                ? await resilience.ExecuteAsync(ExecuteCoreAsync, ct).ConfigureAwait(false)
                : await ExecuteCoreAsync(ct).ConfigureAwait(false);
            outcome = "success";
            return list;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                outcome = "cancelled";
            // READ-001（2026-09-23）：读路由下瞬时故障即丢弃会话缓存的读连接——静默掐断
            // （NAT/LB，无 FIN/RST）后 State 仍为 Open，不丢弃则后续查询复用死连接。
            // 确定性失败不丢弃（连接是健康的，丢弃只会白付一次重建）。
            if (builder._resilience.IsTransient(exception))
                await builder.InvalidateReadConnectionAsync().ConfigureAwait(false);
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
    /// R3（v5.7）改 internal：DataSession.ExecuteAsync（原始 DDL/DML）接入同一三段式。</summary>
    internal static void NotifyInterceptorsOnBefore(
        List<IQueryInterceptor> interceptors, QueryContext context)
    {
        if (interceptors.Count == 0) return;
        foreach (IQueryInterceptor interceptor in interceptors) interceptor.OnBefore(context);
    }

    /// <summary>触发所有拦截器的 OnAfter——v3.1 抽出辅助，让 SELECT/UPDATE 管线共用并保留"空列表跳过"优化。
    /// Stopwatch 由调用方传入，仅当拦截器非空时才会读取 Elapsed（调用方需保证拦截器非空时 sw 也非 null）。
    /// R3（v5.7）改 internal：DataSession.ExecuteAsync 接入。</summary>
    internal static void NotifyInterceptorsOnAfter(
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
        limited._takeLiteral = true;   // SQLite 上内联成 LIMIT 1（理由与实测见 QueryBuilder.LiteralTakeValue）
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
        limited._takeLiteral = true;   // SQLite 上内联成 LIMIT 2（同 FirstOrDefaultAsync）
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
        limited._takeLiteral = true;   // SQLite 上内联成 LIMIT 2（同 FirstOrDefaultAsync）
        limited._cacheKey = null;
        List<T> results = await ExecuteQueryAsync(limited, ct).ConfigureAwait(false);
        return results.Count <= 1 ? results.FirstOrDefault() : throw new InvalidOperationException("More than one.");
    }

    /// <summary>键集（keyset）分页：返回一页数据与总行数。</summary>
    /// <para>COUNT 与页查询在同一事务内执行保证一致性快照——无外部事务时自动开启并提交/回滚。</para>
    /// <para>lastValue 为上一页末行的 orderBy 键值：非默认值时生成 <c>orderBy &lt; lastValue</c>（降序）
    /// 或 <c>&gt;</c>（升序）续页条件；首页传 default。键值为 default 的行无法作为续页锚点。</para>
    /// <para>ITM-582: ① COUNT 查询走 Tracing/Metrics 但<b>不经过</b> IQueryInterceptor（页查询经过）——
    /// 审计型拦截器不会看到 COUNT SQL；② builder 上已设的 <c>Skip()</c> 被忽略（键集分页以
    /// lastValue 锚点续页，OFFSET 语义不适用）；③ 用户先行的 <c>Take()</c> 同被
    /// pageSize 覆写（r9-S-B/r10-N2 补交付——双覆写契约显式化）。</para>
    public static async ValueTask<(List<T> Rows, long Total)> ToPageAsync<T, TKey>(this QueryBuilder<T> builder,
        int pageSize, Expression<Func<T, TKey>> orderBy, TKey? lastValue = default, bool descending = true, CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operationLease =
            builder._operationState.Enter();
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
        // ITM-649：自有事务经 PublishTransaction 登记进 SessionOperationState——会话 Dispose
        // 与事务门禁可见该事务（此前直接 BeginTransactionAsync 自包含但登记缺席；若 Dispose/
        // 门禁依赖登记状态则为盲区）。外部事务已由其创建方登记，此处不重复发布。
        // r5-S2：自开事务 honoring 会话 WithIsolationLevel——原裸调 BeginTransactionAsync
        // 恒驱动默认，Serializable/Snapshot 会话的分页一致性快照被静默降级
        DbTransaction transaction = existingTransaction
            ?? (paged._isolationLevel is { } iso
                ? await paged._conn.BeginTransactionAsync(iso, ct).ConfigureAwait(false)
                : await paged._conn.BeginTransactionAsync(ct).ConfigureAwait(false));
        bool ownsTransaction = existingTransaction is null;
        Exception? primaryException = null;
        // T1（v5.7）：同 WithTransaction——提交尝试标志区分提交失败与查询失败
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
            long total = countResult is long count ? count : Convert.ToInt64(countResult);

            string operation = descending ? "<" : ">";
            if (lastValue is not null && !EqualityComparer<TKey>.Default.Equals(lastValue, default))
                paged.AddWhereComparison(orderBy, operation, lastValue);
            // review R3：如果 builder 已有 OrderBy，AddOrderBy 追加为次级排序键。
            // keyset 续页条件始终锚定 orderBy 参数列——用户预设排序不影响续页正确性。
            paged.AddOrderBy(orderBy, descending);
            List<T> rows = await ExecuteQueryAsync(
                paged, ct, operationLease.Owner).ConfigureAwait(false);
            if (ownsTransaction)
            {
                // T1（v5.7）：提交尝试标志——裁决依据见 TransactionCleanup.TrySkipRollbackAfterCommitFailure
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
                    DbOptions.ToCommandTimeoutSeconds(paged._commandTimeout))
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
            builder._operationState.Enter();
        bool operationTransferred = false;
        try
        {
            DbConnection connection = await builder.AcquireExecutionConnectionAsync(false, ct).ConfigureAwait(false);
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
            if (builder._resilience.IsTransient(exception))
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

    /// <summary>构建可观测性追踪点——仅当 tracing 或 metrics 任一启用时创建。</summary>
    private static QueryObservation? StartObservation<T>(QueryBuilder<T> builder, string operation) where T : class, new()
        => builder._tracing || builder._metrics
            ? new QueryObservation(builder._tracing, builder._metrics, operation, builder._dialect.GetName())
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
        Stopwatch? sw = observed || interceptors.Count > 0 ? Stopwatch.StartNew() : null;
        string outcome = "error";
        // ITM-513: UPDATE 执行管线补齐拦截器，与 SELECT 一致覆盖 OnBefore/OnAfter/OnError
        IReadOnlyList<DbParameter> updateParameters = builder.GetUpdateParameters();
        var context = new QueryContext(sql, updateParameters, builder._sensitiveMasks);
        try
        {
            using SessionOperationState.SessionOperationLease operationLease =
                builder._operationState.Enter();
            DbConnection connection = await builder.AcquireExecutionConnectionAsync(true, ct).ConfigureAwait(false);
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = builder.GetActiveTransaction();
            command.CommandText = sql;
            command.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(builder._commandTimeout);
            AddParameters(command, updateParameters);
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

    // v4.1 极致降内存：直接复用 GetQueryParameters 已创建的 DbParameter，避免 _paramFactory 重建 N 个参数。
    // r18：builder 参数自 v4.1 起未再使用——删除并消除 S1172 抑制（refine R-P2-01）。
    private static void AddParameters(DbCommand command,
        IReadOnlyList<DbParameter> parameters)
    {
        foreach (DbParameter parameter in parameters)
            command.Parameters.Add(parameter);
    }
}
