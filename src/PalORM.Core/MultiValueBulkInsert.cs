using System.Data.Common;

namespace PalORM;

/// <summary>多值 INSERT 批量骨架——SQLite/MySQL Provider 共享（ITM-304：两份逐字复制已发生
/// 参数钳制漂移，收敛为单一实现）。PG 的 Binary COPY 是真实方言差异，不并入此骨架。
/// <para>职责：binder 参数数 probe 校验 → 按单语句参数上限钳制批次 →
/// 多值 INSERT 分批执行 → 事务生命周期与异常保留清理。</para>
/// <para>命令、回滚或事务释放失败附加到主异常，不替换原始执行失败。</para></summary>
public static class MultiValueBulkInsert
{
    /// <summary>多值 INSERT 分批写入实体列表，返回受影响总行数。批次大小取
    /// <paramref name="ctx"/>.<see cref="BulkContext.BatchSize"/> 与参数上限
    /// <see cref="BulkContext.MaxParametersPerStatement"/>/列数的较小者；
    /// <paramref name="transaction"/> 为 null 时自建事务并在全部批次成功后提交，
    /// 传入外部事务时提交/回滚由调用方负责。
    /// <see cref="BulkContext.CommandTimeoutSeconds"/> 应用到每个批量命令（ITM-557）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "表名/列名来自源生成器")]
    public static async Task<long> ExecuteAsync<T>(
        DbConnection conn,
        DbTransaction? transaction,
        IReadOnlyList<T> entities,
        BulkContext ctx,
        CancellationToken ct)
        where T : class, new()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ctx.BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ctx.MaxParametersPerStatement);
        ArgumentOutOfRangeException.ThrowIfNegative(ctx.CommandTimeoutSeconds);
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(ctx.QuoteIdentifier);
        ArgumentNullException.ThrowIfNull(ctx.CreateParameter);
        int batchSize = ctx.BatchSize;
        int maxParametersPerStatement = ctx.MaxParametersPerStatement;
        Func<string, string> quoteIdentifier = ctx.QuoteIdentifier;
        Func<string, object?, DbParameter> createParameter = ctx.CreateParameter;
        int commandTimeoutSeconds = ctx.CommandTimeoutSeconds;
        if (entities.Count == 0) return 0;
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName)
            || metadata.InsertColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated insert metadata.");

        Action<DbCommand, object> binder = metadata.BindInsert;
        int columnCount = metadata.InsertColumns.Count;
        await BulkOperationFramework.ProbeBinderAsync(
            conn, binder, entities[0], columnCount, typeof(T).Name,
            "PalORM.ProbeCommandCleanupException").ConfigureAwait(false);

        if (columnCount > maxParametersPerStatement)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has {columnCount} insert columns, exceeding the " +
                $"{maxParametersPerStatement}-parameter statement limit.");

        int effectiveBatchSize = Math.Min(batchSize, maxParametersPerStatement / columnCount);
        string quotedTable = quoteIdentifier(tableName);
        string quotedColumns = string.Join(", ",
            metadata.InsertColumns.Select(quoteIdentifier));
        long total = 0;

        DbTransaction tran = transaction
            ?? await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        bool ownsTransaction = transaction is null;
        Exception? primaryException = null;
        try
        {
            // 行参数暂存命令在批间复用（原实现每行新建一个 DbCommand，1 万行即 1 万次分配）
            DbCommand rowCommand = conn.CreateCommand();
            Exception? rowCommandException = null;
            try
            {
                total = await ExecuteBatchesAsync(
                    conn, tran, rowCommand, entities, effectiveBatchSize, columnCount,
                    quotedTable, quotedColumns, binder, createParameter, commandTimeoutSeconds,
                    typeof(T).Name, ct).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                rowCommandException = exception;
                throw;
            }
            finally
            {
                await BulkOperationFramework.DisposePreservingAsync(rowCommand, rowCommandException,
                    "PalORM.RowCommandCleanupException").ConfigureAwait(false);
            }
            if (ownsTransaction)
                await tran.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await TransactionCleanup.RollbackPreservingAsync(tran, exception).ConfigureAwait(false);
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

    /// <summary>构建每行 (?,?,?,?,?) 占位符组——批内每行一组，逗号分隔。</summary>
    private static string[] BuildRowPlaceholders(int batchLength, int columnCount)
    {
        var rowPlaceholders = new string[batchLength];
        for (int row = 0; row < batchLength; row++)
        {
            int parameterOffset = row * columnCount;
            rowPlaceholders[row] = "(" + string.Join(", ",
                Enumerable.Range(0, columnCount)
                    .Select(column => $"@p{parameterOffset + column}")) + ")";
        }
        return rowPlaceholders;
    }

    /// <summary>分批执行 INSERT——批大小受 effectiveBatchSize 与参数上限钳制。
    /// 行参数暂存命令在批间复用（原实现每行新建一个 DbCommand，1 万行即 1 万次分配）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S107:MethodsShouldNotHaveTooManyParameters",
        Justification = "批量执行参数多但全是必要——连接/事务/命令/实体集合/binder/quoter/cancellationToken "
            + "都是 ADO.NET 批量骨架的必然组件，聚合成对象会引入跨方法状态传递。已抽出 ProbeBinderAsync "
            + "+ BuildRowPlaceholders 减少方法主体复杂度。")]
    private static async Task<long> ExecuteBatchesAsync<T>(
        DbConnection conn, DbTransaction tran, DbCommand rowCommand,
        IReadOnlyList<T> entities, int effectiveBatchSize, int columnCount,
        string quotedTable, string quotedColumns,
        Action<DbCommand, object> binder,
        Func<string, object?, DbParameter> createParameter,
        int commandTimeoutSeconds, string typeName, CancellationToken ct) where T : class, new()
    {
        long total = 0;
        for (int start = 0; start < entities.Count; start += effectiveBatchSize)
        {
            int end = Math.Min(start + effectiveBatchSize, entities.Count);
            int batchLength = end - start;
            string[] rowPlaceholders = BuildRowPlaceholders(batchLength, columnCount);

            DbCommand cmd = conn.CreateCommand();
            Exception? commandException = null;
            try
            {
                cmd.Transaction = tran;
                cmd.CommandTimeout = commandTimeoutSeconds;
                cmd.CommandText =
                    $"INSERT INTO {quotedTable} ({quotedColumns}) VALUES " +
                    string.Join(", ", rowPlaceholders);

                for (int row = 0; row < batchLength; row++)
                {
                    int parameterOffset = row * columnCount;
                    rowCommand.Parameters.Clear();
                    binder(rowCommand, entities[start + row]);
                    if (rowCommand.Parameters.Count != columnCount)
                        throw new InvalidOperationException(
                            $"Type '{typeName}' generated {columnCount} insert columns but " +
                            $"{rowCommand.Parameters.Count} parameters.");

                    for (int column = 0; column < columnCount; column++)
                    {
                        DbParameter source = rowCommand.Parameters[column];
                        cmd.Parameters.Add(createParameter(
                            $"@p{parameterOffset + column}", source.Value));
                    }
                }

                total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        }
        return total;
    }
}

/// <summary>多值 INSERT 批量骨架的 provider 能力 + 批次配置聚合——消除 9 参参数列表（S107）。
/// 每次调用 new 一个；批量内部多次复用。</summary>
public readonly record struct BulkContext(
    int BatchSize,
    int MaxParametersPerStatement,
    Func<string, string> QuoteIdentifier,
    Func<string, object?, DbParameter> CreateParameter,
    int CommandTimeoutSeconds);
