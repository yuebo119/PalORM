using System.Data.Common;
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
    /// UseCompression: 默认 false 不变，显式固定避免部署环境注入 Compress=true；
    /// CancellationTimeout: 2→5（软取消 5 秒后强制关闭，避免连接泄漏）；
    /// AllowLoadLocalInfile: false→true（v5.0 阶段 4.2 MySqlBulkCopy 前提）；
    /// ServerRedirectionMode: Disabled→Preferred（Azure MySQL 直连后端）。</para>
    /// <para><b>判断策略</b>：用"属性当前值 == ADO.NET 默认值"作为"用户未显式设置"的判据
    /// （同 PostgreSqlProvider，详见其注释）。</para></summary>
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            MaximumPoolSize = checked((uint)options.MaxPoolSize),
            ConnectionIdleTimeout = checked((uint)options.PoolIdleTimeoutSeconds),
            ConnectionLifeTime = checked((uint)(options.PoolLifetimeMinutes * 60))
        };

        // v5.0 阶段 3.2：仅当属性当前值等于 ADO.NET 默认值时覆盖为调优推荐值。
        // MySqlConnector 默认值：AutoEnlist=true，ConnectionReset=true，UseCompression=false，
        // CancellationTimeout=2，AllowLoadLocalInfile=false，ServerRedirectionMode=Disabled。
        if (builder.AutoEnlist)
            builder.AutoEnlist = false;
        if (builder.ConnectionReset)
            builder.ConnectionReset = false;
        // UseCompression 默认 false 即目标值，无需改——仅防注入：用户若显式 true 不覆盖
        if (builder.CancellationTimeout == 2)
            builder.CancellationTimeout = 5;
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
        return $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
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
        command.CommandText = $"SHOW COLUMNS FROM {QuoteQualifiedIdentifier(schema, tableName)}";
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
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds, CancellationToken ct)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(entities);
        // batchSize 校验优先于 entities.Count 检查——调用方契约（ProviderTests 验证）。
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
        if (entities.Count == 0) return 0;

        // local_infile 能力检测：开启走 BulkCopy（对齐 PG 永远 COPY），关闭走多值 INSERT。
        if (conn is MySqlConnection mySqlConnection && await IsLocalInfileEnabledAsync(mySqlConnection, ct).ConfigureAwait(false))
        {
            return await ExecuteBulkCopyAsync(
                mySqlConnection, transaction, entities, commandTimeoutSeconds, ct).ConfigureAwait(false);
        }

        // 回退路径：local_infile=OFF 或非 MySqlConnection，走多值 INSERT。
        return await MultiValueBulkInsert.ExecuteAsync(
            conn, transaction, entities,
            new BulkContext(
                batchSize,
                MaxParametersPerStatement: 65535,
                QuoteIdentifier, CreateParameter, commandTimeoutSeconds),
            ct).ConfigureAwait(false);
    }

    /// <summary>检测服务端 local_infile 是否开启（每次执行检测，无静态缓存，符合零全局状态原则）。
    /// BulkCopy 走 LOAD DATA LOCAL INFILE，需要 local_infile=ON（MySQL 默认 OFF）。</summary>
    private static async ValueTask<bool> IsLocalInfileEnabledAsync(MySqlConnection conn, CancellationToken ct)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SHOW VARIABLES LIKE 'local_infile'";
        using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return false;
        string value = reader.GetString(1);
        return string.Equals(value, "ON", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    /// <summary>v5.0 阶段 4.2：MySqlBulkCopy 路径。从 BulkInsertAsync 抽出以降低认知复杂度（S3776）。
    /// <para><b>事务语义</b>：调用方传入的 transaction 一并使用；未传时内部开新事务包整批。</para></summary>
    private static async Task<long> ExecuteBulkCopyAsync<T>(
        MySqlConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int commandTimeoutSeconds, CancellationToken ct)
        where T : class, new()
    {
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName)
            || metadata.InsertColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated insert metadata.");

        // MySQL schema=database；当前会话已在连接串指定的库里，表名直接引用。
        string quotedTable = QuoteIdentifier(tableName);
        // 主键列——DataTable 中放首列填 NULL（AUTO_INCREMENT 自增）。
        string? pkColumn = PalORM_Runtime.PkColumns.TryGetValue(typeof(T), out string? pk) ? pk : null;
        IReadOnlyList<string> pkColumns = pkColumn is not null ? [pkColumn] : [];

        // 事务：BulkCopy 需在事务内执行；未传时内部开新事务保证原子性。
        MySqlTransaction? mySqlTransaction = transaction as MySqlTransaction;
        bool ownsTransaction = false;
        if (mySqlTransaction is null)
        {
            mySqlTransaction = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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
                    commandTimeoutSeconds),
                ct).ConfigureAwait(false);
            // 成功路径：自管事务需显式 commit（DisposeAsync 默认 rollback）。
            if (ownsTransaction)
                await mySqlTransaction.CommitAsync(ct).ConfigureAwait(false);
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
                    mySqlTransaction, primaryException, "PalORM.TransactionCleanupException", ct)
                    .ConfigureAwait(false);
            }
        }
    }
}
