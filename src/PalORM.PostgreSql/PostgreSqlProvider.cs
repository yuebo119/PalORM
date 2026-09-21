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
    /// 主动设成低性能默认值的实际场景极少，收益（透明调优）大于风险。
    /// <b>布尔旋钮边界（审计 PROV-011）</b>：对 NoResetOnClose/Enlist 这类布尔参数，被覆盖的
    /// 后果不是性能而是正确性（会话状态泄漏 ITM-652 / 环境事务脱离 ITM-643），上述
    /// "收益大于风险"论证不适用于它们——需要这些语义的用户必须绕开本工厂自建连接。</para></summary>
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
        // C4（v5.7）：空闲保留下限——0 = 不覆盖（Npgsql 默认 0）。>0 时空闲修剪
        // （ConnectionIdleLifetime 到期）至少保留这么多条连接，避免稀疏流量清池后
        // 突发查询重建物理连接（远程建连实测 ~8.5 ms/条）。
        if (options.MinPoolSize > 0 && builder.MinPoolSize == 0)
            builder.MinPoolSize = options.MinPoolSize;
        // v5.6：0 = 不覆盖（保留 Npgsql 默认 300 秒）——原默认 30 秒把驱动的空闲超时砍到 1/10，
        // 间隔超过 30 秒的首次查询须重建物理连接（跨网段实测多付 13.2 ms）。
        if (options.PoolIdleTimeoutSeconds > 0 && builder.ConnectionIdleLifetime == 300)
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
        // ITM-652(r4) 登记：NoResetOnClose=true 使归池连接跳过 DISCARD ALL——经 raw SQL
        // 的 SET/临时表/会话状态会泄漏给下一个池租客（吞吐收益的既定取舍）。需要会话
        // 状态隔离的场景请用独立连接（连接串 Max Pool Size=1）或显式 DISCARD。
        if (!builder.NoResetOnClose)
            builder.NoResetOnClose = true;
        if (builder.ReadBufferSize == 8192)
            builder.ReadBufferSize = 16384;
        if (builder.WriteBufferSize == 8192)
            builder.WriteBufferSize = 16384;
        // ITM-643(r4) 登记：显式 Enlist=true 与默认 true 不可区分（同 ADR-G 的
        // AllowLoadLocalInfile 形态）——依赖 TransactionScope 的用户被静默脱离环境事务。
        // 缓解：环境事务场景请用 BeginTransaction 显式事务（PalORM 主路径）。
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
        // L3：无内嵌引号时 string.Replace 仍返回新实例——标识符引用在 SQL 构造路径上
        // 被反复调用（MultiValueBulkInsert/BatchUpdateSqlBuilder 的列名清单即由它产出），
        // 无引号场景走 Concat 免掉一次纯拷贝。
        return identifier.Contains('"', StringComparison.Ordinal)
            ? $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : string.Concat("\"", identifier, "\"");
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

    /// <summary>创建 NpgsqlParameter;value 为 null 时转为 <see cref="DBNull.Value"/>(ADO.NET 中 null 参数值不会被发送)。
    /// <para><b>R5（2026-09-21）：显式 DbType</b>——<c>new NpgsqlParameter(name, object)</c> 对装箱的
    /// 值类型<b>不做类型映射</b>，NpgsqlDbType 落在 Unknown，服务端按 text 推断，于是
    /// <c>WHERE "Id" = @p0</c>（@p0 是装箱 long）报 "operator does not exist: text = bigint"。
    /// 该路径覆盖所有手写 SQL 的 Where 条件与原始 SQL 入口，属跨方言通用缺陷
    /// （MySQL/SQLite 容忍弱类型故未暴露）。</para></summary>
    public static DbParameter CreateParameter(string name, object? value)
    {
        var parameter = new NpgsqlParameter(name, value ?? DBNull.Value);
        // 仅对已知基元显式映射——未知类型留给驱动推断（保持既有行为）
        switch (value)
        {
            case bool: parameter.DbType = System.Data.DbType.Boolean; break;
            case byte: parameter.DbType = System.Data.DbType.Byte; break;
            case sbyte: parameter.DbType = System.Data.DbType.SByte; break;
            case short: parameter.DbType = System.Data.DbType.Int16; break;
            case ushort: parameter.DbType = System.Data.DbType.UInt16; break;
            case int: parameter.DbType = System.Data.DbType.Int32; break;
            case uint: parameter.DbType = System.Data.DbType.UInt32; break;
            case long: parameter.DbType = System.Data.DbType.Int64; break;
            case ulong: parameter.DbType = System.Data.DbType.UInt64; break;
            case float: parameter.DbType = System.Data.DbType.Single; break;
            case double: parameter.DbType = System.Data.DbType.Double; break;
            case decimal: parameter.DbType = System.Data.DbType.Decimal; break;
            case string: parameter.DbType = System.Data.DbType.String; break;
            case char: parameter.DbType = System.Data.DbType.String; break;
            case DateTime: parameter.DbType = System.Data.DbType.DateTime; break;
            case DateTimeOffset: parameter.DbType = System.Data.DbType.DateTimeOffset; break;
            case Guid: parameter.DbType = System.Data.DbType.Guid; break;
            default: break;  // 未知类型留给驱动推断（保持既有行为）
        }
        return parameter;
    }

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
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds, CancellationToken ct,
        System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted)
        where T : class, new()
    {
        // ITM-643：COPY 路径经 NpgsqlBinaryImporter 而非 DbCommand，无 CommandTimeout 挂点——
        // 用联动 CTS 按 commandTimeoutSeconds 取消每次 COPY，履行 IDbProvider 契约
        // "批量命令必须应用超时"（0 = 无限等待，不设超时）。取消与调用方 ct 可区分：
        // 超时触发的 OCE 挂回滚路径，调用方取消原样透传。
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // ITM-637 同型面：元数据检查先于空列表短路——未注册类型与空/非空列表一致抛
        //（PROV-010：守卫收敛至 BulkOperationFramework.EnsureInsertMetadata 单一实现点）
        // tableName 弃元：B3 起 COPY 目标的引用形态由 GetQuotedInsertTarget 缓存提供
        (CrudMetadata metadata, _) = BulkOperationFramework.EnsureInsertMetadata(typeof(T));
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

        // B3：引号后的表名与列清单只由 (Type, Dialect) 决定，是纯函数——原每次调用重算
        // （方法组转委托 + LINQ 迭代器 + string.Join 中间数组 + 每列一次 QuoteIdentifier）。
        (string quotedTable, string quotedColumns) = GetQuotedInsertTarget(typeof(T));
        long total = 0;
        DbTransaction bulkTransaction = transaction
            ?? await npgsqlConnection.BeginTransactionAsync(isolationLevel, ct).ConfigureAwait(false);  // r6-N2
        bool ownsTransaction = transaction is null;
        Exception? primaryException = null;
        // ITM-760(r21)：超时判定线索提升到方法级——catch 原本拿不到循环内的 timeoutCts，
        // 只能用 "!ct.IsCancellationRequested && timeout>0" 近似，驱动自抛 OCE 会被误标
        // InfrastructureTimeout。现以方法级标志记录"本批超时 CTS 确已触发"。
        bool[] timeoutFlag = [false];  // ITM-760：数组包装供 Register 回调写

        try
        {
            // M2：rowCommand 与参数池提升到批循环外——COPY 路径下 rowCommand 只是参数容器
            // （从不执行），原实现每批 CreateCommand + 每批分配异步释放，千批即千次 DbCommand
            // 与 NpgsqlParameterCollection 分配（与 MultiValueBulkInsert 的跨批复用同范式）。
            DbCommand rowCommand = conn.CreateCommand();
            Exception? rowCommandException = null;
            try
            {
                // v5.6 参数复用：参数对象整批只建一次，逐行只写 Value。
                // 原实现每行 Parameters.Clear() + binder 重建 columnCount 个参数
                // （1 万行 × 4 列 = 4 万个 NpgsqlParameter），是 COPY 路径每行分配的
                // 主项——实测 882 B/行，而已在用参数池的 MySQL 多值 INSERT 只要
                // 361 B/行。绑定改用生成器已产出的 BindInsertValues（只写 Value、
                // 零创建），与 MultiValueBulkInsert 的 v4.6 池同一机制，无需改生成器。
                // 旧版生成器模型程序集 BindInsertValues 为 null 时回退逐行 binder。
                Action<DbParameter[], object, int>? valuesBinder = metadata.BindInsertValues;
                // 池的引用数组：参数对象仍留在 rowCommand.Parameters 内——
                // WriteRowAsync 读 parameter.NpgsqlDbType，脱离集合会丢失类型推断。
                DbParameter[]? pool = null;
                for (int start = 0; start < entities.Count; start += batchSize)
                {
                    int end = Math.Min(start + batchSize, entities.Count);
                    // ITM-643：每次 COPY 一个独立超时窗口（对齐 ADO.NET 每命令超时语义，非整批累计）。
                    CancellationTokenSource timeoutCts =
                        CreateCopyTimeoutTokenSource(commandTimeoutSeconds, ct);
                    try
                    {
                        CancellationToken commandCt = timeoutCts.Token;
                        // ITM-760：注册回调记录超时触发（回调先于 OCE 抛出点的传播）
                        using CancellationTokenRegistration reg = commandCt.Register(
                            static state => ((bool[])state!)[0] = true, timeoutFlag);
                        NpgsqlBinaryImporter importer = await npgsqlConnection.BeginBinaryImportAsync(
                            $"COPY {quotedTable} ({quotedColumns}) FROM STDIN (FORMAT BINARY)", commandCt)
                            .ConfigureAwait(false);
                        Exception? importerException = null;
                        try
                        {
                            for (int index = start; index < end; index++)
                            {
                                if (valuesBinder is not null && pool is not null)
                                {
                                    valuesBinder(pool, entities[index], 0);
                                }
                                else
                                {
                                    rowCommand.Parameters.Clear();
                                    binder(rowCommand, entities[index], 0);
                                    if (rowCommand.Parameters.Count != columnCount)
                                        throw new InvalidOperationException(
                                            $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                                            $"{rowCommand.Parameters.Count} parameters.");
                                    // 首行绑定成功后建池：参数数已校验，后续行不再重复校验
                                    // （列数由生成器保证且池大小固定，逐行重验是纯开销）。
                                    if (valuesBinder is not null)
                                    {
                                        pool = new DbParameter[columnCount];
                                        for (int column = 0; column < columnCount; column++)
                                            pool[column] = rowCommand.Parameters[column];
                                    }
                                }

                                // P1：满批路径直传 pool——原实现经 rowCommand.Parameters 索引器
                                // 取值，每行每列一次跨接口虚调用 + 一次硬转型（10 万行 × 10 列
                                // = 100 万次），而 pool 与 Parameters 持有的是同一批对象。
                                await WriteRowAsync(importer, rowCommand, pool, columnCount, commandCt)
                                    .ConfigureAwait(false);
                                total++;
                            }
                            await importer.CompleteAsync(commandCt).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            importerException = exception;
                            throw;
                        }
                        finally
                        {
                            await BulkOperationFramework.DisposePreservingAsync(importer, importerException,
                                "PalORM.ImporterCleanupException").ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        timeoutCts.Dispose();
                    }
                }
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
                await CommitWithTimeoutAsync(bulkTransaction, commandTimeoutSeconds, ct)
                    .ConfigureAwait(false);
            return total;
        }
        catch (Exception exception)
        {
            // ITM-759(r21)：rollback/cleanup 异常挂"实际抛出对象"——包装路径下挂原 OCE
            // 会让顶层 TimeoutException.Data 丢失这些键（只能经 InnerException.Data 找回）。
            // 先判定是否包装，再以最终抛出对象为主异常挂载。
            bool wrapAsTimeout = exception is OperationCanceledException
                && !ct.IsCancellationRequested
                && timeoutFlag[0];  // ITM-760：本批超时 CTS 确已触发（驱动自抛 OCE 不会置位）
            Exception thrown = exception;
            if (wrapAsTimeout)
            {
                // ITM-726(r20)：COPY 无 CommandTimeout 挂点，超时经 CTS 触发，与调用方 ct 取消
                // 在异常类型上不可区分——包装为 TimeoutException 并打 Data 标记（Resilience 口径）。
                var wrappedTimeout = new TimeoutException(
                    $"COPY bulk insert timed out after {commandTimeoutSeconds}s per batch.", exception);
                wrappedTimeout.Data["PalORM.InfrastructureTimeout"] = true;
                thrown = wrappedTimeout;
            }
            primaryException = thrown;
            if (ownsTransaction)
                await RollbackPreservingAsync(bulkTransaction, thrown, commandTimeoutSeconds).ConfigureAwait(false);
            throw thrown;
        }
        finally
        {
            if (ownsTransaction)
                await BulkOperationFramework.DisposePreservingAsync(bulkTransaction, primaryException,
                    "PalORM.TransactionCleanupException").ConfigureAwait(false);
        }
    }

    /// <summary>B3：COPY 目标表的引用形态缓存——key 为 (Type, Dialect)，值为 (quotedTable, quotedColumns)。
    /// <para><b>键空间有限</b>：Type 由注册表决定（<c>Register</c> 对重复类型抛异常，故 Type→metadata
    /// 是 1:1 稳定映射），Dialect 只有 3 个值。因此键集上限 = 实体数 × 3，天然有限，只增不减，
    /// 与 <c>DataSessionCache</c> 的有限键集豁免同口径（已登记 docs/静态缓存清单.md）。</para>
    /// <para><b>为什么不在 Core 的 DataSessionCache</b>：那个类是 internal，Provider 是独立程序集
    /// 访问不到；且本缓存的值含 PG 方言引用符，本就该留在 PG 程序集内。</para></summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (Type EntityType, SqlDialect Dialect), (string QuotedTable, string QuotedColumns)>
        QuotedInsertTargetCache = new();

    /// <summary>取（或构建并缓存）指定实体类型的 COPY 目标引用形态。</summary>
    private static (string QuotedTable, string QuotedColumns) GetQuotedInsertTarget(Type entityType)
    {
        (Type, SqlDialect) key = (entityType, Dialect);
        if (QuotedInsertTargetCache.TryGetValue(key, out var cached))
            return cached;

        string tableName = PalORM_Runtime.TableNames.TryGetValue(entityType, out string? tn)
            ? tn
            : throw new InvalidOperationException(
                $"Type '{entityType.Name}' has no generated table metadata.");
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(entityType, out CrudMetadata crud))
            throw new InvalidOperationException(
                $"Type '{entityType.Name}' has no generated CRUD.");

        var built = (
            QuoteIdentifier(tableName),
            string.Join(", ", crud.InsertColumns.Select(QuoteIdentifier)));
        QuotedInsertTargetCache.TryAdd(key, built);
        return built;
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

    /// <summary>把单行参数写入 PG Binary importer——DBNull 转换为 null 让 importer 用列默认类型。
    /// <para>P1：<paramref name="pool"/> 非 null（满批复用路径）时直接索引取值，避开
    /// <see cref="DbParameterCollection"/> 索引器的跨接口虚调用与硬转型；池尚未建立
    /// （首行）或旧版生成器回退路径下走 rowCommand.Parameters。</para></summary>
    private static async ValueTask WriteRowAsync(
        NpgsqlBinaryImporter importer, DbCommand rowCommand, DbParameter[]? pool,
        int columnCount, CancellationToken ct)
    {
        await importer.StartRowAsync(ct).ConfigureAwait(false);
        for (int parameterIndex = 0; parameterIndex < columnCount; parameterIndex++)
        {
            var parameter = (NpgsqlParameter)(pool is not null
                ? pool[parameterIndex]
                : rowCommand.Parameters[parameterIndex]);
            object? value = parameter.Value is DBNull ? null : parameter.Value;
            await importer.WriteAsync(value, parameter.NpgsqlDbType, ct).ConfigureAwait(false);
        }
    }

    /// <summary>R2/T2：自管事务的 COMMIT 纳入 <paramref name="commandTimeoutSeconds"/> 超时窗口。
    /// COPY/LOAD DATA 每批已有 per-batch 超时（ITM-643），但整批收尾的 COMMIT 此前只受
    /// 调用方 ct 约束——<c>ct == default</c> 时网络黑洞可让提交永久挂起。超时包装为
    /// <see cref="TimeoutException"/> 并打 <c>PalORM.InfrastructureTimeout</c> 标记（与
    /// COPY 路径的 wrapAsTimeout 同口径），调用方据此判定"服务端状态未知"并尝试回滚。</summary>
    private static async ValueTask CommitWithTimeoutAsync(
        DbTransaction transaction, int commandTimeoutSeconds, CancellationToken ct)
    {
        if (commandTimeoutSeconds <= 0)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(commandTimeoutSeconds));
        try
        {
            await transaction.CommitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException timeoutException) when (timeoutCts.IsCancellationRequested)
        {
            var wrappedTimeout = new TimeoutException(
                $"Bulk insert commit timed out after {commandTimeoutSeconds}s.", timeoutException);
            wrappedTimeout.Data["PalORM.InfrastructureTimeout"] = true;
            throw wrappedTimeout;
        }
    }

    // ITM-412 防漂移锚点：以下清理助手与 Core 的 DataSession.RollbackPreservingAsync 是同一
    // "主异常保留"骨架的复制体（Provider 不得反向依赖 Core 内部实现，故刻意复制）。
    // 修改任一侧语义（异常挂载键、有界回滚）时必须同步核对另一侧——两侧分叉即 ITM-304 同型温床。
    // 注：DisposePreservingAsync 已抽到 BulkOperationFramework（v3.0），三 Provider 共享同一实现。
    /// <summary>R3：有界回滚——原实现用 CancellationToken.None 无界等待，网络黑洞下会把一次
    /// COPY 失败拖成永久卡死（且发生在异常传播路径）。超时异常挂主异常 Data，不替换原始失败。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "回滚是清理路径；异常附加到主异常，不能替换原始 COPY 失败。")]
    private static async ValueTask RollbackPreservingAsync(
        DbTransaction transaction, Exception primaryException, int rollbackTimeoutSeconds = 30)
    {
        CancellationToken ct = CancellationToken.None;
        CancellationTokenSource? timeoutCts = null;
        if (rollbackTimeoutSeconds > 0)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(rollbackTimeoutSeconds));
            ct = timeoutCts.Token;
        }
        try
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException timeoutException) when (ct.IsCancellationRequested)
        {
            primaryException.Data["PalORM.RollbackTimeoutException"] = new TimeoutException(
                $"Rollback timed out after {rollbackTimeoutSeconds}s; the server-side transaction may still be open.",
                timeoutException);
        }
        catch (Exception rollbackException)
        {
            primaryException.Data["PalORM.RollbackException"] = rollbackException;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }
}
