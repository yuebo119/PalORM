using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace PalORM.Sqlite;

/// <summary>SQLite Provider —— Microsoft.Data.Sqlite 适配。</summary>
public sealed class SqliteProvider : IDbProvider
{
    static SqliteProvider()
    {
        // SQLite3MC.PCLRaw.bundle 要求显式初始化（其 README 明示）；Microsoft.Data.Sqlite.Core
        // 不含自动 bundle 探测，NativeAOT/裁剪下更不能依赖反射发现（ITM-317）。
        // Init 幂等，静态构造保证首次使用前恰好执行一次。
        SQLitePCL.Batteries_V2.Init();
    }

    /// <summary>Provider 名称:SQLite。</summary>
    public static string Name => "SQLite";

    /// <summary>SQL 方言标识:<see cref="SqlDialect.Sqlite"/>。</summary>
    public static SqlDialect Dialect => SqlDialect.Sqlite;

    /// <summary>创建连接。<b>池参数在 SQLite 上被忽略</b>（不再抛异常）——依据与取舍见方法体注释。
    /// 静态构造已保证 SQLitePCL bundle 在首次使用前初始化。</summary>
    public static DbConnection CreateConnection(string connectionString, DbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // v5.6：池参数在此被忽略，不再抛 NotSupportedException。
        //
        // 原实现见 PoolExplicitlyConfigured 即抛——但该标记由 WithPool(...) 与
        // PALORM_MAX_POOL_SIZE 环境变量设置，而 DbOptions.Production(...) 内部就调用 WithPool，
        // 于是「Production 预设 + SQLite」必然在构造期失败：走预设或走环境变量的 SQLite 部署
        // 全都装不起来。（ITM-315 把「与默认值比对」改成显式标记位是对的，误的是把
        // 「无意义的配置」升级成了「不可用的部署」。）
        //
        // 忽略的依据：SQLite 是进程内嵌入式库，没有服务端连接池可供调这三个旋钮——
        // 驱动自带的池只有 Pooling=on/off 一个开关，无法表达最大连接数/空闲寿命/存活期，
        // 故 MaxPoolSize / PoolIdleTimeoutSeconds / PoolLifetimeMinutes 在 SQLite 上
        // 没有可映射的目标。它们对 PostgreSQL / MySQL 仍然生效。
        //
        // 这不是静默失效：本类型与 README 的配置项表都显式标注了这三项在 SQLite 上无效。
        return new SqliteConnection(connectionString);
    }

    /// <summary>双引号引用标识符(SQL 标准风格),内部双引号以 "" 转义。</summary>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        // ITM-584/593: NUL 会截断驱动/服务端 C 层语句；C0 控制字符族 + DEL 同样不稳。
        // 三方言共享 IdentifierSafety 守卫（替换 ITM-584 仅查 NUL 的补丁式实现）。
        IdentifierSafety.ThrowIfUnsafe(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>schema 与表名分别引用后以点连接;SQLite 中 schema 对应 ATTACH 数据库别名(main/temp/自定义)。</summary>
    public static string QuoteQualifiedIdentifier(string? schema, string identifier)
        => string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(identifier)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}";

    /// <summary>SQLite 3.35+ 支持 RETURNING 子句。</summary>
    public static bool SupportsReturningClause => true;

    /// <summary>SQLite 的 CURRENT_TIMESTAMP 恒为 UTC；MySQL/PG 为会话时区——
    /// 软删除 deleted_at 跨库混用时语义不同（ITM-326）。</summary>
    public static string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    /// <summary>SQLite 连接初始化：开启 FK 约束 + WAL 模式 + v5.0 阶段 3.3 写入/读取 PRAGMA 调优。
    /// 数据库文件被其他进程锁定时受调用方取消/超时约束。
    /// <para><b>v5.0 阶段 3.3 PRAGMA 调优</b>（统一执行一次，单次往返）：
    /// synchronous=NORMAL（WAL 下安全，减少 fsync，写性能提升）；
    /// cache_size=-65536（64MB 页缓存，默认 2MB，读密集型提升）；
    /// temp_store=MEMORY（临时表/索引走内存）；
    /// wal_autocheckpoint=1000（WAL 自动检查点，默认即 1000，显式固定防部署漂移）。</para>
    /// <para><b>v5.0 阶段 3.5 mmap_size 条件判断</b>：仅文件数据库追加 mmap_size=268435456（256MB
    /// mmap I/O）。</para>
    /// <para><b>v7.2.1 内存库分支收窄</b>：<c>:memory:</c> 库仅执行 foreign_keys 与 cache_size——
    /// journal_mode=WAL / synchronous(fsync) / wal_autocheckpoint(检查点) / mmap_size 对无文件
    /// I/O 的内存库均无语义（SQLite 静默返回不报错但徒增误导）；temp_store 本就以内存为目标。</para></summary>
    public static async Task InitializeConnectionAsync(DbConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // v5.0 阶段 3.5：检测 :memory: 数据库——文件 I/O 治理类 PRAGMA 仅对文件库有意义。
        // ITM-733(r20)：原用 ConnectionString.Contains(":memory:") 子串判定——DataSource 恰含该子串
        // 的真实文件库（Linux 合法文件名，如 /tmp/mem:memory:1.db）被误判为内存库，静默跳过
        // WAL/synchronous/mmap 配置。改走 SqliteConnectionStringBuilder 结构化解析。
        bool isInMemory;
        bool isReadOnly;
        try
        {
            var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
            // Microsoft.Data.Sqlite 语义：Mode=Memory（含 ":memory:" 与命名共享内存库）
            // 或省略 Data Source（临时内存库）均为内存库。
            isInMemory = builder.Mode == SqliteOpenMode.Memory
                || string.IsNullOrEmpty(builder.DataSource)
                || string.Equals(builder.DataSource, ":memory:", StringComparison.Ordinal);
            isReadOnly = builder.Mode == SqliteOpenMode.ReadOnly;
        }
        catch (ArgumentException)
        {
            // 非法连接串由后续命令执行报错；此处保守按文件库处理（不静默降级耐久性配置）
            isInMemory = false;
            isReadOnly = false;
        }

        await using DbCommand command = connection.CreateCommand();
        // 单次往返执行全部 PRAGMA（用分号连接，SQLite 原生支持）。
        // ITM-766(r21)：只读库（Mode=ReadOnly / 只读副本）走窄分支——journal_mode=WAL 在只读
        // 连接上抛 SqliteException Error 8（SQLite 真库探针实证），会使会话创建与读副本连接
        // 整体失败。只读库本就不写入，WAL/synchronous/checkpoint 类治理无语义；
        // foreign_keys 与 cache_size 是纯连接态设置，只读下安全。
        command.CommandText = isInMemory || isReadOnly
            ? "PRAGMA foreign_keys = ON; PRAGMA cache_size=-65536"
            : "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL; "
              + "PRAGMA synchronous=NORMAL; PRAGMA cache_size=-65536; "
              + "PRAGMA temp_store=MEMORY; PRAGMA wal_autocheckpoint=1000; "
              + "PRAGMA mmap_size=268435456";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>用 PRAGMA table_info 查询列信息,列名位于结果集序号 1。
    /// SQLite 不支持实体 Schema——schema 非空时抛 <see cref="NotSupportedException"/>。</summary>
    public static int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.IsNullOrWhiteSpace(schema))
            throw new NotSupportedException("The SQLite provider does not support entity schema configuration.");
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        return 1;
    }

    /// <summary>创建 SqliteParameter;value 为 null 时转为 <see cref="DBNull.Value"/>(ADO.NET 中 null 参数值不会被发送)。</summary>
    public static DbParameter CreateParameter(string name, object? value)
        => new SqliteParameter(name, value ?? DBNull.Value);

    /// <summary>SQLITE_BUSY (5) / SQLITE_LOCKED (6)——数据库或表被其他连接锁定,属可重试瞬时故障。</summary>
    public static bool IsTransient(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 5 or 6 };

    /// <summary>SQLITE_CONSTRAINT_UNIQUE (2067) / SQLITE_CONSTRAINT_PRIMARYKEY (1555)——
    /// 仅唯一/主键冲突。主码 19 涵盖 NOT NULL(1299)/FK(787) 等全部约束违规，
    /// 按主码判定会把数据完整性错误误报为"记录已存在"（ITM-403）。</summary>
    public static bool IsUniqueViolation(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 or 1555 };

    /// <summary>批量插入——委托共享多值 INSERT 骨架；SQLite 单语句参数上限 999。</summary>
    /// <para>r6-N2：隔离级别参数仅为接口形态同步（SQLite 事务隔离单一 Serializable，
    /// BeginTransactionAsync 忽略隔离参数差异；r9-S4：本调用点走 BulkContext 默认值（ReadCommitted），非透传——行为中性，措辞订正）。</para>
    public static Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds, CancellationToken ct,
        System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted)
        where T : class, new()
        => MultiValueBulkInsert.ExecuteAsync(
            conn, transaction, entities,
            new BulkContext(
                batchSize,
                MaxParametersPerStatement: 999,
                QuoteIdentifier, CreateParameter, commandTimeoutSeconds),
            ct);
}
