using Npgsql;
using NpgsqlTypes;

namespace PalORM.PostgreSql;

/// <summary>PostgreSQL NOTIFY/LISTEN 异步通知监听器。
/// 每次断线后创建新连接并重新执行全部 LISTEN；不复用已损坏的会话。
/// <para>注意: <see cref="OnNotification"/> 事件在监听后台任务上触发。
/// 订阅者异常会被隔离，不会终止后续订阅者或重连循环。</para></summary>
public sealed partial class PgNotificationListener : IAsyncDisposable
{
    private const int _maxReconnectAttempts = 5;

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Error,
        Message = "PgNotificationListener background loop terminated; notifications will no longer be delivered.")]
    private static partial void LogListenerTerminated(
        Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "PgNotificationListener: failed disposing damaged connection (swallowed).")]
    private static partial void LogDisposeFailed(
        Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "PgNotificationListener: OnError subscriber threw an exception (swallowed).")]
    private static partial void LogOnErrorSubscriberThrew(
        Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "PgNotificationListener: OnNotification subscriber threw an exception (swallowed).")]
    private static partial void LogOnNotificationSubscriberThrew(
        Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Debug,
        Message = "PgNotificationListener: run task was canceled while stopping.")]
    private static partial void LogRunTaskCanceled(
        Microsoft.Extensions.Logging.ILogger logger);

    private readonly Func<IPgNotificationConnection> _connectionFactory;
    private readonly Func<int, TimeSpan> _reconnectDelay;
    private readonly string[] _channels;
    /// <summary>B7：channel 的引用形态在构造期预计算——原每次重连都对全部 channel 重跑
    /// <see cref="PostgreSqlProvider.QuoteIdentifier"/>（含 IdentifierSafety 校验 + 字符串拼接），
    /// 而 channel 集合构造后不变。</summary>
    private readonly IReadOnlyList<string> _quotedChannels;
    private readonly Lock _lock = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "RunAsync finally owns and disposes the active CancellationTokenSource after the run task exits.")]
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _disposed;

    /// <summary>首次启动成功后，后台监听因非取消异常终止时触发。</summary>
    public event EventHandler<PgNotificationErrorEventArgs>? OnError;

    /// <summary>可选兜底日志。未订阅 <see cref="OnError"/> 时，后台监听终止原因
    /// 经此记录，避免监听器静默死亡后 NOTIFY 丢失无痕。</summary>
    public Microsoft.Extensions.Logging.ILogger? Logger { get; set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDE", "IDE0032:Use auto property",
        Justification = "Volatile 发布需要显式支撑字段——后台线程写、外部线程读，"
            + "自动属性无法表达 Volatile.Read/Write 语义（ITM-761）。")]
    private Exception? _lastError;

    /// <summary>ITM-724(r20)：最近一次后台终止且无 <see cref="OnError"/> 订阅者时的异常。
    /// 未配置 <see cref="Logger"/>（默认构造）时，这是监听器静默死亡后唯一可观察的信号。
    /// <para><b>ITM-761(r21) 生命周期</b>：①<see cref="StartAsync"/> 成功时清零（上一次
    /// 运行期的失败不得污染新会话的判断）；②后台线程写、外部线程读——经 Volatile 发布
    /// （引用写原子，但可见性需屏障）；③查询时机：监听循环结束后（StopAsync 返回），
    /// <b>不含</b>首次启动失败——该异常由 StartAsync 直接抛出，不经本属性。</para></summary>
    public Exception? LastError
    {
        get => Volatile.Read(ref _lastError);
        private set => Volatile.Write(ref _lastError, value);
    }

    /// <summary>创建监听器,重连退避为线性递增 + 全量抖动(第 n 次重连等待 n 秒 ± 抖动,上限 5 次)。
    /// 构造不建立连接;调用 <see cref="StartAsync"/> 后才连接并 LISTEN。
    /// <para><b>A5 为什么加抖动</b>：无抖动的线性退避在 PG 重启/网络恢复瞬间会让同进程或同机的
    /// 大量监听器以完全同步的 1s/2s/3s 节奏重连，对刚恢复的服务端形成二次冲击，可能把瞬时
    /// 故障放大成持续故障。抖动把重连时刻打散。</para></summary>
    public PgNotificationListener(string connectionString, params string[] channels)
        : this(() => new NpgsqlNotificationConnection(connectionString),
            channels,
            DefaultReconnectDelay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    /// <summary>A5：默认重连退避——线性递增（第 n 次 n 秒）+ 全量抖动（0~100% 随机乘子），
    /// 上限 30 秒。全量抖动而非固定小额的原因是：监听器数量与启动时刻都不可控，
    /// 只有打散到整个区间才能避免同步。</summary>
    // CA5394：此处要的是抖动（打散重连时刻），不是密码学随机——用 RandomNumberGenerator
    // 反而给热路径增加不必要的开销。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394",
        Justification = "Jitter for reconnect backoff, not a security-sensitive random value.")]
    private static readonly Func<int, TimeSpan> DefaultReconnectDelay = attempt =>
    {
        double baseSeconds = Math.Min(Math.Max(attempt, 1), MaxReconnectDelaySeconds);
        double jittered = baseSeconds * Random.Shared.NextDouble();
        return TimeSpan.FromSeconds(Math.Max(jittered, 0.05));
    };

    /// <summary>重连退避的上限（秒）——与 <see cref="DefaultReconnectDelay"/> 共用。</summary>
    private const double MaxReconnectDelaySeconds = 30;

    /// <summary>A2：心跳间隔默认值——LISTEN 连接的静默断线检测周期。30 秒远小于常见
    /// NAT/LB 空闲超时（通常 60~350s），又不足以对服务端造成可感知负担
    /// （每 30 秒一次 SELECT 1）。</summary>
    internal static readonly TimeSpan DefaultKeepaliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>A2：本实例的心跳间隔——可注入以便测试与按部署调整（见内部构造的
    /// <c>keepaliveInterval</c> 参数）。</summary>
    private readonly TimeSpan _keepaliveInterval;

    internal PgNotificationListener(
        Func<IPgNotificationConnection> connectionFactory,
        string[] channels,
        Func<int, TimeSpan>? reconnectDelay = null,
        TimeSpan? keepaliveInterval = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length == 0) throw new ArgumentException("At least one channel must be specified.", nameof(channels));
        foreach (string channel in channels)
            ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        _connectionFactory = connectionFactory;
        _channels = (string[])channels.Clone();
        // B7：引用形态预计算一次，重连直接用
        _quotedChannels = [.. _channels.Select(PostgreSqlProvider.QuoteIdentifier)];
        _reconnectDelay = reconnectDelay ?? DefaultReconnectDelay;
        _keepaliveInterval = keepaliveInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : DefaultKeepaliveInterval;
    }

    /// <summary>启动后台监听:首次连接成功并对全部 channel 执行 LISTEN 后返回;
    /// 首次连接失败时异常直接抛出(不进入重连)。非幂等——已启动时再次调用抛
    /// <see cref="InvalidOperationException"/>;<see cref="StopAsync"/> 之后可再次启动。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        CancellationTokenSource cts;
        Task runTask;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runTask is not null) throw new InvalidOperationException("Listener is already started.");
            // A6：入口即清零——原实现只在 started.Task 成功后清零，首次启动就失败的路径
            // 会把上一次会话的 LastError 留给调用方（与本次失败无关的陈旧故障态）。
            // 语义变为「本次启动尝试前的清零」，与 ITM-761 的成功路径清零不冲突
            // （成功后此处已清零，运行期故障仍会写入新值）。
            LastError = null;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            runTask = RunAsync(cts, started);
            _runTask = runTask;
        }

        try
        {
            await started.Task.ConfigureAwait(false);
            // ITM-761(r21)：新一次启动成功——上一次运行期的 LastError 清零，
            // 避免已恢复的监听器被读为故障态。
            LastError = null;
        }
        catch
        {
            // 启动失败路径：等待 RunAsync 内部收尾（其自管 CTS/连接清理与错误上报），
            // 再重抛原始异常——此处不吞不改型，仅保证 started 未触发时后台任务不悬空。
            await runTask.ConfigureAwait(false);
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "RunAsync 是后台监听主循环：双层 while + 多层 try/catch 是异步生命周期管理的必然形态。"
            + "finally 块的 CTS 所有权判定与 StopCoreAsync 存在显式并发契约（见注释 line 195-205），"
            + "严禁拆分——拆分会破坏 dispose 权移交语义。")]
    private async Task RunAsync(CancellationTokenSource owner, TaskCompletionSource started)
    {
        // 先完成 _runTask 发布，避免同步 Open 失败的 finally 被随后赋值覆盖。
        await Task.Yield();
        int reconnectAttempt = 0;
        bool initialConnection = true;
        // ITM-620：startupPhase = started.Task 尚未完成（StartAsync 仍在 await started）。
        // initialConnection 在 Open 成功后即置 false（R9），而 LISTEN 阶段耗尽重连的 throw
        // 需要区分"启动失败"（TrySetException 让 StartAsync 抛真实异常）与"运行期失败"
        // （RaiseError）——用 initialConnection 判定会把启动失败误报为 TaskCanceled。
        bool startupPhase = true;
        try
        {
            while (!owner.IsCancellationRequested)
            {
                // ITM-583：连接释放显式管理——断线后 NpgsqlConnection.DisposeAsync 若抛出，
                // `await using` 的隐式 Dispose 会绕过 transient 重连逻辑直达外层终止监听。
                // 已损坏连接的清理失败无诊断价值，吞掉后按原路径继续重连。
                IPgNotificationConnection connection = _connectionFactory();
                connection.Notification += OnConnectionNotification;
                try
                {
                    await connection.OpenAsync(owner.Token).ConfigureAwait(false);
                    // R9 修复：Open 成功即标记 initialConnection=false——LISTEN 阶段 transient 失败
                    // 也应走重连逻辑而非直接终止监听（review R9：首次 LISTEN 失败永久退出）。
                    initialConnection = false;  // r5-S1 后 wasInitial 无消费者——S1481 移除
                    // B7：全部 channel 一次往返（原逐 channel 各一次 RTT + 各建一个命令）；
                    // 引用名在构造期预计算（重连不重跑 IdentifierSafety 校验与拼接）。
                    await connection.ListenAllAsync(_quotedChannels, owner.Token).ConfigureAwait(false);

                    // r5-S1：完成信号锚定 startupPhase（与异常路径判定一致）——原 wasInitial 在
                    // R9 提前置 false 后，"首连 LISTEN 瞬态失败→重连成功"路径永不 TrySetResult，
                    // StartAsync 永久挂起而监听器实际正常（最隐蔽形态）。
                    if (startupPhase)
                    {
                        started.TrySetResult();
                        startupPhase = false;
                    }
                    // ITM-567：重连成功（Open+LISTEN 全通过）即清零——此前仅收到 NOTIFY 才清零，
                    // 静默通道 + 周期性断连（LB 空闲切断）下每次成功重连仍累加计数，
                    // 第 N+1 次断开监听器永久死亡。上限语义 = 连续失败次数，与文档直觉一致。
                    reconnectAttempt = 0;

                    // A2：心跳保活——原实现单次无限期 WaitAsync，连接被中间设备静默掐断
                    // （NAT 超时、LB 空闲切断、无 FIN/RST）时不返回也不抛错，重连逻辑
                    // 完全不触发；该线程同时承担断线感知，低频通道上 NOTIFY 丢失可持續
                    // 数小时无痕。改为周期性向服务端发 SELECT 1，
                    // 失败即退出内层循环走既有 transient 重连路径。
                    // NOTIF-001（2026-09-22）：原实现复用一个 CTS 并在循环内 CancelAfter 重臂，
                    // 但 CTS 一经取消即不可复用（重臂是 no-op），且 waitTask 因它取消产生的 OCE
                    // 没有任何 catch filter 接住（owner 未取消 + 无 PalORM.IsTransient 标记）——
                    // 空闲通道（监听器常态）在 1~2 个心跳周期内静默死亡；每轮还遗留一个不可取消的
                    // 心跳 Task.Delay 定时器。改用 WaitAsync(TimeSpan) 表达超时：
                    //   ① 无 delayTask，超时不再依赖第二个定时器，也不存在取消语义复用；
                    //   ② 超时分支先 Cancel 取消悬挂的 wait——不留同一连接上的并发等待；
                    //   ③ 通知到达走原路径，连接故障仍按 transient 走既有重连。
                    while (!owner.IsCancellationRequested)
                    {
                        using var beat = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
                        Task waitTask = connection.WaitAsync(beat.Token);
                        try
                        {
                            // 超时是本分支唯一的取消源：owner 取消经 beat 联动 waitTask 传播，
                            // 由外层 catch filter（owner.IsCancellationRequested）收口。
                            await waitTask.WaitAsync(_keepaliveInterval, CancellationToken.None)
                                .ConfigureAwait(false);
                            continue;  // 通知到达
                        }
                        catch (TimeoutException)
                        {
                            // 心跳到期：取消悬挂的 wait，避免下一轮对同一连接并发等待
                            await beat.CancelAsync().ConfigureAwait(false);
                        }

                        // 探测连接是否仍活着（静默断线的唯一可靠信号）
                        if (!await ProbeConnectionAsync(connection, owner.Token).ConfigureAwait(false))
                            throw CreateKeepaliveFailure();
                    }
                }
                catch (OperationCanceledException) when (owner.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (IsTransient(exception) && !initialConnection)
                {
                    if (++reconnectAttempt > _maxReconnectAttempts)
                        throw;
                    await Task.Delay(_reconnectDelay(reconnectAttempt), owner.Token).ConfigureAwait(false);
                    continue;
                }
                finally
                {
                    connection.Notification -= OnConnectionNotification;
                    try { await connection.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception disposeException) when (disposeException is not OperationCanceledException)
                    {
                        // 损坏连接的 Dispose 失败不改变控制流（重连/终止由上方 catch 决定）
                        if (Logger is { } logger1)
                            LogDisposeFailed(logger1, disposeException);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            if (startupPhase)
                started.TrySetCanceled(owner.Token);
        }
        catch (Exception exception)
        {
            if (startupPhase)
                started.TrySetException(exception);
            else
                RaiseError(exception);
        }
        finally
        {
            started.TrySetCanceled(owner.Token);
            bool ownsDispose;
            lock (_lock)
            {
                // 引用仍指向 owner = 自然退出（无 Stop 在飞），本方法负责释放；
                // 引用已被 StopCoreAsync 清空 = Stop 已接管释放权——其 CancelAsync 可能未完成，
                // 此处 Dispose 会与之并发（CTS 不支持），改由 Stop 在 runTask 结束后释放。
                ownsDispose = ReferenceEquals(_cts, owner);
                if (ownsDispose)
                {
                    _cts = null;
                    _runTask = null;
                }
            }
            if (ownsDispose)
                owner.Dispose();
        }
    }

    private static bool IsTransient(Exception exception)
        => exception.Data["PalORM.IsTransient"] is true;

    /// <summary>A2：心跳探测——向服务端发 <c>SELECT 1</c> 验证连接仍活着。
    /// 静默断线（无 FIN/RST）时 WaitAsync 不返回也不抛错，只有主动探测能发现。</summary>
    private static async Task<bool> ProbeConnectionAsync(
        IPgNotificationConnection connection, CancellationToken ct)
    {
        try
        {
            await connection.ProbeAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>A2：心跳失败构造的异常——带 <c>PalORM.IsTransient</c> 标记，使既有
    /// transient 重连路径接管（与 <c>NpgsqlNotificationConnection.WrapConnectionException</c> 同口径）。</summary>
    private static InvalidOperationException CreateKeepaliveFailure()
    {
        var exception = new InvalidOperationException(
            "PostgreSQL notification connection failed keepalive probe; the connection is assumed dead.");
        exception.Data["PalORM.IsTransient"] = true;
        return exception;
    }

    private void RaiseError(Exception exception)
    {
        EventHandler<PgNotificationErrorEventArgs>? handlers = OnError;
        if (handlers is null)
        {
            // 无订阅者时后台监听终止必须留痕——否则 NOTIFY 静默丢失（审计 ERR-02）。
            // ITM-724(r20)：Logger 为 null（默认构造）时写入 NullLogger（IsEnabled 恒 false）
            // 等于不留痕，监听器静默死亡且调用方无从察觉。此时记录到 LastError 供
            // StopAsync/DisposeAsync 之后查询——默认会话下唯一的可观察信号。
            LastError = exception;
            LogListenerTerminated(
                Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                exception);
            return;
        }

        var args = new PgNotificationErrorEventArgs(exception);
        bool subscriberThrew = false;
        foreach (Delegate candidate in handlers.GetInvocationList())
        {
            var handler = (EventHandler<PgNotificationErrorEventArgs>)candidate;
            try { handler(this, args); }
            catch (Exception ex)
            {
                subscriberThrew = true;
                // 记录但不传播订阅者异常，确保其他订阅者和监听循环不受影响
                if (Logger is { } logger1)
                    LogOnErrorSubscriberThrew(logger1, ex);
            }
        }
        // r19/ITM-702：订阅者全部抛异常时，订阅者异常只有 Debug 级——监听终止根因
        // 必须 Error 级留痕（与无订阅者路径同口径），否则生产排障丢根因。
        if (subscriberThrew && Logger is { } logger2)
            LogListenerTerminated(logger2, exception);
    }

    private void OnConnectionNotification(string channel, string payload)
    {
        // B6：缓存调用列表快照——原实现每次通知都调 handlers.GetInvocationList()，
        // 每收到一条 NOTIFY 即一次 Delegate[] 分配（订阅者越多数组越大；10k 通知/秒
        // = 10k 数组/秒纯 GC 垃圾）。自定义 add/remove 在变更时换新数组（Interlocked
        // 保证原子发布），分发路径直接 foreach 缓存数组：零分配且天然是安全快照。
        // 注意：分发仍在泵任务线程上同步执行（既有契约）——订阅者慢会阻塞后续通知，
        // 那是 A3 的改造面，涉及回调线程语义变更，未在本次落地。
        Delegate[]? handlers = Volatile.Read(ref _notificationHandlers);
        if (handlers is null || handlers.Length == 0)
            return;

        var args = new PgNotificationEventArgs(channel, payload);
        foreach (Delegate candidate in handlers)
        {
            var handler = (EventHandler<PgNotificationEventArgs>)candidate;
            try { handler(this, args); }
            catch (Exception ex)
            {
                // 记录但不传播订阅者异常，确保其他订阅者和监听循环不受影响
                if (Logger is { } logger1)
                    LogOnNotificationSubscriberThrew(logger1, ex);
            }
        }
    }

    /// <summary>B6：<see cref="OnNotification"/> 的调用列表缓存——add/remove 时经
    /// Interlocked.CompareExchange 换新数组（交换失败重试），分发路径只读。
    /// 支撑字段 <c>_notification</c>（委托本体）与 <c>_notificationHandlers</c>（分发快照）
    /// 成对维护：访问器内只能操作字段，不能对事件自身 +=（会递归）。</summary>
    private EventHandler<PgNotificationEventArgs>? _notification;
    private Delegate[]? _notificationHandlers;

    /// <summary>收到 NOTIFY 时触发。回调在后台监听任务线程上执行——耗时处理请自行转移到其他线程。
    /// 单个订阅者抛出的异常被吞掉,不会阻断其他订阅者,也不会终止监听循环。
    /// <para><b>B6</b>：自定义 add/remove 缓存调用列表快照——分发路径零分配
    /// （原每次通知都 <c>GetInvocationList()</c> 分配一个 <see cref="Delegate"/> 数组）。</para></summary>
    // CA1030：访问器名 add_/remove_ 被建议改为事件——这是 C# 事件的标准命名形态，
    // 规则在此为误报（本意是防「看起来像事件的普通方法」）。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1030",
        Justification = "Standard C# event accessor naming (add_/remove_); the rule targets methods that merely look like events.")]
    public event EventHandler<PgNotificationEventArgs>? OnNotification
    {
        add
        {
            while (true)
            {
                EventHandler<PgNotificationEventArgs>? current = Volatile.Read(ref _notification);
                EventHandler<PgNotificationEventArgs>? updated = current + value;
                Delegate[]? snapshot = updated?.GetInvocationList();
                if (Interlocked.CompareExchange(
                        ref _notification, updated, current) == current)
                {
                    Volatile.Write(ref _notificationHandlers, snapshot);
                    return;
                }
            }
        }
        remove
        {
            while (true)
            {
                EventHandler<PgNotificationEventArgs>? current = Volatile.Read(ref _notification);
                EventHandler<PgNotificationEventArgs>? updated = current - value;
                Delegate[]? snapshot = updated?.GetInvocationList();
                if (Interlocked.CompareExchange(
                        ref _notification, updated, current) == current)
                {
                    Volatile.Write(ref _notificationHandlers, snapshot);
                    return;
                }
            }
        }
    }

    /// <summary>停止后台监听并等待监听任务结束(取消异常被吞)。幂等——未启动或已停止时直接返回;
    /// 并发调用由释放权移交保证只有一方执行清理。</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _cts; }
        if (cts is not null)
            await StopCoreAsync(cts).ConfigureAwait(false);
    }

    private async Task StopCoreAsync(CancellationTokenSource owner)
    {
        Task? runTask;
        Task cancellation;
        lock (_lock)
        {
            if (!ReferenceEquals(_cts, owner))
                return;
            // 先清引用声明释放权：RunAsync 的 finally 看到引用非 owner 即不再 Dispose，
            // 消除 Dispose 与未完成 CancelAsync 的并发（CTS 不支持该并发）。
            _cts = null;
            runTask = _runTask;
            _runTask = null;
            cancellation = owner.CancelAsync();
        }

        await cancellation.ConfigureAwait(false);
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // 正常的取消路径，记录以便诊断但不抛出
                if (Logger is { } logger1)
                    LogRunTaskCanceled(logger1);
            }
        }

        // CancelAsync 已完成、runTask 已结束——此刻 Dispose 无并发窗口。
        owner.Dispose();
    }

    /// <summary>发送 NOTIFY（参数化 pg_notify()，零 SQL 注入风险）。</summary>
    public static async Task NotifyAsync(string connectionString, string channel, string? payload = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
        // A6：显式设 CommandTimeout——原实现走驱动默认 30s，与 DataSession.CreateCommand
        // 声明的「超时权威源」（DataSession.cs:596-600）脱钩，用户调大/调小
        // CommandTimeoutSeconds 在此处静默失效。
        cmd.CommandTimeout = DbOptions.ToCommandTimeoutSeconds(DbOptions.DefaultCommandTimeout);
        ConfigureNotifyParameters(cmd, channel, payload);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static void ConfigureNotifyParameters(NpgsqlCommand command, string channel, string? payload)
    {
        command.Parameters.AddWithValue("@channel", NpgsqlDbType.Text, channel);
        command.Parameters.AddWithValue("@payload", NpgsqlDbType.Text, (object?)payload ?? DBNull.Value);
    }

    /// <summary>停止监听并标记已释放。幂等;释放后 <see cref="StartAsync"/> 抛 <see cref="ObjectDisposedException"/>。</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await StopAsync().ConfigureAwait(false);
    }
}
