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

                // SQLite: 开启 FK 约束 + WAL 模式
                if (TProvider.Name == "SQLite")
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL";
                    await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }

                var interceptors = options.Interceptors?.ToList() ?? [];
                var session = new DataSession<TProvider>(connection, options, interceptors);
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
                    ?? TimeSpan.FromMilliseconds(100 << attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
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
            columnNames, _options.CommandTimeout, _operationState, readConnFactory);

        // 自动附加租户过滤
        EntityFeatures features = GetEntityFeatures<T>();
        if (!_ignoreFilters && (features & EntityFeatures.SoftDelete) != 0)
            builder.AddDefaultFilter($"{TProvider.QuoteIdentifier("deleted_at")} IS NULL");
        if (_tenantId is not null && !_ignoreFilters && (features & EntityFeatures.TenantAware) != 0)
            builder.Where($"tenant_id = {_tenantId}");
        return builder;
    }

    // ─── CRUD ────────────────────────────────────────────

    /// <summary>插入实体，返回带自增 ID 的实体；零可插入列在访问数据库前明确失败。</summary>
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
                return ((IRowFactory<T>)metadata.RowFactory).Read(reader);
        }
        // 分支2: MySQL —— 无 RETURNING, INSERT + SELECT LAST_INSERT_ID 合并为单次 ExecuteScalarAsync
        else
        {
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
            _ => 0
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

        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = updateSql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        metadata.BindUpdate(cmd, entity);
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
            cmd.CommandText = $"UPDATE {TProvider.QuoteIdentifier(tn)} SET {TProvider.QuoteIdentifier("deleted_at")} = {TProvider.CurrentTimestampExpression} WHERE {TProvider.QuoteIdentifier(GetPkColumn<T>())} = @p0";
            cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
            BindGeneratedKeyParameter<T>(cmd, key);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using DbCommand delCmd = CreateCommand();
        delCmd.CommandText = sqls.Delete;
        delCmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindGeneratedKeyParameter<T>(delCmd, key);
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
        string filter = GetSoftDeleteFilter<T>();
        string selectColumns = string.Join(", ", columnNames.Select(TProvider.QuoteIdentifier));
        cmd.CommandText = $"SELECT {selectColumns} FROM {TProvider.QuoteIdentifier(tableName)} WHERE {TProvider.QuoteIdentifier(GetPkColumn<T>())} = @p0{filter}";
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindGeneratedKeyParameter<T>(cmd, key);

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
        cmd.CommandText = $"SELECT {selectColumns} FROM {TProvider.QuoteIdentifier(tableName)}{GetSoftDeleteWhereClause<T>()}";
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;

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
        => PalORM_Runtime.CommandSqlsByDialect.TryGetValue(
            typeof(T), out CommandSqlByDialect sqls)
            ? sqls.Get(TProvider.Dialect)
            : fallback;

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

    /// <summary>动态添加查询拦截器（日志/缓存/审计），并按 <see cref="IQueryInterceptor.Priority"/> 执行。</summary>
    public DataSession<TProvider> AddInterceptor(IQueryInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
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

    /// <summary>从编译时生成的 DDL 执行迁移——零运行时反射。</summary>
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
        }
    }

    // ─── 聚合方法 ────────────────────────────────────────

    /// <summary>COUNT 聚合。</summary>
    public async ValueTask<long> CountAsync<T>(FormattableString? where = null, CancellationToken ct = default) where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' not registered.");
        string softDeleteCondition = GetSoftDeleteCondition<T>();
        string sql = $"SELECT COUNT(*) FROM {TProvider.QuoteIdentifier(tn)}";
        if (where is not null)
            sql += " WHERE " + (softDeleteCondition.Length == 0 ? "" : softDeleteCondition + " AND ")
                + FormatSqlWithParameters(where);
        else if (softDeleteCondition.Length > 0)
            sql += " WHERE " + softDeleteCondition;
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        if (where is not null) BindFormattableParameters(cmd, where);
        object? r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return r is long l ? l : Convert.ToInt64(r);
    }

    /// <summary>SUM 聚合。</summary>
    public async ValueTask<decimal> SumAsync<T>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn)) throw new InvalidOperationException($"'{typeof(T).Name}' not registered.");
        return Convert.ToDecimal(await ExecuteScalarAsync($"SELECT SUM({FormatSqlWithParameters(expression)}) FROM {TProvider.QuoteIdentifier(tn)}{GetSoftDeleteWhereClause<T>()}", expression, ct).ConfigureAwait(false));
    }

    /// <summary>MAX 聚合。</summary>
    public async ValueTask<TValue?> MaxAsync<T, TValue>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn)) throw new InvalidOperationException($"'{typeof(T).Name}' not registered.");
        object? r = await ExecuteScalarAsync($"SELECT MAX({FormatSqlWithParameters(expression)}) FROM {TProvider.QuoteIdentifier(tn)}{GetSoftDeleteWhereClause<T>()}", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? default : (TValue)Convert.ChangeType(r, typeof(TValue));
    }

    /// <summary>MIN 聚合。</summary>
    public async ValueTask<TValue?> MinAsync<T, TValue>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn)) throw new InvalidOperationException($"'{typeof(T).Name}' not registered.");
        object? r = await ExecuteScalarAsync($"SELECT MIN({FormatSqlWithParameters(expression)}) FROM {TProvider.QuoteIdentifier(tn)}{GetSoftDeleteWhereClause<T>()}", expression, ct).ConfigureAwait(false);
        return r is null or DBNull ? default : (TValue)Convert.ChangeType(r, typeof(TValue));
    }

    /// <summary>AVG 聚合。</summary>
    public async ValueTask<double> AvgAsync<T>(FormattableString expression, CancellationToken ct = default) where T : class, new()
    {
        if (!PalORM_Runtime.TableNames.TryGetValue(typeof(T), out string? tn)) throw new InvalidOperationException($"'{typeof(T).Name}' not registered.");
        return Convert.ToDouble(await ExecuteScalarAsync($"SELECT AVG({FormatSqlWithParameters(expression)}) FROM {TProvider.QuoteIdentifier(tn)}{GetSoftDeleteWhereClause<T>()}", expression, ct).ConfigureAwait(false));
    }

    private async ValueTask<object?> ExecuteScalarAsync(string sql, FormattableString original, CancellationToken ct)
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        BindFormattableParameters(cmd, original);
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
        _operationState);

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
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            yield return tf.Read(reader);
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
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(typedFactory.Read(reader));
        return list;
    }

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
    /// （PG COUNT 返回 long、MySQL SUM 返回 decimal 等常见情形）；无法转换时抛 InvalidCastException 而非静默返回 default。</summary>
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
        _options = options;
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
        Exception? cleanupException = null;
        foreach (IQueryInterceptor interceptor in _interceptors)
        {
            if (interceptor is not IDisposable disposable) continue;
            try { disposable.Dispose(); }
            catch (Exception exception) { cleanupException ??= exception; }
        }

        try
        {
            if (_conn.State == ConnectionState.Open)
                await _conn.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { cleanupException ??= exception; }

        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupException ??= exception; }

        if (cleanupException is not null)
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }

    private static EntityFeatures GetEntityFeatures<T>() where T : class, new()
        => PalORM_Runtime.EntityFeatures.GetValueOrDefault(typeof(T), EntityFeatures.None);

    private string GetSoftDeleteCondition<T>() where T : class, new()
        => !_ignoreFilters && (GetEntityFeatures<T>() & EntityFeatures.SoftDelete) != 0
            ? $"{TProvider.QuoteIdentifier("deleted_at")} IS NULL"
            : "";

    private string GetSoftDeleteFilter<T>() where T : class, new()
    {
        string condition = GetSoftDeleteCondition<T>();
        return condition.Length == 0 ? "" : $" AND {condition}";
    }

    private string GetSoftDeleteWhereClause<T>() where T : class, new()
    {
        string condition = GetSoftDeleteCondition<T>();
        return condition.Length == 0 ? "" : $" WHERE {condition}";
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
