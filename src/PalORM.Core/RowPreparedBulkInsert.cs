using System.Data.Common;

namespace PalORM;

/// <summary>行预编译批量 INSERT 骨架——SQLite 专属（A 方案，2026-10-25 立项）。
/// <para>形态：单行 INSERT 语句 + columnCount 个参数一次建立挂命令，事务内逐行只写
/// <c>Value</c> 执行。命令文本恒定命中 Microsoft.Data.Sqlite 的连接级语句缓存，
/// 每行成本从"巨型多值语句解析"（多值路径，探针实测 8.6~9.4µs/行）降到
/// "绑定 + 步进"（探针实测 2.2~2.6µs/行，UpsertProbe 2026-10-25）。</para>
/// <para><b>为什么仅 SQLite</b>：进程内库无网络往返，逐行执行只付绑定成本；MySQL/PG
/// 逐行 = N 次 RTT（PerfHub 备案：20K 行 MySQL multi-exec 31s），联网方言必须维持
/// 多值/加载器形态。MySQL 的多值路径继续走 <see cref="MultiValueBulkInsert"/>。</para>
/// <para><b>batchSize 语义（A 方案口径变更）</b>：本骨架不分批——<c>BatchSize</c> 不再
/// 决定语句形状，仅作兼容保留（上游校验仍拒绝 ≤0）；事务包整批，全有或全无。</para>
/// <para>职责与 <see cref="MultiValueBulkInsert"/> 同构：元数据守卫 → binder probe →
/// 单行 SQL 构建缓存 → 事务生命周期（R1 提交裁决/T3 异常保留清理）→ 逐行执行。
/// 参数池形态复用 v4.6 契约：<c>InitInsertParameters</c> 一次建立 DbType，
/// <c>BindInsertValues</c> 每行只写 Value；旧生成器程序集（无值 binder）回退
/// 每行 Clear + <c>BindInsert</c> 的整建形态。</para></summary>
public static class RowPreparedBulkInsert
{
    /// <summary>单行 INSERT 文本缓存——键 (实体类型, quotedTable)，值恒定
    /// （列集来自编译期注册表、引用符由 Provider 决定；与
    /// <see cref="MultiValueBulkInsert"/> 的 QuotedColumnsCache 同键空间纪律）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (Type EntityType, string QuotedTable), string> SingleRowSqlCache = new();

    /// <summary>行预编译批量写入实体列表，返回受影响总行数（每行 1，驱动口径）。
    /// <paramref name="transaction"/> 为 null 时自建事务并在全部行成功后提交，
    /// 传入外部事务时提交/回滚由调用方负责。
    /// <paramref name="ctx"/>.<see cref="BulkContext.CommandTimeoutSeconds"/> 应用到
    /// 命令（逐行执行共享同一命令，超时语义为单语句超时而非整批墙钟）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "表名/列名来自源生成器")]
    public static async Task<long> ExecuteAsync<T>(
        DbConnection conn,
        DbTransaction? transaction,
        IReadOnlyList<T> entities,
        BulkContext ctx,
        CancellationToken ct)
        where T : class, new()
    {
        // 守卫与 MultiValueBulkInsert 同族：BatchSize/超时拒绝非法值（BatchSize 不再塑形但仍校验）
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ctx.BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(ctx.CommandTimeoutSeconds);
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(ctx.QuoteIdentifier);
        ArgumentNullException.ThrowIfNull(ctx.CreateParameter);
        // ITM-637 同族：元数据检查先于空列表短路
        (CrudMetadata metadata, string tableName) = BulkOperationFramework.EnsureInsertMetadata(typeof(T));
        if (entities.Count == 0) return 0;

        Action<DbCommand, object, int> binder = metadata.BindInsert;
        Action<DbParameter[], object, int>? valuesBinder = metadata.BindInsertValues;
        int columnCount = metadata.InsertColumns.Count;
        // ITM-846 同族独立防御位
        if (columnCount == 0)
            throw new NotSupportedException(
                $"Type '{typeof(T).Name}' has no insertable columns; row-prepared insert requires at least one.");
        // v4.3 同族：源生成器保证 binder 参数数 == 列数，probe 只需首次验证
        if (!metadata.InsertBinderValidated)
        {
            await BulkOperationFramework.ProbeBinderAsync(
                conn, binder, entities[0], columnCount, typeof(T).Name,
                "PalORM.ProbeCommandCleanupException", ct).ConfigureAwait(false);
        }

        string quotedTable = ctx.QuoteIdentifier(tableName);
        string sql;
        if (SingleRowSqlCache.TryGetValue((typeof(T), quotedTable), out string? cached))
        {
            sql = cached;
        }
        else
        {
            string quotedColumns = string.Join(", ", metadata.InsertColumns.Select(ctx.QuoteIdentifier));
            Span<char> sqlBuffer = stackalloc char[512];
            var sb = new ValueStringBuilder(sqlBuffer);
            try
            {
                // 占位符形态与多值路径逐字一致（@pN）——binder/参数池两套生成物零改动复用
                MultiValueBulkInsert.AppendInsertStatement(ref sb, quotedTable, quotedColumns, 1, columnCount);
                sql = sb.ToString();
            }
            finally
            {
                sb.Dispose();
            }

            SingleRowSqlCache.TryAdd((typeof(T), quotedTable), sql);
        }

        // r7-S1 同族：自开事务遵从会话隔离级别；R1/T3 提交裁决与异常保留清理镜像多值骨架
        DbTransaction tran = transaction
            ?? await conn.BeginTransactionAsync(ctx.IsolationLevel, ct).ConfigureAwait(false);
        bool ownsTransaction = transaction is null;
        Exception? primaryException = null;
        bool commitAttempted = false;
        long total;
        try
        {
            total = await ExecuteRowsAsync(conn, tran, entities, sql, binder, valuesBinder,
                metadata.InitInsertParameters, columnCount, ctx.CommandTimeoutSeconds, ct).ConfigureAwait(false);
            if (ownsTransaction)
            {
                commitAttempted = true;
                await TransactionCleanup.CommitWithTimeoutAsync(tran, ctx.CommandTimeoutSeconds, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            primaryException = exception;
            // R1 同族：批执行失败一律回滚；仅 CommitAsync 自身失败走"跳过回滚"裁决
            if (ownsTransaction
                && (!commitAttempted
                    || !TransactionCleanup.TrySkipRollbackAfterCommitFailureForException(exception)))
            {
                await TransactionCleanup.RollbackPreservingAsync(
                    tran, exception, ctx.CommandTimeoutSeconds, ct).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (ownsTransaction)
                await TransactionCleanup.DisposeTransactionPreservingAsync(tran, primaryException,
                    "PalORM.TransactionCleanupException").ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>单行语句逐行执行——命令文本恒定，参数池（v4.6 契约）建后只写 Value；
    /// 旧生成器程序集（<paramref name="valuesBinder"/> 为 null）回退每行 Clear + 整建。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S107:MethodsShouldNotHaveTooManyParameters",
        Justification = "与 MultiValueBulkInsert.ExecuteBatchesAsync 同族：批量骨架的必要组件，"
            + "聚合为对象会引入跨方法状态传递。")]
    private static async Task<long> ExecuteRowsAsync<T>(
        DbConnection conn,
        DbTransaction tran,
        IReadOnlyList<T> entities,
        string sql,
        Action<DbCommand, object, int> binder,
        Action<DbParameter[], object, int>? valuesBinder,
        Action<DbParameter[], int>? initInsertParameters,
        int columnCount,
        int commandTimeoutSeconds,
        CancellationToken ct) where T : class, new()
    {
        long total = 0;
        // 不用 await using：释放统一走 finally 的 DisposePreservingAsync（附加到主异常
        // Data，不替换原始失败）——await using 会二次释放，非幂等的命令释放失败会
        // 裸抛丢失主异常（MultiValueBulkInsert 同款形态，BulkCleanupTests 锁定）
        DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = sql;
        // 命令释放失败附加到主异常（Data["PalORM.CommandCleanupException"]），不替换
        // 原始执行失败——与 MultiValueBulkInsert.ExecuteBatchesAsync 同一契约
        //（BulkCleanupTests 经 SqliteProvider 路由锁定本行为）
        Exception? commandException = null;
        try
        {
            if (valuesBinder is not null)
            {
                // 参数池路径（v4.6 契约）：columnCount 个参数一次建立挂命令，
                // Init 一次设 DbType，循环只写 Value——命中语句缓存后每行成本即绑定 + 步进
                DbParameter[] pool = new DbParameter[columnCount];
                for (int i = 0; i < columnCount; i++)
                {
                    DbParameter p = cmd.CreateParameter();
                    p.ParameterName = ParameterNameCache.GetName(i);
                    cmd.Parameters.Add(p);
                    pool[i] = p;
                }

                initInsertParameters?.Invoke(pool, 0);
                // PARAM-REUSE-OK[pool:pool] 池建后只写 Value（valuesBinder 契约），无集合变更
                foreach (T entity in entities)
                {
                    valuesBinder(pool, entity, 0);
                    total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            else
            {
                // 旧生成器程序集回退：每行整建参数（multi-exec 形态）——正确性优先，
                // 该形态仍命中语句缓存（命令文本恒定），只多付每行参数对象分配
                foreach (T entity in entities)
                {
                    // PARAM-REUSE-OK[legacy] 仅旧版生成器模型程序集可达（无 BindInsertValues），命令复用 + 每行整建参数
                    cmd.Parameters.Clear();
                    binder(cmd, entity, 0);
                    total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            commandException = exception;
            throw;
        }
        finally
        {
            await BulkOperationFramework.DisposePreservingAsync(cmd, commandException,
                "PalORM.CommandCleanupException").ConfigureAwait(false);
        }

        return total;
    }
}
