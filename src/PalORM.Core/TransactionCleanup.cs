using System.Data.Common;

namespace PalORM;

/// <summary>事务清理助手——主异常保留模式的唯一 Core 内实现（A2 去重：此前
/// DataSession/QueryBuilderExtensions/MultiValueBulkInsert 各持一份逐字复制）。
/// PG Provider 的同名助手是跨程序集刻意独立（Provider 不依赖 Core 内部），不合并。</summary>
internal static class TransactionCleanup
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "回滚是清理路径；异常附加到主异常，不能替换原始执行失败。")]
    internal static async ValueTask RollbackPreservingAsync(
        DbTransaction transaction, Exception primaryException)
    {
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception rollbackException) { primaryException.Data["PalORM.RollbackException"] = rollbackException; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "释放是清理路径；异常附加到主异常，不能替换原始执行失败。")]
    internal static async ValueTask DisposeTransactionPreservingAsync(
        DbTransaction transaction,
        Exception? primaryException,
        string exceptionDataKey = "PalORM.TransactionCleanupException")
    {
        // ITM-595: when (primaryException is not null) 守卫使成功路径（primaryException==null）
        // 下 DisposeAsync 抛的清理异常被完全吞掉——无 Data 挂载点（无主异常可挂）、无日志、
        // 无返回信号。这是成功路径 Dispose 失败极罕见（连接已断才走到此分支）下的取舍：
        // 调用方 WithTransaction/BulkUpdate/BulkDelete 等的 finally 已无业务异常可保留，
        // 重新抛 Dispose 异常会掩盖成功路径的语义（业务方看到异常会以为整体失败）。
        // 替代方案：引入 logger 字段记录此类异常；当前未做（TransactionCleanup 是 static helper
        // 无依赖注入点），调用方自行包 try/catch 可观测成功路径 Dispose 失败。
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data[exceptionDataKey] = cleanupException;
        }
    }
}
