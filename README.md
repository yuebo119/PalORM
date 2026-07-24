<h1 align="center">PalORM</h1>
<p align="center"><strong>面向 Native AOT 的 .NET 11 微 ORM</strong></p>
<p align="center">
  <img src="https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet">
  <img src="https://img.shields.io/badge/tests-425%2F425-success">
  <img src="https://img.shields.io/badge/AOT-verified-success">
  <img src="https://img.shields.io/badge/IL%20suppressions-0-success">
  <img src="https://img.shields.io/badge/license-AGPL%20v3-blue">
</p>

Roslyn 源生成器在编译时生成数据访问代码。运行时零反射、零 IL/AOT 警告抑制。支持 SQLite、PostgreSQL、MySQL 三 Provider，完整 CRUD、OwnedJson、乐观锁、跨程序集与 NuGet consumer Native AOT 均已验证。

---

## 安装

```xml
<PackageReference Include="PalORM.Core" Version="4.6.0" />
<PackageReference Include="PalORM.Sqlite" Version="4.6.0" />
<PackageReference Include="PalORM.SourceGen" Version="4.6.0" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

---

## 快速开始

```csharp
[Table("users")]
public partial class User
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

var db = await DataSession<SqliteProvider>.CreateAsync(
    DbOptions.Development("Data Source=app.db"));
await db.MigrateAsync();

var user = await db.InsertAsync(new User { Name = "Alice" });
var found = await db.GetAsync<User>(user.Id);
var users = await db.From<User>()
    .Where($"name = {"Alice"}")
    .OrderBy(u => u.Id, descending: true)
    .Take(10)
    .ToListAsync();
```

## 配置

```csharp
// 预设
var dev = DbOptions.Development("Data Source=dev.db");       // 宽松
var prod = DbOptions.Production("$ENV:DATABASE_URL");        // 严格 + 连接池
var test = DbOptions.Testing("Data Source=:memory:");        // 短超时

// 自定义
var options = new DbOptions
{
    ConnectionString = "$ENV:DATABASE_URL",
    CommandTimeout = TimeSpan.FromSeconds(60),
    MaxRetries = 5,
    CircuitBreakerThreshold = 10
}.WithPool(maxSize: 200);

// 环境变量（Docker/K8s）
var env = DbOptions.FromEnvironment("DATABASE_URL");
```

---

## 核心特性

### 架构

| 特性 | 说明 |
|------|------|
| `FormattableString` 参数化 | 编译时提取参数，杜绝 SQL 注入 |
| `struct QueryBuilder<T>` | 值类型，copy-on-write，零堆分配 |
| Native AOT | `IsAotCompatible` + `IsTrimmable` + `PublishAot`，零 IL 抑制 |
| `static abstract IDbProvider` | 编译时分发，零虚调用 |
| 零第三方依赖 | Core：BCL + ADO.NET + `LoggerFactory.Abstractions` |

### 查询

| API | 说明 |
|-----|------|
| `Where(FormattableString)` / `OrWhere` | 参数化条件 |
| `OrderBy(expr)` / `ThenBy` / `Take` / `Skip` | 排序与分页 |
| `Select(expr[])` | 列投影（仅 DryRun） |
| `InnerJoin` / `LeftJoin` / `RightJoin` | SQL JOIN |
| `Include<TChild>(fk, pk)` / `ThenInclude` | 多级导航 |
| `WhereIn` / `WhereNotIn` | 自动分批（SQLite 999 / MySQL 65535） |
| `GroupBy` / `Having` | 聚合 |
| `With("cte", subquery)` | 公用表表达式 |
| `UnsafeWindowOver` | 窗口函数 |
| `ForUpdate()` / `ForShare()` | 悲观锁 |
| `AsSplitQuery()` | 仅构建根查询 |
| `ForRead()` / `ForWrite()` | 读写分离 |
| `WithCache(key, TTL)` | 有界 LRU 缓存 |
| `AsPrepared()` | DbCommand.PrepareAsync |
| `AsDryRun()` | SQL 预览不执行 |
| `Tag("name")` / `TagWithCaller()` | SQL 注释标签 |

### 写入

| API | 说明 |
|-----|------|
| `InsertAsync` / `UpdateAsync` / `DeleteAsync` | CRUD |
| `SaveAsync` (UPSERT) | 单次往返：`ON CONFLICT DO UPDATE` / `ON DUPLICATE KEY UPDATE` |
| `GetAsync(key)` / `GetAllAsync` | 主键查询 / 全表查询 |
| `BulkInsertAsync` / `BulkUpdateAsync` / `BulkDeleteAsync` | 批量操作 |

### 弹性

| 特性 | 说明 |
|------|------|
| 重试 | 可配置次数 + 指数退避 |
| 熔断器 | 连续失败 N 次后快速失败，半开探针恢复 |
| 超时 | 每命令通过 `CancellationTokenSource.CancelAfter` 控制 |
| 事务 | `WithTransaction(callback)` 自动 commit/rollback + 异常保留 |
| 保存点 | `SavepointAsync` / `RollbackToAsync` |

### 横切

| 特性 | 注解 |
|------|------|
| 软删除 | `[SoftDelete]` + `deleted_at` 列，查询自动过滤 |
| 多租户 | `[TenantAware]` + `tenant_id` 列，查询自动隔离 |
| 乐观锁 | `[ConcurrencyCheck]` + `Version` 列 |
| 拦截器 | `IQueryInterceptor` — OnBefore/OnAfter/OnError |
| 可观测性 | `WithTracing()` / `WithMetrics()` — BCL ActivitySource + Meter |
| SQL 文件嵌入 | `[SqlFile("path.sql")]` — 编译时嵌入为 `const string` |
| SQL 模板 | `[SqlTemplate("name")]` — 提取 `FormattableString` 为常量 |
| 值转换器 | `[Converter(typeof(T))]` — AOT 安全，源生成 |

### PostgreSQL 专有

| 特性 | 说明 |
|------|------|
| NOTIFY/LISTEN | `PgNotificationListener` — 自动重连、半开探针、订阅者隔离 |
| JSONB 查询 | `WhereJson(column, jsonpath, value)` |
| Binary COPY | `NpgsqlBinaryImporter` — 零往返批量写入 |

---

## 编译时诊断（21 条规则）

| 规则 | 说明 |
|------|------|
| PALORM001 | [Table] 实体缺少 [Key] |
| PALORM002 | 属性缺少 [Column] |
| PALORM003 | [ForeignKey] 引用表不存在 |
| PALORM004 | [ForeignKey] 缺少 OnDelete |
| PALORM005 | N+1 查询检测 |
| PALORM008-010 | OwnedJson 上下文验证 |
| PALORM011 | 拒绝限定表名 |
| PALORM012-013 | 并发令牌类型约束 |
| PALORM014 | [SoftDelete] 需 deleted_at 列 |
| PALORM016 | 未知类型 / 无效映射 |
| PALORM017 | 注解声明但不参与 DDL |
| PALORM018 | [TenantAware] 需 tenant_id 列 |
| PALORM019-022 | OwnedJson 上下文 / Key 合法性 |

> PALORM006/007 已移除（006 由 SqlFileEmitter Obsolete-error 机制承担）。

---

## 注解列表

`[Table]` `[Column]` `[Key]` `[NotMapped]` `[ForeignKey]` `[ConcurrencyCheck]` `[IgnoreOnInsert]` `[Computed]` `[DefaultValue]` `[SensitiveData]` `[Converter]` `[SoftDelete]` `[TenantAware]` `[OwnedJson]` `[Index]` `[Unique]` `[SqlFile]` `[SqlTemplate]`

---

## 使用示例

### 事务

```csharp
await db.WithTransaction(async ct =>
{
    await db.InsertAsync(new Order { Status = "pending" }, ct);
    await db.BulkInsertAsync(order.Items, ct);
    await db.ExecuteAsync(
        $"UPDATE inventory SET stock = stock - {qty} WHERE id = {itemId}", ct);
});
```

### 批量

```csharp
await db.BulkInsertAsync(tenThousandUsers);
await db.BulkUpdateAsync(modifiedUsers);
await db.BulkDeleteAsync<User>(new object[] { 1L, 2L, 3L });
```

### 多结果集

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
var (next, _) = await db.From<Order>()
    .OrderBy(o => o.CreatedAt, descending: true)
    .ToPageAsync(20, o => o.CreatedAt, rows[^1].CreatedAt.Ticks);
```

### OwnedJson

```csharp
[JsonSerializable(typeof(ProductDetails))]
internal sealed partial class ProductCtx : JsonSerializerContext;

[Table("products")]
public partial class Product
{
    [Key] public long Id { get; set; }
    [OwnedJson(typeof(ProductCtx))] public ProductDetails? Details { get; set; }
}
```

### 自定义转换器

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
```

### 聚合

```csharp
long total = await db.CountAsync<Order>();
decimal revenue = await db.SumAsync<Order>($"total");
double avgPrice = await db.AvgAsync<Product>($"price");
```

### 原生 SQL

```csharp
var users = await db.QueryAsync<User>($"SELECT * FROM users WHERE age > {18}");
var count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM users");
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

| Provider | 状态 | IL 抑制 |
|----------|------|:--:|
| SQLite | 原生运行通过 | 0 |
| PostgreSQL | 原生运行通过（Docker） | 0 |
| MySQL | 原生运行通过（Docker） | 0 |

---

## 性能数据

v4.6 基准测试 vs Dapper（SQLite 内存模式，10K 行种子）：

| 操作 | PalORM | Dapper | 对比 |
|------|-------:|-------:|:-----:|
| QueryAll 10K | 4.85ms / 1.47MB | 3.99ms / 1.32MB | +22% / +11% |
| GetByKey | 26.47μs / 3.98KB | 22.43μs / 2.34KB | +18% / +70% |
| Insert | 33.93μs / 4.97KB | 25.22μs / 3.73KB | +35% / +33% |
| BulkInsert 10K | 55.86ms / **4.97MB** | 34.32ms / 12.97MB | +63% / **-62%** |

> BulkInsert 分配**比 Dapper 低 62%**。详见 [BENCHMARKS.md](bench/PalORM.Benchmarks/BENCHMARKS.md)。

---

## 运行测试

测试使用 TUnit（Microsoft.Testing.Platform 模式），必须用 `dotnet run`，不能用 `dotnet test`：

```bash
dotnet run --project test/PalORM.Core.Tests          # 161 用例
dotnet run --project test/PalORM.SourceGen.Tests     # 104 用例
dotnet run --project test/PalORM.Integration.Tests -- \
  --treenode-filter "/*/*/*/*[Category!=ExternalDatabase]"  # 本地（160 用例）
```

全仓库 425 项测试。外部 DB 依赖测试标注 `Category=ExternalDatabase`。

---

## 文档

| 文档 | 内容 |
|------|------|
| [CHANGELOG.md](CHANGELOG.md) | 版本变更记录 |
| [docs/API参考.md](docs/API参考.md) | API 参考 |
| [docs/架构设计.md](docs/架构设计.md) | 架构设计与决策 |
| [docs/AOT部署指南.md](docs/AOT部署指南.md) | AOT 部署指南 |
| [docs/编码规范.md](docs/编码规范.md) | 编码规范 |
| [docs/踩坑目录.md](docs/踩坑目录.md) | 302 项 ORM 陷阱 |
| [docs/变更日志.md](docs/变更日志.md) | v2.0.1 历史快照 |

---

AGPL v3 · [PalDDD](https://github.com/PalDDD)
