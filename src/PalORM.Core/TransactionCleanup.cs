using System.Data.Common;

namespace PalORM;

/// <summary>事务清理助手——主异常保留模式的唯一 Core 内实现（A2 去重：此前
/// DataSession/QueryBuilderExtensions/MultiValueBulkInsert 各持一份逐字复制）。
/// PG Provider 的同名助手是跨程序集刻意独立（Provider 不依赖 Core 内部），不合并。</summary>
internal static class TransactionCleanup
{
    /// <summary>失败提交后跳过回滚时写主异常 Data 的键（T1，v5.7）。</summary>
    internal const string RollbackSkippedDataKey = "PalORM.RollbackSkipped";

    /// <summary>失败提交后的回滚裁决（T1，v5.7）：PG/MySQL 的失败 COMMIT 由服务端终止
    /// 事务（PG 文档：COMMIT 出错即回滚；MySQL 错误处理同义）——后续 RollbackAsync 只会
    /// 得到"transaction already completed"类驱动噪音并多一次徒劳往返。SQLite 相反：
    /// 失败的 COMMIT（如 SQLITE_BUSY）保留活动事务，必须回滚释放写锁。
    /// 返回 true = 已在主异常 Data 标记跳过（调用方不再回滚）；false = 照常回滚。</summary>
    internal static bool TrySkipRollbackAfterCommitFailure(
        SqlDialect dialect, Exception primaryException)
    {
        if (dialect == SqlDialect.Sqlite) return false;
        primaryException.Data[RollbackSkippedDataKey] =
            "Rollback skipped: CommitAsync failed; the server already terminated the transaction.";
        return true;
    }

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
