<div align="center">

# PalORM

**面向 Native AOT 的 .NET 11 微 ORM**

[![.NET](https://img.shields.io/badge/.NET-11.0.0--preview.6-512BD4)](https://dotnet.microsoft.com)
[![AOT](https://img.shields.io/badge/Native%20AOT-✓%20全链路验证-512BD4)](#native-aot)
[![Version](https://img.shields.io/badge/version-5.0.0-512BD4)](#)
[![License](https://img.shields.io/badge/license-AGPL--3.0--only-red)](LICENSE)

**编译时生成全部 SQL/参数绑定/物化代码——零运行时反射，Native AOT 全链路验证通过。**

**支持 PostgreSQL / MySQL / SQLite 三方言，同一套 API 跨数据库最优——BulkInsert 走 COPY/BulkCopy/多值 INSERT，BulkUpdateBatch 走 FROM VALUES/CASE WHEN/自动回退。**

**内置多租户隔离、乐观锁、软删除、AES-256 加密（SQLite）、审计拦截器、咨询锁——企业级特性开箱即用。**

</div>

---

## 目录

- [环境要求](#环境要求)
- [安装](#安装)
- [快速开始](#快速开始)
- [配置系统](#配置系统)
- [特性总览](#特性总览)
- [性能基准报告](#性能基准报告)
- [使用示例](#使用示例)
- [Native AOT](#native-aot)
- [Scaffold 工具](#scaffold-工具)

---

## 环境要求

### 硬件要求

| 维度 | JIT 运行时 | Native AOT 编译 | 说明 |
|------|:---:|:---:|------|
| CPU 架构 | x64 ✓ / ARM64 ✓ | **x64 ✓**（ARM64 未实测） | AOT 需指定 RID（`-r win-x64` / `linux-x64`） |
| 内存 | 256 MB | 4 GB（ILC 编译器） | AOT 编译器内存消耗高；运行时仅需 ~100 MB |
| 磁盘 | 50 MB（NuGet 包 + 依赖） | 100 MB（含发布产物） | PG AOT 产物 61MB / MySQL 47MB / SQLite 26MB |
| 存储 | 任意 | SSD 推荐 | ILC 编译大量临时文件，HDD 编译时间 5-10x |

### 软件要求

| 组件 | 版本 | 说明 |
|------|------|------|
| **.NET SDK** | **11.0.100-preview.6+** | `global.json` 锁定 `rollForward: latestMinor` |
| **C#** | 15.0（latest） | `LangVersion: latest` |
| **操作系统** | Windows 10+ / Linux / macOS | x64 / ARM64 |
| **IDE** | Visual Studio 2026 / Rider / VS Code | 需支持 Roslyn 5.6+ 源生成器 |

### 数据库兼容性

| 数据库 | 版本 | 驱动 | 加密 |
|--------|------|------|:---:|
| **PostgreSQL** | 14+（推荐 18） | Npgsql 10.0.3 | SSL/TLS |
| **MySQL** | 8.0+（推荐 8.4 LTS） | MySqlConnector 2.6.1 | SSL/TLS |
| **SQLite** | 3.47+（via SQLite3MC 2.3.6） | Microsoft.Data.Sqlite.Core 11.0-p6 | ✓ AES-256 |

### 为什么选择这些版本

- **Npgsql 10.0.3**：原生支持 PG `date/time` → `DateOnly/TimeOnly`、`NpgsqlSlimDataSourceBuilder`（AOT 友好）、Binary COPY 批量写入
- **MySqlConnector 2.6.1**：含安全修复 GHSA-473q（zero-config TLS MitM）、`MySqlBulkCopy`（LOAD DATA LOCAL INFILE）、VECTOR 类型准备
- **SQLite3MC 2.3.6**：内嵌 SQLite 3.47.2 + AES-256 加密，PCLRaw 跨平台原生二进制加载

---

## 安装

```xml
<!-- PostgreSQL -->
<PackageReference Include="PalORM.PostgreSql" Version="5.0.0" />
<!-- MySQL -->
<PackageReference Include="PalORM.MySql" Version="5.0.0" />
<!-- SQLite -->
<PackageReference Include="PalORM.Sqlite" Version="5.0.0" />
```

每个 Provider 包含 `PalORM.Core`（运行时）和 `PalORM.SourceGen`（编译时源生成器）。

---

## 快速开始

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

### 创建会话

```csharp
using var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = "Host=localhost;Username=user;Password=pass;Database=mydb"
});
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
// 批量插入（PG: Binary COPY / MySQL: BulkCopy / SQLite: 多值 INSERT）
await db.BulkInsertAsync(users);

// 批量更新（逐条 + 乐观锁）
await db.BulkUpdateAsync(users);

// 批量更新（v5.0 单语句批量，PG: FROM VALUES / MySQL: CASE WHEN / SQLite: 自动回退逐条）
await db.BulkUpdateBatchAsync(users);

// 批量删除（IN 子句单语句）
await db.BulkDeleteAsync<User>(keyList);

// 批量 UPSERT
await db.BulkMergeAsync(users);
```

---

## 配置系统

### 预设配置

```csharp
// 开发环境
var dev = DbOptions.Development(connectionString);

// 生产环境（含连接池配置）
var prod = DbOptions.Production(connectionString, readConnectionString);

// 测试环境（零重试 + 短超时）
var test = DbOptions.Testing(connectionString);

// 环境变量加载（Docker/K8s 友好）
var env = DbOptions.FromEnvironment("PALORM_CONNECTION");
```

### 全部配置项

| 属性 | 类型 | 默认值 | 说明 | v5.0 |
|------|------|:---:|------|:---:|
| `ConnectionString` | `string` (required) | — | 主库连接串（必需）。支持 `$ENV:VAR_NAME` 环境变量引用 | |
| `ReadConnectionString` | `string?` | null | 只读副本连接串。配置后 `ForRead()` 自动路由到副本 | |
| `ConnectionTimeout` | `TimeSpan` | 15s | 连接建立超时（含重试）。超时后抛 `TimeoutException` | |
| `CommandTimeout` | `TimeSpan` | 30s | 每条 SQL 命令的执行超时。亚秒值向上取整为 1 秒（避免塌缩为 0=无限等待） | |
| `MaxRetries` | `int` | 3 | 瞬时故障（连接失败/超时/死锁）最大重试次数。0=禁用重试 | |
| `RetryBackoff` | `Func<int, TimeSpan>?` | 指数退避 | 自定义重试间隔（参数=重试次数）。返回负值抛异常 | |
| `MaxPoolSize` | `int` | 100 | 连接池最大连接数。SQLite 不支持（抛 `NotSupportedException`） | |
| `PoolIdleTimeoutSeconds` | `int` | 30 | 连接池空闲超时（秒）。超时后空闲连接被关闭 | |
| `PoolLifetimeMinutes` | `int` | 60 | 连接最大生命周期（分钟）。到期后强制重建，避免长期持有陈旧连接 | |
| `PoolExplicitlyConfigured` | `bool` | false | `WithPool()` 设置后为 true。SQLite 据此拒绝池配置 | |
| `CircuitBreakerThreshold` | `int` | 5 | 断路器：连续失败次数阈值。0=禁用熔断 | |
| `CircuitBreakerResetAfter` | `TimeSpan` | 30s | 熔断后恢复等待时间。超时后进入半开状态（允许一次试探请求） | |
| `NamingConvention` | `enum` | None | 命名策略（None=原样 / SnakeCase / LowerCase）。仅影响自定义 SQL 中的标识符归一化，不影响源生成器列映射 | |
| `Interceptors` | `IReadOnlyList<IQueryInterceptor>?` | null | 查询拦截器列表。按 `Priority` 升序执行（数值小先执行）。`AuditInterceptor` 默认 Priority=200 | |
| `ValidateQueryColumnOrder` | `bool` | true | `QueryAsync` 首行列名与实体声明序比对，不匹配抛异常。使用列别名/表达式列的查询需关闭 | |
| `QueryCache` | `IQueryCache?` | 默认 1024 条 | 查询缓存实现。注入独立实例可实现会话/租户级隔离。实现需线程安全 | |
| `SessionSetupSql` | `string?` | null | 主连接首次激活后执行的 SQL（`SET TIME ZONE` / `search_path` / `statement_timeout`）。多条用分号分隔 | **v5.0** |
| `ReadSessionSetupSql` | `string?` | null | 读副本连接首次激活后执行的 SQL。语义同 `SessionSetupSql`，作用于 `ForRead` 路由的只读副本 | **v5.0** |
| `LoggerFactory` | `ILoggerFactory?` | null | 日志工厂。设置后 `DataSession` 创建 `ILogger`。日志级别过滤在 `LoggerFactory` 配置 | |

### v5.0 连接串自动调优

PalORM v5.0 在 `CreateConnection` 时自动调优（仅当用户未显式设置时覆盖默认值）：

**PostgreSQL**（6 项）：

| 参数 | 默认 → 调优值 | 收益 |
|------|:---:|------|
| `MaxAutoPrepare` | 0 → **100** | 查询延迟 -30~50%（自动预编译） |
| `AutoPrepareMinUsages` | 5 → **2** | 第 2 次执行起 Prepare |
| `NoResetOnClose` | false → **true** | 归还连接跳过 DISCARD ALL，+30% localhost 吞吐 |
| `ReadBufferSize` | 8192 → **16384** | 大结果集吞吐 |
| `WriteBufferSize` | 8192 → **16384** | 大值写入吞吐 |
| `Enlist` | true → **false** | 跳过 TransactionScope 检查 |

**MySQL**（5 项）：

| 参数 | 默认 → 调优值 | 收益 |
|------|:---:|------|
| `AutoEnlist` | true → **false** | 跳过 TransactionScope |
| `ConnectionReset` | true → **false** | 跳过 COM_RESET_CONNECTION |
| `CancellationTimeout` | 2 → **5** | 防连接泄漏 |
| `AllowLoadLocalInfile` | false → **true** | MySqlBulkCopy 前提 |
| `ServerRedirectionMode` | Disabled → **Preferred** | Azure MySQL 直连 |

**SQLite PRAGMA**（5 项）：

| PRAGMA | 默认 → 调优值 | 收益 |
|------|:---:|------|
| `synchronous` | FULL → **NORMAL** | WAL 下安全，减少 fsync |
| `cache_size` | 2MB → **64MB** | 读密集型提升 |
| `temp_store` | DEFAULT → **MEMORY** | 临时表走内存 |
| `wal_autocheckpoint` | 1000 → **1000** | 显式固定防漂移 |
| `mmap_size` | 0 → **256MB** | 文件库 I/O 加速（`:memory:` 跳过） |

---

## 与主流 ORM 特性对比

> 版本基准：**PalORM 5.0.0**（.NET 11）/ **Dapper 2.1.79**（2025）/ **EF Core 10.0.10**（2025-11 LTS）/ **RepoDb 1.15.1**（2025）。单元格依据见下方"对比依据"小节。

| 特性 | **PalORM 5.0** | Dapper 2.1.79 | EF Core 10.0.10 | RepoDb 1.15.1 |
|------|:---:|:---:|:---:|:---:|
| **Native AOT 全链路** | ✓ 源生成验证 | △ Dapper.Aot 可选（实验性拦截器） | ❌ 实验性，生产不推荐 | ❌ 反射 + IL Emit |
| **编译时类型诊断** | ✓ 20 条诊断规则 | ❌ 运行时失败 | △ 迁移检查（设计时） | ❌ 运行时失败 |
| **编译时 SQL 预构建** | ✓ Roslyn 源生成 | ❌ 运行时拼接 | △ 预编译查询（实验性） | ❌ 运行时表达式树 |
| **运行时反射** | 零 | △ 首次反射 + IL Emit 缓存 | △ 表达式树编译 | ❌ 反射 + IL Emit |
| **三方言批量策略** | ✓ COPY / BulkCopy / 多值 | ❌ 无（手写多值 SQL） | △ Provider 各异 | △ BulkInsert 仅 SQL Server |
| **单语句多行 UPDATE** | ✓ FROM VALUES / CASE WHEN | ❌ | ❌ ExecuteUpdate 仅按 WHERE 单值 | ❌ |
| **乐观锁** | ✓ `[ConcurrencyCheck]` 自动 | ❌ 手写 | ✓ `RowVersion` 自动 | ❌ 手写 |
| **软删除** | ✓ `[SoftDelete]` 自动过滤 | ❌ | ✓ 全局查询过滤器 | ❌ |
| **多租户列隔离** | ✓ `[TenantAware]` 编译时 | ❌ | △ 需手动实现 | ❌ |
| **OwnedJson 编译时安全** | ✓ `[OwnedJson]` + 源生成 | ❌ 手写 STJ | ✓ Owned Types（运行时） | ❌ |
| **审计拦截器** | ✓ `AuditInterceptor`（v5.0） | ❌ | ✓ Interceptors | ❌ |
| **咨询锁** | ✓ `pg_advisory_xact_lock`（v5.0） | ❌ | ❌ | ❌ |
| **会话级 SET** | ✓ `SessionSetupSql`（v5.0） | ❌ | ❌ | ❌ |
| **SQL 文件嵌入** | ✓ `[SqlFile]` 编译时校验 | ❌ | ❌ | ❌ |
| **断路器 + 重试** | ✓ 内置 | ❌ 需 Polly | △ 类似（执行策略） | ❌ |
| **CTE / 窗口函数** | ✓ 链式 API | △ 原生 SQL 字符串 | △ LINQ 翻译（部分） | △ 原生 SQL |
| **多结果集** | ✓ `GridReader` | ✓ `QueryMultiple` | ❌ | △ `ExecuteQueryMultiple` |
| **Keyset 分页** | ✓ `ToPageAsync` | ❌ | ❌ | ❌ |
| **Scaffold 工具** | ✓ 三 Provider（v5.0） | ❌ | ✓ `dotnet ef dbContext scaffold` | ❌ |
| **连接串自动调优** | ✓ PG 6 / MySQL 5 / SQLite 5 | ❌ | ❌ | ❌ |
| **BulkInsert 内存效率** | ✓ Dapper 的 ~40% | 基线 | 最高（ChangeTracker） | 中等（packed） |
| **核心包 NuGet 依赖** | 零 | 零 | 高（多包拆分） | 中等 |
| **目标框架** | net11.0（单目标） | 多目标（netstandard2.0+） | 多目标（net8+） | 多目标（netstandard2.0+） |
| **许可证** | AGPL-3.0-only | Apache-2.0 | MIT | Apache-2.0 |

> **PalORM 的核心差异**：编译时生成 + 全链路 AOT 兼容 + 三方言批量策略。Dapper 快但运行时反射；EF Core 功能完整但运行时重、AOT 仍实验性；RepoDb 与 PalORM 同为微 ORM 但无源生成，且批量仅 SQL Server。

### 对比依据

- **Dapper 2.1.79**：`Dapper.AOT`（独立包，[aot.dapperlib.dev](https://aot.dapperlib.dev)）通过 Roslyn interceptors 生成 AOT 拦截器，但 interceptors 是 C# 实验性特性，非默认启用。
- **EF Core 10.0.10**：EF Core 10 为 LTS（[learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)）。`ExecuteUpdateAsync`/`ExecuteDeleteAsync` 仅支持"按 WHERE 单值更新"，无法在单 SQL 内对每行设置不同值。AOT 仍为实验性（[issue #35945](https://github.com/dotnet/efcore/issues/35945)，CS9137 错误未解决）。Scaffold：`dotnet ef dbContext scaffold` 完整支持。
- **RepoDb 1.15.1**：BulkOperation 仅 SQL Server（[repodb.net/operation/bulkinsert](https://repodb.net/operation/bulkinsert)：*"It is only supporting the SQL Server RDBMS."*），其他方言走 `InsertAll`（packed statements，非真正二进制 bulk）。`ExecuteQueryMultiple` 提供多结果集。

## 特性总览

### 编译时源生成

Roslyn `IIncrementalGenerator` 为每个 `[Table]` 实体生成 RowFactory（物化委托）、CommandFactory（参数绑定）、Migration（三方言 DDL）。`FormattableString` 参数化杜绝 SQL 注入，`WithComparer` 优化增量缓存命中率。Native AOT 全链路零 IL。

### 注解

| 注解 | 说明 |
|------|------|
| `[Table("name")]` | 表名，标识 ORM 实体 |
| `[Column("name")]` | 列名 |
| `[Key]` | 主键（支持 `AutoIncrement = false`） |
| `[ForeignKey]` | 外键引用（支持 `OnDelete` 级联策略） |
| `[ConcurrencyCheck]` | 乐观锁版本检查 |
| `[SoftDelete]` | 软删除自动过滤 |
| `[TenantAware]` | 多租户自动隔离（`SetTenant(id)` 单库列过滤） |
| `[OwnedJson(typeof(Ctx))]` | 编译时安全 JSON 序列化 |
| `[Index(name, cols, Unique = true)]` | 复合索引 |
| `[Unique]` | 唯一约束 |
| `[Computed("SQL")]` | 计算列 |
| `[IgnoreOnInsert]` | 插入跳过 |
| `[Converter(typeof(T))]` | 自定义值转换器 |
| `[SqlFile("path.sql")]` | 编译时嵌入 SQL 文件 |
| `[SqlTemplate("name")]` | FormattableString 常量 |
| `[SensitiveData]` | 敏感字段标记 |
| `[NotMapped]` | 排除映射 |
| `[Schema("name")]` | 数据库 schema |

### 查询

`From<T>()` 返回 `struct QueryBuilder<T>`，链式 `.Where()` / `.OrderBy()` / `.Take()` / `.Skip()` / `.Select()` / `.GroupBy()` / `.Having()` / `.Include()` / `.ThenInclude()`。支持 `InnerJoin` / `LeftJoin` / `RightJoin`、`WhereIn` / `WhereNotIn`（自动分批）、CTE、窗口函数、悲观锁（`ForUpdate` / `ForShare`）、SQL 预览（`AsDryRun`）、查询缓存、Keyset 分页。

### 写入与批量

`InsertAsync` / `UpdateAsync` / `DeleteAsync` / `SaveAsync`（UPSERT）。批量：

| 方法 | PG | MySQL | SQLite |
|------|------|------|------|
| `BulkInsertAsync` | Binary COPY | BulkCopy（local_infile）或 多值 INSERT | 多值 INSERT |
| `BulkUpdateAsync` | 逐条 + 乐观锁 | 逐条 + 乐观锁 | 逐条 + 乐观锁 |
| `BulkUpdateBatchAsync` | **FROM VALUES**（v5.0） | **CASE WHEN**（v5.0） | **自动回退逐条** |
| `BulkDeleteAsync` | `IN` 单语句 | `IN` 单语句 | `IN` 单语句 |
| `BulkMergeAsync` | 逐条 UPSERT | 逐条 UPSERT | 逐条 UPSERT |

### 事务与弹性

函数式事务 `WithTransaction(callback)` 自动 commit/rollback，支持保存点。`WithRetry` 指数退避重试，`WithCircuitBreaker` 熔断快速失败。

### 横切关注点

| 功能 | 说明 |
|------|------|
| `[SoftDelete]` | 软删除自动 WHERE 过滤 |
| `[TenantAware]` | 多租户 `SetTenant(id)` 单库列隔离 |
| `[ConcurrencyCheck]` | 乐观锁 `version` 字段自动检查 |
| `AuditInterceptor`（v5.0） | SQL 审计拦截器（OnBefore/OnAfter/OnError，参数脱敏） |
| `IQueryInterceptor` | 三阶段查询拦截器接口 |
| `SessionSetupSql`（v5.0） | 连接首次激活后执行 SET 语句（`SET TIME ZONE` / `search_path`） |
| `ForRead` | 读写分离（只读副本路由） |

### PostgreSQL 专有

| 功能 | 说明 |
|------|------|
| `PgNotificationListener` | 异步通知监听（自动重连 + 半开探针） |
| `WhereJson` | JSONB 路径查询 |
| Binary COPY | `BulkInsertAsync` 内部使用 |
| `AcquireXactLockAsync`（v5.0） | 事务级咨询锁 `pg_advisory_xact_lock` |
| `TryAcquireXactLockAsync`（v5.0） | 非阻塞咨询锁 |

### 编译时诊断（PALORM001-022）

20 条 Roslyn 分析器规则：缺 `[Key]`、N+1 检测、软删/租户列校验、OwnedJson 上下文验证等——编译期发现错误，不是运行时崩溃。

---

## 性能基准报告

> **测试环境**：AMD Ryzen 9 8945HX (32 logical) · Windows 10 22H2 · .NET 11.0.0-preview.6 · BenchmarkDotNet fork (net11) · SQLite 共享内存 10K 行 · PG 18.4 / MySQL 8.4.10 远程（192.168.x.x）

### SQLite CRUD（4 ORM 对照）

#### 全表查询 10,000 行

| 方法 | Mean | vs ADO.NET | Allocated |
|:-----|-----:|:---------:|----------:|
| **ADO.NET**（基线） | 4.59 ms | 1.00x | 1.30 MB |
| Dapper | 4.59 ms | 1.01x | 1.32 MB |
| **PalORM** | **5.52 ms** | **1.22x** | **1.48 MB** |
| RepoDb | 4.31 ms | 0.95x | 1.09 MB |

#### 单行插入

| 方法 | Mean | Allocated |
|:-----|-----:|----------:|
| **ADO.NET** | 25.53 μs | 1.36 KB |
| Dapper | 27.38 μs | 3.66 KB |
| **PalORM** | **38.23 μs** | **5.66 KB** |
| RepoDb | 27.85 μs | 3.66 KB |

#### 主键查询

| 方法 | Mean | Allocated |
|:-----|-----:|----------:|
| **ADO.NET** | 22.32 μs | 1.63 KB |
| **PalORM** | **25.09 μs** | **4.66 KB** |
| RepoDb | 22.39 μs | 5.35 KB |

### 批量操作（10,000 行）

| 方法 | Mean | Allocated | vs Dapper |
|:-----|-----:|----------:|:---------:|
| Dapper 多值 INSERT | 22.99 ms | 12,658 KB | 1.0x |
| **PalORM BulkInsert** | **48.58 ms** | **5,082 KB** | **分配仅 40%** |

> PalORM BulkInsert 耗时高于 Dapper（源生成 binder + SessionOperationState 门禁开销），但**内存分配仅 Dapper 的 40%**——GC 压力显著更低。

### GC 装箱分析（v5.0 新增）

| 操作（10K 行） | Mean | Allocated | bytes/row | 装箱占比 |
|:-----|-----:|----------:|:---------:|:--------:|
| Insert（逐条） | 103.7 ms | 25,930 KB | 2,654 B | ~5% |
| **BulkInsert** | **62.3 ms** | **5,099 KB** | **522 B** | **~24.5%** |
| BulkUpdate（逐条） | 28.8 ms | 17,973 KB | 1,839 B | ~7% |
| BulkUpdateBatch（回退） | 28.3 ms | 17,973 KB | 1,839 B | ~7% |
| Query（对照组） | 0.089 ms | 5.41 KB | 0.55 B | 0% |

> BulkInsert 装箱 ~24.5%（每行 4 值类型列 × ~32B/装箱）。PG COPY / MySQL BulkCopy 路径不走 `DbParameter.Value`，**已无装箱**。

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
| QueryAll 10K | 94.79 ms | **0.85x（快 15%）** | 1,937 KB |
| BulkInsert 10K | 49.41 ms | — | 4,741 KB |
| **BulkUpdateBatch 1K** | **12.43 ms** | — | 2,405 KB |
| **GetByKey** | **518.1 μs** | **0.42x（快 58%）** | 12.02 KB |
| **Insert** | **1,597 μs** | **0.86x（快 14%）** | 12.45 KB |

> **MySQL 单行操作比原生 ADO.NET 快 14~58%**——v5.0 连接串调优（`AutoEnlist=false` / `ConnectionReset=false`）的收益在远程场景放大。

### SQL 构建（纳秒级）

| 方法 | Mean | Allocated |
|:-----|-----:|----------:|
| StringBuilder（基线） | 61.07 ns | 1,496 B |
| PalORM Simple | 129.01 ns | **544 B（-64%）** |
| PalORM Complex | 161.01 ns | **696 B（-53%）** |

### 跨方言 BulkUpdateBatch 对照（1K 行）

| 方言 | SQL 策略 | Mean | 速度比 |
|------|---------|-----:|:------:|
| SQLite | CASE WHEN → 回退逐条 | 28.3 ms | 1.0x |
| **PostgreSQL** | **UPDATE FROM VALUES** | **4.85 ms** | **5.8x 快** |
| MySQL | CASE WHEN | 12.43 ms | 2.3x |

### Native AOT 发布体积

| 方言 | exe 大小 | 发布目录 |
|------|:---:|:---:|
| SQLite | 4.5 MB | 26 MB |
| PostgreSQL | 11.6 MB | 61 MB |
| MySQL | 9.5 MB | 47 MB |

---

## 使用示例

### 事务

```csharp
await db.WithTransaction(async ct =>
{
    await db.InsertAsync(order, ct);
    await db.BulkInsertAsync(order.Items, ct);
    await db.ExecuteAsync($"UPDATE inventory SET stock = stock - {order.Items.Count} WHERE product_id = {productId}", ct);
});
```

### 多结果集（GridReader）

```csharp
using var grid = await db.QueryMultipleAsync($"SELECT * FROM users WHERE id = {userId}; SELECT * FROM orders WHERE user_id = {userId}");
var user = await grid.ReadFirstAsync<User>();
var orders = await grid.ReadAsync<Order>().ToListAsync();
```

### Keyset 游标分页

```csharp
var page = await db.From<Order>()
    .Where($"created_at < {cursor}")
    .OrderBy(o => o.CreatedAt, descending: true)
    .Take(20)
    .ToPageAsync();
```

### AuditInterceptor（v5.0）

```csharp
var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = connectionString,
    Interceptors = [new AuditInterceptor(loggerFactory.CreateLogger("Audit"))]
});
```

### SessionSetupSql（v5.0）

```csharp
var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = connectionString,
    SessionSetupSql = "SET TIME ZONE 'UTC'; SET search_path TO 'app, public'"
});
```

### AdvisoryXactLock（v5.0 PG 专有）

```csharp
await db.WithTransaction(async ct =>
{
    await db.AcquireXactLockAsync(resourceKey, ct);  // 阻塞获取
    // 临界区操作...
});
// 事务结束自动释放锁
```

### 原生 SQL

```csharp
var count = await db.ScalarAsync<long>($"SELECT COUNT(*) FROM users WHERE email LIKE {"%@example.com%"}");
```

### 编译时 SQL 文件

```csharp
[SqlFile("Reports/MonthlySales.sql")]
public static partial MonthlySalesReport[] GetMonthlySales();
```

---

## Native AOT

PalORM 是**全链路 Native AOT 兼容**的微 ORM：

```bash
dotnet publish -c Release -r win-x64 /p:PublishAot=true
```

**AOT 安全保证**：

| 组件 | AOT 状态 |
|------|:---:|
| RowFactory（物化委托） | ✓ 编译时生成 |
| CommandFactory（参数绑定） | ✓ 编译时生成 |
| Migration DDL | ✓ 编译时生成 |
| QueryBuilder（值类型 struct） | ✓ 零虚调用 |
| OwnedJson（JsonSerializerContext） | ✓ 源生成 |
| 注解诊断（PALORM001-022） | ✓ 编译时 |
| AuditInterceptor | ✓ 零反射 |
| BulkUpdateBatchAsync | ✓ StringBuilder + 参数绑定 |

**已验证发布**：SQLite / PostgreSQL / MySQL 三方言 Native AOT 发布全部成功（win-x64），运行输出 `PalORM AOT verification PASSED`。

---

## Scaffold 工具

```bash
dotnet run --project tools/PalORM.Scaffold -- <connection-string> --dialect sqlite|pg|mysql [--namespace NS] [--output DIR]
```

三 Provider schema → C# 实体反向工程。40+ 类型映射（含 `uuid` → `Guid`、`jsonb` → `string`、`bytea` → `byte[]`、`date` → `DateOnly`、`time` → `TimeOnly`）。

---

## 架构设计

```
PalORM.Core           运行时核心（DataSession / QueryBuilder / Resilience / IQueryInterceptor）
PalORM.PostgreSql     Npgsql 适配 + JSONB / NOTIFY / Binary COPY / AdvisoryXactLock
PalORM.MySql          MySqlConnector 适配 + MySqlBulkCopy
PalORM.Sqlite         MDS + SQLite3MC 适配 + PRAGMA 调优
PalORM.SourceGen      Roslyn IIncrementalGenerator（netstandard2.0 编译器插件）
PalORM.Testing        测试辅助（TestEnvironment / TestDb）
```

**零运行时依赖**：PalORM.Core 不引用任何第三方 NuGet 包（仅 BCL + ADO.NET 抽象 + 共享框架日志抽象）。

---

## 许可证

[AGPL-3.0-only](LICENSE)
