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
    // v4.1 极致降内存：引用 ParameterNameCache 消除每次 $"@p{N}" 插值分配
    internal static string GetParameterName(int index) => ParameterNameCache.GetName(index);

    // v4.6：HasClause 位掩码 -- O(1) 判断子句存在，消除 List.Exists 的 O(n) 扫描 + Predicate 委托分配
    private int _clauseBitmask;
    internal DbConnection _conn;
    internal readonly Func<DbConnection>? _readConnFactory;
    internal readonly Func<DbConnection, CancellationToken, Task>? _readConnInitializer;
    internal readonly SqlDialect _dialect;
    internal readonly bool _validateColumnOrder;
    internal readonly Func<string, string> _quoteIdentifier;
    // v3.1: 字段类型 IRowFactory<T> → Func<DbDataReader, T>——消除接口虚分发，每行调用直接 invoke 委托。
    internal readonly Func<DbDataReader, T> _factory;
    internal readonly List<IQueryInterceptor> _interceptors;
    internal readonly Func<string, object?, DbParameter> _paramFactory;
    internal readonly string _tableName;
    internal readonly IReadOnlyList<string> _columnNames;
    internal readonly SessionOperationState _operationState;
    internal TimeSpan _commandTimeout;
    internal List<QueryClause> _clauses;
    internal List<DbParameter> _parameters;
    /// <summary>显式投影的裸列名（构建时才限定表/CTE 名）。</summary>
    internal string[]? _selectColumns;
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
        // v4.1：预分配常见容量，省首次 Add 的 T4 数组扩容
        _clauses = new List<QueryClause>(4);
        _parameters = new List<DbParameter>(8);
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
        // ITM-622：存裸列名，构建时以当前 FROM 源（_cteName ?? _tableName）限定——
        // 调用时点固化限定名会在 Select 先于 With(cte) 时投影指向旧表名
        // （与 OrderBy/GroupBy 的构建时动态求值不对称）。限定语义（ITM-537）不变。
        _selectColumns = members.Select(GetColumnName).ToArray();
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
    /// <para>列名经表名/CTE 名限定（ITM-641——JOIN 下与关联表同名列不再产生 ambiguous column，
    /// 与 OrderBy/GroupBy/AddWhereComparison 的 GetQualifiedColumnName 口径对齐）。</para>
    /// <para>空集合生成恒假条件 1=0（IN () 是非法 SQL）；超过 500 个值按批次切分为多个 IN 片段 OR 组合，规避各数据库参数上限。</para></summary>
    public QueryBuilder<T> WhereIn<TValue>(Expression<Func<T, TValue>> member, IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string column = GetQualifiedColumnName(member);
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
    /// <para>列名经表名/CTE 名限定（同 <see cref="WhereIn{TValue}"/>，ITM-641）。</para>
    /// <para>空集合为 no-op（排除空集等于不过滤）；超过 500 个值按批次切分为多个 NOT IN 片段 AND 组合。</para></summary>
    public QueryBuilder<T> WhereNotIn<TValue>(Expression<Func<T, TValue>> member, IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string column = GetQualifiedColumnName(member);
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

    /// <summary>追加 FOR UPDATE 行锁；skipLocked 为 true 时附加 SKIP LOCKED 跳过已锁行。需在事务内使用才有意义。
    /// <para>ITM-639 登记：SQLite 不支持 FOR UPDATE——构建不拒绝（DryRun/ToSql 预览与
    /// 测试用 SQLite 会话验证 SQL 形态是既定契约），执行期由 SQLite 报语法错误。</para></summary>
    public QueryBuilder<T> ForUpdate(bool skipLocked = false)
    {
        AddClause(QueryClauseKind.Lock, $"FOR UPDATE{(skipLocked ? " SKIP LOCKED" : "")}");
        return this;
    }

    /// <summary>追加 FOR SHARE 共享锁——允许并发读、阻止并发写。需在事务内使用才有意义。
    /// <para>ITM-639 登记：同 ForUpdate，SQLite 执行期报语法错误。</para></summary>
    public QueryBuilder<T> ForShare()
    {
        AddClause(QueryClauseKind.Lock, "FOR SHARE");
        return this;
    }

    /// <summary>追加调用方负责安全性的原始 SQL 片段。不得传入不可信内容。
    /// <para>ITM-645(r4) 契约登记：Raw 在 SELECT 构建中追加于 OrderBy 之后（全句尾、
    /// LIMIT 前——测试锁定的既定位置）；COUNT 构建中位于 Having 后（过滤段语义）。
    /// 组合 OrderBy+Raw 且 Raw 为 WHERE 补充形态（如 "AND deleted=0"）时页 SQL 会产
    /// 无效后缀——WHERE 补充请用 Where()，Raw 的位置语义是"尾部追加"。</para></summary>
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
        // ITM-637：已释放事务的 Connection 为 null——原统一报"不属于主连接"误导排查方向
        if (tran.Connection is null)
            throw new ArgumentException("事务已释放（Connection 为 null），无法绑定。", nameof(tran));
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
            _transaction = _transaction,
            // v4.6：同步位掩码到克隆体
            _clauseBitmask = _clauseBitmask
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
        => GetParametersForKinds(_splitQuery ? QueryClauseKinds.QuerySplit : QueryClauseKinds.Query);

    internal IReadOnlyList<DbParameter> GetCountParameters()
        => GetParametersForKinds(_splitQuery ? QueryClauseKinds.CountSplit : QueryClauseKinds.Count);

    internal IReadOnlyList<DbParameter> GetUpdateParameters()
        => GetParametersForKinds(QueryClauseKinds.Update);

    internal string BuildCountSql()
    {
        // ITM-642(r4)：Set 守卫对齐 BuildSql——`.Set().ToPageAsync()` 原本 COUNT 先真实
        // 执行（一轮 DB 往返+事务回滚）后页构建才抛，构建期拒绝消除无效往返。
        if (HasClause(QueryClauseKind.Set))
            throw new InvalidOperationException(
                "This builder has Set() clauses; COUNT would silently discard them. " +
                "Use ExecuteNonQueryAsync for UPDATE, or remove Set() for COUNT/paging.");
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
            // ITM-609：Raw 子句进 Count——与 BuildSql 的过滤语义对齐，否则 .Raw("AND deleted=0")
            // 后 ToPageAsync 的页查询生效而 Total 虚高（Raw 在 BuildSql 中位于 Having/OrderBy 后）。
            AppendClauses(ref sb, QueryClauseKind.Raw);
            sb.Append(") AS count_source");
            sb.TrimEnd(); return sb.ToString();
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
        // ITM-623：GroupBy/Having 同属"会被静默丢弃"（构建输出无这两类）——一并显式拒绝。
        if (HasClause(QueryClauseKind.Join) || HasClause(QueryClauseKind.OrderBy)
            || HasClause(QueryClauseKind.Lock) || HasClause(QueryClauseKind.Window)
            || HasClause(QueryClauseKind.GroupBy) || HasClause(QueryClauseKind.Having))
            throw new NotSupportedException(
                "UPDATE does not support Join/OrderBy/Lock/Window/GroupBy/Having clauses; they would be silently dropped. " +
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
            sb.TrimEnd(); return sb.ToString();
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
            // v4.4：直接写 VSB，消除中间 string 分配
            AppendLimitClause(ref sb);
            sb.Append(' ');
            AppendClauses(ref sb, QueryClauseKind.Lock);
            // v4.4：先 TrimEnd 再 ToString，省 1 次 string 分配（TrimEnd 前已用 VSB 原地裁剪）
            sb.TrimEnd();
            return sb.ToString();
        }
        finally { sb.Dispose(); }
    }

    /// <summary>追加 SELECT 子句的列列表：显式列 vs 全列（带表名前缀）。</summary>
    private void AppendSelectColumns(ref ValueStringBuilder sb)
    {
        string sourceName = _cteName ?? _tableName;
        if (_selectColumns is not null)
        {
            // ITM-622：构建时限定（sourceName 为当前 FROM 源）
            for (int index = 0; index < _selectColumns.Length; index++)
            {
                if (index > 0) sb.Append(", ");
                sb.Append(_quoteIdentifier(sourceName));
                sb.Append('.');
                sb.Append(_quoteIdentifier(_selectColumns[index]));
            }
            return;
        }
        // v4.4：sourceName 的 quote 提循环外，避免 N 列重复 quote 同一个值
        string quotedSource = _quoteIdentifier(sourceName);
        for (int index = 0; index < _columnNames.Count; index++)
        {
            if (index > 0) sb.Append(", ");
            sb.Append(quotedSource);
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
            parameters.Add(_paramFactory(GetParameterName(baseIndex + i), sql.GetArgument(i)));
        return (formatted, parameters);
    }

    private void AddFormattableClause(QueryClauseKind kind, string prefix, FormattableString formattable)
    {
        var (sql, parameters) = BindFormattableString(formattable);
        AddClause(kind, prefix + sql, parameters);
    }

    private DbParameter CreateParameter(object? value, int localOffset = 0)
        => _paramFactory(GetParameterName(_parameters.Count + localOffset), value);

    private void AddClause(QueryClauseKind kind, string sql,
        IReadOnlyList<DbParameter>? parameters = null)
    {
        // 无条件写时复制：struct 副本共享列表引用，任何一次性"已复制"标志都会随副本
        // 一起被拷贝而失效（QUERY-001 场景 B/C）。每次写入先复制，保证副本间完全隔离。
        _clauses = new List<QueryClause>(_clauses);
        _parameters = new List<DbParameter>(_parameters);
        IReadOnlyList<DbParameter> ownedParameters = parameters ?? Array.Empty<DbParameter>();
        _clauses.Add(new QueryClause(kind, sql, ownedParameters));
        // v4.6：同步设置位掩码
        _clauseBitmask |= 1 << (int)kind;
        foreach (DbParameter parameter in ownedParameters) _parameters.Add(parameter);
    }

    // v4.6：位掩码 O(1) 判断，消除 List.Exists 的 O(n) 扫描 + Predicate 委托分配
    private bool HasClause(QueryClauseKind kind)
        => (_clauseBitmask & (1 << (int)kind)) != 0;

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

    // v4.1：去 AsReadOnly 包装（省 1 次 ReadOnlyCollection 分配），改用 Array.IndexOf 去 LINQ 迭代器
    private List<DbParameter> GetParametersForKinds(QueryClauseKind[] kinds)
    {
        // 预分配至全参数量上限--绝大多数查询全部子句类别都被选中，扩容为零
        var parameters = new List<DbParameter>(_parameters.Count);
        foreach (QueryClause clause in _clauses)
        {
            if (Array.IndexOf(kinds, clause.Kind) < 0) continue;
            parameters.AddRange(clause.Parameters);
        }
        return parameters;
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

    // v4.4：直接写 ValueStringBuilder，消除中间 string 分配
    private void AppendLimitClause(ref ValueStringBuilder sb)
    {
        if (!_take.HasValue && !_skip.HasValue) return;
        if (!_take.HasValue)
        {
            switch (_dialect)
            {
                case SqlDialect.MySql:
                    sb.Append("LIMIT ");
                    sb.Append(_skip!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(", 18446744073709551615");
                    break;
                case SqlDialect.Sqlite:
                    sb.Append("LIMIT -1 OFFSET ");
                    sb.Append(_skip!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    sb.Append("OFFSET ");
                    sb.Append(_skip!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
            return;
        }
        switch (_dialect)
        {
            case SqlDialect.MySql:
                sb.Append("LIMIT ");
                sb.Append((_skip ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(", ");
                sb.Append(_take.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append("LIMIT ");
                sb.Append(_take.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(" OFFSET ");
                sb.Append((_skip ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
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
        => MemberResolver.GetQualifiedColumnName(member, _cteName ?? _tableName, _quoteIdentifier);

    private static string GetColumnName<TEntity, TKey>(Expression<Func<TEntity, TKey>> member)
        => MemberResolver.GetColumnName(member);
}

/// <summary>Provider 能力聚合——把 dialect/factory/interceptors/paramFactory/quoteIdentifier/
/// operationState/commandTimeout 七项打包为单参数，消除 QueryBuilder 14 参 ctor 的 S107 警告。
/// 一次构造，多个 QueryBuilder 实例共享。</summary>
internal sealed record QueryBuilderServices<T>(
    SqlDialect Dialect,
    Func<DbDataReader, T> Factory,
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
