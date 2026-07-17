using System.Data.Common;

namespace PalORM;

/// <summary>数据库 Provider 接口——C# 11 static abstract interface。
/// <para><b>为什么用 static abstract</b>: 编译时分发，零虚调用开销。泛型 DataSession&lt;TProvider&gt; 的每个 Provider
/// 实例在 JIT 编译时独立特化，运行时零分支判断、零接口查找。</para>
/// <para><b>为什么不是 DI 注册</b>: Provider 是编译时常量——一个项目只用一种数据库。DI 容器注册增加启动开销和复杂度。</para>
/// <para><b>扩展方式</b>: 实现此接口即可新增 Provider，Provider 之间零引用、零耦合。</para></summary>
public interface IDbProvider
{
    /// <summary>Provider 名称（PostgreSql / MySql / SQLite）。</summary>
    static abstract string Name { get; }

    /// <summary>参数占位符前缀（统一使用 @）。</summary>
    [Obsolete("零调用点的死接口成员；LIMIT 构建统一在 QueryBuilder.BuildLimitClause。3.0 移除。")]
    static abstract char ParameterPrefix { get; }

    /// <summary>SQL 方言标识。</summary>
    static abstract SqlDialect Dialect { get; }

    /// <summary>创建数据库连接。</summary>
    static abstract DbConnection CreateConnection(string connectionString);

    /// <summary>创建应用连接池配置的数据库连接。</summary>
    static abstract DbConnection CreateConnection(string connectionString, DbOptions options);

    /// <summary>引用单段标识符，并转义 Provider 对应的内部引用符。</summary>
    static abstract string QuoteIdentifier(string identifier);

    /// <summary>分别引用 schema 与表名，避免把点分名称误作单个标识符。</summary>
    static abstract string QuoteQualifiedIdentifier(string? schema, string identifier);

    /// <summary>LIMIT/OFFSET 子句。不同数据库语法不同。</summary>
    [Obsolete("零调用点的死接口成员；LIMIT 构建统一在 QueryBuilder.BuildLimitClause（按 SqlDialect 分支）。3.0 移除。")]
    static abstract string GetLimitOffsetClause(int? limit, int? offset);

    /// <summary>是否支持 RETURNING 子句（PG/SQLite ✅，MySQL ❌）。</summary>
    static abstract bool SupportsReturningClause { get; }

    /// <summary>数据库当前时间表达式，用于软删除等服务端时间写入。</summary>
    static abstract string CurrentTimestampExpression { get; }

    /// <summary>参数占位符生成（如 @p0）。</summary>
    static abstract string GetParameterPlaceholder(int index);

    /// <summary>创建 DbParameter。避免 QueryBuilder.Where() 中 CreateCommand() 资源泄漏。</summary>
    static abstract DbParameter CreateParameter(string name, object? value);

    /// <summary>判断数据库异常是否属于可安全重试的瞬时故障。</summary>
    static virtual bool IsTransient(Exception exception)
        => exception is DbException { IsTransient: true };

    /// <summary>连接打开后的一次性初始化钩子（如 SQLite 的 PRAGMA 配置）。默认无操作。
    /// 由 DataSession.CreateAsync 在连接打开后、会话可用前调用；取消与连接超时保护对其生效。</summary>
    static virtual Task InitializeConnectionAsync(DbConnection connection, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>判断 DDL 异常是否为"架构对象已存在"（迁移幂等兜底）。
    /// 默认 false；无 CREATE INDEX IF NOT EXISTS 语法的方言（MySQL）覆盖为真实判定。</summary>
    static virtual bool IsDuplicateSchemaObject(Exception exception) => false;

    /// <summary>批量插入默认实现。由 DataSession.BulkInsertAsync 直接处理，各 Provider 可覆盖为高效实现。</summary>
    static virtual Task<long> BulkInsertAsync<T>(DbConnection conn, DbTransaction? transaction,
        IReadOnlyList<T> entities, int batchSize, CancellationToken ct)
        where T : class, new()
    {
        throw new NotSupportedException("Override BulkInsertAsync in your provider for optimized bulk insert.");
    }

    /// <summary>配置 Schema 列查询命令，并返回结果集中列名所在的序号。</summary>
    static abstract int ConfigureSchemaCommand(DbCommand command, string tableName, string? schema = null);
}
