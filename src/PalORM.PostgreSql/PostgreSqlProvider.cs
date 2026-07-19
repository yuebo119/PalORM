using System.Data.Common;
using Npgsql;

namespace PalORM.PostgreSql;

/// <summary>PostgreSQL Provider —— Npgsql 适配 + JSONB/NOTIFY/Binary COPY。</summary>
public sealed class PostgreSqlProvider : IDbProvider
{
    /// <summary>Provider 名称:PostgreSql。</summary>
    public static string Name => "PostgreSql";

    /// <summary>SQL 方言标识:<see cref="SqlDialect.PostgreSql"/>。</summary>
    public static SqlDialect Dialect => SqlDialect.PostgreSql;

    /// <summary>创建 NpgsqlConnection,连接池配置沿用连接串默认值。</summary>
    public static DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    /// <summary>创建连接并把 <see cref="DbOptions"/> 池配置映射到 Npgsql 连接串:
    /// MaxPoolSize / ConnectionIdleLifetime(秒)/ ConnectionLifetime(分钟换算为秒,checked 防溢出)。</summary>
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = options.MaxPoolSize,
            ConnectionIdleLifetime = options.PoolIdleTimeoutSeconds,
            ConnectionLifetime = checked(options.PoolLifetimeMinutes * 60)
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    /// <summary>双引号引用标识符(PG 标准),内部双引号以 "" 转义;引用后保留大小写敏感。</summary>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        // ITM-584/593: 三方言共享 IdentifierSafety 守卫（C0 控制字符族 + DEL）。
        IdentifierSafety.ThrowIfUnsafe(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>schema 与表名分别引用后以点连接;schema 为空时省略,落到 search_path 解析。</summary>
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    /// <summary>PostgreSQL 原生支持 RETURNING 子句。</summary>
    public static bool SupportsReturningClause => true;

    /// <summary>CURRENT_TIMESTAMP——注意 PG 返回会话时区时间(与 SQLite 的恒 UTC 语义不同,ITM-326)。</summary>
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    /// <summary>SQLSTATE 23505 unique_violation——唯一约束冲突。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is PostgresException { SqlState: "23505" };

    /// <summary>用 information_schema.columns 查询列名(参数化,schema 为空时回退 current_schema()),列名位于结果集序号 0。</summary>
    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_name = @table_name AND table_schema = COALESCE(@table_schema, current_schema())";
        command.Parameters.Add(CreateParameter("@table_name", tableName));
        command.Parameters.Add(CreateParameter("@table_schema", schema));
        return 0;
    }

    /// <summary>参数占位符,形如 @p0(Npgsql 支持命名参数)。</summary>
    public static string GetParameterPlaceholder(int index) => $"@p{index}";

    /// <summary>创建 NpgsqlParameter;value 为 null 时转为 <see cref="DBNull.Value"/>(ADO.NET 中 null 参数值不会被发送)。</summary>
    public static DbParameter CreateParameter(string name, object? value)
        => new NpgsqlParameter(name, value ?? DBNull.Value);

    /// <summary>批量插入——按源生成 InsertColumns 与 BindInsert 执行 Npgsql Binary COPY。
    /// <para>BeginBinaryImportAsync → StartRowAsync → WriteAsync(value, NpgsqlDbType) → CompleteAsync。</para>
    /// <para>列数与参数数在开始 COPY 前校验，无需运行时类型映射。</para>
    /// <para>命令、Importer、回滚或事务释放失败附加到主异常，不替换原始 COPY 失败。</para>
    /// <para><b>ITM-527 已知限制（待 CI 真库矩阵验证）</b>：本路径复用 BindInsert 产生的
    /// NpgsqlParameter.NpgsqlDbType 作为 COPY 写入类型，依赖 Npgsql 从 CLR 值推断类型。
    /// 两种边界场景可能失败：(1) 整批某可空列全为 null 时，Npgsql 无值可推断类型，
    /// COPY 二进制协议要求显式类型，可能抛类型未知异常——规避方法是该列至少一行给非 null 值，
    /// 或改用逐行 INSERT 路径；(2) DateTime 列的 Kind 为 Local/Unspecified 时，
    /// timestamptz 列写入行为依赖服务器时区，建议实体侧统一用 DateTimeKind.Utc。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062", Justification = "conn 在外层已有验证")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "表名/列名来自源生成器")]
    public static async Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds, CancellationToken ct)
        where T : class, new()
    {
        // ITM-557 注记：COPY 路径经 NpgsqlBinaryImporter 而非 DbCommand，无 CommandTimeout 挂点；
        // 写入超时由 Npgsql 连接串 CommandTimeout/取消令牌治理。参数保留以维持接口对称。
        _ = commandTimeoutSeconds;
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (entities.Count == 0) return 0;
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName)
            || metadata.InsertColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated insert metadata.");

        if (conn is not NpgsqlConnection npgsqlConnection)
            throw new ArgumentException(
                "PostgreSqlProvider.BulkInsertAsync requires an NpgsqlConnection.", nameof(conn));

        Action<DbCommand, object> binder = metadata.BindInsert;
        int columnCount = metadata.InsertColumns.Count;
        await ProbeBinderAsync(conn, binder, entities[0], columnCount, typeof(T).Name).ConfigureAwait(false);

        string quotedColumns = string.Join(", ",
            metadata.InsertColumns.Select(QuoteIdentifier));
        string quotedTable = QuoteIdentifier(tableName);
        long total = 0;
        DbTransaction bulkTransaction = transaction
            ?? await npgsqlConnection.BeginTransactionAsync(ct).ConfigureAwait(false);
        bool ownsTransaction = transaction is null;
        Exception? primaryException = null;

        try
        {
            for (int start = 0; start < entities.Count; start += batchSize)
            {
                int end = Math.Min(start + batchSize, entities.Count);
                NpgsqlBinaryImporter importer = await npgsqlConnection.BeginBinaryImportAsync(
                    $"COPY {quotedTable} ({quotedColumns}) FROM STDIN (FORMAT BINARY)", ct)
                    .ConfigureAwait(false);
                Exception? importerException = null;
                try
                {
                    DbCommand rowCommand = conn.CreateCommand();
                    Exception? rowCommandException = null;
                    try
                    {
                        for (int index = start; index < end; index++)
                        {
                            rowCommand.Parameters.Clear();
                            binder(rowCommand, entities[index]);
                            if (rowCommand.Parameters.Count != columnCount)
                                throw new InvalidOperationException(
                                    $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                                    $"{rowCommand.Parameters.Count} parameters.");

                            await WriteRowAsync(importer, rowCommand, columnCount, ct).ConfigureAwait(false);
                            total++;
                        }
                        await importer.CompleteAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        rowCommandException = exception;
                        throw;
                    }
                    finally
                    {
                        await DisposePreservingAsync(rowCommand, rowCommandException,
                            "PalORM.RowCommandCleanupException").ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    importerException = exception;
                    throw;
                }
                finally
                {
                    await DisposePreservingAsync(importer, importerException,
                        "PalORM.ImporterCleanupException").ConfigureAwait(false);
                }
            }

            if (ownsTransaction)
                await bulkTransaction.CommitAsync(ct).ConfigureAwait(false);
            return total;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (ownsTransaction)
                await RollbackPreservingAsync(bulkTransaction, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (ownsTransaction)
                await DisposePreservingAsync(bulkTransaction, primaryException,
                    "PalORM.TransactionCleanupException").ConfigureAwait(false);
        }
    }

    /// <summary>探测 binder 生成的参数数量与列数一致——不一致即抛 InvalidOperationException。
    /// 探测命令独立释放，cleanup 异常挂 Data 不替换原始失败（ITM-412 与 MultiValueBulkInsert 同骨架）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "释放是清理路径；异常附加到主异常，不能替换原始批量写失败。")]
    private static async ValueTask ProbeBinderAsync(
        DbConnection conn, Action<DbCommand, object> binder, object first,
        int columnCount, string typeName)
    {
        DbCommand probeCommand = conn.CreateCommand();
        Exception? probeException = null;
        try
        {
            binder(probeCommand, first);
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
            await DisposePreservingAsync(probeCommand, probeException,
                "PalORM.ProbeCommandCleanupException").ConfigureAwait(false);
        }
    }

    /// <summary>把单行参数写入 PG Binary importer——DBNull 转换为 null 让 importer 用列默认类型。</summary>
    private static async ValueTask WriteRowAsync(
        NpgsqlBinaryImporter importer, DbCommand rowCommand, int columnCount, CancellationToken ct)
    {
        await importer.StartRowAsync(ct).ConfigureAwait(false);
        for (int parameterIndex = 0; parameterIndex < columnCount; parameterIndex++)
        {
            var parameter = (NpgsqlParameter)rowCommand.Parameters[parameterIndex];
            object? value = parameter.Value is DBNull ? null : parameter.Value;
            await importer.WriteAsync(value, parameter.NpgsqlDbType, ct).ConfigureAwait(false);
        }
    }

    // ITM-412 防漂移锚点：以下两个清理助手与 Core 的 DataSession.RollbackPreservingAsync /
    // DisposeTransactionPreservingAsync 是同一"主异常保留"骨架的复制体（Provider 不得反向
    // 依赖 Core 内部实现，故刻意复制）。修改任一侧语义（异常挂载键、CancellationToken.None、
    // when 过滤条件）时必须同步核对另一侧——两侧分叉即 ITM-304 同型温床。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "回滚是清理路径；异常附加到主异常，不能替换原始 COPY 失败。")]
    private static async ValueTask RollbackPreservingAsync(DbTransaction transaction, Exception primaryException)
    {
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception rollbackException) { primaryException.Data["PalORM.RollbackException"] = rollbackException; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "释放是清理路径；异常附加到主异常，不能替换原始 COPY 失败。")]
    private static async ValueTask DisposePreservingAsync(
        IAsyncDisposable resource,
        Exception? primaryException,
        string exceptionDataKey)
    {
        try { await resource.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data[exceptionDataKey] = cleanupException;
        }
    }
}
