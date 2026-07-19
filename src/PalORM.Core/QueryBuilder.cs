using System.Data.Common;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace PalORM;

/// <summary>类型安全链式查询构建器——struct（值类型）。
/// <para><b>为什么是 struct</b>: class 方案每次 From&lt;T&gt;() 都会分配堆内存，高 QPS 下增加 GC 压力。</para>
/// <para><b>为什么执行方法在扩展类</b>: struct 的 async 实例方法会装箱，静态扩展方法可避免该分配。</para>
/// <para><b>写时复制</b>: struct 复制后共享子句/参数列表；每次追加子句都创建独立副本，任意时点复制的分支互不污染。</para></summary>
public struct QueryBuilder<T> where T : class, new()
{
    internal DbConnection _conn;
    internal readonly Func<DbConnection>? _readConnFactory;
    internal readonly Func<DbConnection, CancellationToken, Task>? _readConnInitializer;
    internal readonly SqlDialect _dialect;
    internal readonly bool _validateColumnOrder;
    internal readonly Func<string, string> _quoteIdentifier;
    internal readonly IRowFactory<T> _factory;
    internal readonly List<IQueryInterceptor> _interceptors;
    internal readonly Func<string, object?, DbParameter> _paramFactory;
    internal readonly string _tableName;
    internal readonly IReadOnlyList<string> _columnNames;
    internal readonly SessionOperationState _operationState;
    internal TimeSpan _commandTimeout;
    internal List<QueryClause> _clauses;
    internal List<DbParameter> _parameters;
    internal string? _selectColumns;
    internal int? _take;
    internal int? _skip;
    internal string? _cacheKey;
    internal TimeSpan? _cacheTtl;
    internal string? _cteName;
    internal bool _prepared;
    internal bool _tracing;
    internal bool _metrics;
    internal bool _splitQuery;
    internal bool _useReadRoute;
    internal DbTransaction? _transaction;
    internal readonly IQueryCache _queryCache;

    internal QueryBuilder(QueryBuilderContext<T> ctx)
    {
        _validateColumnOrder = ctx.ValidateColumnOrder;
        _conn = ctx.Connection;
        _readConnFactory = ctx.ReadConnFactory;
        _readConnInitializer = ctx.ReadConnInitializer;
        _queryCache = ctx.QueryCache ?? CacheStore.Default;
        _dialect = ctx.Services.Dialect;
        _quoteIdentifier = ctx.Services.QuoteIdentifier;
        _factory = ctx.Services.Factory;
        _interceptors = ctx.Services.Interceptors;
        _paramFactory = ctx.Services.ParamFactory;
        _tableName = ctx.TableName;
        _columnNames = ctx.ColumnNames;
        _operationState = ctx.Services.OperationState;
        _commandTimeout = ctx.Services.CommandTimeout;
        _clauses = [];
        _parameters = [];
        _selectColumns = null;
        _take = null;
        _skip = null;
        _cacheKey = null;
        _cacheTtl = null;
        _cteName = null;
        _prepared = false;
        _tracing = false;
        _metrics = false;
        _splitQuery = false;
        _useReadRoute = false;
        _transaction = null;
    }

    /// <summary>遗留 14 参构造——内部已迁移到 QueryBuilderContext。
    /// 保留供测试/反射代码兼容；新代码请用 <see cref="QueryBuilder(QueryBuilderContext{T})"/>。</summary>
    [Obsolete("Use QueryBuilder(QueryBuilderContext<T>). Kept for compat.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability",
        "S107:MethodsShouldNotHaveTooManyParameters",
        Justification = "遗留 ctor 保留向后兼容，参数多于 7 但不能改签名。新代码请用聚合 ctor。")]
    internal QueryBuilder(DbConnection conn, SqlDialect dialect, IRowFactory<T> factory,
        List<IQueryInterceptor> interceptors, Func<string, object?, DbParameter> paramFactory,
        Func<string, string> quoteIdentifier, string tableName,
        IReadOnlyList<string> columnNames, TimeSpan commandTimeout,
        SessionOperationState operationState,
        Func<DbConnection>? readConnFactory = null,
        IQueryCache? queryCache = null,
        bool validateColumnOrder = false,
        Func<DbConnection, CancellationToken, Task>? readConnInitializer = null)
        : this(new QueryBuilderContext<T>(
            conn,
            new QueryBuilderServices<T>(dialect, factory, interceptors, paramFactory,
                quoteIdentifier, operationState, commandTimeout),
            tableName, columnNames,
            readConnFactory, queryCache, validateColumnOrder, readConnInitializer))
    {
    }

    /// <summary>链式追加 WHERE/AND 条件。用户条件整体括号包裹并与默认过滤（软删/租户）
    /// 分组组合：WHERE defaults AND ((A) OR (B))——用户 OR 无法绕过默认过滤（ITM-401 根治）。</summary>
    public QueryBuilder<T> Where(FormattableString clause)
    {
        AddParenthesizedClause(
            HasClause(QueryClauseKind.Where) ? "AND " : "", clause);
        return this;
    }

    /// <summary>链式追加 OR 条件——仅与既有用户条件 OR 组合；默认过滤（软删/租户）恒以 AND 前置，不受影响。
    /// <para>首个用户子句使用 OrWhere 时无既有条件可 OR，语义等价 <see cref="Where"/>。</para></summary>
    public QueryBuilder<T> OrWhere(FormattableString clause)
    {
        // 用户子句在独立分组内组合，默认过滤恒以 AND 前置（AppendWhereSection）——
        // 首个用户子句用 OrWhere 时无既有条件可 OR，语义等价 Where。
        AddParenthesizedClause(
            HasClause(QueryClauseKind.Where) ? "OR " : "", clause);
        return this;
    }

    private void AddParenthesizedClause(string prefix, FormattableString clause)
    {
        var (sql, parameters) = BindFormattableString(clause);
        AddClause(QueryClauseKind.Where, $"{prefix}({sql})", parameters);
    }

    /// <summary>排序。重复调用退化为多键续排（等价 ThenBy）——避免生成双 ORDER BY 非法 SQL（ITM-306）。</summary>
    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> member, bool descending = false)
    {
        AddOrderBy(member, descending);
        return this;
    }

    /// <summary>在既有排序上追加次级排序键。无前置 <see cref="OrderBy{TKey}"/> 时抛 <see cref="InvalidOperationException"/>。</summary>
    public QueryBuilder<T> ThenBy<TKey>(Expression<Func<T, TKey>> member, bool descending = false)
    {
        if (!HasClause(QueryClauseKind.OrderBy))
            throw new InvalidOperationException("ThenBy requires a preceding OrderBy.");
        AddClause(QueryClauseKind.OrderBy,
            $", {GetQualifiedColumnName(member)}{(descending ? " DESC" : "")}");
        return this;
    }

    /// <summary>配置 SQL 投影。当前仅支持 DryRun/ToSql；实体执行需完整 RowFactory，部分投影会明确失败。</summary>
    public QueryBuilder<T> Select(params Expression<Func<T, object?>>[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        // ITM-537: 投影列带表限定（_cteName ?? _tableName），与 GetQualifiedColumnName 一致——
        // JOIN/Include 下裸列名产生 ambiguous column 错误。
        string source = _quoteIdentifier(_cteName ?? _tableName);
        Func<string, string> quoteIdentifier = _quoteIdentifier;
        _selectColumns = string.Join(", ",
            members.Select(member => $"{source}.{quoteIdentifier(GetColumnName(member))}"));
        return this;
    }

    /// <summary>限制返回行数（LIMIT）。n 必须为正数。</summary>
    public QueryBuilder<T> Take(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        _take = n;
        return this;
    }

    /// <summary>跳过前 n 行（OFFSET）。n 不能为负；裸 OFFSET 的方言差异（SQLite/MySQL）由构建器自动处理。</summary>
    public QueryBuilder<T> Skip(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        _skip = n;
        return this;
    }

    /// <summary>追加参数化 IN 条件，与既有条件 AND 组合。
    /// <para>空集合生成恒假条件 1=0（IN () 是非法 SQL）；超过 500 个值按批次切分为多个 IN 片段 OR 组合，规避各数据库参数上限。</para></summary>
    public QueryBuilder<T> WhereIn<TValue>(Expression<Func<T, TValue>> member, IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string column = _quoteIdentifier(GetColumnName(member));
        var items = values as IReadOnlyList<TValue> ?? values.ToList();
        if (items.Count == 0)
        {
            AddClause(QueryClauseKind.Where, HasClause(QueryClauseKind.Where) ? "AND 1=0" : "1=0");
            return this;
        }
        // ITM-514: 分批规避单条 IN 的参数上限，但参数总量仍受协议约束——超 65535（PG 协议 int16 上限，
        // 最严方言）应改用临时表 JOIN 或分批查询，而非静默生成越界 SQL。
        // ITM-562: 判定按"存量 + 增量"累计——两次 40k 的 WhereIn 各自增量合规但总量越界，
        // 只查增量会静默通过、运行期 PG 协议层才报错。
        if (_parameters.Count + items.Count > 65535)
            throw new ArgumentException(
                $"WhereIn received {items.Count} values on a builder holding {_parameters.Count} parameters; " +
                "the total exceeds the 65535 bind-parameter limit (PostgreSQL protocol max). " +
                "Use a temp table join or split the query into batches.", nameof(values));

        const int maxBatch = 500;
        // O2 预分配：批次数与参数总量在进循环前已知
        var batches = new List<string>((items.Count + maxBatch - 1) / maxBatch);
        var parameters = new List<DbParameter>(items.Count);
        for (int start = 0; start < items.Count; start += maxBatch)
        {
            int end = Math.Min(start + maxBatch, items.Count);
            var placeholders = new string[end - start];
            for (int i = start; i < end; i++)
            {
                DbParameter parameter = CreateParameter(items[i], parameters.Count);
                placeholders[i - start] = parameter.ParameterName;
                parameters.Add(parameter);
            }
            batches.Add($"{column} IN ({string.Join(", ", placeholders)})");
        }

        AddClause(QueryClauseKind.Where,
            $"{(HasClause(QueryClauseKind.Where) ? "AND " : "")}({string.Join(" OR ", batches)})",
            parameters);
        return this;
    }

    /// <summary>追加参数化 NOT IN 条件，与既有条件 AND 组合。
    /// <para>空集合为 no-op（排除空集等于不过滤）；超过 500 个值按批次切分为多个 NOT IN 片段 AND 组合。</para></summary>
    public QueryBuilder<T> WhereNotIn<TValue>(Expression<Func<T, TValue>> member, IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string column = _quoteIdentifier(GetColumnName(member));
        var items = values as IReadOnlyList<TValue> ?? values.ToList();
        if (items.Count == 0) return this;
        // ITM-514: 同 WhereIn——参数总量不封顶会生成越界 SQL；ITM-562: 存量+增量累计判定。
        if (_parameters.Count + items.Count > 65535)
            throw new ArgumentException(
                $"WhereNotIn received {items.Count} values on a builder holding {_parameters.Count} parameters; " +
                "the total exceeds the 65535 bind-parameter limit (PostgreSQL protocol max). " +
                "Use a temp table join or split the query into batches.", nameof(values));

        const int maxBatch = 500;
        // O2 预分配：批次数与参数总量在进循环前已知
        var batches = new List<string>((items.Count + maxBatch - 1) / maxBatch);
        var parameters = new List<DbParameter>(items.Count);
        for (int start = 0; start < items.Count; start += maxBatch)
        {
            int end = Math.Min(start + maxBatch, items.Count);
            var placeholders = new string[end - start];
            for (int i = start; i < end; i++)
            {
                DbParameter parameter = CreateParameter(items[i], parameters.Count);
                placeholders[i - start] = parameter.ParameterName;
                parameters.Add(parameter);
            }
            batches.Add($"{column} NOT IN ({string.Join(", ", placeholders)})");
        }

        AddClause(QueryClauseKind.Where,
            $"{(HasClause(QueryClauseKind.Where) ? "AND " : "")}({string.Join(" AND ", batches)})",
            parameters);
        return this;
    }

    /// <summary>INNER JOIN 已注册实体 TJoin 的表，ON 条件参数化绑定。TJoin 需有 [Table] 且经源生成器注册。</summary>
    public QueryBuilder<T> InnerJoin<TJoin>(FormattableString onClause) where TJoin : class, new()
        => AddJoin<TJoin>("INNER", onClause);

    /// <summary>LEFT JOIN 已注册实体 TJoin 的表，ON 条件参数化绑定。TJoin 需有 [Table] 且经源生成器注册。</summary>
    public QueryBuilder<T> LeftJoin<TJoin>(FormattableString onClause) where TJoin : class, new()
        => AddJoin<TJoin>("LEFT", onClause);

    /// <summary>RIGHT JOIN 已注册实体 TJoin 的表，ON 条件参数化绑定。TJoin 需有 [Table] 且经源生成器注册。</summary>
    public QueryBuilder<T> RightJoin<TJoin>(FormattableString onClause) where TJoin : class, new()
        => AddJoin<TJoin>("RIGHT", onClause);

    /// <summary>追加 GROUP BY 分组列。重复调用追加多列；列名带表限定，JOIN 下不产生 ambiguous column。</summary>
    public QueryBuilder<T> GroupBy(Expression<Func<T, object?>> member)
    {
        string prefix = HasClause(QueryClauseKind.GroupBy) ? ", " : "GROUP BY ";
        // 表限定与其余子句对齐（ITM-425）：JOIN 下裸列名产生 ambiguous column
        AddClause(QueryClauseKind.GroupBy, prefix + GetQualifiedColumnName(member));
        return this;
    }

    /// <summary>追加 HAVING 条件（作用于分组后），参数化绑定；重复调用以 AND 组合。</summary>
    public QueryBuilder<T> Having(FormattableString clause)
    {
        AddFormattableClause(QueryClauseKind.Having,
            HasClause(QueryClauseKind.Having) ? "AND " : "HAVING ", clause);
        return this;
    }

    /// <summary>追加 UPDATE 的 SET 赋值项，值参数化绑定。至少一个 Set 才能执行 ExecuteNonQueryAsync。</summary>
    public QueryBuilder<T> Set<TValue>(Expression<Func<T, TValue>> member, TValue value)
    {
        DbParameter parameter = CreateParameter(value);
        string prefix = HasClause(QueryClauseKind.Set) ? ", " : "SET ";
        AddClause(QueryClauseKind.Set,
            $"{prefix}{_quoteIdentifier(GetColumnName(member))} = {parameter.ParameterName}", [parameter]);
        return this;
    }

    /// <summary>按外键/主键 INNER JOIN 已注册子实体 TChild 的表。仅生成 JOIN 子句，不装配导航对象。</summary>
    public QueryBuilder<T> Include<TChild>(Expression<Func<T, object?>> fk,
        Expression<Func<TChild, object?>> pk) where TChild : class, new()
    {
        string childTable = GetRegisteredTableName(typeof(TChild));
        // ITM-515: With(CTE) 后 FROM 已切 CTE 名，JOIN ON 右端须与 GetQualifiedColumnName 一致用
        // _cteName ?? _tableName，否则 ON 引用了不在 FROM 中的实体表名，SQL 报未知表别名。
        string leftSource = _cteName ?? _tableName;
        AddClause(QueryClauseKind.Join,
            $"INNER JOIN {_quoteIdentifier(childTable)} ON " +
            $"({_quoteIdentifier(childTable)}.{_quoteIdentifier(GetColumnName(pk))} = " +
            $"{_quoteIdentifier(leftSource)}.{_quoteIdentifier(GetColumnName(fk))})");
        return this;
    }

    /// <summary>已废弃：单键无法表达 JOIN 两端，调用即抛 <see cref="NotSupportedException"/>。
    /// 请使用 ThenInclude&lt;TGrandChild, TParent&gt;(grandChildKey, parentKey)。</summary>
    [Obsolete("此重载无法表达 JOIN 两端。请使用 ThenInclude<TGrandChild, TParent>(grandChildKey, parentKey)。")]
    public QueryBuilder<T> ThenInclude<TGrandChild>(Expression<Func<TGrandChild, object?>> fk)
        where TGrandChild : class, new()
        => throw new NotSupportedException(
            "ThenInclude requires both join keys. Use ThenInclude<TGrandChild, TParent>(grandChildKey, parentKey).");

    /// <summary>在 <see cref="Include{TChild}"/> 基础上按两端键继续 INNER JOIN 孙实体 TGrandChild 的表。
    /// 两实体均需已注册；仅生成 JOIN 子句，不装配导航对象。</summary>
    public QueryBuilder<T> ThenInclude<TGrandChild, TParent>(
        Expression<Func<TGrandChild, object?>> grandChildKey,
        Expression<Func<TParent, object?>> parentKey)
        where TGrandChild : class, new()
        where TParent : class, new()
    {
        string grandChildTable = GetRegisteredTableName(typeof(TGrandChild));
        string parentTable = GetRegisteredTableName(typeof(TParent));
        // ITM-551: 与 Include（ITM-515）对称——若 TParent 恰为根实体（parentTable == _tableName）且已 With(CTE)，
        // FROM 已切 CTE 名，JOIN ON 右端须用 _cteName 否则引用了不在 FROM 中的实体表名。
        // 但 TParent 可为任意祖先类型（不一定是根 T），故仅在等于根表名时重映射，其余祖先保持真实表名。
        string parentSource = parentTable == _tableName ? _cteName ?? _tableName : parentTable;
        AddClause(QueryClauseKind.Join,
            $"INNER JOIN {_quoteIdentifier(grandChildTable)} ON " +
            $"({_quoteIdentifier(grandChildTable)}.{_quoteIdentifier(GetColumnName(grandChildKey))} = " +
            $"{_quoteIdentifier(parentSource)}.{_quoteIdentifier(GetColumnName(parentKey))})");
        return this;
    }

    /// <summary>追加调用方负责安全性的窗口 SQL 片段。不得传入不可信内容。</summary>
    public QueryBuilder<T> UnsafeWindowOver(string func, string over)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(func);
        ArgumentException.ThrowIfNullOrWhiteSpace(over);
        AddClause(QueryClauseKind.Window, $"{func} OVER ({over})");
        return this;
    }

    /// <summary>定义 CTE（WITH cteName AS (subquery)），子查询参数化绑定；后续主查询 FROM 该 CTE 而非实体表。
    /// <para>列名限定与 OrderBy/GroupBy 也切换为 CTE 名——子查询需输出实体全部列。</para></summary>
    public QueryBuilder<T> With(string cteName, FormattableString subquery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cteName);
        var (sql, parameters) = BindFormattableString(subquery);
        _cteName = cteName;
        AddClause(QueryClauseKind.CommonTableExpression,
            $"{_quoteIdentifier(cteName)} AS ({sql})", parameters);
        return this;
    }

    /// <summary>标记为拆分查询：构建 SQL 时移除 JOIN 只查根实体，JOIN 参数同步排除。
    /// <para>当前不执行导航对象装配，仅影响根查询构建。</para></summary>
    public QueryBuilder<T> AsSplitQuery()
    {
        _splitQuery = true;
        return this;
    }

    /// <summary>追加 FOR UPDATE 行锁；skipLocked 为 true 时附加 SKIP LOCKED 跳过已锁行。需在事务内使用才有意义。</summary>
    public QueryBuilder<T> ForUpdate(bool skipLocked = false)
    {
        AddClause(QueryClauseKind.Lock, $"FOR UPDATE{(skipLocked ? " SKIP LOCKED" : "")}");
        return this;
    }

    /// <summary>追加 FOR SHARE 共享锁——允许并发读、阻止并发写。需在事务内使用才有意义。</summary>
    public QueryBuilder<T> ForShare()
    {
        AddClause(QueryClauseKind.Lock, "FOR SHARE");
        return this;
    }

    /// <summary>追加调用方负责安全性的原始 SQL 片段。不得传入不可信内容。</summary>
    public QueryBuilder<T> Raw(string literal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(literal);
        AddClause(QueryClauseKind.Raw, literal);
        return this;
    }

    /// <summary>为 SQL 附加块注释标记（业务标识/排查关联）。拒绝注释定界符与 NUL 字符。</summary>
    public QueryBuilder<T> Tag(string name)
    {
        AddClause(QueryClauseKind.Comment, $"/* {ValidateSqlComment(name)} */");
        return this;
    }

    /// <summary>以调用方源码位置为 Tag。注意：[CallerFilePath] 是编译机绝对路径，
    /// 会随 SQL 注释发送到数据库服务器（可见于 DB 日志/pg_stat_activity），泄露内部目录结构。
    /// 生产环境建议使用 Tag(name) 传业务标识，或配置 PathMap 规范化编译路径。</summary>
    public QueryBuilder<T> TagWithCaller([CallerMemberName] string? member = null,
        [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
        => Tag($"{file}:{line} {member}");

    /// <summary>路由到只读连接（若配置了读连接工厂）。活跃事务或写操作时自动回退主连接。</summary>
    public QueryBuilder<T> ForRead() { _useReadRoute = true; return this; }

    /// <summary>强制路由回主（写）连接，撤销 <see cref="ForRead"/> 的读路由。</summary>
    public QueryBuilder<T> ForWrite() { _useReadRoute = false; return this; }

    /// <summary>覆盖本查询的命令超时（秒）。必须为正数；仅影响当前构建器。</summary>
    public QueryBuilder<T> WithCommandTimeout(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        _commandTimeout = TimeSpan.FromSeconds(seconds);
        return this;
    }

    /// <summary>结果缓存。<b>浅拷贝契约</b>：命中返回新 List，但元素为共享实体实例——
    /// 命中实体应视为只读；需要修改时先自行深拷贝，否则会污染缓存与其他调用方（ITM-308）。</summary>
    public QueryBuilder<T> WithCache(string cacheKey, TimeSpan? ttl = null)
    {
        _cacheKey = cacheKey;
        _cacheTtl = ttl;
        return this;
    }

    /// <summary>在参数绑定完成后调用 Provider 的 <see cref="DbCommand.PrepareAsync(CancellationToken)"/>。</summary>
    public QueryBuilder<T> AsPrepared()
    {
        _prepared = true;
        return this;
    }

    /// <summary>绑定既有事务。事务必须属于本构建器的主连接，否则抛 <see cref="ArgumentException"/>。</summary>
    public QueryBuilder<T> WithTransaction(DbTransaction tran)
    {
        ArgumentNullException.ThrowIfNull(tran);
        if (!ReferenceEquals(tran.Connection, _conn))
            throw new ArgumentException("事务必须属于 QueryBuilder 的主连接。", nameof(tran));
        _transaction = tran;
        return this;
    }

    /// <summary>为查询执行启用 PalORM Activity。追踪数据不包含 SQL、参数或调用方路径。</summary>
    public QueryBuilder<T> WithTracing()
    {
        _tracing = true;
        return this;
    }

    /// <summary>为查询执行启用 PalORM Meter。名称仅保留 API 兼容，不作为指标标签。</summary>
    public QueryBuilder<T> WithMetrics(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains("*/", StringComparison.Ordinal) || name.Contains('\0'))
            throw new ArgumentException("Metric name contains an invalid character.", nameof(name));
        _metrics = true;
        return this;
    }

    /// <summary>不执行查询，返回构建好的 SQL 与参数快照（<see cref="DryRunResult"/>），用于预览/测试断言。
    /// 参数为防御性副本——修改快照参数不影响后续对同一 builder 的真实执行（ITM-511）。
    /// <para>ITM-563: 含 Set 子句时返回 UPDATE 预览（与 ExecuteNonQueryAsync 实际执行一致），
    /// 不再返回丢弃 Set 的 SELECT 误导预览。</para></summary>
    public DryRunResult AsDryRun()
    {
        bool isUpdate = HasClause(QueryClauseKind.Set);
        IReadOnlyList<DbParameter> live = isUpdate ? GetUpdateParameters() : GetQueryParameters();
        var snapshot = new DbParameter[live.Count];
        for (int i = 0; i < live.Count; i++)
            snapshot[i] = _paramFactory(live[i].ParameterName, live[i].Value);
        return new(isUpdate ? BuildUpdateSql() : BuildSql(), Array.AsReadOnly(snapshot));
    }

    internal ValueTask<ConnectionLease> AcquireConnectionLeaseAsync(bool writeOperation,
        CancellationToken cancellationToken)
    {
        if (GetActiveTransaction() is not null || writeOperation
            || !_useReadRoute || _readConnFactory is null)
            return ValueTask.FromResult(ConnectionLease.Borrow(_conn));

        return ConnectionLease.OpenOwnedAsync(_readConnFactory, cancellationToken, _readConnInitializer);
    }

    internal DbTransaction? GetActiveTransaction()
    {
        // ITM-524: 用户经 WithTransaction 显式绑定的事务若已释放（Connection 置空），不得静默回退到
        // 会话事务或无事务执行——那会让本应在指定事务内的写操作脱离事务。显式失效应显式失败。
        if (_transaction is not null && _transaction.Connection is null)
            throw new InvalidOperationException(
                "The transaction bound via WithTransaction has been disposed (its Connection is null); " +
                "the query would silently execute outside the intended transaction. Bind a live transaction.");
        return _transaction is not null
            ? _transaction
            : _operationState.GetActiveTransaction();
    }

    internal void AddDefaultFilter(string condition)
        => AddClause(QueryClauseKind.DefaultFilter, condition);

    /// <summary>带参数的默认过滤（租户）——参数编号进入统一 @p{N} 空间（ITM-401：
    /// 走 DefaultFilter 类别，与用户 WHERE 组恒 AND 组合，OrWhere 无法绕过）。</summary>
    internal void AddDefaultFilter(FormattableString condition)
    {
        var (sql, parameters) = BindFormattableString(condition);
        AddClause(QueryClauseKind.DefaultFilter, sql, parameters);
    }

    internal QueryBuilder<T> CloneForExecution()
    {
        var clone = new QueryBuilder<T>(new QueryBuilderContext<T>(
            _conn,
            new QueryBuilderServices<T>(_dialect, _factory, _interceptors, _paramFactory,
                _quoteIdentifier, _operationState, _commandTimeout),
            _tableName, _columnNames, _readConnFactory, _queryCache, _validateColumnOrder, _readConnInitializer))
        {
            _selectColumns = _selectColumns,
            _take = _take,
            _skip = _skip,
            _cacheKey = _cacheKey,
            _cacheTtl = _cacheTtl,
            _cteName = _cteName,
            _prepared = _prepared,
            _tracing = _tracing,
            _metrics = _metrics,
            _splitQuery = _splitQuery,
            _useReadRoute = _useReadRoute,
            _transaction = _transaction
        };
        foreach (QueryClause clause in _clauses)
        {
            var parameters = new List<DbParameter>(clause.Parameters.Count);
            foreach (DbParameter parameter in clause.Parameters)
            {
                DbParameter copy = clone._paramFactory(parameter.ParameterName, parameter.Value);
                parameters.Add(copy);
                clone._parameters.Add(copy);
            }
            clone._clauses.Add(new QueryClause(clause.Kind, clause.Sql, parameters));
        }
        return clone;
    }

    internal void AddWhereComparison<TKey>(Expression<Func<T, TKey>> member,
        string operation, TKey value)
    {
        // ITM-555：键集续页条件走 DefaultFilter 类别而非 Where——AppendWhereSection 对
        // DefaultFilter 恒以 AND 拼在用户子句组括号之外（WHERE keyset AND ((A) OR (B))）。
        // 若并入 Where 组，用户 OrWhere 的 OR 优先级会使续页条件仅约束末分支，
        // 页间重复、分页不推进（ITM-401 括组根因的新入口，真库探针实测）。
        DbParameter parameter = CreateParameter(value);
        AddClause(QueryClauseKind.DefaultFilter,
            $"{GetQualifiedColumnName(member)} {operation} {parameter.ParameterName}", [parameter]);
    }

    internal void AddOrderBy<TKey>(Expression<Func<T, TKey>> member, bool descending)
        => AddClause(QueryClauseKind.OrderBy,
            $"{(HasClause(QueryClauseKind.OrderBy) ? ", " : "ORDER BY ")}" +
            $"{GetQualifiedColumnName(member)}{(descending ? " DESC" : "")}");

    internal IReadOnlyList<DbParameter> GetQueryParameters()
        => GetParametersForKinds(_splitQuery
            ? [QueryClauseKind.CommonTableExpression, QueryClauseKind.DefaultFilter, QueryClauseKind.Where,
                QueryClauseKind.GroupBy, QueryClauseKind.Having, QueryClauseKind.OrderBy,
                QueryClauseKind.Raw, QueryClauseKind.Lock]
            : [QueryClauseKind.CommonTableExpression, QueryClauseKind.Join, QueryClauseKind.DefaultFilter,
                QueryClauseKind.Where, QueryClauseKind.GroupBy, QueryClauseKind.Having, QueryClauseKind.OrderBy,
                QueryClauseKind.Raw, QueryClauseKind.Lock]);

    internal IReadOnlyList<DbParameter> GetCountParameters()
        => GetParametersForKinds(_splitQuery
            ? [QueryClauseKind.CommonTableExpression, QueryClauseKind.DefaultFilter, QueryClauseKind.Where,
                QueryClauseKind.GroupBy, QueryClauseKind.Having]
            : [QueryClauseKind.CommonTableExpression, QueryClauseKind.Join, QueryClauseKind.DefaultFilter,
                QueryClauseKind.Where, QueryClauseKind.GroupBy, QueryClauseKind.Having]);

    internal IReadOnlyList<DbParameter> GetUpdateParameters()
        => GetParametersForKinds([QueryClauseKind.Set, QueryClauseKind.DefaultFilter,
            QueryClauseKind.Where, QueryClauseKind.Raw]);

    internal string BuildCountSql()
    {
        var sb = new ValueStringBuilder(stackalloc char[384]);
        try
        {
            AppendComments(ref sb);
            AppendCtes(ref sb);
            sb.Append("SELECT COUNT(*) FROM (SELECT 1 FROM ");
            sb.Append(_quoteIdentifier(_cteName ?? _tableName));
            sb.Append(' ');
            if (!_splitQuery) AppendClauses(ref sb, QueryClauseKind.Join);
            AppendWhereSection(ref sb);
            AppendClauses(ref sb, QueryClauseKind.GroupBy);
            AppendClauses(ref sb, QueryClauseKind.Having);
            sb.Append(") AS count_source");
            return sb.ToString().TrimEnd();
        }
        finally { sb.Dispose(); }  // 构建中途异常时归还池数组；ToString 已释放则为幂等 no-op
    }

    internal string BuildUpdateSql()
    {
        if (!HasClause(QueryClauseKind.Set))
            throw new InvalidOperationException("ExecuteNonQueryAsync requires at least one Set clause.");
        if (HasClause(QueryClauseKind.CommonTableExpression))
            throw new NotSupportedException("CTE is not supported by the current UPDATE builder.");
        // ITM-522: UPDATE 构建只消费 Set/Where/Raw——Join/OrderBy/Lock/Window 会被静默丢弃，
        // 让调用方误以为已生效。与 CTE 守卫并列，显式拒绝这些不受支持的子句。
        if (HasClause(QueryClauseKind.Join) || HasClause(QueryClauseKind.OrderBy)
            || HasClause(QueryClauseKind.Lock) || HasClause(QueryClauseKind.Window))
            throw new NotSupportedException(
                "UPDATE does not support Join/OrderBy/Lock/Window clauses; they would be silently dropped. " +
                "Remove them, or express the filter via Where.");
        var sb = new ValueStringBuilder(stackalloc char[256]);
        try
        {
            AppendComments(ref sb);
            sb.Append("UPDATE ");
            sb.Append(_quoteIdentifier(_tableName));
            sb.Append(' ');
            AppendClauses(ref sb, QueryClauseKind.Set);
            AppendWhereSection(ref sb);
            AppendClauses(ref sb, QueryClauseKind.Raw);
            return sb.ToString().TrimEnd();
        }
        finally { sb.Dispose(); }
    }

    /// <summary>构建 SQL 预览（含参数）。等同于 AsDryRun().Sql，但不创建 DryRunResult。
    /// 含 Set 子句时返回 UPDATE 预览（ITM-563）。</summary>
    public string ToSql() => HasClause(QueryClauseKind.Set) ? BuildUpdateSql() : BuildSql();

    /// <summary>按 QueryClauseKind 构建完整 SQL，调用顺序不再决定 SQL 语法顺序。
    /// <para>SplitQuery 当前只构建根查询并移除 JOIN，不执行导航对象装配。</para></summary>
    internal string BuildSql()
    {
        // ITM-563：SELECT 构建拒绝 Set 子句（ITM-522 的反向对称）——`Set(...).ToListAsync()`
        // 静默丢 Set 误导；`Set+Where` 的 AsDryRun/ToSql 会返回 SELECT 预览而非 UPDATE。
        // UPDATE 请走 ExecuteNonQueryAsync（BuildUpdateSql 独立路径，不受此守卫影响）。
        if (HasClause(QueryClauseKind.Set))
            throw new InvalidOperationException(
                "This builder has Set() clauses; SELECT execution/preview would silently discard them. " +
                "Use ExecuteNonQueryAsync for UPDATE, or remove Set() for SELECT.");
        var sb = new ValueStringBuilder(stackalloc char[512]);
        try
        {
            AppendComments(ref sb);
            AppendCtes(ref sb);
            sb.Append("SELECT ");
            AppendSelectColumns(ref sb);
            AppendWindowClauses(ref sb);
            sb.Append(" FROM ");
            sb.Append(_quoteIdentifier(_cteName ?? _tableName));
            sb.Append(' ');
            if (!_splitQuery) AppendClauses(ref sb, QueryClauseKind.Join);
            AppendWhereSection(ref sb);
            AppendClauses(ref sb, QueryClauseKind.GroupBy);
            AppendClauses(ref sb, QueryClauseKind.Having);
            AppendClauses(ref sb, QueryClauseKind.OrderBy);
            AppendClauses(ref sb, QueryClauseKind.Raw);
            string? limitClause = BuildLimitClause();
            if (limitClause is not null)
            {
                sb.Append(limitClause);
                sb.Append(' ');
            }
            AppendClauses(ref sb, QueryClauseKind.Lock);
            return sb.ToString().TrimEnd();
        }
        finally { sb.Dispose(); }
    }

    /// <summary>追加 SELECT 子句的列列表：显式列 vs 全列（带表名前缀）。</summary>
    private void AppendSelectColumns(ref ValueStringBuilder sb)
    {
        string sourceName = _cteName ?? _tableName;
        if (_selectColumns is not null)
        {
            sb.Append(_selectColumns);
            return;
        }
        for (int index = 0; index < _columnNames.Count; index++)
        {
            if (index > 0) sb.Append(", ");
            sb.Append(_quoteIdentifier(sourceName));
            sb.Append('.');
            sb.Append(_quoteIdentifier(_columnNames[index]));
        }
    }

    /// <summary>追加窗口函数列——出现在 SELECT 列表后段（与普通列以逗号分隔）。</summary>
    private void AppendWindowClauses(ref ValueStringBuilder sb)
    {
        foreach (QueryClause window in _clauses)
        {
            if (window.Kind != QueryClauseKind.Window) continue;
            sb.Append(", ");
            sb.Append(window.Sql);
        }
    }

    internal static string FormatFormattableSql(FormattableString sql, int baseIndex)
        => FormattableSqlFormatter.Format(sql, baseIndex);

    private QueryBuilder<T> AddJoin<TJoin>(string joinType, FormattableString onClause)
        where TJoin : class, new()
    {
        string joinTable = GetRegisteredTableName(typeof(TJoin));
        var (sql, parameters) = BindFormattableString(onClause);
        AddClause(QueryClauseKind.Join,
            $"{joinType} JOIN {_quoteIdentifier(joinTable)} ON ({sql})", parameters);
        return this;
    }

    private (string Sql, IReadOnlyList<DbParameter> Parameters) BindFormattableString(FormattableString sql)
    {
        int baseIndex = _parameters.Count;
        string formatted = FormatFormattableSql(sql, baseIndex);
        var parameters = new List<DbParameter>(sql.ArgumentCount);
        for (int i = 0; i < sql.ArgumentCount; i++)
            parameters.Add(_paramFactory($"@p{baseIndex + i}", sql.GetArgument(i)));
        return (formatted, parameters);
    }

    private void AddFormattableClause(QueryClauseKind kind, string prefix, FormattableString formattable)
    {
        var (sql, parameters) = BindFormattableString(formattable);
        AddClause(kind, prefix + sql, parameters);
    }

    private DbParameter CreateParameter(object? value, int localOffset = 0)
        => _paramFactory($"@p{_parameters.Count + localOffset}", value);

    private void AddClause(QueryClauseKind kind, string sql,
        IReadOnlyList<DbParameter>? parameters = null)
    {
        // 无条件写时复制：struct 副本共享列表引用，任何一次性"已复制"标志都会随副本
        // 一起被拷贝而失效（QUERY-001 场景 B/C）。每次写入先复制，保证副本间完全隔离。
        _clauses = new List<QueryClause>(_clauses);
        _parameters = new List<DbParameter>(_parameters);
        IReadOnlyList<DbParameter> ownedParameters = parameters ?? Array.Empty<DbParameter>();
        _clauses.Add(new QueryClause(kind, sql, ownedParameters));
        foreach (DbParameter parameter in ownedParameters) _parameters.Add(parameter);
    }

    private bool HasClause(QueryClauseKind kind)
        => _clauses.Exists(clause => clause.Kind == kind);

    /// <summary>统计"用户实质子句"数——排除 Comment（Tag/TagWithCaller）与 DefaultFilter
    /// （From&lt;T&gt;() 注入的软删/租户过滤）。QueryMultipleAsync 误用守卫据此判断，
    /// 避免加个 Tag 就误触异常（ITM-523）。</summary>
    internal int CountUserSubstantiveClauses()
    {
        int count = 0;
        foreach (QueryClause clause in _clauses)
        {
            if (clause.Kind is QueryClauseKind.Comment or QueryClauseKind.DefaultFilter) continue;
            count++;
        }
        return count;
    }

    private System.Collections.ObjectModel.ReadOnlyCollection<DbParameter> GetParametersForKinds(QueryClauseKind[] kinds)
    {
        // 预分配至全参数量上限——绝大多数查询全部子句类别都被选中，扩容为零
        var parameters = new List<DbParameter>(_parameters.Count);
        foreach (QueryClause clause in _clauses)
        {
            if (!kinds.Contains(clause.Kind)) continue;
            parameters.AddRange(clause.Parameters);
        }
        return parameters.AsReadOnly();
    }

    private void AppendComments(ref ValueStringBuilder builder)
    {
        // BuildSql 热路径：手写循环替代 LINQ Where（每次查询省委托+迭代器分配）
        foreach (QueryClause clause in _clauses)
        {
            if (clause.Kind != QueryClauseKind.Comment) continue;
            builder.Append(clause.Sql);
            builder.Append(' ');
        }
    }

    private void AppendCtes(ref ValueStringBuilder builder)
    {
        bool first = true;
        foreach (QueryClause clause in _clauses)
        {
            if (clause.Kind != QueryClauseKind.CommonTableExpression) continue;
            builder.Append(first ? "WITH " : ", ");
            builder.Append(clause.Sql);
            first = false;
        }
        if (!first) builder.Append(' ');
    }

    /// <summary>组装 WHERE 段：默认过滤（软删/租户）与用户子句组恒以 AND 组合——
    /// <c>WHERE d1 AND d2 AND ((A) OR (B))</c>。用户 OR 被括组隔离，无法绕过默认过滤（ITM-401）。</summary>
    private void AppendWhereSection(ref ValueStringBuilder builder)
    {
        bool hasDefault = HasClause(QueryClauseKind.DefaultFilter);
        bool hasUser = HasClause(QueryClauseKind.Where);
        if (!hasDefault && !hasUser) return;

        builder.Append("WHERE ");
        // 默认过滤（软删/租户）恒以 AND 前置；用户 OR 无法绕过默认过滤（ITM-401 根治）。
        AppendClauseKind(ref builder, QueryClauseKind.DefaultFilter, separator: "AND ");
        if (hasUser)
        {
            if (hasDefault) builder.Append("AND (");
            AppendClauseKind(ref builder, QueryClauseKind.Where, separator: null);
            if (hasDefault)
            {
                builder.TrimEnd();
                builder.Append(") ");
            }
        }
    }

    /// <summary>追加指定类别的全部子句。separator 用于条目间分隔（如 "AND "）。
    /// 同一调用负责遍历 _clauses 内的全部目标类别子句，避免重复循环。</summary>
    private void AppendClauseKind(
        ref ValueStringBuilder builder, QueryClauseKind kind, string? separator)
    {
        bool first = true;
        foreach (QueryClause clause in _clauses)
        {
            if (clause.Kind != kind) continue;
            if (!first && separator is not null) builder.Append(separator);
            builder.Append(clause.Sql);
            builder.Append(' ');
            first = false;
        }
    }

    private void AppendClauses(ref ValueStringBuilder builder, QueryClauseKind kind)
    {
        foreach (QueryClause clause in _clauses)
        {
            if (clause.Kind != kind) continue;
            builder.Append(clause.Sql);
            builder.Append(' ');
        }
    }

    private string? BuildLimitClause()
    {
        if (!_take.HasValue && !_skip.HasValue) return null;
        if (!_take.HasValue)
        {
            // SQLite 的 OFFSET 必须伴随 LIMIT（裸 OFFSET 是语法错误）——LIMIT -1 表示无上限；
            // PG 支持裸 OFFSET；MySQL 用极大值哨兵（ITM-407）
            return _dialect switch
            {
                SqlDialect.MySql => $"LIMIT {_skip!.Value}, 18446744073709551615",
                SqlDialect.Sqlite => $"LIMIT -1 OFFSET {_skip!.Value}",
                _ => $"OFFSET {_skip!.Value}"
            };
        }
        return _dialect switch
        {
            SqlDialect.MySql => $"LIMIT {_skip ?? 0}, {_take.Value}",
            _ => $"LIMIT {_take.Value} OFFSET {_skip ?? 0}"
        };
    }

    private static string GetRegisteredTableName(Type entityType)
        => PalORM_Runtime.TableNames.TryGetValue(entityType, out string? tableName)
            ? tableName
            : throw new InvalidOperationException(
                $"Type '{entityType.Name}' is not registered; ensure it has [Table] and the source generator ran.");

    private static string ValidateSqlComment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        // 同时拒绝 /*：PostgreSQL 块注释支持嵌套，未配对的 /* 会让整条语句解析失败。
        if (value.Contains("*/", StringComparison.Ordinal)
            || value.Contains("/*", StringComparison.Ordinal)
            || value.Contains('\0'))
        {
            throw new ArgumentException("SQL 注释不能包含注释定界符（/* 或 */）或 NUL 字符。", nameof(value));
        }
        return value;
    }

    private string GetQualifiedColumnName<TKey>(Expression<Func<T, TKey>> member)
        => $"{_quoteIdentifier(_cteName ?? _tableName)}." +
            $"{_quoteIdentifier(GetColumnName(member))}";

    private static string GetColumnName<TEntity, TKey>(Expression<Func<TEntity, TKey>> member)
    {
        string propertyName = GetMemberName(member);
        return PalORM_Runtime.PropertyToColumn.TryGetValue(typeof(TEntity), out var mapping)
            && mapping.TryGetValue(propertyName, out string? columnName)
            ? columnName
            : propertyName;
    }

    private static string GetMemberName<TEntity, TKey>(Expression<Func<TEntity, TKey>> member)
    {
        if (member.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        if (member.Body is UnaryExpression { Operand: MemberExpression unaryExpression }) return unaryExpression.Member.Name;
        throw new InvalidOperationException($"Cannot resolve member name from {member.Body}");
    }
}

/// <summary>Provider 能力聚合——把 dialect/factory/interceptors/paramFactory/quoteIdentifier/
/// operationState/commandTimeout 七项打包为单参数，消除 QueryBuilder 14 参 ctor 的 S107 警告。
/// 一次构造，多个 QueryBuilder 实例共享。</summary>
internal sealed record QueryBuilderServices<T>(
    SqlDialect Dialect,
    IRowFactory<T> Factory,
    List<IQueryInterceptor> Interceptors,
    Func<string, object?, DbParameter> ParamFactory,
    Func<string, string> QuoteIdentifier,
    SessionOperationState OperationState,
    TimeSpan CommandTimeout) where T : class, new();

/// <summary>QueryBuilder 构造上下文——把 Services + 连接 + 表元数据 + 读路由 + 缓存全部聚合。
/// 用 record 而非 struct：成员复杂、生命周期跨多个 QueryBuilder 实例（每次查询从 DataSession 派生），
/// 引用语义更自然。</summary>
internal sealed record QueryBuilderContext<T>(
    DbConnection Connection,
    QueryBuilderServices<T> Services,
    string TableName,
    IReadOnlyList<string> ColumnNames,
    Func<DbConnection>? ReadConnFactory = null,
    IQueryCache? QueryCache = null,
    bool ValidateColumnOrder = false,
    Func<DbConnection, CancellationToken, Task>? ReadConnInitializer = null) where T : class, new();
