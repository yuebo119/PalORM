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
    /// <summary>批量写入共用的插入元数据前置守卫——审计 2026-09-19 PROV-010：四处同型守卫
    /// （PG 入口 / MySQL 入口 / MySQL ExecuteBulkCopyAsync / Core MultiValueBulkInsert）收敛为
    /// 单一实现点，消除复制粘贴漂移（MySQL 同文件双份守卫是漂移实证）。
    /// ITM-637 口径：守卫先于空列表短路——未注册类型与空/非空列表一致抛。</summary>
    /// <returns>已验证的写入元数据与表名（InsertColumns 非空）。</returns>
    public static (CrudMetadata Metadata, string TableName) EnsureInsertMetadata(Type entityType)
    {
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(entityType, out CrudMetadata metadata)
            || !PalORM_Runtime.TableNames.TryGetValue(entityType, out string? tableName)
            || metadata.InsertColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{entityType.Name}' has no generated insert metadata.");
        return (metadata, tableName);
    }

    /// <summary>探测 binder 生成的参数数量与列数一致——不一致即抛 InvalidOperationException。
    /// 探测命令独立释放，cleanup 异常挂 Data 不替换原始失败。</summary>
    /// <param name="conn">用于创建 probe 命令的连接。</param>
    /// <param name="binder">源生成器生成的参数绑定委托。</param>
    /// <param name="first">用首行实体调用 binder。</param>
    /// <param name="columnCount">期望的列数（来自 metadata.InsertColumns.Count）。</param>
    /// <param name="typeName">实体类型名，用于错误消息。</param>
    /// <param name="cleanupDataKey">probe 命令清理失败时挂 Data 的键名（如 PalORM.ProbeCommandCleanupException）。</param>
    /// <param name="ct">取消令牌。ITM-784(r21)：当前无消费点（binder 与 CreateCommand 均同步）——
    /// 保留参数是为公共 async API 的取消令牌一致性（G25），未来探测引入可取消 IO 时立即生效。</param>
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
            await DisposePreservingAsync(probeCommand, probeException, cleanupDataKey).ConfigureAwait(false);
        }
    }

    /// <summary>R14：Provider 能力探测（如 MySQL <c>local_infile</c>）失败计数。
    /// <para><b>为什么需要</b>：探测故障与"能力关闭"都会让批量路径降级到慢形态
    /// （MySQL 多值 INSERT vs BulkCopy 差约 4.84×），原降级路径静默无痕——生产上
    /// "突然变慢"无从归因。纯进程内计数，不依赖 LoggerFactory（默认会话是 NullLogger）。</para>
    /// <para>公开为 public 是为了让三个独立 Provider 程序集都能上报（Provider 不依赖
    /// Core 的内部可见成员），仍是 PalORM 内部 API，不写入公共文档、不保证兼容。</para></summary>
    public static void RecordCapabilityProbeFailure()
        => System.Threading.Interlocked.Increment(ref _capabilityProbeFailures);

    private static long _capabilityProbeFailures;

    /// <summary>累计能力探测失败次数——正常运行应恒为 0，增长即批量写入在静默降级。</summary>
    public static long CapabilityProbeFailures =>
        System.Threading.Interlocked.Read(ref _capabilityProbeFailures);

    /// <summary>资源清理——cleanup 失败的异常挂到主异常 Data，不替换原始失败。
    /// 通用接口（IAsyncDisposable）覆盖 DbCommand/DbTransaction/NpgsqlBinaryImporter 等全部资源。
    /// <para><b>无取消参数（ITM-747 r20）</b>：释放路径不接受 CancellationToken——原签名的
    /// <c>ct</c> 从未被消费（仅透传），公共 API 带令牌会诱导调用方以为释放受取消约束。
    /// 释放必须尽力完成，不受取消影响（CancellationToken.None 语义由实现固定）。</para></summary>
    /// <param name="resource">待释放的资源（DbCommand/DbTransaction/importer 等）。</param>
    /// <param name="primaryException">主异常；为 null（成功路径）时 cleanup 失败**向外传播**
    /// （ITM-660：when-filter false 不捕获——释放失败必须可见，不静默吞，B26）。</param>
    /// <param name="dataKey">cleanup 异常挂 Data 的键名（如 PalORM.CommandCleanupException）。</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "释放是清理路径；异常附加到主异常，不能替换原始批量写失败。")]
    public static async ValueTask DisposePreservingAsync(
        IAsyncDisposable resource,
        Exception? primaryException,
        string dataKey)
    {
        try { await resource.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data[dataKey] = cleanupException;
        }
    }
}
