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

    /// <summary>2026-10-22 终审 P1-8：COM_RESET_CONNECTION 清会话变量（sql_mode/用户变量等）。
    /// 连接串 ConnectionReset=false 只跳过自动归还复位，显式调用不受影响。</summary>
    public static async Task ResetConnectionStateAsync(
        DbConnection connection, int commandTimeoutSeconds, CancellationToken ct)
    {
        _ = commandTimeoutSeconds;
        if (connection is MySqlConnection mySqlConnection)
            await mySqlConnection.ResetConnectionAsync(ct).ConfigureAwait(false);
    }

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
    /// <b>布尔旋钮边界（审计 PROV-011，PROV-001 起已可区分）</b>：AutoEnlist/ConnectionReset 等
    /// 布尔参数显式给出时不再被改写（此前显式 true 会被静默改为 false，导致环境事务脱离
    /// ITM-643 / 会话状态隔离失效）。唯一例外是 <c>AllowLoadLocalInfile</c>：其改写属安全策略
    /// （ADR-G 三层兜底裁决在案），ADR-G 的前提"builder 层无法区分未设置与显式 false"已被
    /// 实测证伪，但改写与否需用户裁决后再动，当前维持现状。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "本方法是连接串调优旋钮的线性清单（池参数 + 预编译/缓冲/环境事务），"
            + "复杂度来自旋钮数量而非嵌套；PROV-001 起每个旋钮多一个显式键判定条件。"
            + "拆分会把 ITM-612 的'单点覆盖口径'打散到多处，反而增加漂移风险。")]
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // MySQL-6（2026-09-27，PG-6 同构移植）：改写结果按全部输入缓存——本方法输入只有
        // (连接串, 4 个 options 池参数字段)（旋钮清单只读 MaxPoolSize/MinPoolSize/
        // PoolIdleTimeoutSeconds/PoolLifetimeMinutes，其余为常量/驱动默认比对）。探针十四实测
        // 单次成本 = MySqlConnectionStringBuilder 解析 4.00µs + 9× Keys LINQ 扫描 3.46µs ≈ 7.5µs；
        // PerfHub 夹具用共享连接不经过，但每操作自建会话用法（DataSession.CreateAsync per op）
        // 全额支付。缓存命中后只剩字典查询 + new MySqlConnection（0.04µs）。逻辑零变更。
        // MySQL-6/D21（2026-10-12 审读修复）：多租户逐租户连接串使键空间不再"个位数"——
        // 上限封顶 + 满清自愈（PG-6 同构，值含凭据副本常驻量随之封顶）。工厂失败不缓存。
        if (RewrittenConnectionStringCache.Count >= RewrittenCacheCapacity)
            RewrittenConnectionStringCache.Clear();
        string rewritten = RewrittenConnectionStringCache.GetOrAdd(
            (connectionString, options.MaxPoolSize, options.MinPoolSize,
                options.PoolIdleTimeoutSeconds, options.PoolLifetimeMinutes),
            static (key, opts) => RewriteConnectionString(key.ConnectionString, opts),
            options);
        return new MySqlConnection(rewritten);
    }

    /// <summary>MySQL-6：改写结果缓存——键 = 本方法全部输入（连接串 + 4 个 options 池参数字段）。
    /// 键空间上限 = 连接串变体数 × 池参数组合数（应用内通常个位数），与
    /// PostgreSqlProvider 的同名缓存同族设计（跨程序集不可 cref）。工厂失败
    /// （非法连接串）不缓存，异常照旧传播。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string ConnectionString, int MaxPoolSize, int MinPoolSize, int PoolIdleTimeoutSeconds, int PoolLifetimeMinutes), string>
        RewrittenConnectionStringCache = new();

    /// <summary>MySQL-6/D21：改写缓存容量上限（与 PostgreSqlProvider 同构纪律）。</summary>
    private const int RewrittenCacheCapacity = 256;

    /// <summary>MySQL-6：连接串旋钮改写的实际逻辑（从 CreateConnection 原样抽出，逻辑零变更）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "MySQL-6：连接串调优旋钮改写（原 CreateConnection 主体原样抽出）——线性旋钮清单，"
            + "复杂度来自旋钮数量而非嵌套；拆分会打散 ITM-612 的'单点覆盖口径'。")]
    private static string RewriteConnectionString(string connectionString, DbOptions options)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        // ITM-612：池参数遵循下方系列的"仅默认时覆盖"策略——原对象初始化器在连接串解析后
        // 无条件覆盖，连接串内嵌池参数被静默改写为 DbOptions 默认值。
        // MySqlConnector 默认：MaximumPoolSize=100 / ConnectionIdleTimeout=180 / ConnectionLifeTime=0。
        // PROV-001（2026-09-23）：判据补 ContainsKey（键集只含显式出现的键）——"值 == 驱动默认"
        // 无法区分"用户没设"与"用户显式设成默认值"（后者会被静默改写，用户意图落空）。
        // 两条件同时保留：ContainsKey 管用户意图，值比对管驱动默认值漂移。键名经 MySqlConnector
        // 2.6.2 探针实测。
        if (!HasExplicitKey(builder, "Maximum Pool Size") && builder.MaximumPoolSize == 100)
            builder.MaximumPoolSize = checked((uint)options.MaxPoolSize);
        // C4（v5.6.0）：空闲保留下限——0 = 不覆盖（MySqlConnector 默认 0）。>0 时
        // ConnectionIdleTimeout 到期修剪至少保留这么多条（官方 XML 文档语义），
        // 避免稀疏流量清池后突发查询重建物理连接。uint 池参数 checked 转换。
        if (options.MinPoolSize > 0 && !HasExplicitKey(builder, "Minimum Pool Size") && builder.MinimumPoolSize == 0)
            builder.MinimumPoolSize = checked((uint)options.MinPoolSize);
        // v5.6：0 = 不覆盖（保留 MySqlConnector 默认 180 秒）——理由同 PostgreSqlProvider。
        if (options.PoolIdleTimeoutSeconds > 0 && !HasExplicitKey(builder, "Connection Idle Timeout")
            && builder.ConnectionIdleTimeout == 180)
        {
            builder.ConnectionIdleTimeout = checked((uint)options.PoolIdleTimeoutSeconds);
        }
        if (!HasExplicitKey(builder, "Connection Lifetime") && builder.ConnectionLifeTime == 0)
            builder.ConnectionLifeTime = checked((uint)(options.PoolLifetimeMinutes * 60));

        // v5.0 阶段 3.2：仅当属性当前值等于 ADO.NET 默认值（且连接串未显式给出该键）时覆盖为调优推荐值。
        // MySqlConnector 默认值：AutoEnlist=true，ConnectionReset=true，UseCompression=false，
        // CancellationTimeout=2，AllowLoadLocalInfile=false，ServerRedirectionMode=Disabled。
        // ITM-643(r4)：显式 AutoEnlist=true 此前与默认不可区分（环境事务静默脱离）；
        // PROV-001 起 ContainsKey 可区分，显式值不再被改写。
        if (!HasExplicitKey(builder, "Auto Enlist") && builder.AutoEnlist)
            builder.AutoEnlist = false;
        if (!HasExplicitKey(builder, "Connection Reset") && builder.ConnectionReset)
            builder.ConnectionReset = false;
        // ITM-730(r20) 订正：UseCompression 不在此赋值。本方法签名只接收 connectionString，
        // 无从区分"用户显式 true"与"部署环境注入"——原注释"显式固定防注入"无法实现（假承诺）。
        // 当前行为 = 不干预（用户设置优先），如需强制策略请在传入前构造连接串。
        if (!HasExplicitKey(builder, "Cancellation Timeout") && builder.CancellationTimeout == 2)
            builder.CancellationTimeout = 5;
        // AllowLoadLocalInfile: false→true（v5.0 阶段 4.2 MySqlBulkCopy 前提）。
        // ITM-612/EVAL-1：builder 层无法区分"未设置（驱动默认 false）"与"显式 false（安全加固，
        // 规避恶意服务端读取客户端文件的攻击面）"——显式 false 会被此覆盖。
        // ITM-666：ADR-G（2026-08-15）已裁决维持本现状——三层兜底（文档登记攻击面 /
        // 服务端 local_infile=OFF 即禁 BulkCopy / 不做启发式检测），revisit 条件见 ADR-G。
        // PROV-001（2026-09-23）：ContainsKey 现已可区分两者（ADR-G 的前提"builder 层无法区分"
        // 被实测证伪）——但改写与否是安全策略裁决，仍按 ADR-G 维持现状，待用户裁决后再动。
        if (!builder.AllowLoadLocalInfile)
            builder.AllowLoadLocalInfile = true;
        if (!HasExplicitKey(builder, "Server Redirection Mode")
            && builder.ServerRedirectionMode == MySqlServerRedirectionMode.Disabled)
        {
            builder.ServerRedirectionMode = MySqlServerRedirectionMode.Preferred;
        }

        return builder.ConnectionString;
    }

    /// <summary>连接串是否显式给出某键（PROV-001，2026-09-23）。
    /// <b>不用</b> <c>ContainsKey</c>：MySqlConnector 2.6.2 的 ContainsKey 实测行为正确
    /// （未设置返回 false），但 Npgsql 10.0.3 对未设置的已知关键字返回 true——两 Provider
    /// 用同一判据（<c>Keys</c> 集合，只含显式出现的键）以避免"同一语义两种机制"的漂移。
    /// 冷路径（每会话一次），LINQ 形式可接受。</summary>
    private static bool HasExplicitKey(MySqlConnectionStringBuilder builder, string keyword)
        => builder.Keys.Cast<string>().Contains(keyword, StringComparer.OrdinalIgnoreCase);

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
    /// 实现 static abstract 成员（IDbProvider 不提供默认实现——CS8926，见接口注释）。ITM-871（r23）：订正 doc 残留（原写"覆盖接口默认实现"）。</summary>
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    /// <summary>MySQL 不支持 RETURNING 子句(自增主键回读走 LAST_INSERT_ID 路径)。</summary>
    public static bool SupportsReturningClause => false;

    /// <summary>复用命令晋升时执行一次服务端预编译——MySQL 驱动没有 PostgreSQL 的自动预编译
    /// （逐次文本解析）；命令复用槽保证同 SQL、同参数形态，晋升点正是付一次
    /// COM_STMT_PREPARE 的最佳时点。实测重复单键读约 −10~18%。</summary>
    public static bool PrepareReusedCommands => true;

    /// <summary>全量物化支持同步逐行读（需 <see cref="DbOptions.UseSynchronousRowRead"/> 开启）。
    /// 5 轮交替实测同步 998 ns/行 vs 异步 1846~2043 ns/行。</summary>
    public static bool SupportsSynchronousRowRead => true;

    /// <summary>多条独立只读查询可经 <see cref="MySqlBatch"/> 合并为一次调用（本机单库
    /// 执行耗时时与顺序持平，WAN 下省 RTT；结果集迭代顺序与入队一致）。</summary>
    public static bool SupportsQueryPipeline => true;

    /// <summary>管道单批语句上限。</summary>
    public static int MaxPipelineCommands => 64;

    /// <summary>清空 local_infile 能力探测缓存（测试基础设施入口，经 InternalsVisibleTo 暴露给
    /// Integration 测试）。真库用例在运行中 SET GLOBAL local_infile=OFF 再恢复 ON，恢复后
    /// 缓存仍可能持有 OFF 结论至 60s TTL 到期，导致其余用例静默降级多值路径（BulkCopy 集成
    /// 覆盖率长期 0%，2026-10-18 审计实测）。生产代码不调用本方法。</summary>
    internal static void ResetLocalInfileProbeCache() => CapabilityProbeCache<LocalInfileProbe>.Clear();

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

    /// <summary>1062 Duplicate entry——唯一约束冲突。
    /// R4（v6.0）：对统一翻译异常幂等（UniqueConstraintViolationException 的 Inner 为 1062）。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is UniqueConstraintViolationException
            or MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };

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
        if (conn is MySqlConnection mySqlConnection)
        {
            LocalInfileProbeResult probe = await ProbeLocalInfileAsync(
                mySqlConnection, transaction as MySqlTransaction, ct).ConfigureAwait(false);
            if (probe == LocalInfileProbeResult.Enabled)
            {
                try
                {
                    return await ExecuteBulkCopyAsync(
                        mySqlConnection, transaction, entities, batchSize, commandTimeoutSeconds, isolationLevel, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // ITM-892（r24，真库实证）：探测缓存 60s TTL 内服务端可被关闭——BulkCopy 抛
                    // "Loading local data is disabled"（协议握手阶段拒绝，零行写入）整批硬失败。
                    // 2026-10-22 终审 P2-5：重探测只有明确 Disabled 才回退（握手拒绝零写入）；
                    // Enabled（真故障，可能部分写入）与 ProbeFailed（能力未知）都透传，避免重插。
                    CapabilityProbeCache<LocalInfileProbe>.Invalidate(mySqlConnection.ConnectionString);
                    LocalInfileProbeResult reprobe = await ProbeLocalInfileAsync(
                        mySqlConnection, transaction as MySqlTransaction, ct).ConfigureAwait(false);
                    if (reprobe == LocalInfileProbeResult.Disabled)
                    {
                        return await MultiValueBulkInsert.ExecuteAsync(
                            conn, transaction, entities,
                            new BulkContext(
                                batchSize,
                                MaxParametersPerStatement: SqlLimits.MaxBindParameters,
                                QuoteIdentifier, CreateParameter, commandTimeoutSeconds,
                                IsolationLevel: isolationLevel),
                            ct).ConfigureAwait(false);
                    }
                    throw;
                }
            }
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
    /// <para><b>L1：按连接串缓存探测结果</b>（EPSL 2026-10-11 审计 B 批 #2 修订）——原实现每次
    /// BulkInsertAsync 都付一次 <c>SHOW VARIABLES</c> RTT；L1 首修按连接包装实例键控，但每操作
    /// 自建会话形态（DataSession.CreateAsync per op）每次都是新包装实例，缓存永不命中，per-op
    /// 场景照付全款 RTT（跨网段 0.3~2ms，短事务场景可占总延迟三成以上）。探测结果是
    /// 「同一连接串所指服务端 + 账号」的事实，故键取改写后连接串（CapabilityProbeCache 底座，
    /// RewrittenConnectionStringCache 同族键控），会话间共享、60s TTL 内零探测往返。</para>
    /// <para><b>TTL 与失效</b>：60 秒——服务端变量可运行时变更，短 TTL 让托管环境的配置刷新
    /// 在一分钟内生效；探测异常不写入缓存（下次重探，不把瞬时故障固化为 OFF）；BulkCopy 失败
    /// 后经 <see cref="CapabilityProbeCache{T}.Invalidate"/> 强制失效重探（ITM-892）。</para>
    /// <para><b>R14：降级可观测</b>——探测故障 vs 能力关闭都会走多值路径（慢约 4.84×），
    /// 原实现静默无痕。故障经 <see cref="BulkOperationFramework.RecordCapabilityProbeFailure"/>
    /// 计数，运行期"突然变慢"有归因线索。</para>
    /// <para><b>ITM-633：检测命令挂接外部事务</b>（连接 pending 事务下未挂接命令 MySqlConnector
    /// 会抛 InvalidOperationException）；检测自身故障（网络抖动/超时）降级为 OFF 走多值
    /// INSERT——能力探测不应终止整批插入。</para></summary>
    private sealed record LocalInfileProbe(bool Enabled);

    private const int LocalInfileProbeTtlMilliseconds = 60_000;

    /// <summary>检测服务端 local_infile 是否开启——键 = <see cref="MySqlConnection.ConnectionString"/>
    /// （改写后串），命中未过期直接返回；未命中执行 SHOW VARIABLES 并写缓存。探测故障
    /// （异常 / ITM-822 零行形态）经异常路径上抛，由本方法捕获后计数降级（不写缓存）。
    /// R3（2026-10-12）：失败时打负结果标记（10s 窗口）——持续故障形态下原每次 BulkInsert
    /// 重付探测往返 + 3s 超时封顶（最坏延迟洞），窗口内直接降级多值路径不再探测。</summary>
    private const int LocalInfileNegativeTtlMilliseconds = 10_000;

    private enum LocalInfileProbeResult
    {
        Enabled,
        Disabled,
        ProbeFailed,
    }

    /// <summary>检测服务端 local_infile 三态——键 = <see cref="MySqlConnection.ConnectionString"/>
    /// （改写后串），命中未过期直接返回；未命中执行 SHOW VARIABLES 并写缓存。
    /// <para><b>P2-5 三态</b>：负窗口是探测故障，不是能力关闭。启动路径 Enabled 之外的
    /// Disabled/ProbeFailed 都回退多值（尚无写入，安全）；BulkCopy 失败后的重探测只有
    /// Disabled（握手拒绝、零行写入）才回退，ProbeFailed 必须原样透传，避免整批重插。</para></summary>
    private static async ValueTask<LocalInfileProbeResult> ProbeLocalInfileAsync(
        MySqlConnection conn, MySqlTransaction? transaction, CancellationToken ct)
    {
        if (CapabilityProbeCache<LocalInfileProbe>.IsNegativeFresh(conn.ConnectionString))
            return LocalInfileProbeResult.ProbeFailed;
        try
        {
            LocalInfileProbe probe = await CapabilityProbeCache<LocalInfileProbe>.GetOrProbeAsync(
                conn.ConnectionString,
                LocalInfileProbeTtlMilliseconds,
                async token =>
                {
                    using DbCommand cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandTimeout = 3;  // SHOW VARIABLES 是即时查询，3 秒足够
                    cmd.CommandText = "SHOW VARIABLES LIKE 'local_infile'";
                    using DbDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        // ITM-822（r23 实修）：SHOW VARIABLES 恒返回一行，零行=服务端异常形态——
                        // 以异常走失败路径（计数留痕、不写缓存；受限账号每次 BulkInsert 恒付探测
                        // RTT 且计数恒 0 的观测盲区与原形态一致）
                        throw new InvalidOperationException(
                            "SHOW VARIABLES returned no row for 'local_infile'.");
                    }
                    string value = reader.GetString(1);
                    bool enabled = string.Equals(value, "ON", StringComparison.OrdinalIgnoreCase) || value == "1";
                    return new LocalInfileProbe(enabled);
                },
                ct).ConfigureAwait(false);
            return probe.Enabled ? LocalInfileProbeResult.Enabled : LocalInfileProbeResult.Disabled;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // R14：探测故障与能力关闭在多值路径上行为一致，但归因完全不同——计数留痕
            BulkOperationFramework.RecordCapabilityProbeFailure();
            CapabilityProbeCache<LocalInfileProbe>.MarkNegative(
                conn.ConnectionString, LocalInfileNegativeTtlMilliseconds);
            return LocalInfileProbeResult.ProbeFailed;
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
        // ITM-870（r23）：提交尝试标志——服务端错误的 COMMIT 失败跳过回滚（与 PG COPY 路径
        // 及 Core MultiValueBulkInsert 同语义，经 BulkOperationFramework 共享单一实现）
        bool commitAttempted = false;
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
            {
                commitAttempted = true;
                // ITM-910（r24）：收敛到 BulkOperationFramework 委派 Core 的单一实现
                await BulkOperationFramework.CommitWithTimeoutAsync(mySqlTransaction, commandTimeoutSeconds, ct)
                    .ConfigureAwait(false);
            }
            return inserted;
        }
        catch (Exception ex)
        {
            primaryException = ex;
            // T2/P0-32（2026-09-22）：自管事务失败时显式有界回滚，不依赖驱动 Dispose 的隐式语义。
            // 与 PG 路径（PostgreSqlProvider 的 COPY catch）同口径：隐式回滚发生在异常传播路径上
            // 且无超时上界，网络黑洞下把一次快速失败拖成永久卡死；不发起回滚则服务端事务悬置到
            // 连接归还，继续持锁与 undo 日志。显式回滚让"回滚失败"与"未尝试回滚"在诊断上可区分。
            // ITM-870（r23）：服务端错误的 COMMIT 失败跳过回滚——服务端已终止事务，
            // 回滚只会得到 "already completed" 噪音并多一次徒劳往返。
            if (ownsTransaction
                && (!commitAttempted
                    || !BulkOperationFramework.TrySkipRollbackAfterCommitFailure(ex)))
            {
                await BulkOperationFramework.RollbackPreservingAsync(
                    mySqlTransaction, ex, commandTimeoutSeconds, ct).ConfigureAwait(false);
            }
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

    // ITM-910（r24）：原私有 CommitWithTimeoutAsync 复制体（与 PG 同构）已删，
    // 调用点走 BulkOperationFramework.CommitWithTimeoutAsync（Core 单一实现）。
}
