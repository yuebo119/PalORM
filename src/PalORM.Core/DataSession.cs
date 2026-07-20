using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PalORM;

/// <summary>数据库会话 —— 封装连接生命周期。using-scoped 无状态，用完即弃。
/// 类似 Dapper 的 SqlConnection 扩展 + EF Core 的 DbContext，但零状态追踪。
/// 一个会话仅支持一个活动数据库操作；重叠操作会明确失败。</summary>
/// <typeparam name="TProvider">数据库 Provider 类型（PostgreSqlProvider / MySqlProvider / SqliteProvider）。</typeparam>
public sealed partial class DataSession<TProvider> : IAsyncDisposable
    where TProvider : IDbProvider
{
    private readonly DbConnection _conn;
    private DbOptions _options;
    private ResilienceExecutor _resilience;
    private readonly List<IQueryInterceptor> _interceptors;
    private readonly ILogger _logger;
    private readonly SessionOperationState _operationState = new();

    internal DataSession(DbConnection conn, DbOptions options, List<IQueryInterceptor> interceptors, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(options);
        _conn = conn;
        _options = options;
        _resilience = new ResilienceExecutor(options, TProvider.IsTransient);
        _interceptors = interceptors.OrderBy(i => i.Priority).ToList();
        _logger = logger ?? NullLogger.Instance;
        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            _logger.LogDebug("DataSession<{Provider}> created", TProvider.Name);
    }

    /// <summary>创建并打开数据库会话（含连接重试和 SQLite PRAGMA 初始化）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "DbConnection ownership is transferred to DataSession which disposes it.")]
    public static async Task<DataSession<TProvider>> CreateAsync(DbOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        string cs = options.ResolveConnectionString();
        int maxRetries = options.MaxRetries;
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            DbConnection? connection = null;
            try
            {
                connection = TProvider.CreateConnection(cs, options);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(options.ConnectionTimeout);
                await connection.OpenAsync(cts.Token).ConfigureAwait(false);

                // Provider 初始化钩子（SQLite: PRAGMA foreign_keys/WAL）。
                // 与 OpenAsync 共享连接超时和调用方取消——初始化被锁阻塞时不再无限等待。
                await TProvider.InitializeConnectionAsync(connection, cts.Token).ConfigureAwait(false);

                var interceptors = options.Interceptors?.ToList() ?? [];
                ILogger? logger = options.LoggerFactory?.CreateLogger($"PalORM.{TProvider.Name}");
                var session = new DataSession<TProvider>(connection, options, interceptors, logger);
                connection = null;
                return session;
            }
            catch (Exception exception) when (attempt < maxRetries && IsRetryable(exception, ct))
            {
                await DisposeConnectionSafelyAsync(connection).ConfigureAwait(false);
                connection = null;
                TimeSpan delay = options.RetryBackoff?.Invoke(attempt)
                    ?? ResilienceExecutor.GetDefaultBackoff(attempt);
                // ITM-603/605: 自定义 RetryBackoff 委托返回负值会让 Task.Delay 抛
                // ArgumentOutOfRangeException（参数名"delay"），错误消息不指向 RetryBackoff 配置。
                // 显式拒绝，消息与 ResilienceExecutor 构造函数包装守卫对齐（两路径共享口径）。
                if (delay < TimeSpan.Zero)
                    throw new InvalidOperationException(
                        $"DbOptions.RetryBackoff(attempt={attempt}) returned a negative TimeSpan ({delay}). " +
                        "The delegate must return a non-negative delay.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException timeoutException) when (!ct.IsCancellationRequested)
            {
                // 连接超时且重试耗尽：包装为 TimeoutException，与命令路径
                // （ResilienceExecutor）对称——调用方可与"我被取消"区分（ITM-206）。
                throw new TimeoutException(
                    $"Connection open timed out after {options.ConnectionTimeout} " +
                    $"(attempt {attempt + 1}/{maxRetries + 1}).",
                    timeoutException);
            }
            finally
            {
                await DisposeConnectionSafelyAsync(connection).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>连接清理——清理失败不能覆盖连接或初始化异常。
    /// 重试 catch 与 finally 共享，消除 try/catch 重复（G-4 重构）。</summary>
    private static async ValueTask DisposeConnectionSafelyAsync(DbConnection? connection)
    {
        if (connection is null) return;
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch { /* 清理失败不能覆盖连接或初始化异常。 */ }
    }

    private static bool IsRetryable(Exception exception, CancellationToken callerToken)
        => exception is OperationCanceledException
            ? !callerToken.IsCancellationRequested
            : TProvider.IsTransient(exception);

    /// <summary>创建查询构建器——每次调用创建新的 struct QueryBuilder（值类型）。
    /// <para><b>为什么是 struct</b>: 避免每次查询的堆分配。高 QPS 场景(10K+)每秒省 ~2MB 堆分配。</para>
    /// <para><b>为什么每次新建</b>: GORM #7437——条件残留在构建器实例上导致数据错误。全新构建器保证条件隔离。</para>
    /// <para>自动附加: 租户过滤([TenantAware])、软删除过滤([SoftDelete])；会话事务在执行时解析。</para></summary>

    /// <summary>忽略全局过滤器（[SoftDelete]/[TenantAware]）。设置后本次会话所有查询跳过自动过滤。
    /// <para>ITM-568: 与 AddInterceptor 同受门禁保护——有查询在飞时调用会明确失败，
    /// 防止飞行查询与过滤状态变更竞态产生跨租户/含软删数据。</para></summary>
    public DataSession<TProvider> IgnoreFilters()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        _ignoreFilters = true;
        return this;
    }
    internal bool _ignoreFilters;

    /// <summary>动态添加查询拦截器（日志/缓存/审计），并按 <see cref="IQueryInterceptor.Priority"/> 执行。
    /// 与其他会话操作一样受门禁保护：有查询在飞时调用会明确失败（拦截器列表被执行管线枚举，无锁修改是竞态）。</summary>
    public DataSession<TProvider> AddInterceptor(IQueryInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        int index = _interceptors.FindIndex(existing => existing.Priority > interceptor.Priority);
        if (index < 0)
            _interceptors.Add(interceptor);
        else
            _interceptors.Insert(index, interceptor);
        return this;
    }

    /// <summary>设置当前会话的事务。设置后所有后续查询在此事务内执行。
    /// 调用 CommitAsync()/RollbackAsync() 后需再次设置或清空 (UseTransaction(null))。</summary>
    public DataSession<TProvider> UseTransaction(DbTransaction? tran)
    {
        // ITM-606: 先查 disposed——tran.Connection == null 时下方 ReferenceEquals 永远 false，
        // 会遮蔽 SessionOperationState.UseTransaction 中"Cannot use a disposed transaction"的精确消息。
        if (tran is not null && tran.Connection is null)
            throw new ArgumentException("Cannot use a disposed transaction (its Connection is null). "
                + "Pass a transaction from an open DbConnection, or null to clear.", nameof(tran));
        if (tran is not null && !ReferenceEquals(tran.Connection, _conn))
            throw new ArgumentException("事务必须属于当前 DataSession 的主连接。", nameof(tran));
        _operationState.UseTransaction(tran);
        return this;
    }

    /// <summary>设置当前租户 ID。标注 [TenantAware] 的实体自动附加 WHERE tenant_id = @value。</summary>
    public DataSession<TProvider> WithTenant(object tenantId)
    {
        // 拒绝 null（ITM-532）：null 使 HasTenantFilter 恒 false 静默关闭租户过滤，
        // 上游 tenantId 缺失时全部查询跨租户返回——失败开放。宁可明确失败。
        ArgumentNullException.ThrowIfNull(tenantId);
        // ITM-568: 门禁保护（同 AddInterceptor）——飞行查询期间切换租户是竞态
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        _tenantId = tenantId;
        return this;
    }
    internal object? _tenantId;

    // ─── 弹性配置链式 API ────────────────────────────────

    /// <summary>设置事务隔离级别。</summary>
    public DataSession<TProvider> WithIsolationLevel(IsolationLevel level)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        _isolationLevel = level;
        return this;
    }
    private IsolationLevel _isolationLevel = IsolationLevel.ReadCommitted;

    /// <summary>设置会话默认命令超时，并重置当前弹性策略状态。</summary>
    public DataSession<TProvider> WithTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout.Ticks, 0, nameof(timeout));
        UpdateResilience(_options with { CommandTimeout = timeout });
        return this;
    }

    /// <summary>启用重试策略——仅重试 Provider 判定的瞬时数据库故障和内部命令超时，并重置当前弹性策略状态。</summary>
    public DataSession<TProvider> WithRetry(int maxRetries, Func<int, TimeSpan>? backoff = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        UpdateResilience(_options with { MaxRetries = maxRetries, RetryBackoff = backoff ?? _options.RetryBackoff });
        return this;
    }

    /// <summary>启用熔断器——连续最终失败达到阈值后快速失败，并重置当前弹性策略状态。
    /// <para>ITM-582: <paramref name="failureThreshold"/> = 0 表示<b>禁用熔断</b>（非"零容忍
    /// 立即熔断"）——与 DbOptions.CircuitBreakerThreshold 默认值语义一致。</para></summary>
    public DataSession<TProvider> WithCircuitBreaker(int failureThreshold, TimeSpan resetAfter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failureThreshold);
        ArgumentOutOfRangeException.ThrowIfNegative(resetAfter.Ticks, nameof(resetAfter));
        UpdateResilience(_options with { CircuitBreakerThreshold = failureThreshold, CircuitBreakerResetAfter = resetAfter });
        return this;
    }

    /// <summary>使用会话级弹性策略执行操作（自动重试+熔断）。</summary>
    public async ValueTask<T> ExecuteWithResilience<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
        => await Volatile.Read(ref _resilience).ExecuteAsync(operation, ct).ConfigureAwait(false);

    /// <summary>使用会话级弹性策略执行操作（无返回值）。</summary>
    public async ValueTask ExecuteWithResilience(Func<CancellationToken, Task> operation, CancellationToken ct = default)
        => await Volatile.Read(ref _resilience).ExecuteAsync(operation, ct).ConfigureAwait(false);

    private void UpdateResilience(DbOptions options)
    {
        // ITM-568: 配置发布与飞行查询互斥（门禁），消除"读到新旧混合配置"窗口；
        // ITM-577: 注释同步——一致性由门禁保证（单活动操作），不再依赖 Volatile.Read 配对协议。
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        UpdateResilienceCore(options);
    }

    private void UpdateResilienceCore(DbOptions options)
    {
        // ITM-577: 此前注释声称"读取方经 Volatile.Read(_resilience) 可见一致 _options"，但
        // CRUD 路径全部普通读、只有 ExecuteWithResilience 遵守该配对——声明与读方不符。
        // 现一致性由 UpdateResilience 的操作门禁保证（配置变更与飞行查询互斥）；
        // Volatile.Write 保留为跨线程发布的最低保障。
        Volatile.Write(ref _options, options);
        Volatile.Write(ref _resilience, new ResilienceExecutor(options, TProvider.IsTransient));
    }

    /// <summary>停止后台操作，释放连接。幂等。</summary>
    public ValueTask DisposeAsync()
    {
        if (_operationState.IsCurrentOperationScope
            || _operationState.IsCurrentTransactionFlow)
        {
            throw new InvalidOperationException(
                "DataSession cannot be disposed from its active operation or transaction scope.");
        }
        return _operationState.DisposeAsync(DisposeCoreAsync);
    }

    private async Task DisposeCoreAsync()
    {
        // 主异常保留模式：首个清理异常作为主异常抛出，后续异常挂 Exception.Data 不丢弃
        //（与 GridReader/三 Provider 的清理约定一致）。
        Exception? cleanupException = null;
        foreach (IQueryInterceptor interceptor in _interceptors)
        {
            // ITM-534: 优先同步 Dispose；仅实现 IAsyncDisposable 的拦截器走异步释放，不再漏释放。
            if (interceptor is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch (Exception exception) { RecordCleanupException(ref cleanupException, exception); }
            }
            else if (interceptor is IAsyncDisposable asyncDisposable)
            {
                try { await asyncDisposable.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { RecordCleanupException(ref cleanupException, exception); }
            }
        }

        try
        {
            if (_conn.State == ConnectionState.Open)
                await _conn.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { RecordCleanupException(ref cleanupException, exception); }

        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { RecordCleanupException(ref cleanupException, exception); }

        if (cleanupException is not null)
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }

    private static void RecordCleanupException(ref Exception? primary, Exception exception)
    {
        if (primary is null)
        {
            primary = exception;
            return;
        }
        // 后续异常挂 Data 不丢弃（与 GridReader 清理约定一致）；用 Data.Count 推导索引避免外部 ref 计数器
        primary.Data[$"PalORM.CleanupException{primary.Data.Count}"] = exception;
    }

    /// <summary>MySQL UPSERT 的 SET 子句（ON DUPLICATE KEY UPDATE 后的部分）。
    /// updateColumns 为空时：依赖 MySQL 行为——主键自增场景用 LAST_INSERT_ID(expr) 回填新主键，
    /// 否则用 VALUES(col) 回写（MySQL 8 起被 VALUES() 弃用警告，但仍是兼容路径）。
    /// updateColumns 非空时：显式列出每列的 VALUES(col)。</summary>
    private static string BuildMySqlUpsertSetClause(
        string[] updateColumns,
        string quotedPrimaryKey,
        bool hasGeneratedKey,
        Func<string, string> quoteIdentifier)
    {
        if (updateColumns.Length == 0)
        {
            return hasGeneratedKey
                ? $"{quotedPrimaryKey} = LAST_INSERT_ID({quotedPrimaryKey})"
                : $"{quotedPrimaryKey} = VALUES({quotedPrimaryKey})";
        }

        return string.Join(", ", updateColumns.Select(column =>
            $"{quoteIdentifier(column)} = VALUES({quoteIdentifier(column)})"));
    }

    private static EntityFeatures GetEntityFeatures<T>() where T : class, new()
        => PalORM_Runtime.EntityFeatures.GetValueOrDefault(typeof(T), EntityFeatures.None);

    // ─── 默认过滤（软删除 + 租户）────────────────────────
    // 三个 Get* 形态 + Bind 必须共用同一判定谓词：条件里引用了 @__tenant0 而 Bind 未绑（或反之）
    // 都会直接产生运行时错误。租户过滤覆盖所有直连读写入口（ITM-302）；
    // Insert/Save 不做租户过滤——实体自带 tenant_id 列值，由调用方负责。

    private bool HasTenantFilter<T>() where T : class, new()
        => _tenantId is not null && !_ignoreFilters
            && (GetEntityFeatures<T>() & EntityFeatures.TenantAware) != 0;

    /// <summary>默认过滤条件的参数名——避开生成 SQL 的 @p{N} 命名空间。</summary>
    private const string _tenantParameterName = "@__tenant0";

    private string GetDefaultFilterCondition<T>() where T : class, new()
    {
        string softDelete = !_ignoreFilters && (GetEntityFeatures<T>() & EntityFeatures.SoftDelete) != 0
            ? $"{TProvider.QuoteIdentifier("deleted_at")} IS NULL"
            : "";
        if (!HasTenantFilter<T>())
            return softDelete;
        string tenant = $"{TProvider.QuoteIdentifier("tenant_id")} = {_tenantParameterName}";
        return softDelete.Length == 0 ? tenant : $"{softDelete} AND {tenant}";
    }

    /// <summary>已有 WHERE 时的追加片段：" AND cond" 或空。</summary>
    private string GetDefaultFilterFragment<T>() where T : class, new()
    {
        string condition = GetDefaultFilterCondition<T>();
        return condition.Length == 0 ? "" : $" AND {condition}";
    }

    /// <summary>独立 WHERE 子句：" WHERE cond" 或空。</summary>
    private string GetDefaultFilterWhereClause<T>() where T : class, new()
    {
        string condition = GetDefaultFilterCondition<T>();
        return condition.Length == 0 ? "" : $" WHERE {condition}";
    }

    /// <summary>为默认过滤条件绑定参数。任何拼接了 GetDefaultFilter* 结果的命令都必须调用。</summary>
    private void BindDefaultFilterParameters<T>(DbCommand cmd) where T : class, new()
    {
        if (HasTenantFilter<T>())
            cmd.Parameters.Add(TProvider.CreateParameter(_tenantParameterName, _tenantId));
    }

    private SessionOperationState.SessionOperationLease EnterOperation(
        object? operationOwner = null)
        => _operationState.Enter(operationOwner);

    private DbCommand CreateCommand()
    {
        DbCommand command = _conn.CreateCommand();
        command.Transaction = GetActiveTransaction();
        // ITM-557 根治：超时在工厂集中设置——新调用点在结构上不可能漏（ValidateSchemaAsync 即漏网实例）。
        // ITM-597: 部分路径（HealthCheck/Scalar/Execute/Migrate/Bulk 等）显式重复赋同值——
        // 这是<b>防御冗余</b>而非死代码：万一未来 CreateCommand 被重构不再设超时，这些路径仍自洽。
        // 删除冗余会削弱回归防御；保留并在此声明 CreateCommand 是<b>权威源</b>。
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        return command;
    }

    private DbTransaction? GetActiveTransaction()
        => _operationState.GetActiveTransaction();

    /// <summary>将复合格式项映射为参数名，参数值保持原始对象。</summary>
    private static string FormatSqlWithParameters(FormattableString sql)
        => FormattableSqlFormatter.Format(sql);

    /// <summary>FormattableString → DbParameter 绑定。</summary>
    private static void BindFormattableParameters(DbCommand cmd, FormattableString sql)
    {
        for (int i = 0; i < sql.ArgumentCount; i++)
        {
            object? value = sql.GetArgument(i);
            DbParameter param = TProvider.CreateParameter($"@p{i}", value);
            cmd.Parameters.Add(param);
        }
    }
}
