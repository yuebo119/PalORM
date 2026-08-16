using System.Data.Common;

namespace PalORM;

/// <summary>批量写入骨架共享助手——三 Provider（SQLite/MySQL/PG）的 probe + cleanup 模式收敛。
/// <para><b>ITM-412 防漂移锚点</b>：此前 MultiValueBulkInsert 与 PostgreSqlProvider 各自复制
/// ProbeBinderAsync + DisposePreservingAsync，两侧分叉即温床。本类作为单一实现点，修改一处全生效。</para>
/// <para>异常保留模式：cleanup 失败的异常挂到主异常的 Data 字典（不替换原始失败）——
/// 调用方可通过 Data 键追溯清理链。</para>
/// <para><b>可见性</b>：标 public 而非 internal——PalORM.PostgreSql / PalORM.MySql / PalORM.Sqlite
/// 是独立程序集，需跨程序集访问。仍是 PalORM 内部 API（不写入公共文档/不保证兼容）。</para></summary>
public static class BulkOperationFramework
{
    /// <summary>探测 binder 生成的参数数量与列数一致——不一致即抛 InvalidOperationException。
    /// 探测命令独立释放，cleanup 异常挂 Data 不替换原始失败。</summary>
    /// <param name="conn">用于创建 probe 命令的连接。</param>
    /// <param name="binder">源生成器生成的参数绑定委托。</param>
    /// <param name="first">用首行实体调用 binder。</param>
    /// <param name="columnCount">期望的列数（来自 metadata.InsertColumns.Count）。</param>
    /// <param name="typeName">实体类型名，用于错误消息。</param>
    /// <param name="cleanupDataKey">probe 命令清理失败时挂 Data 的键名（如 PalORM.ProbeCommandCleanupException）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async ValueTask ProbeBinderAsync(
        DbConnection conn,
        Action<DbCommand, object, int> binder,
        object first,
        int columnCount,
        string typeName,
        string cleanupDataKey,
        CancellationToken ct = default)
    {
        DbCommand probeCommand = conn.CreateCommand();
        Exception? probeException = null;
        try
        {
            binder(probeCommand, first, 0);
            if (probeCommand.Parameters.Count != columnCount)
                throw new InvalidOperationException(
                    $"Type '{typeName}' generated {columnCount} insert columns but " +
                    $"{probeCommand.Parameters.Count} parameters.");
        }
        catch (Exception exception)
        {
            probeException = exception;
            throw;
        }
        finally
        {
            await DisposePreservingAsync(probeCommand, probeException, cleanupDataKey, ct).ConfigureAwait(false);
        }
    }

    /// <summary>资源清理——cleanup 失败的异常挂到主异常 Data，不替换原始失败。
    /// 通用接口（IAsyncDisposable）覆盖 DbCommand/DbTransaction/NpgsqlBinaryImporter 等全部资源。</summary>
    /// <param name="resource">待释放的资源（DbCommand/DbTransaction/importer 等）。</param>
    /// <param name="primaryException">主异常；为 null（成功路径）时 cleanup 失败**向外传播**
    /// （ITM-660：when-filter false 不捕获——释放失败必须可见，不静默吞，B26）。</param>
    /// <param name="dataKey">cleanup 异常挂 Data 的键名（如 PalORM.CommandCleanupException）。</param>
    /// <param name="ct">取消令牌。</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "释放是清理路径；异常附加到主异常，不能替换原始批量写失败。")]
    public static async ValueTask DisposePreservingAsync(
        IAsyncDisposable resource,
        Exception? primaryException,
        string dataKey,
        CancellationToken ct = default)
    {
        try { await resource.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data[dataKey] = cleanupException;
        }
    }
}
