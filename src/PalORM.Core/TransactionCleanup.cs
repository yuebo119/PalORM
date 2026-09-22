using System.Data.Common;

namespace PalORM;

/// <summary>事务清理助手——主异常保留模式的唯一 Core 内实现（A2 去重：此前
/// DataSession/QueryBuilderExtensions/MultiValueBulkInsert 各持一份逐字复制）。
/// PG Provider 的同名助手是跨程序集刻意独立（Provider 不依赖 Core 内部），不合并。</summary>
internal static class TransactionCleanup
{
    /// <summary>失败提交后跳过回滚时写主异常 Data 的键（T1，v5.7）。</summary>
    internal const string RollbackSkippedDataKey = "PalORM.RollbackSkipped";

    /// <summary>失败提交后的回滚裁决（T1，v5.7）。
    /// <para><b>裁决依据</b>：PG/MySQL 的失败 COMMIT 在<b>服务端返回错误</b>时已由服务端
    /// 终止事务（PG 文档：COMMIT 出错即回滚；MySQL 错误处理同义）——后续 RollbackAsync
    /// 只会得到 "transaction already completed" 类驱动噪音并多一次徒劳往返。SQLite 相反：
    /// 失败的 COMMIT（如 SQLITE_BUSY）保留活动事务，必须回滚释放写锁。</para>
    /// <para><b>v5.8 收窄（R1）</b>：原实现对 PG/MySQL 一律跳过回滚，只覆盖"服务端返回错误"。
    /// 若 COMMIT 因<b>连接断开/取消/超时</b>失败，事务在服务端的最终状态未知，此时跳过回滚
    /// 会让服务端事务悬置到连接归还或回收，继续占用锁与 undo 日志。裁决条件从
    /// "方言非 SQLite" 收紧为 "方言非 SQLite 且失败看起来是服务端错误"——
    /// 连接类失败（OperationCanceledException / IOException 及其内层链）照常尝试回滚，
    /// 回滚失败仍按既有模式挂主异常 Data，不覆盖原始失败。</para>
    /// <para>返回 true = 已在主异常 Data 标记跳过（调用方不再回滚）；false = 照常回滚。</para></summary>
    internal static bool TrySkipRollbackAfterCommitFailure(
        SqlDialect dialect, Exception primaryException)
    {
        if (dialect == SqlDialect.Sqlite) return false;
        if (!LooksLikeServerSideCommitError(primaryException)) return false;
        primaryException.Data[RollbackSkippedDataKey] =
            "Rollback skipped: CommitAsync failed; the server already terminated the transaction.";
        return true;
    }

    /// <summary>只按异常形态裁决、不带方言的变体——供多方言共享骨架（<see cref="MultiValueBulkInsert"/>）
    /// 使用：传输类失败一律回滚，服务端错误类一律跳过。</summary>
    internal static bool TrySkipRollbackAfterCommitFailureForException(Exception primaryException)
    {
        if (!LooksLikeServerSideCommitError(primaryException)) return false;
        primaryException.Data[RollbackSkippedDataKey] =
            "Rollback skipped: CommitAsync failed; the server already terminated the transaction.";
        return true;
    }

    /// <summary>判定 COMMIT 失败是否"服务端已收到并处理了提交请求"——只有这种形态才敢跳过回滚。
    /// 连接断裂与取消（含超时）下服务端状态未知，必须尝试回滚。判定保守偏向回滚：
    /// 多回滚一次的代价是一次失败往返挂 Data，少回滚的代价是服务端事务悬置。</summary>
    private static bool LooksLikeServerSideCommitError(Exception exception)
    {
        // 取消/超时：COMMIT 可能根本没到服务端，也可能已执行——状态未知
        if (exception is OperationCanceledException or TimeoutException) return false;
        // 传输层失败：请求可能未送达
        if (exception is System.IO.IOException or System.Net.Sockets.SocketException) return false;
        // 驱动异常（NpgsqlException/MySqlException/SqliteException）本身是服务端错误，
        // 但其内层若是传输层/取消，同样说明请求未完成
        return !HasConnectionFailureInChain(exception);
    }

    /// <summary>沿 InnerException 链查找传输层或取消类失败——驱动常把底层 IO 错误包在
    /// 数据库异常里（如 NpgsqlException { Inner = IOException }）。</summary>
    private static bool HasConnectionFailureInChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TimeoutException
                or System.IO.IOException or System.Net.Sockets.SocketException)
                return true;
        }
        return false;
    }

    /// <summary>回滚——cleanup 失败的异常挂到主异常 Data，不替换原始失败。
    /// <para><b>R3：有界回滚</b>（2026-09-20）——原实现用 <see cref="CancellationToken.None"/>
    /// 无界等待。ITM-747 的论证（"释放必须尽力完成"）对本地句柄 Dispose 成立，但
    /// Rollback 是<b>网络往返</b>，且发生在异常传播路径的 finally 里：网络黑洞下会把
    /// 一次快速失败拖成永久卡死。改为按 <paramref name="rollbackTimeoutSeconds"/> 设有界
    /// 取消；超时后把 TimeoutException 挂主异常 Data（与"清理异常不覆盖主异常"同模式）。
    /// ≤0 表示调用方显式要求无限等待（与 CommandTimeout Zero 契约一致）。</para></summary>
    internal static async ValueTask RollbackPreservingAsync(
        DbTransaction transaction, Exception primaryException,
        int rollbackTimeoutSeconds = DefaultRollbackTimeoutSeconds)
    {
        CancellationToken ct = CancellationToken.None;
        CancellationTokenSource? timeoutCts = null;
        if (rollbackTimeoutSeconds > 0)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(rollbackTimeoutSeconds));
            ct = timeoutCts.Token;
        }
        try
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException timeoutException) when (ct.IsCancellationRequested)
        {
            primaryException.Data["PalORM.RollbackTimeoutException"] = new TimeoutException(
                $"Rollback timed out after {rollbackTimeoutSeconds}s; the server-side transaction may still be open.",
                timeoutException);
        }
        catch (Exception rollbackException)
        {
            primaryException.Data["PalORM.RollbackException"] = rollbackException;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    /// <summary>回滚的有界上限默认值（秒）——正常回滚是毫秒级本地往返；触发即异常路径。</summary>
    internal const int DefaultRollbackTimeoutSeconds = 30;

    /// <summary>提交的超时包装（T1/TX-003，2026-09-22）：事务提交不受 CommandTimeout 治理，
    /// 调用方 ct 为 default 时网络黑洞会让提交永久挂起。Provider 批量路径早已为同一问题实现
    /// 同款包装（PostgreSqlProvider / MySqlProvider 的 CommitWithTimeoutAsync），事务主 API
    /// 此前未对齐——本方法把它收敛为全库单一实现。
    /// <para>口径：<paramref name="commandTimeoutSeconds"/> ≤ 0（Zero 契约）不设超时；调用方取消
    /// 原样上抛（OCE）；仅本方法引入的超时包装为带 <c>PalORM.InfrastructureTimeout</c> 标记的
    /// <see cref="TimeoutException"/>——调用方据此判定"服务端状态未知"并照常尝试回滚。</para></summary>
    internal static async ValueTask CommitWithTimeoutAsync(
        DbTransaction transaction, int commandTimeoutSeconds, CancellationToken ct)
    {
        if (commandTimeoutSeconds <= 0)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(commandTimeoutSeconds));
        try
        {
            await transaction.CommitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException timeoutException) when (timeoutCts.IsCancellationRequested)
        {
            var wrappedTimeout = new TimeoutException(
                $"Commit timed out after {commandTimeoutSeconds}s; the server-side transaction state is unknown.",
                timeoutException);
            wrappedTimeout.Data["PalORM.InfrastructureTimeout"] = true;
            throw wrappedTimeout;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "释放是清理路径；异常附加到主异常，不能替换原始执行失败。")]
    internal static async ValueTask DisposeTransactionPreservingAsync(
        DbTransaction transaction,
        Exception? primaryException,
        string exceptionDataKey = "PalORM.TransactionCleanupException")
    {
        // ITM-595/660：when (primaryException is not null) 守卫的语义——成功路径
        // （primaryException==null）下 DisposeAsync 抛出的清理异常**向外传播**（filter 为
        // false 不捕获），调用方看到释放失败而非静默成功；失败路径下清理异常挂主异常
        // Data 不替换原始失败。这是有意裁决：静默吞掉释放失败违反 B26（防静默错误优先），
        // 且成功路径 Dispose 失败极罕见（连接已断才走到此分支）——传播不掩盖真实信号。
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data[exceptionDataKey] = cleanupException;
        }
    }
}
