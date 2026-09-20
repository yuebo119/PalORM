using System.Data.Common;
using System.Runtime.CompilerServices;
using MySqlConnector;

namespace PalORM.MySql;

/// <summary>MySQL Provider —— MySqlConnector 适配。</summary>
public sealed class MySqlProvider : IDbProvider
{
    /// <summary>Provider 名称:MySql。</summary>
    public static string Name => "MySql";

    /// <summary>SQL 方言标识:<see cref="SqlDialect.MySql"/>。</summary>
    public static SqlDialect Dialect => SqlDialect.MySql;

    /// <summary>创建连接并把 <see cref="DbOptions"/> 池配置映射到 MySqlConnector 连接串:
    /// MaximumPoolSize / ConnectionIdleTimeout(秒)/ ConnectionLifeTime(分钟换算为秒);
    /// MySqlConnector 池参数为 uint,checked 转换防负值/溢出静默截断。
    /// <para><b>v5.0 阶段 3.2 调优</b>：对每个调优参数，如果用户连接串里的值等于该参数的
    /// ADO.NET 默认值（即用户未显式调优），则覆盖为推荐调优值：
    /// AutoEnlist: true→false（跳过 TransactionScope 检查）；
    /// ConnectionReset: true→false（跳过 COM_RESET_CONNECTION，归还池更快）；
    /// UseCompression: 默认 false 不变（本类不写该参数——见下方 ITM-730 订正）；
    /// CancellationTimeout: 2→5（软取消 5 秒后强制关闭，避免连接泄漏）；
    /// AllowLoadLocalInfile: false→true（v5.0 阶段 4.2 MySqlBulkCopy 前提）；
    /// ServerRedirectionMode: Disabled→Preferred（Azure MySQL 直连后端）。</para>
    /// <para><b>判断策略</b>：用"属性当前值 == ADO.NET 默认值"作为"用户未显式设置"的判据
    /// （同 PostgreSqlProvider，详见其注释）。
    /// <b>布尔旋钮边界（审计 PROV-011 文档化）</b>：该判据对布尔参数意味着显式设置与
    /// 未设置完全不可区分——AutoEnlist/ConnectionReset 的显式 true（环境事务/会话状态
    /// 隔离需求，ITM-643）会被本调优静默改写为 false；AllowLoadLocalInfile 的显式 false
    /// （安全加固）会被改写为 true（ADR-G 三层兜底裁决在案）。需要这些语义的用户必须
    /// 绕开本工厂自建连接。</para></summary>
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new MySqlConnectionStringBuilder(connectionString);
        // ITM-612：池参数遵循下方系列的"仅默认时覆盖"策略——原对象初始化器在连接串解析后
        // 无条件覆盖，连接串内嵌池参数被静默改写为 DbOptions 默认值。
        // MySqlConnector 默认：MaximumPoolSize=100 / ConnectionIdleTimeout=180 / ConnectionLifeTime=0。
        if (builder.MaximumPoolSize == 100)
            builder.MaximumPoolSize = checked((uint)options.MaxPoolSize);
        // C4（v5.7）：空闲保留下限——0 = 不覆盖（MySqlConnector 默认 0）。>0 时
        // ConnectionIdleTimeout 到期修剪至少保留这么多条（官方 XML 文档语义），
        // 避免稀疏流量清池后突发查询重建物理连接。uint 池参数 checked 转换。
        if (options.MinPoolSize > 0 && builder.MinimumPoolSize == 0)
            builder.MinimumPoolSize = checked((uint)options.MinPoolSize);
        // v5.6：0 = 不覆盖（保留 MySqlConnector 默认 180 秒）——理由同 PostgreSqlProvider。
        if (options.PoolIdleTimeoutSeconds > 0 && builder.ConnectionIdleTimeout == 180)
            builder.ConnectionIdleTimeout = checked((uint)options.PoolIdleTimeoutSeconds);
        if (builder.ConnectionLifeTime == 0)
            builder.ConnectionLifeTime = checked((uint)(options.PoolLifetimeMinutes * 60));

        // v5.0 阶段 3.2：仅当属性当前值等于 ADO.NET 默认值时覆盖为调优推荐值。
        // MySqlConnector 默认值：AutoEnlist=true，ConnectionReset=true，UseCompression=false，
        // CancellationTimeout=2，AllowLoadLocalInfile=false，ServerRedirectionMode=Disabled。
        // ITM-643(r4) 登记：显式 AutoEnlist=true 与默认不可区分（同上）——环境事务静默脱离。
        if (builder.AutoEnlist)
            builder.AutoEnlist = false;
        if (builder.ConnectionReset)
            builder.ConnectionReset = false;
        // ITM-730(r20) 订正：UseCompression 不在此赋值。本方法签名只接收 connectionString，
        // 无从区分"用户显式 true"与"部署环境注入"——原注释"显式固定防注入"无法实现（假承诺）。
        // 当前行为 = 不干预（用户设置优先），如需强制策略请在传入前构造连接串。
        if (builder.CancellationTimeout == 2)
            builder.CancellationTimeout = 5;
        // AllowLoadLocalInfile: false→true（v5.0 阶段 4.2 MySqlBulkCopy 前提）。
        // ITM-612/EVAL-1：builder 层无法区分"未设置（驱动默认 false）"与"显式 false（安全加固，
        // 规避恶意服务端读取客户端文件的攻击面）"——显式 false 会被此覆盖。
        // ITM-666：ADR-G（2026-08-15）已裁决维持本现状——三层兜底（文档登记攻击面 /
        // 服务端 local_infile=OFF 即禁 BulkCopy / 不做启发式检测），revisit 条件见 ADR-G。
        if (!builder.AllowLoadLocalInfile)
            builder.AllowLoadLocalInfile = true;
        if (builder.ServerRedirectionMode == MySqlServerRedirectionMode.Disabled)
            builder.ServerRedirectionMode = MySqlServerRedirectionMode.Preferred;

        return new MySqlConnection(builder.ConnectionString);
    }

    /// <summary>反引号引用标识符(MySQL 方言,非 SQL 标准双引号),内部反引号以 `` 转义。</summary>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        // ITM-584/593: 三方言共享 IdentifierSafety 守卫（C0 控制字符族 + DEL）。
        IdentifierSafety.ThrowIfUnsafe(identifier);
        // L3：无内嵌反引号时 string.Replace 仍返回新实例——走 Concat 免掉一次纯拷贝。
        return identifier.Contains('`', StringComparison.Ordinal)
            ? $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`"
            : string.Concat("`", identifier, "`");
    }

    /// <summary>schema 与表名分别反引号引用后以点连接;MySQL 中 schema 即数据库名。
    /// 覆盖接口默认实现以支持 MySQL 的 schema/database 语义。</summary>
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    /// <summary>MySQL 不支持 RETURNING 子句(自增主键回读走 LAST_INSERT_ID 路径)。</summary>
    public static bool SupportsReturningClause => false;

    /// <summary>CURRENT_TIMESTAMP——注意 MySQL 返回会话时区时间(与 SQLite 的恒 UTC 语义不同,ITM-326)。</summary>
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    /// <summary>MySQL 无 CREATE INDEX IF NOT EXISTS：迁移幂等靠识别 1061 重名索引错误。
    /// <para><b>ITM-528 已知限制</b>：错误码 1061 只表示"索引名已存在"，无法区分
    /// "同名同构"（真正幂等，应跳过）与"同名异构"（旧索引列集不同，实为冲突）。
    /// 因此修改 [Index]/[Unique] 的列集但保留索引名后再迁移，旧索引会被当作幂等静默保留，
    /// 新列集不生效。规避方法：变更被索引列后需手动 DROP INDEX 旧索引再迁移，
    /// 或直接改用新的索引名。运行时无法安全区分两者，故此处不改判定逻辑。</para></summary>
    public static bool IsDuplicateSchemaObject(Exception exception)
        => exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyName };

    /// <summary>1062 Duplicate entry——唯一约束冲突。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };

    /// <summary>用 SHOW COLUMNS 查询列信息(表名/库名经反引号引用内联),列名位于结果集序号 0。</summary>
    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        // S2077 报备（M1-5）：表名/schema 经 QuoteQualifiedIdentifier 标识符转义（标识符面）
#pragma warning disable S2077
        command.CommandText = $"SHOW COLUMNS FROM {QuoteQualifiedIdentifier(schema, tableName)}";
#pragma warning restore S2077
        return 0;
    }

    /// <summary>创建 MySqlParameter;value 为 null 时转为 <see cref="DBNull.Value"/>(ADO.NET 中 null 参数值不会被发送)。</summary>
    public static DbParameter CreateParameter(string name, object? value)
        => new MySqlParameter(name, value ?? DBNull.Value);

    /// <summary>批量插入——v5.0 阶段 4.2 改进：local_infile 能力检测分流（替代原 2000 阈值）。
    /// <para>分流判据：<c>local_infile=ON</c>（服务端）走 <c>MySqlBulkCopy</c>（LOAD DATA LOCAL
    /// INFILE 协议，~4.84x），否则走多值 INSERT（无协议初始化开销）。与 PG COPY 永远走最优协议对齐。</para>
    /// <para><b>无阈值</b>：不再用行数阈值（2000 是伪精确），改为环境能力检测——行为可预测。
    /// 检测开销：每次 BulkInsert 额外 1 次 SHOW VARIABLES RTT（&lt;1ms，批量场景占比可忽略）。</para>
    /// <para>MySQL 协议单语句占位符上限 65535（2 字节计数）；宽表大批次经骨架按列数钳制，
    /// 避免超出 max_allowed_packet/预处理参数上限时的晚期运行时错误（ITM-304）。</para></summary>
    public static async Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds, CancellationToken ct,
        System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);
        // batchSize 校验优先于 entities.Count 检查——调用方契约（ProviderTests 验证）。
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
        // ITM-637 同型面：元数据检查先于空列表短路——未注册类型与空/非空列表一致抛
        //（BulkCopy 与多值 fallback 两分支共用此前置；PROV-010：守卫收敛至单一实现点）
        _ = BulkOperationFramework.EnsureInsertMetadata(typeof(T));
        // ITM-740(r20)/778(r21)：包装/装饰事务（非 MySqlTransaction）在连接上挂起时，能力探测
        // 会因"命令未挂接事务"抛异常并被吞掉 → 静默降级多值路径。守卫置于**空列表短路之前**
        // （ITM-637 口径：契约校验先于短路）——原顺序下空列表+包装事务静默返 0、非空则抛，
        // 同参数形态结果不对称。
        if (conn is MySqlConnection && transaction is not null && transaction is not MySqlTransaction)
            throw new ArgumentException(
                $"External transaction must be a MySqlTransaction (got '{transaction.GetType().Name}'). "
                + "Wrapped/decorated transactions are not supported by the MySQL bulk path.",
                nameof(transaction));
        if (entities.Count == 0) return 0;

        // local_infile 能力检测：开启走 BulkCopy（对齐 PG 永远 COPY），关闭走多值 INSERT。
        if (conn is MySqlConnection mySqlConnection
            && await IsLocalInfileEnabledAsync(mySqlConnection, transaction as MySqlTransaction, ct).ConfigureAwait(false))
        {
            return await ExecuteBulkCopyAsync(
                mySqlConnection, transaction, entities, batchSize, commandTimeoutSeconds, isolationLevel, ct).ConfigureAwait(false);
        }

        // 回退路径：local_infile=OFF 或非 MySqlConnection，走多值 INSERT。
        return await MultiValueBulkInsert.ExecuteAsync(
            conn, transaction, entities,
            new BulkContext(
                batchSize,
                MaxParametersPerStatement: SqlLimits.MaxBindParameters,
                QuoteIdentifier, CreateParameter, commandTimeoutSeconds,
                IsolationLevel: isolationLevel),  // r7-S1：回退分支同透传
            ct).ConfigureAwait(false);
    }

    /// <summary>检测服务端 local_infile 是否开启。
    /// <para><b>L1：按连接缓存探测结果</b>——原实现每次 BulkInsertAsync 都付一次
    /// <c>SHOW VARIABLES</c> RTT。注释以"&lt;1ms，批量场景占比可忽略"论证，但每请求只插
    /// 5 行的短事务场景下这一次 RTT 与业务写入同量级（跨网段 0.3~2ms，可占总延迟三成以上）。
    /// 探测结果是连接级事实（同一连接的 <c>local_infile</c> 由连接串与账号决定），
    /// 不是进程级可变状态，缓存不违反零全局状态原则。</para>
    /// <para><b>TTL 与失效</b>：默认 60 秒——服务端变量可运行时变更，短 TTL 让托管环境
    /// 的配置刷新在一分钟内生效；探测异常不写入缓存（下次重探，不把瞬时故障固化为 OFF）。</para>
    /// <para><b>R14：降级可观测</b>——探测故障 vs 能力关闭都会走多值路径（慢约 4.84×），
    /// 原实现静默无痕。故障经 <see cref="BulkOperationFramework.RecordCapabilityProbeFailure"/>
    /// 计数，运行期"突然变慢"有归因线索。</para>
    /// <para>ITM-633：检测命令挂接外部事务（连接 pending 事务下未挂接命令 MySqlConnector
    /// 会抛 InvalidOperationException）；检测自身故障（网络抖动/超时）降级为 OFF 走多值
    /// INSERT——能力探测不应终止整批插入。</para></summary>
    private static readonly ConditionalWeakTable<MySqlConnection, LocalInfileProbe> LocalInfileCache = [];

    /// <summary>探测结果缓存条目——<see cref="ExpiresAtTicks"/> 用 <see cref="Environment.TickCount64"/>
    /// 单调钟，不受系统时间回拨影响。必须是引用类型：ConditionalWeakTable 的 TValue 约束为
    /// class（键才是弱引用，值随键一起回收）。</summary>
    private sealed class LocalInfileProbe(bool enabled, long expiresAtTicks)
    {
        public bool Enabled { get; } = enabled;
        public long ExpiresAtTicks { get; } = expiresAtTicks;
    }

    private const int LocalInfileProbeTtlMilliseconds = 60_000;

    private static async ValueTask<bool> IsLocalInfileEnabledAsync(
        MySqlConnection conn, MySqlTransaction? transaction, CancellationToken ct)
    {
        if (LocalInfileCache.TryGetValue(conn, out LocalInfileProbe? cached)
            && Environment.TickCount64 < cached.ExpiresAtTicks)
        {
            return cached.Enabled;
        }

        try
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = 3;  // SHOW VARIABLES 是即时查询，3 秒足够
            cmd.CommandText = "SHOW VARIABLES LIKE 'local_infile'";
            using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return false;
            string value = reader.GetString(1);
            bool enabled = string.Equals(value, "ON", StringComparison.OrdinalIgnoreCase) || value == "1";
            LocalInfileCache.AddOrUpdate(conn, new LocalInfileProbe(
                enabled, Environment.TickCount64 + LocalInfileProbeTtlMilliseconds));
            return enabled;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // R14：探测故障与能力关闭在多值路径上行为一致，但归因完全不同——计数留痕
            BulkOperationFramework.RecordCapabilityProbeFailure();
            return false;  // 探测故障降级多值路径（见 summary）；取消原样上抛
        }
    }

    /// <summary>v5.0 阶段 4.2：MySqlBulkCopy 路径。从 BulkInsertAsync 抽出以降低认知复杂度（S3776）。
    /// <para><b>事务语义</b>：调用方传入的 transaction 一并使用；未传时内部开新事务包整批。</para>
    /// <para><b>分批</b>：ITM-710(r20)——<paramref name="batchSize"/> 透传到 Inserter 分块物化，
    /// 与回退多值路径同口径（此前整表一次性灌入 DataTable，契约静默失效 + 内存峰值）。</para></summary>
    private static async Task<long> ExecuteBulkCopyAsync<T>(
        MySqlConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds,
        System.Data.IsolationLevel isolationLevel, CancellationToken ct)  // r6-N2（CA1068 ct 末位）
        where T : class, new()
    {
        // PROV-010：守卫收敛至单一实现点（唯一调用方 BulkInsertAsync 入口已检，此处经
        // helper 取值而非复制实现——防御语义保留，重复实现消除）
        (CrudMetadata metadata, string tableName) = BulkOperationFramework.EnsureInsertMetadata(typeof(T));

        // MySQL schema=database；当前会话已在连接串指定的库里，表名直接引用。
        string quotedTable = QuoteIdentifier(tableName);
        // 主键列——DataTable 中放首列填 NULL（AUTO_INCREMENT 自增）。
        // L4：表名/主键列的引用只由 (Type, Provider) 决定，原实现每批重算一次
        // （MySqlBulkCopyInserter 按 batchSize 分块调用本方法，千批 = 千次无谓分配）
        string? pkColumn = PalORM_Runtime.PkColumns.TryGetValue(typeof(T), out string? pk) ? pk : null;
        IReadOnlyList<string> pkColumns = pkColumn is not null ? [pkColumn] : [];

        // 事务：BulkCopy 需在事务内执行；未传时内部开新事务保证原子性。
        MySqlTransaction? mySqlTransaction = transaction as MySqlTransaction;
        // ITM-795(r21)：原此处的包装事务守卫已上移至 BulkInsertAsync 入口（ITM-740/778，
        // 探测前拦截）——本 private 方法的同型守卫不可达，删除。新增调用点经公共入口即受守卫。
        bool ownsTransaction = false;
        if (mySqlTransaction is null)
        {
            mySqlTransaction = await conn.BeginTransactionAsync(isolationLevel, ct).ConfigureAwait(false);  // r6-N2
            ownsTransaction = true;
        }
        Exception? primaryException = null;
        try
        {
            long inserted = await MySqlBulkCopyInserter.ExecuteAsync(
                conn,
                mySqlTransaction,
                entities,
                new MySqlBulkCopyContext(
                    quotedTable,
                    metadata.InsertColumns,
                    pkColumns,
                    metadata.BindInsert,
                    commandTimeoutSeconds,
                    batchSize,
                    metadata.BindInsertValues),
                ct).ConfigureAwait(false);
            // R2/T2：自管事务的 COMMIT 纳入 commandTimeoutSeconds 超时窗口。
            // BulkCopyTimeout 只覆盖 LOAD DATA，不覆盖 COMMIT——此前 ct == default 时提交
            // 无界等待。超时包装为 TimeoutException 并打 InfrastructureTimeout 标记，
            // 与 PG 路径同口径；同时让 TransactionCleanup 判定"服务端状态未知"以尝试回滚。
            if (ownsTransaction)
                await CommitWithTimeoutAsync(mySqlTransaction, commandTimeoutSeconds, ct)
                    .ConfigureAwait(false);
            return inserted;
        }
        catch (Exception ex)
        {
            primaryException = ex;
            throw;
        }
        finally
        {
            if (ownsTransaction)
            {
                await BulkOperationFramework.DisposePreservingAsync(
                    mySqlTransaction, primaryException, "PalORM.TransactionCleanupException")
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>COMMIT 的超时包装——与 PG 路径（<c>PostgreSqlProvider.CommitWithTimeoutAsync</c>）
    /// 同口径：仅超时触发时包装为带 <c>PalORM.InfrastructureTimeout</c> 标记的
    /// <see cref="TimeoutException"/>，调用方取消原样上抛；commandTimeoutSeconds ≤ 0
    /// （全库 Zero = 无限等待契约）时不设超时。</summary>
    private static async ValueTask CommitWithTimeoutAsync(
        MySqlTransaction transaction, int commandTimeoutSeconds, CancellationToken ct)
    {
        if (commandTimeoutSeconds <= 0)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(System.TimeSpan.FromSeconds(commandTimeoutSeconds));
        try
        {
            await transaction.CommitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (System.OperationCanceledException timeoutException) when (timeoutCts.IsCancellationRequested)
        {
            var wrappedTimeout = new System.TimeoutException(
                $"Bulk insert commit timed out after {commandTimeoutSeconds}s.", timeoutException);
            wrappedTimeout.Data["PalORM.InfrastructureTimeout"] = true;
            throw wrappedTimeout;
        }
    }
}
