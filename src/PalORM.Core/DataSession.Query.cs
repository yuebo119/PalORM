using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace PalORM;

public sealed partial class DataSession<TProvider>
    where TProvider : IDbProvider
{
    /// <summary>见 DataSession 主文档。</summary>
    public async ValueTask<long> CountAsync<T>(FormattableString? where = null, CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' not registered.");
        string defaultFilter = GetDefaultFilterCondition<T>();
        // PERF-002（2026-09-23）：基底恒定（仅条件可变）——按 (Type, Dialect) 缓存，
        // 原先每次 CountAsync 付一次 QuoteIdentifier + 插值。
        string sql = DataSessionCache.CountBaseSqlCache.GetOrAdd(
            (typeof(T), TProvider.Dialect),
            static (_, tableName) => $"SELECT COUNT(*) FROM {TProvider.QuoteIdentifier(tableName)}",
            tn);
        if (where is not null)
            // 用户条件必须整体括号包裹：含 OR 时 AND 优先级会使默认过滤对 OR 分支失效
            sql += " WHERE " + (defaultFilter.Length == 0 ? "" : defaultFilter + " AND ")
                + "(" + FormatSqlWithParameters(where) + ")";
        else if (defaultFilter.Length > 0)
            sql += " WHERE " + defaultFilter;
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        if (where is not null) BindFormattableParameters(cmd, where);
        BindDefaultFilterParameters<T>(cmd);
        // v5.4 弹性接入：COUNT 为无事务只读路径时经会话弹性策略
        object? scalar = await ExecuteReadPipelineAsync(
            async token => await cmd.ExecuteScalarAsync(token).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        // ITM-637 同型面：COUNT null 静默 0 掩蔽驱动异常——与 ToPageAsync 同口径显式报错
        if (scalar is null)
            throw new InvalidOperationException(
                "COUNT query returned null scalar — the ADO.NET driver behaved unexpectedly.");
        return scalar is long l ? l : Convert.ToInt64(scalar);
    }

    /// <summary>聚合标量内核（Sum/Max/Min/Avg 共享，v5.4 精炼 L3）。
    /// ITM-613 门禁次序保持：先 EnterOperation 再拼 SQL——门禁外读过滤状态 + 门禁内
    /// 二次求值的窗口内，并发 IgnoreFilters()/WithTenant() 可致 SQL 片段与参数集错配。</summary>
    private async ValueTask<object?> ExecuteAggregateScalarAsync<T>(
        string function, FormattableString expression, CancellationToken ct)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn))
            throw new InvalidOperationException($"'{typeof(T).Name}' not registered.");
        return await ExecuteScalarAsync<T>(
            $"SELECT {function}({FormatSqlWithParameters(expression)}) FROM {TProvider.QuoteIdentifier(tn)}{GetDefaultFilterWhereClause<T>()}",
            expression, ct).ConfigureAwait(false);
    }

    /// <summary>SUM 聚合。空表/全过滤时 SUM 返回 NULL——与 Max/Min 一致返回 0（ITM-408）。</summary>
    public async ValueTask<decimal> SumAsync<T>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        object? r = await ExecuteAggregateScalarAsync<T>("SUM", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? 0m : Convert.ToDecimal(r, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>MAX 聚合。TValue 限 IConvertible 基元类型（数值/字符串/DateTime）；
    /// Guid/DateOnly/枚举等经 Convert.ChangeType 会抛 InvalidCastException。
    /// TValue 可为可空值类型（如 int?）——底层类型解包见 <see cref="ConvertScalar{T}"/>（ITM-711）。</summary>
    public async ValueTask<TValue?> MaxAsync<T, TValue>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        object? r = await ExecuteAggregateScalarAsync<T>("MAX", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? default : ConvertScalar<TValue>(r);
    }

    /// <summary>MIN 聚合。TValue 限制同 MaxAsync。</summary>
    public async ValueTask<TValue?> MinAsync<T, TValue>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        object? r = await ExecuteAggregateScalarAsync<T>("MIN", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? default : ConvertScalar<TValue>(r);
    }

    /// <summary>AVG 聚合。空表/全过滤时 AVG 返回 NULL——与 Max/Min 一致返回 0（ITM-408）。</summary>
    public async ValueTask<double> AvgAsync<T>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        object? r = await ExecuteAggregateScalarAsync<T>("AVG", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? 0d : Convert.ToDouble(r, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>聚合执行内核（不带操作门禁）——门禁由四个聚合入口持有（ITM-613：
    /// 先入门禁再拼 SQL）。Enter 不可重入，此处再 Enter 会抛
    /// "already has an active database operation"（SoftDelete 聚合测试实证）。
    /// v5.4 弹性接入：无事务只读标量经会话弹性策略（重试/熔断）。</summary>
    private async ValueTask<object?> ExecuteScalarAsync<T>(string sql, FormattableString original, CancellationToken ct)
        where T : class, new()
    {
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindFormattableParameters(cmd, original);
        BindDefaultFilterParameters<T>(cmd);
        return await ExecuteReadPipelineAsync(
            async token => await cmd.ExecuteScalarAsync(token).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
    }

    /// <summary>标量值到 CLR 类型的归一转换——解包可空泛型后统一走 Convert.ChangeType。
    /// ITM-533：InvariantCulture 避免线程区域性影响数值/日期解析。
    /// ITM-711：T 为可空值类型（如 int?）时直接转换会抛 InvalidCastException（已实测
    /// Convert.ChangeType(5L, typeof(int?))）；须先取底层类型。ScalarAsync/MaxAsync/MinAsync 共用本助手。</summary>
    private static T ConvertScalar<T>(object value)
        => (T)Convert.ChangeType(
            value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);

    // 说明：保存点入口在 DataSession.Transactions.cs（SavepointAsync/RollbackToAsync）——
    // 此处原空"保存点"节头为拆分残留，v5.4 移除。

    // ─── 存储过程 ──────────────────────────────────────

    /// <summary>存储过程入口。</summary>
    public StoredProcBuilder StoredProc(string name) => new(
        _conn, name, _options.CommandTimeout, TProvider.CreateParameter,
        _operationState, _options.ValidateQueryColumnOrder);

    /// <summary>流式查询——IAsyncEnumerable 恒定内存。
    /// <para><b>ITM-572/677 警告</b>：SQL 逐字执行，<b>默认过滤（[SoftDelete]/[TenantAware]）不适用</b>——
    /// 租户会话经此入口可读到全部租户与已软删数据（与 QueryMultipleAsync 同契约）。
    /// 多租户场景必须在 SQL 中自行携带 tenant_id/deleted_at 条件，或改用受过滤保护的常规查询入口。</para>
    /// <para><b>枚举器必须释放（ITM-508）</b>：操作租约在枚举期间持有，到枚举器 DisposeAsync 才归还。
    /// 用 <c>await foreach</c> 消费（自动释放）；手写 <c>GetAsyncEnumerator</c> 时必须 <c>await using</c>
    /// 或在 break/异常路径显式 DisposeAsync——否则租约永不归还，会话后续操作被门禁拒绝，
    /// 且 DataSession.DisposeAsync 会挂起至 DisposeWaitTimeout 后抛诊断异常。</para></summary>
    public async IAsyncEnumerable<T> QueryAsyncEnumerable<T>(FormattableString sql, [EnumeratorCancellation] CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' not registered.");

        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = FormatSqlWithParameters(sql);
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindFormattableParameters(cmd, sql);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var tf = (Func<DbDataReader, T>)factory;
        bool firstRow = true;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (firstRow)
            {
                ValidateColumnOrder<T>(reader);
                firstRow = false;
            }
            yield return tf(reader);
        }
    }

    // ─── 查询缓存 ────────────────────────────────────────

    private static void BindGeneratedKeyParameter<T>(DbCommand cmd, object key)
        where T : class, new()
    {
        if (PalORM_Runtime.BindDelete.TryGetValue(
                typeof(T),
                out Action<DbCommand, object>? binder))
        {
            binder(cmd, key);
            return;
        }

        throw new InvalidOperationException(
            $"Type '{typeof(T).Name}' has no generated key binder.");
    }

    private static string GetPkColumn<T>() where T : class, new()
    {
        if (PalORM_Runtime.PkColumns.TryGetValue(typeof(T), out string? pk))
            return pk;
        throw new InvalidOperationException($"No primary key for '{typeof(T).Name}'.");
    }
    /// <summary>直查实体列表——绕过 QueryBuilder 的原生 SQL 入口。
    /// <para><b>ITM-572/677/700 警告</b>：SQL 逐字执行，<b>默认过滤（[SoftDelete]/[TenantAware]）不适用</b>——
    /// 租户会话经此入口可读到全部租户与已软删数据（与 QueryAsyncEnumerable/QueryMultipleAsync 同契约）。
    /// 多租户场景必须在 SQL 中自行携带 tenant_id/deleted_at 条件，或改用受过滤保护的常规查询入口。</para>
    /// <para><b>列序契约（重要）</b>: 结果按序号（ordinal）映射到实体，第 n 列写入实体声明序第 n 个映射属性。
    /// SELECT 列序必须与实体列声明序一致；同类型列错位会静默交换数据。
    /// 避免 <c>SELECT *</c>（依赖物理表列序）——请显式 <c>SELECT col1, col2, ...</c> 按实体声明序列出，
    /// 或使用列序由编译期保证的 <c>From&lt;T&gt;()</c> 查询。见 ADR-A。</para></summary>
    public async ValueTask<List<T>> QueryAsync<T>(FormattableString sql, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered.");

        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = FormatSqlWithParameters(sql);
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindFormattableParameters(cmd, sql);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        // v4.1：对齐 GetAllAsync 的 capacity=16，避免 10K 行场景 14 次扩容
        List<T> list = new(16);
        var typedFactory = (Func<DbDataReader, T>)factory;
        bool firstRow = true;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (firstRow)
            {
                ValidateColumnOrder<T>(reader);
                firstRow = false;
            }
            list.Add(typedFactory(reader));
        }
        return list;
    }

    /// <summary>ADR-A 首行列名校验：结果列名与实体声明序列名不匹配即抛异常，
    /// 把"同型列静默交换数据"变为明确失败。仅首行执行，热路径零开销。</summary>
    private void ValidateColumnOrder<T>(DbDataReader reader) where T : class, new()
        => ColumnOrderValidator.Validate<T>(reader, _options.ValidateQueryColumnOrder);

    /// <summary>直查首行——无结果抛 InvalidOperationException。
    /// <para>review R4：流式读取首行后立即释放 reader，不物化全表（大结果集场景省分配）。</para></summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Bugprone", "S1751",
        Justification = "「循环内 return」是有意为之的流式首行模式——取首行后立即跳出并释放 reader。")]
    public async ValueTask<T> QueryFirstAsync<T>(FormattableString sql, CancellationToken ct = default)
        where T : class, new()
    {
        await foreach (T row in QueryAsyncEnumerable<T>(sql, ct).ConfigureAwait(false))
            return row;
        throw new InvalidOperationException($"QueryFirstAsync: no rows for '{typeof(T).Name}'.");
    }

    /// <summary>直查精确单行——0 或 >1 行均抛异常。
    /// <para>评审 2026-09-02：流式精确单行——读到第 2 行立即失败并释放 reader，
    /// 不再经 <see cref="QueryAsync{T}"/> 物化全表（大表上的隐式全读）。因流式提前终止，
    /// 多于 1 行时报告 "at least 2" 而非精确总数。</para>
    /// <para><b>ITM-700 警告</b>：原始 SQL 入口，默认过滤（[SoftDelete]/[TenantAware]）不适用（同 QueryAsync 契约）。</para></summary>
    public async ValueTask<T> QuerySingleAsync<T>(FormattableString sql, CancellationToken ct = default)
        where T : class, new()
    {
        int count = 0;
        T single = default!;
        await foreach (T row in QueryAsyncEnumerable<T>(sql, ct).ConfigureAwait(false))
        {
            count++;
            if (count > 1)
                throw new InvalidOperationException(
                    "QuerySingleAsync: expected 1 row, got at least 2.");
            single = row;
        }
        return count == 1 ? single
            : throw new InvalidOperationException($"QuerySingleAsync: expected 1 row, got {count}.");
    }

    /// <summary>直查标量。数据库返回类型与 <typeparamref name="T"/> 不同时按 Convert.ChangeType 转换
    /// （PG COUNT 返回 long、MySQL SUM 返回 decimal 等常见情形）；无法转换时抛 InvalidCastException 而非静默返回 default。
    /// <para><b>ITM-700 警告</b>：原始 SQL 入口，默认过滤（[SoftDelete]/[TenantAware]）不适用（同 QueryAsync 契约）。</para>
    /// <para><b>类型支持范围</b>（与 MaxAsync/MinAsync 一致）：<typeparamref name="T"/> 限 IConvertible 基元类型
    /// （数值/bool/string/DateTime）及其 Nullable；Guid/枚举/DateOnly 等非 IConvertible 目标在类型不完全匹配时
    /// 抛 InvalidCastException——此类值请以 string 取回后自行 Parse。</para></summary>
    public async ValueTask<T?> ScalarAsync<T>(FormattableString sql, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = FormatSqlWithParameters(sql);
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindFormattableParameters(cmd, sql);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull) return default;
        if (result is T t) return t;
        return ConvertScalar<T>(result);
    }

    /// <summary>执行任意 DDL/DML。
    /// <para><b>ITM-700 警告</b>：原始 SQL 入口，默认过滤（[SoftDelete]/[TenantAware]）不适用（同 QueryAsync 契约）。</para>
    /// <para><b>R3（v5.7）</b>：拦截器三段式接入（此前为覆盖面缺口）——OnBefore/OnAfter/OnError
    /// 与 ToListAsync 同语义；参数表仅在拦截器非空时物化，默认会话零开销。</para></summary>
    public async ValueTask<int> ExecuteAsync(FormattableString sql, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = FormatSqlWithParameters(sql);
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindFormattableParameters(cmd, sql);
        // R3（v5.7）：拦截器非空才物化参数表与计时——与 SELECT 管线"空列表跳过"同口径
        QueryContext context = default;
        System.Diagnostics.Stopwatch? stopwatch = null;
        if (_interceptors.Count > 0)
        {
            var parameters = new List<System.Data.Common.DbParameter>(cmd.Parameters.Count);
            foreach (System.Data.Common.DbParameter parameter in cmd.Parameters) parameters.Add(parameter);
            context = new QueryContext(cmd.CommandText, parameters);
            stopwatch = System.Diagnostics.Stopwatch.StartNew();
            QueryBuilderExtensions.NotifyInterceptorsOnBefore(_interceptors, context);
        }
        int affected;
        try
        {
            affected = await ExecuteWriteRowsAsync(cmd, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (stopwatch is not null)
                QueryBuilderExtensions.NotifyInterceptorsOnError(_interceptors, context, exception);
            throw;
        }
        if (stopwatch is not null)
        {
            stopwatch.Stop();
            QueryBuilderExtensions.NotifyInterceptorsOnAfter(_interceptors, context, stopwatch, affected);
        }
        return affected;
    }
}
