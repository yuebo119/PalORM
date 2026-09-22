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

    /// <summary>测试专用：会话级 Dispose 等待上限（默认 5 分钟）。转发 <see cref="SessionOperationState"/>
    /// 实例状态——原静态可变属性已结构化（评审 2026-09-02），不同会话互不干扰，
    /// 测试无需保存/还原全局值。仅应在会话构造后、首个操作前设置。</summary>
    internal TimeSpan DisposeWaitTimeout
    {
        get => _operationState.DisposeWaitTimeout;
        set => _operationState.DisposeWaitTimeout = value;
    }
    // v5.6：读路由连接改为会话级懒创建并复用。原实现（v4.1 起）每次读查询都经
    // 工厂 CreateConnection + Open + Provider 初始化（SQLite 为 7 条 PRAGMA）——实测
    // 建连 26.3µs / 2209B 每次，是读副本查询总耗时（37.2µs）的 3.5 倍。
    private readonly string? _readConnectionString;
    private DbConnection? _readConnection;
    // v5.0 阶段 5.2：读连接初始化器——包装 Provider 钩子 + ReadSessionSetupSql。
    // 仅当 ReadSessionSetupSql 非空时才捕获实例委托；否则用 static 委托（无闭包分配）。
    private readonly Func<DbConnection, CancellationToken, Task>? _readConnInitializer;
    /// <summary>读连接提供者——会话级复用，无读连接串时为 null（读路由退化为主连接）。
    /// 缓存为实例字段：方法组直接传给 QueryBuilderContext 会在每次 From&lt;T&gt;() 分配闭包。</summary>
    private readonly Func<CancellationToken, ValueTask<DbConnection>>? _readConnProvider;

    internal DataSession(DbConnection conn, DbOptions options, List<IQueryInterceptor> interceptors, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(options);
        _conn = conn;
        _options = options;
        _resilience = new ResilienceExecutor(options, TProvider.IsTransient);
        _interceptors = interceptors.OrderBy(i => i.Priority).ToList();
        _logger = logger ?? NullLogger.Instance;
        // ITM-624 同型面（修复侧纪律卡第三问实证）：读连接每次创建都读 _options 字段而非
        // 捕获构造期 options——WithTimeout/WithRetry 后读连接与主连接的池参数/超时口径一致。
        _readConnectionString = options.ResolveReadConnectionString();
        _readConnProvider = _readConnectionString is not null ? AcquireReadConnectionAsync : null;
        // v5.0 阶段 5.2：读连接初始化器——无 ReadSessionSetupSql 时用 static 委托（零闭包分配），
        // 有时包装一层实例委托追加执行 ReadSessionSetupSql。
        _readConnInitializer = string.IsNullOrWhiteSpace(options.ReadSessionSetupSql)
            ? static (conn, ct) => TProvider.InitializeConnectionAsync(conn, ct)
            : async (conn, ct) =>
            {
                await TProvider.InitializeConnectionAsync(conn, ct).ConfigureAwait(false);
                await using DbCommand cmd = conn.CreateCommand();
                // ITM-624：读 _options 字段而非捕获构造期 options——WithTimeout/WithRetry 经
                // Volatile.Write 替换 _options 后，读连接初始化与主连接的超时口径保持一致。
                cmd.CommandTimeout = _options.CommandTimeoutSeconds;
                cmd.CommandText = _options.ReadSessionSetupSql;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            };
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

                // v5.0 阶段 5.2：用户会话级 SQL（SET TIME ZONE / search_path / statement_timeout 等）。
                // 在 Provider 钩子后执行——Provider 钩子可能设置影响后续 SET 的状态（如 SQLite PRAGMA）。
                // 多条 SQL 用分号分隔，一次 ExecuteNonQueryAsync 执行（三方言均支持多语句）。
                if (!string.IsNullOrWhiteSpace(options.SessionSetupSql))
                {
                    await using DbCommand setupCmd = connection.CreateCommand();
                    setupCmd.CommandTimeout = options.CommandTimeoutSeconds;
                    setupCmd.CommandText = options.SessionSetupSql;
                    await setupCmd.ExecuteNonQueryAsync(cts.Token).ConfigureAwait(false);
                }

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
                var wrappedTimeout = new TimeoutException(
                    $"Connection open timed out after {options.ConnectionTimeout} " +
                    $"(attempt {attempt + 1}/{maxRetries + 1}).",
                    timeoutException);
                // ITM-760(r21)：补 Data 标记——Resilience/PG COPY 的同类包装均带
                // PalORM.InfrastructureTimeout，调用方按该键分基础设施超时；连接路径
                // 此前缺失（同族三处口径不一致）。
                wrappedTimeout.Data["PalORM.InfrastructureTimeout"] = true;
                throw wrappedTimeout;
            }
            finally
            {
                await DisposeConnectionSafelyAsync(connection).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>预热连接池（C4，v5.7）：逐条打开 <paramref name="count"/> 条连接随即归还池，
    /// 使首批查询命中暖连接而非新建物理连接（远程建连实测 ~8.5 ms/条）。
    /// <para><b>与 <see cref="DbOptions.MinPoolSize"/> 的关系</b>：本方法是启动期一次性灌暖；
    /// MinPoolSize 是空闲修剪保留下限（稀疏流量下池不被清空）。两者配合才保持暖态——
    /// 只预热不设下限，空闲超时到期后预热成果仍会被修剪清空。</para>
    /// <para><b>SQLite</b>：无连接池，无暖态可留——直接返回，不报错（与池参数被
    /// SqliteProvider 忽略的既有契约一致）。</para>
    /// <para><b>失败语义</b>：建连异常原样抛出（不套用重试管线）——预热是优化，
    /// 是否容忍失败由调用方决定（catch 后降级启动或直接失败均可）。</para></summary>
    public static async Task PreWarmAsync(DbOptions options, int count, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (TProvider.Dialect == SqlDialect.Sqlite) return;

        string cs = options.ResolveConnectionString();
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            DbConnection? connection = null;
            try
            {
                connection = TProvider.CreateConnection(cs, options);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(options.ConnectionTimeout);
                await connection.OpenAsync(cts.Token).ConfigureAwait(false);
                // 与 CreateAsync 同口径：预热连接也过 Provider 初始化钩子（SQLite 无此路径，
                // PG/MySQL 钩子为方言初始化），归还池后的首用行为与正常连接一致。
                await TProvider.InitializeConnectionAsync(connection, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                // Dispose = 归还池（这正是预热动作本身）；清理失败不覆盖建连异常。
                await DisposeConnectionSafelyAsync(connection).ConfigureAwait(false);
            }
        }
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

    /// <summary>取读路由连接（会话级复用）。
    /// <para><b>为什么无需额外同步</b>：本方法只在会话持有操作租约期间被调用（读租约在
    /// <c>QueryBuilder.AcquireConnectionLeaseAsync</c> 内取得，而该调用点在
    /// <c>SessionOperationState.Enter</c> 之后），操作门禁保证同一会话同时最多一个操作。</para>
    /// <para><b>失效重建</b>：连接被外部关闭或断开时丢弃并按全新连接重建——Provider 初始化
    /// 随新物理句柄一并补设（ITM-207 的初始化契约不因复用而豁免）。</para></summary>
    private async ValueTask<DbConnection> AcquireReadConnectionAsync(CancellationToken cancellationToken)
    {
        DbConnection? existing = _readConnection;
        if (existing is not null)
        {
            if (existing.State == ConnectionState.Open)
                return existing;
            await DisposeReadConnectionAsync().ConfigureAwait(false);
        }

        DbConnection created = TProvider.CreateConnection(_readConnectionString!, _options);
        try
        {
            // READ-003（2026-09-22）：与主连接 CreateAsync 同口径——连接建立与初始化共享连接
            // 超时和调用方取消。此前只吃调用方 ct，ct == default 时读连接建立可无界挂起。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.ConnectionTimeout);
            await created.OpenAsync(cts.Token).ConfigureAwait(false);
            if (_readConnInitializer is not null)
                await _readConnInitializer(created, cts.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try { await created.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupException) { exception.Data["PalORM.ConnectionCleanupException"] = cleanupException; }
            throw;
        }
        _readConnection = created;
        return created;
    }

    /// <summary>释放并清空会话持有的读连接，返回释放异常（无异常返回 null）。
    /// 返回而非抛出：两个调用方对失败的处理不同——重建路径下连接已被判定不可用，
    /// 其释放失败无诊断价值；会话释放路径须按"主异常保留"约定挂到 Data 上。</summary>
    private async Task<Exception?> DisposeReadConnectionAsync()
    {
        DbConnection? connection = _readConnection;
        _readConnection = null;
        if (connection is null) return null;
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

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

    /// <summary>当前会话是否有活动事务（含经 <see cref="BeginTransactionAsync"/> 自开与
    /// <see cref="UseTransaction"/> 外部设入两种来源）。
    /// <para><b>为什么需要公开</b>：事务级语义的 API 需要调用方自查前置条件。典型是
    /// PG 咨询锁（<c>pg_advisory_xact_lock</c>）——事务外调用会获得锁但立即释放，
    /// 方法却正常返回，调用方以为临界区已持锁，跨进程互斥形同虚设且无任何错误信号。
    /// 有本属性后这类 API 可以显式失败而非静默无效。</para>
    /// <para><b>与失效契约的关系</b>：外部设入的事务被 Dispose 时（ITM-640），执行路径的
    /// <see cref="GetActiveTransaction"/> 会响亮抛异常；本属性是事实查询，返回 false（该状态下
    /// 没有可用事务），不跟着抛——调用方自查前置条件不该拿到与自身语义无关的 ITM-640 异常
    /// （API-002，2026-09-22：此前实现直接调 GetActiveTransaction，与本文档矛盾）。</para></summary>
    public bool IsInTransaction => _operationState.HasUsableTransaction;

    /// <summary>设置当前会话的事务。设置后所有后续查询在此事务内执行。
    /// 调用 CommitAsync()/RollbackAsync() 后需再次设置或清空 (UseTransaction(null))。
    /// <para><b>失效契约（ITM-640）</b>: 设入的事务若在会话外被 Dispose（Connection 置 null），
    /// 后续命令将抛 <see cref="InvalidOperationException"/> 而非静默降级为自动提交——
    /// 写操作脱离事务是无反馈的数据完整性风险。调用 <see cref="UseTransaction"/>(null) 可显式
    /// 清场回归自动提交。内部经 <see cref="BeginTransactionAsync"/> 开启的事务完成后的残留
    /// 仍走静默清理（手动 begin→commit→继续使用是正常流程）。</para></summary>
    public DataSession<TProvider> UseTransaction(DbTransaction? tran)
    {
        // ITM-606: 先查 disposed——tran.Connection == null 时下方 ReferenceEquals 永远 false，
        // 会遮蔽 SessionOperationState.UseTransaction 中"Cannot use a disposed transaction"的精确消息。
        if (tran is not null && !SessionOperationState.IsTransactionAlive(tran))
            throw new ArgumentException("Cannot use a disposed transaction (its Connection is null). "
                + "Pass a transaction from an open DbConnection, or null to clear.", nameof(tran));
        if (tran is not null && !ReferenceEquals(tran.Connection, _conn))
            throw new ArgumentException(
                "The transaction must belong to the DataSession's primary connection.", nameof(tran));
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

    /// <summary>设置会话默认命令超时，并以**合并后的当前配置**重建弹性执行器。
    /// <para>"重建"意味着：此前经 <see cref="WithRetry"/>/<see cref="WithCircuitBreaker"/>
    /// 做过的会话级配置**保留**（三者都落到同一份 <c>_options</c>，叠加而非互相清空）；
    /// 已通过 <c>From&lt;T&gt;()</c> 创建的 builder 因快照语义不受影响（继续用旧执行器）。</para></summary>
    public DataSession<TProvider> WithTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout.Ticks, 0, nameof(timeout));
        UpdateResilience(_options with { CommandTimeout = timeout });
        return this;
    }

    /// <summary>启用重试策略——仅重试 Provider 判定的瞬时数据库故障和内部命令超时，并重置当前弹性策略状态。
    /// <para><b>作用域（v5.4 起）</b>: 覆盖 ① 连接建立（<see cref="CreateAsync"/> 自有循环）
    /// ② 只读查询内置管线——From&lt;T&gt;() SELECT 家族（ToList/First/Single）、GetAsync、GetAllAsync、
    /// Count/Sum/Max/Min/Avg。<b>不覆盖</b>写入路径（Insert/Update/Delete/Save/ExecuteNonQueryAsync/
    /// Bulk/StoredProc/原始 SQL 家族）与事务内查询——非幂等写重试有重复执行风险
    /// （<see cref="ResilienceExecutor.ExecuteAsync{T}"/> 的幂等性契约），事务内重试会以次生异常
    /// 掩盖根因。此类路径的显式弹性需求用 <see cref="ExecuteWithResilience{T}"/> 包裹。</para>
    /// <para><b>快照语义</b>: 已通过 From&lt;T&gt;() 创建的 builder 捕获创建时的策略实例，
    /// 本方法不回灌既有 builder（配置变更受操作门禁保护，无并发撕裂）。</para></summary>
    public DataSession<TProvider> WithRetry(int maxRetries, Func<int, TimeSpan>? backoff = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        UpdateResilience(_options with { MaxRetries = maxRetries, RetryBackoff = backoff ?? _options.RetryBackoff });
        return this;
    }

    /// <summary>启用熔断器——连续最终失败达到阈值后快速失败，并重置当前弹性策略状态。
    /// <para><b>作用域（v5.4 起）</b>: 与 <see cref="WithRetry"/> 同口径——覆盖连接建立与
    /// 只读查询内置管线；写入路径与事务内查询不计入熔断也不受开闸影响（直连语义）。</para>
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

        // v5.6：读连接由会话持有，在此统一释放（DisposeAsync 先等待全部活动操作结束，
        // 故不存在无飞行查询仍持有该连接的情形）。
        if (await DisposeReadConnectionAsync().ConfigureAwait(false) is { } readConnectionException)
            RecordCleanupException(ref cleanupException, readConnectionException);

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

    // v4.1 cache key includes softDelete/tenant flags; _ignoreFilters changes the key (not cached when ignoring)
    // S2743：用非泛型 DataSessionCache 持有，跨 TProvider 共享
    /// <summary>取默认过滤的三种拼接形态（缓存）。<b>v5.6</b>：命中路径走 <c>TryGetValue</c>——
    /// 原实现用 <c>GetOrAdd(key, 捕获 lambda)</c>，factory 捕获 <c>_tenantParameterName</c> 所在
    /// 实例语境，每次调用都分配一个显示类与委托（实测 96B/次），即使缓存命中。未命中才构建并
    /// <c>TryAdd</c>；并发下同键可能被构建多次，但同输入恒同输出，正确性无影响。</summary>
    private DefaultFilterForms GetDefaultFilterForms<T>() where T : class, new()
    {
        bool hasSoftDelete = !_ignoreFilters && (GetEntityFeatures<T>() & EntityFeatures.SoftDelete) != 0;
        bool hasTenant = HasTenantFilter<T>();
        if (!hasSoftDelete && !hasTenant) return DefaultFilterForms.Empty;
        (Type, SqlDialect, bool, bool) key = (typeof(T), TProvider.Dialect, hasSoftDelete, hasTenant);
        if (DataSessionCache.FilterFormsCache.TryGetValue(key, out DefaultFilterForms cached))
            return cached;
        string softDelete = hasSoftDelete ? $"{TProvider.QuoteIdentifier("deleted_at")} IS NULL" : "";
        string condition;
        if (!hasTenant)
        {
            condition = softDelete;
        }
        else
        {
            string tenant = $"{TProvider.QuoteIdentifier("tenant_id")} = {_tenantParameterName}";
            condition = softDelete.Length == 0 ? tenant : $"{softDelete} AND {tenant}";
        }
        DefaultFilterForms forms = DefaultFilterForms.FromCondition(condition);
        DataSessionCache.FilterFormsCache.TryAdd(key, forms);
        return forms;
    }

    /// <summary>默认过滤的裸条件（COUNT 等直接拼 WHERE 的调用点用）。</summary>
    private string GetDefaultFilterCondition<T>() where T : class, new()
        => GetDefaultFilterForms<T>().Condition;

    /// <summary>已有 WHERE 时的追加片段：" AND cond" 或空。</summary>
    private string GetDefaultFilterFragment<T>() where T : class, new()
        => GetDefaultFilterForms<T>().AndFragment;

    /// <summary>独立 WHERE 子句：" WHERE cond" 或空。</summary>
    private string GetDefaultFilterWhereClause<T>() where T : class, new()
        => GetDefaultFilterForms<T>().WhereClause;

    /// <summary>为默认过滤条件绑定参数。任何拼接了 GetDefaultFilter* 结果的命令都必须调用。</summary>
    private void BindDefaultFilterParameters<T>(DbCommand cmd) where T : class, new()
    {
        if (HasTenantFilter<T>())
            cmd.Parameters.Add(TProvider.CreateParameter(_tenantParameterName, _tenantId));
    }

    // ─── 租户过滤 SQL 片段缓存（M1，v5.7）──────────────────────
    // GetDefaultFilterForms 覆盖 SELECT 家族的三形态；以下三个入口覆盖写路径的
    // 四处缓存外重建（UpdateCoreAsync 追加 / DeleteAsync 软删全句 / DeleteAsync
    // 物理追加 / BulkDeleteAsync 后缀）。命中走 TryGetValue（v5.6 口径，无闭包分配），
    // 并发下同键可能被构建多次，同输入恒同输出，正确性无影响。

    /// <summary>租户过滤追加片段（per-Dialect 缓存）：" AND {quote(tenant_id)} = @__tenant0"。</summary>
    private static string GetTenantAppendFragment()
    {
        SqlDialect dialect = TProvider.Dialect;
        if (DataSessionCache.TenantAppendFragmentCache.TryGetValue(dialect, out string? cached))
            return cached;
        string fragment = $" AND {TProvider.QuoteIdentifier("tenant_id")} = {_tenantParameterName}";
        DataSessionCache.TenantAppendFragmentCache.TryAdd(dialect, fragment);
        return fragment;
    }

    /// <summary>带租户过滤的 Update/Delete 语句（per-(Type, Dialect) 缓存）——
    /// baseSql 为已缓存的生成语句，与租户后缀的拼接结果原先每次调用重建。</summary>
    private static string GetTenantWrappedSql<T>(
        System.Collections.Concurrent.ConcurrentDictionary<(Type, SqlDialect), string> cache,
        string baseSql)
        where T : class, new()
    {
        (Type, SqlDialect) key = (typeof(T), TProvider.Dialect);
        if (cache.TryGetValue(key, out string? cached))
            return cached;
        string sql = baseSql + GetTenantAppendFragment();
        cache.TryAdd(key, sql);
        return sql;
    }

    /// <summary>软删 UPDATE 全句（per-(Type, Dialect, hasTenant) 缓存）——原先 DeleteAsync
    /// 软删路径每次调用 5 次 QuoteIdentifier + 全句插值。</summary>
    private static string GetSoftDeleteUpdateSql<T>(string tableName, bool hasTenant) where T : class, new()
    {
        (Type, SqlDialect, bool) key = (typeof(T), TProvider.Dialect, hasTenant);
        if (DataSessionCache.SoftDeleteUpdateSqlCache.TryGetValue(key, out string? cached))
            return cached;
        string tenantFilter = hasTenant ? GetTenantAppendFragment() : "";
        // S2077 报备（M1-5，与原调用点同）：插值成分全为 QuoteIdentifier 标识符、
        // CurrentTimestampExpression 与 const 参数名——用户值经 @p0 绑定
#pragma warning disable S2077
        string sql = $"UPDATE {TProvider.QuoteIdentifier(tableName)} SET {TProvider.QuoteIdentifier("deleted_at")} = {TProvider.CurrentTimestampExpression} WHERE {TProvider.QuoteIdentifier(GetPkColumn<T>())} = @p0 AND {TProvider.QuoteIdentifier("deleted_at")} IS NULL{tenantFilter}";
#pragma warning restore S2077
        DataSessionCache.SoftDeleteUpdateSqlCache.TryAdd(key, sql);
        return sql;
    }

    private SessionOperationState.SessionOperationLease EnterOperation(
        object? operationOwner = null)
        => _operationState.Enter(operationOwner);

    /// <summary>创建批量执行器——把 N 条非查询语句压成一次往返（方言支持时，实测 PG 3.4×/10 语句）。
    /// 见 <see cref="SessionBatch{TProvider}"/> 的语义契约。</summary>
    public SessionBatch<TProvider> CreateBatch() => new(this);

    internal DbCommand CreateCommandForBatch() => CreateCommand();

    internal SessionOperationState.SessionOperationLease EnterBatchOperation(
        object? operationOwner = null)
        => EnterOperation(operationOwner);

    internal DbConnection BatchConnection => _conn;

    internal DbTransaction? GetActiveBatchTransaction() => GetActiveTransaction();

    internal int BatchCommandTimeoutSeconds => _options.CommandTimeoutSeconds;

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

    /// <summary>只读查询的弹性执行入口——WithRetry/WithCircuitBreaker 在内置管线的接入点
    /// （v5.4 行为变更，评审 P1-a：此前弹性配置对内置管线无效）。
    /// <para><b>接入条件</b>: 命令不带事务且策略非直通。事务内重试会以次生异常掩盖根因
    /// （如 PG aborted transaction），且跨重试的一致性快照语义不成立——事务内读取保持直连。</para>
    /// <para><b>覆盖面</b>: GetAsync/GetAllAsync/聚合五兄弟与 From&lt;T&gt;() SELECT 管线。
    /// 连接建立的重试由 <see cref="CreateAsync"/> 自有循环承担；写入/Bulk/StoredProc/
    /// 原始 SQL 家族维持直连（幂等性契约见 <see cref="ResilienceExecutor.ExecuteAsync{T}"/>，
    /// 显式需求用 <see cref="ExecuteWithResilience{T}"/> 包裹）。</para></summary>
    /// <summary>P2-1：写路径的直调重载（行数形态）——调用方已持有命令时经此入口，省掉
    /// <c>async token => (long)await cmd.ExecuteNonQueryAsync(token)</c> 的委托与
    /// display class（实测约 208 B/行，含两层包装）。
    /// <para><b>语义与委托版逐位一致</b>：直通分支（策略直通或事务内）直接执行并返回；
    /// 非直通分支才包 <see cref="ResilienceExecutor.ExecuteWithTimeoutAsync"/>——
    /// 此时仍需委托（超时包装要求），该形态本就少见（写入路径默认不重试，
    /// 只有显式配置了非零 CommandTimeout 才会走到）。</para>
    /// <para><b>为什么值得单独一个重载</b>：单条写路径（Update/Delete/ExecuteAsync）
    /// 每次调用都过这里，是每操作固定成本；208 B 在 MySQL Insert 的 4241 B/行上约 5%，
    /// 在 BulkInsert 的 1222 B/行上约 17%。</para></summary>
    private ValueTask<int> ExecuteWriteRowsAsync(DbCommand command, CancellationToken ct)
    {
        ResilienceExecutor executor = Volatile.Read(ref _resilience);
        if (executor.IsPassThrough || GetActiveTransaction() is not null)
            return ExecuteRowsCoreAsync(command, ct);
        return executor.ExecuteWithTimeoutAsync(
            token => ExecuteRowsCoreAsync(command, token).AsTask(), ct);
    }

    private static async ValueTask<int> ExecuteRowsCoreAsync(DbCommand command, CancellationToken ct)
        => await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);


    private async ValueTask<T> ExecuteReadPipelineAsync<T>(
        Func<CancellationToken, Task<T>> attemptCore, CancellationToken ct)
    {
        ResilienceExecutor executor = Volatile.Read(ref _resilience);
        if (executor.IsPassThrough || GetActiveTransaction() is not null)
            return await attemptCore(ct).ConfigureAwait(false);
        return await executor.ExecuteAsync(attemptCore, ct).ConfigureAwait(false);
    }

    /// <summary>将复合格式项映射为参数名，参数值保持原始对象。</summary>
    private static string FormatSqlWithParameters(FormattableString sql)
        => FormattableSqlFormatter.Format(sql);

    /// <summary>FormattableString → DbParameter 绑定。</summary>
    private static void BindFormattableParameters(DbCommand cmd, FormattableString sql)
    {
        for (int i = 0; i < sql.ArgumentCount; i++)
        {
            object? value = sql.GetArgument(i);
            DbParameter param = TProvider.CreateParameter(ParameterNameCache.GetName(i), value);
            cmd.Parameters.Add(param);
        }
    }
}
