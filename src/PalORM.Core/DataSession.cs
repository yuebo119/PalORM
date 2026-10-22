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
/// 一个会话仅支持一个活动数据库操作；重叠操作会明确失败。
/// <para><b>生命周期契约（LIFE-002，2026-09-23）</b>：主连接在构造期建立并持有到
/// <see cref="DisposeAsync"/>，<b>会话级重连不受支持</b>——连接被中间设备静默掐断后，该会话的
/// 后续操作会持续失败（弹性重试不会重建主连接）。推荐用法是<b>一个请求一个会话</b>
/// （per-request scope）：长生命周期会话请显式处理重建（新建会话）或使用
/// <c>DbOptions.ReadConnectionString</c> 的读路由。并发能力由"会话数 = 并发度"表达，
/// 多个会话共享同一 <see cref="DbOptions"/> 实例即可。</para></summary>
/// <typeparam name="TProvider">数据库 Provider 类型（PostgreSqlProvider / MySqlProvider / SqliteProvider）。</typeparam>
public sealed partial class DataSession<TProvider> : IAsyncDisposable, IQuerySessionResilience
    where TProvider : IDbProvider
{
    private readonly DbConnection _conn;
    /// <summary>是否拥有主连接（默认 true）。测试/基准的"共享已打开连接 + 每操作一个会话"形态
    /// 传 false：会话 Dispose 释放复用槽与读侧资源，但不关闭/释放调用方的连接。</summary>
    private readonly bool _ownsConnection;
    private DbOptions _options;
    /// <summary>命令超时的 ADO.NET 秒值会话级快照——<see cref="DbOptions.ToCommandTimeoutSeconds"/>
    /// 含 <c>Math.Ceiling(TotalSeconds)</c> 双精度除法，原实现在每条命令构造与每次复用槽重设时
    /// 现算（单行 CRUD 每操作 1~2 次）。会话配置不可变（WithTimeout 经 UpdateResilienceCore
    /// 整体重建），故在构造与配置发布点写入，热路径只读 int。</summary>
    private int _commandTimeoutSeconds;
    /// <summary>W3（两轴清单轮 2026-10-14）：弹性执行器<b>惰性创建</b>——原构造器无条件
    /// new ResilienceExecutor（含熔断器与 Lock 共 ~228B/个，探针实测），而 Insert 家族
    /// （InsertCore/FinishInsert 直执）、纯事务会话与批量族从不读弹性（写入路径直连契约，
    /// 事务族经 ExecuteWriteRowsAsync 的事务短路也绕开）——per-op 会话形态每 op 白付。
    /// 首个读/写弹性触点（<see cref="Resilience"/> getter）时 CAS 挂接；
    /// 池化会话在 <see cref="MarkPooled"/> 强制物化，池语义（基准实例跨租期存活）逐位不变。</summary>
    private ResilienceExecutor? _resilience;
    /// <summary>S3b：弹性配置代际——每次 WithTimeout/WithRetry/WithCircuitBreaker 重建执行器时 +1。
    /// QueryBuilder 捕获创建时的代际；执行时若代际未变才懒取会话执行器，变了则用捕获的
    /// DbOptions 快照自建，保留“From 之后改配置不影响既有 builder”的原快照语义。</summary>
    internal int ResilienceGeneration;
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
    /// <summary>读连接提供者——W1（深挖轮 2026-10-14）起<b>惰性创建</b>：原构造器无条件
    /// 挂接方法组委托（~64B/个），而 GetByKey/Update/Insert 等主连接操作从不走读路由——
    /// per-op 会话形态每 op 白付两个委托。首个 From&lt;T&gt;()/读路由命令创建时 <c>??=</c> 初始化，
    /// 并发竞态良性（同 _querySlot 先例：引用写原子、败者被 GC）。
    /// 缓存为实例字段的原因不变：方法组直接传给 QueryBuilderContext 会每次 From&lt;T&gt;() 分配。</summary>
    private Func<CancellationToken, ValueTask<DbConnection>>? _readConnProvider;
    /// <summary>读连接失效上报（READ-001，2026-09-23）——配置读路由时非 null，
    /// 由读路径在瞬时失败时调用（见 <see cref="InvalidateReadConnectionAsync"/>）。</summary>
    private readonly Func<ValueTask>? _readConnInvalidator;
    /// <summary>并行读连接归还回调（ARCH-001，2026-09-23）——W1 起惰性创建（首个
    /// From&lt;T&gt;() 时初始化，QueryBuilder 上下文恒拿到非 null）：并行读作用域在未配置读路由时
    /// 也生效（连接回落主连接串），From&lt;T&gt;() 管线是它的唯一消费面。</summary>
    private Func<DbConnection, bool, ValueTask>? _readConnReturner;
    /// <summary>A9（2026-10-01）：读查询命令复用槽（PL-2 惰性晋升的泛化）——惰性创建：
    /// 并行读作用域内并发 From&lt;T&gt;() 的竞态只会各建一个空槽（引用写原子、败者被 GC、
    /// 槽在禁用晋升下永无命令可漏）。</summary>
    internal ReusableQuerySlot? _querySlot;

    /// <summary>C11（2026-10-12 审读重开·安全子集）：日志类别串每闭合泛型实例缓存一次
    ///（原每会话创建时插值分配一个新串，仅 LoggerFactory 配置时发生）。</summary>
    private static readonly string LogCategory = $"PalORM.{TProvider.Name}";

    /// <summary>R2-#3（2026-10-13 微项）：Provider 静态方法组的每封闭泛型缓存——
    /// TProvider.IsTransient 是 static 成员，方法组转委托在每会话构造时分配一个新委托
    ///（~56B，实例无关）；嵌套静态类按 (TProvider) 闭合类型缓存一份，第二个会话起零分配。
    /// 实例方法组（_readConnProvider 等）绑定 this 不可缓存，维持每会话分配。</summary>
    private static class ProviderDelegates
    {
        public static readonly Func<Exception, bool> IsTransient = TProvider.IsTransient;
        public static readonly Action<DbConnection> MarkStateUnknown = TProvider.MarkConnectionStateUnknown;
    }
    /// <summary>并行读作用域的连接池（ARCH-001）——作用域内创建的读连接（全部）与其中空闲可复用的。
    /// ITM-796（r23）：并发读租约（最多 MaxParallelReads 路）可同时进入获取/归还路径，
    /// 两个集合的全部操作（Pop/Add/Contains/Push/Clear）必须持 <see cref="_parallelReadPoolLock"/>。
    /// O2（2026-10-01）：惰性初始化——per-operation 会话（每操作 new DataSession 且从不进入
    /// 并行读作用域）不付两个容器与其背衬数组的分配，首次进入并行读获取/归还路径时才建；
    /// 全部读写点持 <see cref="_parallelReadPoolLock"/>，null 判定与惰性赋值都在锁内，竞态安全。</summary>
    private List<DbConnection>? _parallelReadConnections;
    private Stack<DbConnection>? _idleParallelReadConnections;
    private readonly Lock _parallelReadPoolLock = new();

    internal DataSession(DbConnection conn, DbOptions options, List<IQueryInterceptor> interceptors, ILogger? logger = null, bool ownsConnection = true)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(options);
        _conn = conn;
        _ownsConnection = ownsConnection;
        _options = options;
        _commandTimeoutSeconds = options.CommandTimeoutSeconds;
        // W3（两轴清单轮 2026-10-14）：弹性执行器改惰性（见 _resilience 字段 doc）——
        // 原此处无条件 new ResilienceExecutor（228B/个）；首个弹性触点或 MarkPooled 时建。
        // MySQL-9/P1（2026-09-27）：空拦截器列表跳过 OrderBy LINQ——大多数会话不配拦截器，
        // 原实现对空集合也分配迭代器 + 委托 + 新 List（探针十八拆解：CreateAsync 每 op
        // 2625B 里约 180B 属此）。空列表直接复用 CreateAsync 传入的私有副本
        // （options.Interceptors?.ToList() 的产物，无外部引用，munate 安全）；
        // 非空时保持防御性复制语义（OrderBy().ToList()）。
        _interceptors = interceptors.Count == 0
            ? interceptors
            : interceptors.OrderBy(i => i.Priority).ToList();
        _logger = logger ?? NullLogger.Instance;
        // ITM-624 同型面（修复侧纪律卡第三问实证）：读连接每次创建都读 _options 字段而非
        // 捕获构造期 options——WithTimeout/WithRetry 后读连接与主连接的池参数/超时口径一致。
        _readConnectionString = options.ResolveReadConnectionString();
        // W1（深挖轮 2026-10-14）：_readConnProvider/_readConnReturner 改惰性——原构造器
        // 无条件挂接两个方法组委托，而主连接操作（GetByKey/Insert/Update/事务族）从不走
        // 读路由，per-op 会话形态每 op 白付 ~128B。首个 From<T>()/读路由命令时 ??= 初始化。
        // ARCH-001 语义不变：作用域内走池（未配置读连接串时回落主连接串），作用域外
        // 未配置读路由时 AcquireReadConnectionAsync 直接返回主连接（ForRead 既有语义）。
        // READ-001（2026-09-23）：读连接失效上报——只在配置了读路由时挂接（方法组一次性缓存，
        // 不在 From<T>() 里新建委托）。见 InvalidateReadConnectionIfCurrentAsync。
        _readConnInvalidator = _readConnectionString is not null ? InvalidateReadConnectionAsync : null;
        // v5.0 阶段 5.2：读连接初始化器——无 ReadSessionSetupSql 时用 static 委托（零闭包分配），
        // 有时包装一层实例委托追加执行 ReadSessionSetupSql。
        _readConnInitializer = string.IsNullOrWhiteSpace(options.ReadSessionSetupSql)
            ? static (conn, ct) => TProvider.InitializeConnectionAsync(conn, ct)
            : async (conn, ct) =>
            {
                await TProvider.InitializeConnectionAsync(conn, ct).ConfigureAwait(false);
                // 同 CreateAsync 的 SessionSetupSql：先作废初始化登记再执行读会话 SQL
                TProvider.InvalidateConnectionInitialization(conn);
                await using DbCommand cmd = conn.CreateCommand();
                // ITM-624：读 _options 字段而非捕获构造期 options——WithTimeout/WithRetry 经
                // Volatile.Write 替换 _options 后，读连接初始化与主连接的超时口径保持一致。
                cmd.CommandTimeout = _commandTimeoutSeconds;
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
            // F5：非可取消 ct 走租借池（A3 补扫）——归还收在外层 finally（成功 return 与全部
            // 失败路径共用，不引入嵌套 try）
            CancellationTokenSource? openScope = null;
            try
            {
                connection = TProvider.CreateConnection(cs, options);
                openScope = RentOpenScopeCts(ct);
                // 条件武装：驱动池命中时 OpenAsync 同步完成（本地 SQLite/暖池），CancelAfter
                // 会白建一个定时器并入队一次取消登记（实测每会话 ~100B 与百纳秒级开销）。
                // 仅在打开未同步完成时才武装超时；已同步完成的路径不存在可超时的等待。
                Task openTask = connection.OpenAsync(openScope.Token);
                if (!openTask.IsCompleted)
                    openScope.CancelAfter(options.ConnectionTimeout);
                await openTask.ConfigureAwait(false);

                // Provider 初始化钩子（SQLite: PRAGMA foreign_keys/WAL）。
                // 与 OpenAsync 共享连接超时和调用方取消——初始化被锁阻塞时不再无限等待。
                await TProvider.InitializeConnectionAsync(connection, openScope.Token).ConfigureAwait(false);

                // v5.0 阶段 5.2：用户会话级 SQL（SET TIME ZONE / search_path / statement_timeout 等）。
                // 在 Provider 钩子后执行——Provider 钩子可能设置影响后续 SET 的状态（如 SQLite PRAGMA）。
                // 多条 SQL 用分号分隔，一次 ExecuteNonQueryAsync 执行（三方言均支持多语句）。
                if (!string.IsNullOrWhiteSpace(options.SessionSetupSql))
                {
                    // 先作废再执行：会话 SQL 可改写连接态（含部分执行后失败），该物理连接
                    // 下次被池化复用时须重新完整初始化，不得把本会话的定制带给下一个会话
                    TProvider.InvalidateConnectionInitialization(connection);
                    await using DbCommand setupCmd = connection.CreateCommand();
                    setupCmd.CommandTimeout = options.CommandTimeoutSeconds;
                    setupCmd.CommandText = options.SessionSetupSql;
                    await setupCmd.ExecuteNonQueryAsync(openScope.Token).ConfigureAwait(false);
                }

                // R3 回退登记（2026-10-12）：曾改共享空表省 ~32B/会话——构造器对空表"直接复用传入
                // 引用"的前提是列表私有（AddInterceptor 会就地变异 _interceptors），共享表会被
                // 任意会话的 AddInterceptor 全局污染（跨会话串味 + 枚举竞态，全量套件实测抓红）。
                var interceptors = options.Interceptors?.ToList() ?? [];
                ILogger? logger = options.LoggerFactory?.CreateLogger(LogCategory);
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
                if (openScope is not null)
                    ReturnOpenScopeCts(openScope, ct);
                await DisposeConnectionSafelyAsync(connection).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>预热连接池（C4，v5.6.0）：逐条打开 <paramref name="count"/> 条连接随即归还池，
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
                // F5：同 CreateAsync——非可取消 ct 走租借池
                CancellationTokenSource cts = RentOpenScopeCts(ct);
                try
                {
                    Task warmOpenTask = connection.OpenAsync(cts.Token);
                    if (!warmOpenTask.IsCompleted)
                        cts.CancelAfter(options.ConnectionTimeout);
                    await warmOpenTask.ConfigureAwait(false);
                    // 与 CreateAsync 同口径：预热连接也过 Provider 初始化钩子（SQLite 无此路径，
                    // PG/MySQL 钩子为方言初始化），归还池后的首用行为与正常连接一致。
                    await TProvider.InitializeConnectionAsync(connection, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    ReturnOpenScopeCts(cts, ct);
                }
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

    // F5（2026-10-13 全量重读微项包）：连接打开范围的 CTS——A3 纪律补扫（ResilienceExecutor
    // 早已落地的同型分支，建连四处此前漏扫）：ct 可取消走链接形态（源 token 注册不可跨次复用，
    // 用后 Dispose），否则走 <see cref="PooledCts"/> 租借（未取消时 TryReset 复用，约省一次
    // CTS+CancelAfter 定时器的每建连分配）。归还纪律与 ResilienceExecutor 相同：token 只在
    // 单次打开/初始化的 await 生命周期内传递，驱动在执行完成前注销自己的取消注册。
    private static CancellationTokenSource RentOpenScopeCts(CancellationToken ct)
        => ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : PooledCts.Rent();

    private static void ReturnOpenScopeCts(CancellationTokenSource cts, CancellationToken ct)
    {
        if (ct.CanBeCanceled) cts.Dispose();
        else PooledCts.Return(cts);
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
        // ARCH-001（2026-09-23）：并行读作用域内从作用域局部池取（用完归还）——同一条会话级
        // 读连接无法承载并发 reader。
        if (_operationState.ParallelReadsEnabled)
            return await AcquireParallelReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (_readConnectionString is null)
            return _conn;   // 未配置读路由：ForRead 语义 = 主连接（既有行为，不新建连接）

        DbConnection? existing = _readConnection;
        if (existing is not null)
        {
            if (existing.State == ConnectionState.Open)
                return existing;
            await DisposeReadConnectionAsync().ConfigureAwait(false);
        }

        DbConnection created = TProvider.CreateConnection(_readConnectionString, _options);
        try
        {
            // READ-003（2026-09-22）：与主连接 CreateAsync 同口径——连接建立与初始化共享连接
            // 超时和调用方取消。此前只吃调用方 ct，ct == default 时读连接建立可无界挂起。
            // F5：非可取消 ct 走租借池（同 CreateAsync 的 A3 补扫）。
            CancellationTokenSource cts = RentOpenScopeCts(cancellationToken);
            try
            {
                Task readOpenTask = created.OpenAsync(cts.Token);
                if (!readOpenTask.IsCompleted)
                    cts.CancelAfter(_options.ConnectionTimeout);
                await readOpenTask.ConfigureAwait(false);
                if (_readConnInitializer is not null)
                    await _readConnInitializer(created, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                ReturnOpenScopeCts(cts, cancellationToken);
            }
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

    /// <summary>丢弃会话持有的读连接（READ-001，2026-09-23）——读路径瞬时失败时经
    /// <see cref="QueryBuilder{T}.InvalidateReadConnectionAsync"/> 上报：静默掐断（NAT 超时 /
    /// LB 空闲切断、无 FIN/RST）后 <c>State</c> 仍为 Open，不丢弃则后续查询复用同一条死连接。
    /// 幂等：无读连接或已丢弃时为空操作。</summary>
    internal async ValueTask InvalidateReadConnectionAsync()
    {
        if (_readConnection is null) return;
        // 释放结果忽略：连接已不可用，清理异常无诊断价值（与 AcquireReadConnectionAsync
        // 丢弃失效连接时同口径）。
        await DisposeReadConnectionAsync().ConfigureAwait(false);
    }

    /// <summary>并行读作用域内的连接获取（ARCH-001）：空闲连接优先复用，否则新建——连接串取
    /// 读连接串，未配置时回落主连接串（同一库上的并行只读）。连接建立与初始化受 ConnectionTimeout
    /// 约束（与 READ-003 同口径）。</summary>
    private async ValueTask<DbConnection> AcquireParallelReadConnectionAsync(CancellationToken cancellationToken)
    {
        // ITM-796（r23）：池集合操作持锁——并发读租约可同时进入本方法，无锁 Pop/Add
        // 在竞态下可把同一空闲连接发放给两个 reader 或丢失登记（连接不被追踪/不归还）。
        lock (_parallelReadPoolLock)
        {
            if (_idleParallelReadConnections is { Count: > 0 })
                return _idleParallelReadConnections.Pop();
        }
        DbConnection created = TProvider.CreateConnection(
            _readConnectionString ?? _options.ResolveConnectionString(), _options);
        try
        {
            // F5：非可取消 ct 走租借池（READ-003 口径不变）
            CancellationTokenSource cts = RentOpenScopeCts(cancellationToken);
            try
            {
                Task readOpenTask = created.OpenAsync(cts.Token);
                if (!readOpenTask.IsCompleted)
                    cts.CancelAfter(_options.ConnectionTimeout);
                await readOpenTask.ConfigureAwait(false);
                if (_readConnInitializer is not null)
                    await _readConnInitializer(created, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                ReturnOpenScopeCts(cts, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // ITM-797②（r23）：Open/初始化失败时释放新建连接（与 AcquireReadConnectionAsync
            // 同口径——CreateAsync 泄漏教训族），此前裸抛泄漏物理句柄。
            try { await created.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupException) { exception.Data["PalORM.ConnectionCleanupException"] = cleanupException; }
            throw;
        }
        lock (_parallelReadPoolLock)
        {
            (_parallelReadConnections ??= []).Add(created);
        }
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
    /// <para>该标志在当前会话内<b>没有公开复位 API</b>：跨租户管理查询结束后请换一个会话
    /// （池化会话归还后重租即可），不要在同一会话继续做租户过滤查询。</para>
    /// <para>ITM-568: 与 AddInterceptor 同受门禁保护——有查询在飞时调用会明确失败，
    /// 防止飞行查询与过滤状态变更竞态产生跨租户/含软删数据。</para></summary>
    public DataSession<TProvider> IgnoreFilters()
    {
        using SessionOperationState.SessionOperationLease operation = _operationState.EnterConfigGate();
        _ignoreFilters = true;
        _tenantScopeEntry = null;  // T6：作用域判别式含 _ignoreFilters，变更即失效单条目缓存
        return this;
    }
    internal bool _ignoreFilters;

    /// <summary>动态添加查询拦截器（日志/缓存/审计），并按 <see cref="IQueryInterceptor.Priority"/> 执行。
    /// 与其他会话操作一样受门禁保护：有查询在飞时调用会明确失败（拦截器列表被执行管线枚举，无锁修改是竞态）。</summary>
    public DataSession<TProvider> AddInterceptor(IQueryInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        using SessionOperationState.SessionOperationLease operation = _operationState.EnterConfigGate();
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
        using SessionOperationState.SessionOperationLease operation = _operationState.EnterConfigGate();
        _tenantId = tenantId;
        _tenantScopeEntry = null;  // T6：作用域判别式含 _tenantId，切换即失效单条目缓存
        return this;
    }

    /// <summary>T6：每会话租户过滤作用域的单条目缓存——命中同实体类型时零拼接
    /// （消 <c>$"__t:{tenantId}"</c> 的 ~32 B/查询）。
    /// <para><b>为什么是类不是值元组</b>：(Type, Scope) 两字段的写法在并行读下有撕裂读风险
    /// （读到旧的 Type 配新的 Scope 即跨租户串数据）；单引用字段的赋值是原子的，
    /// 条目不可变，撕裂不可能。</para>
    /// <para><b>失效</b>：<see cref="IgnoreFilters"/> / <see cref="WithTenant"/> 显式清空——
    /// 两者是判别式（_ignoreFilters / _tenantId）的唯一变更点，且都受操作门禁保护（ITM-568）。</para></summary>
    private TenantScopeEntry? _tenantScopeEntry;

    private sealed class TenantScopeEntry(Type type, object tenantId)
    {
        public Type Type { get; } = type;
        /// <summary>ITM-866（r23）：判别式含租户值——并发 WithTenant 清缓存与 From&lt;T&gt;
        /// 写回交错时条目可能短暂 stale（Type 相同但租户已变），按 TenantId 值相等判定
        /// 避免 stale 命中。
        /// N6（2026-10-04 step23 全量复读）：存<b>装箱原值</b>而非 ToString 形态——T6 消掉了
        /// Scope 拼接，但命中比较路径对非 string 租户（int/long/Guid 装箱值）仍每查询
        /// <c>ToString()</c> 一次（~30B），T6"命中零拼接"的承诺此前只对 string 租户成立。
        /// 装箱值类型的 <c>Equals(object)</c> 是解箱值比较，零分配；比较语义见
        /// <see cref="DataSession{TProvider}.TenantValueEquals"/>。</summary>
        public object TenantId { get; } = tenantId;
        public string Scope { get; } = $"__t:{tenantId}";
    }
    internal object? _tenantId;

    // ─── 弹性配置链式 API ────────────────────────────────

    /// <summary>设置事务隔离级别。</summary>
    public DataSession<TProvider> WithIsolationLevel(IsolationLevel level)
    {
        using SessionOperationState.SessionOperationLease operation = _operationState.EnterConfigGate();
        _isolationLevel = level;
        return this;
    }
    private IsolationLevel _isolationLevel = IsolationLevel.ReadCommitted;

    /// <summary>设置会话默认命令超时，并以**合并后的当前配置**重建弹性执行器。
    /// <para>"重建"意味着：此前经 <see cref="WithRetry"/>/<see cref="WithCircuitBreaker"/>
    /// 做过的会话级配置**保留**（三者都落到同一份 <c>_options</c>，叠加而非互相清空）；
    /// 已通过 <c>From&lt;T&gt;()</c> 创建的 builder 因快照语义不受影响（继续用旧执行器）。</para>
    /// <para><b>ITM-908 口径不对称登记</b>：本入口拒绝 Zero/负值，而
    /// <see cref="DbOptions.CommandTimeout"/> 的 Zero 语义是"无限等待"（ITM-619）——
    /// 会话级无法把已设的超时**恢复**为无限等待（只能重建会话）。差异源于公共 API 的
    /// 参数校验口径（ThrowIfLessThanOrEqual），Zero 透传属行为变更，未采纳。</para></summary>
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

    /// <summary>W3：弹性执行器的惰性取用器——首个触点创建（CAS 良性竞态：并发首触只多建
    /// 一个实例，败者被 GC；单活动操作契约下实际不触发，同 _querySlot/_readConnProvider 先例）。
    /// 全部弹性读点经此取器，插入/批量/事务短路路径不再触发创建。</summary>
    internal ResilienceExecutor Resilience
    {
        get
        {
            ResilienceExecutor? executor = Volatile.Read(ref _resilience);
            if (executor is not null)
                return executor;
            var created = new ResilienceExecutor(_options, ProviderDelegates.IsTransient, typeof(TProvider));
            return Interlocked.CompareExchange(ref _resilience, created, null) ?? created;
        }
    }

    /// <summary>S3：会话是否配置了任何弹性语义。全零（MaxRetries=0 且熔断禁用且无总预算）
    /// 时读路径完全不经执行器，连惰性创建都不发生。</summary>
    internal bool ResilienceConfigured
        => _options.MaxRetries > 0
            || _options.CircuitBreakerThreshold > 0
            || _options.OverallDeadline > TimeSpan.Zero;

    /// <summary>S3：首次尝试直接执行的可用条件——尚无执行器（会话级熔断器恒从 Closed 起步，
    /// 首次失败前无需 Enter 检查）、非进程级熔断（进程级必须先查共享开闸状态）、且未设总预算
    /// （总预算由执行器统一执法）。满足时成功路径零执行器、零超时 CTS、零重试委托。</summary>
    internal bool DirectFirstReadAllowed
        => _resilience is null
            && _options.CircuitBreakerScope != CircuitBreakerScope.Process
            && _options.OverallDeadline <= TimeSpan.Zero;

    ResilienceExecutor IQuerySessionResilience.GetResilienceExecutor() => Resilience;
    bool IQuerySessionResilience.SessionResilienceConfigured => ResilienceConfigured;
    bool IQuerySessionResilience.SessionDirectFirstAllowed => DirectFirstReadAllowed;
    int IQuerySessionResilience.ResilienceGeneration => ResilienceGeneration;

    /// <summary>使用会话级弹性策略执行操作（自动重试+熔断）。
    /// <para><b>并发契约（OPS-001，2026-09-23）</b>：本入口<b>不占操作门禁</b>——它是给调用方
    /// 自带编排（并行扇出、自定义批处理）的逃生门，可被多线程并发调用，因此：(1) 不要与会话的
    /// 其他操作（<c>From&lt;T&gt;()</c> 管线、Bulk 家族、事务 API）交错使用，那些路径受单活动操作
    /// 门禁保护，混合使用会让门禁失去意义；(2) 会话级配置变更（<c>WithRetry</c>/<c>WithTimeout</c>/
    /// <c>WithCircuitBreaker</c>）与飞行中的本入口调用互斥由调用方负责；(3) 熔断器与执行器的
    /// 状态访问已按并发加固（无锁快路径 + 锁内计数），但<b>熔断作用域为会话级</b>时并发调用的
    /// 失败计数共享同一实例，若需跨会话共享请设 <see cref="DbOptions.CircuitBreakerScope"/> 为
    /// <see cref="CircuitBreakerScope.Process"/>。</para></summary>
    public async ValueTask<T> ExecuteWithResilience<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
        => await Resilience.ExecuteAsync(operation, ct).ConfigureAwait(false);

    /// <summary>使用会话级弹性策略执行操作（无返回值）。</summary>
    public async ValueTask ExecuteWithResilience(Func<CancellationToken, Task> operation, CancellationToken ct = default)
        => await Resilience.ExecuteAsync(operation, ct).ConfigureAwait(false);

    private void UpdateResilience(DbOptions options)
    {
        // ITM-568: 配置发布与飞行查询互斥（门禁），消除"读到新旧混合配置"窗口；
        // ITM-577: 注释同步——一致性由门禁保证（单活动操作），不再依赖 Volatile.Read 配对协议。
        using SessionOperationState.SessionOperationLease operation = _operationState.EnterConfigGate();
        UpdateResilienceCore(options);
    }

    private void UpdateResilienceCore(DbOptions options)
    {
        // ITM-577: 此前注释声称"读取方经 Volatile.Read(_resilience) 可见一致 _options"，但
        // CRUD 路径全部普通读、只有 ExecuteWithResilience 遵守该配对——声明与读方不符。
        // 现一致性由 UpdateResilience 的操作门禁保证（配置变更与飞行查询互斥）；
        // Volatile.Write 保留为跨线程发布的最低保障。
        Volatile.Write(ref _options, options);
        _commandTimeoutSeconds = options.CommandTimeoutSeconds;
        Volatile.Write(ref _resilience, new ResilienceExecutor(options, TProvider.IsTransient, typeof(TProvider)));
        ResilienceGeneration++;
    }

    /// <summary>停止后台操作，释放连接。幂等。</summary>
    /// <summary>所属连接池（非池化会话为 null）——DisposeAsync 在池化形态下走归还路径
    ///（驻留 + 瞬态复位 + 入队），不关闭连接、不释放复用槽（PL-2/A9/P1-1 槽跨租用存活
    /// 是池化的核心收益）。由 <see cref="DataSessionPool{TProvider}"/> 在创建后立即注入。</summary>
    internal DataSessionPool<TProvider>? _pool;
    internal DbOptions? _poolBaseOptions;
    internal ResilienceExecutor? _poolBaseResilience;

    /// <summary>池标记注入（池创建会话后、首次租出前调用）——记录基准 options/resilience
    /// 供归还时复位（With* 链在租用期内的覆写不带入下一租期）。</summary>
    internal void MarkPooled(DataSessionPool<TProvider> pool)
    {
        _pool = pool;
        _poolBaseOptions = _options;
        // W3：强制物化基准执行器——池语义要求基准实例跨租期存活（租用期 WithResilience
        // 覆写经 ReturnToPoolAsync 复位回本实例，熔断状态不因归还重置）；池创建一次的成本。
        _poolBaseResilience = Resilience;
    }

    /// <summary>租出准备：连接健康校验（非 Open 由池侧丢弃重建）+ 驻留态复位。</summary>
    internal bool IsConnectionHealthyForRent
        => _conn.State == System.Data.ConnectionState.Open;

    /// <summary>租出准备：驻留态复位 + Provider 连接态复位。后者对干净 SQLite 句柄是零往返
    /// （脏标记机制见 <see cref="IDbProvider.MarkConnectionStateUnknown"/>），网络驱动为 no-op；
    /// 修复池化会话跨租期携带上一租期原生 SQL 改写的连接态。</summary>
    internal async ValueTask PrepareForRentAsync(CancellationToken ct)
    {
        _operationState.Unpark();
        await TProvider.InitializeConnectionAsync(_conn, ct).ConfigureAwait(false);
        if (_readConnection is { State: ConnectionState.Open } readConnection
            && _readConnInitializer is not null)
        {
            await _readConnInitializer(readConnection, ct).ConfigureAwait(false);
        }
    }

    /// <summary>停止后台操作，释放连接。幂等。
    /// <para><b>池化会话</b>（经 <see cref="DataSessionPool{TProvider}.RentAsync"/> 租出）：
    /// DisposeAsync 是<b>归还</b>而非释放——瞬态状态（WithTenant/IgnoreFilters/With* 覆写、
    /// AddInterceptor 加项）复位，会话与复用槽留在池中待下一租期。有事务、在飞操作或并行读
    /// 作用域未收口时抛出（先提交/消费完再归还）。</para></summary>
    public ValueTask DisposeAsync()
    {
        if (_operationState.IsCurrentOperationScope
            || _operationState.IsCurrentTransactionFlow)
        {
            throw new InvalidOperationException(
                "DataSession cannot be disposed from its active operation or transaction scope.");
        }
        // 池化形态：归还路径（驻留门禁失败时抛出——事务/在飞操作/并行作用域未收口是
        // 调用方契约违反；池满或池已释放则降级真实释放）
        if (_pool is not null)
            return ReturnToPoolAsync();
        return _operationState.DisposeAsync(DisposeCoreAsync, _interceptors.Count == 0);
    }

    /// <summary>池化归还：Park 门禁 → 瞬态复位 → 入队；未被接收（池满/池释放）则 Unpark
    /// 后走真实释放。复用槽、读连接、物化容量启发<b>不复位</b>（跨租用存活即收益）。
    /// 幂等（加固 2026-10-13）：重复 Dispose 命中 AlreadyParked/TornDown 直接返回——
    /// 与非池化 DisposeAsync 的幂等语义对齐；并发双归还由 Park 的锁内状态判定互斥
    /// （先驻留者独占归还路径，后到者拿到 AlreadyParked 返回；会话在入队前不可能被
    /// 租出，不存在"复位与新租期并发"窗口）。</summary>
    private async ValueTask ReturnToPoolAsync()
    {
        SessionOperationState.ParkResult parkResult = _operationState.Park();
        if (parkResult != SessionOperationState.ParkResult.Parked)
            return;   // AlreadyParked（重复 Dispose）/ TornDown（池丢弃后的残留 Dispose）
        // 瞬态复位（已 Park，无并发进入）：过滤/租户/隔离级别/弹性覆写/拦截器加项全部
        // 回到池的基准形态——上一租期的 WithTenant/IgnoreFilters/WithTimeout/AddInterceptor
        // 不得串味到下一租期（R3 共享拦截器表教训的池化同款防线）。
        _ignoreFilters = false;
        _tenantId = null;
        _tenantScopeEntry = null;
        _isolationLevel = System.Data.IsolationLevel.ReadCommitted;
        Volatile.Write(ref _options, _poolBaseOptions!);
        _commandTimeoutSeconds = _poolBaseOptions!.CommandTimeoutSeconds;
        Volatile.Write(ref _resilience, _poolBaseResilience!);
        _operationState.DiscardPostCommitBackfills();
        _operationState.DefersPostCommitActions = false;
        ResetInterceptorsToPoolBase();
        if (_pool!.TryReturn(this))
            return;
        // 池满/池已释放：Unpark 后走真实释放（连接关闭、槽释放——语义与非池化一致）
        _operationState.Unpark();
        await _operationState.DisposeAsync(DisposeCoreAsync, _interceptors.Count == 0).ConfigureAwait(false);
    }

    /// <summary>拦截器表复位为池基准清单（内容级：清空后按 Priority 重排回填；表实例保留
    /// ——列表本身是会话私有的，无共享可变表风险，R3 教训不适用）。</summary>
    private void ResetInterceptorsToPoolBase()
    {
        _interceptors.Clear();
        if (_poolBaseOptions!.Interceptors is { Count: > 0 } baseInterceptors)
        {
            foreach (IQueryInterceptor interceptor in baseInterceptors.OrderBy(static i => i.Priority))
                _interceptors.Add(interceptor);
        }
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
            // PL-2：晋升后的复用命令归会话所有，在主连接关闭前统一释放
            // （DisposeAsync 已等待全部活动操作结束，此时无飞行查询持有这些命令）
            if (await DisposeReusableCommandsAndCloseAsync().ConfigureAwait(false) is { } commandsCloseException)
                RecordCleanupException(ref cleanupException, commandsCloseException);
        }
        catch (Exception exception) { RecordCleanupException(ref cleanupException, exception); }

        if (await DisposeConnectionIfOwnedAsync().ConfigureAwait(false) is { } connectionException)
            RecordCleanupException(ref cleanupException, connectionException);

        // v5.6：读连接由会话持有，在此统一释放（DisposeAsync 先等待全部活动操作结束，
        // 故不存在无飞行查询仍持有该连接的情形）。
        // ITM-797③（r23）：并行读池兜底释放——用户漏 Dispose ParallelReadScope 时由会话
        // 释放路径清池（DisposeAsync 等待面已含读租约排空，此时池内连接无飞行消费者）。
        // 释放异常与池清理同口径（静默——空闲句柄无诊断价值）。
        // （DisposeCoreAsync 的读侧收尾段抽离，降 S3776 认知复杂度。）
        if (await DisposeReadSideAssetsAsync().ConfigureAwait(false) is { } readConnectionException)
            RecordCleanupException(ref cleanupException, readConnectionException);

        if (cleanupException is not null)
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }

    /// <summary>主连接释放（所有者为 false 时为空操作）——返回清理异常（无异常返回 null），
    /// 由 <see cref="DisposeCoreAsync"/> 按主异常保留约定挂链。</summary>
    private async ValueTask<Exception?> DisposeConnectionIfOwnedAsync()
    {
        if (!_ownsConnection) return null;
        try
        {
            await _conn.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>复用命令释放 + 主连接关闭（DisposeCoreAsync 的中段抽离，降 S3776）——
    /// 返回首个清理异常（无异常返回 null）。</summary>
    private async ValueTask<Exception?> DisposeReusableCommandsAndCloseAsync()
    {
        try
        {
            await DisposeReusableCrudCommandsAsync().ConfigureAwait(false);
            // A9：晋升的读查询命令同口径释放
            if (await DisposeReusableQueryCommandAsync().ConfigureAwait(false) is { } queryCleanup)
                return queryCleanup;
            if (_ownsConnection && _conn.State == ConnectionState.Open)
                await _conn.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }
        return null;
    }

    /// <summary>读连接释放 + 并行读池兜底释放（DisposeCoreAsync 的读侧收尾段抽离）——
    /// 返回读连接释放异常（无异常返回 null；池清理异常静默，与既有口径一致）。</summary>
    private async ValueTask<Exception?> DisposeReadSideAssetsAsync()
    {
        // A2：读连接释放失败不得跳过并行池清理——池内物理连接须在会话释放时无条件释放
        Exception? readConnectionException = await DisposeReadConnectionAsync().ConfigureAwait(false);
        await DisposeParallelReadPoolAsync().ConfigureAwait(false);
        return readConnectionException;
    }

    /// <summary>A9：释放晋升的读查询命令（会话所有）——返回清理异常（无异常返回 null），
    /// 由调用方按"主异常保留"约定挂链；槽引用同步清空（重复 Dispose 幂等）。</summary>
    private async ValueTask<Exception?> DisposeReusableQueryCommandAsync()
    {
        if (_querySlot?.Command is not { } queryCommand) return null;
        _querySlot.Command = null;
        try
        {
            await queryCommand.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
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

    /// <summary>PL-2：释放晋升后的单行 CRUD 复用命令（写路径 + GetByKey 读路径，2026-09-25 扩展）。
    /// 首个异常直接抛出（由 DisposeCoreAsync 的连接清理 catch 记入主异常链），后续异常挂其
    /// <see cref="Exception.Data"/> 不丢弃。</summary>
    private async Task DisposeReusableCrudCommandsAsync()
    {
        Exception? primary = null;
        // S5：六个槽同构释放收敛到单一助手（原逐槽 if/try 把方法复杂度推过 S3776 线）。
        if (await DisposeReusableCommandAsync(_reusableInsert).ConfigureAwait(false) is { } insertError)
            RecordCleanupException(ref primary, insertError);
        if (await DisposeReusableCommandAsync(_reusableUpdate).ConfigureAwait(false) is { } updateError)
            RecordCleanupException(ref primary, updateError);
        if (await DisposeReusableCommandAsync(_reusableGetByKey).ConfigureAwait(false) is { } getError)
            RecordCleanupException(ref primary, getError);
        if (await DisposeReusableCommandAsync(_reusableDelete).ConfigureAwait(false) is { } deleteError)
            RecordCleanupException(ref primary, deleteError);
        if (await DisposeReusableCommandAsync(_reusableUpsert).ConfigureAwait(false) is { } upsertError)
            RecordCleanupException(ref primary, upsertError);
        if (await DisposeReusableCommandAsync(_reusableSoftDelete).ConfigureAwait(false) is { } softDeleteError)
            RecordCleanupException(ref primary, softDeleteError);
        if (primary is not null)
            ExceptionDispatchInfo.Capture(primary).Throw();
    }

    /// <summary>S5：单槽释放，返回清理异常（无槽/无异常返回 null）。</summary>
    private static async Task<Exception?> DisposeReusableCommandAsync(ReusableCrudCommand? slot)
    {
        if (slot is null) return null;
        try
        {
            await slot.Command.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static EntityFeatures GetEntityFeatures<T>() where T : class, new()
        => PalORM_Runtime.EntityFeatures.GetValueOrDefault(typeof(T), EntityFeatures.None);

    // ─── 刀1（2026-10-13 二轮全量重读）：每实体类型 T 的合并元数据单例缓存 ──
    //
    // 动机：P1-2 的合并视图把 From<T>/GetByKey/GetAll 从 3 次 FrozenDictionary 查找收敛为
    // 1 次，但稳态热路径（长会话读循环）每次操作仍付 1~5 次查找（EntityInfo + EntityFeatures
    // + FilterForms + 语句缓存）。类型级注册表条目单调不可变（Register 拒绝重复注册，
    // ITM-758 的 live 读安全性论证同源），且缓存只在查找成功后写入——未注册类型的首次
    // 失败不缓存，注册后首查入缓，此后零查找（约 -20ns/次）。
    // 载体在非泛型宿主类 EntityInfoCache 内（S2743：静态缓存按 T 分片是本缓存的目的而非
    // 事故——见该类抑制理由）；写入经静态 Store 方法（S2696）。
    private static EntityRuntimeInfo GetEntityInfo<T>() where T : class, new()
    {
        EntityRuntimeInfo? cached = EntityInfoCache.Per<T>.Info;
        if (cached is not null)
            return cached;
        if (!PalORM_Runtime.CurrentState._entityInfo.TryGetValue(typeof(T), out EntityRuntimeInfo? info))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered.");
        EntityInfoCache.Store<T>(info);
        return info;
    }

    /// <summary>CRUD 入口的静态视图取用：保留旧入口的 "has no generated CRUD" 错误消息
    /// （投影类型只有行工厂，没有 CRUD 元数据）。冷路径才查一次注册表，命中静态缓存后零查找。</summary>
    private static EntityRuntimeInfo GetEntityInfoForCrud<T>() where T : class, new()
    {
        if (EntityInfoCache.Per<T>.Info is { } cached)
            return cached;
        if (!PalORM_Runtime.CurrentState._crudMetadatas.ContainsKey(typeof(T)))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        return GetEntityInfo<T>();
    }

    /// <summary>S1：取注册期投影的 CRUD 引用视图（热路径只持有 8B 引用）。</summary>
    private static CrudPlan GetCrudPlan<T>(EntityRuntimeInfo info) where T : class, new()
        => info.Plan
            ?? throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");

    /// <summary>刀1：行工厂的每类型静态缓存——直查族（QueryAsync/QueryAsyncEnumerable/
    /// QueryFirst/QuerySingle）专用。T 允许是 [Projection] 类型（只进 RowFactories 不进
    /// TableNames），故不走 EntityRuntimeInfo 视图而走 RowFactories 注册表。</summary>
    internal static Func<DbDataReader, T> GetRowFactory<T>()
    {
        object? cached = EntityInfoCache.FactoryPer<T>.Factory;
        if (cached is not null)
            return (Func<DbDataReader, T>)cached;
        if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered.");
        EntityInfoCache.StoreFactory<T>(factory);
        return (Func<DbDataReader, T>)factory;
    }

    // ─── 默认过滤（软删除 + 租户）────────────────────────
    // 三个 Get* 形态 + Bind 必须共用同一判定谓词：条件里引用了 @__tenant0 而 Bind 未绑（或反之）
    // 都会直接产生运行时错误。租户过滤覆盖所有直连读写入口（ITM-302）；
    // Insert/Save 不做租户过滤——实体自带 tenant_id 列值，由调用方负责。
    // 刀1：热路径调用方已持有 EntityRuntimeInfo.Features 的，经 features 参数重载省一次
    // EntityFeatures 字典查找；无参重载留给批量族等冷调用方（语义同源）。

    private bool HasTenantFilter<T>() where T : class, new()
        => HasTenantFilter(GetEntityFeatures<T>());

    private bool HasTenantFilter(EntityFeatures features)
        => _tenantId is not null && !_ignoreFilters
            && (features & EntityFeatures.TenantAware) != 0;

    /// <summary>默认过滤条件的参数名——避开生成 SQL 的 @p{N} 命名空间。</summary>
    private const string _tenantParameterName = "@__tenant0";

    // v4.1 cache key includes softDelete/tenant flags; _ignoreFilters changes the key (not cached when ignoring)
    // S2743：用非泛型 DataSessionCache 持有，跨 TProvider 共享
    /// <summary>取默认过滤的三种拼接形态（缓存）。<b>v5.6</b>：命中路径走 <c>TryGetValue</c>——
    /// 原实现用 <c>GetOrAdd(key, 捕获 lambda)</c>，factory 捕获 <c>_tenantParameterName</c> 所在
    /// 实例语境，每次调用都分配一个显示类与委托（实测 96B/次），即使缓存命中。未命中才构建并
    /// <c>TryAdd</c>；并发下同键可能被构建多次，但同输入恒同输出，正确性无影响。
    /// 刀1：热路径调用方经 features 参数省一次 EntityFeatures 查找。</summary>
    private DefaultFilterForms GetDefaultFilterForms<T>(EntityFeatures features) where T : class, new()
    {
        bool hasSoftDelete = !_ignoreFilters && (features & EntityFeatures.SoftDelete) != 0;
        bool hasTenant = HasTenantFilter(features);
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

    /// <summary>刀1：features 参数重载（调用方持有合并视图的 Features 时省一次字典查找；
    /// T 保留——透传给 GetDefaultFilterForms 的 (Type, Dialect) 缓存键）。</summary>
    private string GetDefaultFilterFragment<T>(EntityFeatures features) where T : class, new()
        => GetDefaultFilterForms<T>(features).AndFragment;

    /// <summary>为默认过滤条件绑定参数。任何拼接了 GetDefaultFilter* 结果的命令都必须调用。</summary>
    private void BindDefaultFilterParameters<T>(DbCommand cmd) where T : class, new()
    {
        if (HasTenantFilter<T>())
            cmd.Parameters.Add(TProvider.CreateParameter(_tenantParameterName, _tenantId));
    }

    /// <summary>刀1：features 参数重载（同 <see cref="BindDefaultFilterParameters{T}"/>）。</summary>
    private void BindDefaultFilterParameters(DbCommand cmd, EntityFeatures features)
    {
        if (HasTenantFilter(features))
            cmd.Parameters.Add(TProvider.CreateParameter(_tenantParameterName, _tenantId));
    }

    // ─── 租户过滤 SQL 片段缓存（M1，v5.6.0）──────────────────────
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

    /// <summary>租户护栏 WHERE 片段：" WHERE {限定租户列} = @__tenant0"——供批量 UPSERT 的
    /// 冲突子句后追加（调用方传表名限定引用，PG 下裸列名对 excluded 关系歧义）。</summary>
    private static string GetUpsertTenantWhereFragment(string qualifiedTenantColumn, string tenantParameterName)
        => $" WHERE {qualifiedTenantColumn} = {tenantParameterName}";

    /// <summary>UPSERT 冲突更新的租户护栏（2026-10-06，10-05 审计 P2 收口）：带租户会话的
    /// SaveAsync/BulkMerge 冲突更新只允许改写本租户的行，与 UpdateAsync 的租户条件对称。
    /// 路由：PG/SQLite 在 RETURNING 子句前插 WHERE（冲突不命中=0 行受影响，静默跳过）；
    /// MySQL ODKU 无 WHERE，把每个赋值项包 IF(...)（IF 短路求值，租户不符时
    /// VALUES(col)/LAST_INSERT_ID(col) 分支不求值，行为=静默跳过）。per-(Type, Dialect) 缓存。
    /// <para><b>表名限定</b>：INSERT 列清单含 tenant_id，PG 的 DO UPDATE 中裸列名对目标表与
    /// excluded 关系歧义（42702 实测）——守卫条件必须以表名限定（42702 修复实测）。</para></summary>
    private static string UpsertTenantGuardedSql<T>(string baseSql) where T : class, new()
    {
        (Type, SqlDialect) key = (typeof(T), TProvider.Dialect);
        if (DataSessionCache.UpsertTenantGuardedSqlCache.TryGetValue(key, out string? cached))
            return cached;
        string sql = TProvider.Dialect == SqlDialect.MySql
            ? WrapMySqlUpsertAssignmentsWithTenantGuard(baseSql, TenantColumnRef<T>(), _tenantParameterName)
            : InsertTenantGuardBeforeReturning(baseSql, TenantColumnRef<T>(), _tenantParameterName);
        DataSessionCache.UpsertTenantGuardedSqlCache.TryAdd(key, sql);
        return sql;
    }

    /// <summary>租户列的表名限定引用（"{quote(table)}.{quote(tenant_id)}"）。</summary>
    private static string TenantColumnRef<T>() where T : class, new()
    {
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName) || tableName is null)
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no [Table] attribute.");
        return $"{TProvider.QuoteIdentifier(tableName)}.{TProvider.QuoteIdentifier("tenant_id")}";
    }

    /// <summary>MySQL ODKU 赋值项守卫：`col` = VALUES(`col`) / `pk` = LAST_INSERT_ID(`pk`)
    /// 逐项包 IF(tenant_id = @p, 原表达式, col)。已知局限：列名内含 " RETURNING " 形态的
    /// 病态标识符不在本手术假设内（与 UpdateWithTenant 的 WHERE 结尾假设同量级）。</summary>
    private static string WrapMySqlUpsertAssignmentsWithTenantGuard(
        string sql, string qualifiedTenantColumn, string tenantParameterName)
        => System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"((?:`[^`]+`|\w+)) = (VALUES|LAST_INSERT_ID)\(\1\)",
            "$1 = IF(" + qualifiedTenantColumn + " = " + tenantParameterName + ", $2($1), $1)",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(2));

    private static string InsertTenantGuardBeforeReturning(
        string sql, string qualifiedTenantColumn, string tenantParameterName)
    {
        // 手术锚点：生成器形态 "… DO UPDATE SET … RETURNING …"（BuildUpsertReturningSql），
        // RETURNING 关键字恰一次；列名经引号包裹，裸 RETURNING 只能是子句关键字。
        int returningIndex = sql.LastIndexOf(" RETURNING", StringComparison.Ordinal);
        return returningIndex < 0
            ? sql
            : sql[..returningIndex] + " WHERE " + qualifiedTenantColumn + " = " + tenantParameterName + sql[returningIndex..];
    }

    /// <summary>G4（2026-10-13 五轮微项包）：带租户后缀的 UPDATE/DELETE 全句单值静态槽——
    /// 值 = 生成语句 + 租户追加片段，由 (T, 方言) 唯一决定（非租户形态不走本槽）。
    /// 替代 GetTenantWrappedSql 的 per-(Type, Dialect) 元组键字典查找。</summary>
    private static string GetUpdateWithTenantSqlFast<T>(string baseSql) where T : class, new()
        => SqlSlots<T>.UpdateWithTenant ??= baseSql + GetTenantAppendFragment();

    /// <summary>G4：带租户后缀的 DELETE 全句单值静态槽（同 <see cref="GetUpdateWithTenantSqlFast{T}"/>）。</summary>
    private static string GetDeleteWithTenantSqlFast<T>(string baseSql) where T : class, new()
        => SqlSlots<T>.DeleteWithTenant ??= baseSql + GetTenantAppendFragment();

    /// <summary>软删 UPDATE 全句——G4（五轮微项包）起为纯构建器，仅在 2 槽静态缓存未命中时调用
    ///（M1 原注释：原先 DeleteAsync 软删路径每次调用 5 次 QuoteIdentifier + 全句插值）。</summary>
    private static string GetSoftDeleteUpdateSql<T>(string tableName, bool hasTenant) where T : class, new()
    {
        string tenantFilter = hasTenant ? GetTenantAppendFragment() : "";
        // S2077 报备（M1-5，与原调用点同）：插值成分全为 QuoteIdentifier 标识符、
        // CurrentTimestampExpression 与 const 参数名——用户值经 @p0 绑定
#pragma warning disable S2077
        var quotedTable = TProvider.QuoteIdentifier(tableName);
        var quotedDeletedAt = TProvider.QuoteIdentifier("deleted_at");
        var quotedPk = TProvider.QuoteIdentifier(GetPkColumn<T>());
        return $"UPDATE {quotedTable} SET {quotedDeletedAt} = {TProvider.CurrentTimestampExpression} "
            + $"WHERE {quotedPk} = @p0 AND {quotedDeletedAt} IS NULL{tenantFilter}";
#pragma warning restore S2077
    }

    /// <summary>G4：软删 UPDATE 全句的 2 槽静态缓存取用（F6b 同型）——bit0=hasTenant，
    /// 命中路径零字典查找（原 per-(Type, Dialect, bool) 元组键）。</summary>
    private string GetSoftDeleteUpdateSqlFast<T>(EntityRuntimeInfo info) where T : class, new()
    {
        int slot = HasTenantFilter(info.Features) ? 1 : 0;
        if (SqlSlots<T>.SoftDeleteUpdate[slot] is { } cached)
            return cached;
        string built = GetSoftDeleteUpdateSql<T>(info.TableName, slot == 1);
        SqlSlots<T>.SoftDeleteUpdate[slot] = built;
        return built;
    }

    private SessionOperationState.SessionOperationLease EnterOperation(
        object? operationOwner = null)
        => _operationState.Enter(operationOwner);

    /// <summary>只读操作租约入口（step8-T3）：作用域外与 <see cref="EnterOperation"/> 逐位一致
    /// （EnterReadOnly 内部退化 Enter），并行读作用域内走只读并发租约（上限 MaxParallelReads）。
    /// 纯读入口（GetAsync/GetAllAsync/CountAsync/QueryAsync/QueryAsyncEnumerable/ScalarAsync/
    /// 聚合族）统一走本入口——ITM-811 同型面的收口（该轮只修了 QueryBuilderExtensions 族，
    /// DataSession 直查族漏扫，并发 GetAsync 在作用域内必抛 "already active" 实测坐实）。</summary>
    private SessionOperationState.SessionOperationLease EnterReadOnly()
        => _operationState.EnterReadOnly();

    /// <summary>创建批量执行器——把 N 条非查询语句压成一次往返（方言支持时，实测 PG 3.4×/10 语句）。
    /// 见 <see cref="SessionBatch{TProvider}"/> 的语义契约。</summary>
    /// <summary>创建批执行器。ITM-836（r23 复核登记）：不取操作租约是<b>内部复用契约</b>——
    /// MigrateAsync 的 ApplyTableDdlAsync 持租约调用本方法（Schema.cs），加租约会自撞
    /// "already active"（r23 修复实测坐实后回滚）。公共调用方应在无飞行操作时调用
    ///（会话常规使用形态天然满足）；构造期读取（CommandTimeout/事务）的门禁由调用方租约覆盖。</summary>
    public SessionBatch<TProvider> CreateBatch()
    {
        // 批量脚本承载任意用户 SQL（含 DDL/PRAGMA）——入口即标脏，连接态复位交给 Provider。
        MarkConnectionStateUnknownForRawSql();
        return new SessionBatch<TProvider>(this);
    }

    internal DbCommand CreateCommandForBatch() => CreateCommand();

    internal SessionOperationState.SessionOperationLease EnterBatchOperation(
        object? operationOwner = null)
        => EnterOperation(operationOwner);

    internal DbConnection BatchConnection => _conn;

    internal DbTransaction? GetActiveBatchTransaction() => GetActiveTransaction();

    internal int BatchCommandTimeoutSeconds => _commandTimeoutSeconds;

    private DbCommand CreateCommand()
        => CreateCommand(GetActiveTransaction());

    /// <summary>刀2：事务由调用方单次取用后传入（每操作省一次 GetActiveTransaction 的锁往返）。</summary>
    private DbCommand CreateCommand(DbTransaction? transaction)
    {
        DbCommand command = _conn.CreateCommand();
        command.Transaction = transaction;
        // ITM-557 根治：超时在工厂集中设置——新调用点在结构上不可能漏（ValidateSchemaAsync 即漏网实例）。
        // ITM-597: 部分路径（HealthCheck/Scalar/Execute/Migrate/Bulk 等）显式重复赋同值——
        // 这是<b>防御冗余</b>而非死代码：万一未来 CreateCommand 被重构不再设超时，这些路径仍自洽。
        // 删除冗余会削弱回归防御；保留并在此声明 CreateCommand 是<b>权威源</b>。
        command.CommandTimeout = _commandTimeoutSeconds;
        return command;
    }

    /// <summary>原生 SQL / 存储过程 / DDL / 裸连接改写了连接态——通知 Provider 把该物理连接
    /// 标记为"下次会话初始化需复位"（SQLite 实现只重设 foreign_keys；网络驱动默认 no-op）。</summary>
    internal void MarkConnectionStateUnknownForRawSql()
        => TProvider.MarkConnectionStateUnknown(_conn);

    /// <summary>全量物化的逐行读取是否走同步叶子——进程内引擎恒真；网络驱动需
    /// <see cref="DbOptions.UseSynchronousRowRead"/> 与 Provider 能力同时成立（MySQL 实测
    /// 同步读约为异步的一半耗时；PostgreSQL 不声明能力）。流式 API 不走本属性。</summary>
    internal bool PreferSynchronousRowRead
        => TProvider.PreferSynchronousIo
            || (_options.UseSynchronousRowRead && TProvider.SupportsSynchronousRowRead);

    /// <summary>单行读叶子（GetByKey/首行族）——按 <see cref="PreferSynchronousRowRead"/> 分流。</summary>
    private ValueTask<bool> ReadRowAsync(DbDataReader reader, CancellationToken ct)
        => PreferSynchronousRowRead
            ? new ValueTask<bool>(ReadSync(reader, ct))
            : ReadAsync(reader, ct);

    /// <summary>刀3（2026-10-13 二轮）：复用命令的状态守卫式重设——Transaction/CommandTimeout
    /// 同值不重设（SqliteCommand.Transaction setter 含连接归属验证，长会话循环里同值重设是
    /// 纯开销）；CommandText 保持既有同值守卫（驱动同值 setter 短路未证实，显式跳过）。</summary>
    /// <summary>直查族命令（<see cref="ReusableQuerySlot"/>）的状态重设——该槽无缓存字段，
    /// 走驱动属性比较的单值形态（与单行 CRUD 槽的字段缓存相反）。</summary>
    private void ApplyReusableCommandState(DbCommand command, DbTransaction? transaction, string commandText)
    {
        if (!ReferenceEquals(command.Transaction, transaction))
            command.Transaction = transaction;
        int timeoutSeconds = _commandTimeoutSeconds;
        if (command.CommandTimeout != timeoutSeconds)
            command.CommandTimeout = timeoutSeconds;
        if (!ReferenceEquals(command.CommandText, commandText)
            && !string.Equals(command.CommandText, commandText, StringComparison.Ordinal))
            command.CommandText = commandText;
    }

    private void ApplyReusableCommandState(ReusableCrudCommand slot, DbTransaction? transaction, string commandText)
    {
        // 槽内缓存已应用状态，命中路径不再读驱动属性（Transaction/CommandTimeout/CommandText
        // 三个 getter 每操作各一次）。缓存只在唯一写入口与晋升初始化处更新。
        DbCommand command = slot.Command;
        if (!ReferenceEquals(slot.AppliedTransaction, transaction))
        {
            command.Transaction = transaction;
            slot.AppliedTransaction = transaction;
        }
        int timeoutSeconds = _commandTimeoutSeconds;
        if (slot.AppliedTimeoutSeconds != timeoutSeconds)
        {
            command.CommandTimeout = timeoutSeconds;
            slot.AppliedTimeoutSeconds = timeoutSeconds;
        }
        if (!ReferenceEquals(slot.AppliedCommandText, commandText))
        {
            command.CommandText = commandText;
            slot.AppliedCommandText = commandText;
        }
    }

    private DbTransaction? GetActiveTransaction()
        => _operationState.GetActiveTransaction();

    /// <summary>并行读作用域（ARCH-001，2026-09-23）——作用域内只读操作允许并发（各自持一条独立
    /// 连接，从作用域局部池取、用完归还）；写/事务/Bulk 仍走单活动门禁（遇在飞只读租约即拒绝），
    /// 即"只读并行、写与事务串行"的非对称语义。
    /// <para><b>连接来源</b>：配置了 <c>ReadConnectionString</c> 时用读连接串，否则回落到主连接串
    /// （同一库上的并行只读）。池在作用域退出时统一释放，连接数上限 <c>MaxParallelReads</c>（8）。</para>
    /// <para><b>用法</b>：<c>await using (session.ForParallelReads()) { await Task.WhenAll(a, b); }</c>。
    /// 作用域内不要发起写操作（会被门禁拒绝）；作用域可嵌套，最外层退出才释放池。</para></summary>
    public ParallelReadScope ForParallelReads()
    {
        _operationState.EnterParallelReadScope();
        return new ParallelReadScope(this);
    }

    /// <summary>并行读作用域句柄——DisposeAsync 退出作用域并在最外层释放池内连接。
    /// ITM-797④（r23）：幂等——重复 Dispose 不再把外层作用域深度多扣一次
    /// （原形态内层双 Dispose 使 _parallelReadScopes 提前归零、外层池被整体提前释放）。
    /// ITM-897（r24）：幂等守卫改原子交换——bool 先查后写的窗口在并发双 Dispose 下双扣
    /// （await using 正常路径不触发，仅并发手写 Dispose 暴露）。</summary>
    public sealed class ParallelReadScope(DataSession<TProvider> session) : IAsyncDisposable
    {
        private int _disposeState;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
            await session.EndParallelReadsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>退出并行读作用域（ARCH-001）——最外层退出时释放池内全部连接。
    /// 已知边界：退出时在飞的读查询由其自身 finally 归还连接；作用域先于查询完成退出
    /// 属调用方违约（正确用法是 Task.WhenAll 完成后再退作用域）。</summary>
    private async ValueTask EndParallelReadsAsync()
    {
        _operationState.ExitParallelReadScope();
        if (_operationState.ParallelReadsEnabled) return;   // 仍有外层作用域：保留池
        await DisposeParallelReadPoolAsync().ConfigureAwait(false);
    }

    /// <summary>释放并行读池内全部连接（ITM-797③，r23）——最外层作用域退出与会话
    /// Dispose 兜底共用。快照在锁内取得（ITM-796），释放 await 在锁外。</summary>
    private async ValueTask DisposeParallelReadPoolAsync()
    {
        DbConnection[] connections;
        lock (_parallelReadPoolLock)
        {
            // O2：池从未建立（本会话未进过并行读获取路径）即无可释放连接；
            // 置 null 而非 Clear——惰性形态下下次进入作用域重建，语义与清空一致。
            if (_parallelReadConnections is null)
            {
                _idleParallelReadConnections = null;
                return;
            }
            connections = [.. _parallelReadConnections];
            _parallelReadConnections = null;
            _idleParallelReadConnections = null;
        }
        foreach (DbConnection connection in connections)
        {
            // 清理路径：池内连接多为空闲句柄，释放失败无诊断价值（与既有失效连接丢弃同口径）
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { _ = exception; }
        }
    }

    /// <summary>归还并行读连接（ARCH-001）——作用域内读操作完成后由执行路径调用。
    /// 非池内连接为空操作（主连接与会话级读连接不归还，仍归会话持有）。
    /// ITM-796（r23）：并发归还路径持锁。</summary>
    internal ValueTask ReleaseReadConnectionAsync(DbConnection connection)
    {
        if (!_operationState.ParallelReadsEnabled) return default;
        lock (_parallelReadPoolLock)
        {
            // O2：池未建立（本会话未进过并行读获取路径）时连接必不属池——归还为空操作。
            if (_parallelReadConnections is null || !_parallelReadConnections.Contains(connection)) return default;
            if (!(_idleParallelReadConnections ??= []).Contains(connection))
                _idleParallelReadConnections.Push(connection);
        }
        return default;
    }

    /// <summary>读完成后的池连接处置（A1）：成功归还空闲栈，失败丢弃。失败路径的连接状态不可信
    /// （静默断开时 <c>State</c> 仍为 Open），归还即让同作用域后续读复用死连接。</summary>
    internal ValueTask ReleaseOrDiscardReadConnectionAsync(DbConnection connection, bool healthy)
        => healthy ? ReleaseReadConnectionAsync(connection) : DiscardReadConnectionAsync(connection);

    /// <summary>丢弃并行读池内的连接（A1）：从池登记中移除并释放，不回空闲栈。非池连接（主连接、
    /// 会话级读连接）不在池登记内，为空操作——它们的失效由 <see cref="InvalidateReadConnectionAsync"/> 处理。</summary>
    internal async ValueTask DiscardReadConnectionAsync(DbConnection connection)
    {
        lock (_parallelReadPoolLock)
        {
            if (_parallelReadConnections is null || !_parallelReadConnections.Remove(connection)) return;
        }
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { _ = exception; }
    }

    /// <summary>读路径统一命令创建（ARCH-001 收口，2026-10-06）：路由判据按序——
    /// ① 事务活动期走主连接（读未提交数据 + 事务归属；与 QueryBuilder 活动事务守卫同语义）；
    /// ② 并行读作用域内走作用域池（特性语义：作用域内每个只读操作持独立连接；事务与作用域
    ///    互斥 ITM-798，池路径无事务绑定面），租约释放时经 <see cref="ReleaseReadConnectionAsync"/>
    ///    归还池；
    /// ③ 其余走既有形态：replica opt-in 走会话级读连接（READ-002 opt-in 教义，不回归），
    ///    否则主连接。
    /// <para><b>收口背景</b>：GetAsync（并行分支）与 QueryBuilder 管线此前已接线，
    /// GetAllAsync/CountAsync/聚合内核/原始 SQL 家族漏接——作用域内未接线家族在主连接开
    /// 并发 reader（10-05 审计 P1"接线一半"，PG 上 Npgsql "already open DataReader" 实测）。
    /// 本助手取代 CreateReadOrPrimaryCommandAsync（其副本路由语义并入 ③，池归还语义新增）。</para></summary>
    /// <summary>刀2：事务由调用方单次取用后传入（每读省一次 GetActiveTransaction 锁往返）。</summary>
    private async ValueTask<ReadCommandLease> CreateReadRoutedCommandAsync(bool readFromReplica, DbTransaction? activeTransaction, CancellationToken ct)
    {
        if (activeTransaction is not null)
            return new ReadCommandLease(this, CreateCommand(activeTransaction), null);

        if (_operationState.ParallelReadsEnabled)
        {
            DbConnection connection = await AcquireReadConnectionAsync(ct).ConfigureAwait(false);
            DbCommand command = connection.CreateCommand();
            command.CommandTimeout = _commandTimeoutSeconds;
            return new ReadCommandLease(this, command, connection);
        }

        if (readFromReplica)
        {
            // W1：惰性挂接——readFromReplica 形态此前必然已初始化（From<T> 或上一条复制读），
            // ??= 兜底首次直达；原构造器恒挂接使 is-not-null 判定恒真，此处语义等价。
            DbConnection connection = await (_readConnProvider ??= AcquireReadConnectionAsync)(ct).ConfigureAwait(false);
            DbCommand command = connection.CreateCommand();
            command.CommandTimeout = _commandTimeoutSeconds;
            return new ReadCommandLease(this, command, null);   // 会话级读连接：不归还（归会话持有）
        }

        return new ReadCommandLease(this, CreateCommand(activeTransaction), null);
    }

    /// <summary>读命令租约：命令随租约释放；并行读作用域内租到的池连接在释放时归还
    /// （<see cref="ReleaseReadConnectionAsync"/> 对非池连接空操作）。会话级连接
    /// （主连接/ForRead 读连接）不归还，仍归会话持有。
    /// F7（2026-10-13 全量重读微项包）：class → struct——直查族（GetAll/Count/聚合/Query/
    /// QueryAsyncEnumerable）每次读原先为租约本身付一个堆对象（约 48B），而主连接 + 非并行读
    /// 形态下它只包一层命令引用。struct 经 await using 的<b>模式化</b> DisposeAsync 释放
    /// （不实现 IAsyncDisposable 接口——接口形态对 struct 按接口调用会装箱，模式化直调不装箱）；
    /// MarkHealthy/MarkFailed 只在局部变量上调用（值语义，变异作用于调用方持有的实例）。</summary>
    private struct ReadCommandLease
    {
        private readonly DataSession<TProvider> _session;
        private readonly DbCommand _command;
        private readonly DbConnection? _pooledConnection;
        private bool _healthy;

        public ReadCommandLease(DataSession<TProvider> session, DbCommand command, DbConnection? pooledConnection)
        {
            _session = session;
            _command = command;
            _pooledConnection = pooledConnection;
            _healthy = false;
        }

        public DbCommand Command => _command;

        /// <summary>读操作成功完成（结果已完整消费）后调用（A1）。未调用即视为失败路径：
        /// 池连接在释放时被丢弃而非归还，避免失败后的连接状态被后续读复用。</summary>
        public void MarkHealthy() => _healthy = true;

        /// <summary>撤销健康标记（A1）：流式枚举在 reader 成功打开后先标记健康（早终止消费
        /// 属正常路径），读取阶段抛出时撤销——此后连接状态不可信，释放时丢弃。</summary>
        public void MarkFailed() => _healthy = false;

        public async ValueTask DisposeAsync()
        {
            // default 租约（QueryAsyncEnumerable 的复用命令分支从未赋值）：无资源可释放。
            // 空安全形态让调用点无需 hasLease 分支（S3776：流式枚举器的分支预算紧）。
            if (_command is null) return;
            // ITM-891：Command 释放抛出时连接仍须归还——try/finally 保证
            //（原形态连接留在池登记表外，同作用域内不再入 idle 栈，直到作用域退出兜底）。
            try
            {
                await _command.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (_pooledConnection is not null)
                    await _session.ReleaseOrDiscardReadConnectionAsync(_pooledConnection, _healthy).ConfigureAwait(false);
            }
        }
    }

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
    /// 非直通分支才包 <see cref="ResilienceExecutor.ExecuteWithTimeoutAsync{T}(Func{CancellationToken,Task{T}},CancellationToken)"/>——
    /// 此时仍需委托（超时包装要求），该形态本就少见（写入路径默认不重试，
    /// 只有显式配置了非零 CommandTimeout 才会走到）。</para>
    /// <para><b>为什么值得单独一个重载</b>：单条写路径（Update/Delete/ExecuteAsync）
    /// 每次调用都过这里，是每操作固定成本；208 B 在 MySQL Insert 的 4241 B/行上约 5%，
    /// 在 BulkInsert 的 1222 B/行上约 17%。</para></summary>
    private ValueTask<int> ExecuteWriteRowsAsync(DbCommand command, DbTransaction? activeTransaction, CancellationToken ct)
    {
        // W3（两轴清单轮 2026-10-14）：事务短路前置——原实现先读弹性再判事务，纯事务会话
        // （TxRollback 族逐行 Update）每行都先触发惰性执行器物化；事务内写入恒直连
        //（重试语义与事务互斥，见方法 doc），前置判定后事务路径零弹性接触，
        // per-op 事务会话整会话不再建执行器（−228B/op）。判定语义逐位不变：
        // 原式 (IsPassThrough || tx is not null) 在 tx 非空时恒真 → ExecuteRowsCoreAsync。
        if (activeTransaction is not null)
            return ExecuteRowsCoreAsync(command, ct);
        ResilienceExecutor executor = Resilience;
        if (executor.IsPassThrough)
            return ExecuteRowsCoreAsync(command, ct);
        // R4-λ:static lambda + 命令状态,闭包归零(写路径每行的 display class 分配)
        return executor.ExecuteWithTimeoutAsync(
            static (cmd, token) => ExecuteRowsCoreAsync(cmd, token).AsTask(),
            command,
            ct);
    }

    /// <summary>A2（2026-10-01 全 API 逐项轮）：`new ValueTask&lt;int&gt;(task)` 直包替代
    /// async 方法——写路径直通分支（每操作必经）省一个异步状态机；ConfigureAwait
    /// 由消费端覆盖（ValueTask 包装 Task 的 awaiter 即 Task 的 awaiter）。
    /// <para>Provider 覆写 <see cref="IDbProvider.PreferSynchronousIo"/> 时走同步叶子
    /// （进程内引擎的异步 API 本就是同步执行，异步形态只多付脚手架）。</para></summary>
    private static ValueTask<int> ExecuteRowsCoreAsync(DbCommand command, CancellationToken ct)
        => TProvider.PreferSynchronousIo
            ? new ValueTask<int>(ExecuteRowsSync(command, ct))
            : new ValueTask<int>(command.ExecuteNonQueryAsync(ct));

    private static int ExecuteRowsSync(DbCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return command.ExecuteNonQuery();
    }

    /// <summary>读命令执行叶子——同步/异步按 Provider 能力分流（同 <see cref="ExecuteRowsCoreAsync"/>）。</summary>
    internal static ValueTask<DbDataReader> ExecuteReaderAsync(DbCommand command, CancellationToken ct)
        => TProvider.PreferSynchronousIo
            ? new ValueTask<DbDataReader>(ExecuteReaderSync(command, ct))
            : new ValueTask<DbDataReader>(command.ExecuteReaderAsync(ct));

    private static DbDataReader ExecuteReaderSync(DbCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return command.ExecuteReader();
    }

    /// <summary>逐行读取叶子——同步/异步按 Provider 能力分流。</summary>
    internal static ValueTask<bool> ReadAsync(DbDataReader reader, CancellationToken ct)
        => TProvider.PreferSynchronousIo
            ? new ValueTask<bool>(ReadSync(reader, ct))
            : new ValueTask<bool>(reader.ReadAsync(ct));

    private static bool ReadSync(DbDataReader reader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return reader.Read();
    }

    /// <summary>将复合格式项映射为参数名，参数值保持原始对象。
    /// <para>A1（2026-10-01 全 API 逐项轮）：改走形状缓存（baseIndex 恒 0）——与 QueryBuilder
    /// 路径（<see cref="QueryBuilder{T}.FormatFormattableSql"/>）同源。格式化是
    /// （Format 文本, baseIndex, 参数个数）的纯函数；直查的 Format 是编译期常量或工厂定串，
    /// 值相等键可稳定命中——命中时复用同一 SQL 文本实例并省去复合格式扫描
    /// （FormatCached 注释口径：约 -40B/次 + 省扫描）。校验（裸 @pN 拒绝等）在首次
    /// 未命中构建时执行，纯函数性保证命中项已通过同一校验。</para></summary>
    private static string FormatSqlWithParameters(FormattableString sql)
        => FormattableSqlFormatter.FormatCached(sql.Format, 0, sql.ArgumentCount);

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

    /// <summary>直接查询复用槽晋升后的首次使用：槽只预置命令文本，不预置参数——此处补建一次
    /// 参数对象（后续命中只写 Value）。只在参数集合为空时动作；非空但数量不匹配说明形态/
    /// 租户作用域已漂移，交由 <see cref="TryBindFormattableValuesInPlace"/> 返回 false 回退新建。</summary>
    private void EnsureReusableDirectParameters(
        DbCommand command, FormattableString? sql, EntityFeatures features)
    {
        if (command.Parameters.Count != 0)
            return;
        if (sql is not null)
            BindFormattableParameters(command, sql);
        BindDefaultFilterParameters(command, features);
    }

    /// <summary>无默认过滤形态的重载（原始 SQL 入口：QueryAsync/Scalar/Execute/流式）。</summary>
    private static void EnsureReusableDirectParameters(DbCommand command, FormattableString? sql)
    {
        if (command.Parameters.Count != 0 || sql is null)
            return;
        BindFormattableParameters(command, sql);
    }

    /// <summary>P1-1（2026-10-12 全量重读）：直查族（QueryAsync/Scalar/Count/聚合/
    /// QueryAsyncEnumerable/ExecuteAsync）的命令复用入口——From&lt;T&gt; 查询管线（A9）同槽
    /// 共用（C15 按形状计数使交替 SQL 形态永不互相淘汰）。守卫：非并行读作用域 + 主连接形态
    ///（读路由/并行池连接不参与——连接引用一致性是复用前提）。命中返回槽内命令（归会话所有，
    /// 调用方不得 Dispose，负责重设 Transaction/超时并经
    /// <see cref="TryBindFormattableValuesInPlace"/> 就地写参数值）。</summary>
    private DbCommand? TryAcquireReusableDirectCommand(string sql)
    {
        if (_operationState.ParallelReadsEnabled)
            return null;
        // 惰性创建（首个直查时）——纯直查会话此前从不建槽，晋升永不生效；与 From<T>() 的
        // 惰性形态同源，并发竞态良性（引用写原子、败者被 GC）。
        ReusableQuerySlot slot = _querySlot ??= new ReusableQuerySlot(TProvider.PrepareReusedCommands);
        return slot.TryAcquire(_conn, sql);
    }

    /// <summary>P1-1：把 FormattableString 参数值就地写进复用命令的既有参数集合
    ///（<b>不 Clear、不 Add 新实例</b>——R-UNNESTB：PG auto-prepare 下 Clear+Add 会让驱动
    /// 沿用 prepare 时的绑定值）。同 SQL 文本恒同参数个数（FormatCached 的输出文本唯一决定
    /// 占位符集）；<paramref name="extraSlots"/> 是随命令持久存在的附加参数槽数
    ///（如租户过滤 @__tenant0，值由调用方就地刷新）。非空值的运行时类型与前值不一致时返回
    /// false（PG 的 DbType 在池存续期恒定，同位异类型会让驱动按陈旧类型错绑——调用方回退
    /// 新建命令，槽保留其命令）。</summary>
    private static bool TryBindFormattableValuesInPlace(
        DbCommand cmd, FormattableString? sql, int extraSlots = 0)
    {
        if (sql is null)
            return cmd.Parameters.Count == extraSlots;
        int argumentCount = sql.ArgumentCount;
        if (cmd.Parameters.Count != argumentCount + extraSlots)
            return false;
        // 先全量校验形态再写值（半写状态无害但不整洁；A9 TryCopyParameterValues 同口径）
        for (int i = 0; i < argumentCount; i++)
        {
            object? value = sql.GetArgument(i);
            object? existing = cmd.Parameters[i].Value;
            // null/DBNull 任一侧不参与类型校验：DBNull 期间 DbType 恒定不失（PG-4 单一来源）
            if (value is null or DBNull || existing is null or DBNull)
                continue;
            if (value.GetType() != existing.GetType())
                return false;
        }
        for (int i = 0; i < argumentCount; i++)
            cmd.Parameters[i].Value = sql.GetArgument(i) ?? DBNull.Value;
        return true;
    }
}

/// <summary>A9（2026-10-01 全 API 逐项轮）：读查询命令复用槽（PL-2 惰性晋升的泛化）——
/// 同 SQL 文本的查询复用晋升命令，省去每查询的命令创建/释放（收益上限探针
/// KeyLookupCommandDiag：-504B/op、-2.57µs，SQLite 直驱两臂对比）。
/// <para>守卫（TryAcquireReusableSelectCommand）：连接引用一致（读路由/并行读换连接即不
/// 复用）、并行读作用域禁用（并发 reader 不能共用命令）、晋升阈值 3（对齐 PL-2，防
/// "每操作一会话"泄漏）。单槽：交替形状不受益（每次替换旧命令），文档已登记。</para></summary>
internal sealed class ReusableQuerySlot
{
    /// <summary>最近一次晋升的 SQL 文本（值相等比较作复用键；形状缓存命中即同文本）。</summary>
    public string? Sql;
    /// <summary>晋升的命令（会话所有，Dispose 时统一释放）。</summary>
    public DbCommand? Command;
    /// <summary>命令所属连接（执行时连接必须引用一致，否则不复用）。</summary>
    public DbConnection? Connection;
    /// <summary>晋升判据：同 SQL 第 <see cref="PromotionThreshold"/> 次晋升（对齐 PL-2）。</summary>
    public int ServedOps;
    /// <summary>最近一次被计数的 SQL 文本——形状切换时清零 ServedOps 的判据（C15，2026-10-12）：
    /// 计数按形状后，交替形状永不晋升（原计数全体未命中使交替形态反复 Dispose+重建晋升命令）。</summary>
    public string? LastCountedSql;

    /// <summary>晋升时是否请求服务端预编译（由 Provider 能力位决定）。</summary>
    private readonly bool _prepareOnPromotion;

    /// <summary>本次晋升尚待预编译——首次执行前由 <see cref="EnsurePreparedAsync"/> 消费一次。</summary>
    private bool _needsPrepare;

    /// <summary>预编译失败后本槽退化为未预编译形态，不再重试。</summary>
    private bool _prepareFailed;

    internal ReusableQuerySlot(bool prepareOnPromotion)
        => _prepareOnPromotion = prepareOnPromotion;

    /// <summary>晋升后的首次执行前调用一次：预编译是优化而非契约，失败静默退化，
    /// 真正的连接/语句错误由执行路径原样上报。</summary>
    internal async ValueTask EnsurePreparedAsync(CancellationToken ct)
    {
        if (!_needsPrepare)
            return;
        _needsPrepare = false;
        if (_prepareFailed || Command is not { } command)
            return;
        try
        {
            await command.PrepareAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消不是预编译缺陷——恢复待预编译标记，下一次执行再试。
            _needsPrepare = true;
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _prepareFailed = true;
            _ = exception;
        }
    }

    /// <summary>同一 (实体类型, 操作) 的晋升阈值——低于此次数走新建路径（对齐 PL-2，
    /// 防"每操作一会话"的抛弃式用法泄漏命令）。</summary>
    internal const int PromotionThreshold = 3;

    /// <summary>P1-1（2026-10-12 全量重读）：取（或晋升）本槽服务当前 SQL 文本的命令——
    /// From&lt;T&gt; 查询管线（A9）与直查族（QueryAsync/Scalar/Count/聚合/ExecuteAsync）共用的
    /// 单一真源。命中：同 SQL 文本 + 同连接 → 返回槽内命令（调用方负责重设 Transaction/超时、
    /// <b>就地写参数值</b>且不得 Dispose）。未命中：按形状计数（C15），达到阈值在给定连接上
    /// 晋升新命令（旧命令替换即释放——单操作门禁保证无飞行 reader）。
    /// <para>并行读作用域由调用方判定后不进入本方法（并发 reader 不能共用命令）。</para></summary>
    internal DbCommand? TryAcquire(DbConnection connection, string sql)
    {
        if (Command is { } cmd
            && Connection is { } cmdConn
            && ReferenceEquals(cmdConn, connection)
            && string.Equals(Sql, sql, StringComparison.Ordinal))
        {
            return cmd;
        }

        // C15（2026-10-12）：计数按形状——被计数 SQL 与上一被计数 SQL 不同时清零。交替形状
        // （A/B/A/B…）永不晋升，保持每操作新建命令；单形状连续查询的晋升时点不变。
        // 两族共用本槽时同理：已晋升 A 后交替出现的 B 形态永不晋升也不淘汰 A。
        if (!string.Equals(LastCountedSql, sql, StringComparison.Ordinal))
        {
            ServedOps = 0;
            LastCountedSql = sql;
        }
        ServedOps++;
        if (ServedOps < PromotionThreshold)
            return null;

        Command?.Dispose();
        DbCommand promoted = connection.CreateCommand();
        promoted.CommandText = sql;
        Sql = sql;
        Connection = connection;
        Command = promoted;
        _needsPrepare = _prepareOnPromotion;
        return promoted;
    }
}
