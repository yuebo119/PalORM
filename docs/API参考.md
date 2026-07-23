# PalORM API 参考

> v4.0.0 · .NET 11 · C# 15 · 源生成器驱动 · 零运行时反射
> 测试: 全仓库 419 项 `[Test]` 声明（Core + SourceGen + Integration；外部 DB 测试标注 `Category=ExternalDatabase` 不计入 badge，B14 口径）
> 构建: 0 警告 / 0 错误（SonarAnalyzer P0+P1 全 error）
> Native AOT: 三 Provider publish + 原生运行通过

---

## P0 — 核心功能

### Schema & 迁移 (5/5)

| # | API | 实现位置 | 说明 |
|---|------|------|------|
| M1 | `MigrateAsync()` | `DataSession.Schema.cs` | 从 `CreateTableSqlByDialect` 选择方言 DDL；拒绝旧无方言片段 |
| M2 | `SeedAsync<T>(IEnumerable<T>)` | `DataSession_Bulk.cs` | 非默认稳定主键，事务内 Upsert 幂等 |
| M3 | `ValidateSchemaAsync<T>()` → `List<string>` | `DataSession.Schema.cs` | `ConfigureSchemaCommand()` 配置安全命令 |
| M4 | `[Unique]` / `[Index]` | `Annotations.cs` | 三方言索引 DDL（ADR-B） |
| M5 | `[Index(name,cols,unique)]` | `Annotations.cs` | 复合索引 |

### 编译时验证 — 21 条 PALORM 诊断
> PALORM006/007 已删除（006 由 SqlFileEmitter Obsolete-error 机制承担，007 占位移除）。实际编号 001-005 + 008-022，共 21 条。

| 规则 | 说明 |
|------|------|
| PALORM001 | [Table] 实体必须有 [Key] |
| PALORM002 | 属性无 [Column] 建议添加 |
| PALORM003 | [ForeignKey] 引用表不存在 |
| PALORM004 | [ForeignKey] 缺 OnDelete |
| PALORM005 | N+1 查询检测（循环内 From/Insert 等） |
| PALORM008-010 | OwnedJson 上下文验证 |
| PALORM011 | 拒绝限定表名（Schema/Database） |
| PALORM012-013 | 并发令牌类型约束 |
| PALORM014 | [SoftDelete] 必须有 deleted_at 列 |
| PALORM015 | 拒绝无法生成的实体形状 |
| PALORM016 | 拒绝未知类型/无效映射 |
| PALORM017 | 注解声明但不参与 DDL 告警 |
| PALORM018 | [TenantAware] 必须有 tenant_id 列 |
| PALORM019-022 | OwnedJson 上下文/Key 合法性 |

### 基础注解 (22 个)

`[Table]` `[Column]` `[Key]` `[NotMapped]` `[ForeignKey]` `[ConcurrencyCheck]` `[IgnoreOnInsert]` `[Required]` `[DefaultValue]` `[Timestamp]` `[Computed]` `[SensitiveData]` `[Converter]` `[SoftDelete]` `[TenantAware]` `[OwnedJson]` `[Index]` `[Unique]` `[SqlFile]` `[Schema]` `[Database]` `[SqlTemplate]`

### 查询构建器 (struct QueryBuilder&lt;T&gt;)

| API | 说明 |
|------|------|
| `From<T>()` | 返回 struct（值类型，copy-on-write） |
| `.Where(FormattableString)` / `.OrWhere(...)` | 编译时参数化 |
| `.WhereIn(expr, values)` / `.WhereNotIn(...)` | 自动分批（参数上限钳制） |
| `.OrderBy(expr)` / `.OrderByDescending` / `.ThenBy` / `.ThenByDescending` | 表达式排序 |
| `.Select(expr[])` | 源生成列映射投影（仅 DryRun/ToSql） |
| `.Take(n)` / `.Skip(n)` | 分页 |
| `.GroupBy(expr)` / `.Having(FormattableString)` | 聚合 |
| `.InnerJoin<T>()` / `.LeftJoin` / `.RightJoin` | JOIN |
| `.Include<TChild>(fk,pk)` / `.ThenInclude<TGC,TP>(gk,pk)` | 多级导航 |
| `.Set(expr, value)` | UPDATE SET |
| `.With("cte", subquery)` | CTE |
| `.UnsafeWindowOver(func, over)` | 窗口函数 |
| `.AsSplitQuery()` | 根查询模式（不执行导航装配） |
| `.ForUpdate()` / `.ForShare()` | 悲观锁 |
| `.Raw(string)` | 原始 SQL 逃生舱 |
| `.Tag("name")` / `.TagWithCaller()` | SQL 注释标签 |
| `.ForRead()` / `.ForWrite()` | 读写路由意图 |
| `.WithCommandTimeout(TimeSpan)` | 命令超时 |
| `.WithCache(key, TTL?)` | 有界 LRU 缓存 |
| `.AsPrepared()` | DbCommand.PrepareAsync 预编译 |
| `.WithTransaction(tran)` | 显式事务绑定 |
| `.WithTracing()` / `.WithMetrics(name)` | ActivitySource + Meter |
| `.AsDryRun()` → `DryRunResult` | SQL + 参数预览 |

### 执行方法 (QueryBuilderExtensions)

| API | 说明 |
|------|------|
| `.ToListAsync(ct)` | 全量物化 |
| `.FirstAsync(ct)` / `.FirstOrDefaultAsync(ct)` | LIMIT 1 |
| `.SingleAsync(ct)` / `.SingleOrDefaultAsync(ct)` | LIMIT 2 + 行数断言 |
| `.ToPageAsync(size, orderBy, lastValue?, descending?)` | Keyset 游标分页 → `(rows, total)` |
| `.ExecuteNonQueryAsync(ct)` | UPDATE/DELETE 执行 |
| `.QueryMultipleAsync(sql)` → `GridReader` | 多结果集（单活动读取） |

### 直查 / CRUD

| API | 实现 |
|------|------|
| `QueryAsync<T>(FormattableString)` | 源生成 RowFactory 物化 |
| `QueryFirstAsync<T>(...)` / `QuerySingleAsync<T>(...)` | 限制行数 |
| `ScalarAsync<T>(FormattableString)` | 标量 |
| `GetAsync<T>(object key)` | 按主键 |
| `GetAllAsync<T>()` | 全表 |
| `InsertAsync<T>(T)` → `T` | PG/SQLite RETURNING / MySQL LAST_INSERT_ID |
| `UpdateAsync<T>(T)` | 乐观锁自动检查 |
| `DeleteAsync<T>(object key)` | SoftDelete 更新 deleted_at / 物理删除 |
| `SaveAsync<T>(T)` | UPSERT（默认键 Insert / 非默认键 Upsert） |
| `ExecuteAsync(FormattableString)` → `int` | DDL/DML 直执 |

### 写入 / 批量

| API | 实现 |
|------|------|
| `BulkInsertAsync<T>(items, batchSize)` | PG Binary COPY / SQLite+MySQL 多值 INSERT |
| `BulkUpdateAsync<T>(items)` | 单事务 + 乐观锁 |
| `BulkMergeAsync<T>(items)` | 逐项 UPSERT |
| `BulkDeleteAsync<T>(keys)` | 500/批 IN 子句 |

### 聚合

| API | 说明 |
|------|------|
| `CountAsync<T>(where?)` → `long` | 软删除自动过滤 |
| `SumAsync<T>(expr)` → `decimal` | |
| `MaxAsync<T,TValue>(expr)` → `TValue?` | |
| `MinAsync<T,TValue>(expr)` → `TValue?` | |
| `AvgAsync<T>(expr)` → `double` | |

### 事务

| API | 说明 |
|------|------|
| `BeginTransactionAsync()` | 创建事务 |
| `UseTransaction(tran)` | 绑定外部事务 |
| `WithTransaction(action)` / `WithTransaction<T>(func)` | 自动 Commit/Rollback + 异常保留 |
| `SavepointAsync(tran, name)` / `RollbackToAsync(tran, name)` | 保存点 |
| `WithIsolationLevel(level)` | 隔离级别 |

### 弹性配置

| API | 说明 |
|------|------|
| `.WithRetry(max, backoff?)` | 指数退避重试瞬时故障 |
| `.WithCircuitBreaker(threshold, resetAfter)` | 熔断器（generation 防陈旧） |
| `.WithTimeout(TimeSpan)` | 命令超时 |
| `ExecuteWithResilience(operation)` | 手动弹性执行入口 |

### 横切关注点

| API | 说明 |
|------|------|
| `[SoftDelete]` + `deleted_at` | 查询自动过滤 |
| `[TenantAware]` + `tenant_id` | 查询自动隔离 |
| `[ConcurrencyCheck]` + Version | 乐观锁自动检查 |
| `IgnoreFilters()` | 显式跳过全局过滤 |
| `WithTenant(id)` | 切换租户 |
| `AddInterceptor(IQueryInterceptor)` | OnBefore/OnAfter/OnError + 优先级 |
| `StoredProc("name").WithParam().WithOutputParam().ExecuteAsync()` | 存储过程 |
| `QueryAsyncEnumerable<T>(sql)` → `IAsyncEnumerable<T>` | 流式读取 |
| `HealthCheckAsync()` → `HealthResult` | SELECT 1 探活 |
| `GetRawConnection()` → `DbConnection` | 逃生舱 |

### 横切注解

| 注解 | 说明 |
|------|------|
| `[Column(StoreAs=...)]` | 存储格式 |
| `[Computed("SQL")]` | GENERATED ALWAYS AS ... STORED |
| `[SensitiveData]` | 标记敏感字段 |
| `[Converter(typeof(T))]` | 自定义值转换器 |
| `[OwnedJson]` / `[OwnedJson(typeof(Context))]` | JSON 序列化列 |
| `[SqlFile("path.sql")]` | 编译时嵌入 SQL 文件 |
| `[SqlTemplate("name")]` | 提取 FormattableString 为常量 |

---

## DbOptions

| 属性 | 说明 |
|------|------|
| `ConnectionString` / `ReadConnectionString` | 主库 / 只读副本（支持 `$ENV:VAR`） |
| `CommandTimeout` / `ConnectionTimeout` | TimeSpan |
| `MaxRetries` / `RetryBackoff` | 重试策略 |
| `CircuitBreakerThreshold` / `CircuitBreakerResetAfter` | 熔断策略 |
| `MaxPoolSize` / `PoolIdleTimeoutSeconds` / `PoolLifetimeMinutes` | 连接池（PG/MySQL） |
| `Interceptors` | `List<IQueryInterceptor>` |
| `LoggerFactory` | `ILoggerFactory?` |
| `QueryCache` | `IQueryCache?`——未注入时各会话共享进程级 `BoundedQueryCache` 默认实例（容量 1024）。`BoundedQueryCache` 自 .NET 11 起暴露 OTel 指标（对齐 MemoryCache 标准口径）：`palorm.cache.requests{outcome=hit\|miss}`、`palorm.cache.evictions`、`palorm.cache.entries`、`palorm.cache.estimated_size`，经 `PalORM` Meter 上游 OTLP 导出。 |
| `ValidateQueryColumnOrder` | ADR-A 列序契约 |
| `NamingConvention` | None / SnakeCase / LowerCase |

---

## PostgreSQL 专有

| API | 说明 |
|------|------|
| `WhereJson(column, path, value)` | JSONB 路径条件 `"col"->>@p0 = @p1` |
| `PgNotificationListener` | NOTIFY/LISTEN 异步监听（自动重连 + 探针 + 订阅者隔离） |
| `NotifyAsync(connectionString, channel, payload)` | 发送 NOTIFY（`pg_notify()` 参数化） |

---

## Provider 扩展 (IDbProvider)

| 成员 | 类型 | 说明 |
|------|------|------|
| `Name` / `Dialect` | static abstract | Provider 标识 |
| `CreateConnection(cs, options)` | static abstract | 连接池映射 |
| `QuoteIdentifier(id)` / `QuoteQualifiedIdentifier(schema, id)` | static abstract | 标识符引用 |
| `GetParameterPlaceholder(index)` | static virtual | `@p{N}`（默认实现） |
| `CreateParameter(name, value)` | static abstract | DbParameter 创建 |
| `SupportsReturningClause` | static abstract | PG/SQLite true / MySQL false |
| `CurrentTimestampExpression` | static abstract | `CURRENT_TIMESTAMP` |
| `IsTransient(exception)` | static virtual | 瞬时故障判定 |
| `InitializeConnectionAsync(conn, ct)` | static virtual | SQLite PRAGMA FK+WAL |
| `IsUniqueViolation(exception)` | static virtual | 唯一约束错误码 |
| `IsDuplicateSchemaObject(exception)` | static virtual | 架构对象已存在 |
| `BulkInsertAsync(...)` | static virtual | Provider 原生批量 |
| `ConfigureSchemaCommand(cmd, table, schema?)` | static abstract | 列信息查询 |

---

## 运行时元数据注册

源生成器为每个模型程序集生成 `RegistryFragment`，通过 `PalORM_Runtime.Register(fragment)` 注册。运行时一次发布不可变快照（FrozenDictionary），外部只读。

16 个注册字典：RowFactories / TableNames / CommandSqls / CommandSqlsByDialect / BindInsert / BindUpdate / BindDelete / PkColumns / ColumnNames / PropertyToColumn / CreateTableSql / CreateTableSqlByDialect / CreateIndexSqlByDialect / SetIdDelegates / CrudMetadatas / EntityFeatures。

---

## 设计决策记录

| 决策 | 理由 |
|------|------|
| struct QueryBuilder | 值类型避免堆分配；copy-on-write 保证条件隔离 |
| static abstract Provider | 编译时分发，零虚调用，AOT 友好 |
| ValueStringBuilder | 栈分配 + ArrayPool 兜底，消除热路径 GC 压力 |
| FrozenDictionary 注册表 | 不可变快照，读无锁，AOT 友好 |
| CrudMetadata 聚合 | 单次 TryGetValue 替代四次独立查找 |
| UPSERT 单次往返 | ON CONFLICT / ON DUPLICATE KEY |
| BulkOperationFramework | 三 Provider 共享 probe + cleanup 骨架 |
| CircuitBreaker 独立 | 熔断状态机与重试循环正交组合 |
| LoggerMessage 源生成 | 零装箱 + 零字符串格式化开销 |
| FormattableString 参数化 | 编译时提取参数值，杜绝 SQL 注入 |
