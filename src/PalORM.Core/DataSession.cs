using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PalORM;

/// <summary>数据库会话 —— 封装连接生命周期。using-scoped 无状态，用完即弃。
/// 类似 Dapper 的 SqlConnection 扩展 + EF Core 的 DbContext，但零状态追踪。
/// 一个会话仅支持一个活动数据库操作；重叠操作会明确失败。</summary>
/// <typeparam name="TProvider">数据库 Provider 类型（PostgreSqlProvider / MySqlProvider / SqliteProvider）。</typeparam>
public sealed partial class DataSession<TProvider> : IAsyncDisposable
    where TProvider : IDbProvider
{
    private readonly DbConnection _conn;
    private DbOptions _options;
    private ResilienceExecutor _resilience;
    private readonly List<IQueryInterceptor> _interceptors;
    private readonly ILogger _logger;
    private readonly SessionOperationState _operationState = new();

    internal DataSession(DbConnection conn, DbOptions options, List<IQueryInterceptor> interceptors, ILogger? logger = null)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resilience = new ResilienceExecutor(options, TProvider.IsTransient);
        _interceptors = interceptors.OrderBy(i => i.Priority).ToList();
        _logger = logger ?? NullLogger.Instance;
        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            _logger.LogDebug("DataSession<{Provider}> created", TProvider.Name);
    }

    /// <summary>创建并打开数据库会话（含连接重试和 SQLite PRAGMA 初始化）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "DbConnection ownership is transferred to DataSession which disposes it.")]
    public static async Task<DataSession<TProvider>> CreateAsync(DbOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        string cs = options.ResolveConnectionString();
        int maxRetries = options.MaxRetries;
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            DbConnection? connection = null;
            try
            {
                connection = TProvider.CreateConnection(cs, options);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(options.ConnectionTimeout);
                await connection.OpenAsync(cts.Token).ConfigureAwait(false);

                // Provider 初始化钩子（SQLite: PRAGMA foreign_keys/WAL）。
                // 与 OpenAsync 共享连接超时和调用方取消——初始化被锁阻塞时不再无限等待。
                await TProvider.InitializeConnectionAsync(connection, cts.Token).ConfigureAwait(false);

                var interceptors = options.Interceptors?.ToList() ?? [];
                ILogger? logger = options.LoggerFactory?.CreateLogger($"PalORM.{TProvider.Name}");
                var session = new DataSession<TProvider>(connection, options, interceptors, logger);
                connection = null;
                return session;
            }
            catch (Exception exception) when (attempt < maxRetries && IsRetryable(exception, ct))
            {
                if (connection is not null)
                {
                    try { await connection.DisposeAsync().ConfigureAwait(false); }
                    catch { /* 清理失败不能覆盖连接或初始化异常。 */ }
                    connection = null;
                }
                TimeSpan delay = options.RetryBackoff?.Invoke(attempt)
                    ?? ResilienceExecutor.GetDefaultBackoff(attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException timeoutException) when (!ct.IsCancellationRequested)
            {
                // 连接超时且重试耗尽：包装为 TimeoutException，与命令路径
                // （ResilienceExecutor）对称——调用方可与"我被取消"区分（ITM-206）。
                throw new TimeoutException(
                    $"Connection open timed out after {options.ConnectionTimeout} " +
                    $"(attempt {attempt + 1}/{maxRetries + 1}).",
                    timeoutException);
            }
            finally
            {
                if (connection is not null)
                {
                    try { await connection.DisposeAsync().ConfigureAwait(false); }
                    catch { /* 清理失败不能覆盖连接或初始化异常。 */ }
                }
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    private static bool IsRetryable(Exception exception, CancellationToken callerToken)
        => exception is OperationCanceledException
            ? !callerToken.IsCancellationRequested
            : TProvider.IsTransient(exception);

    /// <summary>创建查询构建器——每次调用创建新的 struct QueryBuilder（值类型）。
    /// <para><b>为什么是 struct</b>: 避免每次查询的堆分配。高 QPS 场景(10K+)每秒省 ~2MB 堆分配。</para>
    /// <para><b>为什么每次新建</b>: GORM #7437——条件残留在构建器实例上导致数据错误。全新构建器保证条件隔离。</para>
    /// <para>自动附加: 租户过滤([TenantAware])、软删除过滤([SoftDelete])；会话事务在执行时解析。</para></summary>
    public QueryBuilder<T> From<T>() where T : class, new()
    {
        _operationState.EnsureAvailable();

        if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out var factory))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' is not registered. Add [Table] attribute.");
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out var tableName))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no [Table] attribute.");
        if (!PalORM_Runtime.ColumnNames.TryGetValue(typeof(T), out var columnNames))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated column metadata.");

        string? readConnectionString = _options.ResolveReadConnectionString();
        Func<DbConnection>? readConnFactory = readConnectionString is not null
            ? () => TProvider.CreateConnection(readConnectionString, _options)
            : null;
        var builder = new QueryBuilder<T>(_conn, TProvider.Dialect, (IRowFactory<T>)factory!,
            _interceptors, TProvider.CreateParameter, TProvider.QuoteIdentifier, tableName,
            columnNames, _options.CommandTimeout, _operationState, readConnFactory,
            _options.QueryCache, _options.ValidateQueryColumnOrder,
            static (conn, ct) => TProvider.InitializeConnectionAsync(conn, ct));

        // 自动附加默认过滤（软删/租户）——统一走 DefaultFilter 子句类别，
        // 与用户 WHERE 组恒 AND 组合，OrWhere 无法绕过（ITM-401）
        EntityFeatures features = GetEntityFeatures<T>();
        if (!_ignoreFilters && (features & EntityFeatures.SoftDelete) != 0)
            builder.AddDefaultFilter($"{TProvider.QuoteIdentifier("deleted_at")} IS NULL");
        if (_tenantId is not null && !_ignoreFilters && (features & EntityFeatures.TenantAware) != 0)
        {
            // 列名 quote 与软删过滤对齐（quote 后不含 {}，可安全进入复合格式串文本段）
            builder.AddDefaultFilter(System.Runtime.CompilerServices.FormattableStringFactory.Create(
                $"{TProvider.QuoteIdentifier("tenant_id")} = {{0}}", _tenantId));
        }
        builder._defaultClauseCount = builder._clauses.Count;
        return builder;
    }

    // ─── CRUD ────────────────────────────────────────────

    /// <summary>插入实体，返回带自增 ID 的实体；零可插入列在访问数据库前明确失败。
    /// <para><b>跨方言差异</b>: PG/SQLite 经 RETURNING 返回完整行——含 DB 默认值与 [Computed] 列；
    /// MySQL 无 RETURNING，仅回填自增 ID，其余属性保持传入值。需要 DB 计算列的最新值时请在插入后 GetAsync 重查。</para>
    /// <para><b>回填契约</b>: 三方言下传入实体的自增 ID 均被回填（可安全继续使用原引用）；
    /// 但 DB 默认值/[Computed] 列只存在于返回实例（PG/SQLite）——两者不是同一对象。</para></summary>
    public ValueTask<T> InsertAsync<T>(T entity, CancellationToken ct = default)
        where T : class, new()
        => InsertCoreAsync(entity, null, ct);

    private async ValueTask<T> InsertCoreAsync<T>(
        T entity,
        object? operationOwner,
        CancellationToken ct) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation =
            EnterOperation(operationOwner);
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        if (metadata.InsertColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated insert metadata.");

        await using DbCommand cmd = CreateCommand();
        CommandSqlSet sqls = GetCommandSqls<T>(metadata.Sqls);
        // 分支1: PG/SQLite —— RETURNING 子句, INSERT 同时返回完整行(含自增ID), 单次往返
        if (TProvider.SupportsReturningClause)
        {
            cmd.CommandText = sqls.InsertReturning;
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
            metadata.BindInsert(cmd, entity);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                T materialized = ((IRowFactory<T>)metadata.RowFactory).Read(reader);
                // 回填对齐 MySQL 路径（ITM-325）：调用方继续持有传入实体引用时，
                // 两方言下至少自增 ID 一致可见；完整 DB 计算列仍以返回实例为准
                if (PalORM_Runtime.SetIdDelegates.TryGetValue(typeof(T), out Action<object, long>? backfill)
                    && PalORM_Runtime.PkColumns.TryGetValue(typeof(T), out string? pkColumn))
                {
                    int pkOrdinal = reader.GetOrdinal(pkColumn);
                    if (!await reader.IsDBNullAsync(pkOrdinal, ct).ConfigureAwait(false))
                        backfill(entity, reader.GetInt64(pkOrdinal));
                }
                return materialized;
            }
        }
        // 分支2: MySQL —— 无 RETURNING, INSERT + SELECT LAST_INSERT_ID 合并为单次 ExecuteScalarAsync
        else
        {
            // 显式方言守卫："无 RETURNING" 不等于 "MySQL 语法"。
            // 未来第三方 Provider 走到此处应明确失败，而非收到 LAST_INSERT_ID 专有 SQL。
            if (TProvider.Dialect != SqlDialect.MySql)
                throw new NotSupportedException(
                    $"Provider '{TProvider.Name}' does not support RETURNING and has no insert-id strategy; " +
                    "only the MySQL dialect fallback (LAST_INSERT_ID) is implemented.");
            cmd.CommandText = sqls.Insert + "; SELECT LAST_INSERT_ID();";
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
            metadata.BindInsert(cmd, entity);
            long? generatedId = NormalizeGeneratedId(
                await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (generatedId is long id &&
                PalORM_Runtime.SetIdDelegates.TryGetValue(typeof(T), out Action<object, long>? setId))
            {
                setId(entity, id);
            }
            return entity;
        }
        throw new InvalidOperationException($"INSERT failed for '{typeof(T).Name}'.");
    }

    internal static long? NormalizeGeneratedId(object? value)
    {
        long id = value switch
        {
            long result => result,
            ulong result => checked((long)result),
            int result => result,
            uint result => result,
            short result => result,
            ushort result => result,
            byte result => result,
            sbyte result => result,
            null or DBNull => 0,
            // 驱动返回意外类型时明确失败，不静默丢弃自增 ID（ERR-05）。
            _ => throw new InvalidOperationException(
                $"Generated key has unexpected type '{value.GetType().Name}'; expected an integer type.")
        };
        return id > 0 ? id : null;
    }

    /// <summary>更新实体。WHERE 子句由源生成器从 [Key] 注解自动生成；无可更新列时在访问数据库前明确失败。
    /// <para>乐观锁: [ConcurrencyCheck] 注解的列自动加入 WHERE version=@old 条件, 防并发覆盖。</para>
    /// <para>单次查找: 使用 CrudMetadatas 聚合字典, 一次 TryGetValue 替代三次独立查找。</para></summary>
    public ValueTask<int> UpdateAsync<T>(T entity, CancellationToken ct = default)
        where T : class, new()
        => UpdateCoreAsync(entity, null, ct);

    private async ValueTask<int> UpdateCoreAsync<T>(
        T entity,
        object? operationOwner,
        CancellationToken ct) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation =
            EnterOperation(operationOwner);
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");

        string updateSql = GetCommandSqls<T>(metadata.Sqls).Update;
        if (updateSql.Length == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no updatable columns.");
        // 生成的 UPDATE 以 "WHERE pk = @pN [AND version = @pM]" 结尾，可安全追加租户过滤；
        // 跨租户主键的更新命中 0 行（并发实体表现为 ConcurrencyConflictException，失败关闭）
        if (HasTenantFilter<T>())
            updateSql += $" AND {TProvider.QuoteIdentifier("tenant_id")} = {TenantParameterName}";

        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = updateSql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        metadata.BindUpdate(cmd, entity);
        BindDefaultFilterParameters<T>(cmd);
        int affectedRows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (metadata.IncrementVersion is not null)
        {
            if (affectedRows == 0)
                throw new ConcurrencyConflictException(
                    $"Entity '{typeof(T).Name}' was modified by another transaction.");
            if (affectedRows != 1)
                throw new InvalidOperationException(
                    $"Concurrency update for '{typeof(T).Name}' affected {affectedRows} rows.");
            metadata.IncrementVersion(entity);
        }
        return affectedRows;
    }

    /// <summary>按主键删除。[SoftDelete] 实体执行 UPDATE deleted_at，否则物理 DELETE。</summary>
    public async ValueTask<int> DeleteAsync<T>(object key, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.CommandSqls.TryGetValue(typeof(T), out CommandSqlSet legacySqls))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        CommandSqlSet sqls = GetCommandSqls<T>(legacySqls);

        // [SoftDelete]: 物理删除改为软删除 (UPDATE deleted_at)
        bool isSoftDelete = PalORM_Runtime.EntityFeatures.TryGetValue(typeof(T), out EntityFeatures features)
            && (features & EntityFeatures.SoftDelete) != 0;
        if (isSoftDelete && PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn))
        {
            await using DbCommand cmd = CreateCommand();
            // AND deleted_at IS NULL：与 BulkDeleteAsync 幂等语义对齐——重复删除不刷新时间戳且返回 0
            string tenantFilter = HasTenantFilter<T>()
                ? $" AND {TProvider.QuoteIdentifier("tenant_id")} = {TenantParameterName}"
                : "";
            cmd.CommandText = $"UPDATE {TProvider.QuoteIdentifier(tn)} SET {TProvider.QuoteIdentifier("deleted_at")} = {TProvider.CurrentTimestampExpression} WHERE {TProvider.QuoteIdentifier(GetPkColumn<T>())} = @p0 AND {TProvider.QuoteIdentifier("deleted_at")} IS NULL{tenantFilter}";
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
            BindGeneratedKeyParameter<T>(cmd, key);
            BindDefaultFilterParameters<T>(cmd);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using DbCommand delCmd = CreateCommand();
        delCmd.CommandText = HasTenantFilter<T>()
            ? $"{sqls.Delete} AND {TProvider.QuoteIdentifier("tenant_id")} = {TenantParameterName}"
            : sqls.Delete;
        delCmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindGeneratedKeyParameter<T>(delCmd, key);
        BindDefaultFilterParameters<T>(delCmd);
        return await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>按主键查询。</summary>
    public async ValueTask<T?> GetAsync<T>(object key, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName)
            || !PalORM_Runtime.ColumnNames.TryGetValue(typeof(T), out var columnNames))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered.");

        await using DbCommand cmd = CreateCommand();
        string filter = GetDefaultFilterFragment<T>();
        string selectColumns = string.Join(", ", columnNames.Select(TProvider.QuoteIdentifier));
        cmd.CommandText = $"SELECT {selectColumns} FROM {TProvider.QuoteIdentifier(tableName)} WHERE {TProvider.QuoteIdentifier(GetPkColumn<T>())} = @p0{filter}";
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindGeneratedKeyParameter<T>(cmd, key);
        BindDefaultFilterParameters<T>(cmd);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ((IRowFactory<T>)factory).Read(reader) : default;
    }

    /// <summary>查询全表。</summary>
    public async ValueTask<List<T>> GetAllAsync<T>(CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName)
            || !PalORM_Runtime.ColumnNames.TryGetValue(typeof(T), out var columnNames))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered.");

        await using DbCommand cmd = CreateCommand();
        string selectColumns = string.Join(", ", columnNames.Select(TProvider.QuoteIdentifier));
        cmd.CommandText = $"SELECT {selectColumns} FROM {TProvider.QuoteIdentifier(tableName)}{GetDefaultFilterWhereClause<T>()}";
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindDefaultFilterParameters<T>(cmd);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<T>();
        IRowFactory<T> tf = (IRowFactory<T>)factory;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(tf.Read(reader));
        return list;
    }

    /// <summary>InsertOrUpdate —— 单次往返 UPSERT；key-only 实体使用幂等冲突分支，不生成空 SET。</summary>
    public ValueTask<T> SaveAsync<T>(T entity, CancellationToken ct = default)
        where T : class, new()
        => SaveCoreAsync(entity, null, ct);

    private async ValueTask<T> SaveCoreAsync<T>(
        T entity,
        object? operationOwner,
        CancellationToken ct) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation =
            EnterOperation(operationOwner);
        object? effectiveOperationOwner = operationOwner ?? operation.Owner;
        if (!PalORM_Runtime.CrudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata)
            || !PalORM_Runtime.PkColumns.TryGetValue(typeof(T), out string? primaryKeyColumn)
            || !PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");

        if (metadata.HasDefaultKey(entity))
        {
            return await InsertCoreAsync(
                entity, effectiveOperationOwner, ct).ConfigureAwait(false);
        }

        await using DbCommand cmd = CreateCommand();
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        metadata.BindUpsert(cmd, entity);

        IReadOnlyList<string> upsertColumns = metadata.UpsertColumns;
        if (upsertColumns.Count != cmd.Parameters.Count)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' generated {upsertColumns.Count} upsert columns but " +
                $"{cmd.Parameters.Count} parameters.");

        string[] updateColumns = upsertColumns
            .Where(column => !string.Equals(
                column, primaryKeyColumn, StringComparison.Ordinal))
            .ToArray();
        string columnList = string.Join(", ",
            upsertColumns.Select(TProvider.QuoteIdentifier));
        string valueList = string.Join(", ",
            Enumerable.Range(0, cmd.Parameters.Count).Select(static index => $"@p{index}"));

        if (TProvider.SupportsReturningClause)
        {
            if (!PalORM_Runtime.ColumnNames.TryGetValue(
                    typeof(T), out IReadOnlyList<string>? returningColumns))
            {
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' has no generated column metadata.");
            }

            string conflictAction = updateColumns.Length == 0
                ? "DO NOTHING"
                : "DO UPDATE SET " + string.Join(", ", updateColumns.Select(column =>
                    $"{TProvider.QuoteIdentifier(column)} = " +
                    $"excluded.{TProvider.QuoteIdentifier(column)}"));
            string returningList = string.Join(", ",
                returningColumns.Select(TProvider.QuoteIdentifier));
            cmd.CommandText =
                $"INSERT INTO {TProvider.QuoteIdentifier(tableName)} ({columnList}) " +
                $"VALUES ({valueList}) ON CONFLICT " +
                $"({TProvider.QuoteIdentifier(primaryKeyColumn)}) {conflictAction} " +
                $"RETURNING {returningList}";
            await using DbDataReader reader =
                await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                return ((IRowFactory<T>)metadata.RowFactory).Read(reader);
            return entity;
        }

        // MySQL 仅对编译期确认的数值自增键使用 LAST_INSERT_ID(expr)。
        if (TProvider.Dialect != SqlDialect.MySql)
            throw new NotSupportedException(
                $"Provider '{TProvider.Name}' does not support RETURNING and has no upsert strategy; " +
                "only the MySQL dialect fallback (ON DUPLICATE KEY UPDATE) is implemented.");
        bool hasGeneratedKey = PalORM_Runtime.SetIdDelegates.TryGetValue(
            typeof(T), out Action<object, long>? setId);
        cmd.CommandText = BuildMySqlUpsertSql(
            tableName, primaryKeyColumn, upsertColumns,
            cmd.Parameters.Count, hasGeneratedKey);
        if (!hasGeneratedKey)
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return entity;
        }

        long? generatedId = NormalizeGeneratedId(
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (generatedId is long id)
            setId!(entity, id);
        return entity;
    }

    private static CommandSqlSet GetCommandSqls<T>(CommandSqlSet fallback)
        where T : class, new()
    {
        if (PalORM_Runtime.CommandSqlsByDialect.TryGetValue(
                typeof(T), out CommandSqlByDialect sqls))
        {
            return sqls.Get(TProvider.Dialect);
        }
        // 拒绝回退到无方言 legacy SQL：其标识符未经引用转义（保留字/特殊字符表列名
        // 产生错误语句），仅旧版本生成器的模型程序集会走到这里——要求重新编译。
        _ = fallback;
        throw new InvalidOperationException(
            $"Type '{typeof(T).Name}' has no dialect-specific generated SQL. " +
            "The model assembly was compiled with an older PalORM source generator; recompile it against the current version.");
    }

    internal static string BuildMySqlUpsertSql(
        string tableName,
        string primaryKeyColumn,
        IReadOnlyList<string> upsertColumns,
        int parameterCount,
        bool hasGeneratedKey)
    {
        if (upsertColumns.Count != parameterCount)
            throw new InvalidOperationException(
                $"MySQL upsert generated {upsertColumns.Count} columns but " +
                $"{parameterCount} parameters.");

        string[] updateColumns = upsertColumns
            .Where(column => !string.Equals(
                column, primaryKeyColumn, StringComparison.Ordinal))
            .ToArray();
        string columnList = string.Join(", ",
            upsertColumns.Select(TProvider.QuoteIdentifier));
        string valueList = string.Join(", ",
            Enumerable.Range(0, parameterCount)
                .Select(static index => $"@p{index}"));
        string quotedPrimaryKey = TProvider.QuoteIdentifier(primaryKeyColumn);
        string setClause = updateColumns.Length == 0
            ? hasGeneratedKey
                ? $"{quotedPrimaryKey} = LAST_INSERT_ID({quotedPrimaryKey})"
                : $"{quotedPrimaryKey} = VALUES({quotedPrimaryKey})"
            : string.Join(", ", updateColumns.Select(column =>
                $"{TProvider.QuoteIdentifier(column)} = " +
                $"VALUES({TProvider.QuoteIdentifier(column)})"));
        if (hasGeneratedKey && updateColumns.Length > 0)
            setClause += $", {quotedPrimaryKey} = LAST_INSERT_ID({quotedPrimaryKey})";

        string sql = $"INSERT INTO {TProvider.QuoteIdentifier(tableName)} " +
            $"({columnList}) VALUES ({valueList}) " +
            $"ON DUPLICATE KEY UPDATE {setClause}";
        return hasGeneratedKey ? $"{sql}; SELECT LAST_INSERT_ID()" : sql;
    }

    /// <summary>部分更新 — 通过 QueryBuilder.Set() 链式构建，编译时安全。</summary>
    public QueryBuilder<T> UpdateColumns<T>() where T : class, new()
        => From<T>();

    /// <summary>忽略全局过滤器（[SoftDelete]/[TenantAware]）。设置后本次会话所有查询跳过自动过滤。</summary>
    public DataSession<TProvider> IgnoreFilters() { _ignoreFilters = true; return this; }
    internal bool _ignoreFilters;

    /// <summary>动态添加查询拦截器（日志/缓存/审计），并按 <see cref="IQueryInterceptor.Priority"/> 执行。
    /// 与其他会话操作一样受门禁保护：有查询在飞时调用会明确失败（拦截器列表被执行管线枚举，无锁修改是竞态）。</summary>
    public DataSession<TProvider> AddInterceptor(IQueryInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        int index = _interceptors.FindIndex(existing => existing.Priority > interceptor.Priority);
        if (index < 0)
            _interceptors.Add(interceptor);
        else
            _interceptors.Insert(index, interceptor);
        return this;
    }

    /// <summary>设置当前会话的事务。设置后所有后续查询在此事务内执行。
    /// 调用 CommitAsync()/RollbackAsync() 后需再次设置或清空 (UseTransaction(null))。</summary>
    public DataSession<TProvider> UseTransaction(DbTransaction? tran)
    {
        if (tran is not null && !ReferenceEquals(tran.Connection, _conn))
            throw new ArgumentException("事务必须属于当前 DataSession 的主连接。", nameof(tran));
        _operationState.UseTransaction(tran);
        return this;
    }

    /// <summary>设置当前租户 ID。标注 [TenantAware] 的实体自动附加 WHERE tenant_id = @value。</summary>
    public DataSession<TProvider> WithTenant(object tenantId) { _tenantId = tenantId; return this; }
    internal object? _tenantId;

    // ─── Schema 验证 ────────────────────────────────────

    /// <summary>运行时校验 Schema 与实体定义的一致性。使用源生成器产出的列名数组，零运行时反射。</summary>
    public async ValueTask<List<string>> ValidateSchemaAsync<T>(CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        var issues = new List<string>();
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tableName))
            return issues;
        if (!PalORM_Runtime.ColumnNames.TryGetValue(typeof(T), out IReadOnlyList<string>? expectedColumns))
            return issues;

        try
        {
            await using DbCommand cmd = CreateCommand();
            int columnNameOrdinal = TProvider.ConfigureSchemaCommand(cmd, tableName);
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var dbColumns = new HashSet<string>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                dbColumns.Add(reader.GetString(columnNameOrdinal));

            foreach (string colName in expectedColumns)
            {
                if (!dbColumns.Contains(colName))
                    issues.Add($"Column '{colName}' not found in table '{tableName}'");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { issues.Add($"Validation failed: {ex.Message}"); }
        return issues;
    }

    /// <summary>Schema 差异检测（CI 仅检查不执行）。</summary>
    public async ValueTask<List<string>> DiffAsync<T>(CancellationToken ct = default) where T : class, new()
        => (await ValidateSchemaAsync<T>(ct).ConfigureAwait(false)).Select(d => $"[DIFF] {d}").ToList();

    // ─── 迁移 ────────────────────────────────────────────

    /// <summary>从编译时生成的 DDL 执行迁移——零运行时反射。
    /// 建表后执行 [Index]/[Unique] 索引 DDL（ADR-B）；SQLite/PG 走 IF NOT EXISTS，
    /// MySQL 靠 IsDuplicateSchemaObject 识别重名索引实现幂等。</summary>
    public async ValueTask MigrateAsync(CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        foreach (var kv in PalORM_Runtime.CreateTableSql)
        {
            string ddl = PalORM_Runtime.CreateTableSqlByDialect.TryGetValue(
                kv.Key, out CreateTableSqlSet sqls)
                ? sqls.Get(TProvider.Dialect)
                : kv.Value;
            await using DbCommand cmd = CreateCommand();
            cmd.CommandText = ddl;
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (!PalORM_Runtime.CreateIndexSqlByDialect.TryGetValue(
                    kv.Key, out CreateIndexSqlSet indexSqls))
            {
                continue;
            }
            foreach (string indexDdl in indexSqls.Get(TProvider.Dialect))
            {
                await using DbCommand indexCmd = CreateCommand();
                indexCmd.CommandText = indexDdl;
                indexCmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
                try
                {
                    await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (DbException exception) when (TProvider.IsDuplicateSchemaObject(exception))
                {
                    // MySQL 重名索引（1061）通常 = 已建过（幂等跳过）；但同名异构索引（另一实体
                    // 占用同名）也触发 1061——记录警告以免唯一约束静默缺失（ITM-203）
                    _logger.LogWarning(
                        "Index DDL skipped as duplicate; verify no cross-entity index name collision: {IndexDdl}",
                        indexDdl);
                }
            }
        }
    }

    // ─── 聚合方法 ────────────────────────────────────────

    /// <summary>COUNT 聚合。</summary>
    public async ValueTask<long> CountAsync<T>(FormattableString? where = null, CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' not registered.");
        string defaultFilter = GetDefaultFilterCondition<T>();
        string sql = $"SELECT COUNT(*) FROM {TProvider.QuoteIdentifier(tn)}";
        if (where is not null)
            // 用户条件必须整体括号包裹：含 OR 时 AND 优先级会使默认过滤对 OR 分支失效
            sql += " WHERE " + (defaultFilter.Length == 0 ? "" : defaultFilter + " AND ")
                + "(" + FormatSqlWithParameters(where) + ")";
        else if (defaultFilter.Length > 0)
            sql += " WHERE " + defaultFilter;
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        if (where is not null) BindFormattableParameters(cmd, where);
        BindDefaultFilterParameters<T>(cmd);
        object? r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return r is long l ? l : Convert.ToInt64(r);
    }

    /// <summary>SUM 聚合。</summary>
    public async ValueTask<decimal> SumAsync<T>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn)) throw new InvalidOperationException($"'{typeof(T).Name}' not registered.");
        return Convert.ToDecimal(await ExecuteScalarAsync<T>($"SELECT SUM({FormatSqlWithParameters(expression)}) FROM {TProvider.QuoteIdentifier(tn)}{GetDefaultFilterWhereClause<T>()}", expression, ct).ConfigureAwait(false));
    }

    /// <summary>MAX 聚合。TValue 限 IConvertible 基元类型（数值/字符串/DateTime）；
    /// Guid/DateOnly/枚举等经 Convert.ChangeType 会抛 InvalidCastException。</summary>
    public async ValueTask<TValue?> MaxAsync<T, TValue>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn)) throw new InvalidOperationException($"'{typeof(T).Name}' not registered.");
        object? r = await ExecuteScalarAsync<T>($"SELECT MAX({FormatSqlWithParameters(expression)}) FROM {TProvider.QuoteIdentifier(tn)}{GetDefaultFilterWhereClause<T>()}", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? default : (TValue)Convert.ChangeType(r, typeof(TValue));
    }

    /// <summary>MIN 聚合。TValue 限制同 MaxAsync。</summary>
    public async ValueTask<TValue?> MinAsync<T, TValue>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn)) throw new InvalidOperationException($"'{typeof(T).Name}' not registered.");
        object? r = await ExecuteScalarAsync<T>($"SELECT MIN({FormatSqlWithParameters(expression)}) FROM {TProvider.QuoteIdentifier(tn)}{GetDefaultFilterWhereClause<T>()}", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? default : (TValue)Convert.ChangeType(r, typeof(TValue));
    }

    /// <summary>AVG 聚合。</summary>
    public async ValueTask<double> AvgAsync<T>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn)) throw new InvalidOperationException($"'{typeof(T).Name}' not registered.");
        return Convert.ToDouble(await ExecuteScalarAsync<T>($"SELECT AVG({FormatSqlWithParameters(expression)}) FROM {TProvider.QuoteIdentifier(tn)}{GetDefaultFilterWhereClause<T>()}", expression, ct).ConfigureAwait(false));
    }

    private async ValueTask<object?> ExecuteScalarAsync<T>(string sql, FormattableString original, CancellationToken ct)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindFormattableParameters(cmd, original);
        BindDefaultFilterParameters<T>(cmd);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    // ─── 保存点 ────────────────────────────────────────

    /// <summary>事务内创建保存点。</summary>
    public async ValueTask SavepointAsync(DbTransaction tran, string name, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(tran);
        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"SAVEPOINT {TProvider.QuoteIdentifier(name)}";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>回滚到保存点。</summary>
    public async ValueTask RollbackToAsync(DbTransaction tran, string name, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(tran);
        await using DbCommand cmd = CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = $"ROLLBACK TO SAVEPOINT {TProvider.QuoteIdentifier(name)}";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ─── 存储过程 ──────────────────────────────────────

    /// <summary>存储过程入口。</summary>
    public StoredProcBuilder StoredProc(string name) => new(
        _conn, name, _options.CommandTimeout, TProvider.CreateParameter,
        _operationState, _options.ValidateQueryColumnOrder);

    /// <summary>流式查询——IAsyncEnumerable 恒定内存。</summary>
    public async IAsyncEnumerable<T> QueryAsyncEnumerable<T>(FormattableString sql, [EnumeratorCancellation] CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.RowFactories.TryGetValue(typeof(T), out object? factory))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' not registered.");

        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = FormatSqlWithParameters(sql);
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindFormattableParameters(cmd, sql);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        IRowFactory<T> tf = (IRowFactory<T>)factory;
        bool firstRow = true;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (firstRow)
            {
                ValidateColumnOrder<T>(reader);
                firstRow = false;
            }
            yield return tf.Read(reader);
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
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindFormattableParameters(cmd, sql);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<T>();
        IRowFactory<T> typedFactory = (IRowFactory<T>)factory;
        bool firstRow = true;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (firstRow)
            {
                ValidateColumnOrder<T>(reader);
                firstRow = false;
            }
            list.Add(typedFactory.Read(reader));
        }
        return list;
    }

    /// <summary>ADR-A 首行列名校验：结果列名与实体声明序列名不匹配即抛异常，
    /// 把"同型列静默交换数据"变为明确失败。仅首行执行，热路径零开销。</summary>
    private void ValidateColumnOrder<T>(DbDataReader reader) where T : class, new()
        => ColumnOrderValidator.Validate<T>(reader, _options.ValidateQueryColumnOrder);

    /// <summary>直查首行——无结果抛 InvalidOperationException。</summary>
    public async ValueTask<T> QueryFirstAsync<T>(FormattableString sql, CancellationToken ct = default)
        where T : class, new()
    {
        var results = await QueryAsync<T>(sql, ct).ConfigureAwait(false);
        return results.Count > 0 ? results[0]
            : throw new InvalidOperationException($"QueryFirstAsync: no rows for '{typeof(T).Name}'.");
    }

    /// <summary>直查精确单行——0 或 >1 行均抛异常。</summary>
    public async ValueTask<T> QuerySingleAsync<T>(FormattableString sql, CancellationToken ct = default)
        where T : class, new()
    {
        var results = await QueryAsync<T>(sql, ct).ConfigureAwait(false);
        return results.Count == 1 ? results[0]
            : throw new InvalidOperationException($"QuerySingleAsync: expected 1 row, got {results.Count}.");
    }

    /// <summary>直查标量。数据库返回类型与 <typeparamref name="T"/> 不同时按 Convert.ChangeType 转换
    /// （PG COUNT 返回 long、MySQL SUM 返回 decimal 等常见情形）；无法转换时抛 InvalidCastException 而非静默返回 default。
    /// <para><b>类型支持范围</b>（与 MaxAsync/MinAsync 一致）：<typeparamref name="T"/> 限 IConvertible 基元类型
    /// （数值/bool/string/DateTime）及其 Nullable；Guid/枚举/DateOnly 等非 IConvertible 目标在类型不完全匹配时
    /// 抛 InvalidCastException——此类值请以 string 取回后自行 Parse。</para></summary>
    public async ValueTask<T?> ScalarAsync<T>(FormattableString sql, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = FormatSqlWithParameters(sql);
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindFormattableParameters(cmd, sql);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull) return default;
        if (result is T t) return t;
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>执行任意 DDL/DML。</summary>
    public async ValueTask<int> ExecuteAsync(FormattableString sql, CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = FormatSqlWithParameters(sql);
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindFormattableParameters(cmd, sql);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>设置会话默认隔离级别。</summary>
    public DataSession<TProvider> WithIsolationLevel(IsolationLevel level) { _isolationLevel = level; return this; }
    private IsolationLevel _isolationLevel = IsolationLevel.ReadCommitted;

    /// <summary>设置会话默认命令超时，并重置当前弹性策略状态。</summary>
    public DataSession<TProvider> WithTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout.Ticks, 0, nameof(timeout));
        UpdateResilience(_options with { CommandTimeout = timeout });
        return this;
    }

    /// <summary>启用重试策略——仅重试 Provider 判定的瞬时数据库故障和内部命令超时，并重置当前弹性策略状态。</summary>
    public DataSession<TProvider> WithRetry(int maxRetries, Func<int, TimeSpan>? backoff = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        UpdateResilience(_options with { MaxRetries = maxRetries, RetryBackoff = backoff ?? _options.RetryBackoff });
        return this;
    }

    /// <summary>启用熔断器——连续最终失败达到阈值后快速失败，并重置当前弹性策略状态。</summary>
    public DataSession<TProvider> WithCircuitBreaker(int failureThreshold, TimeSpan resetAfter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failureThreshold);
        ArgumentOutOfRangeException.ThrowIfNegative(resetAfter.Ticks, nameof(resetAfter));
        UpdateResilience(_options with { CircuitBreakerThreshold = failureThreshold, CircuitBreakerResetAfter = resetAfter });
        return this;
    }

    /// <summary>使用会话级弹性策略执行操作（自动重试+熔断）。</summary>
    public async ValueTask<T> ExecuteWithResilience<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
        => await Volatile.Read(ref _resilience).ExecuteAsync(operation, ct).ConfigureAwait(false);

    /// <summary>使用会话级弹性策略执行操作（无返回值）。</summary>
    public async ValueTask ExecuteWithResilience(Func<CancellationToken, Task> operation, CancellationToken ct = default)
        => await Volatile.Read(ref _resilience).ExecuteAsync(operation, ct).ConfigureAwait(false);

    private void UpdateResilience(DbOptions options)
    {
        // _options 与 _resilience 保护策略对齐：先发布配置再发布执行器，
        // 跨线程读取方经 Volatile.Read(_resilience) 建立的先行关系可见到一致的 _options。
        Volatile.Write(ref _options, options);
        Volatile.Write(ref _resilience, new ResilienceExecutor(options, TProvider.IsTransient));
    }

    /// <summary>开始事务（使用会话默认隔离级别或显式指定）。</summary>
    public ValueTask<DbTransaction> BeginTransactionAsync(
        IsolationLevel? level = null, CancellationToken ct = default)
        => BeginTransactionCoreAsync(level, null, ct);

    private async ValueTask<DbTransaction> BeginTransactionCoreAsync(
        IsolationLevel? level,
        object? operationOwner,
        CancellationToken ct)
    {
        using SessionOperationState.SessionOperationLease operation =
            EnterOperation(operationOwner);
        if (GetActiveTransaction() is not null)
            throw new InvalidOperationException("DataSession does not support nested transactions.");

        DbTransaction transaction = await _conn.BeginTransactionAsync(
            level ?? _isolationLevel, ct).ConfigureAwait(false);
        try
        {
            _operationState.PublishTransaction(
                transaction, operationOwner);
            return transaction;
        }
        catch (Exception exception)
        {
            await DisposeTransactionPreservingAsync(
                transaction, exception).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>事务包裹执行——自动 commit/rollback。callback 内仅支持顺序数据库操作，不支持嵌套事务。</summary>
    public async ValueTask WithTransaction(Func<CancellationToken, Task> action,
        IsolationLevel? level = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        object owner = _operationState.EnterTransactionFlow();
        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction? transaction = null;
        Exception? primaryException = null;
        try
        {
            transaction = await BeginTransactionAsync(level, ct).ConfigureAwait(false);
            try
            {
                await action(ct).ConfigureAwait(false);
                await _operationState.DisposeTransactionResourcesAsync(null)
                    .ConfigureAwait(false);
                using SessionOperationState.SessionOperationLease operation =
                    _operationState.EnterTransactionOperation();
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                primaryException = exception;
                await _operationState.DisposeTransactionResourcesAsync(exception)
                    .ConfigureAwait(false);
                await RollbackTransactionPreservingAsync(transaction, exception)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (transaction is not null)
            {
                _operationState.RestoreTransaction(
                    transaction, previousTransaction);
            }
            try
            {
                if (transaction is not null)
                {
                    await DisposeTransactionPreservingAsync(
                        transaction, primaryException).ConfigureAwait(false);
                }
            }
            finally
            {
                _operationState.ExitTransactionFlow(owner);
            }
        }
    }

    /// <summary>事务包裹执行（带返回值）。callback 内仅支持顺序数据库操作，不支持嵌套事务。</summary>
    public async ValueTask<T> WithTransaction<T>(Func<CancellationToken, Task<T>> action,
        IsolationLevel? level = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        object owner = _operationState.EnterTransactionFlow();
        DbTransaction? previousTransaction = GetActiveTransaction();
        DbTransaction? transaction = null;
        Exception? primaryException = null;
        try
        {
            transaction = await BeginTransactionAsync(level, ct).ConfigureAwait(false);
            try
            {
                T result = await action(ct).ConfigureAwait(false);
                await _operationState.DisposeTransactionResourcesAsync(null)
                    .ConfigureAwait(false);
                using SessionOperationState.SessionOperationLease operation =
                    _operationState.EnterTransactionOperation();
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (Exception exception)
            {
                primaryException = exception;
                await _operationState.DisposeTransactionResourcesAsync(exception)
                    .ConfigureAwait(false);
                await RollbackTransactionPreservingAsync(transaction, exception)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (transaction is not null)
            {
                _operationState.RestoreTransaction(
                    transaction, previousTransaction);
            }
            try
            {
                if (transaction is not null)
                {
                    await DisposeTransactionPreservingAsync(
                        transaction, primaryException).ConfigureAwait(false);
                }
            }
            finally
            {
                _operationState.ExitTransactionFlow(owner);
            }
        }
    }

    private async ValueTask RollbackTransactionPreservingAsync(
        DbTransaction transaction,
        Exception primaryException)
    {
        try
        {
            using SessionOperationState.SessionOperationLease operation =
                _operationState.EnterTransactionOperation();
            await transaction.RollbackAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception rollbackException)
        {
            primaryException.Data["PalORM.RollbackException"] =
                rollbackException;
        }
    }

    private static async ValueTask RollbackPreservingAsync(DbTransaction transaction, Exception primaryException)
    {
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception rollbackException) { primaryException.Data["PalORM.RollbackException"] = rollbackException; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "事务释放是清理路径；异常附加到主异常，不能替换原始执行失败。")]
    private static async ValueTask DisposeTransactionPreservingAsync(
        DbTransaction transaction,
        Exception? primaryException)
    {
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupException) when (primaryException is not null)
        {
            primaryException.Data["PalORM.TransactionCleanupException"] = cleanupException;
        }
    }

    /// <summary>健康检查 —— SELECT 1 返回延迟和状态。</summary>
    public async ValueTask<HealthResult> HealthCheckAsync(CancellationToken ct = default)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        var sw = Stopwatch.StartNew();
        try
        {
            await using DbCommand cmd = CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return new HealthResult(true, sw.Elapsed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new HealthResult(false, sw.Elapsed, ex.Message);
        }
    }

    /// <summary>逃生舱 —— 获取原生 DbConnection（第三方工具集成）。原生操作不受会话并发门禁保护。</summary>
    public DbConnection GetRawConnection()
    {
        _operationState.EnsureAvailable();
        return _conn;
    }

    /// <summary>创建独立的只读会话。调用方必须释放返回的会话；新代码请使用 <c>From&lt;T&gt;().ForRead()</c>。</summary>
    [Obsolete("返回值具有独立连接所有权。请使用 From<T>().ForRead() 让查询执行管线管理连接。")]
    public async ValueTask<DataSession<TProvider>> ForRead(CancellationToken ct = default)
    {
        // 保留 $ENV: 间接引用原样传递，不物化为明文——CreateAsync 内部按需解析。
        // 物化会让刻意用环境变量避免明文驻留的配置在新 DbOptions 实例中出现明文密码。
        string? readConnectionString = _options.ReadConnectionString ?? _options.ConnectionString;
        DbOptions readOptions = _options with { ConnectionString = readConnectionString, ReadConnectionString = null };
        return await CreateAsync(readOptions, ct).ConfigureAwait(false);
    }

    /// <summary>返回当前主库会话。新代码请使用 <c>From&lt;T&gt;().ForWrite()</c> 明确查询路由。</summary>
    [Obsolete("DataSession 始终持有主连接。请使用 From<T>().ForWrite() 明确查询路由。")]
    public DataSession<TProvider> ForWrite()
    {
        return this;
    }

    /// <summary>等待活动操作、GridReader 和事务作用域结束后释放连接；重复调用共享结果，事务 callback 内调用会明确失败。</summary>
    public ValueTask DisposeAsync()
    {
        if (_operationState.IsCurrentOperationScope
            || _operationState.IsCurrentTransactionFlow)
        {
            throw new InvalidOperationException(
                "DataSession cannot be disposed from its active operation or transaction scope.");
        }
        return _operationState.DisposeAsync(DisposeCoreAsync);
    }

    private async Task DisposeCoreAsync()
    {
        // 主异常保留模式：首个清理异常作为主异常抛出，后续异常挂 Exception.Data 不丢弃
        //（与 GridReader/三 Provider 的清理约定一致）。
        Exception? cleanupException = null;
        int secondaryIndex = 0;
        foreach (IQueryInterceptor interceptor in _interceptors)
        {
            if (interceptor is not IDisposable disposable) continue;
            try { disposable.Dispose(); }
            catch (Exception exception) { RecordCleanupException(ref cleanupException, ref secondaryIndex, exception); }
        }

        try
        {
            if (_conn.State == ConnectionState.Open)
                await _conn.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { RecordCleanupException(ref cleanupException, ref secondaryIndex, exception); }

        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { RecordCleanupException(ref cleanupException, ref secondaryIndex, exception); }

        if (cleanupException is not null)
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }

    private static void RecordCleanupException(
        ref Exception? primary, ref int secondaryIndex, Exception exception)
    {
        if (primary is null)
        {
            primary = exception;
            return;
        }
        primary.Data[$"PalORM.CleanupException{secondaryIndex++}"] = exception;
    }

    private static EntityFeatures GetEntityFeatures<T>() where T : class, new()
        => PalORM_Runtime.EntityFeatures.GetValueOrDefault(typeof(T), EntityFeatures.None);

    // ─── 默认过滤（软删除 + 租户）────────────────────────
    // 三个 Get* 形态 + Bind 必须共用同一判定谓词：条件里引用了 @__tenant0 而 Bind 未绑（或反之）
    // 都会直接产生运行时错误。租户过滤覆盖所有直连读写入口（ITM-302）；
    // Insert/Save 不做租户过滤——实体自带 tenant_id 列值，由调用方负责。

    private bool HasTenantFilter<T>() where T : class, new()
        => _tenantId is not null && !_ignoreFilters
            && (GetEntityFeatures<T>() & EntityFeatures.TenantAware) != 0;

    /// <summary>默认过滤条件的参数名——避开生成 SQL 的 @p{N} 命名空间。</summary>
    private const string TenantParameterName = "@__tenant0";

    private string GetDefaultFilterCondition<T>() where T : class, new()
    {
        string softDelete = !_ignoreFilters && (GetEntityFeatures<T>() & EntityFeatures.SoftDelete) != 0
            ? $"{TProvider.QuoteIdentifier("deleted_at")} IS NULL"
            : "";
        if (!HasTenantFilter<T>())
            return softDelete;
        string tenant = $"{TProvider.QuoteIdentifier("tenant_id")} = {TenantParameterName}";
        return softDelete.Length == 0 ? tenant : $"{softDelete} AND {tenant}";
    }

    /// <summary>已有 WHERE 时的追加片段：" AND cond" 或空。</summary>
    private string GetDefaultFilterFragment<T>() where T : class, new()
    {
        string condition = GetDefaultFilterCondition<T>();
        return condition.Length == 0 ? "" : $" AND {condition}";
    }

    /// <summary>独立 WHERE 子句：" WHERE cond" 或空。</summary>
    private string GetDefaultFilterWhereClause<T>() where T : class, new()
    {
        string condition = GetDefaultFilterCondition<T>();
        return condition.Length == 0 ? "" : $" WHERE {condition}";
    }

    /// <summary>为默认过滤条件绑定参数。任何拼接了 GetDefaultFilter* 结果的命令都必须调用。</summary>
    private void BindDefaultFilterParameters<T>(DbCommand cmd) where T : class, new()
    {
        if (HasTenantFilter<T>())
            cmd.Parameters.Add(TProvider.CreateParameter(TenantParameterName, _tenantId));
    }

    private SessionOperationState.SessionOperationLease EnterOperation(
        object? operationOwner = null)
        => _operationState.Enter(operationOwner);

    private DbCommand CreateCommand()
    {
        DbCommand command = _conn.CreateCommand();
        command.Transaction = GetActiveTransaction();
        return command;
    }

    private DbTransaction? GetActiveTransaction()
        => _operationState.GetActiveTransaction();

    /// <summary>将复合格式项映射为参数名，参数值保持原始对象。</summary>
    private static string FormatSqlWithParameters(FormattableString sql)
        => FormattableSqlFormatter.Format(sql);

    /// <summary>FormattableString → DbParameter 绑定。</summary>
    private static void BindFormattableParameters(DbCommand cmd, FormattableString sql)
    {
        for (int i = 0; i < sql.ArgumentCount; i++)
        {
            object? value = sql.GetArgument(i);
            DbParameter param = TProvider.CreateParameter($"@p{i}", value);
            cmd.Parameters.Add(param);
        }
    }
}
