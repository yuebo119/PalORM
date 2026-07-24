<h1 align="center">PalORM</h1>
<p align="center"><strong>面向 Native AOT 的 .NET 11 微 ORM</strong></p>
<p align="center">
  <img src="https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet">
  <img src="https://img.shields.io/badge/tests-425%2F425-success">
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

### 编译时类型安全与 AOT 全链路

| 特性 | 说明 |
|------|------|
| `FormattableString` 参数化 | SQL 使用 `$"WHERE id = {value}"` 插值语法，编译时提取参数值绑定到 `DbParameter`，从架构层面杜绝 SQL 注入 |
| 源生成 RowFactory | 每个实体编译时生成 `Func<DbDataReader, T>` 委托，零反射物化 |
| 源生成 CommandFactory | INSERT/UPDATE/UPSERT/DELETE SQL 与参数绑定均在编译时完成 |
| 源生成 Migration | CREATE TABLE DDL 按三方言分别生成 |
| 原子元数据注册 | ModuleInitializer 启动时一次性注册所有实体的元数据，发布不可变快照 |
| Native AOT 全链路 | `IsAotCompatible=true` + `IsTrimmable=true`，三 Provider 原生二进制运行通过，**零 IL 抑制** |

### 编译时诊断（21 条规则）

| 规则 | 说明 |
|------|------|
| PALORM001 | [Table] 实体缺少 [Key] |
| PALORM002 | 属性未标注 [Column] |
| PALORM003 | [ForeignKey] 引用表不存在 |
| PALORM004 | [ForeignKey] 缺少 OnDelete |
| PALORM005 | N+1 查询检测 |
| PALORM008-010 | OwnedJson 上下文校验 |
| PALORM011 | 拒绝限定表名（Database.Schema.Table） |
| PALORM012 | 并发令牌类型约束 |
| PALORM014 | [SoftDelete] 需 `deleted_at` 列 |
| PALORM016 | 未知类型映射 |
| PALORM017 | 注解声明但不参与 DDL |
| PALORM018 | [TenantAware] 需 `tenant_id` 列 |
| PALORM019-022 | OwnedJson / Key 合法性校验 |

### 查询构建器与注解（22 个）

`[Table]` `[Column]` `[Key]` `[NotMapped]` `[ForeignKey]` `[ConcurrencyCheck]` `[IgnoreOnInsert]` `[Computed]` `[DefaultValue]` `[SensitiveData]` `[Converter]` `[SoftDelete]` `[TenantAware]` `[OwnedJson]` `[Index]` `[Unique]` `[SqlFile]` `[SqlTemplate]`

### 查询能力

| API | 说明 |
|-----|------|
| `From<T>()` | 返回 `struct QueryBuilder<T>`（值类型，零堆分配） |
| `.Where(FormattableString)` / `.OrWhere()` | 参数化条件，用户 OR 无法绕过默认过滤 |
| `.OrderBy(expr)` / `.ThenBy()` / `.Take()` / `.Skip()` | 排序与分页 |
| `.Select(expr[])` | 列投影（DryRun/ToSql 用） |
| `.InnerJoin<T>()` / `.LeftJoin()` / `.RightJoin()` | SQL JOIN |
| `.Include<TChild>(fk, pk)` / `.ThenInclude()` | 多级导航，双参数显式表达 JOIN 两端 |
| `.WhereIn(expr, values)` / `.WhereNotIn()` | 自动按参数上限分批 |
| `.GroupBy(expr)` / `.Having(FormattableString)` | 聚合条件 |
| `.With("cte", subquery)` | 公用表表达式 |
| `.UnsafeWindowOver(func, over)` | 窗口函数（仅可信常量） |
| `.ForUpdate()` / `.ForShare()` | 悲观锁 |
| `.AsSplitQuery()` | 仅构建根查询，不装配导航对象 |
| `.ForRead()` / `.ForWrite()` | 读写路由意图 |
| `.WithCache(key, TTL)` | 有界 LRU 查询缓存 + 快照副本隔离 |
| `.AsPrepared()` | 参数绑定后调用 `DbCommand.PrepareAsync` |
| `.AsDryRun()` -> `DryRunResult` | SQL + 参数预览，不执行 |
| `.Tag("name")` / `.TagWithCaller()` | SQL 注释标签 |
| `.WithTracing()` / `.WithMetrics()` | BCL ActivitySource + Meter，AOT 安全 |
| `.ToPageAsync(size, orderBy, cursor?)` | Keyset 游标分页 |

### 写入能力

| API | 说明 |
|-----|------|
| `InsertAsync(T)` -> T | PG/SQLite 走 RETURNING，MySQL 走 LAST_INSERT_ID |
| `UpdateAsync(T)` | 自动乐观锁检查（ConcurrencyCheck 列） |
| `DeleteAsync<T>(key)` | SoftDelete 实体更新 deleted_at，其他物理删除 |
| `SaveAsync(T)` -> T | UPSERT：默认键走 Insert，非默认键走 `ON CONFLICT DO UPDATE` |
| `GetAsync<T>(key)` | 按主键查询 |
| `GetAllAsync<T>()` | 全表查询 |
| `BulkInsertAsync(items)` | PG Binary COPY / SQLite+MySQL 多值 INSERT（参数上限自动钳制） |
| `BulkUpdateAsync(items)` | 单事务批量更新 |
| `BulkDeleteAsync<T>(keys)` | 500/批 IN 子句 |
| `BulkMergeAsync(items)` | 逐项 UPSERT |
| `SeedAsync(items)` | 非默认稳定主键，事务内 Upsert 幂等 |

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

| 特性 | 说明 |
|------|------|
| 重试 | `WithRetry(maxRetries: 3)` — 指数退避（100→200→400ms），仅重试瞬时数据库故障和内部命令超时 |
| 熔断器 | `WithCircuitBreaker(threshold: 5, resetAfter: 30s)` — 连续失败 N 次后快速失败，半开探针验证恢复，generation 防陈旧 |
| 超时 | 每命令通过 `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter` 控制 |
| 异常保留 | cleanup 失败挂 `Exception.Data`，不替换原始失败，审计可追溯 |

### 横切关注点

| 特性 | 注解/API | 说明 |
|------|---------|------|
| 软删除 | `[SoftDelete]` + `deleted_at` | 查询自动过滤，`IgnoreFilters()` 显式包含 |
| 多租户 | `[TenantAware]` + `tenant_id` | 查询自动隔离，`WithTenant(id)` 切换 |
| 乐观锁 | `[ConcurrencyCheck]` | Update 自动检查 version 匹配，不匹配抛 `ConcurrencyConflictException` |
| 拦截器 | `IQueryInterceptor` | OnBefore/OnAfter/OnError 三阶段 + 优先级排序 |
| 可观测性 | `WithTracing()` / `WithMetrics()` | BCL ActivitySource + Meter，不记录 SQL/参数/连接串，AOT 安全 |
| SQL 文件 | `[SqlFile("path.sql")]` | 编译时读取 .sql 嵌入为 `const string`，支持 Provider 条件分支 |
| SQL 模板 | `[SqlTemplate("name")]` | 提取 `FormattableString` 为静态常量 |
| 值转换器 | `[Converter(typeof(T))]` | AOT 安全，源生成调用代码 |
| 查询缓存 | `WithCache(key, TTL)` | 有界 LRU + OTel 指标，对齐 .NET 11 MemoryCache 口径 |
| 读写分离 | `ForRead()` / `ForWrite()` | ReadConnectionString 配置后自动路由 |

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

int total = db.StoredProc("GetUsersByAge")
    .WithParam("minAge", 18)
    .WithOutputParam<int>("total")
    .GetOutputValue<int>("total");
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

### v4.6 优化历程

| 版本 | 核心优化 | BulkInsert 分配 |
|------|---------|:---:|
| v4.0 | 基线 | 10.66 MB |
| v4.2 | FormattableSqlFormatter VSB + 参数名缓存 + selectColumns 缓存 | 8.33 MB |
| v4.5 | TCS 延迟创建 + Exit 不写 AsyncLocal（-388B/操作） | 6.73 MB |
| v4.6 | owner=this + SqliteParameter 复用 + HasClause 位掩码 + GetAsync SQL 缓存 | **4.97 MB** |

---

## 运行测试

测试使用 TUnit（Microsoft.Testing.Platform 模式）。`dotnet test` 静默零输出，必须用 `dotnet run`：

```bash
dotnet run --project test/PalORM.Core.Tests           # 161 用例
dotnet run --project test/PalORM.SourceGen.Tests      # 104 用例 + 快照基线
dotnet run --project test/PalORM.Integration.Tests -- \
  --treenode-filter "/*/*/*/*[Category!=ExternalDatabase]"   # 160 用例（本地）
```

全仓库 **425 项**测试（Core 161 + SourceGen 104 + Integration 160）。外部 DB 依赖测试（PG/MySQL）标注 `Category=ExternalDatabase`，不计入 badge 总数。CI 请校验输出含 `Test run summary` 行，无摘要视为未运行。

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
