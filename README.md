<h1 align="center">PalORM</h1>
<p align="center"><strong>面向严格 Native AOT 的 .NET 微 ORM</strong></p>
<p align="center">
  <img src="https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet">
  <img src="https://img.shields.io/badge/tests-419%2F419-success">
  <img src="https://img.shields.io/badge/AOT-3%20providers%20verified-success">
  <img src="https://img.shields.io/badge/IL%20suppressions-0-success">
  <img src="https://img.shields.io/badge/license-AGPL%20v3-blue">
</p>

PalORM 通过 Roslyn 源生成器在**编译时**生成数据访问代码。运行时生产路径禁止反射和 IL/AOT 警告抑制。三 Provider（SQLite/PostgreSQL/MySQL）完整 CRUD、OwnedJson、并发、跨程序集与 NuGet consumer 原生运行均已验证——PG/MySQL 经本机 Docker（CI 同配置服务容器）实测，远端 CI 待 push 触发。

> v3.0.0 是 breaking release：移除了 ForRead/ForWrite/CrudMetadata 旧 ctor/ThenInclude 单参重载。

---

## 主要特性

### 核心架构

| 特性 | 说明 |
|------|------|
| **编译时安全** | `FormattableString` 参数化 — 编译时提取参数值，杜绝 SQL 注入 |
| **原子元数据注册** | SourceGen 生成 `RegistryFragment`；运行时一次发布不可变快照，外部只读 |
| **struct QueryBuilder** | 值类型 — `From<T>()` 返回 stack-allocated struct，copy-on-write 保证条件隔离 |
| **真 Native AOT** | 零 IL 抑制 — 三 Provider 原生二进制运行通过（`IsAotCompatible` + `IsTrimmable` + `PublishAot`） |
| **static abstract Provider** | C# 11 `IDbProvider` 接口编译时分发——零虚调用开销，JIT 特化 |
| **最小依赖** | Core 零第三方 NuGet：BCL + ADO.NET + `Microsoft.Extensions.Logging.Abstractions`；可观测性基于 BCL `ActivitySource` / `Meter` |

### 编译时诊断（21 条规则）

| 特性 | 说明 |
|------|------|
| **PALORM001-005** | 实体级诊断：缺少主键 / 列名不匹配 / FK 引用不存在 / FK 缺 OnDelete / N+1 查询检测 |
| **PALORM008-012** | OwnedJson / 表声明 / Schema 限定 / 并发令牌类型 |
| **PALORM013-019** | Schema 列校验 / 软删 / 租户 / 值映射 / 注解不生效 / 索引声明 |
| **PALORM020-022** | 索引重复 / 列名唯一性 / Key 声明合法性（AutoIncrement 类型/init-only/nullable） |

### 查询能力

| 特性 | 说明 |
|------|------|
| **链式 DSL** | Where / OrWhere / OrderBy / ThenBy / Take / Skip / Select / GroupBy / Having |
| **JOIN** | InnerJoin / LeftJoin / RightJoin / Include / ThenInclude（多级导航） |
| **IN 操作** | WhereIn / WhereNotIn（参数上限自动钳制） |
| **CTE** | `With("cte", $"SELECT ...")` 公用表表达式 |
| **窗口函数** | `UnsafeWindowOver("ROW_NUMBER()", "PARTITION BY ...")` |
| **锁** | `ForUpdate()` / `ForShare()`（SELECT ... FOR UPDATE/SHARE） |
| **缓存** | `WithCache("key", TTL)` — 有界 LRU 缓存 + 快照副本隔离 |
| **预编译** | `AsPrepared()` — `DbCommand.PrepareAsync` 预编译参数化命令 |
| **DryRun** | `AsDryRun()` — 生成 SQL + 参数预览不执行 |
| **读写分离** | `ForRead()` 路由到只读副本连接（独立 ConnectionLease） |

### 写入能力

| 特性 | 说明 |
|------|------|
| **CRUD** | Insert / Update / Delete / Save（UPSERT）/ Get / GetAll |
| **批量操作** | BulkInsert / BulkDelete / BulkUpdate / BulkMerge / SeedAsync |
| **PG Binary COPY** | `NpgsqlBinaryImporter` 零往返批量写入 |
| **多值 INSERT** | SQLite/MySQL 共享骨架（参数上限自动钳制：SQLite 999 / MySQL 65535） |
| **UPSERT 单次往返** | PG/SQLite `ON CONFLICT DO UPDATE` / MySQL `ON DUPLICATE KEY UPDATE` |
| **RETURNING** | PG/SQLite `INSERT ... RETURNING` 单次往返物化完整行 |

### 弹性与可靠性

| 特性 | 说明 |
|------|------|
| **重试** | 可配置次数 + 指数退避（100ms→200ms→400ms） |
| **熔断器** | 连续失败≥阈值→快速失败→resetAfter 后半开探针验证（generation 防陈旧） |
| **命令超时** | `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter` |
| **事务编排** | `WithTransaction(async callback)` — 自动 Commit/Rollback + 异常保留语义 |
| **保存点** | `SavepointAsync` / `RollbackToAsync` — 事务内部分回滚 |
| **异常保留** | cleanup 失败挂 `Exception.Data` 不替换原始失败（审计可追溯） |

### 横切关注点

| 特性 | 说明 |
|------|------|
| **软删除** | `[SoftDelete]` + `deleted_at` 列——查询自动过滤，`IgnoreFilters()` 可显式包含 |
| **多租户** | `[TenantAware]` + `tenant_id` 列——查询自动隔离，`WithTenant(id)` 切换 |
| **乐观锁** | `[ConcurrencyCheck]` + `Version` 字段——Update 自动检查 + 递增版本号 |
| **拦截器** | `IQueryInterceptor` — OnBefore/OnAfter/OnError 三阶段 + 优先级排序 |
| **可观测性** | `WithTracing()` / `WithMetrics()` — BCL `ActivitySource` + `Meter`（零第三方） |
| **编译时 SQL** | `[SqlFile("path.sql")]` — 编译时读取 .sql 文件嵌入为 `const string` |
| **SQL 模板** | `[SqlTemplate("name")]` — 提取方法内的 `FormattableString` 为静态常量 |

### PostgreSQL 专有

| 特性 | 说明 |
|------|------|
| **NOTIFY/LISTEN** | 异步通知监听——自动重连 + 重试 LISTEN + 半开探针 + 订阅者异常隔离 |
| **JSONB 查询** | `WhereJson("column", "jsonpath", value)` — JSONB 路径条件 |
| **Binary COPY** | 零往返批量写入——`StartRowAsync` + `WriteAsync(value, NpgsqlDbType)` |

---

## 使用示例

### 基础 CRUD

```csharp
[Table("users")]
public partial class User
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("email")] public string? Email { get; set; }
}

var db = await DataSession<SqliteProvider>.CreateAsync(
    new DbOptions { ConnectionString = "Data Source=app.db" });
await db.MigrateAsync();

// 插入 — 返回带自增 ID 的实体
var user = await db.InsertAsync(new User { Name = "Alice", Email = "a@example.com" });

// 按主键查询
var found = await db.GetAsync<User>(user.Id);

// 条件查询
var users = await db.From<User>()
    .Where($"name = {"Alice"}")
    .OrderBy(u => u.Id, descending: true)
    .Take(10)
    .ToListAsync();

// 更新
user.Name = "Bob";
await db.UpdateAsync(user);

// 删除
await db.DeleteAsync<User>(user.Id);
```

### UPSERT — 单次往返

```csharp
// 自动 INSERT ON CONFLICT DO UPDATE（PG/SQLite）
// 或 ON DUPLICATE KEY UPDATE（MySQL）
await db.SaveAsync(new User { Id = 1, Name = "Updated" });
```

### 批量操作

```csharp
// 批量插入 — 三 Provider 统一使用源生成 InsertColumns/BindInsert
await db.BulkInsertAsync(tenThousandUsers);

// 批量删除 — SoftDelete 实体更新 deleted_at，其他实体物理删除
await db.BulkDeleteAsync<User>(new object[] { 1L, 2L, 3L });

// 批量更新
await db.BulkUpdateAsync(modifiedUsers);
```

### 编译时 SQL 文件嵌入

```csharp
// 源生成器编译时读取 .sql 文件，嵌入为常量
public partial class Queries
{
    [SqlFile("Queries/GetUsers.sql")]
    public static partial string GetUsers();
}
// → SELECT * FROM users WHERE active = 1
```

### Provider 条件分支

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
// CTE
var result = await db.From<Product>()
    .With("top", $"SELECT * FROM products WHERE price > {100m}")
    .ToListAsync();

// 窗口函数：参数是原始 SQL 结构，只能使用可信常量
var ranked = await db.From<Product>()
    .UnsafeWindowOver("ROW_NUMBER()", "ORDER BY price DESC")
    .ToListAsync();
```

### 关联查询与拆分

```csharp
// Include — 编译时 JOIN
var orders = await db.From<Order>()
    .Include<Customer>(o => o.CustomerId, c => c.Id)
    .Where($"status = {"pending"}")
    .ToListAsync();

// SplitQuery 当前只构建根查询并移除 JOIN，不装配导航对象。
var rootOrders = await db.From<Order>()
    .Include<Customer>(o => o.CustomerId, c => c.Id)
    .AsSplitQuery()
    .ToListAsync();

// ThenInclude — 双参数显式表达 JOIN 两端
var sql = db.From<Order>()
    .ThenInclude<Customer, Order>(c => c.Id, o => o.CustomerId)
    .ToSql();
```

### 读写分离

```csharp
var options = new DbOptions
{
    ConnectionString = "Host=primary;...",
    ReadConnectionString = "Host=replica;..."
};

// 强制走读副本（QueryBuilder 级路由——执行时打开独立连接并自动释放）
var users = await db.From<User>().ForRead().ToListAsync();

// 写后立即读 — 强制走主库
var user = await db.From<User>().ForWrite().Where($"id = {1}").FirstAsync();
```

### 事务

```csharp
// 函数式 — 自动 commit/rollback；callback 内按顺序使用同一会话
await db.WithTransaction(async ct =>
{
    await db.InsertAsync(new User { Name = "A" }, ct);
    await db.ExecuteAsync($"UPDATE users SET name = {"B"} WHERE id = {1}", ct);
});
// 同一 DataSession 不支持重叠数据库操作、嵌套事务或 callback 内派生并发任务。

// 显式 — 手动控制
using var tran = await db.BeginTransactionAsync();
db.UseTransaction(tran);
await db.BulkInsertAsync(users);
await tran.CommitAsync();
```

### 直查与存储过程

```csharp
// 直查 — 绕过 QueryBuilder
var users = await db.QueryAsync<User>($"SELECT * FROM users WHERE age > {18}");
var count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM users");

// 存储过程
var result = await db.StoredProc("GetUsersByAge")
    .WithParam("@minAge", 18)
    .WithOutputParam<int>("@total")
    .QueryAsync<User>();

int total = db.StoredProc("GetUsersByAge")
    .WithParam("@minAge", 18)
    .WithOutputParam<int>("@total")
    .GetOutputValue<int>("@total");
```

### 弹性与可观测性

```csharp
var db = await DataSession<SqliteProvider>.CreateAsync(options);

// 重试 + 熔断：同一 DataSession 持久保存连续最终失败状态
// MaxRetries=3 表示首次尝试之外最多重试 3 次
// 仅重试 Provider 判定的瞬时数据库异常和内部命令超时
db.WithRetry(maxRetries: 3)
  .WithCircuitBreaker(failureThreshold: 5, resetAfter: TimeSpan.FromSeconds(30));

// AOT 安全 Activity + Meter；不记录 SQL、参数、连接串或调用方路径
await db.From<User>()
    .WithTracing()
    .WithMetrics("get-active-users") // 名称不进入指标标签
    .Where($"status = {"active"}")
    .ToListAsync();

// 参数绑定后调用 Provider 的 DbCommand.PrepareAsync
await db.From<User>()
    .Where($"status = {"active"}")
    .AsPrepared()
    .ToListAsync();

// 查询缓存
await db.From<User>()
    .WithCache("all-users", TimeSpan.FromMinutes(5))
    .ToListAsync();
```

### 多租户与软删除

```csharp
[Table("orders")]
[TenantAware]   // 自动附加 WHERE tenant_id = @current
[SoftDelete]    // 自动附加 WHERE deleted_at IS NULL
public partial class Order
{
    [Key] public long Id { get; set; }
    [Column("status")] public string Status { get; set; } = "";
    [Column("deleted_at")] public DateTime? DeletedAt { get; set; }
}

// 设置当前租户 — 后续查询自动过滤
db.WithTenant(tenantId);

// IgnoreFilters 是当前 DataSession 的持久开关，后续 ORM 查询均忽略默认过滤器。
// 需要隔离作用域时创建独立 DataSession。
var allOrders = await db.IgnoreFilters().From<Order>().ToListAsync();
```

### PostgreSQL NOTIFY

```csharp
// 监听：瞬态断线后创建新连接并重新执行全部 LISTEN
await using var listener = new PgNotificationListener(cs, "events");
listener.OnNotification += (_, args) => Console.WriteLine($"{args.Channel}: {args.Payload}");
listener.OnError += (_, args) => Console.Error.WriteLine(args.Exception.GetType().Name);
await listener.StartAsync();

// 发送（参数化，零 SQL 注入）；payload 可为 null
await PgNotificationListener.NotifyAsync(cs, "events", "order-created");
```

### 聚合查询

```csharp
// 所有聚合自动附加软删除过滤（WHERE deleted_at IS NULL）
long total = await db.CountAsync<Order>();
decimal revenue = await db.SumAsync<Order>($"total");
DateTime? latest = await db.MaxAsync<Order, DateTime>($"created_at");
double avgPrice = await db.AvgAsync<Product>($"price");
```

### 乐观锁（并发控制）

```csharp
[ConcurrencyCheck]
[Column("version")]
public long Version { get; set; }

// UpdateAsync 自动检查 version 匹配——不匹配抛 ConcurrencyConflictException
user.Name = "Updated";
await db.UpdateAsync(user);  // WHERE id = @id AND version = @oldVersion
                             // SET version = version + 1
```

### WhereIn / WhereNotIn

```csharp
// 自动按参数上限分批（SQLite 999 / MySQL 65535）
var statuses = new[] { "pending", "shipped", "delivered" };
var orders = await db.From<Order>()
    .WhereIn(o => o.Status, statuses)
    .ToListAsync();

// 排除
var active = await db.From<User>()
    .WhereNotIn(u => u.Role, bannedRoles)
    .ToListAsync();
```

### Keyset 游标分页

```csharp
// 第一页
var (rows, total) = await db.From<Order>()
    .OrderBy(o => o.CreatedAt, descending: true)
    .ToPageAsync(pageSize: 20, o => o.CreatedAt);

// 续页（传入上一页最后一行的排序值作为游标）
long? lastCursor = rows[^1].CreatedAt.Ticks;
var (next, _) = await db.From<Order>()
    .OrderBy(o => o.CreatedAt, descending: true)
    .ToPageAsync(20, o => o.CreatedAt, lastCursor);
```

### 自定义值转换器

```csharp
// Ulid ↔ string 的自定义转换（AOT 安全——编译时生成调用代码）
public sealed class UlidStringConverter : IValueConverter<Ulid, string>
{
    public string ToProvider(Ulid model) => model.ToString();
    public Ulid FromProvider(string provider) => Ulid.Parse(provider);
}

[Table("documents")]
public partial class Document
{
    [Key] [Converter(typeof(UlidStringConverter))]
    public Ulid Id { get; set; }
    [Column("title")] public string Title { get; set; } = "";
}
```

### OwnedJson（编译时安全 JSON 序列化）

```csharp
// 需要源生成的 JsonSerializerContext（AOT 安全）
[JsonSerializable(typeof(ProductDetails))]
internal sealed partial class ProductJsonContext : JsonSerializerContext;

[Table("products")]
public partial class Product
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [OwnedJson(typeof(ProductJsonContext))]
    [Column("details")]
    public ProductDetails? Details { get; set; }
}

// 读写自动 JSON 序列化/反序列化（零运行时反射）
var product = await db.InsertAsync(new Product
{
    Name = "Widget",
    Details = new ProductDetails { Sku = "W-001", Weight = 1.5m }
});
```

---

## AOT 原生编译

```bash
dotnet publish test/PalORM.AotTest -c Release -r win-x64 \
  --self-contained true -p:PublishAot=true -p:PublishTrimmed=true \
  -p:JsonSerializerIsReflectionEnabledByDefault=false -o artifacts/aot/sqlite
./artifacts/aot/sqlite/PalORM.AotTest.exe
```

| Provider | 验证状态 | IL 抑制 |
|----------|----------|:--:|
| SQLite | 本机原生运行通过 | 0 |
| PostgreSQL | 原生运行通过（本机 Docker CI 同配置容器） | 0 |
| MySQL | 原生运行通过（本机 Docker CI 同配置容器） | 0 |

---

## .NET 11 性能特性

PalORM 已为 .NET 11 runtime-async 优化，并在 `Directory.Build.props` 中启用：

```xml
<PropertyGroup>
  <!-- .NET 11 runtime-async：运行时原生异步，替换编译器状态机 -->
  <Features>runtime-async=on</Features>
</PropertyGroup>
```

**被动受益**（无代码改动，preview 6 SDK 自动生效）：

- **Runtime-async + ExecutionContext 空捕获跳过**——PalORM `AsyncLocal<T>` 仅 2 个（操作/事务 owner），绝大多数 `await` 路径无环境状态可恢复，跳过捕获直接受益；`ConfigureAwait(false)` 全库统一协同最大化
- **NativeAOT 接口派发加速**——热路径 `IRowFactory<T>.Read`（每行调用）+ `IDbProvider` 静态抽象 + `IQueryInterceptor` 链直接受益；preview 6 共享派发 helper 减小二进制体积
- **JIT 边界检查消除 + `SequenceEqual` 常量折叠**——`ValueStringBuilder` Span 操作与 `EquatableArray.Equals` 受益
- **R2R 对 `EqualityComparer<T>.Default` 专门化**——`QueryBuilderExtensions.ToPageAsync` 续页检查受益（官方称最高提速 20×）

**可观测性**（BoundedQueryCache 暴露 OTel 指标，对齐 .NET 11 MemoryCache 标准口径）：

- `palorm.cache.requests{outcome=hit|miss}`
- `palorm.cache.evictions`
- `palorm.cache.entries` (ObservableGauge，Pull 模式)
- `palorm.cache.estimated_size` (ObservableGauge，以条目数近似)

通过 `PalORM` Meter 上游 OTLP 导出即可观测（无需额外适配器包）。如需按规则启停 `Activity` 跟踪，使用 .NET 11 `AddTracing`：

```csharp
builder.Services.AddTracing(tracing =>
{
    tracing.EnableTracing(sourceName: "PalORM");           // PalORM.ActivitySource
    tracing.DisableTracing(sourceName: "PalORM", operationName: "HealthCheck");
});
```

---

## 运行测试

测试项目使用 TUnit（Microsoft.Testing.Platform 模式）：**`dotnet test` 会静默零输出**（MTP 与经典 test 管道不桥接），必须用 `dotnet run`：

```bash
dotnet run --project test/PalORM.Core.Tests            # 156 用例
dotnet run --project test/PalORM.SourceGen.Tests       # 生成器/分析器
dotnet run --project test/PalORM.Integration.Tests -- \
  --treenode-filter "/*/*/*/*[Category!=ExternalDatabase]"   # 本地（无 MySQL/PG 服务）
```

CI 中请校验输出含 `Test run summary` 行——无摘要即视为未运行，不是通过。

---

## 测试配置（连接串）

测试 / 工具链的连接串由**双层覆盖**机制管理（生产库 PalORM.Core 不引入配置依赖）：

| 文件 | 跟踪 | 用途 |
|------|------|------|
| `appsettings.test.json` | ✅ git 跟踪 | 结构化模板（端口/超时/host 占位符） |
| `.env.test.example` | ✅ git 跟踪 | 凭据示例 |
| `.env.test` | ❌ gitignored | 本地凭据（从 .example 复制后填入） |

**优先级**：`PALORM_*_CONNECTION` 整串环境变量 > `appsettings.test.json` 模板 `${VAR}` 占位符替换 > 显式失败（不静默回退 localhost，避免误写系统库——ITM-428 凭据卫生）。

首次使用：

```bash
cp .env.test.example .env.test
# 编辑 .env.test 填入本地 PG/MySQL 凭据
source scripts/set-test-env.sh
dotnet run --project test/PalORM.Integration.Tests
```

CI 通过 secret 注入 `PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION` 即可，无需 .env.test 文件。

`PalORM.Testing.TestEnvironment` 从 `AppContext.BaseDirectory` 向上回溯查找 `appsettings.test.json`，集成测试项目 csproj 已配置 `<CopyToOutputDirectory>` 自动复制。

---

## 文档

| | |
|---|---|
| [API 参考](docs/API参考.md) | 100+ 项 QueryBuilder/DataSession/StoredProc API 清单 |
| [架构设计](docs/架构设计.md) | 源生成器 · 数据流 · 17 项决策 |
| [踩坑目录](docs/踩坑目录.md) | 302 项跨语言 ORM 陷阱 |
| [AOT 部署指南](docs/AOT部署指南.md) | 发布配置与验证 |
| [编码规范](docs/编码规范.md) | 167 条 STD 规则 × 17 类 |
| [变更日志](docs/变更日志.md) | 版本交付记录 |

---

AGPL v3
