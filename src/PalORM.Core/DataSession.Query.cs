using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace PalORM;

public sealed partial class DataSession<TProvider>
    where TProvider : IDbProvider
{
    /// <summary>见 DataSession 主文档。（R4 预热包装同 GetAsync。）</summary>
    public ValueTask<long> CountAsync<T>(FormattableString? where = null, CancellationToken ct = default) where T : class, new()
    {
        if (_operationState.CompletedOps >= SessionOperationState.PrewarmThreshold)
            _operationState.PrewarmCurrentOperationOwner();
        return CountEntryAsync<T>(where, ct);
    }

    private async ValueTask<long> CountEntryAsync<T>(FormattableString? where, CancellationToken ct) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterReadOnly();
        // 刀1（2026-10-13 二轮）：表名/features 走合并视图静态缓存（原 TableNames + EntityFeatures
        // + FilterForms 三次查找）；刀2：事务单次取用贯穿复用分支与 lease 路径。
        EntityRuntimeInfo info = GetEntityInfo<T>();
        DbTransaction? activeTransaction = GetActiveTransaction();
        // G4（2026-10-13 五轮微项包）：基底与组合句走闭式泛型静态槽（F6b 同型，原
        // CountBaseSqlCache/CountComposedSqlCache 元组键字典）；wherePrefix 保持惰性
        //（既有修正语义：无 where 的 Empty 形态直出基底，不付 " WHERE (" 拼接）。
        int filterSlot = FilterSlotBits(info.Features);
        string baseSql = SqlSlots<T>.CountBaseOrBuilt(TProvider.QuoteIdentifier(info.TableName));
        string sql;
        if (where is null)
        {
            sql = filterSlot == 0
                ? baseSql
                : (SqlSlots<T>.CountComposed[filterSlot] ??= BuildCountComposed<T>(info, baseSql)).FilterOnlySql;
        }
        else
        {
            // 用户条件必须整体括号包裹：含 OR 时 AND 优先级会使默认过滤对 OR 分支失效
            string prefix = filterSlot == 0
                ? baseSql + " WHERE ("
                : (SqlSlots<T>.CountComposed[filterSlot] ??= BuildCountComposed<T>(info, baseSql)).WherePrefix;
            sql = prefix + FormatSqlWithParameters(where) + ")";
        }
        // P1-1（2026-10-12 全量重读）：直查族命令复用（A9 同型，主连接 + 非并行读形态）——
        // 满批 COUNT 的 SQL 文本经组合句缓存恒稳定，晋升后每次调用省命令创建/释放与参数
        // 集合重建。租户槽值随 WithTenant 刷新；就地写失败（形状/类型漂移）回退下方新建路径。
        (bool countHandled, long reusedCountValue) = await TryCountViaReusableCommandAsync(sql, where, info, activeTransaction, ct)
            .ConfigureAwait(false);
        if (countHandled)
            return reusedCountValue;

        await using ReadCommandLease lease = await CreateReadRoutedCommandAsync(readFromReplica: false, activeTransaction, ct).ConfigureAwait(false);
        DbCommand cmd = lease.Command;
        cmd.CommandText = sql;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        if (where is not null) BindFormattableParameters(cmd, where);
        BindDefaultFilterParameters(cmd, info.Features);
        // v5.4 弹性接入：COUNT 为无事务只读路径时经会话弹性策略。
        // A2（2026-10-01 全 API 逐项轮）：直调重载消调用点 async lambda 的闭包与状态机
        // （与 P2-1 ExecuteWriteRowsAsync 同型口径）。
        object? scalar = await ExecuteReadScalarAsync(cmd, ct).ConfigureAwait(false);
        lease.MarkHealthy();
        // ITM-637 同型面：COUNT null 静默 0 掩蔽驱动异常——与 ToPageAsync 同口径显式报错
        if (scalar is null)
            throw new InvalidOperationException(
                "COUNT query returned null scalar — the ADO.NET driver behaved unexpectedly.");
        return scalar is long l ? l : Convert.ToInt64(scalar);
    }

    /// <summary>P1-1：CountAsync 的复用分支（从 CountEntryAsync 抽出，降 S3776）——
    /// 槽命中且就地绑定成功时执行并返回 (true, 计数)；未命中/绑定失败返回 default，
    /// 调用方回退新建命令路径。租户槽（@__tenant0）随命令持久存在，值每次就地刷新。
    /// 刀1：features 经合并视图传入；刀2：事务传入（守卫式重设）。</summary>
    private async ValueTask<(bool Handled, long Count)> TryCountViaReusableCommandAsync(
        string sql, FormattableString? where, EntityRuntimeInfo info,
        DbTransaction? activeTransaction, CancellationToken ct)
    {
        int tenantSlots = HasTenantFilter(info.Features) ? 1 : 0;
        DbCommand? reused = TryAcquireReusableDirectCommand(sql);
        if (reused is null || !TryBindFormattableValuesInPlace(reused, where, tenantSlots))
            return default;
        ApplyReusableCommandState(reused, activeTransaction, sql);
        if (tenantSlots > 0)
            reused.Parameters[^1].Value = _tenantId;
        object? scalar = await ExecuteReadScalarAsync(reused, activeTransaction, ct).ConfigureAwait(false);
        // ITM-637 同型面：COUNT null 静默 0 掩蔽驱动异常——与新建路径同口径显式报错
        if (scalar is null)
            throw new InvalidOperationException(
                "COUNT query returned null scalar — the ADO.NET driver behaved unexpectedly.");
        return (true, scalar is long l ? l : Convert.ToInt64(scalar));
    }

    /// <summary>G4（五轮微项包）：Count 组合句的纯构建器（仅槽未命中时调用）——条件文本经
    /// <see cref="GetDefaultFilterForms{T}"/> 单一真源派生（(filter-only 全句, where 前缀片段)，
    /// 原值形态与 T4/PG-5 逐位一致），不另立第二真源。</summary>
    private (string FilterOnlySql, string WherePrefix) BuildCountComposed<T>(
        EntityRuntimeInfo info, string baseSql)
        where T : class, new()
    {
        string filter = GetDefaultFilterForms<T>(info.Features).Condition;
        return (baseSql + " WHERE " + filter, baseSql + " WHERE " + filter + " AND (");
    }

    /// <summary>聚合标量内核（Sum/Max/Min/Avg 共享，v5.4 精炼 L3）。
    /// ITM-613 门禁次序保持：先 EnterOperation 再拼 SQL——门禁外读过滤状态 + 门禁内
    /// 二次求值的窗口内，并发 IgnoreFilters()/WithTenant() 可致 SQL 片段与参数集错配。</summary>
    // G2 证伪回退（2026-10-14，勿重开非 async 租约入口）：理由见 DataSession.Crud 的
    // GetByKeyEntryAsync 上方登记——非 async 入口的 Enter AsyncLocal 写逃逸调用方 EC，
    // 每操作新会话 = 新 AsyncLocal 实例键 → EC map 无界增长 → O(N²)。
    private async ValueTask<object?> ExecuteAggregateScalarAsync<T>(
        string function, FormattableString expression, CancellationToken ct)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterReadOnly();
        // 刀1：表名/features 走合并视图；刀2：事务单次取用。
        EntityRuntimeInfo info = GetEntityInfo<T>();
        DbTransaction? activeTransaction = GetActiveTransaction();
        // G4（五轮微项包）：") FROM {table}{whereClause}" 后缀走闭式泛型静态槽（F6b 同型，原
        // AggregateSuffixCache/AggregateFilteredSuffixCache 元组键字典）——只剩 "SELECT {fn}("
        // 前缀与表达式文本每次拼（1 次 concat）。R3（2026-10-12）原纪律保持：过滤判别单次求值。
        int aggregateSlot = FilterSlotBits(info.Features);
        string suffix = aggregateSlot == 0
            ? SqlSlots<T>.AggregateSuffixOrBuilt(TProvider.QuoteIdentifier(info.TableName))
            : SqlSlots<T>.AggregateFilteredSuffixOrBuilt(
                aggregateSlot, TProvider.QuoteIdentifier(info.TableName),
                GetDefaultFilterForms<T>(info.Features).WhereClause);
        return await ExecuteScalarAsync(
            $"SELECT {function}(" + FormatSqlWithParameters(expression) + suffix,
            expression, info, activeTransaction, ct).ConfigureAwait(false);
    }

    /// <summary>SUM 聚合。空表/全过滤时 SUM 返回 NULL——与 Max/Min 一致返回 0（ITM-408）。</summary>
    public ValueTask<decimal> SumAsync<T>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (_operationState.CompletedOps >= SessionOperationState.PrewarmThreshold) _operationState.PrewarmCurrentOperationOwner();
        return SumEntryAsync<T>(expression, ct);
    }

    private async ValueTask<decimal> SumEntryAsync<T>(FormattableString expression, CancellationToken ct) where T : class, new()
    {
        object? r = await ExecuteAggregateScalarAsync<T>("SUM", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? 0m : Convert.ToDecimal(r, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>MAX 聚合。TValue 限 IConvertible 基元类型（数值/字符串/DateTime）；
    /// Guid/DateOnly/枚举等经 Convert.ChangeType 会抛 InvalidCastException。
    /// TValue 可为可空值类型（如 int?）——底层类型解包见 <see cref="ConvertScalar{T}"/>（ITM-711）。</summary>
    public ValueTask<TValue?> MaxAsync<T, TValue>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (_operationState.CompletedOps >= SessionOperationState.PrewarmThreshold) _operationState.PrewarmCurrentOperationOwner();
        return MaxEntryAsync<T, TValue>(expression, ct);
    }

    private async ValueTask<TValue?> MaxEntryAsync<T, TValue>(FormattableString expression, CancellationToken ct) where T : class, new()
    {
        object? r = await ExecuteAggregateScalarAsync<T>("MAX", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? default : ConvertScalar<TValue>(r);
    }

    /// <summary>MIN 聚合。TValue 限制同 MaxAsync。</summary>
    public ValueTask<TValue?> MinAsync<T, TValue>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (_operationState.CompletedOps >= SessionOperationState.PrewarmThreshold) _operationState.PrewarmCurrentOperationOwner();
        return MinEntryAsync<T, TValue>(expression, ct);
    }

    private async ValueTask<TValue?> MinEntryAsync<T, TValue>(FormattableString expression, CancellationToken ct) where T : class, new()
    {
        object? r = await ExecuteAggregateScalarAsync<T>("MIN", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? default : ConvertScalar<TValue>(r);
    }

    /// <summary>AVG 聚合。空表/全过滤时 AVG 返回 NULL——与 Max/Min 一致返回 0（ITM-408）。</summary>
    public ValueTask<double> AvgAsync<T>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (_operationState.CompletedOps >= SessionOperationState.PrewarmThreshold) _operationState.PrewarmCurrentOperationOwner();
        return AvgEntryAsync<T>(expression, ct);
    }

    private async ValueTask<double> AvgEntryAsync<T>(FormattableString expression, CancellationToken ct) where T : class, new()
    {
        object? r = await ExecuteAggregateScalarAsync<T>("AVG", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? 0d : Convert.ToDouble(r, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>聚合执行内核（不带操作门禁）——门禁由四个聚合入口持有（ITM-613：
    /// 先入门禁再拼 SQL）。Enter 不可重入，此处再 Enter 会抛
    /// "already has an active database operation"（SoftDelete 聚合测试实证）。
    /// v5.4 弹性接入：无事务只读标量经会话弹性策略（重试/熔断）。
    /// 刀1：features 经合并视图；刀2：事务传入。</summary>
    private async ValueTask<object?> ExecuteScalarAsync(string sql, FormattableString original,
        EntityRuntimeInfo info, DbTransaction? activeTransaction, CancellationToken ct)
    {
        // P1-1：直查族命令复用（A9 同型）——聚合全句由缓存片段拼接恒稳定（值相等）；
        // 租户槽随命令持久，值就地刷新。就地写失败回退下方新建路径。
        int aggregateTenantSlots = HasTenantFilter(info.Features) ? 1 : 0;
        DbCommand? reusedAggregate = TryAcquireReusableDirectCommand(sql);
        if (reusedAggregate is not null
            && TryBindFormattableValuesInPlace(reusedAggregate, original, aggregateTenantSlots))
        {
            ApplyReusableCommandState(reusedAggregate, activeTransaction, sql);
            if (aggregateTenantSlots > 0)
                reusedAggregate.Parameters[^1].Value = _tenantId;
            return await ExecuteReadScalarAsync(reusedAggregate, activeTransaction, ct).ConfigureAwait(false);
        }

        await using ReadCommandLease lease = await CreateReadRoutedCommandAsync(readFromReplica: false, activeTransaction, ct).ConfigureAwait(false);
        DbCommand cmd = lease.Command;
        cmd.CommandText = sql;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindFormattableParameters(cmd, original);
        BindDefaultFilterParameters(cmd, info.Features);
        // A2（2026-10-01 全 API 逐项轮）：直调重载覆盖 Sum/Max/Min/Avg 四个聚合入口
        // （CountAsync 同点改）。
        object? result = await ExecuteReadScalarAsync(cmd, ct).ConfigureAwait(false);
        lease.MarkHealthy();
        return result;
    }

    /// <summary>A2（2026-10-01 全 API 逐项轮）：只读标量直调重载——省调用点 async lambda
    /// 的闭包与状态机（与 P2-1 <see cref="ExecuteWriteRowsAsync"/> 同型；该注释实测
    /// 包装层约 208B/次）。语义与委托版逐位一致：直通（策略直通或事务内）直接执行并返回；
    /// 非直通分支才包执行器（重试/熔断与超时包装要求，此时包装成本本就少见且必要）。
    /// <para>Core 内核用 <c>new ValueTask&lt;T&gt;(task)</c> 直包而非 async 方法——连状态机
    /// 一并省去；ConfigureAwait 由消费端 <c>await ... .ConfigureAwait(false)</c> 覆盖
    /// （ValueTask 包装 Task 的 awaiter 即 Task 的 awaiter）。</para></summary>
    private ValueTask<object?> ExecuteReadScalarAsync(DbCommand command, CancellationToken ct)
        => ExecuteReadScalarAsync(command, GetActiveTransaction(), ct);

    /// <summary>刀2：事务由调用方单次取用后传入的重载。</summary>
    private ValueTask<object?> ExecuteReadScalarAsync(DbCommand command, DbTransaction? activeTransaction, CancellationToken ct)
    {
        ResilienceExecutor executor = Resilience;
        if (executor.IsPassThrough || activeTransaction is not null)
            return ExecuteScalarCoreAsync(command, ct);
        // R4-λ:static lambda + 命令状态,闭包归零(原捕获 command 的 display class ~56B/读)
        return executor.ExecuteAsync(
            static (cmd, token) => ExecuteScalarCoreAsync(cmd, token).AsTask(),
            command,
            ct);
    }

    private static ValueTask<object?> ExecuteScalarCoreAsync(DbCommand command, CancellationToken ct)
        => new(command.ExecuteScalarAsync(ct));

    /// <summary>标量值到 CLR 类型的归一转换——解包可空泛型后统一走 Convert.ChangeType。
    /// ITM-533：InvariantCulture 避免线程区域性影响数值/日期解析。
    /// ITM-711：T 为可空值类型（如 int?）时直接转换会抛 InvalidCastException（已实测
    /// Convert.ChangeType(5L, typeof(int?))）；须先取底层类型。ScalarAsync/MaxAsync/MinAsync 共用本助手。
    /// 底层类型取用 .NET 11 新增的 Type.GetNullableUnderlyingType() 实例方法（与 Nullable.GetUnderlyingType 等价）。</summary>
    private static T ConvertScalar<T>(object value)
        => (T)Convert.ChangeType(
            value, typeof(T).GetNullableUnderlyingType() ?? typeof(T),
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
    public async IAsyncEnumerable<T> QueryAsyncEnumerable<T>(
        FormattableString sql,
        bool readFromReplica = false,
        [EnumeratorCancellation] CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterReadOnly();
        // 刀1：工厂走每类型静态缓存（[Projection] 兼容——同 QueryEntryAsync）；刀2：事务单次取用。
        Func<DbDataReader, T> streamRowFactory = GetRowFactory<T>();
        DbTransaction? streamTransaction = GetActiveTransaction();

        // P1-1（2026-10-12 全量重读）：直查族命令复用（A9 同型，主连接 + 非读路由形态）。
        // 流式枚举下 reader 在枚举期间持有，命令归槽所有不释放；单活动操作门禁保证槽命令
        // 不被并发使用。就地写失败回退下方新建路径。
        string streamCommandText = FormatSqlWithParameters(sql);
        DbCommand? reusedStream = readFromReplica ? null : TryAcquireReusableDirectCommand(streamCommandText);
        bool streamInPlace = reusedStream is not null && TryBindFormattableValuesInPlace(reusedStream, sql);
        // F7：租约改 struct——default 形态（复用命令分支）的 MarkHealthy/MarkFailed/DisposeAsync
        // 均为空安全空操作，调用点保持无分支形态（Nullable<T>.Value 是只读属性，经它调用
        // 变异方法会作用在临时拷贝上，故不用 Nullable 形态）。
        ReadCommandLease lease = default;
        try
        {
            DbCommand cmd;
            if (streamInPlace)
            {
                cmd = reusedStream!;
                ApplyReusableCommandState(cmd, streamTransaction, streamCommandText);
            }
            else
            {
                lease = await CreateReadRoutedCommandAsync(readFromReplica, streamTransaction, ct).ConfigureAwait(false);
                cmd = lease.Command;
                cmd.CommandText = streamCommandText;
                cmd.CommandTimeout = _options.CommandTimeoutSeconds;
                BindFormattableParameters(cmd, sql);
            }

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            // A1：reader 成功打开即证明连接本轮可用——先标记健康（早终止消费如 First/Single 族
            // 在首行后 break 属正常路径，池连接照常归还）；读取阶段异常时撤销标记并丢弃。
            // try/catch 只包 ReadAsync，yield 保持在 try 之外（C# 禁止 yield 出现在带 catch 的 try 内）。
            lease.MarkHealthy();
            var tf = streamRowFactory;
            bool firstRow = true;
            while (true)
            {
                bool hasRow;
                try
                {
                    hasRow = await reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    lease.MarkFailed();
                    throw;
                }
                if (!hasRow) break;
                if (firstRow)
                {
                    ValidateColumnOrder<T>(reader);
                    firstRow = false;
                }
                yield return tf(reader);
            }
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ─── 查询缓存 ────────────────────────────────────────

    private static void BindGeneratedKeyParameter<T>(DbCommand cmd, object key)
        where T : class, new()
    {
        if (GetEntityInfo<T>().BindDelete is { } binder)
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
    public ValueTask<List<T>> QueryAsync<T>(FormattableString sql, bool readFromReplica = false, CancellationToken ct = default)
        where T : class, new()
    {
        if (_operationState.CompletedOps >= SessionOperationState.PrewarmThreshold) _operationState.PrewarmCurrentOperationOwner();
        return QueryEntryAsync<T>(sql, readFromReplica, ct);
    }

    private async ValueTask<List<T>> QueryEntryAsync<T>(FormattableString sql, bool readFromReplica, CancellationToken ct)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterReadOnly();
        // 刀1：工厂走每类型静态缓存（直查族允许 [Projection] 类型——走 RowFactories 视图，
        // 不走 EntityRuntimeInfo 实体视图）；刀2：事务单次取用。
        Func<DbDataReader, T> rowFactory = GetRowFactory<T>();
        DbTransaction? activeTransaction = GetActiveTransaction();

        // P1-1：直查族命令复用（A9 同型，主连接 + 非读路由形态）——reader 由 using 释放，
        // 命令归槽所有不释放（下一次同形状查询的就地写值前提）；就地写失败回退新建路径。
        string queryCommandText = FormatSqlWithParameters(sql);
        DbCommand? reusedQuery = readFromReplica ? null : TryAcquireReusableDirectCommand(queryCommandText);
        if (reusedQuery is not null && TryBindFormattableValuesInPlace(reusedQuery, sql))
        {
            ApplyReusableCommandState(reusedQuery, activeTransaction, queryCommandText);
            await using DbDataReader reusedReader = await reusedQuery.ExecuteReaderAsync(ct).ConfigureAwait(false);
            List<T> reusedList = await ResultListReader.ReadAllAsync(
                reusedReader, rowFactory, 16, ct,
                _options.ValidateQueryColumnOrder ? static r => ColumnOrderValidator.Validate<T>(r, true) : null)
                .ConfigureAwait(false);
            return reusedList;
        }

        await using ReadCommandLease lease = await CreateReadRoutedCommandAsync(readFromReplica, activeTransaction, ct).ConfigureAwait(false);
        DbCommand cmd = lease.Command;
        cmd.CommandText = queryCommandText;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindFormattableParameters(cmd, sql);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        // ADR-A 首行列序校验经 onFirstRow 保持"仅在有首行时执行一次"；关闭校验时传 null
        //（Validate 关闭即空操作，省掉每次调用的委托判断）。static lambda 按泛型实例缓存，零分配。
        List<T> list = await ResultListReader.ReadAllAsync(
            reader, rowFactory, 16, ct,
            _options.ValidateQueryColumnOrder ? static r => ColumnOrderValidator.Validate<T>(r, true) : null)
            .ConfigureAwait(false);
        lease.MarkHealthy();
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
    public async ValueTask<T> QueryFirstAsync<T>(FormattableString sql, bool readFromReplica = false, CancellationToken ct = default)
        where T : class, new()
    {
        await foreach (T row in QueryAsyncEnumerable<T>(sql, readFromReplica, ct).ConfigureAwait(false))
            return row;
        throw new InvalidOperationException($"QueryFirstAsync: no rows for '{typeof(T).Name}'.");
    }

    /// <summary>直查精确单行——0 或 >1 行均抛异常。
    /// <para>评审 2026-09-02：流式精确单行——读到第 2 行立即失败并释放 reader，
    /// 不再经 <see cref="QueryAsync{T}"/> 物化全表（大表上的隐式全读）。因流式提前终止，
    /// 多于 1 行时报告 "at least 2" 而非精确总数。</para>
    /// <para><b>ITM-700 警告</b>：原始 SQL 入口，默认过滤（[SoftDelete]/[TenantAware]）不适用（同 QueryAsync 契约）。</para></summary>
    public async ValueTask<T> QuerySingleAsync<T>(FormattableString sql, bool readFromReplica = false, CancellationToken ct = default)
        where T : class, new()
    {
        int count = 0;
        T single = default!;
        await foreach (T row in QueryAsyncEnumerable<T>(sql, readFromReplica, ct).ConfigureAwait(false))
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
    public ValueTask<T?> ScalarAsync<T>(FormattableString sql, bool readFromReplica = false, CancellationToken ct = default)
    {
        if (_operationState.CompletedOps >= SessionOperationState.PrewarmThreshold) _operationState.PrewarmCurrentOperationOwner();
        return ScalarEntryAsync<T>(sql, readFromReplica, ct);
    }

    private async ValueTask<T?> ScalarEntryAsync<T>(FormattableString sql, bool readFromReplica, CancellationToken ct)
    {
        using SessionOperationState.SessionOperationLease operation = EnterReadOnly();
        // 刀2：事务单次取用（刀1 不适用：Scalar 的 T 是标量类型而非实体，无注册表查找）。
        DbTransaction? scalarTransaction = GetActiveTransaction();
        // P1-1：直查族命令复用（A9 同型，主连接 + 非读路由形态）——就地写失败回退下方新建路径
        string scalarCommandText = FormatSqlWithParameters(sql);
        DbCommand? reusedScalar = readFromReplica ? null : TryAcquireReusableDirectCommand(scalarCommandText);
        if (reusedScalar is not null && TryBindFormattableValuesInPlace(reusedScalar, sql))
        {
            ApplyReusableCommandState(reusedScalar, scalarTransaction, scalarCommandText);
            object? reusedResult = await reusedScalar.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return NormalizeScalarResult<T>(reusedResult);
        }

        await using ReadCommandLease lease = await CreateReadRoutedCommandAsync(readFromReplica, scalarTransaction, ct).ConfigureAwait(false);
        DbCommand cmd = lease.Command;
        cmd.CommandText = scalarCommandText;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindFormattableParameters(cmd, sql);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        lease.MarkHealthy();
        return NormalizeScalarResult<T>(result);
    }

    /// <summary>P1-1：ScalarEntryAsync 两分支共用的结果归一（原内联尾段逐位提取）。</summary>
    private static T? NormalizeScalarResult<T>(object? result)
    {
        if (result is null or DBNull) return default;
        if (result is T typed) return typed;
        return ConvertScalar<T>(result);
    }

    /// <summary>执行任意 DDL/DML。
    /// <para><b>ITM-700 警告</b>：原始 SQL 入口，默认过滤（[SoftDelete]/[TenantAware]）不适用（同 QueryAsync 契约）。</para>
    /// <para><b>R3（v5.6.0）</b>：拦截器三段式接入（此前为覆盖面缺口）——OnBefore/OnAfter/OnError
    /// 与 ToListAsync 同语义；参数表仅在拦截器非空时物化，默认会话零开销。</para></summary>
    public async ValueTask<int> ExecuteAsync(FormattableString sql, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        // P1-1（2026-10-12 全量重读）：直查族命令复用（A9 同型，主连接形态）——重复执行的
        // DDL/DML（建表迁移循环、分批 DELETE 等同文本形态）省命令创建/释放；就地写失败回退
        // 新建路径。复用命令归槽所有（ownsCommand=false，不得释放），事务/超时每次重设。
        // 刀2：事务单次取用。
        DbTransaction? executeTransaction = GetActiveTransaction();
        string executeCommandText = FormatSqlWithParameters(sql);
        DbCommand? reusedExecute = TryAcquireReusableDirectCommand(executeCommandText);
        DbCommand cmd;
        bool ownsCommand;
        if (reusedExecute is not null && TryBindFormattableValuesInPlace(reusedExecute, sql))
        {
            ApplyReusableCommandState(reusedExecute, executeTransaction, executeCommandText);
            cmd = reusedExecute;
            ownsCommand = false;
        }
        else
        {
            cmd = CreateCommand(executeTransaction);
            cmd.CommandText = executeCommandText;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            BindFormattableParameters(cmd, sql);
            ownsCommand = true;
        }
        try
        {
            // R3（v5.6.0）：拦截器非空才物化参数表与计时——与 SELECT 管线"空列表跳过"同口径。
            // A7（2026-10-01 全 API 逐项轮）：计时改起始时间戳（与 QueryBuilderExtensions 管线同型）。
            QueryContext context = default;
            long? swStart = null;
            if (_interceptors.Count > 0)
            {
                var parameters = new List<System.Data.Common.DbParameter>(cmd.Parameters.Count);
                foreach (System.Data.Common.DbParameter parameter in cmd.Parameters) parameters.Add(parameter);
                context = new QueryContext(cmd.CommandText, parameters);
                swStart = System.Diagnostics.Stopwatch.GetTimestamp();
                QueryBuilderExtensions.NotifyInterceptorsOnBefore(_interceptors, context);
            }
            int affected;
            try
            {
                affected = await ExecuteWriteRowsAsync(cmd, executeTransaction, ct).ConfigureAwait(false);
                // ITM-865（r23）：OnAfter 移入 try——拦截器 OnAfter 抛异常时同样经 OnError 通知
                // 后上抛（与 ExecuteNonQueryAsync/ExecuteQueryAsync 口径一致；原形态在 try 外
                // 直接逃逸，调用方会误判"执行失败"而实际 DML 已成功）。
                if (swStart is not null)
                {
                    QueryBuilderExtensions.NotifyInterceptorsOnAfter(_interceptors, context, swStart, affected);
                }
            }
            catch (Exception exception)
            {
                if (swStart is not null)
                    QueryBuilderExtensions.NotifyInterceptorsOnError(_interceptors, context, exception);
                throw;
            }
            return affected;
        }
        finally
        {
            if (ownsCommand)
                await cmd.DisposeAsync().ConfigureAwait(false);
        }
    }
}
