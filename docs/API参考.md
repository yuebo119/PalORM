# PalORM API 参考

> v5.5.1 · .NET 11 · C# 15 · 源生成器驱动 · 零运行时反射
> 测试: 全仓库 665 项 `[Test]` 声明（Core + SourceGen + Integration；外部 DB 测试标注 `Category=ExternalDatabase` 不计入 badge，B14 口径）
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

### 编译时验证 — 39 条 PALORM 诊断（36 条分析器 + 3 条生成器：PALORM041/045/046）
> PALORM006/007 已删除（006 由 SqlFileEmitter Obsolete-error 机制承担，007 占位移除）。
> v5.0 扩充（2026-07-26）：PALORM023-027（实体级硬规则）+ PALORM031-033（调用级 API 误用）+ PALORM034-037/040（防静默错误）。
> v7.2 扩充（2026-08-26，ITM-640 收口）：PALORM042-044——生成器 throw/静默跳过的编译期定位面（分工同 022：分析器定位报错，生成器防御性跳过）。
> 评审批次（2026-09-02）：新增 PALORM045（生成器 transform 失败面兜底 Warning——分析器规则被
> .editorconfig/ruleset 抑制时实体静默跳过的唯一编译期线索）；PALORM003 因带已知多程序集误报、
> 默认严重度由 Error 复议为 Warning；PALORM041 category 归一为 "PalORM"；PALORM020 消息改为
> 真实格式模板（"Invalid [Index] declaration on type '{0}': {1}"）。
> 评审批次（2026-09-10，r21）：新增 PALORM041（[SqlTemplate] 同命名空间重名）与 PALORM046
> （[SqlTemplate] 声明形状/生成类名冲突）；PALORM031 由 Error 降为 Warning——SQLite 方言回退
> 逐条路径使其在该方言合法可用，而分析器无 Provider 信息（ITM-752）。
> 价值分层：P0 防崩溃（throw）/ P1 防静默错误（不 throw 但数据错/安全绕过）/ P2 风格。

| 规则 | 说明 | 层级 |
|------|------|------|
| PALORM001 | [Table] 实体必须有 [Key] | P0 |
| PALORM002 | 属性无 [Column] 建议添加 | P2 |
| PALORM003 | [ForeignKey] 引用表不存在（默认 Warning——多程序集场景可误报，ADR-D 已裁决 D3） | P1 |
| PALORM004 | [ForeignKey] 缺 OnDelete | P2 |
| PALORM005 | N+1 查询检测（循环内 From/Insert/Bulk/Save 等） | P0 |
| PALORM008-010 | OwnedJson 上下文验证 | P0 |
| PALORM011 | 拒绝限定表名（Schema/Database） | P0 |
| PALORM012-013 | 并发令牌类型约束 | P0 |
| PALORM014 | [SoftDelete] 必须有 deleted_at 列 | P0 |
| PALORM015 | 拒绝无法生成的实体形状 | P0 |
| PALORM016 | 拒绝未知类型/无效映射 | P0 |
| PALORM017 | 注解声明但不参与 DDL 告警 | P2 |
| PALORM018 | [TenantAware] 必须有 tenant_id 列 | P0 |
| PALORM019-022 | 复合主键/索引/列名冲突/Key 合法性 | P0 |
| **PALORM023** | **实体无可插入列**（运行期必崩） | **P0** |
| **PALORM024** | **实体无可更新列**（运行期必崩） | **P0** |
| **PALORM025** | **[Timestamp] 标在非时间类型** | **P0** |
| **PALORM026** | **[NotMapped] 与映射特性互斥** | **P0** |
| **PALORM027** | **[Converter] 与 [OwnedJson] 互斥** | **P0** |
| **PALORM031** | **BulkUpdateBatchAsync 对 [ConcurrencyCheck] 实体调用**（PG/MySQL 路径必抛；SQLite 回退逐条故 Warning） | **P1** |
| **PALORM032** | **Include/Join 引用未注册实体** | **P0** |
| **PALORM033** | **Select(projection).ToListAsync()**（必崩） | **P0** |
| **PALORM034** | **[Key] 非默认初值让 SaveAsync 永远走 Update** | **P1** |
| **PALORM035** | **[ConcurrencyCheck]+[IgnoreOnInsert] 乐观锁基线为 0** | **P1** |
| **PALORM036** | **#nullable disable 下引用类型不生成 IsDBNull 守卫** | **P1** |
| **PALORM037** | **[Required] + 可空注解矛盾** | **P1** |
| **PALORM040** | **[TenantAware] 租户列可空（跨租户数据可见）** | **P1** |
| **PALORM042** | **[Timestamp]+[Computed] 同标（GENERATED 列不得带 DEFAULT）** | **P0** |
| **PALORM043** | **SQL 标识符含控制字符或为空（表/列/索引/FK 名）** | **P0** |
| **PALORM044** | **[Computed] 表达式 NUL 或括号不平衡（实体曾被静默跳过）** | **P1** |
| **PALORM045** | **生成器兜底：实体被跳过且无对应分析器诊断时的编译期提示（分析器被抑制场景的最后线索）** | **P1** |
| **PALORM041** | **[SqlTemplate] 同命名空间内模板名重复** | **P1** |
| **PALORM046** | **[SqlTemplate] 声明形状非法或生成类名冲突（关键字名/带参/泛型/record 宿主/既有非 partial SqlTemplates）** | **P1** |

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
| `.WithCache(key, TTL?)` | 有界缓存（超容量先剔过期、仍满则拒绝写入；非 LRU） |
| `.AsPrepared()` | DbCommand.PrepareAsync 预编译 |
| `.WithTransaction(tran)` | 显式事务绑定 |
| `.WithTracing()` / `.WithMetrics(name)` | ActivitySource + Meter |
| `.AsDryRun()` → `DryRunResult` | SQL + 参数预览 |
| `.ForEachAsync(action, ct?)` | 流式消费——逐行回调不物化列表（大结果集省整表 List 分配；不写 WithCache；语义契约见 XML doc） |
| `CreateBatch().Append(...).ExecuteNonQueryAsync()` | 显式批量——N 条非查询语句一次往返（PG 实测 3.4×/10 语句；MySQL 驱动批；SQLite 回退顺序执行）；自动绑定活跃事务 |

> **性能提示（表达式类构建器）**：上表中标注 `expr` 的方法接收 `Expression<Func<T, ...>>`。
> C# 在调用点构造表达式树，库无法缓存——每次调用都重建，实测每棵树 512 字节加 0.5~1.6 µs。
> 热路径上把 lambda 提到 `static readonly` 字段即可消除（`UPDATE`+`Set` 分配 −18%，
> 单行查询 + `OrderBy` 分配 −12%）。`Where` / `OrWhere` / `Having` 收 `FormattableString`，
> 不受影响。详见 README「查询构建的性能提示」。

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
| `IsInTransaction` | 当前是否有活动事务（含自开与外部设入）。事务级语义 API 的前置自查点 |
| `WithTransaction(action)` / `WithTransaction<T>(func)` | 自动 Commit/Rollback + 异常保留 |
| `SavepointAsync(tran, name)` / `RollbackToAsync(tran, name)` | 保存点 |
| `WithIsolationLevel(level)` | 隔离级别 |

### 弹性配置

| API | 说明 |
|------|------|
| `.WithRetry(max, backoff?)` | 指数退避重试瞬时故障。v5.4 起覆盖连接建立与只读查询内置管线（From\<T\>() SELECT 族/GetAsync/GetAllAsync/聚合）；写入与事务内查询不自动重试 |
| `.WithCircuitBreaker(threshold, resetAfter)` | 熔断器（generation 防陈旧）。作用域同 WithRetry；写入路径不计入熔断 |
| `.WithTimeout(TimeSpan)` | 命令超时 |
| `ExecuteWithResilience(operation)` | 手动弹性执行入口——任意操作（含非幂等写）的显式弹性通道 |

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
| `[SensitiveData]` | 标记敏感字段——`Set()` 写入该列的参数值在审计日志中以 `Mask`（默认 `***MASKED***`）替代（Where 手写 SQL 洞值为脱敏边界） |
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

17 个注册字典：RowFactories / TableNames / CommandSqls（legacy 兼容载荷——运行时不消费，
当前生成器不再发射，仅为旧版本生成器片段保留可选注册）/ CommandSqlsByDialect（v5.6 起按方言族只发射会被读到的字段，跨族与本族无关字段为空串）/ BindInsert / BindUpdate / BindDelete / PkColumns / ColumnNames / PropertyToColumn / CreateTableSql（legacy 兼容载荷——同 CommandSqls，ADR-J）/ CreateTableSqlByDialect / CreateIndexSqlByDialect / SetIdDelegates / CrudMetadatas / EntityFeatures / SensitiveColumnMasks（`[SensitiveData]` 脱敏掩码，v5.5.0 起）。

**分块构建**（v5.6）：注册条目按**估算 IL 预算**切成若干 `AddChunk{N}` 方法，块内写一个可变
`RegistryDraft`，最后仍是一次 `Register`。原因：注册代码原先全落在单个 `[ModuleInitializer]
Initialize()` 里，IL 随实体数线性增长——实测 500 实体（4 列）即**单方法 611,449 B IL**，
且把同一份 IL 摊到 22 个方法后，全部方法的 JIT 编译耗时中位数从 1299.2 ms 降到 376.8 ms
（−71%，3 样本/侧）：超大方法的 JIT 代价是超线性的。按估算切块而非固定实体数，是因为单实体
IL 随列数增长（4 列 ≈1032 B、30 列 ≈2392 B）——固定"每块 N 个实体"在宽表上会重新撑破方法体。

**触达时机契约**：各模型程序集的 `ModuleInitializer` 在其模块首次被触达（任一成员被调用、
类型被实例化、静态字段被访问）时执行——引用了库程序集但从未触达其中任何类型时，该程序集
实体不会注册，运行期表现为 "not registered"。跨程序集消费方请确保实体类型被真实引用后再使用会话。

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
