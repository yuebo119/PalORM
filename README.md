<h1 align="center">PalORM</h1>
<p align="center"><strong>面向严格 Native AOT 的 .NET 微 ORM</strong></p>
<p align="center">
  <img src="https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet">
  <img src="https://img.shields.io/badge/tests-361%2F361-success">
  <img src="https://img.shields.io/badge/AOT-3%20providers%20verified-success">
  <img src="https://img.shields.io/badge/IL%20suppressions-0-success">
  <img src="https://img.shields.io/badge/license-AGPL%20v3-blue">
</p>

PalORM 通过 Roslyn 源生成器在**编译时**生成数据访问代码。运行时生产路径禁止反射和 IL/AOT 警告抑制。三 Provider（SQLite/PostgreSQL/MySQL）完整 CRUD、OwnedJson、并发、跨程序集与 NuGet consumer 原生运行均已验证——PG/MySQL 经本机 Docker（CI 同配置服务容器）实测，远端 CI 待 push 触发。

> 2.0.0 收紧了 `PalORM_Runtime` 注册 API，属于 binary-breaking 变更；从 1.x 升级的已编译消费者必须重新编译。

---

## 主要特性

| 特性 | 说明 |
|------|------|
| **编译时安全** | `FormattableString` 参数化 — 编译时提取参数值，杜绝 SQL 注入 |
| **原子元数据注册** | SourceGen 生成 `RegistryFragment`；运行时一次发布不可变快照，外部只读 |
| **struct QueryBuilder** | 值类型 — `From<T>()` 返回 stack-allocated struct |
| **真 AOT** | 零 IL 抑制 — 三 Provider 原生运行通过（SQLite 本机 + PG/MySQL 本机 Docker CI 同配置容器实测） |
| **Provider 原生优化** | PG Binary COPY · MySQL/SQLite batched INSERT |
| **UPSERT 单次往返** | `INSERT ON CONFLICT DO UPDATE` / `ON DUPLICATE KEY UPDATE` |
| **最小依赖** | Core 零第三方 NuGet 依赖：BCL + ADO.NET + 共享框架日志抽象（`Microsoft.Extensions.Logging.Abstractions`）；可观测性基于 BCL `ActivitySource` / `Meter` |

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
// 需要关联对象时应执行显式查询并在应用层组合。
var rootOrders = await db.From<Order>()
    .Include<Customer>(o => o.CustomerId, c => c.Id)
    .AsSplitQuery()
    .ToListAsync();

// ThenInclude 必须显式提供 JOIN 两端，单参数旧重载会明确失败。
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

// 强制走读副本
var users = await db.From<User>().ForRead().ToListAsync();

// 写后立即读 — 强制走主库
var user = await db.From<User>().ForWrite().Where($"id = {1}").FirstAsync();

// 会话级 ForRead/ForWrite 已弃用：ForRead 返回独立会话并要求调用方释放。
// 新代码统一使用上面的 QueryBuilder 路由。
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

## 运行测试

测试项目使用 TUnit（Microsoft.Testing.Platform 模式）：**`dotnet test` 会静默零输出**（MTP 与经典 test 管道不桥接），必须用 `dotnet run`：

```bash
dotnet run --project test/PalORM.Core.Tests            # 137 用例
dotnet run --project test/PalORM.SourceGen.Tests       # 生成器/分析器
dotnet run --project test/PalORM.Integration.Tests -- \
  --treenode-filter "/*/*/*/*[Category!=ExternalDatabase]"   # 本地（无 MySQL/PG 服务）
```

CI 中请校验输出含 `Test run summary` 行——无摘要即视为未运行，不是通过。

---

## 文档

| | |
|---|---|
| [API 参考](docs/API参考.md) | 113 项清单：112 项实现，1 项移除 |
| [架构设计](docs/架构设计.md) | 源生成器 · 数据流 · 17 项决策 |
| [踩坑目录](docs/踩坑目录.md) | 302 项跨语言 ORM 陷阱 |
| [AOT 部署指南](docs/AOT部署指南.md) | 发布配置与验证 |
| [编码规范](docs/编码规范.md) | 167 条 STD 规则 × 17 类 |
| [变更日志](docs/变更日志.md) | 版本交付记录 |

---

AGPL v3
