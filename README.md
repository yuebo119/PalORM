<h1 align="center">PalORM</h1>
<p align="center"><strong>面向 Native AOT 的 .NET 11 微 ORM</strong></p>
<p align="center">
  <img src="https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet">
  <img src="https://img.shields.io/badge/tests-424%2F424-success">
  <img src="https://img.shields.io/badge/AOT-verified-success">
  <img src="https://img.shields.io/badge/IL%20suppressions-0-success">
  <img src="https://img.shields.io/badge/license-AGPL%20v3-blue">
</p>

PalORM 通过 Roslyn 源生成器在**编译时**生成数据访问代码，运行时完全禁止反射和 IL/AOT 警告抑制。支持 SQLite、PostgreSQL、MySQL 三种数据库，涵盖完整 CRUD、批量操作、多结果集、OwnedJson、乐观锁、软删除、多租户、事务编排、弹性重试、熔断器等特性。跨程序集实体注册与 NuGet consumer 的 Native AOT 原生运行均已验证。

---

## 安装

```xml
<PackageReference Include="PalORM.Core" Version="4.6.0" />
<PackageReference Include="PalORM.Sqlite" Version="4.6.0" />
<PackageReference Include="PalORM.SourceGen" Version="4.6.0"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

`PalORM.SourceGen` 是 Roslyn 增量源生成器，仅参与编译不打包到运行时。`PalORM.Core` 零第三方依赖，仅依赖 BCL + ADO.NET + `Microsoft.Extensions.Logging.Abstractions`。

---

## 快速开始

### 定义实体

```csharp
[Table("users")]
public partial class User
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("email")] public string? Email { get; set; }
}
```

实体必须是 `partial class`，源生成器为其生成 RowFactory、CommandFactory、Migration DDL 和注册代码。

### 创建会话

```csharp
// 使用预设配置
var db = await DataSession<SqliteProvider>.CreateAsync(
    DbOptions.Development("Data Source=app.db"));
await db.MigrateAsync();
```

`DataSession<TProvider>` 是 `using`-scoped 的短生命周期会话，用完即弃。`MigrateAsync()` 根据实体注解自动建表。

### CRUD

```csharp
// 插入——返回带自增 ID 的实体
var user = await db.InsertAsync(new User { Name = "Alice", Email = "a@example.com" });

// 按主键查询
var found = await db.GetAsync<User>(user.Id);

// 链式条件查询
var users = await db.From<User>()
    .Where($"name = {"Alice"}")
    .OrderBy(u => u.Id, descending: true)
    .Take(10)
    .ToListAsync();

// 更新
user.Name = "Bob";
await db.UpdateAsync(user);

// 删除（SoftDelete 实体更新 deleted_at，其他实体物理删除）
await db.DeleteAsync<User>(user.Id);
```

---

## 配置系统

### 预设配置

```csharp
// 开发环境：宽松超时 + 禁用熔断 + 单次重试
var dev = DbOptions.Development("Data Source=dev.db");

// 生产环境：严格超时 + 5 次重试 + 熔断 + 读写分离 + 连接池
var prod = DbOptions.Production("$ENV:DATABASE_URL",
    readConnectionString: "$ENV:REPLICA_URL");

// 测试环境：短超时 + 零重试 + 禁用熔断
var test = DbOptions.Testing("Data Source=:memory:");
```

### 自定义配置

```csharp
var options = new DbOptions
{
    ConnectionString = "$ENV:DATABASE_URL",
    ReadConnectionString = "$ENV:REPLICA_URL",   // 可选，配置后 ForRead() 自动路由
    CommandTimeout = TimeSpan.FromSeconds(60),    // 默认 30 秒
    ConnectionTimeout = TimeSpan.FromSeconds(15), // 默认 15 秒
    MaxRetries = 3,                              // 默认 3 次
    CircuitBreakerThreshold = 5,                 // 默认 5 次（0 = 禁用）
    CircuitBreakerResetAfter = TimeSpan.FromSeconds(30),
    LoggerFactory = loggerFactory,               // 注入 ILoggerFactory
    NamingConvention = NamingConvention.None,     // None/SnakeCase/LowerCase
    ValidateQueryColumnOrder = true,             // 查询列序校验（建议开启）
}.WithPool(maxSize: 200, idleTimeoutSeconds: 60, lifetimeMinutes: 120);

// 主动校验配置合法性（fail-fast）
options.Validate();

var db = await DataSession<SqliteProvider>.CreateAsync(options);
```

### 环境变量加载（Docker/K8s）

```csharp
var env = DbOptions.FromEnvironment("DATABASE_URL");
```

支持的环境变量：

| 变量 | 说明 |
|------|------|
| `PALORM_CONNECTION` | 主库连接串（必需） |
| `PALORM_READ_CONNECTION` | 只读副本连接串 |
| `PALORM_COMMAND_TIMEOUT` | 命令超时（秒） |
| `PALORM_CONNECTION_TIMEOUT` | 连接超时（秒） |
| `PALORM_MAX_RETRIES` | 最大重试次数 |
| `PALORM_CIRCUIT_BREAKER_THRESHOLD` | 熔断阈值 |
| `PALORM_MAX_POOL_SIZE` | 连接池大小 |

### 连接串占位符

```csharp
// $ENV: 前缀自动从环境变量读取（避免硬编码凭据）
ConnectionString = "$ENV:DATABASE_URL"
```

---

## 核心特性

### 架构设计

PalORM 的编译时→运行时数据流：

```
[Table("orders")] public partial class Order { ... }
        │
        ▼  编译时（Roslyn IIncrementalGenerator）
  ┌─────────────────────────────────────────────┐
  │ RowFactory      → Func<DbDataReader, T> 委托 │
  │ CommandFactory  → INSERT/UPDATE/UPSERT/DELETE │
  │ Migration       → 三方言 CREATE TABLE DDL    │
  │ Registry        → ModuleInitializer 注册     │
  │ SqlFile         → 编译时嵌入 .sql 文件       │
  │ SqlTemplate     → FormattableString 常量     │
  └─────────────────────────────────────────────┘
        │
        ▼  运行时
  var db = await DataSession<SqliteProvider>.CreateAsync(options);
  var orders = await db.From<Order>().Where($"status = {"active"}").ToListAsync();
```

| 组件 | 说明 |
|------|------|
| `DataSession<TProvider>` | `using`-scoped 会话，持有单个数据库连接，用完即弃。`static abstract IDbProvider` 泛型参数在 JIT 编译时特化，零虚调用 |
| `QueryBuilder<T>` | **值类型 struct**，`From<T>()` 返回栈分配副本，链式方法 copy-on-write 保证条件隔离 |
| `IDbProvider` | C# 11 `static abstract interface`，编译时分发 `CreateConnection`/`QuoteIdentifier`/`BulkInsertAsync` 等核心操作 |
| `ValueStringBuilder` | `ref struct`，栈分配 512B 初始缓冲 + `ArrayPool<char>` 兜底，BuildSql 热路径零堆分配 |
| `FrozenDictionary` 元数据 | 运行时一次发布不可变快照，读路径无锁 |

**三 Provider 架构**：`PalORM.Sqlite` / `PalORM.PostgreSql` / `PalORM.MySql` 独立程序集，互不引用。Core 通过 `IDbProvider` 接口统一调度，增删 Provider 不影响其他 Provider。

**依赖最小化**：Core 只依赖 BCL + ADO.NET + `Microsoft.Extensions.Logging.Abstractions`（.NET 共享框架提供），零第三方 NuGet 包。可观测性基于 BCL `ActivitySource` / `Meter`。

### 编译时安全与 Native AOT

| 特性 | 说明 |
|------|------|
| `FormattableString` 参数化 | SQL 使用 `$"WHERE id = {value}"` 插值语法，编译时提取参数值绑定到 `DbParameter`，从架构层面杜绝 SQL 注入。每个 FormattableString 的格式字符串在编译时已知，参数值与 SQL 结构强制分离 |
| 源生成 RowFactory | 每个 `[Table]` 实体编译时生成 `Func<DbDataReader, T>` 委托，直接调 `reader.GetInt64(0)`、`GetString(1)` 等强类型方法。零反射、零 `GetValue` 装箱、零动态列名查找。v3.1 起委托替代 `IRowFactory<T>` 接口虚分发 |
| 源生成 CommandFactory | INSERT/UPDATE/UPSERT/DELETE 四类 SQL 及 `BindInsert`/`BindUpdate` 参数绑定全部编译时生成。值类型列通过 Converter 单例转换（`static readonly`），BulkInsert 走 `BindInsertToBatch` 直绑。v4.6 新增 `BindInsertValues` 旁路 binder 实现参数对象跨批复用 |
| 源生成 Migration | `CREATE TABLE` DDL 按 SQLite/PostgreSQL/MySQL 三方言分别生成对应的列类型、主键、自增、默认值语法。`MigrateAsync()` 按 `TProvider.Dialect` 自动选择 |
| 原子元数据注册 | 源生成器为每个程序集生成 `RegistryFragment`，`ModuleInitializer` 运行时一次性注册所有实体。16 个元数据字典（RowFactories / TableNames / ColumnNames / CrudMetadatas 等）通过 `Register(fragment)` 快照锁定，外部只读 |
| Native AOT 全链路 | `IsAotCompatible=true` + `IsTrimmable=true`。禁止 `Type.GetType()`、`Assembly.GetType()`、`MakeGenericType`、`Expression.Compile()`、`Activator.CreateInstance`。三 Provider `dotnet publish -p:PublishAot=true` 原生二进制运行通过，**零 IL 抑制**（0 条 SuppressMessage 针对 IL/AOT 警告） |

### 实体建模

实体必须是 `partial class`。源生成器通过 `ForAttributeWithMetadataName("PalORM.TableAttribute")` 收集所有 `[Table]` 类，逐个生成对应的工厂代码。

```csharp
[Table("users")]
[SoftDelete]         // 自动过滤 WHERE deleted_at IS NULL
[TenantAware]        // 自动过滤 WHERE tenant_id = @current
public partial class User
{
    [Key] public long Id { get; set; }
    [Column("email")] public string Email { get; set; } = "";
    [Column("deleted_at")] public DateTime? DeletedAt { get; set; }
    [Column("tenant_id")] public string TenantId { get; set; } = "";
    [NotMapped] public string DisplayName => Email;
}
```

**注解列表**：

| 注解 | 说明 |
|------|------|
| `[Table("name")]` | 指定数据库表名，同时标识该类为 ORM 实体 |
| `[Column("name")]` | 指定列名，源生成器按此生成读写映射 |
| `[Key]` | 主键标记，支持自增（默认）和 `AutoIncrement=false` |
| `[NotMapped]` | 排除属性，不参与数据库映射 |
| `[ForeignKey]` | 外键引用声明（编译时校验引用表和 OnDelete） |
| `[ConcurrencyCheck]` | 乐观锁标记，Update 时自动检查版本递增 |
| `[IgnoreOnInsert]` | 插入时跳过该列（如数据库默认值列） |
| `[Computed("SQL")]` | 计算列，`GENERATED ALWAYS AS ... STORED` |
| `[DefaultValue("SQL")]` | 数据库默认值表达式（DDL 生成，当前未实现） |
| `[SensitiveData]` | 标记敏感字段（日志/审计脱敏提示） |
| `[Converter(typeof(T))]` | 自定义值转换器（AOT 安全，编译时生成调用代码） |
| `[SoftDelete]` | 软删除实体，查询自动附加 `WHERE deleted_at IS NULL` |
| `[TenantAware]` | 多租户实体，查询自动附加 `WHERE tenant_id = @current` |
| `[OwnedJson(typeof(Ctx))]` | JSON 序列化列（需 `JsonSerializerContext`，AOT 安全） |
| `[Index(name, cols, Unique = true)]` | 复合索引声明 |
| `[Unique]` | 唯一约束 |
| `[SqlFile("path.sql")]` | 编译时嵌入 .sql 文件为 `const string` |
| `[SqlTemplate("name")]` | 提取 `FormattableString` 为静态常量 |

### 查询能力

`QueryBuilder<T>` 是 `struct`值类型，每次 `From<T>()` 返回栈分配副本。链式方法（Where/OrderBy/Take 等）追加子句时通过写时复制保证分支隔离——任意时点复制的分支互不污染。

**执行管线**：每个子句收集 `FormattableString` 格式串和参数到 `List<QueryClause>` → `BuildSql()` 用 `ValueStringBuilder` 栈分配拼接 SQL → `ExecuteQueryAsync()` 创建 `DbCommand` 并绑定参数 → `DbDataReader` + `RowFactory` 委托逐行物化实体 → 返回 `List<T>`。

| API | 说明 |
|-----|------|
| `From<T>()` | 返回 `struct QueryBuilder<T>`（值类型，零堆分配） |
| `.Where(FormattableString)` / `.OrWhere()` | 参数化条件，用户 OR 无法绕过默认过滤（软删/租户） |
| `.OrderBy(expr)` / `.ThenBy()` | 表达式排序，支持降序 |
| `.Take(n)` / `.Skip(n)` | 分页（LIMIT/OFFSET） |
| `.Select(expr[])` | 列投影（仅 DryRun/ToSql 模式） |
| `.InnerJoin<T>()` / `.LeftJoin()` / `.RightJoin()` | SQL JOIN |
| `.Include<TChild>(fk, pk)` / `.ThenInclude()` | 多级导航关联，双参数显式指定 JOIN 两端 |
| `.WhereIn(expr, values)` / `.WhereNotIn()` | IN/NOT IN 操作，参数上限自动钳制（SQLite 999 / MySQL 65535） |
| `.GroupBy(expr)` / `.Having(FormattableString)` | GROUP BY / HAVING |
| `.With("cte", FormattableString)` | 公用表表达式（CTE） |
| `.UnsafeWindowOver(func, over)` | 窗口函数（参数为可信常量，非用户输入） |
| `.ForUpdate()` / `.ForShare()` | 悲观锁（SELECT ... FOR UPDATE / FOR SHARE） |
| `.AsSplitQuery()` | 仅构建根查询并移除 JOIN，不装配导航对象 |
| `.ForRead()` / `.ForWrite()` | 读写路由意图（需配置 ReadConnectionString） |
| `.WithCache(key, TTL)` | 有界 LRU 查询缓存 + 快照副本隔离 |
| `.AsPrepared()` | 参数绑定后调用 `DbCommand.PrepareAsync`（PG/MySQL 服务端缓存执行计划） |
| `.AsDryRun()` → `DryRunResult` | 生成 SQL + 参数列表预览，不执行数据库操作 |
| `.Tag("name")` / `.TagWithCaller()` | SQL 注释标签（`/* tag */`，可观测性辅助） |
| `.WithTracing()` / `.WithMetrics(name)` | BCL `ActivitySource` + `Meter`，零第三方可观测性 |
| `.WithCommandTimeout(TimeSpan)` | 独立命令超时 |
| `.WithTransaction(DbTransaction)` | 绑定外部事务 |
| `.ToPageAsync(size, orderBy, cursor?)` | Keyset 游标分页，返回 `(rows, total)` |

**执行方法**（QueryBuilderExtensions 扩展方法）：

| 方法 | 说明 |
|------|------|
| `.ToListAsync(ct)` | 全量物化 `List<T>` |
| `.FirstAsync(ct)` / `.FirstOrDefaultAsync(ct)` | 首行（LIMIT 1） |
| `.SingleAsync(ct)` / `.SingleOrDefaultAsync(ct)` | 唯一行断言（LIMIT 2） |
| `.ExecuteNonQueryAsync(ct)` | UPDATE/DELETE 执行 |
| `.QueryMultipleAsync(sql)` → `GridReader` | 多结果集（单 reader 读取多个 SELECT） |

### 写入能力

| API | 说明 |
|-----|------|
| `InsertAsync(T)` → `T` | 插入并返回含自增 ID 的实体。PG/SQLite 走 `INSERT ... RETURNING` 单次往返；MySQL 走 `INSERT; SELECT LAST_INSERT_ID()` |
| `UpdateAsync(T)` | 按主键更新，自动乐观锁检查 |
| `DeleteAsync<T>(key)` | 按主键删除。`[SoftDelete]` 实体执行 `UPDATE SET deleted_at`；其他实体执行物理 DELETE |
| `SaveAsync(T)` → `T` | UPSERT。默认键走 Insert，非默认键走 `ON CONFLICT DO UPDATE`（PG/SQLite）/ `ON DUPLICATE KEY UPDATE`（MySQL）。单次往返 |
| `GetAsync<T>(key)` | 按主键查询 |
| `GetAllAsync<T>()` | 全表查询（带默认过滤） |
| `BulkInsertAsync(items, batchSize?)` | 批量插入。PG 走 `NpgsqlBinaryImporter`（Binary COPY）；SQLite/MySQL 走多值 INSERT，参数上限自动钳制 |
| `BulkUpdateAsync(items)` | 单事务内批量更新，支持乐观锁 |
| `BulkDeleteAsync<T>(keys)` | 按主键批量删除，500/批 IN 子句 |
| `BulkMergeAsync(items)` | 逐项 UPSERT |
| `SeedAsync(items)` | 种子数据幂等插入，事务内逐项 Upsert |
| `QueryAsync<T>(FormattableString)` | 直查物化 `List<T>`（走源生成 RowFactory） |
| `QueryAsyncEnumerable<T>(FormattableString)` | 流式 `IAsyncEnumerable<T>`，逐行读取不物化全表 |
| `ScalarAsync<T>(FormattableString)` | 标量查询 |
| `ExecuteAsync(FormattableString)` → `int` | 直执 DDL/DML |
| `CountAsync<T>(where?)` → `long` | 计数（自动附加软删除过滤） |
| `SumAsync<T>(expr)` → `decimal` | 求和 |
| `MaxAsync<T, TValue>(expr)` → `TValue?` | 最大值 |
| `AvgAsync<T>(expr)` → `double` | 平均值 |
| `StoredProc("name")` | 存储过程入口，链式 `WithParam().QueryAsync<T>()` |

### 事务与保存点

```csharp
// 函数式事务——自动 commit/rollback + 异常保留语义
await db.WithTransaction(async ct =>
{
    var order = await db.InsertAsync(new Order { Status = "pending" }, ct);
    await db.BulkInsertAsync(order.Items, ct);
    await db.ExecuteAsync(
        $"UPDATE inventory SET stock = stock - {qty} WHERE id = {itemId}", ct);
});

// 显式事务
using var tran = await db.BeginTransactionAsync();
db.UseTransaction(tran);
await db.BulkInsertAsync(users);
await db.SavepointAsync(tran, "sp1");
await db.BulkInsertAsync(moreUsers);
await db.RollbackToAsync(tran, "sp1");  // 回退到保存点
await tran.CommitAsync();
```

### 弹性与可靠性

每个 `DataSession` 内部持有 `ResilienceExecutor`（~100 行，零外部依赖）。所有数据库操作默认不经重试/熔断——通过 `ExecuteWithResilience()` 显式包装幂等操作。

| 特性 | 说明 |
|------|------|
| **重试** | `WithRetry(maxRetries: 3)` 配置重试次数。退避策略默认指数递增（100ms→200ms→400ms），可通过 `RetryBackoff` 委托自定义。仅重试 Provider 判定的瞬时故障（`DbException.IsTransient`）和内部命令超时；调用方取消与确定性异常（唯一键冲突等）不重试 |
| **熔断器** | `WithCircuitBreaker(threshold: 5, resetAfter: 30s)`。连续失败 N 次后进入 Open 状态（快速失败抛 `CircuitBreakerOpenException`），`resetAfter` 后进入 Half-Open（允许一个探针请求验证恢复），成功则 Close，失败则回到 Open。generation 机制防止陈旧半开状态在并发下重复放行 |
| **超时** | `WithTimeout(TimeSpan)` 或 `WithCommandTimeout(seconds)`。通过 `CancellationTokenSource.CreateLinkedTokenSource(ct) + CancelAfter(timeout)` 控制。超时内未完成的操作抛 `TimeoutException`，超时后服务器可能已完成但客户端不知——幂等操作才适合重试 |
| **事务编排** | `WithTransaction(async callback)` 函数式事务。自动 Begin/Commit/Rollback，callback 内顺序使用同一 DataSession。支持事务内任意步数的 Insert/Update/BulkInsert/Execute。`BeginTransactionAsync()` + `UseTransaction(tran)` 提供显式模式 |
| **保存点** | `SavepointAsync(tran, "sp")` / `RollbackToAsync(tran, "sp")`，事务内部分回滚 |
| **异常保留** | cleanup 失败挂 `Exception.Data` 不替换原始失败。rollback/probe 清理等辅助操作的异常均通过 Data 字典附加，审计可追溯 |

### 横切关注点

| 特性 | 实现 | 说明 |
|------|------|------|
| **软删除** | `[SoftDelete]` + `deleted_at` 列 | 查询自动过滤 `WHERE deleted_at IS NULL`，`IgnoreFilters()` 取消过滤，Delete 执行 UPDATE 而非物理删除 |
| **多租户** | `[TenantAware]` + `tenant_id` 列 | 查询自动附加 `WHERE tenant_id = @__tenant0`，`WithTenant(id)` 切换租户，`IgnoreFilters()` 取消 |
| **乐观锁** | `[ConcurrencyCheck]` | Update 自动生成 `WHERE id = @id AND version = @oldVersion`，递增 `SET version = version + 1`。0 行影响抛 `ConcurrencyConflictException` |
| **拦截器** | `IQueryInterceptor` | OnBefore/OnAfter/OnError 三阶段钩子。优先级排序，抛出异常跳过后续拦截器。典型用途：审计日志、全局过滤桥接、性能采样 |
| **可观测性** | `WithTracing()` / `WithMetrics()` | BCL `ActivitySource` + `Meter`，零第三方依赖。不记录 SQL/参数/连接串/调用方路径。BoundedQueryCache 暴露 OTel 标准口径：`palorm.cache.requests{outcome=hit\|miss}`、`palorm.cache.evictions` 等 |
| **SQL 文件嵌入** | `[SqlFile("path.sql")]` | 源生成器编译时读取 .sql 文件并嵌入为 `const string`。支持 Provider 条件分支（`-- @pg` / `-- @mysql` / `-- @sqlite` / `-- @all`） |
| **SQL 模板** | `[SqlTemplate("name")]` | 提取方法体内的 `FormattableString` 为静态常量，编译时校验格式 |
| **值转换器** | `[Converter(typeof(T))]` | AOT 安全：源生成器直接生成 `new Converter().ToProvider(entity.Prop)` 调用代码，不经反射 |
| **查询缓存** | `WithCache(key, TTL)` | `BoundedQueryCache` 有界 LRU（默认 1024 条）+ TTL 过期。命中返回快照副本隔离，ConcurrentDictionary 无锁读。可通过 `DbOptions.QueryCache` 注入自定义 `IQueryCache` 实现 |
| **读写分离** | `ForRead()` / `ForWrite()` | 配置 `ReadConnectionString` 后生效。读路由在执行时打开独立连接并自动释放，写路由和事务强制走主库 |
| **直接 SQL** | `QueryAsync<T>(sql)` / `ExecuteAsync(sql)` | 绕过 QueryBuilder，直接传 `FormattableString`。RowFactory 物化、参数绑定仍走编译时生成 |
| **连接池** | `DbOptions.WithPool()` | PG/MySQL 生效（配置同名 pool 参数），SQLite 不支持池配置时显式拒绝 |
| **PostgreSQL NOTIFY** | `PgNotificationListener` | 异步通知监听：瞬态断线自动重连 + 重新 LISTEN，半开探针验证，订阅者异常隔离。`NotifyAsync()` 参数化发送 |

### PostgreSQL 专有

| 特性 | 说明 |
|------|------|
| NOTIFY/LISTEN | `PgNotificationListener` — 异步通知监听，自动重连 + 重试 LISTEN + 半开探针 + 订阅者异常隔离 |
| JSONB 查询 | `WhereJson("column", "jsonpath", value)` |
| Binary COPY | `NpgsqlBinaryImporter` — 零往返批量写入 |

---

## 使用示例

### 批量操作

```csharp
// 批量插入
await db.BulkInsertAsync(tenThousandUsers);

// 批量更新
await db.BulkUpdateAsync(modifiedUsers);

// 批量删除
await db.BulkDeleteAsync<User>(new object[] { 1L, 2L, 3L });
```

### 多结果集（GridReader）

```csharp
await using var grid = await db.From<Order>().QueryMultipleAsync(
    $"SELECT * FROM orders WHERE id = {orderId}; " +
    $"SELECT * FROM order_items WHERE order_id = {orderId}");
var order = await grid.ReadAsync<Order>();
var items = await grid.ReadItemsAsync<OrderItem>();
```

### Keyset 游标分页

```csharp
var (rows, total) = await db.From<Order>()
    .OrderBy(o => o.CreatedAt, descending: true)
    .ToPageAsync(pageSize: 20, o => o.CreatedAt);

// 续页
var (next, _) = await db.From<Order>()
    .OrderBy(o => o.CreatedAt, descending: true)
    .ToPageAsync(20, o => o.CreatedAt, rows[^1].CreatedAt.Ticks);
```

### OwnedJson（编译时安全）

```csharp
[JsonSerializable(typeof(ProductDetails))]
internal sealed partial class ProductCtx : JsonSerializerContext;

[Table("products")]
public partial class Product
{
    [Key] public long Id { get; set; }
    [OwnedJson(typeof(ProductCtx))]
    [Column("details")]
    public ProductDetails? Details { get; set; }
}

var product = await db.InsertAsync(
    new Product { Name = "Widget", Details = new ProductDetails { Sku = "W-001" } });
```

### 自定义值转换器

```csharp
public sealed class UlidConverter : IValueConverter<Ulid, string>
{
    public string ToProvider(Ulid m) => m.ToString();
    public Ulid FromProvider(string p) => Ulid.Parse(p);
}

[Table("docs")]
public partial class Document
{
    [Key] [Converter(typeof(UlidConverter))] public Ulid Id { get; set; }
}
```

### 存储过程

```csharp
var result = await db.StoredProc("GetUsersByAge")
    .WithParam("minAge", 18)
    .WithOutputParam<int>("total")
    .QueryAsync<User>();

// 读取输出参数需先执行
var proc = db.StoredProc("GetUsersByAge")
    .WithParam("minAge", 18)
    .WithOutputParam<int>("total");
await proc.ExecuteAsync();
int total = proc.GetOutputValue<int>("total");
```

### 聚合查询

```csharp
long total = await db.CountAsync<Order>();
decimal revenue = await db.SumAsync<Order>($"total");
double avgPrice = await db.AvgAsync<Product>($"price");
DateTime? latest = await db.MaxAsync<Order, DateTime>($"created_at");
```

### 原生 SQL

```csharp
var users = await db.QueryAsync<User>($"SELECT * FROM users WHERE age > {18}");
var count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM users");
await db.ExecuteAsync($"UPDATE users SET status = {"active"} WHERE id = {1}");
```

### 编译时 SQL 文件嵌入

```csharp
public partial class Queries
{
    [SqlFile("Queries/GetUsers.sql")]
    public static partial string GetUsers();
}
// → SELECT * FROM users WHERE active = 1
```

```sql
-- Queries/Stats.sql
-- @pg    SELECT current_database()
-- @mysql SELECT DATABASE()
-- @sqlite SELECT 'sqlite'
-- @all   FROM dual
```

```csharp
[SqlFile("Queries/Stats.sql", Provider = "pg")]
public static partial string PgStats();
// → SELECT current_database() FROM dual
```

### CTE 与窗口函数

```csharp
var result = await db.From<Product>()
    .With("top", $"SELECT * FROM products WHERE price > {100m}")
    .ToListAsync();

var ranked = await db.From<Product>()
    .UnsafeWindowOver("ROW_NUMBER()", "ORDER BY price DESC")
    .ToListAsync();
```

### WHERE IN

```csharp
var orders = await db.From<Order>()
    .WhereIn(o => o.Status, new[] { "pending", "shipped", "delivered" })
    .ToListAsync();

var active = await db.From<User>()
    .WhereNotIn(u => u.Role, bannedRoles)
    .ToListAsync();
```

### 软删除 + 多租户

```csharp
[Table("orders")]
[TenantAware]
[SoftDelete]
public partial class Order
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public string TenantId { get; set; } = "";
    [Column("deleted_at")] public DateTime? DeletedAt { get; set; }
}

db.WithTenant("tenant-42");
var orders = await db.From<Order>().ToListAsync();  // 自动过滤 tenant_id + deleted_at
var all = await db.IgnoreFilters().From<Order>().ToListAsync();  // 跳过过滤
```

### 乐观锁

```csharp
public class User
{
    [ConcurrencyCheck]
    [Column("version")]
    public long Version { get; set; }
}

user.Name = "Updated";
await db.UpdateAsync(user);
// UPDATE users SET ... WHERE id = @id AND version = @oldVersion
// SET version = version + 1
// → 不匹配抛 ConcurrencyConflictException
```

### PostgreSQL NOTIFY

```csharp
await using var listener = new PgNotificationListener(cs, "events");
listener.OnNotification += (_, args) => Console.WriteLine(
    $"{args.Channel}: {args.Payload}");
await listener.StartAsync();

await PgNotificationListener.NotifyAsync(cs, "events", "order-created");
```

---

## Native AOT

```bash
dotnet publish test/PalORM.AotTest -c Release -r win-x64 \
  --self-contained -p:PublishAot=true -p:PublishTrimmed=true \
  -p:JsonSerializerIsReflectionEnabledByDefault=false \
  -o artifacts/aot
./artifacts/aot/PalORM.AotTest.exe
```

| Provider | 验证状态 | IL 抑制 |
|----------|---------|:--:|
| SQLite | 本机原生运行通过 | 0 |
| PostgreSQL | 原生运行通过（Docker） | 0 |
| MySQL | 原生运行通过（Docker） | 0 |

AOT 部署前提：
- 实体类为 `partial class`
- OwnedJson 需提供 `JsonSerializerContext`
- 禁止运行时反射、`Expression.Compile()`、`Activator.CreateInstance`
- `FormattableString` 重载可用，`string` 裸 SQL 重载 AOT 下不可用

详见 [AOT 部署指南](docs/AOT部署指南.md)。

---

## 性能数据

v4.6 基准测试（BenchmarkDotNet v0.14.0 · SQLite 内存模式 · 10K 行种子 · `launchCount=3, warmupCount=5, iterationCount=10`）：

> 数据来自独立基准运行。BulkInsert 分配已优于 Dapper 62%。完整报告见 [BENCHMARKS.md](bench/PalORM.Benchmarks/BENCHMARKS.md)。

### CRUD

| 操作 | PalORM | Dapper | RepoDb | PalORM vs Dapper |
|------|-------:|-------:|-------:|:----:|
| QueryAll 10K | 4.85ms / 1.47MB | 3.99ms / 1.32MB | 3.90ms / 1.09MB | +22% / +11% |
| GetByKey | 26.47μs / 3.98KB | 22.43μs / 2.34KB | 26.63μs / 5.38KB | +18% / +70% |
| Insert | 33.93μs / 4.97KB | 25.22μs / 3.73KB | 25.38μs / 3.86KB | +35% / +33% |
| Update | 27.78μs / 7.44KB | 20.45μs / 2.33KB | — | +36% / +219% |
| Upsert | 30.33μs / 4.77KB | 22.82μs / 2.89KB | — | +33% / +65% |
| Delete (Soft) | 36.65μs / 5.63KB | 30.82μs / 4.66KB | — | +19% / +21% |

### 批量

| 操作 | PalORM | Dapper | 对比 |
|------|-------:|-------:|:---:|
| BulkInsert 10K | 55.86ms / **4.97MB** | 34.32ms / 12.97MB | **分配 -62%** |
| BulkUpdate 1K | 3.13ms / 1.61MB | — | — |
| BulkDelete 500 | 4.97ms / 0.81MB | — | — |

### BulkInsert 拐点扫描

| 行数 | PalORM | Dapper | 对比 |
|-----:|-------:|-------:|:---:|
| 100 | 649μs / 140KB | 534μs / 133KB | 1.22× |
| 1,000 | 6.8ms / 1.09MB | 3.6ms / 1.28MB | 1.89× |
| 10,000 | 55.3ms / 10.2MB | 36.1ms / 12.8MB | 1.53× |
| 100,000 | 532ms / 101.6MB | 247ms / 124.3MB | 2.15× |

> PalORM 在所有数据规模上分配低于 Dapper（-15% ~ -21%），10K 行场景分配低 62%。完整报告见 [BENCHMARKS.md](bench/PalORM.Benchmarks/BENCHMARKS.md)。

---

## 运行测试

测试使用 TUnit（Microsoft.Testing.Platform 模式）。`dotnet test` 静默零输出，必须用 `dotnet run`：

```bash
dotnet run --project test/PalORM.Core.Tests           # 161 用例
dotnet run --project test/PalORM.SourceGen.Tests      # 104 用例 + 快照基线
dotnet run --project test/PalORM.Integration.Tests -- \
  --treenode-filter "/*/*/*/*[Category!=ExternalDatabase]"   # 160 用例（本地）
```

全仓库 **424 项**测试。外部 DB 依赖测试（PG/MySQL）标注 `Category=ExternalDatabase`，不计入 badge 总数。CI 请校验输出含 `Test run summary` 行，无摘要视为未运行。

### 测试环境配置

连接串通过双层覆盖管理：

| 文件 | 跟踪 | 用途 |
|------|:---:|------|
| `appsettings.test.json` | ✅ | 结构化模板（`${VAR}` 占位符） |
| `.env.test.example` | ✅ | 凭据示例 |
| `.env.test` | ❌ | 本地凭据（从 .example 复制后填入） |

优先级：`PALORM_*_CONNECTION` 整串环境变量 > `appsettings.test.json` 模板 `${VAR}` 替换 > 显式失败（不静默回退 localhost）。

```bash
cp .env.test.example .env.test
# 编辑 .env.test 填入本地 PG/MySQL 凭据
source scripts/set-test-env.sh
dotnet run --project test/PalORM.Integration.Tests
```

CI 通过 secret 注入 `PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION` 即可。

### 性能基准

```bash
dotnet build bench/PalORM.Benchmarks -c Release
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*SqliteBenchmarks*'
```

---

## 文档

| 文档 | 内容 |
|------|------|
| [CHANGELOG.md](CHANGELOG.md) | 版本变更记录（v4.0-v4.6 25+ 项优化） |
| [docs/API参考.md](docs/API参考.md) | API 参考（DataSession / QueryBuilder / StoredProc 等） |
| [docs/架构设计.md](docs/架构设计.md) | 架构设计（源生成器 / 数据流 / 18 项决策） |
| [docs/AOT部署指南.md](docs/AOT部署指南.md) | AOT 发布配置与验证清单 |
| [docs/编码规范.md](docs/编码规范.md) | 167 条 STD 规则 × 17 类 |
| [docs/踩坑目录.md](docs/踩坑目录.md) | 302 项跨语言 ORM 陷阱 |
| [docs/变更日志.md](docs/变更日志.md) | v2.0.1 历史快照 |

---

AGPL v3 · [PalDDD](https://github.com/PalDDD)
