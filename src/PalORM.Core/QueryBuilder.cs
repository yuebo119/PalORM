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
    /// <summary>读路由连接提供者——v5.6 起由会话级复用：提供者首次建连并执行 Provider
    /// 初始化，之后返回同一连接（原为每次查询新建连接的工厂 + 初始化器两件套）。</summary>
    internal readonly Func<CancellationToken, ValueTask<DbConnection>>? _readConnProvider;
    internal readonly SqlDialect _dialect;
    /// <summary>r5-S2：会话隔离级别（WithIsolationLevel 透传，null=驱动默认）。</summary>
    internal readonly System.Data.IsolationLevel? _isolationLevel;
    internal readonly bool _validateColumnOrder;
    internal readonly Func<string, string> _quoteIdentifier;
    // v3.1: 字段类型 IRowFactory<T> → Func<DbDataReader, T>——消除接口虚分发，每行调用直接 invoke 委托。
    internal readonly Func<DbDataReader, T> _factory;
    internal readonly List<IQueryInterceptor> _interceptors;
    internal readonly Func<string, object?, DbParameter> _paramFactory;
    internal readonly string _tableName;
    internal readonly IReadOnlyList<string> _columnNames;
    internal readonly SessionOperationState _operationState;
    /// <summary>会话弹性策略快照（From&lt;T&gt;() 时捕获，与 _commandTimeout 同口径）——
    /// 只读执行管线据此决定是否经重试/熔断。WithRetry 等在 From 之后调用不回灌已存在的
    /// builder（门禁禁止与飞行查询并发变更，快照语义见 WithRetry 文档）。</summary>
    internal readonly ResilienceExecutor _resilience;
    internal TimeSpan _commandTimeout;
    internal List<QueryClause> _clauses;
    // T5a：平铺参数列表已删除——QueryClause 自带本子句参数（Parameters），扁平视图
    // 只在执行期按 Kind 过滤时组装（GetParametersForKinds 本就从子句读取）。
    // 此处仅保留计数，供参数全局编号（@pN 跨子句递增）与 WhereIn 65535 守卫使用。
    // 删除它使 AddClause 的写时复制从两列表降为单列表（每次 AddClause 少 2 次分配）。
    internal int _parameterCount;
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
    /// <summary>ITM-763(r21)：参数名 → [SensitiveData] 掩码。Set() 写入敏感列时登记，
    /// 执行管线经 QueryContext.SensitiveParameterMasks 交给拦截器脱敏。null = 无敏感参数
    /// （零分配；绝大多数实体无敏感列）。CloneForExecution 深拷贝（与子句参数同纪律）。</summary>
    internal Dictionary<string, string>? _sensitiveMasks;
    internal readonly IQueryCache _queryCache;

    internal QueryBuilder(QueryBuilderContext<T> ctx)
    {
        _validateColumnOrder = ctx.ValidateColumnOrder;
        _conn = ctx.Connection;
        _readConnProvider = ctx.ReadConnProvider;
        _queryCache = ctx.QueryCache ?? CacheStore.Default;
        _dialect = ctx.Services.Dialect;
        _isolationLevel = ctx.Services.IsolationLevel;
        _quoteIdentifier = ctx.Services.QuoteIdentifier;
        _factory = ctx.Services.Factory;
        _interceptors = ctx.Services.Interceptors;
        _paramFactory = ctx.Services.ParamFactory;
        _tableName = ctx.TableName;
        _columnNames = ctx.ColumnNames;
        _operationState = ctx.Services.OperationState;
        _resilience = ctx.Services.Resilience;
        _commandTimeout = ctx.Services.CommandTimeout;
        // v5.6：初始容量 0——AddClause 的写时复制按 Count+4 预留（见 AddClause 注释），
        // 首次写入即拿到足够余量，故预先分配的空数组只会成为首个子句时被丢弃的垃圾
        // （原 (4)/(8) 在每次查询上白分配 4 个 QueryClause 槽与 8 个参数槽）。
        _clauses = new List<QueryClause>();
        _parameterCount = 0;
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
        _sensitiveMasks = null;
    }

    /// <summary>链式追加 WHERE/AND 条件。用户条件整体括号包裹并与默认过滤（软删/租户）
    /// 分组组合：WHERE defaults AND ((A) OR (B))——用户 OR 无法绕过默认过滤（ITM-401 根治）。</summary>
    public QueryBuilder<T> Where(FormattableString clause)
    {
        ArgumentNullException.ThrowIfNull(clause);  // ITM-664：null 在 BindFormattableString 处 NRE，入口显式拒绝
        AddParenthesizedClause(
            HasClause(QueryClauseKind.Where) ? "AND " : "", clause);
        return this;
    }

    /// <summary>链式追加 OR 条件——仅与既有用户条件 OR 组合；默认过滤（软删/租户）恒以 AND 前置，不受影响。
    /// <para>首个用户子句使用 OrWhere 时无既有条件可 OR，语义等价 <see cref="Where"/>。</para></summary>
    public QueryBuilder<T> OrWhere(FormattableString clause)
    {
        ArgumentNullException.ThrowIfNull(clause);  // ITM-664
        // 用户子句在独立分组内组合，默认过滤恒以 AND 前置（AppendWhereSection）——
        // 首个用户子句用 OrWhere 时无既有条件可 OR，语义等价 Where。
        AddParenthesizedClause(
            HasClause(QueryClauseKind.Where) ? "OR " : "", clause);
        return this;
    }

    private void AddParenthesizedClause(string prefix, FormattableString clause)
    {
        var (sql, parameters) = BindFormattableString(clause);
        // ITM-745(r20)：空/空白子句生成 "()" / "AND ()" 非法 SQL，晚失败在 DB 侧——
        // 入口显式拒绝（Raw 已有同类守卫，此处统一 Where/OrWhere/Having/With 一族）。
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException(
                "Query clause must not be empty or whitespace; an empty clause produces invalid SQL.", nameof(clause));
        AddClause(QueryClauseKind.Where, $"{prefix}({sql})", parameters);
    }

    /// <summary>排序。重复调用退化为多键续排（等价 ThenBy）——避免生成双 ORDER BY 非法 SQL（ITM-306）。</summary>
    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> member, bool descending = false)
    {
        AddOrderBy(member, descending);
        return this;
    }

    /// <summary>降序排序——<see cref="OrderBy{TKey}"/> 的 <c>descending: true</c> 便捷形态。
    /// <para>补此方法是因为 <c>docs/API参考.md</c> 一直把它和 <see cref="ThenByDescending{TKey}"/>
    /// 列为可用 API，而源码里从未存在：调用方写出 <c>.OrderByDescending(x =&gt; x.Id)</c> 时，
    /// 编译器会去匹配 LINQ 的 <c>OrderByDescending</c> 扩展并抛出难以归因的
    /// CS0411「无法推断类型参数」，而不是"方法不存在"。</para></summary>
    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> member)
        => OrderBy(member, descending: true);

    /// <summary>降序次级排序键——<see cref="ThenBy{TKey}"/> 的 <c>descending: true</c> 便捷形态。</summary>
    public QueryBuilder<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> member)
        => ThenBy(member, descending: true);

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
        // ITM-663：空数组合法但 _selectColumns=[] 与"未调用（null=全列）"语义分叉，
        // 生成 SELECT  FROM 非法 SQL——入口显式拒绝。
        if (members.Length == 0)
            throw new ArgumentException("Select requires at least one member.", nameof(members));
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
        => AddInClause(member, values, negated: false, nameof(WhereIn));

    /// <summary>追加参数化 NOT IN 条件，与既有条件 AND 组合。
    /// <para>列名经表名/CTE 名限定（同 <see cref="WhereIn{TValue}"/>，ITM-641）。</para>
    /// <para>空集合为 no-op（排除空集等于不过滤）；超过 500 个值按批次切分为多个 NOT IN 片段 AND 组合。</para></summary>
    public QueryBuilder<T> WhereNotIn<TValue>(Expression<Func<T, TValue>> member, IEnumerable<TValue> values)
        => AddInClause(member, values, negated: true, nameof(WhereNotIn));

    /// <summary>WhereIn/WhereNotIn 共享内核——两者此前逐字重复，仅 IN/NOT IN 与 OR/AND 连接词不同。
    /// <para><b>v5.6 单缓冲</b>：原实现每批一个 <c>string[]</c> + <c>string.Join</c> + 插值，
    /// 最后再对批次集合 <c>string.Join</c> 一次；改为单个 <see cref="ValueStringBuilder"/>
    /// 顺序写出整条 IN 体。实测 500 值省 21.8KB（76%）、2000 值省 122.3KB（64%）
    /// 的字符串与列表 churn（参数对象是驱动固有的，不在收益内）。</para>
    /// <para>SQL 文本逐字节不变：<c>(col IN (@p0, @p1) OR col IN (@p2))</c>，既有 WHERE 时前缀 <c>AND </c>。</para></summary>
    private QueryBuilder<T> AddInClause<TValue>(Expression<Func<T, TValue>> member,
        IEnumerable<TValue> values, bool negated, string callerName)
    {
        ArgumentNullException.ThrowIfNull(values);
        string column = GetQualifiedColumnName(member);
        IReadOnlyList<TValue> items = values as IReadOnlyList<TValue> ?? values.ToList();
        if (items.Count == 0)
        {
            // 空 IN 恒假（IN () 是非法 SQL）；空 NOT IN 是 no-op（排除空集等于不过滤）
            if (!negated)
                AddClause(QueryClauseKind.Where, HasClause(QueryClauseKind.Where) ? "AND 1=0" : "1=0");
            return this;
        }
        // ITM-514: 分批规避单条 IN 的参数上限，但参数总量仍受协议约束——超 65535（PG 协议 int16 上限，
        // 最严方言）应改用临时表 JOIN 或分批查询，而非静默生成越界 SQL。
        // ITM-562: 判定按"存量 + 增量"累计——两次 40k 的 WhereIn 各自增量合规但总量越界，
        // 只查增量会静默通过、运行期 PG 协议层才报错。
        if (_parameterCount + items.Count > 65535)
            throw new ArgumentException(
                $"{callerName} received {items.Count} values on a builder holding {_parameterCount} parameters; " +
                "the total exceeds the 65535 bind-parameter limit (PostgreSQL protocol max). " +
                "Use a temp table join or split the query into batches.", nameof(values));

        string operatorName = negated ? " NOT IN (" : " IN (";
        string batchSeparator = negated ? " AND " : " OR ";
        // O2 预分配：参数总量在进循环前已知
        var parameters = new List<DbParameter>(items.Count);
        var sb = new ValueStringBuilder(stackalloc char[512]);
        try
        {
            if (HasClause(QueryClauseKind.Where)) sb.Append("AND ");
            sb.Append('(');
            AppendInBatches(ref sb, items, parameters, column, operatorName, batchSeparator);
            sb.Append(')');
            AddClause(QueryClauseKind.Where, sb.ToString(), parameters);
        }
        finally { sb.Dispose(); }
        return this;
    }

    /// <summary>把 items 按 500 一批写成 <c>col IN (@p…)</c> 片段，批间以 <paramref name="batchSeparator"/>
    /// 连接。参数按写入顺序创建并追加到 <paramref name="parameters"/>，参数名序号与旧实现逐位一致。</summary>
    private void AppendInBatches<TValue>(ref ValueStringBuilder sb, IReadOnlyList<TValue> items,
        List<DbParameter> parameters, string column, string operatorName, string batchSeparator)
    {
        const int maxBatch = 500;
        for (int start = 0; start < items.Count; start += maxBatch)
        {
            int end = Math.Min(start + maxBatch, items.Count);
            if (start > 0) sb.Append(batchSeparator);
            sb.Append(column);
            sb.Append(operatorName);
            for (int index = start; index < end; index++)
            {
                if (index > start) sb.Append(", ");
                DbParameter parameter = CreateParameter(items[index], parameters.Count);
                parameters.Add(parameter);
                sb.Append(parameter.ParameterName);
            }
            sb.Append(')');
        }
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
        ArgumentNullException.ThrowIfNull(clause);  // ITM-664
        AddFormattableClause(QueryClauseKind.Having,
            HasClause(QueryClauseKind.Having) ? "AND " : "HAVING ", clause);
        return this;
    }

    /// <summary>追加 UPDATE 的 SET 赋值项，值参数化绑定。至少一个 Set 才能执行 ExecuteNonQueryAsync。</summary>
    public QueryBuilder<T> Set<TValue>(Expression<Func<T, TValue>> member, TValue value)
    {
        DbParameter parameter = CreateParameter(value);
        string columnName = GetColumnName(member);
        // ITM-763(r21)：敏感列掩码登记——执行管线经 QueryContext.SensitiveParameterMasks
        // 交给拦截器（AuditInterceptor 据此以掩码替代真实值）。注册表由生成器发射
        // （SensitiveColumnMasks），仅实体有敏感列时才产生分配。
        if (PalORM_Runtime.SensitiveColumnMasks.TryGetValue(typeof(T), out var columnMasks)
            && columnMasks.TryGetValue(columnName, out string? mask))
            (_sensitiveMasks ??= new Dictionary<string, string>(4))[parameter.ParameterName] = mask;
        string prefix = HasClause(QueryClauseKind.Set) ? ", " : "SET ";
        AddClause(QueryClauseKind.Set,
            $"{prefix}{_quoteIdentifier(columnName)} = {parameter.ParameterName}", [parameter]);
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
        ArgumentNullException.ThrowIfNull(subquery);  // ITM-664
        var (sql, parameters) = BindFormattableString(subquery);
        // ITM-745(r20)：空 CTE 子查询生成 `AS ()` 非法 SQL——入口拒绝
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException(
                "CTE subquery must not be empty or whitespace.", nameof(subquery));
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

    /// <summary>以调用方源码位置为 Tag。
    /// r19/ITM-705：只取文件名（[CallerFilePath] 是编译机绝对路径——完整路径随 SQL 注释
    /// 发送到数据库服务器会泄露内部目录结构）；行号+成员名保留排查价值。
    /// 生产环境仍建议使用 Tag(name) 传业务标识。</summary>
    public QueryBuilder<T> TagWithCaller([CallerMemberName] string? member = null,
        [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
        => Tag($"{System.IO.Path.GetFileName(file)}:{line} {member}");

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
    /// 命中实体应视为只读；需要修改时先自行深拷贝，否则会污染缓存与其他调用方（ITM-308）。
    /// <para><b>ITM-736(r20) 多租户警告</b>：缓存键<b>完全由调用方提供</b>，不含租户/软删维度，
    /// 且未注入 <c>DbOptions.QueryCache</c> 时各会话共享进程级默认实例——同一 key 会在不同
    /// 租户/过滤上下文中复用同一份数据。多租户或 <c>IgnoreFilters()</c> 场景必须把租户
    /// 标识编入 key（如 <c>$"products:{tenantId}"</c>），或经 <c>DbOptions.QueryCache</c>
    /// 为每租户注入独立缓存（ADR-C：隔离责任在调用方 key 约定）。</para></summary>
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
            throw new ArgumentException(
                "Cannot bind the transaction: it has been disposed (its Connection is null).", nameof(tran));
        if (!ReferenceEquals(tran.Connection, _conn))
            throw new ArgumentException(
                "The transaction must belong to the QueryBuilder's primary connection.", nameof(tran));
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

    /// <summary>本构建器的 SQL 方言（只读）。ITM-770(r21)：方言敏感的扩展方法（如 PG 的
    /// WhereJson）据此守卫——误用于其他方言的会话时明确失败而非生成静默错误的 SQL。</summary>
    public SqlDialect Dialect => _dialect;

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
            || !_useReadRoute || _readConnProvider is null)
            return ValueTask.FromResult(ConnectionLease.Borrow(_conn));

        return AcquireReadLeaseAsync(cancellationToken);
    }

    /// <summary>读路由租约——连接来自会话级复用，租约只是借用标记（不释放连接）。
    /// 抽为独立方法而非在 <see cref="AcquireConnectionLeaseAsync"/> 内 await：后者是每查询
    /// 必经的热路径，同步返回分支不应被 async 状态机包裹。</summary>
    private async ValueTask<ConnectionLease> AcquireReadLeaseAsync(CancellationToken cancellationToken)
    {
        DbConnection readConnection = await _readConnProvider!(cancellationToken).ConfigureAwait(false);
        return ConnectionLease.Borrow(readConnection);
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
                _quoteIdentifier, _operationState, _resilience, _commandTimeout, _isolationLevel),  // r6-N1：克隆透传——r5-S2 曾在此断裂致条件分支死代码
            _tableName, _columnNames, _readConnProvider, _queryCache, _validateColumnOrder))
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
            _sensitiveMasks = _sensitiveMasks is null ? null : new Dictionary<string, string>(_sensitiveMasks),
            // v4.6：同步位掩码到克隆体
            _clauseBitmask = _clauseBitmask
        };
        // v5.6：ctor 现以容量 0 起步（见 ctor 注释）。本方法是唯一绕过 AddClause 直接
        // 写 _clauses/子句参数的路径，故在此按源 Count 一次性预分配——否则以下循环
        // 会走 List 的 0→4→8→16 逐级扩容。余量不与源 builder 共享（新列表）。
        clone._clauses = new List<QueryClause>(_clauses.Count);
        foreach (QueryClause clause in _clauses)
        {
            var parameters = new List<DbParameter>(clause.Parameters.Count);
            foreach (DbParameter parameter in clause.Parameters)
            {
                DbParameter copy = clone._paramFactory(parameter.ParameterName, parameter.Value);
                parameters.Add(copy);
            }
            clone._parameterCount += parameters.Count;
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
        // r8-D1(P1)：Take/Skip 是字段非子句位掩码，守卫族结构性看不见——静默丢弃 LIMIT
        // 会扩大更新范围（用户期望限 N 行实为全量匹配行），比其他子句丢弃后果更重
        if (_take > 0 || _skip > 0)
            throw new NotSupportedException(
                "UPDATE does not support Take/Skip; the LIMIT would be silently dropped and widen the affected rows. " +
                "Use Where() to bound the update, or BulkUpdateAsync for row-by-row.");
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

    /// <summary>追加 SELECT 子句的列列表：显式列 vs 全列（带表名前缀）。
    /// <para><b>v5.6 缓存</b>：全列形态的输入（表名 + 列名数组 + 方言引用规则）全部来自
    /// 编译期注册表，同一 (Type, Dialect) 恒产出同一字符串，故按 (Type, Dialect) 缓存。
    /// 命中路径为 O(1) 查找且零分配；未命中才逐列 QuoteIdentifier 构建
    /// （4 列实体实测省 5 次引用分配，约 280B）。</para>
    /// <para>本路径需要<b>表名限定</b>（JOIN 下防 ambiguous column，ITM-641），故用
    /// <see cref="DataSessionCache.QualifiedSelectColumnsCache"/>——它与 GetAllAsync 用的
    /// 裸列清单缓存键相同但值不同，不可混用。</para>
    /// <para>带 CTE 或显式投影时不走缓存：前者的限定名是 CTE 名而非表名，
    /// 后者的列集由调用方决定。</para></summary>
    private void AppendSelectColumns(ref ValueStringBuilder sb)
    {
        if (_selectColumns is not null)
        {
            // ITM-622：构建时限定（sourceName 为当前 FROM 源）
            string source = _cteName ?? _tableName;
            for (int index = 0; index < _selectColumns.Length; index++)
            {
                if (index > 0) sb.Append(", ");
                sb.Append(_quoteIdentifier(source));
                sb.Append('.');
                sb.Append(_quoteIdentifier(_selectColumns[index]));
            }
            return;
        }
        if (_cteName is null)
        {
            sb.Append(GetQualifiedColumnList());
            return;
        }
        // 有 CTE：限定名为 CTE 名，与缓存的表名限定形态不同，逐列构建
        string quotedCte = _quoteIdentifier(_cteName);
        for (int index = 0; index < _columnNames.Count; index++)
        {
            if (index > 0) sb.Append(", ");
            sb.Append(quotedCte);
            sb.Append('.');
            sb.Append(_quoteIdentifier(_columnNames[index]));
        }
    }

    /// <summary>取本实体本方言的表名限定列清单，走共享缓存。
    /// <para>命中路径用 <c>TryGetValue</c>（无委托、无闭包）。未命中时先构建再发布——
    /// QueryBuilder 是 struct，lambda 不能捕获 <c>this</c>（CS1673），且 <c>GetOrAdd</c>
    /// 的值重载不构造委托。并发下同一 (Type, Dialect) 可能被构建多次，只有首个值进入缓存，
    /// 冗余构建的代价是每键一次的少量分配，正确性无影响。</para></summary>
    private string GetQualifiedColumnList()
    {
        (Type, SqlDialect) key = (typeof(T), _dialect);
        if (DataSessionCache.QualifiedSelectColumnsCache.TryGetValue(key, out string? cached))
            return cached;
        string built = BuildQualifiedColumnList();
        return DataSessionCache.QualifiedSelectColumnsCache.GetOrAdd(key, built);
    }

    /// <summary>逐列构建限定列清单——仅缓存未命中时执行。</summary>
    private string BuildQualifiedColumnList()
    {
        string quotedSource = _quoteIdentifier(_tableName);
        var sb = new ValueStringBuilder(stackalloc char[256]);
        try
        {
            for (int index = 0; index < _columnNames.Count; index++)
            {
                if (index > 0) sb.Append(", ");
                sb.Append(quotedSource);
                sb.Append('.');
                sb.Append(_quoteIdentifier(_columnNames[index]));
            }
            return sb.ToString();
        }
        finally { sb.Dispose(); }
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
        => FormattableSqlFormatter.FormatCached(sql.Format, baseIndex, sql.ArgumentCount);

    private QueryBuilder<T> AddJoin<TJoin>(string joinType, FormattableString onClause)
        where TJoin : class, new()
    {
        ArgumentNullException.ThrowIfNull(onClause);  // ITM-664
        string joinTable = GetRegisteredTableName(typeof(TJoin));
        var (sql, parameters) = BindFormattableString(onClause);
        // ITM-757(r21)：空/空白 ON 生成 `JOIN t ON ()` 非法 SQL 晚失败——入口拒绝
        //（与 Where/OrWhere/Having/With 的 ITM-745 守卫同族，Join 一族此前漏覆盖）。
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException(
                "JOIN ON clause must not be empty or whitespace; an empty clause produces invalid SQL.", nameof(onClause));
        AddClause(QueryClauseKind.Join,
            $"{joinType} JOIN {_quoteIdentifier(joinTable)} ON ({sql})", parameters);
        return this;
    }

    private (string Sql, IReadOnlyList<DbParameter> Parameters) BindFormattableString(FormattableString sql)
    {
        int baseIndex = _parameterCount;
        string formatted = FormatFormattableSql(sql, baseIndex);
        var parameters = new List<DbParameter>(sql.ArgumentCount);
        for (int i = 0; i < sql.ArgumentCount; i++)
            parameters.Add(_paramFactory(GetParameterName(baseIndex + i), sql.GetArgument(i)));
        return (formatted, parameters);
    }

    private void AddFormattableClause(QueryClauseKind kind, string prefix, FormattableString formattable)
    {
        var (sql, parameters) = BindFormattableString(formattable);
        // ITM-745(r20)：同 AddParenthesizedClause——空子句生成 "HAVING " 无内容等非法 SQL
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException(
                "Query clause must not be empty or whitespace; an empty clause produces invalid SQL.", nameof(formattable));
        AddClause(kind, prefix + sql, parameters);
    }

    private DbParameter CreateParameter(object? value, int localOffset = 0)
        => _paramFactory(GetParameterName(_parameterCount + localOffset), value);

    private void AddClause(QueryClauseKind kind, string sql,
        IReadOnlyList<DbParameter>? parameters = null)
    {
        // 无条件写时复制：struct 副本共享列表引用，任何一次性"已复制"标志都会随副本
        // 一起被拷贝而失效（QUERY-001 场景 B/C）。每次写入先复制，保证副本间完全隔离。
        // v5.6：复制按 Count+4 预留容量。原实现取精确容量（Count），复制后紧接着的 Add
        // 必然再触发一次扩容，且下次复制又要把更大的一批元素整体搬一遍；预留 4 槽后
        // 该子句链上的总分配降为约一半（16 子句实测 7624B→4365B）。容量余量不与任何
        // 副本共享（复制出来的是新数组），写时复制语义不变。
        var clauses = new List<QueryClause>(_clauses.Count + 4);
        clauses.AddRange(_clauses);
        _clauses = clauses;
        IReadOnlyList<DbParameter> ownedParameters = parameters ?? Array.Empty<DbParameter>();
        _clauses.Add(new QueryClause(kind, sql, ownedParameters));
        // v4.6：同步设置位掩码
        _clauseBitmask |= 1 << (int)kind;
        _parameterCount += ownedParameters.Count;
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

    /// <summary>ITM-715(r20)：是否存在"非子句形态的执行修饰符"——Take/Skip/Select/
    /// AsSplitQuery/WithCache 是字段而非子句，<see cref="CountUserSubstantiveClauses"/> 对它们
    /// 结构性失明。QueryMultipleAsync 逐字执行提供的 SQL，这些修饰符会被静默忽略；同族守卫
    /// 在 BuildUpdateSql 已单独拒绝 Take/Skip（r8-D1），此处补齐查询入口的一致性。
    /// <para>ITM-757(r21)：Take/Skip 用值判定（与 BuildUpdateSql 的 r8-D1 口径一致）——
    /// Take(0)/Skip(0) 是明确 no-op，存在性判定会误拒合法形态。</para></summary>
    internal bool HasIgnoredExecutionModifiers => _take > 0 || _skip > 0
        || _selectColumns is not null || _splitQuery || _cacheKey is not null;

    // v4.1：去 AsReadOnly 包装（省 1 次 ReadOnlyCollection 分配），改用 Array.IndexOf 去 LINQ 迭代器
    private List<DbParameter> GetParametersForKinds(QueryClauseKind[] kinds)
    {
        // 预分配至全参数量上限--绝大多数查询全部子句类别都被选中，扩容为零
        var parameters = new List<DbParameter>(_parameterCount);
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
            throw new ArgumentException(
                "SQL comment must not contain comment delimiters (/* or */) or NUL characters.", nameof(value));
        }
        return value;
    }

    private string GetQualifiedColumnName<TKey>(Expression<Func<T, TKey>> member)
        => MemberResolver.GetQualifiedColumnName(member, _cteName ?? _tableName, _quoteIdentifier);

    private static string GetColumnName<TEntity, TKey>(Expression<Func<TEntity, TKey>> member)
        => MemberResolver.GetColumnName(member);
}

/// <summary>Provider 能力聚合——把 dialect/factory/interceptors/paramFactory/quoteIdentifier/
/// operationState/resilience/commandTimeout 八项打包为单参数，消除 QueryBuilder 14 参 ctor 的 S107 警告。
/// 一次构造，多个 QueryBuilder 实例共享。
/// <para><b>v5.6 为什么是 struct</b>：本类型每个 From&lt;T&gt;() 构造一次。作为 class 时
/// 每次查询产生一笔堆分配（实测与 Context 合计 200B/查询）；改为 readonly record struct
/// 后随 ctor 参数走栈，零堆分配。字段全为引用/值类型，按值传递成本是栈上的字节拷贝。</para></summary>
internal readonly record struct QueryBuilderServices<T>(
    SqlDialect Dialect,
    Func<DbDataReader, T> Factory,
    List<IQueryInterceptor> Interceptors,
    Func<string, object?, DbParameter> ParamFactory,
    Func<string, string> QuoteIdentifier,
    SessionOperationState OperationState,
    ResilienceExecutor Resilience,
    TimeSpan CommandTimeout,
    System.Data.IsolationLevel? IsolationLevel = null)  // r5-S2：会话隔离级别透传（ToPageAsync 自开事务 honoring WithIsolationLevel）
    where T : class, new();

/// <summary>QueryBuilder 构造上下文——把 Services + 连接 + 表元数据 + 读路由 + 缓存全部聚合。
/// 用 record struct 而非 class：仅用于构造期传参，生命周期不超出 ctor；作为 class 时
/// 每次 From&lt;T&gt;() 产生一笔堆分配（v5.6 实测与 Services 合计 200B/查询），struct 随
/// ctor 参数走栈。</summary>
internal readonly record struct QueryBuilderContext<T>(
    DbConnection Connection,
    QueryBuilderServices<T> Services,
    string TableName,
    IReadOnlyList<string> ColumnNames,
    Func<CancellationToken, ValueTask<DbConnection>>? ReadConnProvider = null,
    IQueryCache? QueryCache = null,
    bool ValidateColumnOrder = false) where T : class, new();
