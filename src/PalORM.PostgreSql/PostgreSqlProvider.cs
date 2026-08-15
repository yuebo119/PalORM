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

    /// <summary>创建连接并把 <see cref="DbOptions"/> 池配置映射到 Npgsql 连接串:
    /// MaxPoolSize / ConnectionIdleLifetime(秒)/ ConnectionLifetime(分钟换算为秒,checked 防溢出)。
    /// <para><b>v5.0 阶段 3.1 调优</b>：对每个调优参数，如果用户连接串里的值等于该参数的
    /// ADO.NET 默认值（即用户未显式调优），则覆盖为推荐调优值：
    /// MaxAutoPrepare: 0→100（自动预编译，跨连接复用，查询延迟 -30~50%）；
    /// AutoPrepareMinUsages: 5→2（第 2 次执行起 Prepare）；
    /// NoResetOnClose: false→true（归还连接跳过 DISCARD ALL，+30% localhost 吞吐）；
    /// ReadBufferSize/WriteBufferSize: 默认(8192)→16384（大结果集/大值写入吞吐）；
    /// Enlist: true→false（跳过 TransactionScope 检查）。
    /// <see cref="SslNegotiation"/> 不在此默认追加——Direct 值要求同时 SslMode=Require+，
    /// 用户场景各异，应由用户按需显式设置。</para>
    /// <para><b>判断策略说明</b>：用"属性当前值 == ADO.NET 默认值"作为"用户未显式设置"的判据。
    /// 该判据在罕见场景（用户显式设置成默认值）下会把用户意图当作默认覆盖，但调优参数
    /// 主动设成低性能默认值的实际场景极少，收益（透明调优）大于风险。</para></summary>
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        // ITM-612：池参数遵循下方系列的"仅默认时覆盖"策略——原对象初始化器在连接串解析后
        // 无条件覆盖，连接串内嵌 "Max Pool Size=500" 被静默改写为 DbOptions 默认值。
        // Npgsql 10 实测默认值：MaxPoolSize=100 / ConnectionIdleLifetime=300 / ConnectionLifetime=3600
        //（注意 Lifetime 默认非 0——曾按 0 写判据致 WithPool 值永不应用，实证修正）。
        if (builder.MaxPoolSize == 100)
            builder.MaxPoolSize = options.MaxPoolSize;
        if (builder.ConnectionIdleLifetime == 300)
            builder.ConnectionIdleLifetime = options.PoolIdleTimeoutSeconds;
        if (builder.ConnectionLifetime == 3600)
            builder.ConnectionLifetime = checked(options.PoolLifetimeMinutes * 60);

        // v5.0 阶段 3.1：仅当属性当前值等于 ADO.NET 默认值时覆盖为调优推荐值。
        // Npgsql 默认值（已通过 ConnectionStringBuilder 属性默认核实）：MaxAutoPrepare=0，
        // AutoPrepareMinUsages=5，NoResetOnClose=false，ReadBufferSize/WriteBufferSize=8192，Enlist=true。
        if (builder.MaxAutoPrepare == 0)
            builder.MaxAutoPrepare = 100;
        if (builder.AutoPrepareMinUsages == 5)
            builder.AutoPrepareMinUsages = 2;
        if (!builder.NoResetOnClose)
            builder.NoResetOnClose = true;
        if (builder.ReadBufferSize == 8192)
            builder.ReadBufferSize = 16384;
        if (builder.WriteBufferSize == 8192)
            builder.WriteBufferSize = 16384;
        if (builder.Enlist)
            builder.Enlist = false;

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

    /// <summary>schema 与表名分别引用后以点连接;schema 为空时省略,落到 search_path 解析。
    /// 覆盖接口默认实现以支持 PostgreSQL 的 schema 语义。</summary>
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "PG Binary COPY 批量写入的 4 层 try/catch/finally 是异步 IO 资源管理的必然形态。"
            + "已抽出 ProbeBinderAsync + WriteRowAsync 减少方法体复杂度；余下嵌套是 importer/rowCommand/"
            + "transaction 三级 cleanup 的「主异常保留」模式（ITM-412 防漂移锚点）。")]
    public static async Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds, CancellationToken ct)
        where T : class, new()
    {
        // ITM-643：COPY 路径经 NpgsqlBinaryImporter 而非 DbCommand，无 CommandTimeout 挂点——
        // 用联动 CTS 按 commandTimeoutSeconds 取消每次 COPY，履行 IDbProvider 契约
        // "批量命令必须应用超时"（0 = 无限等待，不设超时）。取消与调用方 ct 可区分：
        // 超时触发的 OCE 挂回滚路径，调用方取消原样透传。
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // ITM-637 同型面（第三处，2026-08-15 复检发现）：元数据检查先于空列表短路——
        // 未注册类型与空/非空列表一致抛（原顺序下 SQLite/MySQL 抛、PG 静默 0，三方言分叉）
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName)
            || metadata.InsertColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated insert metadata.");
        if (entities.Count == 0) return 0;

        if (conn is not NpgsqlConnection npgsqlConnection)
            throw new ArgumentException(
                "PostgreSqlProvider.BulkInsertAsync requires an NpgsqlConnection.", nameof(conn));

        Action<DbCommand, object, int> binder = metadata.BindInsert;
        int columnCount = metadata.InsertColumns.Count;
        // v4.3：源生成器保证 binder 合法，probe 只需首次验证
        if (!metadata.InsertBinderValidated)
        {
            await BulkOperationFramework.ProbeBinderAsync(
                conn, binder, entities[0], columnCount, typeof(T).Name,
                "PalORM.ProbeCommandCleanupException", ct).ConfigureAwait(false);
        }

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
                // ITM-643：每次 COPY 一个独立超时窗口（对齐 ADO.NET 每命令超时语义，非整批累计）。
                CancellationTokenSource timeoutCts =
                    CreateCopyTimeoutTokenSource(commandTimeoutSeconds, ct);
                try
                {
                    CancellationToken commandCt = timeoutCts.Token;
                    NpgsqlBinaryImporter importer = await npgsqlConnection.BeginBinaryImportAsync(
                        $"COPY {quotedTable} ({quotedColumns}) FROM STDIN (FORMAT BINARY)", commandCt)
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
                                binder(rowCommand, entities[index], 0);
                                if (rowCommand.Parameters.Count != columnCount)
                                    throw new InvalidOperationException(
                                        $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                                        $"{rowCommand.Parameters.Count} parameters.");

                                await WriteRowAsync(importer, rowCommand, columnCount, commandCt).ConfigureAwait(false);
                                total++;
                            }
                            await importer.CompleteAsync(commandCt).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            rowCommandException = exception;
                            throw;
                        }
                        finally
                        {
                            await BulkOperationFramework.DisposePreservingAsync(rowCommand, rowCommandException,
                                "PalORM.RowCommandCleanupException", ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception)
                    {
                        importerException = exception;
                        throw;
                    }
                    finally
                    {
                        await BulkOperationFramework.DisposePreservingAsync(importer, importerException,
                            "PalORM.ImporterCleanupException", ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    timeoutCts.Dispose();
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
                await BulkOperationFramework.DisposePreservingAsync(bulkTransaction, primaryException,
                    "PalORM.TransactionCleanupException", ct).ConfigureAwait(false);
        }
    }

    /// <summary>创建单次 COPY 的超时令牌源——ITM-643：COPY 无 CommandTimeout 挂点，
    /// 联动 CTS + CancelAfter 履行"批量命令必须应用超时"契约；0 = 无限等待（不设取消），
    /// 与 DbOptions.ToCommandTimeoutSeconds 的 Zero 透传语义一致。</summary>
    private static CancellationTokenSource CreateCopyTimeoutTokenSource(
        int commandTimeoutSeconds, CancellationToken ct)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (commandTimeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(commandTimeoutSeconds));
        return timeoutCts;
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

    // ITM-412 防漂移锚点：以下清理助手与 Core 的 DataSession.RollbackPreservingAsync 是同一
    // "主异常保留"骨架的复制体（Provider 不得反向依赖 Core 内部实现，故刻意复制）。
    // 修改任一侧语义（异常挂载键、CancellationToken.None）时必须同步核对另一侧——两侧分叉即 ITM-304 同型温床。
    // 注：DisposePreservingAsync 已抽到 BulkOperationFramework（v3.0），三 Provider 共享同一实现。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "回滚是清理路径；异常附加到主异常，不能替换原始 COPY 失败。")]
    private static async ValueTask RollbackPreservingAsync(DbTransaction transaction, Exception primaryException)
    {
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception rollbackException) { primaryException.Data["PalORM.RollbackException"] = rollbackException; }
    }
}
