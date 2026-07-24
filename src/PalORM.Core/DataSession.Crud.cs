using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace PalORM;

public sealed partial class DataSession<TProvider>
    where TProvider : IDbProvider
{
    /// <summary>见 DataSession 主文档。</summary>
    public QueryBuilder<T> From<T>() where T : class, new()
    {
        _operationState.EnsureAvailable();

        // v3.1：单次 Volatile.Read 复用——替代三次独立属性访问（每个属性各自触发 fence）。
        // 单查询固定开销下降 ~2 次内存屏障（与 state 快照合并方案一致）。
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._rowFactories.TryGetValue(typeof(T), out var factory))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' is not registered. Add [Table] attribute.");
        if (!state._tableNames.TryGetValue(typeof(T), out var tableName))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no [Table] attribute.");
        if (!state._columnNames.TryGetValue(typeof(T), out var columnNames))
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated column metadata.");

        // v4.1：使用实例字段缓存的读连接工厂，避免每次 From<T> 新建闭包
        var builder = new QueryBuilder<T>(new QueryBuilderContext<T>(
            _conn,
            new QueryBuilderServices<T>(
                TProvider.Dialect, (Func<DbDataReader, T>)factory!, _interceptors,
                TProvider.CreateParameter, TProvider.QuoteIdentifier,
                _operationState, _options.CommandTimeout),
            tableName, columnNames, _readConnFactory,
            _options.QueryCache, _options.ValidateQueryColumnOrder,
            static (conn, ct) => TProvider.InitializeConnectionAsync(conn, ct)));

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
        return builder;
    }

    // ─── CRUD ────────────────────────────────────────────

    /// <summary>插入实体，返回带自增 ID 的实体；零可插入列在访问数据库前明确失败。
    /// <para><b>跨方言差异</b>: PG/SQLite 经 RETURNING 返回完整行——含 DB 默认值与 [Computed] 列；
    /// MySQL 无 RETURNING，仅回填自增 ID，其余属性保持传入值。需要 DB 计算列的最新值时请在插入后 GetAsync 重查。</para>
    /// <para><b>回填契约</b>: 三方言下传入实体的自增 ID 均被回填（可安全继续使用原引用）；
    /// 但 DB 默认值/[Computed] 列只存在于返回实例（PG/SQLite）——两者不是同一对象。</para>
    /// <para><b>租户契约</b>（ITM-599）：Insert/BulkInsert/BulkMerge 路径<b>不附加租户 WHERE</b>
    /// （写入路径不需要过滤——租户隔离在写入时由实体的 <c>tenant_id</c> 列值承载）。
    /// 调用方必须确保 [TenantAware] 实体的 <c>tenant_id</c> 属性已赋值——NOT NULL 列约束在
    /// DB 层兜底拒绝未赋值写入，但应用层应通过 WithTenant 设置会话上下文并在构造实体时填值。</para></summary>
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
        if (!PalORM_Runtime.CurrentState._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");
        if (metadata.InsertColumns.Count == 0)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' has no generated insert metadata.");

        await using DbCommand cmd = CreateCommand();
        CommandSqlSet sqls = GetCommandSqls<T>(metadata.Sqls);

        // 双路径分发：PG/SQLite 走 RETURNING，MySQL 走 LAST_INSERT_ID。
        // 未来第三方 Provider 无 RETURNING 且非 MySQL 方言，在 LAST_INSERT_ID 分支被显式拒绝。
        return TProvider.SupportsReturningClause
            ? await InsertWithReturningAsync(cmd, sqls, metadata, entity, ct).ConfigureAwait(false)
            : await InsertWithLastInsertIdAsync(cmd, sqls, metadata, entity, ct).ConfigureAwait(false);
    }

    /// <summary>PG/SQLite 路径——INSERT ... RETURNING 单次往返物化完整行（含自增 ID）。
    /// ITM-325：调用方持有的传入实体引用同步回填自增 ID（DB 计算列仍以返回实例为准）。</summary>
    private async ValueTask<T> InsertWithReturningAsync<T>(
        DbCommand cmd, CommandSqlSet sqls, CrudMetadata metadata, T entity, CancellationToken ct)
        where T : class, new()
    {
        cmd.CommandText = sqls.InsertReturning;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        metadata.BindInsert(cmd, entity, 0);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException($"INSERT failed for '{typeof(T).Name}'.");

        T materialized = ((Func<DbDataReader, T>)metadata.RowFactory)(reader);
        // 回填对齐 MySQL 路径（ITM-325）
        var insertState = PalORM_Runtime.CurrentState;
        if (insertState._setIdDelegates.TryGetValue(typeof(T), out Action<object, long>? backfill)
            && insertState._pkColumns.TryGetValue(typeof(T), out string? pkColumn))
        {
            int pkOrdinal = reader.GetOrdinal(pkColumn);
            if (!await reader.IsDBNullAsync(pkOrdinal, ct).ConfigureAwait(false))
                backfill(entity, reader.GetInt64(pkOrdinal));
        }
        return materialized;
    }

    /// <summary>MySQL 路径——INSERT + SELECT LAST_INSERT_ID() 合并为单次 ExecuteScalarAsync。
    /// 显式方言守卫：未来第三方 Provider 走到此处应明确失败，而非收到 LAST_INSERT_ID 专有 SQL。</summary>
    private async ValueTask<T> InsertWithLastInsertIdAsync<T>(
        DbCommand cmd, CommandSqlSet sqls, CrudMetadata metadata, T entity, CancellationToken ct)
        where T : class, new()
    {
        if (TProvider.Dialect != SqlDialect.MySql)
            throw new NotSupportedException(
                $"Provider '{TProvider.Name}' does not support RETURNING and has no insert-id strategy; " +
                "only the MySQL dialect fallback (LAST_INSERT_ID) is implemented.");

        // v4.1：使用编译期预构建 const，消除运行时 string 拼接
        cmd.CommandText = sqls.InsertWithLastInsertId;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        metadata.BindInsert(cmd, entity, 0);
        long? generatedId = NormalizeGeneratedId(
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (generatedId is long id &&
            PalORM_Runtime.SetIdDelegates.TryGetValue(typeof(T), out Action<object, long>? setId))
        {
            setId(entity, id);
        }
        return entity;
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
        CancellationToken ct,
        List<Action>? deferredVersionIncrements = null) where T : class, new()
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
            updateSql += $" AND {TProvider.QuoteIdentifier("tenant_id")} = {_tenantParameterName}";

        await using DbCommand cmd = CreateCommand();
        cmd.CommandText = updateSql;
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
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
            // ITM-556：批量路径下语句成功 ≠ 事务提交——立即抬内存 version 会在整批回滚后
            // 与 DB 失同步（重试假 ConcurrencyConflictException）。批量调用方传入暂存清单，
            // 提交成功后统一回放；单条 UpdateAsync 保持原语义（语句成功即回填）。
            if (deferredVersionIncrements is null)
                metadata.IncrementVersion(entity);
            else
                deferredVersionIncrements.Add(() => metadata.IncrementVersion(entity));
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
                ? $" AND {TProvider.QuoteIdentifier("tenant_id")} = {_tenantParameterName}"
                : "";
            cmd.CommandText = $"UPDATE {TProvider.QuoteIdentifier(tn)} SET {TProvider.QuoteIdentifier("deleted_at")} = {TProvider.CurrentTimestampExpression} WHERE {TProvider.QuoteIdentifier(GetPkColumn<T>())} = @p0 AND {TProvider.QuoteIdentifier("deleted_at")} IS NULL{tenantFilter}";
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;
            BindGeneratedKeyParameter<T>(cmd, key);
            BindDefaultFilterParameters<T>(cmd);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using DbCommand delCmd = CreateCommand();
        delCmd.CommandText = HasTenantFilter<T>()
            ? $"{sqls.Delete} AND {TProvider.QuoteIdentifier("tenant_id")} = {_tenantParameterName}"
            : sqls.Delete;
        delCmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindGeneratedKeyParameter<T>(delCmd, key);
        BindDefaultFilterParameters<T>(delCmd);
        return await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // v4.1 极致降内存：per-(Type, Dialect) 缓存 selectColumns，消除每次 Get/GetAll 的 N 次 QuoteIdentifier + string.Join
    // S2743：泛型类型中的 static 字段不跨 TProvider 共享，故用非泛型 DataSessionCache 持有
    private static System.Collections.Concurrent.ConcurrentDictionary<(Type, SqlDialect), string> SelectColumnsCache
        => DataSessionCache.SelectColumnsCache;

    private static string GetSelectColumns<T>(IReadOnlyList<string> columnNames)
        => SelectColumnsCache.GetOrAdd(
            (typeof(T), TProvider.Dialect),
            _ => string.Join(", ", columnNames.Select(TProvider.QuoteIdentifier)));

    // v4.6：缓存完整 GetAsync SQL（含表名/PK 引用），消除每次插值
    // S2743：泛型中的 static 字段不共享，用非泛型 DataSessionCache 持有
    private static System.Collections.Concurrent.ConcurrentDictionary<(Type, SqlDialect, bool, bool), string> GetByKeySqlCache
        => DataSessionCache.GetByKeySqlCache;

    /// <summary>按主键查询。</summary>
    public async ValueTask<T?> GetAsync<T>(object key, CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        // v4.0 优化 B：CurrentState 单次快照--与 From<T> 对齐，替代 3 次独立 Volatile.Read（每次省 ~2 次内存屏障）。
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._rowFactories.TryGetValue(typeof(T), out object? factory)
            || !state._tableNames.TryGetValue(typeof(T), out string? tableName)
            || !state._columnNames.TryGetValue(typeof(T), out var columnNames))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered.");

        await using DbCommand cmd = CreateCommand();
        string filter = GetDefaultFilterFragment<T>();
        // v4.6：缓存完整 GetAsync SQL（含表名/PK/过滤），消除每次插值 + QuoteIdentifier
        // key 含 hasTenant + ignoreFilters：两者影响 filter 后缀
        bool hasTenant = HasTenantFilter<T>();
        cmd.CommandText = GetByKeySqlCache.GetOrAdd(
            (typeof(T), TProvider.Dialect, hasTenant, !_ignoreFilters),
            _ =>
            {
                string selectColumns = GetSelectColumns<T>(columnNames);
                string pkColumn = GetPkColumn<T>();
                return $"SELECT {selectColumns} FROM {TProvider.QuoteIdentifier(tableName)} WHERE {TProvider.QuoteIdentifier(pkColumn)} = @p0{filter}";
            });
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindGeneratedKeyParameter<T>(cmd, key);
        BindDefaultFilterParameters<T>(cmd);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ((Func<DbDataReader, T>)factory)(reader) : default;
    }

    /// <summary>查询全表。</summary>
    public async ValueTask<List<T>> GetAllAsync<T>(CancellationToken ct = default)
        where T : class, new()
    {
        using SessionOperationState.SessionOperationLease operation = EnterOperation();
        // v4.0 优化 B：CurrentState 单次快照--与 GetAsync 和 From<T> 对齐。
        PalORM_Runtime.RuntimeRegistryState state = PalORM_Runtime.CurrentState;
        if (!state._rowFactories.TryGetValue(typeof(T), out object? factory)
            || !state._tableNames.TryGetValue(typeof(T), out string? tableName)
            || !state._columnNames.TryGetValue(typeof(T), out var columnNames))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered.");

        await using DbCommand cmd = CreateCommand();
        // v4.1：缓存 selectColumns
        string selectColumns = GetSelectColumns<T>(columnNames);
        cmd.CommandText = $"SELECT {selectColumns} FROM {TProvider.QuoteIdentifier(tableName)}{GetDefaultFilterWhereClause<T>()}";
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        BindDefaultFilterParameters<T>(cmd);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        // v4.0 优化 D：默认 Capacity 16 起步——避免 []（=0）在 10K 行场景的 14 次扩容。
        List<T> list = new(16);
        var tf = (Func<DbDataReader, T>)factory;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(tf(reader));
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
        // v4.0 优化 B 对齐：SaveCoreAsync 合并 3 次 Volatile.Read 为单次 CurrentState 快照
        // （GetAsync/GetAllAsync 已做，Save/Upsert 路径此前未对齐，省 2 次内存屏障）
        var state = PalORM_Runtime.CurrentState;
        if (!state._crudMetadatas.TryGetValue(typeof(T), out CrudMetadata metadata))
            throw new InvalidOperationException($"Type '{typeof(T).Name}' has no generated CRUD.");

        if (metadata.HasDefaultKey(entity))
        {
            return await InsertCoreAsync(
                entity, effectiveOperationOwner, ct).ConfigureAwait(false);
        }

        // UPSERT 与乐观锁语义冲突（ITM-503）：ON CONFLICT DO UPDATE 无条件覆盖，
        // 无法表达 "仅当 version 匹配才更新"。带 [ConcurrencyCheck] 的实体若走 UPSERT
        // 会静默 last-write-wins，绕过 UpdateAsync 强制的并发保护——明确失败，引导用
        // InsertAsync（新增）或 UpdateAsync（带乐观锁的更新）。
        if (metadata.IncrementVersion is not null)
            throw new NotSupportedException(
                $"SaveAsync (UPSERT) cannot honor [ConcurrencyCheck] on '{typeof(T).Name}'; " +
                "UPSERT overwrites unconditionally and would bypass optimistic locking. " +
                "Use InsertAsync for new rows or UpdateAsync (which enforces the version check) for updates.");

        await using DbCommand cmd = CreateCommand();
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        metadata.BindUpsert(cmd, entity);
        if (metadata.UpsertColumns.Count != cmd.Parameters.Count)
            throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' generated {metadata.UpsertColumns.Count} upsert columns but " +
                $"{cmd.Parameters.Count} parameters.");

        // v4.1 性能优化：Upsert SQL 预构建为编译期 const，消除运行时 LINQ + string.Join 拼接
        CommandSqlSet sqls = GetCommandSqls<T>(metadata.Sqls);

        return TProvider.SupportsReturningClause
            ? await UpsertWithReturningAsync(cmd, sqls, metadata, entity, ct).ConfigureAwait(false)
            : await UpsertWithMySqlAsync(cmd, sqls, entity, ct).ConfigureAwait(false);
    }

    /// <summary>PG/SQLite UPSERT--ON CONFLICT ... DO UPDATE/NOTHING + RETURNING 物化完整行。
    /// v4.1：SQL 改用编译期预构建的 const（sqls.UpsertReturning），消除运行时拼接。</summary>
    private static async ValueTask<T> UpsertWithReturningAsync<T>(
        DbCommand cmd, CommandSqlSet sqls, CrudMetadata metadata, T entity, CancellationToken ct)
        where T : class, new()
    {
        cmd.CommandText = sqls.UpsertReturning;
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return ((Func<DbDataReader, T>)metadata.RowFactory)(reader);
        return entity;
    }

    /// <summary>MySQL UPSERT--ON DUPLICATE KEY UPDATE，自增键用 LAST_INSERT_ID(expr) 回填。
    /// v4.1：SQL 改用编译期预构建的 const（sqls.UpsertMySql）。</summary>
    private static async ValueTask<T> UpsertWithMySqlAsync<T>(
        DbCommand cmd, CommandSqlSet sqls, T entity, CancellationToken ct)
        where T : class, new()
    {
        if (TProvider.Dialect != SqlDialect.MySql)
            throw new NotSupportedException(
                $"Provider '{TProvider.Name}' does not support RETURNING and has no upsert strategy; " +
                "only the MySQL dialect fallback (ON DUPLICATE KEY UPDATE) is implemented.");

        cmd.CommandText = sqls.UpsertMySql;

        var state = PalORM_Runtime.CurrentState;
        if (!state._setIdDelegates.TryGetValue(typeof(T), out Action<object, long>? setId))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return entity;
        }

        long? generatedId = NormalizeGeneratedId(
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (generatedId is long id)
            setId(entity, id);
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

    // v4.1：Upsert SQL 已改为编译期预构建（CommandFactoryEmitter.BuildUpsertMySqlSql）。
    // 此方法保留用于旧测试验证，运行时不再调用。
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
        string setClause = BuildMySqlUpsertSetClause(updateColumns, quotedPrimaryKey, hasGeneratedKey, TProvider.QuoteIdentifier);
        if (hasGeneratedKey && updateColumns.Length > 0)
            setClause += $", {quotedPrimaryKey} = LAST_INSERT_ID({quotedPrimaryKey})";

        string sql = $"INSERT INTO {TProvider.QuoteIdentifier(tableName)} " +
            $"({columnList}) VALUES ({valueList}) " +
            $"ON DUPLICATE KEY UPDATE {setClause}";
        return hasGeneratedKey ? $"{sql}; SELECT LAST_INSERT_ID()" : sql;
    }
}
