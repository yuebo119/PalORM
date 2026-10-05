using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace PalORM.PostgreSql;

/// <summary>PostgreSQL Provider —— Npgsql 适配 + JSONB/NOTIFY/Binary COPY。
/// <para><b>调优配方（均非默认，须用户显式启用，README「PostgreSQL 进阶配方」表同款）</b>：
/// ① 本机 PG 用 Unix domain socket（<c>Host=/var/run/postgresql</c>）；
/// ② GSS 协商长尾削峰（<c>GssEncryptionMode=Disable</c>，探针实测长尾 149ms→≤21ms、中位不变，
/// 属安全策略变更）；
/// ③ 非关键表批量写在事务内首条 <c>SET LOCAL synchronous_commit TO off</c>（PG 官方 28.4，
/// 风险窗 ≈600ms 崩溃丢提交，账务类不可）。
/// <c>NoResetOnClose=true</c> 的会话状态泄漏取舍见 ITM-652（raw SQL 的 SET/临时表跨池租客
/// 可见，需隔离时用独立连接或显式 <c>DISCARD</c>）。</para></summary>
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
    /// <para><b>判断策略说明（PROV-001，2026-09-23 修订）</b>：判据 = 连接串未显式给出该键
    /// （见 <see cref="HasExplicitKey"/>，按 <c>Keys</c> 集合判定）<b>且</b>属性当前值
    /// 等于驱动默认值。前者管用户意图（显式设成默认值不再被静默改写），后者管驱动默认值漂移
    /// （未来驱动改默认时仍能识别"未设置"）。键名经 Npgsql 10.0.3 探针实测——注意规范键是
    /// "Maximum Pool Size" 而非 "Max Pool Size"，写错会静默失效。
    /// <b>布尔旋钮边界（审计 PROV-011）</b>：NoResetOnClose/Enlist 这类布尔参数显式给出时
    /// 不再被改写（此前"显式 true 与默认不可区分"会导致会话状态泄漏 ITM-652 与环境事务
    /// 脱离 ITM-643）。</para></summary>
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // PG-6（2026-09-27）：改写结果按全部输入缓存——本方法的输入只有 (连接串, 4 个
        // options 池参数字段)（旋钮清单只读 options.MaxPoolSize/MinPoolSize/
        // PoolIdleTimeoutSeconds/PoolLifetimeMinutes，其余为常量）。实测（探针十二）
        // 单次 CreateConnection 的固定成本 = 连接串解析 9.44µs + 10× Keys.Contains
        // 旋钮扫描 2.46µs ≈ 12µs；每操作自建会话的用法（DataSession.CreateAsync per op，
        // README 快速Start 形态）全额支付它——会话税 24µs 的近一半。缓存命中后
        // CreateConnection 只剩字典查询 + new NpgsqlConnection（0.09µs）。
        string rewritten = RewrittenConnectionStringCache.GetOrAdd(
            (connectionString, options.MaxPoolSize, options.MinPoolSize,
                options.PoolIdleTimeoutSeconds, options.PoolLifetimeMinutes),
            static (key, opts) => RewriteConnectionString(key.ConnectionString, opts),
            options);
        return new NpgsqlConnection(rewritten);
    }

    /// <summary>PG-6：改写结果的缓存——键 = 本方法全部输入（连接串 + 4 个 options 池参数字段）。
    /// 键空间上限 = 连接串变体数 × 池参数组合数（应用内通常个位数），与
    /// <see cref="QuotedInsertTargetCache"/> 同族的有限键集豁免。工厂失败（非法连接串）
    /// 不缓存，异常照旧传播。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string ConnectionString, int MaxPoolSize, int MinPoolSize, int PoolIdleTimeoutSeconds, int PoolLifetimeMinutes), string>
        RewrittenConnectionStringCache = new();

    /// <summary>PG-6：连接串旋钮改写的实际逻辑（从 CreateConnection 原样抽出，逻辑零变更）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S3776:CognitiveComplexity",
        Justification = "PG-6：连接串调优旋钮改写（原 CreateConnection 主体原样抽出）——线性旋钮清单，（池参数 + 预编译/缓冲/环境事务），"
            + "复杂度来自旋钮数量而非嵌套；PROV-001 起每个旋钮多一个显式键判定条件。"
            + "拆分会把 ITM-612 的'单点覆盖口径'打散到多处，反而增加漂移风险。")]
    private static string RewriteConnectionString(string connectionString, DbOptions options)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        // ITM-612：池参数遵循下方系列的"仅默认时覆盖"策略——原对象初始化器在连接串解析后
        // 无条件覆盖，连接串内嵌 "Max Pool Size=500" 被静默改写为 DbOptions 默认值。
        // Npgsql 10 实测默认值：MaxPoolSize=100 / ConnectionIdleLifetime=300 / ConnectionLifetime=3600
        //（注意 Lifetime 默认非 0——曾按 0 写判据致 WithPool 值永不应用，实证修正）。
        // PROV-001（2026-09-23）：判据补 ContainsKey（键集只含显式出现的键）——"值 == 驱动默认"
        // 无法区分"用户没设"与"用户显式设成默认值"（后者会被静默改写，用户意图落空）。
        // 两个条件同时保留：ContainsKey 管用户意图，值比对管驱动默认值漂移（未来驱动改默认时
        // 仍能识别"未设置"）。键名经 Npgsql 10.0.3 探针实测（注意规范键是 "Maximum Pool Size"，
        // 不是 "Max Pool Size"——写错会静默失效）。
        if (!HasExplicitKey(builder, "Maximum Pool Size") && builder.MaxPoolSize == 100)
            builder.MaxPoolSize = options.MaxPoolSize;
        // C4（v5.6.0）：空闲保留下限——0 = 不覆盖（Npgsql 默认 0）。>0 时空闲修剪
        // （ConnectionIdleLifetime 到期）至少保留这么多条连接，避免稀疏流量清池后
        // 突发查询重建物理连接（远程建连实测 ~8.5 ms/条）。
        if (options.MinPoolSize > 0 && !HasExplicitKey(builder, "Minimum Pool Size") && builder.MinPoolSize == 0)
            builder.MinPoolSize = options.MinPoolSize;
        // v5.6：0 = 不覆盖（保留 Npgsql 默认 300 秒）——原默认 30 秒把驱动的空闲超时砍到 1/10，
        // 间隔超过 30 秒的首次查询须重建物理连接（跨网段实测多付 13.2 ms）。
        if (options.PoolIdleTimeoutSeconds > 0 && !HasExplicitKey(builder, "Connection Idle Lifetime")
            && builder.ConnectionIdleLifetime == 300)
        {
            builder.ConnectionIdleLifetime = options.PoolIdleTimeoutSeconds;
        }
        if (!HasExplicitKey(builder, "Connection Lifetime") && builder.ConnectionLifetime == 3600)
            builder.ConnectionLifetime = checked(options.PoolLifetimeMinutes * 60);

        // v5.0 阶段 3.1：仅当属性当前值等于 ADO.NET 默认值（且连接串未显式给出该键）时覆盖为调优推荐值。
        // Npgsql 默认值（已通过 ConnectionStringBuilder 属性默认核实）：MaxAutoPrepare=0，
        // AutoPrepareMinUsages=5，NoResetOnClose=false，ReadBufferSize/WriteBufferSize=8192，Enlist=true。
        if (!HasExplicitKey(builder, "Max Auto Prepare") && builder.MaxAutoPrepare == 0)
            builder.MaxAutoPrepare = 100;
        if (!HasExplicitKey(builder, "Auto Prepare Min Usages") && builder.AutoPrepareMinUsages == 5)
            builder.AutoPrepareMinUsages = 2;
        // ITM-652(r4) 登记：NoResetOnClose=true 使归池连接跳过 DISCARD ALL——经 raw SQL
        // 的 SET/临时表/会话状态会泄漏给下一个池租客（吞吐收益的既定取舍）。需要会话
        // 状态隔离的场景请用独立连接（连接串 Max Pool Size=1）或显式 DISCARD。
        if (!HasExplicitKey(builder, "No Reset On Close") && !builder.NoResetOnClose)
            builder.NoResetOnClose = true;
        if (!HasExplicitKey(builder, "Read Buffer Size") && builder.ReadBufferSize == 8192)
            builder.ReadBufferSize = 16384;
        if (!HasExplicitKey(builder, "Write Buffer Size") && builder.WriteBufferSize == 8192)
            builder.WriteBufferSize = 16384;
        // ITM-643(r4) 登记：显式 Enlist=true 与默认 true 此前不可区分（同 ADR-G 的
        // AllowLoadLocalInfile 形态）——依赖 TransactionScope 的用户被静默脱离环境事务。
        // PROV-001（2026-09-23）：ContainsKey 现已可区分（键集只含显式出现的键），显式
        // Enlist=true 不再被改写。缓解措施仍适用：环境事务场景请用 BeginTransaction 显式事务。
        if (!HasExplicitKey(builder, "Enlist") && builder.Enlist)
            builder.Enlist = false;

        return builder.ConnectionString;
    }

    /// <summary>连接串是否显式给出某键（PROV-001，2026-09-23）。
    /// <b>不能用</b> <c>NpgsqlConnectionStringBuilder.ContainsKey</c>——Npgsql 10.0.3 实测对
    /// <b>未设置</b>的已知关键字同样返回 true（<c>Keys.Count</c> 只含显式键，但 ContainsKey
    /// 认的是"已知关键字"），用它做判据会静默关闭全部调优。改用 <c>Keys</c> 集合
    /// （只含显式出现的键）做大小写不敏感匹配。冷路径（每会话一次），LINQ 形式可接受。</summary>
    private static bool HasExplicitKey(NpgsqlConnectionStringBuilder builder, string keyword)
        => builder.Keys.Contains(keyword, StringComparer.OrdinalIgnoreCase);

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
    /// 实现 static abstract 成员（IDbProvider 不提供默认实现——CS8926，见接口注释）。ITM-871（r23）：订正 doc 残留（原写"覆盖接口默认实现"）。</summary>
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    /// <summary>PostgreSQL 原生支持 RETURNING 子句。</summary>
    public static bool SupportsReturningClause => true;

    /// <summary>CURRENT_TIMESTAMP——注意 PG 返回会话时区时间(与 SQLite 的恒 UTC 语义不同,ITM-326)。</summary>
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    /// <summary>SQLSTATE 23505 unique_violation——唯一约束冲突。
    /// R4（v6.0）：对统一翻译异常幂等（UniqueConstraintViolationException 的 Inner SqlState 为 23505）。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is UniqueConstraintViolationException
            or PostgresException { SqlState: "23505" };

    /// <summary>并发建表竞态的幂等信号（2026-09-28 实证）：CREATE TABLE IF NOT EXISTS 的
    /// 存在性检查与系统表插入非原子——多会话并发 MigrateAsync 建同名表时，后到者撞
    /// <c>pg_type_typname_nsp_index</c>（随行复合类型）或 <c>pg_class_relname_nsp_index</c>
    ///（关系行）两个系统表唯一索引之一（23505，两形态均实测）。两者仅在并发建表窗口可达，
    /// 等价于"对方已建成同表"= IF NOT EXISTS 的期望结果（MySQL 1061 索引兜底同族）。
    /// MigrateAsync 的建表批据此回退逐条执行。</summary>
    public static bool IsDuplicateSchemaObject(Exception exception)
        => exception is PostgresException
        {
            SqlState: "23505",
            ConstraintName: "pg_type_typname_nsp_index" or "pg_class_relname_nsp_index"
        };

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
        // 仅对已知基元显式映射——未知类型留给驱动推断（保持既有行为）。
        // C1（2026-10-01 全 API 逐项轮）：case 频率重排——int/long/string/Guid 等高频类型前置
        // （原顺序下 string 至多 13 次、Guid 17 次类型测试才命中）；各 case 为互斥的封闭类型
        // 模式（无类型包含关系），重排纯语义等价。
        switch (value)
        {
            case int: parameter.DbType = System.Data.DbType.Int32; break;
            case long: parameter.DbType = System.Data.DbType.Int64; break;
            case string: parameter.DbType = System.Data.DbType.String; break;
            case Guid: parameter.DbType = System.Data.DbType.Guid; break;
            case bool: parameter.DbType = System.Data.DbType.Boolean; break;
            case DateTime: parameter.DbType = System.Data.DbType.DateTime; break;
            case decimal: parameter.DbType = System.Data.DbType.Decimal; break;
            case double: parameter.DbType = System.Data.DbType.Double; break;
            case short: parameter.DbType = System.Data.DbType.Int16; break;
            case byte: parameter.DbType = System.Data.DbType.Byte; break;
            case sbyte: parameter.DbType = System.Data.DbType.SByte; break;
            case ushort: parameter.DbType = System.Data.DbType.UInt16; break;
            case uint: parameter.DbType = System.Data.DbType.UInt32; break;
            case ulong: parameter.DbType = System.Data.DbType.UInt64; break;
            case float: parameter.DbType = System.Data.DbType.Single; break;
            case char: parameter.DbType = System.Data.DbType.String; break;
            case DateTimeOffset: parameter.DbType = System.Data.DbType.DateTimeOffset; break;
            default: break;  // 未知类型留给驱动推断（保持既有行为）
        }
        return parameter;
    }

    /// <summary>由调用方给出的**显式元素类型**创建数组参数（UNNEST 阶段 B：批量 UPDATE/UPSERT 用）。
    /// <para>与 <see cref="CreateArrayParameter"/> 的差异：后者从数组运行时类型推断元素类型
    /// （阶段 A 的单键数组够用）；本形态下调用方已由生成物拿到列的元素类型
    /// （<c>UpdateColumnArrayElementTypes</c>），直接给出，省一次推断且能覆盖
    /// "元素类型为引用型但数组已建好"的形态。</para>
    /// <para>元素类型为 <c>Nullable&lt;T&gt;</c> 时按 <c>T</c> 映射（PG 数组本身即承载 NULL）。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations",
        Justification = "Npgsql 官方 XML 文档对 NpgsqlDbType.Array 明确要求按位或组合"
            + "（\"This value must be combined with another value from NpgsqlDbType via a bit OR "
            + "(e.g. NpgsqlDbType.Array | NpgsqlDbType.Integer)\"），枚举未标 [Flags] 是驱动侧的标注疏漏。")]
    public static DbParameter? CreateTypedArrayParameter(string name, Array values, Type elementType)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(elementType);
        Type underlying = Nullable.GetUnderlyingType(elementType) ?? elementType;
        if (ArrayElementDbType(underlying) is not { } element)
        {
            return null;  // 不支持的 CLR 元素类型：调用方回退 VALUES 形态
        }

        return new NpgsqlParameter(name, values) { NpgsqlDbType = NpgsqlDbType.Array | element };
    }

    /// <summary>创建数组参数（2026-10-02，UNNEST 形态）——<c>NpgsqlDbType.Array | element</c>。
    /// <para><b>元素类型由数组本身推断</b>（<paramref name="values"/> 的运行时元素类型）：
    /// 生成物构造的是类型化数组（<c>long[]</c>/<c>string[]</c>/…），元素类型即参数类型，
    /// 无需调用方另传 DbType——平行映射表会与生成物元素类型构成第二个真源（B120 同构温床）。</para>
    /// <para><b>为什么必须显式类型</b>：数组参数的类型推断在空数组上无从进行（探针实测：
    /// 不设类型时绑定失败）；显式 <c>Array | element</c> 让空数组与有值形态走同一条路径。
    /// 注意此处用的是 C# 元素 <b>Type</b>（不是泛型 <c>NpgsqlParameter&lt;T&gt;</c>），
    /// <c>values.GetType().GetElementType()</c> 对空数组同样返回元素类型——类型来自数组的
    /// 编译期类型，不来自元素值，故空数组无歧义。</para>
    /// <para><b>值形态</b>：接受 <see cref="Array"/>（含 <c>long[]</c>/<c>string[]</c> 等），
    /// 驱动按元素类型逐项绑定。</para>
    /// <para><b>null 语义</b>：不支持的 CLR 元素类型返回 null，调用方回退 IN 占位符形态
    /// （不静默发一个类型未知的数组参数——那会以服务端推断错误的形式在远端失败，
    /// 错误消息不指向真正的原因）。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations",
        Justification = "Npgsql 官方 XML 文档对 NpgsqlDbType.Array 明确要求按位或组合"
            + "（\"This value must be combined with another value from NpgsqlDbType via a bit OR "
            + "(e.g. NpgsqlDbType.Array | NpgsqlDbType.Integer)\"），枚举未标 [Flags] 是驱动侧的标注疏漏。")]
    public static DbParameter? CreateArrayParameter(string name, Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Type elementType = values.GetType().GetElementType() ?? typeof(object);
        if (ArrayElementDbType(elementType) is not { } element)
        {
            return null;  // 不支持的 CLR 元素类型：调用方回退 IN 占位符形态
        }

        var parameter = new NpgsqlParameter(name, values) { NpgsqlDbType = element };
        parameter.NpgsqlDbType = NpgsqlDbType.Array | element;
        return parameter;
    }

    /// <summary>数组元素 CLR 类型 → 驱动的元素 <see cref="NpgsqlDbType"/>。null = 不支持数组形态。
    /// <para><b>映射面是封闭集合，且刻意收窄</b>（登记，2026-10-02）：恰好 13 类——int / long /
    /// short / byte / string / Guid / bool / decimal / double / float / DateTime / DateTimeOffset /
    /// DateOnly / TimeOnly / byte[]。**未列出的类型（含 <c>uint</c>/<c>ulong</c>/<c>sbyte</c>/
    /// <c>ushort</c>/<c>char</c>/<c>TimeSpan</c>/枚举等）返回 null**，调用方据此整体回退
    /// VALUES 形态（能力检测在 BEGIN 之前一次性判定全部列，任一列不支持即回退整条路径，
    /// 不做「部分列走数组」的混合）。</para>
    /// <para><b>回退是静默的</b>（无日志无计数器）——这是能力检测纪律的既定取舍：形态选择不改变
    /// 语义，只是慢一些。含 <c>uint</c> 等列的实体因此拿不到数组形态收益，属已知边界；
    /// 扩展本映射面须先实测该类型在 PG 端数组绑定的行为（探针 mergearray 的 Q1 形态可复用），
    /// 不得凭 CLR 类型相似就顺手加。</para>
    /// <para>可空元素类型（<c>long?</c>）经 <c>Nullable.GetUnderlyingType</c> 解包——
    /// 数组元素是 <c>T?</c> 时运行时元素类型即 <c>Nullable&lt;T&gt;</c>。</para></summary>
    private static NpgsqlDbType? ArrayElementDbType(Type elementType)
    {
        Type underlying = Nullable.GetUnderlyingType(elementType) ?? elementType;
        if (underlying == typeof(int)) return NpgsqlDbType.Integer;
        if (underlying == typeof(long)) return NpgsqlDbType.Bigint;
        if (underlying == typeof(short)) return NpgsqlDbType.Smallint;
        if (underlying == typeof(byte)) return NpgsqlDbType.Smallint;
        if (underlying == typeof(string)) return NpgsqlDbType.Text;
        if (underlying == typeof(Guid)) return NpgsqlDbType.Uuid;
        if (underlying == typeof(bool)) return NpgsqlDbType.Boolean;
        if (underlying == typeof(decimal)) return NpgsqlDbType.Numeric;
        if (underlying == typeof(double)) return NpgsqlDbType.Double;
        if (underlying == typeof(float)) return NpgsqlDbType.Real;
        if (underlying == typeof(DateTime)) return NpgsqlDbType.Timestamp;
        if (underlying == typeof(DateTimeOffset)) return NpgsqlDbType.TimestampTz;
        if (underlying == typeof(DateOnly)) return NpgsqlDbType.Date;
        if (underlying == typeof(TimeOnly)) return NpgsqlDbType.Time;
        if (underlying == typeof(byte[])) return NpgsqlDbType.Bytea;
        return null;
    }

    /// <summary>批量插入——按源生成 InsertColumns 与 BindInsert 执行 Npgsql Binary COPY。
    /// <para><paramref name="batchSize"/> 即每次 COPY 会话的行数：Binary COPY 无参数上限，
    /// 远程库建议传整段行数（单次协议往返）——小批（如多值 VALUES 思维的 1000）会让
    /// 20000 行付出 20 次 BeginBinaryImport/Complete 往返（实测比值 3.3× 量级）。</para>
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
        // v5.3：源生成器保证 binder 合法，probe 只需首次验证。
        // R50（2026-09-26）：首次并发经 Lazy<Task> 单实例化——原无锁双检让同 (Type, Dialect)
        // 的并发首触各建一次探测命令（binder 绑定 + 参数数校验 + Dispose，无 I/O）。
        // Lazy(ExecutionAndPublication) 对同键只跑一次，其余并发首触等同一个 Task。
        // 失败即摘除缓存项：Lazy 缓存已完成的 Task（含故障），不摘除会把一次 binder 缺陷
        // 固化为"该类型永不再验证/向后续调用者重放故障"。
        if (!metadata.InsertBinderValidated)
        {
            (Type, SqlDialect) probeKey = (typeof(T), Dialect);
            Lazy<Task> probe = InsertProbeLazyCache.GetOrAdd(
                probeKey,
                static (_, state) => new Lazy<Task>(
                    () => BulkOperationFramework.ProbeBinderAsync(
                        state.Connection, state.Binder, state.First, state.ColumnCount, state.TypeName,
                        "PalORM.ProbeCommandCleanupException", CancellationToken.None).AsTask(),
                    System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                (Connection: (DbConnection)npgsqlConnection, Binder: binder, First: entities[0],
                 ColumnCount: columnCount, TypeName: typeof(T).Name));
            try
            {
                await probe.Value.ConfigureAwait(false);
            }
            catch
            {
                InsertProbeLazyCache.TryRemove(probeKey, out _);
                throw;
            }
        }

        // B3：引号后的表名与列清单只由 (Type, Dialect) 决定，是纯函数——原每次调用重算
        // （方法组转委托 + LINQ 迭代器 + string.Join 中间数组 + 每列一次 QuoteIdentifier）。
        // B18（2026-10-01）：COPY 命令文本同缓存，每批不再插值。
        (_, _, string copyCommand) = GetQuotedInsertTarget(typeof(T));
        long total = 0;
        DbTransaction bulkTransaction = transaction
            ?? await npgsqlConnection.BeginTransactionAsync(isolationLevel, ct).ConfigureAwait(false);  // r6-N2
        bool ownsTransaction = transaction is null;
        Exception? primaryException = null;
        // ITM-870（r23）：提交尝试标志——COMMIT 已尝试且失败形态为服务端错误时跳过回滚
        //（服务端已终止事务，回滚只会得到 "already completed" 噪音 + 一次徒劳往返），
        // 与 Core MultiValueBulkInsert 的 TrySkip 裁决同语义（经 BulkOperationFramework 共享）。
        bool commitAttempted = false;
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
                // O1（2026-10-01）：定型 COPY 行写入器——生成器仅在全部可插入列的 provider
                // 类型可直写（IBinaryRowSink 支持面）时发射；null 回退参数池路径（零行为变化）。
                // 行循环走定型直写后，每行不再付值类型装箱（S1Row 形态 104 B/行）。
                Action<IBinaryRowSink, object>? copyWriter = metadata.CopyWriteRow;
                // 池的引用数组：参数对象仍留在 rowCommand.Parameters 内——
                // SampleColumnTypes 从集合读每列 NpgsqlDbType（PG-4），脱离集合会丢失类型来源。
                DbParameter[]? pool = null;
                // PG-4：每列 NpgsqlDbType 的调用级缓存（首行绑定后采样一次）
                NpgsqlDbType[]? columnTypes = null;
                // O1：copyWriter 路径把"首行建池 + 类型采样"提升到批循环外——类型真源仍是
                // 参数 DbType 采样（PG-4 单一来源不变），行循环只走定型直写。
                // B17（2026-10-01 全 API 逐项轮）：类型数组按实体类型缓存——O1 路径的
                // "建池 + 采样"只为 NpgsqlDbType 真源服务（行循环走定型直写不读池）；
                // 原实现每次 BulkInsert 调用都付 columnCount 个参数对象 + 装箱 + 采样读，
                // 缓存命中后整段跳过。同一 Type 的列集在进程内恒定（Register 拒绝重复注册，
                // 热重载不改变类型级元数据），校验降频与 B9（probe 结果缓存）同口径。
                if (copyWriter is not null && valuesBinder is not null)
                {
                    columnTypes = GetOrBuildColumnTypes<T>(rowCommand, binder, entities[0], columnCount);
                }
                for (int start = 0; start < entities.Count; start += batchSize)
                {
                    int end = Math.Min(start + batchSize, entities.Count);
                    // ITM-643：每次 COPY 一个独立超时窗口（对齐 ADO.NET 每命令超时语义，非整批累计）。
                    // B19（2026-10-01）：timeoutCts 可为 null（无限等待 + 调用方不可取消）——
                    // commandCt 回落 ct（不可取消 token 的 Register 为零开销空注册）。
                    CancellationTokenSource? timeoutCts =
                        CreateCopyTimeoutTokenSource(commandTimeoutSeconds, ct);
                    try
                    {
                        CancellationToken commandCt = timeoutCts?.Token ?? ct;
                        // ITM-760：注册回调记录超时触发（回调先于 OCE 抛出点的传播）
                        using CancellationTokenRegistration reg = commandCt.Register(
                            static state => ((bool[])state!)[0] = true, timeoutFlag);
                        NpgsqlBinaryImporter importer = await npgsqlConnection.BeginBinaryImportAsync(
                            copyCommand, commandCt)
                            .ConfigureAwait(false);
                        Exception? importerException = null;
                        try
                        {
                            // O1：每 COPY 批一个定型行槽（importer + 采样类型数组）；仅
                            // copyWriter 与采样结果同时就绪时启用。
                            PgCopyRowSink? sink =
                                copyWriter is not null && columnTypes is not null
                                    ? new PgCopyRowSink(importer, columnTypes)
                                    : null;
                            for (int index = start; index < end; index++)
                            {
                                if (sink is not null)
                                {
                                    // 行边界取消检查 + StartRow 收进同步助手（与 WriteRow 同形，
                                    // CA1849 只在异步上下文触发）；copyWriter 的非空性由 sink 的
                                    // 创建条件保证（与 columnTypes! 同款流分析豁免）
                                    StartCopyRow(importer, commandCt);
                                    copyWriter!(sink, entities[index]);
                                    total++;
                                    continue;
                                }
                                if (valuesBinder is not null && pool is not null)
                                {
                                    valuesBinder(pool, entities[index], 0);
                                }
                                else
                                {
                                    // PARAM-REUSE-OK[noautoprep] 该回退仅旧模型程序集（无 valuesBinder）
                                    // 可达；PG 对应路径的语句为 Binary COPY/ODku，不经参数集合执行
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
                                    // PG-4：类型按列采样一次（含 valuesBinder 为 null 的 legacy 回退路径）
                                    columnTypes ??= SampleColumnTypes(rowCommand, columnCount);
                                }

                                // P1：满批路径直传 pool——原实现经 rowCommand.Parameters 索引器
                                // 取值，每行每列一次跨接口虚调用 + 一次硬转型（10 万行 × 10 列
                                // = 100 万次），而 pool 与 Parameters 持有的是同一批对象。
                                WriteRow(importer, rowCommand, pool, columnTypes!, columnCount, commandCt);
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
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested
                        && timeoutCts is not null && timeoutCts.IsCancellationRequested)
                    {
                        // ITM-869（r23）：超时触发的状态补记——Cancel() 先置 token 状态再执行注册回调，
                        // WriteRow 行边界的轮询检查可在回调执行前抛 OCE（timeoutFlag 仍 false），
                        // 外层 wrapAsTimeout 判定随之失守（超时以裸 OCE 逃逸，熔断/超时分类 miss）。
                        // 本 filter 在 timeoutCts.Dispose() 前执行，状态读取时序安全。
                        timeoutFlag[0] = true;
                        throw;
                    }
                    finally
                    {
                        timeoutCts?.Dispose();
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
            {
                commitAttempted = true;
                await CommitWithTimeoutAsync(bulkTransaction, commandTimeoutSeconds, ct)
                    .ConfigureAwait(false);
            }
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
            // ITM-870（r23）：服务端错误的 COMMIT 失败跳过回滚（三 Provider 与 Core 共享同一份
            // 语义——此前 PG COPY 路径无条件回滚，与同文件注释声称不符）
            if (ownsTransaction
                && (!commitAttempted
                    || !BulkOperationFramework.TrySkipRollbackAfterCommitFailure(thrown)))
            {
                await BulkOperationFramework.RollbackPreservingAsync(bulkTransaction, thrown, commandTimeoutSeconds, ct)
                    .ConfigureAwait(false);
            }
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
    /// <summary>R49（2026-09-26）：COPY 目标引用形态缓存。<b>存 Lazy 而非裸值</b>——实测本运行时
    /// <c>ConcurrentDictionary.GetOrAdd</c> 的工厂对同键可被并发调用多次（只保证入库值唯一）：
    /// 8 线程首触实测构建 8 次。Lazy(ExecutionAndPublication) 才真正单实例化；败者的 Lazy
    /// 永不被 force（工厂不跑），胜者的 Lazy 被所有调用方共享。失败摘除见
    /// <c>GetQuotedInsertTarget</c>（Lazy 缓存故障，不摘除会把一次构建失败固化为永久异常）。
    /// 键空间 = 实体数 × 3，天然有限。
    /// <para>B18（2026-10-01 全 API 逐项轮）：值扩为三元组含 COPY 命令文本——原实现每批
    /// 1 个插值字符串（内容只由 Type 决定，恒定）。</para></summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (Type EntityType, SqlDialect Dialect),
        Lazy<(string QuotedTable, string QuotedColumns, string CopyCommand)>>
        QuotedInsertTargetCache = new();

    /// <summary>R50（2026-09-26）：INSERT binder 首次探测的每 (Type, Dialect) 单实例化
    /// （机制与失败摘除语义见 <c>BulkInsertAsync</c> 内注释）。键空间同
    /// <see cref="QuotedInsertTargetCache"/>（实体数 × 3，天然有限）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (Type EntityType, SqlDialect Dialect), Lazy<Task>> InsertProbeLazyCache = new();

    /// <summary>R49 可观测面：COPY 目标引用形态的实际构建次数（缓存未命中且抢到桶锁时 +1）。
    /// 仅测试读（Integration 并发夹具断言首次并发只构建一次）；生产路径每次 BulkInsertAsync
    /// 一次 Interlocked 增量，缓存命中为零成本。非 const/readonly 是计数器语义（S2223 抑制：
    /// 可变静态计数器是唯一能承载"构建次数"的形态，且仅 internal）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S2223",
        Justification = "构建次数计数器必须可变；仅 internal + 仅测试读，无生产可写面。")]
    internal static int QuotedTargetBuildCount;

    /// <summary>取（或构建并缓存）指定实体类型的 COPY 目标引用形态。
    /// <para><b>R49（2026-09-26）</b>：TryGetValue→build→TryAdd 形态首次并发双构建（纯函数重算
    /// 两遍）；GetOrAdd 工厂仍可被并发多次调用（实测构建 8 次）；收敛为<b>字典存 Lazy</b>——
    /// Lazy(ExecutionAndPublication) 对同键只执行一次构建，其余首触等同一个 Value。
    /// 失败即摘除：Lazy 会缓存已完成的 Task/值（含故障），不摘除会把一次构建失败固化。</para></summary>
    private static (string QuotedTable, string QuotedColumns, string CopyCommand) GetQuotedInsertTarget(Type entityType)
    {
        (Type EntityType, SqlDialect Dialect) key = (entityType, Dialect);
        Lazy<(string QuotedTable, string QuotedColumns, string CopyCommand)> lazy = QuotedInsertTargetCache.GetOrAdd(
            key,
            static (k, _) => new Lazy<(string QuotedTable, string QuotedColumns, string CopyCommand)>(
                () => BuildQuotedTarget(k.EntityType),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
            (object?)null);
        try
        {
            return lazy.Value;
        }
        catch
        {
            QuotedInsertTargetCache.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>构建 COPY 目标引用形态（引号表名 + 引号列清单 + COPY 命令文本）——R49 计数面
    /// 同处递增。B18（2026-10-01）：COPY 文本一并构建缓存。</summary>
    private static (string QuotedTable, string QuotedColumns, string CopyCommand) BuildQuotedTarget(Type entityType)
    {
        string tableName = PalORM_Runtime.TableNames.TryGetValue(entityType, out string? tn)
            ? tn
            : throw new InvalidOperationException(
                $"Type '{entityType.Name}' has no generated table metadata.");
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(entityType, out CrudMetadata crud))
            throw new InvalidOperationException(
                $"Type '{entityType.Name}' has no generated CRUD.");
        System.Threading.Interlocked.Increment(ref QuotedTargetBuildCount);
        string quotedTable = QuoteIdentifier(tableName);
        string quotedColumns = string.Join(", ", crud.InsertColumns.Select(QuoteIdentifier));
        return (
            quotedTable,
            quotedColumns,
            $"COPY {quotedTable} ({quotedColumns}) FROM STDIN (FORMAT BINARY)");
    }

    /// <summary>创建单次 COPY 的超时令牌源——ITM-643：COPY 无 CommandTimeout 挂点，
    /// 联动 CTS + CancelAfter 履行"批量命令必须应用超时"契约；0 = 无限等待（不设取消），
    /// 与 DbOptions.ToCommandTimeoutSeconds 的 Zero 透传语义一致。
    /// <para>B19（2026-10-01 全 API 逐项轮）：免构/单构——无限等待且不可取消时无超时事件
    /// 可发生，返回 null（调用方直接用 ct；不可取消 token 的 Register 是零开销空注册）；
    /// ct 不可取消时单构 CTS 替代 linked（省 registration 结构）。</para></summary>
    private static CancellationTokenSource? CreateCopyTimeoutTokenSource(
        int commandTimeoutSeconds, CancellationToken ct)
    {
        if (commandTimeoutSeconds <= 0 && !ct.CanBeCanceled)
            return null;
        var timeoutCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        if (commandTimeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(commandTimeoutSeconds));
        return timeoutCts;
    }

    /// <summary>把单行参数写入 PG Binary importer——DBNull 转换为 null 让 importer 用列默认类型。
    /// <para>P1：<paramref name="pool"/> 非 null（满批复用路径）时直接索引取值，避开
    /// <see cref="DbParameterCollection"/> 索引器的跨接口虚调用与硬转型；池尚未建立
    /// （首行）或旧版生成器回退路径下走 rowCommand.Parameters。</para>
    /// <para><b>PROV-002（2026-09-23）</b>：行内改<b>同步</b> Write/StartRow——19 列 × 10 万行原先付
    /// 190 万次 async 状态机（每次只做缓冲区写入，无真实异步 IO）；Npgsql 对 COPY 的建议同样是
    /// CPU 密集段用同步 API（API 面已核对本机 npgsql/10.0.3 包 XML：<c>Write&lt;T&gt;(T, NpgsqlDbType)</c>
    /// 与 <c>StartRow()</c> 存在）。取消检查移到行边界，粒度从"每列"变为"每行"。</para>
    /// <para><b>PG-4（2026-09-26）</b>：每列 NpgsqlDbType 由 <paramref name="columnTypes"/>
    /// 每次调用采样一次后传入——类型是列属性不是单元格值属性，原每单元格读
    /// <c>NpgsqlParameter.NpgsqlDbType</c> getter（探针：设 DbType 后仍 31ns/次，20000 行 × 13 列
    /// ≈ 8ms，占 COPY 路径剩余耗时约四分之一）。</para></summary>
    private static void WriteRow(
        NpgsqlBinaryImporter importer, DbCommand rowCommand, DbParameter[]? pool,
        NpgsqlDbType[] columnTypes, int columnCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        importer.StartRow();
        for (int parameterIndex = 0; parameterIndex < columnCount; parameterIndex++)
        {
            var parameter = (NpgsqlParameter)(pool is not null
                ? pool[parameterIndex]
                : rowCommand.Parameters[parameterIndex]);
            object? value = parameter.Value is DBNull ? null : parameter.Value;
            importer.Write(value, columnTypes[parameterIndex]);
        }
    }

    /// <summary>PG-4：每次 COPY 调用采样一次每列 NpgsqlDbType（见 <see cref="WriteRow"/>）。
    /// <para>S3 的显式 DbType 保证首行全 DBNull 的可空列也返回映射类型而非 <c>Unknown</c>
    /// （ITM-527 修复：探针实测 <c>DBNull + DbType.Int32 → Integer</c>，真库执行验证通过），
    /// 因此首行采样对任何列组合都成立。</para>
    /// <para>B17（2026-10-01）：本函数现仅作为 <see cref="GetOrBuildColumnTypes{T}"/> 的
    /// 首次构建真源（缓存命中后不再执行）。</para></summary>
    private static NpgsqlDbType[] SampleColumnTypes(DbCommand rowCommand, int columnCount)
    {
        var columnTypes = new NpgsqlDbType[columnCount];
        for (int column = 0; column < columnCount; column++)
            columnTypes[column] = ((NpgsqlParameter)rowCommand.Parameters[column]).NpgsqlDbType;
        return columnTypes;
    }

    /// <summary>B17（2026-10-01 全 API 逐项轮）：COPY 列类型数组缓存——键 = 实体类型
    /// （类型由生成器静态决定，同一进程内恒定；PG-4/S3 的"binder 显式 DbType → 采样"
    /// 真源仅在首次构建时执行一次）。键空间 = 实体数，天然有限。并发首次构建可重复
    /// （纯函数、同值），与 DataSessionCache 的 TryGetValue/TryAdd 纪律一致。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, NpgsqlDbType[]>
        CopyColumnTypesCache = new();

    /// <summary>B17：取（或首建）列类型数组——未命中时走原"清参 + binder + 参数数校验 +
    /// 采样"路径（首行实体仅用于本次构建）。</summary>
    private static NpgsqlDbType[] GetOrBuildColumnTypes<T>(
        DbCommand rowCommand, Action<DbCommand, object, int> binder, T firstEntity, int columnCount)
        where T : class
    {
        if (CopyColumnTypesCache.TryGetValue(typeof(T), out NpgsqlDbType[]? cached))
            return cached;

        // PARAM-REUSE-OK[carrier] rowCommand 仅用于采样列类型，从不执行（类型按类型缓存）
        rowCommand.Parameters.Clear();
        binder(rowCommand, firstEntity, 0);
        if (rowCommand.Parameters.Count != columnCount)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' generated {columnCount} insert columns but " +
                $"{rowCommand.Parameters.Count} parameters.");
        NpgsqlDbType[] built = SampleColumnTypes(rowCommand, columnCount);
        CopyColumnTypesCache.TryAdd(typeof(T), built);
        return built;
    }

    /// <summary>O1：定型直写路径的行起始——同步形态与 <see cref="WriteRow"/> 同形
    /// （PROV-002：CPU 密集段用同步 API；CA1849 只在异步上下文触发），行边界取消检查
    /// 保持 ITM-643/869 语义（每行一次轮询）。</summary>
    private static void StartCopyRow(NpgsqlBinaryImporter importer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        importer.StartRow();
    }

    /// <summary>O1（2026-10-01）：Binary COPY 定型行槽——把 <see cref="IBinaryRowSink"/> 的
    /// 定型直调转接为 <see cref="NpgsqlBinaryImporter.Write{T}(T, NpgsqlDbType)"/> 泛型重载，
    /// 值类型单元格不再经 DbParameter.Value 装箱中转（探针账目：S1Row 形态 104 B/行装箱，
    /// 2000 行差额 208KB 与 PerfHub 实测 BulkInsert 分配差精确吻合）。
    /// <para>线类型真源不变：构造时传入按列采样的 NpgsqlDbType 数组（PG-4 单一来源），
    /// 每次写入下标取类型——与参数池路径读 NpgsqlParameter.NpgsqlDbType 同值同语义。</para></summary>
    private sealed class PgCopyRowSink(NpgsqlBinaryImporter importer, NpgsqlDbType[] columnTypes) : IBinaryRowSink
    {
        private readonly NpgsqlBinaryImporter _importer = importer;
        private readonly NpgsqlDbType[] _columnTypes = columnTypes;

        /// <inheritdoc />
        public void WriteNull(int ordinal)
            => _importer.Write((object?)null, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteInt64(int ordinal, long value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteInt32(int ordinal, int value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteInt16(int ordinal, short value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteDecimal(int ordinal, decimal value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteDouble(int ordinal, double value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteSingle(int ordinal, float value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteBoolean(int ordinal, bool value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteGuid(int ordinal, Guid value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteDateTime(int ordinal, DateTime value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteDateTimeOffset(int ordinal, DateTimeOffset value)
            => _importer.Write(value, _columnTypes[ordinal]);

        /// <inheritdoc />
        public void WriteString(int ordinal, string? value)
        {
            if (value is null)
            {
                _importer.Write((object?)null, _columnTypes[ordinal]);
                return;
            }
            _importer.Write(value, _columnTypes[ordinal]);
        }

        /// <inheritdoc />
        public void WriteBytes(int ordinal, byte[]? value)
        {
            if (value is null)
            {
                _importer.Write((object?)null, _columnTypes[ordinal]);
                return;
            }
            _importer.Write(value, _columnTypes[ordinal]);
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

    // ITM-412 防漂移锚点（r22 收敛）：有界回滚与 DisposePreservingAsync 同族，已抽到
    // BulkOperationFramework 单一实现（跨程序集 public 入口，委派 Core 的 TransactionCleanup），
    // 三 Provider 与 Core 共享同一份语义——不再存在需要两侧同步核对的复制体。
}
