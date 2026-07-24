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
        int commandTimeoutSeconds = ctx.CommandTimeoutSeconds;
        if (entities.Count == 0) return 0;
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName)
            || metadata.InsertColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated insert metadata.");

        // v4.1：BindInsertToBatch 直绑--binder 支持 paramOffset，消除 rowCommand scratch
        Action<DbCommand, object, int> binder = metadata.BindInsert;
        // v4.6：参数复用路径 -- 仅改 Value 不 CreateParameter（满批跨批复用）
        Action<DbParameter[], object, int>? valuesBinder = metadata.BindInsertValues;
        int columnCount = metadata.InsertColumns.Count;
        // v4.3：源生成器保证 binder 参数数 == 列数，probe 只需首次验证（InsertBinderValidated=true 时跳过）
        if (!metadata.InsertBinderValidated)
        {
            await BulkOperationFramework.ProbeBinderAsync(
                conn, binder, entities[0], columnCount, typeof(T).Name,
                "PalORM.ProbeCommandCleanupException", ct).ConfigureAwait(false);
        }

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
            // v4.1：BindInsertToBatch 直接写入 batchCmd，不再需要 rowCommand scratch
            total = await ExecuteBatchesAsync(
                conn, tran, entities, effectiveBatchSize, columnCount,
                quotedTable, quotedColumns, binder, valuesBinder, commandTimeoutSeconds,
                typeof(T).Name, ct).ConfigureAwait(false);
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

    /// <summary>构建每行 (?,?,?,?,?) 占位符组——批内每行一组，逗号分隔。
    /// v4.0 性能优化：改用 ValueStringBuilder + stackalloc 消除 LINQ + string.Join 分配。
    /// 10000 行批量场景下，每批 BuildRowPlaceholders 省一次数组 + 多次字符串分配。</summary>
    private static string[] BuildRowPlaceholders(int batchLength, int columnCount)
    {
        var rowPlaceholders = new string[batchLength];
        int singleRowMaxLen = columnCount * 10 + 4;
        Span<char> buffer = singleRowMaxLen <= 512
            ? stackalloc char[singleRowMaxLen]
            : new char[singleRowMaxLen];
        for (int row = 0; row < batchLength; row++)
        {
            int parameterOffset = row * columnCount;
            int written = FormatRowPlaceholder(buffer, parameterOffset, columnCount);
            rowPlaceholders[row] = new string(buffer[..written]);
        }
        return rowPlaceholders;
    }

    /// <summary>把单行占位符 "( @p0, @p1, ... )" 写入 buffer，返回写入字符数。</summary>
    private static int FormatRowPlaceholder(Span<char> buffer, int parameterOffset, int columnCount)
    {
        int written = 0;
        buffer[written++] = '(';
        for (int column = 0; column < columnCount; column++)
        {
            if (column > 0)
            {
                buffer[written++] = ',';
                buffer[written++] = ' ';
            }
            buffer[written++] = '@';
            buffer[written++] = 'p';
            written += WriteIndexDigits(buffer[written..], parameterOffset + column);
        }
        buffer[written++] = ')';
        return written;
    }

    /// <summary>把非负整数写入 span（不含前导零），返回写入字符数。</summary>
    private static int WriteIndexDigits(Span<char> buffer, int value)
    {
        if (value == 0)
        {
            buffer[0] = '0';
            return 1;
        }
        Span<char> digits = stackalloc char[5];
        int digitCount = 0;
        while (value > 0)
        {
            digits[digitCount++] = (char)('0' + value % 10);
            value /= 10;
        }
        // digits 是反向存的（个位在前），写入 buffer 时倒序还原
        for (int i = 0; i < digitCount; i++)
            buffer[i] = digits[digitCount - 1 - i];
        return digitCount;
    }

    /// <summary>分批执行 INSERT——批大小受 effectiveBatchSize 与参数上限钳制。
    /// 行参数暂存命令在批间复用（原实现每行新建一个 DbCommand，1 万行即 1 万次分配）。
    /// v4.0 性能优化：DbCommand 跨批次复用（Parameters.Clear + CommandText 更新），
    /// 避免 N 批次 × 1 次 CreateCommand 分配。占位符预构建改用 ValueStringBuilder。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S107:MethodsShouldNotHaveTooManyParameters",
        Justification = "批量执行参数多但全是必要--连接/事务/命令/实体集合/binder/quoter/cancellationToken "
            + "都是 ADO.NET 批量骨架的必然组件，聚合成对象会引入跨方法状态传递。已抽出 ProbeBinderAsync "
            + "+ BuildRowPlaceholders 减少方法主体复杂度。")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "v4.6 参数复用增加满批/末批分支，逻辑紧凑不宜拆分。")]
    private static async Task<long> ExecuteBatchesAsync<T>(
        DbConnection conn, DbTransaction tran,
        IReadOnlyList<T> entities, int effectiveBatchSize, int columnCount,
        string quotedTable, string quotedColumns,
        Action<DbCommand, object, int> binder,
        Action<DbParameter[], object, int>? valuesBinder,
        int commandTimeoutSeconds, string typeName, CancellationToken ct) where T : class, new()
    {
        long total = 0;
        DbCommand batchCmd = conn.CreateCommand();
        string? lastBatchSql = null;
        int lastBatchLength = -1;
        // v4.6：满批参数池 -- 首次分配后跨批复用，只改 Value 不 CreateParameter
        DbParameter[]? paramPool = null;
        bool poolAdded = false; // 首次 Add 后设 true，后续批次不清不重 Add（只改 Value）
        Exception? batchCommandException = null;
        try
        {
            batchCmd.Transaction = tran;
            batchCmd.CommandTimeout = commandTimeoutSeconds;

            for (int start = 0; start < entities.Count; start += effectiveBatchSize)
            {
                int end = Math.Min(start + effectiveBatchSize, entities.Count);
                int batchLength = end - start;
                bool isFullBatch = batchLength == effectiveBatchSize && valuesBinder is not null;

                // CommandText 仅在批大小变化时重建（首批 + 末尾不满批时）
                if (batchLength != lastBatchLength)
                {
                    string[] rowPlaceholders = BuildRowPlaceholders(batchLength, columnCount);
                    lastBatchSql =
                        $"INSERT INTO {quotedTable} ({quotedColumns}) VALUES " +
                        string.Join(", ", rowPlaceholders);
                    lastBatchLength = batchLength;
                }
                batchCmd.CommandText = lastBatchSql;

                if (isFullBatch)
                {
                    // v4.6：满批参数复用路径
                    if (paramPool is null)
                    {
                        // 首次：预分配 + Add 到 batchCmd
                        int poolSize = effectiveBatchSize * columnCount;
                        paramPool = new DbParameter[poolSize];
                        for (int i = 0; i < poolSize; i++)
                        {
                            var p = batchCmd.CreateParameter();
                            p.ParameterName = ParameterNameCache.GetName(i);
                            batchCmd.Parameters.Add(p);
                            paramPool[i] = p;
                        }
                        poolAdded = true;
                    }
                    else if (!poolAdded)
                    {
                        // 末批后回到满批：重新 Add 参数
                        batchCmd.Parameters.Clear();
                        for (int i = 0; i < paramPool.Length; i++)
                            batchCmd.Parameters.Add(paramPool[i]);
                        poolAdded = true;
                    }
                    // valuesBinder 只改 Value，不 Clear/Add
                    for (int row = 0; row < batchLength; row++)
                        valuesBinder!(paramPool, entities[start + row], row * columnCount);
                }
                else
                {
                    // 末批或无 valuesBinder：走老路径
                    batchCmd.Parameters.Clear();
                    poolAdded = false;
                    for (int row = 0; row < batchLength; row++)
                    {
                        int parameterOffset = row * columnCount;
                        binder(batchCmd, entities[start + row], parameterOffset);
                    }
                }

                // 列数校验
                if (batchCmd.Parameters.Count != batchLength * columnCount)
                    throw new InvalidOperationException(
                        $"Type '{typeName}' generated {columnCount} insert columns but " +
                        $"{batchCmd.Parameters.Count} parameters after binding {batchLength} rows.");

                total += await batchCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            batchCommandException = exception;
            throw;
        }
        finally
        {
            await BulkOperationFramework.DisposePreservingAsync(batchCmd, batchCommandException,
                "PalORM.CommandCleanupException", ct).ConfigureAwait(false);
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
