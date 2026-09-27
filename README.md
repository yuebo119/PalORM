<div align="center">

# PalORM

**面向 Native AOT 的 .NET 11 微 ORM：编译时生成一切，运行时零反射**

[![.NET](https://img.shields.io/badge/.NET-11.0.0--preview.6-512BD4)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/PalORM.Core)](https://www.nuget.org/packages/PalORM.Core)
[![CI](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml/badge.svg)](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-AGPL--3.0--only-red)](LICENSE)

简体中文 | [English](README.en.md)

</div>

Roslyn 源生成器在编译期产出 SQL 构造、参数绑定、对象映射与迁移 DDL，运行时零反射、零 IL Emit；支持 PostgreSQL / MySQL / SQLite 三方言，多租户、乐观锁、软删除、审计、咨询锁等企业级特性开箱即用。

| 编译时诊断 | 20,000 行 BulkInsert | MySQL 单行操作 | SQLite Native AOT |
|:---:|:---:|:---:|:---:|
| **41 条** | **1.00× ADO.NET 地板** | **快 14~58%** | **exe 4.5 MB** |

数据口径见[性能](#-性能)一节（2026-09-24 基准批次）。

---

## 目录

- [特性](#-特性)
- [安装](#-安装)
- [快速开始](#-快速开始)
- [使用](#-使用)
- [配置](#-配置)
- [性能](#-性能)
- [与主流 ORM 对比](#-与主流-orm-对比)
- [开发](#-开发)
- [贡献](#-贡献)
- [社区](#-社区)
- [许可证](#-许可证)

---

## ✨ 特性

**编译时生成一切**。Roslyn `IIncrementalGenerator` 为每个 `[Table]` 实体生成 RowFactory（物化委托）、CommandFactory（参数绑定）、Migration（三方言 DDL）。41 条编译时诊断（38 分析器 + 3 生成器）把缺 `[Key]`、租户列可空绕过隔离、乐观锁基线为 0 这类运行时崩溃或静默错数据提前到编译期。`FormattableString` 路径的值只进 `@pN` 占位（编译期参数化，默认防注入）；显式逃生门 `Raw()`（原样字面量片段，拒绝控制字符）与 `SessionSetupSql` 由调用方负责内容。

**全链路 Native AOT**。.NET 生态唯一完整支持全链路 Native AOT 的 ORM：SQLite / PostgreSQL / MySQL 三方言发布验证全部通过（运行输出 `PalORM AOT verification PASSED`），无反射、无 IL Emit、无运行时代码生成。部署细节见 [docs/AOT部署指南.md](docs/AOT部署指南.md)。

**三方言批量策略**。同一套 API 按方言自动选择最快路径：PG Binary COPY、MySQL BulkCopy（LOAD DATA，服务端关闭时自动回退多值 INSERT）、SQLite 多值 INSERT。单语句批量 UPDATE（PG `UPDATE FROM VALUES` / MySQL `UPDATE JOIN VALUES ROW`，8.0.19+ 低版本回退 CASE WHEN）；乐观锁实体批量更新走 DbBatch 打包，PG 远程库实测 7.85×（v5.9 探针二十八，200 命令事务内 15.1ms vs 逐条 118.7ms）。

**企业级特性开箱即用**。多租户列隔离（查询缓存 key 自动加租户前缀，跨租户命中不可能）、乐观锁、软删除、审计拦截器、读写分离（`ForRead`）、咨询锁、弹性重试 + 熔断（v5.4 起自动覆盖只读查询管线），无需样板代码。

**零依赖核心**。PalORM.Core 不引用任何第三方 NuGet 包（仅 BCL + ADO.NET 抽象）。SQLite 经 SQLite3MC 驱动支持 AES-256 静态加密（连接字符串 `Password=` 启用，驱动层能力）。

## 📦 安装

环境要求：.NET SDK `11.0.100-preview.6` 或更高（`global.json` 已锁定 `rollForward: latestMinor`）；IDE 需支持 Roslyn 源生成器（SourceGen 以 Microsoft.CodeAnalysis 5.9.0 构建）。Native AOT 发布另需约 4 GB 内存（ILC 编译器），详见[开发](#-开发)。

```xml
<!-- PostgreSQL -->
<PackageReference Include="PalORM.PostgreSql" Version="5.9.0" />
<!-- MySQL -->
<PackageReference Include="PalORM.MySql" Version="5.9.0" />
<!-- SQLite -->
<PackageReference Include="PalORM.Sqlite" Version="5.9.0" />
```

每个 Provider 包含 `PalORM.Core`（运行时）和 `PalORM.SourceGen`（编译时源生成器）。安装后用下方快速开始的最小示例验证：能创建会话并完成一次插入即安装成功。

### 数据库兼容性

| 数据库 | 版本 | 驱动 | 加密 |
|--------|------|------|:---:|
| PostgreSQL | 14+（推荐 18） | Npgsql 10.0.3 | SSL/TLS |
| MySQL | 8.0+（推荐 8.4 LTS） | MySqlConnector 2.6.2 | SSL/TLS |
| SQLite | 3.47+（via SQLite3MC 2.4.0） | Microsoft.Data.Sqlite.Core 11.0.0-rc.1 | AES-256（驱动层，`Password=`） |

## 🚀 快速开始

### 定义实体

```csharp
using PalORM;

[Table("users")]
public partial class User
{
    [Key] public long Id { get; set; }
    [Column("email")] public string Email { get; set; } = "";
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("metadata")]
    [OwnedJson(typeof(UserJsonContext))]
    public UserMetadata? Metadata { get; set; }
}
```

> **可空约定**：引用类型属性声明为非可空（如 `string Email`）而库里是 NULL 时，读取抛 `SqlNullValueException`（响亮失败，不返回 null、不出静默错数据）。可能存 NULL 的列请声明为可空（`string?`）。替代方案评估留档于 `docs/性能优化方案-step7-pg-master.md` T13。

### 创建会话

```csharp
using var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = "Host=localhost;Username=user;Password=xxx;Database=mydb"
});
```

可选：启动期预热连接池（v5.6.0），让首批突发查询命中暖连接而非各付一次建连（远程建连实测约 8.5 ms/条）：

```csharp
// 打开 N 条连接随即归还池；SQLite 无池直接返回。建议配合 MinPoolSize 保持暖态
await DataSession<PostgreSqlProvider>.PreWarmAsync(options, count: 10);
```

### CRUD

```csharp
// 插入
var user = await db.InsertAsync(new User { Email = "alice@example.com", CreatedAt = DateTime.UtcNow });

// 查询
var alice = await db.GetAsync<User>(user.Id);
var all = await db.From<User>().Where($"email LIKE {"%@example.com%"}").ToListAsync();

// 更新
alice.Email = "new@example.com";
await db.UpdateAsync(alice);

// 删除
await db.DeleteAsync<User>(alice.Id);
```

### 批量操作

```csharp
// 批量插入（PG: Binary COPY / MySQL: BulkCopy 或多值 INSERT / SQLite: 多值 INSERT）
await db.BulkInsertAsync(users);

// 批量更新（逐条 + 乐观锁；v5.9 起 MySQL/PG 并发实体走 DbBatch 打包）
await db.BulkUpdateAsync(users);

// 批量更新（v5.0 单语句批量）
await db.BulkUpdateBatchAsync(users);

// 批量删除（IN 子句单语句）
await db.BulkDeleteAsync<User>(keyList);

// 批量 UPSERT（多行 UPSERT 单语句；默认键新行走逐条 INSERT 回填自增 ID）
await db.BulkMergeAsync(users);
```

各方法按方言的 SQL 策略：

| 方法 | PG | MySQL | SQLite |
|------|------|------|------|
| `BulkInsertAsync` | Binary COPY | BulkCopy（local_infile）或多值 INSERT | 多值 INSERT |
| `BulkUpdateAsync` | 逐条 + 乐观锁（DbBatch 打包） | 逐条 + 乐观锁（DbBatch 打包） | 逐条 + 乐观锁 |
| `BulkUpdateBatchAsync` | `FROM VALUES` | `UPDATE JOIN VALUES ROW`（8.0.19+，低版本回退 CASE WHEN） | 自动回退逐条 |
| `BulkDeleteAsync` | `IN` 单语句 | `IN` 单语句 | `IN` 单语句 |
| `BulkMergeAsync` | 多行 UPSERT（`ON CONFLICT DO UPDATE`） | 多行 UPSERT（`ON DUPLICATE KEY UPDATE`） | 多行 UPSERT（`ON CONFLICT DO UPDATE`） |

`BulkMergeAsync`（v5.6.0 集合化）按键状态分区：默认键（新行）走逐条 INSERT 保留 ID 回填契约，非默认键行按方言参数上限分批走多行 UPSERT（SQLite 999 / PG 与 MySQL 65535 参数上限，MySQL ODKU 单批 1000 行）；`[ConcurrencyCheck]` 实体维持逐条（UPSERT 无法尊重乐观锁）。

## 📖 使用

### 查询与分页

`From<T>()` 返回 `struct QueryBuilder<T>`，链式 `.Where()` / `.OrderBy()` / `.Take()` / `.Skip()` / `.Select()` / `.GroupBy()` / `.Having()` / `.Include()` / `.ThenInclude()`。支持 `InnerJoin` / `LeftJoin` / `RightJoin`、`WhereIn` / `WhereNotIn`（自动分批）、CTE（`.With()`）、窗口函数、悲观锁（`ForUpdate` / `ForShare`）、SQL 预览（`AsDryRun`）、查询缓存（`WithCache`）、可观测性（`WithMetrics` / `WithTracing`，OTel 指标与 tracing）。

同会话并发执行多个查询前先声明并行读租约：

```csharp
await using (db.ForParallelReads())
{
    await Task.WhenAll(db.From<User>().ToListAsync(), db.From<Order>().ToListAsync());
}
```

Keyset 游标分页（大偏移量场景比 OFFSET 稳定，返回行列表与总数）：

```csharp
// 第一页：按 CreatedAt 降序取 20 行
var (rows, total) = await db.From<Order>().ToPageAsync(20, o => o.CreatedAt);

// 下一页：上一页末行的排序值作游标
var next = await db.From<Order>().ToPageAsync(20, o => o.CreatedAt, lastValue: rows[^1].CreatedAt);
```

多结果集（`GridReader`）：

```csharp
using var grid = await db.QueryMultipleAsync($"SELECT * FROM users WHERE id = {userId}; SELECT * FROM orders WHERE user_id = {userId}");
var user = await grid.ReadFirstAsync<User>();
var orders = await grid.ReadAsync<Order>().ToListAsync();
```

### 事务与弹性

函数式事务 `WithTransaction(callback)` 自动 commit/rollback，支持保存点：

```csharp
await db.WithTransaction(async ct =>
{
    await db.InsertAsync(order, ct);
    await db.BulkInsertAsync(order.Items, ct);
    await db.ExecuteAsync($"UPDATE inventory SET stock = stock - {order.Items.Count} WHERE product_id = {productId}", ct);
});
```

弹性策略（`WithRetry` 指数退避 + `WithCircuitBreaker` 熔断）自 v5.4 起自动覆盖只读查询管线：

```csharp
// 配置一次，只读查询自动获得重试 + 熔断；SELECT 瞬时故障（死锁/超时/连接闪断）自动重试
await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(
    DbOptions.Production(connectionString)
        .WithRetry(maxRetries: 3)
        .WithCircuitBreaker(failureThreshold: 5, resetAfter: TimeSpan.FromSeconds(30)));

// 非幂等写入不自动重试，显式声明弹性意图
long affected = await db.ExecuteWithResilience(
    token => db.From<Order>()
        .Set(o => o.Status, OrderStatus.Paid)
        .Where($"id = {orderId}")
        .ExecuteNonQueryAsync(token), ct);
```

| 路径 | 弹性覆盖 | 说明 |
|------|:---:|------|
| `From<T>()` SELECT 族、`GetAsync`/`GetAllAsync`、聚合 | 自动 | 瞬时故障按配置重试并计入熔断 |
| 连接建立 | 自动 | `CreateAsync` 自有重试循环 |
| 写入路径（Insert/Update/Delete/Save/Bulk/存储过程） | 直连 | 非幂等写自动重试有重复执行风险；显式需求用 `ExecuteWithResilience` |
| 事务内查询 / `ToPageAsync` / 原始 SQL 家族 | 直连 | 事务内重试以次生异常掩盖根因 |

只读查询走弹性策略的每查询常数开销约 272 B 分配，与结果行数无关（单行 +8%，千行 +0.2%）；`MaxRetries=0` + `CircuitBreakerThreshold=0` 可完全直通，代价是失去带 `PalORM.InfrastructureTimeout` 标记的超时异常。

PG 事务级咨询锁（`TryAcquireXactLockAsync` 为非阻塞变体；必须在事务内调用，事务外获取会立即释放并显式报错）：

```csharp
await db.WithTransaction(async ct =>
{
    await db.AcquireXactLockAsync(resourceKey, ct);
    // 临界区操作...
});
// 事务结束自动释放锁
```

### 横切关注点

```csharp
// 审计拦截器（v5.0）：logParameters:true 时 [SensitiveData] 列参数值自动掩码
var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = connectionString,
    Interceptors = [new AuditInterceptor(loggerFactory.CreateLogger("Audit"))],
    SessionSetupSql = "SET TIME ZONE 'UTC'; SET search_path TO 'app, public'"
});
```

⚠️ `AuditInterceptor` 覆盖面：实体 SELECT 管线、QueryBuilder UPDATE 与 `ExecuteAsync`（v5.6.0 接入）；`InsertAsync`/`DeleteAsync`/`SaveAsync`/Bulk 家族/存储过程/迁移不产生审计记录，完整写入审计请用数据库层审计或 OpenTelemetry。

其余横切能力：`[SoftDelete]` 自动 WHERE 过滤、`[TenantAware]` + `WithTenant(id)` 单库列隔离（缓存 key 自动加 `__t:{tenantId}:` 前缀，`IgnoreFilters()` 走独立 `__all__:` 命名空间）、`[ConcurrencyCheck]` 乐观锁、`ForRead` 读写分离。全部注解（22 个）与执行方法明细见 [docs/API参考.md](docs/API参考.md)。

### 原生 SQL 与 SQL 文件

```csharp
var count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM users WHERE email LIKE {"%@example.com%"}");
```

SQL 文件编译时嵌入、校验存在性并按需提取方言段：

```csharp
public static partial class Reports
{
    [SqlFile("Reports/MonthlySales.sql")]
    public static partial string MonthlySales();

    // Provider = "pg" 只提取文件内 -- @pg 方言段
    [SqlFile("Reports/Sales.sql", Provider = "pg")]
    public static partial string SalesPg();
}

// MonthlySales() 返回编译期嵌入的 SQL 文本（生成器校验文件存在），交由 QueryAsync / ScalarAsync 等执行
```

PG 专有：`WhereJson`（JSONB 路径查询）、`PgNotificationListener`（NOTIFY/LISTEN 异步监听，自动重连）。

### Scaffold 工具

从既有数据库反向生成实体：

```bash
dotnet run --project tools/PalORM.Scaffold -- <connection-string> --dialect sqlite|pg|mysql [--namespace NS] [--output DIR]
```

三 Provider schema → C# 实体，40+ 类型映射（`uuid` → `Guid`、`jsonb` → `string`、`bytea` → `byte[]`、`date` → `DateOnly`、`time` → `TimeOnly`）。

## 🔧 配置

### 预设配置

```csharp
var dev  = DbOptions.Development(connectionString);            // 开发环境
var prod = DbOptions.Production(connectionString, readConn);  // 生产（含连接池 + 读写分离）
var test = DbOptions.Testing(connectionString);               // 测试（零重试 + 短超时）
var env  = DbOptions.FromEnvironment("PALORM_CONNECTION");    // 环境变量（Docker/K8s 友好）
```

### 配置项

| 属性 | 类型 | 默认值 | 说明 |
|------|------|:---:|------|
| `ConnectionString` | `string`（必需） | — | 主库连接串。支持 `$ENV:VAR_NAME` 环境变量引用 |
| `ReadConnectionString` | `string?` | null | 只读副本连接串。配置后 `ForRead()` 自动路由 |
| `ConnectionTimeout` | `TimeSpan` | 15s | 连接建立超时（含重试），超时抛 `TimeoutException` |
| `CommandTimeout` | `TimeSpan` | 30s | 命令执行超时；亚秒值向上取整为 1 秒 |
| `MaxRetries` | `int` | 3 | 瞬时故障最大重试次数，0=禁用。覆盖只读查询管线；写入不自动重试 |
| `RetryBackoff` | `Func<int, TimeSpan>?` | 指数退避 | 自定义重试间隔（参数=重试次数），负值抛异常 |
| `MaxPoolSize` | `int` | 100 | 连接池上限。SQLite 忽略（嵌入式库无服务端池） |
| `MinPoolSize` | `int` | 0 | v5.6.0。0=不覆盖驱动默认；正数透传，空闲修剪后池内至少保留这么多暖连接，消除突发查询重建连接的延迟尖峰。SQLite 忽略 |
| `PoolIdleTimeoutSeconds` | `int` | 0 | v5.6.0 起默认 0（更早为 30）。0=保留驱动默认（Npgsql 300s / MySqlConnector 180s）；正数才覆盖，代价是空闲超时后首个查询需重建物理连接（实测跨网段 `SELECT 1` 池内 0.3ms vs 新建 13.5ms） |
| `PoolLifetimeMinutes` | `int` | 60 | 连接最大生命周期，到期强制重建 |
| `PoolExplicitlyConfigured` | `bool` | false | `WithPool()` 设置后为 true（内部标记） |
| `CircuitBreakerThreshold` | `int` | 5 | 连续失败次数阈值，0=禁用熔断 |
| `CircuitBreakerResetAfter` | `TimeSpan` | 30s | 熔断后进入半开的等待时间 |
| `NamingConvention` | `enum` | None | None / SnakeCase / LowerCase。仅影响自定义 SQL 标识符归一化 |
| `Interceptors` | `IReadOnlyList<IQueryInterceptor>?` | null | 按 `Priority` 升序执行（`AuditInterceptor` 默认 200） |
| `ValidateQueryColumnOrder` | `bool` | true | `QueryAsync` 首行列序与实体声明序比对，不匹配抛异常；用列别名/表达式列时需关闭 |
| `QueryCache` | `IQueryCache?` | 1024 条 | 默认进程级共享；注入独立实例实现会话/租户级隔离（需线程安全） |
| `SessionSetupSql` | `string?` | null | 主连接首次激活后执行的 SQL（`SET TIME ZONE` / `search_path` 等，分号分隔） |
| `ReadSessionSetupSql` | `string?` | null | 读副本连接的同类设置；读连接按会话复用，每会话执行一次 |
| `LoggerFactory` | `ILoggerFactory?` | null | 设置后 `DataSession` 创建 `ILogger` |

> **SQLite 并发写扩展性**（2026-09-21 实测）：80/20 读写混合负载下，SQLite 从 1 到 8 线程吞吐下降 69%（WAL 单写者模型，裸 ADO.NET 对照同为 −69%，非 PalORM 引入的锁）；同期 PostgreSQL +7.2×、MySQL +4.6×。高并发写场景选 PG/MySQL，SQLite 适合读密集或低并发写的嵌入式场景。详见 [docs/架构设计.md](docs/架构设计.md)。

### 连接串自动调优

`CreateConnection` 时自动调优，仅当用户未显式设置时覆盖默认值。

**PostgreSQL**（6 项）：

| 参数 | 默认 → 调优值 | 收益 |
|------|:---:|------|
| `MaxAutoPrepare` | 0 → 100 | 自动预编译，查询延迟 −30~50% |
| `AutoPrepareMinUsages` | 5 → 2 | 第 2 次执行起 Prepare |
| `NoResetOnClose` | false → true | 归还连接跳过 DISCARD ALL，+30% localhost 吞吐 |
| `ReadBufferSize` | 8192 → 16384 | 大结果集吞吐 |
| `WriteBufferSize` | 8192 → 16384 | 大值写入吞吐 |
| `Enlist` | true → false | 跳过 TransactionScope 检查 |

**MySQL**（5 项）：

| 参数 | 默认 → 调优值 | 收益 |
|------|:---:|------|
| `AutoEnlist` | true → false | 跳过 TransactionScope |
| `ConnectionReset` | true → false | 跳过 COM_RESET_CONNECTION |
| `CancellationTimeout` | 2 → 5 | 防连接泄漏 |
| `AllowLoadLocalInfile` | false → true | MySqlBulkCopy 前提 |
| `ServerRedirectionMode` | Disabled → Preferred | Azure MySQL 直连 |

**SQLite PRAGMA**（8 项）：

| PRAGMA | 默认 → 调优值 | 收益 |
|------|:---:|------|
| `busy_timeout` | 0 → 5000ms | 并发写 BUSY 在引擎内等待 |
| `synchronous` | FULL → NORMAL | WAL 下安全，减少 fsync |
| `cache_size` | 2MB → 64MB | 读密集型提升 |
| `temp_store` | DEFAULT → MEMORY | 临时表走内存 |
| `wal_autocheckpoint` | 1000 → 1000 | 显式固定防漂移 |
| `journal_size_limit` | -1 → 64MB | 防 WAL 无界膨胀拖慢检查点 |
| `mmap_size` | 0 → 256MB | 文件库 I/O 加速（`:memory:` 跳过） |
| `analysis_limit` | 1000 → 400 | 约束 optimize/ANALYZE 采样成本 |

`NoResetOnClose=true` 的会话状态泄漏取舍（raw SQL 的 SET/临时表跨池租客可见）见 ITM-652：需要隔离时用独立连接（连接串 `Max Pool Size=1`）或显式 `DISCARD`。

### 进阶配方

**SQLite**（经 `DbOptions.SessionSetupSql` 执行，可覆盖上述默认）：

| 场景 | 配方 | 说明 |
|------|------|------|
| 批量/大行负载建库 | `PRAGMA page_size=16384` | 须在库首次创建前生效；既有库静默 no-op |
| 读密集 | `PRAGMA mmap_size=1073741824` | 提至 1GB（引擎上限 2GB−64KB） |
| 删除密集 | `PRAGMA secure_delete=OFF` | 消除删页覆写写放大；已删内容不再清零，加密库上有 forensic 残留 |
| 本地弹性直通 | `MaxRetries = 0` | 本地 BUSY 已由 `busy_timeout` 吸收，弹性机构在 RTT≈0 的本地库是净开销；远程库维持默认 |

**PostgreSQL**（连接串层显式启用，各有触发条件与取舍）：

| 场景 | 配方 | 说明 |
|------|------|------|
| 本机/同容器 PG | `Host=/var/run/postgresql` | Unix domain socket 替代 TCP（Npgsql 官方性能文档）；`Host` 以斜杠或盘符开头即按 socket 目录解析 |
| GSS 协商长尾削峰 | `GssEncryptionMode=Disable` | 实测（2026-09-26，Npgsql 10.0.3 × PG 18.4）中位建连不变，长尾 149ms → ≤21ms；安全策略变更，仅服务端不要求 GSS 加密时使用 |
| 非关键表批量写 | 事务内首条 `SET LOCAL synchronous_commit TO off` | PG 官方 28.4：短事务吞吐的最大应用层杠杆；崩溃丢最近约 600ms 提交（不损数据）。事件日志/缓存类可接受，账务类不可；须显式 opt-in，PalORM 不默认开启 |

**MySQL**（服务端/DBA 决策或显式操作）：

| 场景 | 配方 | 说明 |
|------|------|------|
| 计划器统计刷新 | `ANALYZE TABLE 表名`（经 `ExecuteAsync`） | InnoDB 默认 10% 行数变化自动重算，多数场景无需手工；MigrateAsync 不自动跑 |
| 批量写提交削峰 | `innodb_flush_log_at_trx_commit=2`（服务端全局变量） | 提交只写 OS 缓存不 fsync；崩溃丢最近约 1 秒提交，判据同 PG `synchronous_commit=off` |
| 锁等待 | 无需配置 | `innodb_lock_wait_timeout`（默认 50s）+ PalORM 弹性重试（死锁 1213 / 锁超时 1205）两层兜底 |
| 批量写报文上限 | 服务端 `max_allowed_packet` ≥ 16MB | LOAD DATA 与大参数批单包可达数 MB，超限报 `ER_NET_PACKET_TOO_LARGE` 或静默断连；报此错先查服务端值 |
| 批量写 LOAD DATA 开关 | 服务端 `local_infile=ON` | 缺一即回退多值 INSERT（检测按连接缓存 60s）；本机实测 LOAD DATA 比多值 INSERT 快 1.39×，远程库差距更大（官方基准 4~5× 量级） |
| 事务内多行插入选型 | 优先 `BulkInsertAsync` | 同一事务 100 行：逐条 45.7ms → BulkInsert 5.4ms（8.44×）→ `SessionBatch` 16.8ms；需逐条回填自增 ID 或混合语句时才用 SessionBatch |
| 排序/连接内存缓冲 | `sort_buffer_size` / `join_buffer_size` ≥ 256KB（默认即此值） | 远程大结果集 / 百万行级排序 join 先 `EXPLAIN` 确认无 `Using filesort` 再按需调（DBA 决策） |
| 隔离级别 RR→RC | `WithIsolationLevel(IsolationLevel.ReadCommitted)` | 引擎默认 RR（间隙锁）放大热点行锁竞争；取舍为 MVCC 快照语义变化，属业务决策 |

## 📊 性能

> **测试环境**：AMD Ryzen 9 8945HX（32 逻辑核）· Windows 10 22H2 · .NET 11 RC1（SDK 11.0.100-rc.1）· BenchmarkDotNet fork（net11）· SQLite 共享内存 10K 行 · PG 18.4 / MySQL 8.4.10 远程。对照臂版本随 2026-09-24 依赖升级轮（Dapper 2.1.89）。
> **数据批次**：SQLite CRUD 表 = 2026-09-24 BDN gate-set（launch 1 / warmup 3 / iteration 5，均值）；批量表 = 同日 PerfHub 完整批（`history-20260924-231214.json`，中位数 + 精确分配计数）；其余各表为对应专项测量的原始批次结果。完整方法论与复现命令见 [docs/性能基准规范.md](docs/性能基准规范.md) 与 `bench/perfhub/report.html`。

### SQLite CRUD（4 ORM 对照）

| 操作 | ADO.NET | Dapper | PalORM | RepoDb |
|------|------:|------:|------:|------:|
| 全表查询 10,000 行 | 4.28 ms | 3.69 ms | 4.85 ms（1.13x） | 3.53 ms |
| 单行插入 | 25.01 μs | 26.88 μs | 32.51 μs（1.30x） | 27.12 μs |
| 主键查询 | 22.96 μs | 25.28 μs | 27.66 μs（1.20x） | 27.43 μs |

### 批量操作（PerfHub · SQLite · 20,000 行 · 三臂对等口径）

| 方法 | Mean | Allocated | vs Dapper |
|:-----|-----:|----------:|:---------:|
| ADO.NET 地板 | 160.4 ms | 14.9 MB | 0.15x |
| Dapper 多值 INSERT | 1,105.4 ms | 79.5 MB | 1.0x |
| **PalORM BulkInsert** | **161.0 ms** | **14.8 MB** | **0.15x（快 6.9×，分配 19%）** |

三臂契约下 PalORM 与手写 ADO.NET 地板逐项持平（P/ADO 0.98–1.00）；Dapper 多值 INSERT 在 20,000 行档因巨型 SQL 字符串构造慢 6.9×、分配 5.3×。

### GC 装箱分析

| 操作（10K 行） | Mean | Allocated | bytes/row |
|:-----|-----:|----------:|:---------:|
| Insert（逐条） | 103.7 ms | 25,930 KB | 2,654 B |
| **BulkInsert** | **62.3 ms** | **5,099 KB** | **522 B** |
| BulkUpdate（逐条） | 28.8 ms | 17,973 KB | 1,839 B |
| Query（对照组） | 0.089 ms | 5.41 KB | 0.55 B |

PG COPY / MySQL BulkCopy 路径不走 `DbParameter.Value`，已无装箱。

### PostgreSQL（远程 PG 18.4）

| 操作 | Mean | Allocated |
|:-----|-----:|----------:|
| QueryAll 10K | 15.04 ms | 1,140 KB |
| BulkInsert 10K（COPY） | 43.06 ms | 9,797 KB |
| **BulkUpdateBatch 1K（FROM VALUES）** | **4.85 ms** | 2,777 KB |
| GetByKey | 501.9 μs | 13.64 KB |

### MySQL（远程 MySQL 8.4.10）

| 操作 | Mean | vs ADO.NET | Allocated |
|:-----|-----:|:---------:|----------:|
| QueryAll 10K | 94.79 ms | 0.85x（快 15%） | 1,937 KB |
| BulkInsert 10K | 49.41 ms | — | 4,741 KB |
| **BulkUpdateBatch 1K** | **12.43 ms** | — | 2,405 KB |
| **GetByKey** | **518.1 μs** | **0.42x（快 58%）** | 12.02 KB |
| **Insert** | **1,597 μs** | **0.86x（快 14%）** | 12.45 KB |

单行操作比原生 ADO.NET 快 14~58%：连接串调优（`AutoEnlist=false` / `ConnectionReset=false`）的收益在远程场景放大。表中 BulkUpdateBatch 为 CASE WHEN 形态批次数据；MySQL 形态自 v5.9.0 起改为 `UPDATE JOIN VALUES ROW`（20000 行实测 8.75× vs CASE WHEN）。

### SQL 构建（纳秒级）

| 方法 | Mean | Allocated |
|:-----|-----:|----------:|
| StringBuilder（基线） | 61.07 ns | 1,496 B |
| PalORM Simple | 129.01 ns | **544 B（−64%）** |
| PalORM Complex | 161.01 ns | **696 B（−53%）** |

### 跨方言 BulkUpdateBatch（1K 行）

| 方言 | SQL 策略 | Mean | 速度比 |
|------|---------|-----:|:------:|
| SQLite | CASE WHEN → 回退逐条 | 28.3 ms | 1.0x |
| **PostgreSQL** | **UPDATE FROM VALUES** | **4.85 ms** | **5.8x** |
| MySQL | CASE WHEN（v5.9.0 起为 VALUES ROW） | 12.43 ms | 2.3x |

### Native AOT 发布体积

| 方言 | exe 大小 | 发布目录 |
|------|:---:|:---:|
| SQLite | 4.5 MB | 26 MB |
| PostgreSQL | 11.6 MB | 61 MB |
| MySQL | 9.5 MB | 47 MB |

### 查询构建的性能提示：表达式树提到静态字段

`OrderBy` / `Select` / `GroupBy` / `WhereIn` / `Set` / `Include` 等接收 `Expression<Func<T, ...>>`。C# 在调用点构造表达式树，库拿到时成本已付，无法在库内缓存；实测每棵树 512 字节加 0.5~1.6 µs。把 lambda 提到静态字段即可消除：

```csharp
// 每次调用都重建表达式树
await db.From<Order>().OrderBy(o => o.CreatedAt).ToListAsync();

// 表达式树只构造一次，之后复用
private static readonly Expression<Func<Order, DateTime>> ByCreatedAt = o => o.CreatedAt;
await db.From<Order>().OrderBy(ByCreatedAt).ToListAsync();
```

| 场景 | 分配降幅 | 时间降幅 |
|------|:---:|:---:|
| `UPDATE` + `Set(...)` | −18.0% | −18.3% |
| 单行查询 + `OrderBy(...)` | −12.4% | −7.2% |
| `WhereIn(500)` | −0.72% | −1.7% |
| 10K 行查询 | 被结果集摊薄到可忽略 | 同 |

查询次数多、单次行数少且热路径用到构建器方法时值得改；批量与报表型负载不必。`Where` / `OrWhere` / `Having` 接收 `FormattableString`，不构造表达式树，无需处理。

## 🆚 与主流 ORM 对比

> 版本基准：PalORM 5.9.0 / Dapper 2.1.89 / EF Core 10.0.10 / RepoDb 1.16.0（仓库基准套件所用版本）。单元格依据见下方注释。

| 特性 | **PalORM** | Dapper | EF Core | RepoDb |
|------|:---:|:---:|:---:|:---:|
| **Native AOT 全链路** | ✓ 源生成验证 | △ Dapper.Aot 可选（实验性拦截器） | ❌ 实验性，生产不推荐 | ❌ 反射 + IL Emit |
| **编译时类型诊断** | ✓ 41 条（38 分析器 + 3 生成器） | ❌ 运行时失败 | △ 迁移检查（设计时） | ❌ 运行时失败 |
| **编译时 SQL 预构建** | ✓ Roslyn 源生成 | ❌ 运行时拼接 | △ 预编译查询（实验性） | ❌ 运行时表达式树 |
| **运行时反射** | 零 | △ 首次反射 + IL Emit 缓存 | △ 表达式树编译 | ❌ 反射 + IL Emit |
| **三方言批量策略** | ✓ COPY / BulkCopy / 多值 | ❌ 手写多值 SQL | △ Provider 各异 | △ BulkInsert 仅 SQL Server |
| **单语句多行 UPDATE** | ✓ FROM VALUES / UPDATE JOIN VALUES ROW / CASE WHEN | ❌ | ❌ ExecuteUpdate 仅按 WHERE 单值 | ❌ |
| **乐观锁** | ✓ `[ConcurrencyCheck]` 自动 | ❌ 手写 | ✓ `RowVersion` 自动 | ❌ 手写 |
| **软删除** | ✓ `[SoftDelete]` 自动过滤 | ❌ | ✓ 全局查询过滤器 | ❌ |
| **多租户列隔离** | ✓ `[TenantAware]` 编译时 | ❌ | △ 需手动实现 | ❌ |
| **OwnedJson 编译时安全** | ✓ `[OwnedJson]` + 源生成 | ❌ 手写 STJ | △ Owned Types（运行时） | ❌ |
| **审计拦截器** | ✓ | ❌ | ✓ Interceptors | ❌ |
| **咨询锁** | ✓ `pg_advisory_xact_lock` | ❌ | ❌ | ❌ |
| **会话级 SET** | ✓ `SessionSetupSql` | ❌ | ❌ | ❌ |
| **SQL 文件嵌入** | ✓ `[SqlFile]` 编译时校验 | ❌ | ❌ | ❌ |
| **断路器 + 重试** | ✓ 内置 | ❌ 需 Polly | △ 执行策略 | ❌ |
| **CTE / 窗口函数** | ✓ 链式 API | △ 原生 SQL 字符串 | △ LINQ 翻译（部分） | △ 原生 SQL |
| **多结果集** | ✓ `GridReader` | ✓ `QueryMultiple` | ❌ | ✓ `ExecuteQueryMultiple` |
| **Keyset 分页** | ✓ `ToPageAsync` | ❌ | ❌ | ❌ |
| **Scaffold 工具** | ✓ 三 Provider | ❌ | ✓ `dotnet ef dbContext scaffold` | ❌ |
| **连接串自动调优** | ✓ PG 6 / MySQL 5 / SQLite 8 项 | ❌ | ❌ | ❌ |
| **BulkInsert 内存** | ≈ Dapper 的 19% | 基线 | 最高（ChangeTracker） | 中等 |
| **核心包 NuGet 依赖** | 零 | 零 | 高（多包拆分） | 中等 |
| **目标框架** | net11.0（单目标） | 多目标（netstandard2.0+） | 多目标（net8+） | 多目标（netstandard2.0+） |
| **许可证** | AGPL-3.0-only | Apache-2.0 | MIT | Apache-2.0 |

核心差异：编译时生成 + 全链路 AOT 兼容 + 三方言批量策略。Dapper 快但运行时反射；EF Core 功能完整但运行时重、AOT 仍实验性；RepoDb 同为微 ORM 但无源生成，且批量仅 SQL Server。

对比依据：

- **Dapper**：`Dapper.AOT`（独立包，[aot.dapperlib.dev](https://aot.dapperlib.dev)）通过 Roslyn interceptors 生成 AOT 拦截器，interceptors 是 C# 实验性特性，非默认启用。
- **EF Core 10**：LTS（[learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)）。`ExecuteUpdateAsync` 仅支持按 WHERE 单值更新，无法单 SQL 内对每行设置不同值；AOT 仍实验性（[issue #35945](https://github.com/dotnet/efcore/issues/35945)）。
- **RepoDb**：BulkOperation 仅 SQL Server（[repodb.net/operation/bulkinsert](https://repodb.net/operation/bulkinsert)：*"It is only supporting the SQL Server RDBMS."*），其他方言走 packed statements。

## 🧰 开发

### 环境要求

| 维度 | JIT 运行时 | Native AOT 编译 |
|------|:---:|:---:|
| CPU 架构 | x64 / ARM64 | x64 已验证（ARM64 未实测），需指定 RID（`-r win-x64` / `linux-x64`） |
| 内存 | 256 MB | 4 GB（ILC 编译器；运行时仅需约 100 MB） |
| 磁盘 | 50 MB | 100 MB，SSD 推荐（ILC 大量临时文件，HDD 编译慢 5-10x） |

软件：.NET SDK 11.0.100-preview.6+（`global.json` 锁定）· C# `latest` · Windows 10+ / Linux / macOS · Visual Studio 2026 / Rider / VS Code（需支持 Roslyn 源生成器）。

### 项目结构

```
PalORM.Core           运行时核心（DataSession / QueryBuilder / Resilience / IQueryInterceptor）
PalORM.PostgreSql     Npgsql 适配 + JSONB / NOTIFY / Binary COPY / AdvisoryXactLock
PalORM.MySql          MySqlConnector 适配 + MySqlBulkCopy
PalORM.Sqlite         MDS + SQLite3MC 适配 + PRAGMA 调优
PalORM.SourceGen      Roslyn IIncrementalGenerator（netstandard2.0 编译器插件）
PalORM.Testing        测试辅助（TestEnvironment / TestDb）
```

**跨程序集注册契约**：每个模型程序集的生成物通过 `ModuleInitializer` 在该模块首次被触达（任一成员被调用、类型被实例化、静态字段被访问）时向 `PalORM_Runtime` 注册。引用了库程序集但从未触达其中任何类型时，该程序集的实体不会注册，运行期表现为 "not registered"：跨程序集消费方请确保实体类型被真实引用。

### 构建、测试与基准

SQLite 测试开箱即跑（无需外部数据库）；PG/MySQL 集成测试需外部数据库。构建路径、本地提交防线与基准环境（BenchmarkDotNet 本地 fork）见 [CONTRIBUTING.md](CONTRIBUTING.md)，架构决策见 [docs/架构设计.md](docs/架构设计.md) 与 [docs/adr/](docs/adr/)。

## 🤝 贡献

欢迎 issue 与 PR。提交前请跑通相关测试套件并保持 0 警告（`TreatWarningsAsErrors`）；涉及性能的改动请附基准前后对比。详细流程见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 💬 社区

<div align="center">

<img src="docs/qq-group.jpg" width="260" alt="QQ 群二维码：C#/.NET 新技术交流群（群号 1125599744）">

**C#/.NET 新技术交流群**（群号 **1125599744**）

QQ 内搜索群号或扫码加入。

</div>

## 📄 许可证

[AGPL-3.0-only](LICENSE)
