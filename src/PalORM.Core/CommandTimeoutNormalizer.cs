using System.Diagnostics;

namespace PalORM;

/// <summary>命令超时的统一归一（D4 与 D7）。
/// <para><b>D4 解包</b>：驱动把命令超时包在自身异常里（Npgsql 的 <c>NpgsqlException</c> 内层是超时异常），
/// 而连接超时直接抛 <see cref="TimeoutException"/>。这里解包，使调用方按超时类型捕获时能覆盖
/// 弹性路径与直通路径两种情形。</para>
/// <para><b>D7 耗时判定</b>：MySQL 驱动对命令超时没有统一形态——标量路径中断查询后<b>不抛异常</b>
/// 而返回部分结果（实测 <c>SELECT SLEEP(5)</c> 在 1 秒后返回 1）；列表路径抛
/// <c>MySqlException</c>（<c>ErrorCode=CommandTimeoutExpired</c>，内层 <c>QueryInterrupted</c> 1317），
/// 类型既不是 <see cref="TimeoutException"/> 也不含超时内层。两条路都不能按异常类型识别。
/// 因此在该方言上按耗时判定：<b>成功返回与抛出异常都先看是否已越过截止线</b>，越过即归一时超。
/// 计时用 <see cref="Stopwatch.GetTimestamp"/>（无分配），且只在 MySQL 且设置了命令超时时启用，
/// 其他方言与未设超时的场景零开销。</para></summary>
internal static class CommandTimeoutNormalizer
{
    /// <summary>是否需要按耗时判定（仅 MySQL 且命令超时为正）。</summary>
    internal static bool RequiresElapsedCheck(SqlDialect dialect, int timeoutSeconds)
        => dialect == SqlDialect.MySql && timeoutSeconds > 0;

    /// <summary>开始计时；不需要判定时返回 0（表示未启用）。</summary>
    internal static long Begin(bool enabled) => enabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>执行操作：成功返回时做耗时判定；异常路径先按内层超时解包（D4），
    /// 再按"已越过截止线"归一（D7）。
    /// <para>计时起点由调用方给（<see cref="Begin"/>），使判定窗口覆盖调用方认为的整个操作，
    /// 而不是从进入本方法才起算。</para>
    /// <para><b>分配纪律（2026-11-01）</b>：同步完成直接返回原 ValueTask；挂起路径走
    /// <see cref="RunCoreAsync{T}"/>（async Task 内核，状态机盒本身即 Task）。实测每条
    /// 挂起命令的包装成本由 329 B 降到 264 B（GetByKeySession 每操作 32 条命令约 8.4 KB）。
    /// 剩余部分是"事后判定"绕不开的一层异步：彻底归零要把 D4/D7 判定内联进各进入方法
    /// 的状态机，属独立改造，本次不做。</para></summary>
    internal static ValueTask<T> RunAsync<T>(
        ValueTask<T> operation, long startTimestamp, int timeoutSeconds)
    {
        if (operation.IsCompletedSuccessfully)
        {
            ThrowIfElapsed(startTimestamp, timeoutSeconds);
            return operation;
        }
        return new ValueTask<T>(RunCoreAsync(operation, startTimestamp, timeoutSeconds));
    }

    /// <summary>同上，<see cref="Task{TResult}"/> 形态（查询管线用）。
    /// <para><b>只在需要判定时调用</b>：调用方先用 <see cref="RequiresElapsedCheck"/> 判方言，
    /// 非 MySQL 直接返回原 Task，不进本方法的状态机。</para>
    /// <para><b>计时起点必须由调用方在启动操作之前取</b>（2026-11-01）：本方法内部再取会把
    /// 进入方法的同步段排除在窗口外，判定宽度短于真实耗时。</para></summary>
    internal static Task<T> RunAsync<T>(Task<T> operation, long startTimestamp, int timeoutSeconds)
    {
        if (operation.IsCompletedSuccessfully)
        {
            ThrowIfElapsed(startTimestamp, timeoutSeconds);
            return operation;
        }
        return RunCoreAsync<T>(new ValueTask<T>(operation), startTimestamp, timeoutSeconds);
    }

    /// <summary>挂起路径的归一内核（唯一的异步状态机）：async Task 的盒本身即 Task，
    /// 比 "async ValueTask 加 AsTask" 少一次 Task 包装分配。</summary>
    private static async Task<T> RunCoreAsync<T>(ValueTask<T> operation, long startTimestamp, int timeoutSeconds)
    {
        try
        {
            T result = await operation.ConfigureAwait(false);
            ThrowIfElapsed(startTimestamp, timeoutSeconds);
            return result;
        }
        catch (Exception ex) when (NormalizeOnce(ex, startTimestamp, timeoutSeconds) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>异常归一的唯一真源：超时原样放行（返回 null，调用方重抛保留原栈），
    /// 内层超时解包（D4），越过截止线的一切异常归一时超（D7）。</summary>
    private static TimeoutException? NormalizeOnce(Exception ex, long startTimestamp, int timeoutSeconds)
    {
        if (ex is TimeoutException)
            return null;
        if (ex.InnerException is TimeoutException timeout)
            return new TimeoutException(timeout.Message, ex);
        if (IsPastDeadline(startTimestamp, timeoutSeconds))
        {
            // 越过截止线后驱动抛出任何类型都视为超时的表现：截止线已过，命令不可能正常完成。
            // 未越线时原样上抛——真正的 SQL 错误（语法、约束）不会等到超时才返回。
            return new TimeoutException(
                $"Command exceeded its {timeoutSeconds}s timeout: the provider interrupted the query and "
                + $"surfaced {ex.GetType().Name} instead of a timeout exception (MySQL semantics).", ex);
        }
        return null;
    }

    private static void ThrowIfElapsed(long startTimestamp, int timeoutSeconds)
    {
        if (IsPastDeadline(startTimestamp, timeoutSeconds))
        {
            throw new TimeoutException(
                $"Command exceeded its {timeoutSeconds}s timeout: the provider interrupted the query "
                + "but returned a partial result instead of throwing (MySQL semantics).");
        }
    }

    private static bool IsPastDeadline(long startTimestamp, int timeoutSeconds)
    {
        if (startTimestamp == 0L || timeoutSeconds <= 0) return false;
        double elapsedSeconds =
            (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
        return elapsedSeconds >= timeoutSeconds;
    }
}