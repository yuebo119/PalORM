using System.Data.Common;

namespace PalORM;

/// <summary>数据库 Provider 接口——C# 11 static abstract interface。
/// <para><b>为什么用 static abstract</b>: 编译时分发，零虚调用开销。泛型 DataSession&lt;TProvider&gt; 的每个 Provider
/// 实例在 JIT 编译时独立特化，运行时零分支判断、零接口查找。</para>
/// <para><b>为什么不是 DI 注册</b>: Provider 是编译时常量——一个项目只用一种数据库。DI 容器注册增加启动开销和复杂度。</para>
/// <para><b>扩展方式</b>: Provider 之间零引用、零耦合；但新增 SQL 方言还需同步扩展
/// Core 的 SqlDialect 枚举、CommandSqlByDialect 等按方言展开的类型以及 SourceGen 的
/// SqlGenerationDialect——"实现本接口"只覆盖连接/参数/批量层（ITM-331b 已裁决：
/// 确有第四 Provider 需求时再议，文档已收窄）。</para></summary>
public interface IDbProvider
{
    /// <summary>Provider 名称（PostgreSql / MySql / SQLite）。</summary>
    static abstract string Name { get; }

    /// <summary>SQL 方言标识。</summary>
    static abstract SqlDialect Dialect { get; }

    /// <summary>创建应用连接池配置的数据库连接。</summary>
    static abstract DbConnection CreateConnection(string connectionString, DbOptions options);

    /// <summary>引用单段标识符，并转义 Provider 对应的内部引用符。
    /// <para><b>第三方 Provider 契约（ITM-674）</b>：跨 Provider 共享的缓存键仅含
    /// <c>(Type, SqlDialect)</c>——同一 <see cref="SqlDialect"/> 的不同 Provider 实现
    /// <b>必须</b>产生相同的引用语义（同样的 <see cref="QuoteIdentifier"/> 输出）。
    /// 引用语义不同的变体请使用独立 <see cref="SqlDialect"/> 值或实现私有缓存。</para></summary>
    static abstract string QuoteIdentifier(string identifier);

    /// <summary>分别引用 schema 与表名，避免把点分名称误作单个标识符。
    /// 各 Provider 自行实现——虽然三方言实现高度相似，但 C# static virtual 默认实现
    /// 无法调用同接口的 static abstract 成员（CS8926），故不做默认实现。</summary>
    static abstract string QuoteQualifiedIdentifier(string? schema, string identifier);

    /// <summary>是否支持 RETURNING 子句（PG/SQLite ✅，MySQL ❌）。</summary>
    static abstract bool SupportsReturningClause { get; }

    /// <summary>数据库当前时间表达式，用于软删除等服务端时间写入。</summary>
    static abstract string CurrentTimestampExpression { get; }

    /// <summary>参数占位符生成（@p{N}）。统一格式，三 Provider 共享默认实现。</summary>
    static virtual string GetParameterPlaceholder(int index) => $"@p{index}";

    /// <summary>创建 DbParameter。避免 QueryBuilder.Where() 中 CreateCommand() 资源泄漏。</summary>
    static abstract DbParameter CreateParameter(string name, object? value);

    /// <summary>判断数据库异常是否属于可安全重试的瞬时故障。</summary>
    static virtual bool IsTransient(Exception exception)
        => exception is DbException { IsTransient: true };

    /// <summary>连接打开后的一次性初始化钩子（如 SQLite 的 PRAGMA 配置）。默认无操作。
    /// 主连接由 DataSession.CreateAsync 在连接打开后、会话可用前调用；
    /// 读路由（ForRead）连接由 ConnectionLease.OpenOwnedAsync 在打开后调用——
    /// 两类连接均经初始化，取消与超时保护对其生效。</summary>
    static virtual Task InitializeConnectionAsync(DbConnection connection, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>判断异常是否为唯一约束冲突——调用方无需分别 catch 三驱动的专有异常
    /// （SqliteException 19 / MySqlException 1062 / PostgresException 23505，ITM-314）。默认 false。</summary>
    static virtual bool IsUniqueViolation(Exception exception) => false;

    /// <summary>判断 DDL 异常是否为"架构对象已存在"（迁移幂等兜底）。
    /// 默认 false；无 CREATE INDEX IF NOT EXISTS 语法的方言（MySQL）覆盖为真实判定。</summary>
    static virtual bool IsDuplicateSchemaObject(Exception exception) => false;

    /// <summary>批量插入默认实现。由 DataSession.BulkInsertAsync 直接处理，各 Provider 可覆盖为高效实现。
    /// <para>ITM-557: <paramref name="commandTimeoutSeconds"/> 为会话 CommandTimeout——
    /// 批量命令必须应用，否则慢库上按驱动默认（约 30s）超时，与配置意图相反。</para>
    /// <para>r6-N2: <paramref name="isolationLevel"/> 为会话 WithIsolationLevel 值——自开事务遵从（原裸驱动默认）。</para>
    /// <para>r7-O1 登记：isolationLevel 默认参使 ct 非末位（二者不可兼得）；且 static virtual
    /// 接口加参对库外旧签名实现是形态断裂（dispatch 落默认 throw）——破坏性变更，
    /// 5.x minor 内消化（外部 Provider 实现需同步签名，CHANGELOG 注明）。</para></summary>
    static virtual Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, int commandTimeoutSeconds, CancellationToken ct,
        System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted)  // r6-N2：会话隔离级别透传
        where T : class, new()
    {
        throw new NotSupportedException("Override BulkInsertAsync in your provider for optimized bulk insert.");
    }

    /// <summary>配置 Schema 列查询命令，并返回结果集中列名所在的序号。</summary>
    static abstract int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null);
}
