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

    internal QueryBuilder(DbConnection conn, SqlDialect dialect, IRowFactory<T> factory,
        List<IQueryInterceptor> interceptors, Func<string, object?, DbParameter> paramFactory,
        Func<string, string> quoteIdentifier, string tableName,
        IReadOnlyList<string> columnNames, TimeSpan commandTimeout,
        SessionOperationState operationState,
        Func<DbConnection>? readConnFactory = null,
        IQueryCache? queryCache = null,
        bool validateColumnOrder = false)
    {
        _validateColumnOrder = validateColumnOrder;
        _conn = conn;
        _readConnFactory = readConnFactory;
        _queryCache = queryCache ?? CacheStore.Default;
        _dialect = dialect;
        _quoteIdentifier = quoteIdentifier;
        _factory = factory;
        _interceptors = interceptors;
        _paramFactory = paramFactory;
        _tableName = tableName;
        _columnNames = columnNames;
        _operationState = operationState;
        _commandTimeout = commandTimeout;
        _clauses = new List<QueryClause>();
        _parameters = new List<DbParameter>();
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

    /// <summary>链式追加 WHERE/AND 条件。用户条件整体括号包裹——含 OR 时
    /// AND 优先级会使默认过滤（软删/租户）对 OR 分支失效（ITM-307，纪律同 WhereIn）。</summary>
    public QueryBuilder<T> Where(FormattableString clause)
    {
        AddParenthesizedClause(
            HasClause(QueryClauseKind.Where) ? "AND " : "WHERE ", clause);
        return this;
    }

    public QueryBuilder<T> OrWhere(FormattableString clause)
    {
        AddParenthesizedClause(
            HasClause(QueryClauseKind.Where) ? "OR " : "WHERE ", clause);
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
        Func<string, string> quoteIdentifier = _quoteIdentifier;
        _selectColumns = string.Join(", ", members.Select(member => quoteIdentifier(GetColumnName(member))));
        return this;
    }

    public QueryBuilder<T> Take(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        _take = n;
        return this;
    }

    public QueryBuilder<T> Skip(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        _skip = n;
        return this;
    }

    public QueryBuilder<T> WhereIn<TValue>(Expression<Func<T, TValue>> member, IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string column = _quoteIdentifier(GetColumnName(member));
        var items = values as IReadOnlyList<TValue> ?? values.ToList();
        if (items.Count == 0)
        {
            AddClause(QueryClauseKind.Where, HasClause(QueryClauseKind.Where) ? "AND 1=0" : "WHERE 1=0");
            return this;
        }

        var batches = new List<string>();
        var parameters = new List<DbParameter>();
        const int maxBatch = 500;
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
            $"{(HasClause(QueryClauseKind.Where) ? "AND" : "WHERE")} ({string.Join(" OR ", batches)})",
            parameters);
        return this;
    }

    public QueryBuilder<T> WhereNotIn<TValue>(Expression<Func<T, TValue>> member, IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string column = _quoteIdentifier(GetColumnName(member));
        var items = values as IReadOnlyList<TValue> ?? values.ToList();
        if (items.Count == 0) return this;

        var batches = new List<string>();
        var parameters = new List<DbParameter>();
        const int maxBatch = 500;
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
            $"{(HasClause(QueryClauseKind.Where) ? "AND" : "WHERE")} ({string.Join(" AND ", batches)})",
            parameters);
        return this;
    }

    public QueryBuilder<T> InnerJoin<TJoin>(FormattableString onClause) where TJoin : class, new()
        => AddJoin<TJoin>("INNER", onClause);

    public QueryBuilder<T> LeftJoin<TJoin>(FormattableString onClause) where TJoin : class, new()
        => AddJoin<TJoin>("LEFT", onClause);

    public QueryBuilder<T> RightJoin<TJoin>(FormattableString onClause) where TJoin : class, new()
        => AddJoin<TJoin>("RIGHT", onClause);

    public QueryBuilder<T> GroupBy(Expression<Func<T, object?>> member)
    {
        string prefix = HasClause(QueryClauseKind.GroupBy) ? ", " : "GROUP BY ";
        AddClause(QueryClauseKind.GroupBy, prefix + _quoteIdentifier(GetColumnName(member)));
        return this;
    }

    public QueryBuilder<T> Having(FormattableString clause)
    {
        AddFormattableClause(QueryClauseKind.Having,
            HasClause(QueryClauseKind.Having) ? "AND " : "HAVING ", clause);
        return this;
    }

    public QueryBuilder<T> Set<TValue>(Expression<Func<T, TValue>> member, TValue value)
    {
        DbParameter parameter = CreateParameter(value);
        string prefix = HasClause(QueryClauseKind.Set) ? ", " : "SET ";
        AddClause(QueryClauseKind.Set,
            $"{prefix}{_quoteIdentifier(GetColumnName(member))} = {parameter.ParameterName}", [parameter]);
        return this;
    }

    public QueryBuilder<T> Include<TChild>(Expression<Func<T, object?>> fk,
        Expression<Func<TChild, object?>> pk) where TChild : class, new()
    {
        string childTable = GetRegisteredTableName(typeof(TChild));
        AddClause(QueryClauseKind.Join,
            $"INNER JOIN {_quoteIdentifier(childTable)} ON " +
            $"({_quoteIdentifier(childTable)}.{_quoteIdentifier(GetColumnName(pk))} = " +
            $"{_quoteIdentifier(_tableName)}.{_quoteIdentifier(GetColumnName(fk))})");
        return this;
    }

    [Obsolete("此重载无法表达 JOIN 两端。请使用 ThenInclude<TGrandChild, TParent>(grandChildKey, parentKey)。")]
    public QueryBuilder<T> ThenInclude<TGrandChild>(Expression<Func<TGrandChild, object?>> fk)
        where TGrandChild : class, new()
        => throw new NotSupportedException(
            "ThenInclude requires both join keys. Use ThenInclude<TGrandChild, TParent>(grandChildKey, parentKey).");

    public QueryBuilder<T> ThenInclude<TGrandChild, TParent>(
        Expression<Func<TGrandChild, object?>> grandChildKey,
        Expression<Func<TParent, object?>> parentKey)
        where TGrandChild : class, new()
        where TParent : class, new()
    {
        string grandChildTable = GetRegisteredTableName(typeof(TGrandChild));
        string parentTable = GetRegisteredTableName(typeof(TParent));
        AddClause(QueryClauseKind.Join,
            $"INNER JOIN {_quoteIdentifier(grandChildTable)} ON " +
            $"({_quoteIdentifier(grandChildTable)}.{_quoteIdentifier(GetColumnName(grandChildKey))} = " +
            $"{_quoteIdentifier(parentTable)}.{_quoteIdentifier(GetColumnName(parentKey))})");
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

    public QueryBuilder<T> With(string cteName, FormattableString subquery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cteName);
        var (sql, parameters) = BindFormattableString(subquery);
        _cteName = cteName;
        AddClause(QueryClauseKind.CommonTableExpression,
            $"{_quoteIdentifier(cteName)} AS ({sql})", parameters);
        return this;
    }

    public QueryBuilder<T> AsSplitQuery()
    {
        _splitQuery = true;
        return this;
    }

    public QueryBuilder<T> ForUpdate(bool skipLocked = false)
    {
        AddClause(QueryClauseKind.Lock, $"FOR UPDATE{(skipLocked ? " SKIP LOCKED" : "")}");
        return this;
    }

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

    public QueryBuilder<T> ForRead() { _useReadRoute = true; return this; }
    public QueryBuilder<T> ForWrite() { _useReadRoute = false; return this; }

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

    public DryRunResult AsDryRun() => new(BuildSql(), GetQueryParameters());

    internal ValueTask<ConnectionLease> AcquireConnectionLeaseAsync(bool writeOperation,
        CancellationToken cancellationToken)
    {
        if (GetActiveTransaction() is not null || writeOperation
            || !_useReadRoute || _readConnFactory is null)
            return ValueTask.FromResult(ConnectionLease.Borrow(_conn));

        return ConnectionLease.OpenOwnedAsync(_readConnFactory, cancellationToken);
    }

    internal DbTransaction? GetActiveTransaction()
        => _transaction?.Connection is not null
            ? _transaction
            : _operationState.GetActiveTransaction();

    internal void AddDefaultFilter(string condition)
    {
        AddClause(QueryClauseKind.Where,
            $"{(HasClause(QueryClauseKind.Where) ? "AND" : "WHERE")} {condition}");
    }

    internal QueryBuilder<T> CloneForExecution()
    {
        var clone = new QueryBuilder<T>(_conn, _dialect, _factory, _interceptors, _paramFactory,
            _quoteIdentifier, _tableName, _columnNames, _commandTimeout,
            _operationState, _readConnFactory, _queryCache)
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
        DbParameter parameter = CreateParameter(value);
        AddClause(QueryClauseKind.Where,
            $"{(HasClause(QueryClauseKind.Where) ? "AND" : "WHERE")} " +
            $"{GetQualifiedColumnName(member)} {operation} {parameter.ParameterName}", [parameter]);
    }

    internal void AddOrderBy<TKey>(Expression<Func<T, TKey>> member, bool descending)
        => AddClause(QueryClauseKind.OrderBy,
            $"{(HasClause(QueryClauseKind.OrderBy) ? ", " : "ORDER BY ")}" +
            $"{GetQualifiedColumnName(member)}{(descending ? " DESC" : "")}");

    internal IReadOnlyList<DbParameter> GetQueryParameters()
        => GetParametersForKinds(_splitQuery
            ? [QueryClauseKind.CommonTableExpression, QueryClauseKind.Where,
                QueryClauseKind.GroupBy, QueryClauseKind.Having, QueryClauseKind.OrderBy,
                QueryClauseKind.Raw, QueryClauseKind.Lock]
            : [QueryClauseKind.CommonTableExpression, QueryClauseKind.Join, QueryClauseKind.Where,
                QueryClauseKind.GroupBy, QueryClauseKind.Having, QueryClauseKind.OrderBy,
                QueryClauseKind.Raw, QueryClauseKind.Lock]);

    internal IReadOnlyList<DbParameter> GetCountParameters()
        => GetParametersForKinds(_splitQuery
            ? [QueryClauseKind.CommonTableExpression, QueryClauseKind.Where,
                QueryClauseKind.GroupBy, QueryClauseKind.Having]
            : [QueryClauseKind.CommonTableExpression, QueryClauseKind.Join, QueryClauseKind.Where,
                QueryClauseKind.GroupBy, QueryClauseKind.Having]);

    internal IReadOnlyList<DbParameter> GetUpdateParameters()
        => GetParametersForKinds([QueryClauseKind.Set, QueryClauseKind.Where, QueryClauseKind.Raw]);

    internal string BuildCountSql()
    {
        var sb = new ValueStringBuilder(384);
        try
        {
            AppendComments(ref sb);
            AppendCtes(ref sb);
            sb.Append("SELECT COUNT(*) FROM (SELECT 1 FROM ");
            sb.Append(_quoteIdentifier(_cteName ?? _tableName));
            sb.Append(' ');
            if (!_splitQuery) AppendClauses(ref sb, QueryClauseKind.Join);
            AppendClauses(ref sb, QueryClauseKind.Where);
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
        var sb = new ValueStringBuilder(256);
        try
        {
            AppendComments(ref sb);
            sb.Append("UPDATE ");
            sb.Append(_quoteIdentifier(_tableName));
            sb.Append(' ');
            AppendClauses(ref sb, QueryClauseKind.Set);
            AppendClauses(ref sb, QueryClauseKind.Where);
            AppendClauses(ref sb, QueryClauseKind.Raw);
            return sb.ToString().TrimEnd();
        }
        finally { sb.Dispose(); }
    }

    /// <summary>构建 SQL 预览（含参数）。等同于 AsDryRun().Sql，但不创建 DryRunResult。</summary>
    public string ToSql() => BuildSql();

    /// <summary>按 QueryClauseKind 构建完整 SQL，调用顺序不再决定 SQL 语法顺序。
    /// <para>SplitQuery 当前只构建根查询并移除 JOIN，不执行导航对象装配。</para></summary>
    internal string BuildSql()
    {
        var sb = new ValueStringBuilder(512);
        try
        {
            AppendComments(ref sb);
            AppendCtes(ref sb);
            sb.Append("SELECT ");
            string sourceName = _cteName ?? _tableName;
            if (_selectColumns is not null)
            {
                sb.Append(_selectColumns);
            }
            else
            {
                for (int index = 0; index < _columnNames.Count; index++)
                {
                    if (index > 0) sb.Append(", ");
                    sb.Append(_quoteIdentifier(sourceName));
                    sb.Append('.');
                    sb.Append(_quoteIdentifier(_columnNames[index]));
                }
            }
            foreach (QueryClause window in _clauses.Where(clause => clause.Kind == QueryClauseKind.Window))
            {
                sb.Append(", ");
                sb.Append(window.Sql);
            }
            sb.Append(" FROM ");
            sb.Append(_quoteIdentifier(sourceName));
            sb.Append(' ');
            if (!_splitQuery) AppendClauses(ref sb, QueryClauseKind.Join);
            AppendClauses(ref sb, QueryClauseKind.Where);
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

    private System.Collections.ObjectModel.ReadOnlyCollection<DbParameter> GetParametersForKinds(QueryClauseKind[] kinds)
    {
        var parameters = new List<DbParameter>();
        foreach (QueryClause clause in _clauses)
        {
            if (!kinds.Contains(clause.Kind)) continue;
            parameters.AddRange(clause.Parameters);
        }
        return parameters.AsReadOnly();
    }

    private void AppendComments(ref ValueStringBuilder builder)
    {
        foreach (QueryClause clause in _clauses.Where(clause => clause.Kind == QueryClauseKind.Comment))
        {
            builder.Append(clause.Sql);
            builder.Append(' ');
        }
    }

    private void AppendCtes(ref ValueStringBuilder builder)
    {
        QueryClause[] ctes = _clauses.Where(clause => clause.Kind == QueryClauseKind.CommonTableExpression).ToArray();
        if (ctes.Length == 0) return;
        builder.Append("WITH ");
        for (int i = 0; i < ctes.Length; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(ctes[i].Sql);
        }
        builder.Append(' ');
    }

    private void AppendClauses(ref ValueStringBuilder builder, QueryClauseKind kind)
    {
        foreach (QueryClause clause in _clauses.Where(clause => clause.Kind == kind))
        {
            builder.Append(clause.Sql);
            builder.Append(' ');
        }
    }

    private string? BuildLimitClause()
    {
        if (!_take.HasValue && !_skip.HasValue) return null;
        if (!_take.HasValue)
        {
            return _dialect switch
            {
                SqlDialect.MySql => $"LIMIT {_skip!.Value}, 18446744073709551615",
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
