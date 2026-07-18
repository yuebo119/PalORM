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
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data[exceptionDataKey] = cleanupException;
        }
    }
}
