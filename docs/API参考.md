# PalORM API 参考

> 基于 25+ ORM 调研，按严格 Native AOT 约束实现。SQLite 完整矩阵与 NuGet consumer 本机原生运行通过；PostgreSQL/MySQL 服务容器运行待 CI。
> 当前测试: Core 124/124、SourceGen 62/62、无外部服务集成 122/122；PostgreSQL/MySQL 的 4 项待 CI 验证
> 构建: 全解决方案 Release 严格构建 0 warning / 0 error · Native AOT: 三 Provider publish 通过，SQLite 与 NuGet consumer 原生运行通过
> QueryBuilder: struct（值类型）· 113 API 中 112 个已实现，1 个因设计冲突移除

## P0 — 核心功能 (92 项，1 项因设计冲突移除)

### Schema & 迁移 (6/6)

| # | API | 实现位置 | 说明 |
|---|------|------|------|
| M1 | `MigrateAsync()` | `DataSession.cs` | 优先从 `CreateTableSqlByDialect` 选择 SQLite/PostgreSQL/MySQL 编译期 DDL；旧片段回退 `CreateTableSql` |
| M2 | `SeedAsync<T>(IEnumerable<T>)` | `DataSession_Bulk.cs` | 要求非默认稳定主键，事务内复用源生成 Upsert，重复执行更新原行 |
| M3 | `ValidateSchemaAsync<T>()` → `List<string>` | `DataSession.cs:314` | `TProvider.ConfigureSchemaCommand()` 配置安全命令及列名序号 |
| M4 | `[Unique]` / `[Index]` | `Annotations.cs:115-123` | 注解可声明；当前版本不参与迁移 DDL，编译期报 PALORM017 |
| M5 | `DiffAsync<T>()` → `List<string>` | `DataSession.cs:342` | CI 检查用，格式化差异 |
| M6 | `[Index(name,cols,unique)]` | `Annotations.cs:113` | 复合索引声明；同 M4，PALORM017 告知不生成 DDL |

### 编译时验证 (6/6)

| # | API | 实现 | 说明 |
|---|------|------|------|
| V1 | 源生成 SQL 验证 | `PalORMAnalyzer.cs` | PALORM001-016 诊断规则 |
| V2 | `[SqlTemplate]` | `SqlTemplateEmitter.cs` | 源生成 `FormattableString` 常量；`AsPrepared()` 在参数绑定后调用 `DbCommand.PrepareAsync` |
| V3 | 诊断规则 | `PalORMAnalyzer.cs` | 16 条规则；PALORM008-010 验证 OwnedJson，PALORM011 拒绝限定表名，PALORM012-013 约束单一 int/long 并发令牌，PALORM014 要求软删除列，PALORM015 拒绝无法构造或写入的实体形状，PALORM016 拒绝未知类型、nullable Provider、OwnedJson/Converter 冲突和无效 Converter 映射 |
| V4 | `[SqlFile("path")]` | `SqlFileEmitter.cs` | 编译时嵌入 SQL，支持 `-- @pg/@mysql/@sqlite/@all` 条件分支 |
| V5 | `scaffold` CLI | `tools/PalORM.Scaffold` | SQLite PRAGMA → C# 实体 |
| V6 | `[Schema]`/`[Database]` | `Annotations.cs:133-147` | 当前由 PALORM011 编译错误拒绝，避免 CRUD/DDL 静默忽略限定名 |

运行时注册表保留旧 `CommandSqlSet` / `CreateTableSql`，并增加 `CommandSqlByDialect`、`CreateTableSqlSet` 及对应只读字典。SourceGen 片段提供三方言元数据；旧手工片段未提供时继续回退兼容字段。

### 基础注解 (13/13)

| # | API | 实现 |
|---|------|------|
| BA1 | `[Table("name")]` | `Annotations.cs` |
| BA2 | `[Column("name")]` | `Annotations.cs` + `Length`/`Precision`/`Scale`/`TypeName`/`StoreAs` |
| BA3 | `[Key]` | `Annotations.cs:31` |
| BA4 | `[NotMapped]` | `Annotations.cs:35` |
| BA5 | `[ForeignKey]`+`OnDelete` | `Annotations.cs:39` |
| BA6 | `[ConcurrencyCheck]` | `Annotations.cs:51` — 乐观锁 |
| BA7 | `[IgnoreOnInsert]` | `Annotations.cs:55` |
| BA8 | `[Column(Length=128)]` | `ColumnAttribute.Length` |
| BA9 | `[Column(Precision=10,Scale=2)]` | `ColumnAttribute.Precision`/`Scale` |
| BA10 | `[Required]` | `Annotations.cs:59` |
| BA11 | `[DefaultValue("NOW()")]` | `Annotations.cs:63`——注解可声明；不参与 DDL，编译期报 PALORM017 |
| BA12 | `[Timestamp]`/`[RowVersion]` | `Annotations.cs:70` |
| BA13 | `[Column(TypeName="varchar")]` | `ColumnAttribute.TypeName` |

### 查询构建器 (22/22)

| # | API | 实现 |
|---|------|------|
| QB1 | `From<T>()` → `QueryBuilder<T>` | `DataSession.cs` — 返回 struct（值类型） |
| QB2 | `.ToListAsync(ct)` | `QueryBuilderExtensions.cs` — 扩展方法 |
| QB3 | `.FirstAsync(ct)`/`.SingleAsync(ct)` | `QueryBuilderExtensions.cs` — 通过 LIMIT 走统一租约执行管线 |
| QB4 | `.FirstOrDefaultAsync(ct)`/`.SingleOrDefaultAsync(ct)` | `QueryBuilderExtensions.cs` |
| QB5 | `.ToPageAsync(pageSize,orderBy,lastValue,descending,ct)` → `(rows,total)` | `QueryBuilderExtensions.cs` — Keyset 游标分页 |
| QB6 | `.Where(FormattableString)` | `QueryBuilder.cs:68` — 编译时参数化 |
| QB7 | `.OrWhere(FormattableString)` | `QueryBuilder.cs:79` |
| QB8 | `.WhereIn(Expression,IEnumerable)` | `QueryBuilder.cs:105` — 自动分批 500 |
| QB9 | `.WhereNotIn(Expression,IEnumerable)` | `QueryBuilder.cs:122` |
| QB10 | `.OrderBy(Expression)`/`OrderByDescending` | `QueryBuilder.cs:87` |
| QB11 | `.ThenBy(Expression)`/`ThenByDescending` | `QueryBuilder.cs:90` |
| QB12 | `.Select(Expression[])` | `QueryBuilder.cs` — 使用源生成列映射；当前仅支持 DryRun/ToSql，实体执行部分投影会明确失败 |
| QB13 | `.GroupBy(Expression)` | `QueryBuilder.cs:155` |
| QB14 | `.Having(FormattableString)` | `QueryBuilder.cs:158` |
| QB15 | `.InnerJoin<TJoin>(FormattableString)` | `QueryBuilder.cs:161` |
| QB16 | `.LeftJoin<TJoin>(FormattableString)` | `QueryBuilder.cs:165` |
| QB17 | `.RightJoin<TJoin>(FormattableString)` | `QueryBuilder.cs:169` |
| QB18 | `.Set(Expression,value)` | `QueryBuilder.cs:173` |
| QB19 | `.QueryMultipleAsync(sql)`→`GridReader` | `QueryBuilderExtensions.cs` — 单活动读取；GridReader 释放前持续占用会话 operation lease；重复释放共享同一结果；函数式事务结束前自动收口遗留实例 |
| QB20 | `.UnsafeWindowOver(func,over)` | `QueryBuilder.cs:181`，仅接受可信原始 SQL 结构 |
| QB21 | `.With("cte",subquery)` | `QueryBuilder.cs:184` |
| QB22 | `.AsSplitQuery()` | `QueryBuilder.cs` — 只构建根查询并移除 JOIN，不执行导航对象装配 |

### 直查 / CRUD (9/9)

| # | API | 实现 | 备注 |
|---|------|------|------|
| D1 | `QueryAsync<T>(FormattableString)` | `DataSession.cs:480` | 源生成 RowFactory 物化 |
| D2 | `QueryFirstAsync<T>(FormattableString)` | `DataSession.cs` | 包装 QueryAsync |
| D3 | `QuerySingleAsync<T>(FormattableString)` | `DataSession.cs` | 包装 QueryAsync |
| D4 | `ScalarAsync<T>(FormattableString)` | `DataSession.cs:500` | |
| C1 | `GetAsync<T>(object key)` | `DataSession.cs:184` | 按主键 |
| C2 | `GetAllAsync<T>()` | `DataSession.cs:202` | |
| C3 | `InsertAsync<T>(T)` → `T` | `DataSession.cs` | PG/SQLite: RETURNING；MySQL: INSERT+LAST_INSERT_ID；零可插入列在数据库访问前失败 |
| C4 | `UpdateAsync<T>(T)` | `DataSession.cs` | CrudMetadatas 单次查找；key-only 实体因无可更新列明确失败 |
| C5 | `DeleteAsync<T>(object key)` | `DataSession.cs:135` | |

### 写入 / 批量 (7/7)

| # | API | 实现 | 备注 |
|---|------|------|------|
| W1 | `ExecuteAsync(FormattableString)`→`int` | `DataSession.cs:511` | |
| W2 | `SaveAsync<T>(T)` | `DataSession.cs` | 默认自增键走 Insert；非默认键走源生成 Upsert；key-only 实体使用幂等冲突分支 |
| W3 | `UpdateColumnsAsync<T>(id,partial)` | `DataSession.cs:251` | → `From<T>().Set().ExecuteNonQueryAsync()` |
| B1 | `BulkInsertAsync<T>(IReadOnlyList<T>)` | `DataSession_Bulk.cs` | 三 Provider 统一使用源生成 InsertColumns/BindInsert，执行前校验列数与参数数；零列明确失败 |
| B2 | `BulkUpdateAsync<T>(IReadOnlyList<T>)` | `DataSession_Bulk.cs` | 单事务复用源生成 Update 和乐观锁语义 |
| B3 | `BulkMergeAsync<T>(IReadOnlyList<T>)` | `DataSession_Bulk.cs` | 单事务逐项复用源生成 Save/Upsert |
| B4 | `BulkDeleteAsync<T>(IReadOnlyList<object>)` | `DataSession_Bulk.cs` | 500/批；Converter 主键走生成 binder；SoftDelete 更新 deleted_at，其他实体物理删除 |

### 事务 (5/5)

| # | API | 实现 |
|---|------|------|
| T1 | `BeginTransactionAsync()` | `DataSession.cs:525` |
| T2 | `CommitAsync()`/`RollbackAsync()` | DbTransaction 标准方法 |
| T3 | `UseTransaction(tran)` | `DataSession.cs` — 会话级事务 |
| T4 | `WithTransaction(action)` → 自动 commit/rollback | `DataSession.cs` — callback 内允许顺序操作；拒绝嵌套事务和其他逻辑流加入；commit/rollback 前自动释放遗留 GridReader；失败时保留 action/commit 主异常 |
| T5 | `WithIsolationLevel(level)` | `DataSession.cs:521` |

### 关联 / 多租户 / 预准备 (5/5)

| # | API | 实现 |
|---|------|------|
| R1 | `.Include<TChild>(fk,pk)` | `QueryBuilder.cs:176` |
| R2 | `.ThenInclude<TGrandChild,TParent>(grandChildKey,parentKey)` | `QueryBuilder.cs`；显式表达 JOIN 两端，单参数旧重载已弃用并抛错 |
| S1 | `db.WithTenant(object)` | `DataSession.cs:308` |
| S2 | `[TenantAware]` | `Annotations.cs:105` — 自动 WHERE tenant_id |
| P1 | `.AsPrepared()` | `QueryBuilder.cs` — 所有 QueryBuilder 命令在参数绑定后调用 Provider 的 `DbCommand.PrepareAsync(ct)` |

### 类型映射 (3/3)

| # | API | 实现 |
|---|------|------|
| TM1 | `[Column(StoreAs=...)]` | `ColumnAttribute.StoreAs` |
| TM2 | `[Computed("SQL_EXPR")]` | `Annotations.cs:85`；迁移生成 `GENERATED ALWAYS AS (...) STORED`，写入时排除、完整读取时回填 |
| TM3 | `[SensitiveData]` | `Annotations.cs:77` |

### 高级特性 (16 项，A1 因设计冲突移除)

> 注：A1 `WithFilter<T>(Expression<Func<T,bool>>)` 已移除——Expression 树编译需要 `Expression.Compile()`（AOT 不安全），与 FormattableString 路线冲突。全局过滤请用 `[SoftDelete]`/`[TenantAware]` + `Where(FormattableString)`。

| # | API | 实现 | 备注 |
|---|------|------|------|
| A2 | `[OwnedJson]` / `[OwnedJson(typeof(TContext))]` | `Annotations.cs:109` | `string` 原样存储；对象必须显式 STJ 上下文且仅使用 `JsonTypeInfo<T>` overload |
| A3 | `db.WithTracing()` | `PalORMMetrics.cs` + `QueryBuilderExtensions.cs` | 静态 `ActivitySource`；标签仅含 Provider、operation、outcome，不记录 SQL、参数或调用方路径 |
| A4 | `db.WithMetrics(name)` → OTel | `PalORMMetrics.cs` + `QueryBuilderExtensions.cs` | 静态 `Meter` 的 Counter + Histogram；名称不进入标签，结果区分 success/error/cancelled |
| A5 | `db.StoredProc("name")` | `DataSession.cs:471` + `StoredProcBuilder.cs` | 链式 `WithParam().WithOutputParam().QueryAsync<T>()` |
| A6 | `DataSession<TProvider>` | `DataSession.cs` | 泛型 Provider，零虚调用 |
| A7 | `.ForRead()`/`.ForWrite()` | `QueryBuilder.cs` | 仅记录路由意图；执行时打开并释放读连接，事务和写操作强制主连接。会话级同名 API 进入兼容弃用期 |
| A8 | `.WithRetry(max,backoff)` | `DataSession.cs` | 仅重试 `IDbProvider.IsTransient(exception)` 判定的数据库异常和内部命令超时；确定性异常立即失败 |
| A9 | `.WithTimeout(TimeSpan)` | `DataSession.cs` | 会话级 CommandTimeout |
| A10 | `.WithCircuitBreaker(n,reset)` | `DataSession.cs` | + `ExecuteWithResilience()` |
| A11 | `.Tag("name")`/`TagWithCaller()` | `QueryBuilder.cs` | 拒绝注释终止符和 NUL |
| A12 | `DbOptions.WithPool(...)` | `DbOptions.cs:65` | PG/MySQL 应用池参数；SQLite 自定义池参数明确失败 |
| A13 | `.AsDryRun()` → `DryRunResult` | `QueryBuilder.cs` | 预览 SQL + 参数 |
| A14 | `.Raw(string)` | `QueryBuilder.cs` | 不可信内容禁止使用的显式原始 SQL 逃生舱 |
| A15 | `.ForUpdate()`/`ForShare()` | `QueryBuilder.cs` | 悲观锁 |
| A16 | `db.HealthCheckAsync(ct)` → `HealthResult` | `DataSession.cs:551` | SELECT 1 |
| A17 | `db.GetRawConnection()` → `DbConnection` | `DataSession.cs:568` | 逃生舱 |

## P1 — 重要增值 (16/16 ✅)

| # | API | 实现 |
|---|------|------|
| AG1 | `CountAsync<T>(FormattableString?)`→`long` | `DataSession.cs:365` | `[SoftDelete]` 默认追加 `deleted_at IS NULL`，Raw SQL 不改写 |
| AG2 | `SumAsync<T>(FormattableString)`→`decimal` | `DataSession.cs`；软删除默认过滤 |
| AG3 | `MaxAsync<T,TValue>(FormattableString)`→`TValue` | `DataSession.cs`；软删除默认过滤 |
| AG4 | `MinAsync<T,TValue>(FormattableString)`→`TValue` | `DataSession.cs`；软删除默认过滤 |
| AG5 | `AvgAsync<T>(FormattableString)`→`double` | `DataSession.cs`；软删除默认过滤 |
| ST1 | `QueryAsyncEnumerable<T>(sql)`→`IAsyncEnumerable<T>` | `DataSession.cs:444` |
| T6 | `SavepointAsync("sp")`/`RollbackToAsync("sp")` | `DataSession.cs:421,430` |
| A18 | `.WithCommandTimeout(seconds)` | `QueryBuilder.cs:191` |
| A19 | `.WithCache(name,TimeSpan?)` | `QueryBuilder.cs:193` + `CacheStore.cs` |
| A20 | `db.AddInterceptor(IQueryInterceptor)` | `DataSession.cs` |
| A21 | `DbOptions.NamingConvention` | `DbOptions.cs`；init-only 会话配置，不写入全局状态 |
| A22 | `[Converter(typeof(T))]` | `Annotations.cs:92`；编译期校验并生成 `ToProvider`/`FromProvider` 调用，Ulid 必须显式配置 |
| D5 | `StoredProc.WithOutputParam<T>(name).ExecuteAsync()` | `StoredProcBuilder.cs` |
| DS1 | N+1 检测 (PALORM005) | `PalORMAnalyzer.cs` |
| TST1 | `TestDb.Sqlite()` | `TestDb.cs` |
| TST2 | `TestDb.FromRows<T>(IEnumerable<T>)` | `TestDb.cs` |

## DataSession 并发生命周期

一个 `DataSession` 绑定一个主连接并采用单消费者契约。直接 CRUD、QueryBuilder、批量写入、存储过程和 `GridReader` 共用同一 operation state；重叠数据库操作在访问 Provider 前抛 `InvalidOperationException`，不会排队。独立 DataSession 可并行。

`DisposeAsync` 拒绝新操作，并等待活动命令、未释放的 `GridReader` 和 `WithTransaction` 作用域完成；活动 operation 或事务 callback 内直接释放当前会话会明确失败，避免自等待；未完成的显式事务也必须先完成或释放。事务 callback 内可顺序调用会话 API，但不得嵌套 `WithTransaction`，也不得派生并发数据库任务；callback 遗留的 GridReader 会在 commit/rollback 前自动释放。`GetRawConnection()` 是逃生舱；通过原生连接发起的操作不受 PalORM 门禁保护，调用方自行承担并发与事务一致性。

## 运行时元数据注册

Source Generator 会为每个模型程序集生成 `RegistryFragment`，并通过 `PalORM_Runtime.Register(fragment)` 注册。`PalORM_Runtime` 的各元数据属性继续公开读取，但不再允许整体赋值；手工扩展必须构造完整 fragment，经 `Register` 完成键一致性、重复实体校验和一次性状态发布。

`ColumnNames`、`CrudMetadata.InsertColumns` 与 `CrudMetadata.UpsertColumns` 以 `IReadOnlyList<string>` 暴露，并在注册边界复制输入，调用方不能通过保留原数组引用修改已发布元数据。

这是 2.0.0 的 binary-breaking API 收紧：调用旧 setter、读取数组字段或调用旧 `CrudMetadata` 构造函数的 1.x 已编译程序集必须重新编译，并按上述只读接口迁移。

## P2 — 未来 (1/1 ✅)

| # | API | 实现 |
|---|------|------|
| V_SQL | SqlFile 条件分支 | `SqlFileEmitter.cs` — `-- @pg/@mysql/@sqlite/@all` 编译时解析 |

## Provider 扩展 (2/2 ✅)

| # | API | 实现 |
|---|------|------|
| PG1 | `WhereJson(column, path, value)` | `PostgreSqlExtensions.cs`——列名经 QuoteIdentifier，path/value 绑定参数，生成 `"col"->>@p0 = @p1` |
| PG2 | `PgNotificationListener` / `NotifyAsync()` | 瞬态断线创建新连接并重新 LISTEN；`OnError` 报告后台终止；null payload 以显式 text 参数发送 |
| PR1 | `IDbProvider.IsTransient(Exception)` | Provider 强类型瞬时故障判定；SQLite 仅接受 `BUSY/LOCKED` |

---

## 统计

| 优先级 | 数量 | 状态 |
|:--:|:--:|:--:|
| P0 | 92 | ✅ (1 项因 AOT 设计冲突移除) |
| P1 | 16 | ✅ |
| P2 | 1 | ✅ |
| Provider | 3 | ✅ |
| **合计** | **112** | **1 项因设计冲突移除** |

> 原设计 113 项，A1 `WithFilter(Expression)` 因与 FormattableString 编译时安全路线冲突已移除。实际实现 112 项。

## 最优设计决策记录

| 决策 | 说明 |
|------|------|
| `QueryBuilder<T>` 为 struct | 值类型，执行方法分离为扩展方法 |
| `ValueStringBuilder` | 栈分配替代 StringBuilder，ArrayPool 兜底 |
| `CrudMetadata` 字典合并 | 单次 `TryGetValue` 替代三次独立查找 |
| UPSERT 单次往返 | `ON CONFLICT DO UPDATE` / `ON DUPLICATE KEY UPDATE` |
| MySQL InsertAsync 单次往返 | `INSERT; SELECT LAST_INSERT_ID()` 合并 |
| BulkInsert 委派 Provider | 三 Provider 共用源生成列与 binder；PG Binary COPY，MySQL/SQLite 多值 INSERT |
| BulkDelete IN 批量 | 500/批；SoftDelete 更新 deleted_at，其他实体物理删除 |
| WhereIn 自动分批 500 | 防止参数超限 |
| ForRead/ForWrite 路由 | 执行时读连接租约；事务和写操作强制主连接 |
| `[SqlFile]` Provider 分支 | 编译时 `-- @pg` 指令解析 |
| 统一 Formattable SQL formatter | 验证复合格式语法，alignment/format specifier 不改变参数原对象 |
| `BindInsertValues` 委托 | 零 DbCommand 创建的批量路径 |
| JsonOptions 池化 | `static readonly JsonSerializerOptions` 复用 |
| SplitQuery 标记式 | BuildSql 跳过 JOIN，不变异子句列表 |
| Schema 跨 Provider | `IDbProvider.ConfigureSchemaCommand()` 负责参数、引用与列名序号 |
| 瞬时故障判定 | `IDbProvider.IsTransient(Exception)` 使用驱动强类型状态；SQLite 仅重试 `BUSY/LOCKED` |
| A1 WithFilter 移除 | Expression 编译 AOT 不安全，用 `[SoftDelete]`/`Where(FormattableString)` 替代 |
