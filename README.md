<div align="center">

# PalORM

**面向 Native AOT 的 .NET 11 微 ORM：编译时生成一切，运行时零反射**

[![.NET](https://img.shields.io/badge/.NET-11.0.0--rc.2-512BD4)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/PalORM.Core)](https://www.nuget.org/packages/PalORM.Core)
[![CI](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml/badge.svg)](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-AGPL--3.0--only-red)](LICENSE)

[![Native AOT](https://img.shields.io/badge/Native_AOT-3_dialects_verified-512BD4)](docs/AOT部署指南.md)
[![Diagnostics](https://img.shields.io/badge/Compile--time_diagnostics-45_rules-512BD4)](docs/API参考.md)
[![Dialects](https://img.shields.io/badge/Dialects-PostgreSQL_·_MySQL_·_SQLite-512BD4)](#-数据库兼容性)

简体中文 | [English](README.en.md)

</div>

Roslyn 源生成器在编译期产出 SQL 构造、参数绑定、对象映射与迁移 DDL，运行时零反射、零 IL Emit；支持 PostgreSQL / MySQL / SQLite 三方言，多租户、乐观锁、软删除、审计、咨询锁等企业级特性开箱即用。

- **编译期安全网**：45 条 Roslyn 诊断把缺主键、租户列可空绕过隔离、乐观锁基线为 0、循环内 N+1 查询这类运行时崩溃或静默错数据拦截在编译期，错误可跳转声明位置
- **全链路 Native AOT**：.NET 生态唯一三方言 AOT 发布验证全部通过的 ORM，无反射、无 IL Emit、无运行时代码生成，SQLite 发布 exe 仅 4.5 MB
- **地板级性能**：单行 CRUD 与 ADO.NET 手写地板持平（0.94~1.01×），批量写自动走方言最快路径——PG Binary COPY、MySQL LOAD DATA、单语句批量 UPDATE（MySQL 实测快 3.3×）；20,000 行 BulkInsert 分配约为 Dapper 的 19%；Web 服务"一请求一会话"形态内置会话池（32 并发实测 P99 −18%、Gen0 回收 −58%）
- **企业级横切开箱即用**：多租户列隔离（查询与 Update/Delete/Bulk 自动附加租户谓词；租户列在更新中不可写，Insert 由实体 `tenant_id` 承载）、乐观锁、软删除、审计拦截器（`Set()` 写入的敏感列参数掩码）、读写分离、咨询锁、弹性重试 + 熔断（零 Polly 依赖）
- **零依赖核心**：PalORM.Core 不引用任何第三方 NuGet 包（仅 BCL + ADO.NET 抽象）；SQLite 经 SQLite3MC 驱动支持 AES-256 静态加密

数据口径见[性能](#-性能)一节（2026-10-14 基准批次）。

---

## 目录

| 本文档 | | 深度文档（[docs/](docs/)） | |
|---|---|---|---|
| [✨ 特性](#-特性) | [🚀 快速开始](#-快速开始) | [功能特性全景（源码级）](docs/功能特性.md) | [AOT 部署指南](docs/AOT部署指南.md) |
| [🆚 与主流 ORM 对比](#-与主流-orm-对比) | [📖 使用](#-使用) | [API 参考](docs/API参考.md) | [性能基准规范](docs/性能基准规范.md) |
| [📊 性能](#-性能) | [✅ 最佳实践速查](#-最佳实践速查) | [架构设计](docs/架构设计.md) | [发布规范](docs/发布规范.md) |
| [📦 安装](#-安装) | [🔧 配置](#-配置) | [架构决策（ADR）](docs/adr/) | [编码规范](docs/编码规范.md) |
| [🔄 升级指南](#-升级指南) | [🧰 开发](#-开发) | [贡献指南](CONTRIBUTING.md) | [变更日志](CHANGELOG.md) |
| [💬 社区](#-社区) | [📄 许可证](#-许可证) | | |

---

## ✨ 特性

**编译时生成一切**。Roslyn `IIncrementalGenerator` 为每个 `[Table]` 实体生成 RowFactory（物化委托）、CommandFactory（参数绑定）、Migration（三方言 DDL）；`[Projection]` 标记的 DTO 同样获得源生成的 RowFactory——join/报表结果直接映射到非表类型（v6.0）；`readonly struct` 投影配合 `QueryStructsAsync<T>` 可让大结果集每行零堆分配。45 条编译时诊断（42 分析器 + 3 生成器）把缺 `[Key]`、租户列可空绕过隔离、乐观锁基线为 0 这类运行时崩溃或静默错数据提前到编译期，错误列表可跳转到声明位置（PALORM041/045/046 带锚点）。`FormattableString` 路径的值只进 `@pN` 占位（编译期参数化，默认防注入）；显式逃生门 `Raw()`（原样字面量片段，拒绝控制字符）与 `SessionSetupSql` 由调用方负责内容。

**全链路 Native AOT**。.NET 生态唯一完整支持全链路 Native AOT 的 ORM：SQLite / PostgreSQL / MySQL 三方言发布验证全部通过（运行输出 `PalORM AOT verification PASSED`），无反射、无 IL Emit、无运行时代码生成。部署细节见 [docs/AOT部署指南.md](docs/AOT部署指南.md)。

**三方言批量策略**。同一套 API 按方言自动选择最快路径：PG Binary COPY、MySQL BulkCopy（LOAD DATA，服务端关闭时自动回退多值 INSERT）、SQLite 多值 INSERT。单语句批量 UPDATE（PG `UPDATE FROM VALUES` / `UPDATE FROM UNNEST` / MySQL `UPDATE JOIN VALUES ROW`，8.0.19+ 低版本回退 CASE WHEN）；版本化实体（`[ConcurrencyCheck]`）在 PG/MySQL 能力支持时同样走版本化单语句（逐行 version 谓词，S6 探针 1000 行 PG 3.06× / MySQL 4.73×），能力不全回退 DbBatch 逐条。

**企业级横切开箱即用**。多租户列隔离（查询缓存 key 自动加租户前缀，跨租户命中不可能）、乐观锁、软删除、审计拦截器、读写分离（`ForRead`）、咨询锁、弹性重试 + 熔断（v5.4 起自动覆盖只读查询管线），无需样板代码。

**会话池化**（v6.5 `DataSessionPool`）。Web 服务"一请求一会话"形态的固定成本消除器：租出的会话归还后瞬态状态（租户/超时/重试覆写）全部复位，命令复用槽、读连接、物化容量跨租期保留——复用槽在同会话第 3 次同形状操作才晋升，不池化则每请求形态下结构性永不激活。32 并发压测实测 P99 −18%、Gen0 回收 −58%、每操作分配 −2576 B，租还自身约 1 µs。

**宽表列子集更新**。`UpdateColumnsAsync(entity, [x => x.A, x => x.B])` 只 SET 选中列：复用生成绑定器的值转换（转换器/OwnedJson/枚举），裁掉未选中参数并重编号；版本列照常 `+1` 与冲突检测，租户谓词自动追加。宽行大值时 SQL、服务端 SET 列数与网络负载同比例下降。

**请求级查询管道**。`CreatePipeline()` 把多条彼此独立的只读查询合并成一次数据库往返：PostgreSQL 经 `NpgsqlBatch`（本机 4 条单键查询 249 µs → 72~79 µs/条，3.2~3.4×），MySQL 经 `MySqlBatch`（本机执行耗时时持平、WAN 省 RTT），结果按入队顺序类型化返回；SQLite 顺序回退。`pipeline.QueryStructs<T>(sql)` 让结构体投影也走同一批处理（单往返 + 每行零堆对象）。配套三项：MySQL 复用命令晋升时自动服务端预编译（重复单键读写 −10~18%，PostgreSQL 由驱动 AutoPrepare 自动完成）；SQLite 干净连接句柄跳过每会话 PRAGMA、原生 SQL 改写过连接态才重设 `foreign_keys`（CreateAsync+Dispose 1.71 → 0.50 µs，per-op 单键读 7.35 → 6.40 µs、2640 → 2176 B）；MySQL 可选的同步逐行读（`UseSynchronousRowRead`，全表物化约 998 vs 1846~2043 ns/行，流式 API 不受影响）。

**SQL 资产编译期管理**。`[SqlFile]` 把外部 .sql 文件嵌入为常量（支持 `-- @pg/@mysql/@sqlite/@all` 方言条件段，改 SQL 免 Rebuild，文件缺失编译期报错）；`[SqlTemplate]` 把方法体插值串提升为模板常量，配合 `.AsPrepared()` 走 `DbCommand.PrepareAsync` 预编译；AutoTagging（opt-in 编译开关）经 C# `[InterceptsLocation]` 拦截 9 个查询终态方法，SQL 自动注入「文件:行号 成员名」定位注释，排查零成本。

**企业运维面**。`HealthCheckAsync` SELECT 1 探活（错误消息只透出异常类型名，防数据库拓扑泄露）；跨方言统一异常族——唯一键冲突统一翻译为 `UniqueConstraintViolationException`（不用记各驱动错误码）、乐观锁冲突 `ConcurrencyConflictException`、基础设施超时带 `PalORM.InfrastructureTimeout` 标记、瞬时故障带 `PalORM.IsTransient` 标记；OpenTelemetry 全套（`WithTracing` ActivitySource + `WithMetrics` 指标 `palorm.query.executions`/`palorm.query.duration`，查询缓存另有 `palorm.cache.*` 四指标）。

**方言专属能力**。PG：UNNEST 数组参数批量写、事务级咨询锁（阻塞/非阻塞 × long 键/双 int 键四方法）、LISTEN/NOTIFY 后台监听（自动重连 + 30s 心跳 + 零分配事件分发）与 `NotifyAsync` 发送、pgvector 文本边界路线（`::vector` 写入/`::text` 解析，AOT 兼容）、JSONB 路径查询 `WhereJson`。MySQL：local_infile 三层降级（探测缓存 → 失败重探测 → 多值 INSERT 回退）、8.0.19+ `UPDATE JOIN VALUES ROW` 运行时版本探测。SQLite：AES-256 静态加密、9 项 PRAGMA 自动调优（WAL/mmap/optimize 统计优化）、BUSY/LOCKED 瞬时故障自动重试。

**Schema 迁移与种子**。`MigrateAsync` 执行编译期三方言 DDL，FK 依赖按拓扑序建表、重复对象幂等跳过，迁移后自动刷新统计（SQLite `PRAGMA optimize` / PG `ANALYZE`）；`SeedAsync` 事务内幂等种子；`ValidateSchemaAsync` 对照实体声明返回缺失列清单。

**零依赖核心**。PalORM.Core 不引用任何第三方 NuGet 包（仅 BCL + ADO.NET 抽象）。

> 完整功能特性清单（逐项源码锚点，含能力边界）见 [docs/功能特性.md](docs/功能特性.md)。

## 🆚 与主流 ORM 对比

> 版本基准：PalORM 6.6.0 / Dapper 2.1.89 / RepoDb 1.16.0（仓库基准套件实际引用版本）；EF Core 10.0.13（对比所引，NuGet 最新 10.x 稳定版）。单元格依据见下方注释。

| 特性                  | **PalORM**                                                   | Dapper                  | EF Core                               | RepoDb                     |
| ------------------- |:------------------------------------------------------------:|:-----------------------:|:-------------------------------------:|:--------------------------:|
| **Native AOT 全链路**  | ✓ 源生成验证                                                      | △ Dapper.Aot 可选（实验性拦截器） | ❌ 实验性，生产不推荐                           | ❌ 反射 + IL Emit             |
| **编译时类型诊断**         | ✓ 45 条（42 分析器 + 3 生成器），错误可跳转声明位置                             | ❌ 运行时失败                 | △ 迁移检查（设计时）                           | ❌ 运行时失败                    |
| **编译时 SQL 预构建**     | ✓ Roslyn 源生成                                                 | ❌ 运行时拼接                 | △ 预编译查询（实验性）                          | ❌ 运行时表达式树                  |
| **运行时反射**           | 零                                                            | △ 首次反射 + IL Emit 缓存     | △ 表达式树编译                              | ❌ 反射 + IL Emit             |
| **三方言批量策略**         | ✓ COPY / BulkCopy / 多值 / `= ANY` 数组                          | ❌ 手写多值 SQL              | △ Provider 各异                         | △ BulkInsert 仅 SQL Server  |
| **单语句多行 UPDATE**    | ✓ FROM VALUES / UPDATE JOIN VALUES ROW / CASE WHEN           | ❌                       | ❌ ExecuteUpdate 仅按 WHERE 单值           | ❌                          |
| **批量 UPSERT**       | ✓ `BulkMergeAsync`（ON CONFLICT / ON DUPLICATE KEY，冲突更新限定本租户） | ❌ 手写                    | ❌ 需 raw SQL                           | △ Merge 仅 SQL Server       |
| **乐观锁**             | ✓ `[ConcurrencyCheck]` 自动；批量单语句（PG/MySQL，能力不全回退 DbBatch） | ❌ 手写                    | ✓ `RowVersion` 自动                     | ❌ 手写                       |
| **软删除**             | ✓ `[SoftDelete]` 自动过滤（含批量删转 UPDATE）                          | ❌                       | ✓ 全局查询过滤器                             | ❌                          |
| **多租户列隔离**          | ✓ `[TenantAware]` 编译时；批量写跨租户护栏                               | ❌                       | △ 需手动实现                               | ❌                          |
| **值转换 + 枚举存储**      | ✓ `[Converter]` / 枚举三策略（int32/int64/文本）编译时发射                 | △ TypeHandler 手写        | ✓ ValueConverter / HasConversion（运行时） | △ TypeHandler              |
| **读副本路由**           | ✓ `ParallelReadScope` + 查询族 `readFromReplica`                | ❌                       | △ 需手动配置多上下文                           | ❌                          |
| **Schema 自动迁移**     | ✓ `MigrateAsync` + 并发竞态容错（重复对象即跳过）                           | ❌                       | ✓ Migrations（最全面）                     | △                          |
| **OwnedJson 编译时安全** | ✓ `[OwnedJson]` + 源生成                                        | ❌ 手写 STJ                | △ Owned Types（运行时）                    | ❌                          |
| **审计拦截器**           | ✓                                                            | ❌                       | ✓ Interceptors                        | ❌                          |
| **咨询锁**             | ✓ `pg_advisory_xact_lock`                                    | ❌                       | ❌                                     | ❌                          |
| **会话级 SET**         | ✓ `SessionSetupSql`                                          | ❌                       | ❌                                     | ❌                          |
| **SQL 文件嵌入**        | ✓ `[SqlFile]` 编译时校验                                          | ❌                       | ❌                                     | ❌                          |
| **断路器 + 重试**        | ✓ 内置（HalfOpen 探针槽位并发安全）                                      | ❌ 需 Polly               | △ 执行策略                                | ❌                          |
| **查询缓存**            | ✓ `WithCache` 有界缓存 + TTL + `EvictQueryCache` 收窄              | ❌                       | ❌ 需第三方拦截器                             | ✓ 原生 `cacheKey` 参数         |
| **流式消费**            | ✓ `QueryAsyncEnumerable` / `ForEachAsync`                    | △ 手写 CommandBehavior    | ✓ `AsAsyncEnumerable` 原生              | ❌                          |
| **可观测性（OTel）**      | ✓ `WithMetrics` / `WithTracing` 内置                           | ❌                       | △ 第三方 instrumentation 包               | ❌                          |
| **SQL 预览**          | ✓ `AsDryRun`（SQL + 参数）                                       | ❌                       | ✓ `ToQueryString`                     | △ 构建器层 `GetString`         |
| **悲观锁**             | ✓ `ForUpdate` / `ForShare` 链式                                | ❌ 手写 SQL                | △ 本体无内置（PG provider 扩展）               | ❌                          |
| **导航装配（对象图）**       | △ `Include` 生成 JOIN 不装配（micro-ORM 取舍，按父分组自行配对）               | △ multi-mapping 手写      | ✓ `Include` 完整装配 + 变更追踪               | ❌                          |
| **JSON 列查询**        | △ `WhereJson` JSONB 路径查询（PG 专有）；映射见上方 `[OwnedJson]`          | △ 手写 SQL                | ✓ EF7+ JSON 列映射 + 路径查询（EF10 增强）       | △ PropertyHandler 映射，无路径查询 |
| **CTE / 窗口函数**      | ✓ 链式 API                                                     | △ 原生 SQL 字符串            | △ LINQ 翻译（部分）                         | △ 原生 SQL                   |
| **多结果集**            | ✓ `GridReader`                                               | ✓ `QueryMultiple`       | ❌                                     | ✓ `ExecuteQueryMultiple`   |
| **Keyset 分页**       | ✓ `ToPageAsync`                                              | ❌                       | ❌                                     | ❌                          |
| **Scaffold 工具**     | ✓ 三 Provider                                                 | ❌                       | ✓ `dotnet ef dbContext scaffold`      | ❌                          |
| **连接串自动调优**         | ✓ PG 6 / MySQL 5 / SQLite 9 项                                | ❌                       | ❌                                     | ❌                          |
| **会话池化**            | ✓ `DataSessionPool` 内置（32 并发 P99 −18%）                       | ❌                       | ✓ `DbContextPool`                     | ❌                          |
| **存储过程**            | ✓ `StoredProc` 强类型输入/输出参数                                    | ✓ `DynamicParameters`   | △ `FromSqlRaw` 间接                     | ✓ `ExecuteQuery`           |
| **整事务瞬时错误重试**       | ✓ `WithTransactionRetry`（方言错误码重放，at-least-once）              | ❌                       | ✓ `EnableRetryOnFailure` 执行策略         | ❌                          |
| **数据库健康检查**         | ✓ `HealthCheckAsync`                                         | ❌ 手写 SELECT 1           | ✓ `CanConnect()`                      | ❌                          |
| **SQL 文件 / 模板资产化**  | ✓ `[SqlFile]` 方言条件段 + `[SqlTemplate]` 常量化，编译期校验              | ❌                       | ❌                                     | ❌                          |
| **SQL 定位注释**        | ✓ AutoTagging 自动注入「文件:行号 成员」（opt-in）                         | ❌                       | △ `TagWith()` 手动                      | ❌                          |
| **pgvector**        | ✓ 文本边界路线（`::vector` 写入，AOT 兼容）                               | ❌                       | △ 第三方扩展包                              | ❌                          |
| **种子数据**            | ✓ `SeedAsync` 事务内幂等 Upsert                                   | ❌                       | ✓ `HasData`（设计时）                      | ❌                          |
| **BulkInsert 内存**   | ≈ Dapper 的 19%                                               | 基线                      | 最高（ChangeTracker）                     | 中等                         |
| **核心包 NuGet 依赖**    | 零                                                            | 零                       | 高（多包拆分）                               | 中等                         |
| **目标框架**            | net11.0（单目标）                                                 | 多目标（netstandard2.0+）    | 多目标（net8+）                            | 多目标（netstandard2.0+）       |
| **许可证**             | AGPL-3.0-only                                                | Apache-2.0              | MIT                                   | Apache-2.0                 |

核心差异：编译时生成 + 全链路 AOT 兼容 + 三方言批量策略（COPY / BulkCopy / 多值 / 数组）。Dapper 快但运行时反射；EF Core 功能完整但运行时重、AOT 仍实验性；RepoDb 同为微 ORM 但无源生成，且批量仅 SQL Server。

<details>
<summary><b>对比依据（各单元格出处）</b></summary>

- **Dapper**：`Dapper.Aot`（独立包，[aot.dapperlib.dev](https://aot.dapperlib.dev)）通过 Roslyn interceptors 生成 AOT 拦截器，interceptors 是 C# 实验性特性，非默认启用。
- **EF Core 10**：LTS（[learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)）。`ExecuteUpdateAsync` 仅支持按 WHERE 单值更新，无法单 SQL 内对每行设置不同值；AOT 仍实验性（[issue #35945](https://github.com/dotnet/efcore/issues/35945)）；无内置 UPSERT API，`MERGE` 语义需 raw SQL。查询缓存需第三方拦截器（[EFCoreSecondLevelCacheInterceptor](https://github.com/VahidN/EFCoreSecondLevelCacheInterceptor)）；OTel 可观测性官方无一等内置指标，经 [OpenTelemetry.Instrumentation.EntityFrameworkCore](https://www.nuget.org/packages/OpenTelemetry.Instrumentation.EntityFrameworkCore)（第三方，Experimental）接入；悲观锁本体无内置（[多源证实](https://milanjovanovic.tech/blog/a-clever-way-to-implement-pessimistic-locking-in-ef-core)），PG 场景靠 Npgsql provider 的 `ForUpdate` 扩展且跨方言不一致；`ToQueryString` 自 EF Core 5+。
- **RepoDb**：BulkOperation 仅 SQL Server（[repodb.net/operation/bulkinsert](https://repodb.net/operation/bulkinsert)：*"It is only supporting the SQL Server RDBMS."*），其他方言走 packed statements；`Merge` 同属 BulkOperation 族，同受此限。查询缓存为操作级 `cacheKey` + 共享 `ICache` 实例（[官方博客](https://blogs.repodb.net/posts/2026/09/caching-in-repodb)）；SQL 预览仅有构建器层 `QueryBuilder.GetString()`（面向 `IStatementBuilder` 实现者），操作级无一等预览 API。无导航装配：[官方 README](https://github.com/mikependon/repodb) 对比表把 "Rich object graphs" 定位归于 EF，自身主张 "EF-like productivity without losing SQL control"。JSON 能力为 PropertyHandler 系列映射（`JsonToEntity/JsonDocument` 等，[core release notes](https://repodb.net/release/core)），无路径查询构建。
- **PalORM**：诊断计数 45 = 42 分析器 + 3 生成器（`docs/API参考.md`）；批量 UPSERT 租户护栏 = `BulkMergeAsync` 冲突更新限定本租户（2026-10 审计 P2 收口）；读副本路由 = 查询族 `readFromReplica` 参数 + `ParallelReadScope` 并行读作用域；会话池 = `DataSessionPool<TProvider>`（Create/RentAsync，归还复位瞬态状态、复用槽跨租期保留）；整事务重试 = `WithTransactionRetry` 按 PG 40001/40P01、MySQL 1213/1205、SQLite BUSY/LOCKED 瞬时错误码整事务重放；SQL 资产 = `[SqlFile]` 经 AdditionalFiles 进增量管线（改 SQL 免 Rebuild）+ `[SqlTemplate]` 配合 `AsPrepared()` 预编译；AutoTagging = C# `[InterceptsLocation]` 拦截 9 个查询终态方法（编译开关 opt-in 默认关闭）。

</details>

## 📊 性能

> **测试环境**：AMD Ryzen 9 8945HX（32 逻辑核）· Windows 10 22H2 · .NET 11 RC1 · SQLite 共享内存 · PG 18.6 / MySQL 8.4.11 本机容器 · Dapper 2.1.89。三臂（ADO.NET / Dapper / PalORM）同进程同轮采集、同连接口径，各臂使用其生态惯用的最优写法（ADO.NET 为手写性能地板）。
> 
> **数据批次**：2026-10-14 `PerfCli full` 完整批（`history-20261014-191424.json`，中位数 + 精确分配计数）。时延 = 全路径中位数；分配 = 精确计数（1024 进制）。共享 PG/MySQL 服务器空闲度存在 ±30% 批间波动，跨批只读比值不可直接外推；分配为确定性指标，跨批稳定。完整方法论与复现命令见 [docs/性能基准规范.md](docs/性能基准规范.md) 与 `bench/perfhub/report.html`（同批 BDN 门禁矩阵数字亦在该报告）。

**图例**（时延与分配共用，基准 = 手写 ADO.NET 地板；正 % = 比基准慢/费）：
🟢 强优 ≤−30% · 🟩 中优 −29%~−10% · 🔹 微优 −9%~−1% · 🔸 微劣 +1%~+9% · 🟧 中劣 +10%~+29% · 🟥 强劣 ≥+30% · **加粗** = 超出 1.3×/0.7× 显著带。

### 总表 · 跨方言 PalORM / ADO.NET 时延比（ADO 中位数 → PalORM 中位数）

| 操作          | 档   | SQLite                     | PostgreSQL                  | MySQL                       |
| ----------- | ---:|:--------------------------:|:---------------------------:|:---------------------------:|
| GetByKey    | 2K  | 9.1 µs→6.2 µs **0.68**     | 206 µs→199 µs 0.97          | 235 µs→225 µs 0.96          |
| GetByKey    | 20K | 22.2 µs→21.9 µs 0.99       | 204 µs→205 µs 1.00          | 176 µs→217 µs 1.23          |
| QueryAll    | 2K  | 961 µs→955 µs 0.99         | 654 µs→646 µs 0.99          | 1.05 ms→1.33 ms 1.27        |
| QueryAll    | 20K | 9.58 ms→9.81 ms 1.02       | 4.90 ms→4.91 ms 1.00        | 7.72 ms→7.98 ms 1.03        |
| StreamAll   | 2K  | 936 µs→1.00 ms 1.07        | 634 µs→636 µs 1.00          | 1.04 ms→1.40 ms **1.34**    |
| StreamAll   | 20K | 9.41 ms→9.95 ms 1.06       | 4.85 ms→4.84 ms 1.00        | 7.34 ms→8.44 ms 1.15        |
| Insert      | 2K  | 17.8 µs→17.2 µs 0.97       | 578 µs→543 µs 0.94          | 1.50 ms→1.64 ms 1.10        |
| Update      | 2K  | 7.2 µs→7.3 µs 1.01         | 604 µs→557 µs 0.92          | 199 µs→228 µs 1.14          |
| BulkInsert  | 2K  | 16.57 ms→16.26 ms 0.98     | 3.37 ms→3.43 ms 1.02        | 11.51 ms→10.92 ms 0.95      |
| BulkInsert  | 20K | 153.30 ms→152.23 ms 0.99   | 17.38 ms→17.31 ms 1.00      | 82.69 ms→87.62 ms 1.06      |
| BulkUpdate  | 2K  | 2.29 ms→2.28 ms 1.00       | 10.41 ms→4.26 ms **0.41**   | 28.07 ms→9.15 ms **0.33**   |
| BulkUpdate  | 20K | 25.08 ms→25.38 ms 1.01     | 104.02 ms→68.59 ms **0.66** | 281.47 ms→84.18 ms **0.30** |
| BulkDelete  | 2K  | 6.58 ms→5.12 ms 0.78       | 2.71 ms→1.55 ms **0.57**    | 8.99 ms→8.30 ms 0.92        |
| BulkDelete  | 20K | 65.95 ms→35.50 ms **0.54** | 24.02 ms→9.80 ms **0.41**   | 66.29 ms→64.08 ms 0.97      |
| UpsertBatch | 2K  | 15.96 ms→15.86 ms 0.99     | 11.18 ms→5.90 ms **0.53**   | 7.87 ms→7.84 ms 1.00        |
| UpsertBatch | 20K | 149.77 ms→148.63 ms 0.99   | 122.40 ms→71.64 ms **0.59** | 72.12 ms→76.57 ms 1.06      |

**读法**：批量写路径与 ADO 地板全方言持平或更优（0.78~1.06），其中三条方言专属快路径显著快于手写地板：MySQL 批量 UPDATE 凭 `UPDATE JOIN VALUES ROW` 快 3~3.4×，PG 批量族凭数组参数（`= ANY`）与 Binary COPY 快 1.5~2.4×，SQLite BulkDelete 池化复用命令与参数后快 1.9×；单行读 SQLite/PG 与地板持平（0.68~1.00）、MySQL 0.96~1.23（比值由 ADO 地板批间双峰摆动驱动，判读以 PalORM 绝对值为准）。MySQL 2K 档 QueryAll 1.27 / StreamAll 1.34 为多批复现的持续差距（ADO 地板稳定、非批内跳变，未归因，待专项复测）；10-07 批登记的 MySQL 20K 大结果集慢态（曾 +51~62%）已回常态带（1.03~1.15）。

### SQLite 细表 · 时延与分配（三臂对照）

**CRUD 单行与读**

| 操作                | 档   | ADO.NET | Dapper   | PalORM  | P/ADO      | P/Dapper   | 分配 A/D/P       | 分配 ΔADO（D/P） |
| ----------------- | ---:| -------:| --------:| -------:|:----------:|:----------:|:--------------:|:------------:|
| GetByKey          | 2K  | 9.1 µs  | 6.8 µs   | 6.2 µs  | **0.68🟢** | 0.91🔹     | 2.1/2.8/2.7 KB | +30% / +27%  |
| GetByKey          | 20K | 22.2 µs | 15.8 µs  | 21.9 µs | 0.99🔹     | **1.39🟥** | 2.1/2.8/2.8 KB | +32% / +30%  |
| QueryAll          | 2K  | 961 µs  | 1.34 ms  | 955 µs  | 0.99🔹     | 0.71🟩     | 308/513/293 KB | +66% / −5%   |
| QueryAll          | 20K | 9.58 ms | 13.51 ms | 9.81 ms | 1.02🔸     | 0.73🟩     | 3.3/5.3/2.9 MB | +61% / −11%  |
| StreamAll         | 2K  | 936 µs  | 1.37 ms  | 1.00 ms | 1.07🔸     | 0.73🟩     | 276/481/278 KB | +74% / +1%   |
| StreamAll         | 20K | 9.41 ms | 13.84 ms | 9.95 ms | 1.06🔸     | 0.72🟩     | 2.8/4.8/2.8 MB | +72% / +0%   |
| Insert            | 2K  | 17.8 µs | 21.1 µs  | 17.2 µs | 0.97🔹     | 0.82🟩     | 2.8/3.6/2.7 KB | +28% / −5%   |
| Update            | 2K  | 7.2 µs  | 9.0 µs   | 7.3 µs  | 1.01🔸     | 0.81🟩     | 2.6/3.3/3.2 KB | +30% / +24%  |
| InsertReturningId | 2K  | 26.7 µs | 26.6 µs  | 26.5 µs | 0.99🔹     | 1.00⚪      | 1.9/2.3/2.3 KB | +24% / +22%  |

**批量处理**

| 操作          | 档   | ADO.NET   | Dapper     | PalORM    | P/ADO      | P/Dapper   | 分配 A/D/P             | 分配 ΔADO（D/P） |
| ----------- | ---:| ---------:| ----------:| ---------:|:----------:|:----------:|:--------------------:|:------------:|
| BulkInsert  | 2K  | 16.57 ms  | 103.54 ms  | 16.26 ms  | 0.98🔹     | **0.16🟢** | 1.7/7.9/1.6 MB       | +373% / −6%  |
| BulkInsert  | 20K | 153.30 ms | 1043.71 ms | 152.23 ms | 0.99🔹     | **0.15🟢** | 14.9/79.4/14.7 MB    | +432% / −1%  |
| BulkUpdate  | 2K  | 2.29 ms   | 8.45 ms    | 2.28 ms   | 1.00⚪      | **0.27🟢** | 1.7/6.4/1.8 MB       | +280% / +4%  |
| BulkUpdate  | 20K | 25.08 ms  | 249.50 ms  | 25.38 ms  | 1.01🔸     | **0.10🟢** | 17.0/64.9/17.6 MB    | +282% / +4%  |
| BulkDelete  | 2K  | 6.58 ms   | 21.47 ms   | 5.12 ms   | 0.78🟩     | **0.24🟢** | 628 KB/1.6 MB/368 KB | +155% / −41% |
| BulkDelete  | 20K | 65.95 ms  | 213.55 ms  | 35.50 ms  | **0.54🟢** | **0.17🟢** | 6.1/15.6/2.6 MB      | +155% / −57% |
| UpsertBatch | 2K  | 15.96 ms  | 102.50 ms  | 15.86 ms  | 0.99🔹     | **0.15🟢** | 1.7/7.9/1.6 MB       | +379% / −4%  |
| UpsertBatch | 20K | 149.77 ms | 1027.81 ms | 148.63 ms | 0.99🔹     | **0.14🟢** | 14.8/79.5/14.7 MB    | +439% / −0%  |

批量四形态与 ADO 地板逐位持平（P/ADO 0.78~1.01），BulkDelete 凭命令与参数池化复用快于地板（满批只写 Value，20K 档 0.54）；Dapper 多值 INSERT 在 20K 档因巨型 SQL 字符串构造慢 6.8×、分配 5.3×。

**事务**

| 操作               | 档   | ADO.NET  | Dapper    | PalORM   | P/ADO      | P/Dapper   | 分配 A/D/P             | 分配 ΔADO（D/P） |
| ---------------- | ---:| --------:| ---------:| --------:|:----------:|:----------:|:--------------------:|:------------:|
| TxSingleInsert   | 2K  | 19.9 µs  | 19.8 µs   | 19.8 µs  | 0.99🔹     | 1.00⚪      | 4.0/4.8/4.4 KB       | +20% / +11%  |
| TxHundredInserts | 2K  | 183 µs   | 235 µs    | 235 µs   | 1.28🟧     | 1.00⚪      | 137/220/93.1 KB      | +61% / −32%  |
| TxBulkInsert     | 2K  | 16.51 ms | 103.14 ms | 16.16 ms | 0.98🔹     | **0.16🟢** | 1.7/7.9/1.6 MB       | +373% / −6%  |
| TxRollback       | 2K  | 572 µs   | 2.10 ms   | 816 µs   | **1.43🟥** | **0.39🟢** | 435 KB/1.6 MB/437 KB | +279% / +1%  |

### 专项测量

**Native AOT 发布体积**

| 方言         | exe 大小  | 发布目录  |
| ---------- |:-------:|:-----:|
| SQLite     | 4.5 MB  | 26 MB |
| PostgreSQL | 11.6 MB | 61 MB |
| MySQL      | 9.5 MB  | 47 MB |

### 查询构建的性能提示：表达式树提到静态字段

`OrderBy` / `Select` / `GroupBy` / `WhereIn` / `Set` / `Include` 等接收 `Expression<Func<T, ...>>`。C# 在调用点构造表达式树，库拿到时成本已付，无法在库内缓存；实测每棵树 512 字节加 0.5~1.6 µs。把 lambda 提到静态字段即可消除：

```csharp
// 每次调用都重建表达式树
await db.From<Order>().OrderBy(o => o.CreatedAt).ToListAsync();

// 表达式树只构造一次，之后复用
private static readonly Expression<Func<Order, DateTime>> ByCreatedAt = o => o.CreatedAt;
await db.From<Order>().OrderBy(ByCreatedAt).ToListAsync();
```

| 场景                    | 分配降幅       | 时间降幅   |
| --------------------- |:----------:|:------:|
| `UPDATE` + `Set(...)` | −18.0%     | −18.3% |
| 单行查询 + `OrderBy(...)` | −12.4%     | −7.2%  |
| `WhereIn(500)`        | −0.72%     | −1.7%  |
| 10K 行查询               | 被结果集摊薄到可忽略 | 同      |

查询次数多、单次行数少且热路径用到构建器方法时值得改；批量与报表型负载不必。`Where` / `OrWhere` / `Having` 接收 `FormattableString`，不构造表达式树，无需处理。

## 📦 安装

> 三个 Provider 包任选其一，每个都自动携带 `PalORM.Core`（运行时）与 `PalORM.SourceGen`（编译时源生成器）。
> 环境要求：.NET SDK `11.0.100-rc.2` 或更高（`global.json` 已锁定 `rollForward: disable`）；IDE 需支持 Roslyn 源生成器（SourceGen 以 Microsoft.CodeAnalysis 5.9.0 构建）。Native AOT 发布另需约 4 GB 内存（ILC 编译器），详见[开发](#-开发)。

```xml
<!-- PostgreSQL -->
<PackageReference Include="PalORM.PostgreSql" Version="6.6.0" />
<!-- MySQL -->
<PackageReference Include="PalORM.MySql" Version="6.6.0" />
<!-- SQLite -->
<PackageReference Include="PalORM.Sqlite" Version="6.6.0" />
```

每个 Provider 包含 `PalORM.Core`（运行时）和 `PalORM.SourceGen`（编译时源生成器）。安装后用下方快速开始的最小示例验证：能创建会话并完成一次插入即安装成功。

### 数据库兼容性

| 数据库        | 版本                         | 驱动                                     | 加密                       |
| ---------- | -------------------------- | -------------------------------------- |:------------------------:|
| PostgreSQL | 14+（推荐 18）                 | Npgsql 10.0.3                          | SSL/TLS                  |
| MySQL      | 8.0+（推荐 8.4 LTS）           | MySqlConnector 2.6.2                   | SSL/TLS                  |
| SQLite     | 3.54+（via SQLite3MC 2.6.1） | Microsoft.Data.Sqlite.Core 11.0.0-rc.2 | AES-256（驱动层，`Password=`） |

## 🚀 快速开始

> 五步跑通：定义实体 → 创建会话 → 建表种子 → CRUD → 批量操作。所有片段可直接组合成最小可运行程序。

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

**会话复用优先**：同一作用域（请求、批处理循环）内请复用一个 `db` 会话跑多次操作，而不是每操作新建。会话内同形状操作自动晋升命令复用槽（第 3 次起复用命令对象与参数槽），且避免每次重新 prepare——Microsoft.Data.Sqlite 的预备语句缓存按命令实例生效（官方源码确认：换命令即重新 `sqlite3_prepare_v2` 并额外分配一段 SQL 字节缓冲）。复用形态下每操作省下会话构造（约 632 B，2026-10-14 W3 后探针）与重复准备成本；单次即弃的写法（每操作 `CreateAsync`）适合低频调用，高频热路径建议复用。

### 建表与种子

```csharp
// 执行编译期生成的三方言建表 DDL（幂等：已存在对象自动跳过；FK 按依赖拓扑序建表）
await db.MigrateAsync();

// 种子数据：稳定主键事务内幂等 Upsert，重复执行不重复插入
await db.SeedAsync(new[] { new User { Id = 1, Email = "admin@example.com", CreatedAt = DateTime.UtcNow } });

// 对照实体声明检查数据库缺列（返回缺失列清单，升级/对账用）
List<string> missing = await db.ValidateSchemaAsync<User>();
```

### CRUD

```csharp
// 插入（PG/SQLite RETURNING / MySQL LAST_INSERT_ID 自增回填）
var user = await db.InsertAsync(new User { Email = "alice@example.com", CreatedAt = DateTime.UtcNow });

// 查询
var alice = await db.GetAsync<User>(user.Id);   // 单键直查：首选专用 API
var all = await db.From<User>().Where($"email LIKE {"%@example.com%"}").ToListAsync();

// 更新（乐观锁实体自动校验版本，冲突抛 ConcurrencyConflictException）
alice.Email = "new@example.com";
await db.UpdateAsync(alice);

// 插入或更新（默认主键走 Insert，非默认主键单行 UPSERT）
await db.SaveAsync(alice);

// 删除（软删实体自动转 UPDATE deleted_at，否则物理 DELETE）
await db.DeleteAsync<User>(alice.Id);
```

**单键直查用 `GetAsync`，不要用链式等价写法**：`GetAsync` 为单键设计（SQL 常量缓存、单行直读、不经查询构建器与列表物化），同口径实测比 `From<T>().Where(Id==x).FirstOrDefaultAsync()` 省约 880 B/操作且快 25~30%（2026-10-01 探针，3000 次摊销）。带过滤条件的查询才用 `From<T>()` 链式。

### 批量操作

```csharp
// 批量插入（PG: Binary COPY / MySQL: BulkCopy 或多值 INSERT / SQLite: 多值 INSERT）
await db.BulkInsertAsync(users);

// 批量更新（乐观锁实体：PG/MySQL 自动走版本化单语句，能力不全回退 DbBatch）
await db.BulkUpdateAsync(users);

// 批量更新（v5.0 单语句批量）
await db.BulkUpdateBatchAsync(users);

// 批量删除（PG: = ANY(数组) 单语句 / MySQL、SQLite: IN 分批单语句，满批命令与参数池化复用）
await db.BulkDeleteAsync<User>(keyList);

// 批量 UPSERT（多行 UPSERT 单语句；默认键新行走逐条 INSERT 回填自增 ID）
await db.BulkMergeAsync(users);
```

各方法按方言的 SQL 策略：

| 方法                     | PG                                 | MySQL                                             | SQLite                             |
| ---------------------- | ---------------------------------- | ------------------------------------------------- | ---------------------------------- |
| `BulkInsertAsync`      | Binary COPY                        | BulkCopy（local_infile）或多值 INSERT                  | 多值 INSERT                          |
| `BulkUpdateAsync`      | 版本化单语句 + 乐观锁（能力不全回退 DbBatch）         | 版本化单语句 + 乐观锁（能力不全回退 DbBatch）                   | 逐条 + 乐观锁                           |
| `BulkUpdateBatchAsync` | `FROM VALUES`                      | `UPDATE JOIN VALUES ROW`（8.0.19+，低版本回退 CASE WHEN） | 自动回退逐条                             |
| `BulkDeleteAsync`      | `= ANY(数组)` 单语句                    | `IN` 分批单语句                                        | `IN` 分批单语句                         |
| `BulkMergeAsync`       | 多行 UPSERT（`ON CONFLICT DO UPDATE`） | 多行 UPSERT（`ON DUPLICATE KEY UPDATE`）              | 多行 UPSERT（`ON CONFLICT DO UPDATE`） |

`BulkMergeAsync`（v5.6.0 集合化）按键状态分区：默认键（新行）走逐条 INSERT 保留 ID 回填契约，非默认键行按方言参数上限分批走多行 UPSERT（SQLite 999 / PG 与 MySQL 65535 参数上限，MySQL ODKU 单批 1000 行）；`[ConcurrencyCheck]` 实体维持逐条（UPSERT 无法尊重乐观锁）。

## 📖 使用

> 查询与分页、聚合与流式、事务与弹性、横切关注点、原生 SQL 与存储过程、Scaffold 工具、批量选型与形态选型。

### 查询与分页

`From<T>()` 返回 `struct QueryBuilder<T>`，链式 `.Where()` / `.OrderBy()` / `.Take()` / `.Skip()` / `.Select()` / `.GroupBy()` / `.Having()` / `.Include()` / `.ThenInclude()`。支持 `InnerJoin` / `LeftJoin` / `RightJoin`、`WhereIn` / `WhereNotIn`（自动分批）、CTE（`.With()`）、窗口函数、悲观锁（`ForUpdate` / `ForShare`）、SQL 预览（`AsDryRun`）、查询缓存（`WithCache`）、可观测性（`WithMetrics` / `WithTracing`，OTel 指标与 tracing）。

同会话并发执行多个查询前先声明并行读租约：

```csharp
await using (db.ForParallelReads())
{
    await Task.WhenAll(db.From<User>().ToListAsync(), db.From<Order>().ToListAsync());
}
```

> **并行读作用域**：`From<T>()` 查询族与 `GetAsync` 单行直查族在 `ForParallelReads()` 内都走作用域局部读连接池（各自独立连接，上限 8，查询结束归还）；与在飞只读操作并发的写/事务/Bulk 会被单活动门禁拒绝。流式消费大结果集用 `QueryAsyncEnumerable<T>`（`await foreach`），避免全量物化的内存峰值；`Include` 只生成 JOIN 不装配导航，需按父分组时自行配对或直接流式处理。

### 内存与 GC 选型

- **结果集形态**：`ToListAsync` 全量物化（`List<T>` 初始容量按会话内上次行数自适应，重复查询同表零扩容拷贝）；行数不可控的大表用 `QueryAsyncEnumerable<T>` 流式（每行即时消费，峰值内存 O(1)）。
- **查询缓存**：`WithCache` 按查询 opt-in（默认关）；未注入 `DbOptions.QueryCache` 时使用进程级共享默认实例（1024 条，多租户靠 `__t:{tenantIdLength}:{tenantId}:` 前缀隔离，IgnoreFilters 走独立 `__all__:`），需要按租户控制容量/TTL 时注入独立实例；TTL 最终一致，写后读一致用 `db.EvictQueryCache()` 显式收窄。
- **GC 模式**：高吞吐服务端（多核 + 大堆）建议 `<ServerGarbageCollection>true</ServerGarbageCollection>`（Gen0 分配缓冲更大、GC 频率降）；容器配 `GarbageCollectionHeapHardLimit`（如 75c00000=2GB）防OOM；客户端/小内存场景保持 Workstation 默认。基准口径为 Workstation（`docs/性能基准规范.md` §4.1），Server GC 下分配数字不变、GC 频率与暂停分布不同。

### Keyset 游标分页

大偏移量场景比 OFFSET 稳定，返回行列表与总数：

```csharp
// 第一页：按 CreatedAt 降序取 20 行
var (rows, total) = await db.From<Order>().ToPageAsync(20, o => o.CreatedAt);

// 下一页：上一页末行的排序值作游标
var next = await db.From<Order>().ToPageAsync(20, o => o.CreatedAt, lastValue: rows[^1].CreatedAt);
```

### 聚合五函数

软删除与租户过滤自动生效：

```csharp
long count = await db.CountAsync<Order>($"status = {OrderStatus.Paid}");
decimal sum = await db.SumAsync<Order>(o => o.Amount);
double avg = await db.AvgAsync<Order>(o => o.Amount);
DateTime? latest = await db.MaxAsync<Order, DateTime>(o => o.CreatedAt);
```

### 流式消费大结果集

两种形态都不物化整表 `List<T>`，峰值内存 O(1)：

```csharp
// 回调式：逐行消费，不建列表
await db.From<Order>().ForEachAsync(order => sink.Write(order));

// 异步枚举式：await foreach 消费原生 SQL 结果
await foreach (var order in db.QueryAsyncEnumerable<Order>($"SELECT * FROM orders WHERE status = {status}"))
    sink.Write(order);
```

### 多结果集（`GridReader`）：

```csharp
await using var grid = await db.From<User>()
    .QueryMultipleAsync($"SELECT * FROM users WHERE id = {userId}; SELECT * FROM orders WHERE user_id = {userId}");
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

### 事务级瞬时错误重试

（整事务按方言错误码重放，at-least-once 契约：PG 40001/40P01、MySQL 1213/1205、SQLite BUSY/LOCKED）；保存点与外部事务接入：

```csharp
// 事务重试：死锁/锁超时整事务重放（回调须幂等）
await db.WithTransactionRetry(async ct => await db.BulkInsertAsync(items, ct: ct));

// 保存点：长事务内部分回滚
await using var tran = await db.BeginTransactionAsync();
await db.InsertAsync(order);
await db.SavepointAsync(tran, "sp1");
await db.ExecuteAsync($"UPDATE ...");           // 失败则只回滚到 sp1
await db.RollbackToAsync(tran, "sp1");
await tran.CommitAsync();

// 外部事务接入（外部 Dispose 后本会话续用会响亮失败，不静默自动提交）
db.UseTransaction(externalTran);

// 隔离级别：会话级设定，后续 BeginTransactionAsync 默认取用
db.WithIsolationLevel(IsolationLevel.ReadCommitted);
await using var tran = await db.BeginTransactionAsync();
```

弹性策略（`WithRetry` 指数退避 + `WithCircuitBreaker` 熔断）自 v5.4 起自动覆盖只读查询管线：

```csharp
// Production 预设已含弹性（MaxRetries=5 / 熔断阈值 10 / 半开 60s / 作用域 Process）
await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(
    DbOptions.Production(connectionString));

// 需要自定义阈值时在会话上链式覆盖（方法在 DataSession 上，返回同一会话）
db.WithRetry(maxRetries: 3)
  .WithCircuitBreaker(failureThreshold: 5, resetAfter: TimeSpan.FromSeconds(30));

// SELECT 瞬时故障（死锁/超时/连接闪断）自动重试；非幂等写入不自动重试，显式声明弹性意图：
long affected = await db.ExecuteWithResilience(
    token => db.From<Order>()
        .Set(o => o.Status, OrderStatus.Paid)
        .Where($"id = {orderId}")
        .ExecuteNonQueryAsync(token), ct);
```

| 路径                                               | 弹性覆盖 | 说明                                            |
| ------------------------------------------------ |:----:| --------------------------------------------- |
| `From<T>()` SELECT 族、`GetAsync`/`GetAllAsync`、聚合 | 自动   | 瞬时故障按配置重试并计入熔断                                |
| 连接建立                                             | 自动   | `CreateAsync` 自有重试循环                          |
| 写入路径（Insert/Update/Delete/Save/Bulk/存储过程）        | 直连   | 非幂等写自动重试有重复执行风险；显式需求用 `ExecuteWithResilience` |
| 事务内查询 / `ToPageAsync` / 原始 SQL 家族                | 直连   | 事务内重试以次生异常掩盖根因                                |

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

其余横切能力：`[SoftDelete]` 自动 WHERE 过滤、`[TenantAware]` + `WithTenant(id)` 单库列隔离（缓存 key 自动加 `__t:{tenantIdLength}:{tenantId}:` 前缀，`IgnoreFilters()` 走独立 `__all__:` 命名空间）、`[ConcurrencyCheck]` 乐观锁、`ForRead` 读写分离。全部注解（23 个）与执行方法明细见 [docs/API参考.md](docs/API参考.md)。

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

`[SqlTemplate]` 把方法体插值串提升为模板常量（方法须无参非泛型，插值洞只引用静态可解析符号），重复执行的 SQL 免每次字符串构造，配合查询构建器 `.AsPrepared()` 走 `DbCommand.PrepareAsync` 预编译：

```csharp
public static class UserQueries
{
    [SqlTemplate("ActiveUsers")]
    public static FormattableString ActiveUsers()
        => $"SELECT * FROM users WHERE active = 1 AND deleted_at IS NULL";
}

// 模板方法返回 FormattableString，直接交给原生 SQL 族执行
var users = await db.QueryAsync<User>(UserQueries.ActiveUsers());
```

PG 专有：`WhereJson`（JSONB 路径查询）、`PgNotificationListener`（NOTIFY/LISTEN 异步监听，自动重连）、`NotifyAsync`（参数化 `pg_notify()` 发送）。

### 存储过程与健康检查

```csharp
// 存储过程：强类型输入/输出参数，过程名白名单校验，single-use 契约
var sp = db.StoredProc("sp_transfer_stock")
    .WithParam("from_sku", fromSku)
    .WithParam("qty", qty)
    .WithOutputParam<int>("rows_affected");
int affected = await sp.ExecuteAsync(ct);            // 返回受影响行数
int? moved = sp.GetOutputValue<int>("rows_affected"); // 输出参数强类型取回

// 存储过程结果集直接物化
List<StockRow> rows = await sp.QueryAsync<StockRow>(ct);

// 健康检查：SELECT 1 探活（错误消息只透出异常类型名，防数据库拓扑泄露）
HealthResult health = await db.HealthCheckAsync();

// 唯一键冲突跨方言统一翻译（不用记各驱动错误码）
try { await db.InsertAsync(user); }
catch (UniqueConstraintViolationException) { /* 邮箱已注册 */ }

// 乐观锁冲突（[ConcurrencyCheck] 实体 UpdateAsync 版本不匹配时）
try { await db.UpdateAsync(order); }
catch (ConcurrencyConflictException) { /* 重新读取合并 */ }
```

需要驱动原生能力时 `db.GetRawConnection()` 返回底层 `DbConnection` 逃生舱。

### Scaffold 工具

从既有数据库反向生成实体：

```bash
dotnet run --project tools/PalORM.Scaffold -- <connection-string> --dialect sqlite|pg|mysql [--namespace NS] [--output DIR]
```

三 Provider schema → C# 实体，40+ 类型映射（`uuid` → `Guid`、`jsonb` → `string`、`bytea` → `byte[]`、`date` → `DateOnly`、`time` → `TimeOnly`）。

### 批量 API 选型速查

| 场景               | 用                                          | 不用                     | 理由                                                      |
| ---------------- | ------------------------------------------ | ---------------------- | ------------------------------------------------------- |
| 批量插入，不需要 ID 回填   | `BulkInsertAsync`                          | 逐条 / `SessionBatch`    | 方言最快路径（COPY / LOAD DATA / 多值），实测事务内 100 行比逐条快 8.4×      |
| 批量插入且需逐行回填自增 ID  | `BulkMergeAsync`（默认键走逐条回填）或 `SessionBatch` | `BulkInsertAsync`      | COPY / LOAD DATA 不回填 ID                                 |
| 混合语句或需逐条语义控制     | `SessionBatch`（DbBatch 打包）                 | 逐条往返                   | 单往返打包多语句，SQLite 20 条/批                                  |
| 按非主键列批量更新（每行不同值） | `BulkUpdateBatchAsync`                     | 逐条                     | 单语句多行 SET（PG `FROM VALUES` / MySQL `JOIN VALUES ROW`）   |
| 乐观锁实体批量更新        | `BulkUpdateAsync`                          | `BulkUpdateBatchAsync` | 版本化单语句（PG UNNEST / MySQL JOIN VALUES ROW）逐行校验版本；能力不全回退 DbBatch |
| 插入或更新（键存在则更新）    | `BulkMergeAsync`                           | 手写 ON CONFLICT         | 租户实体自动限定本租户（跨租户命中 0 行）                                  |
| 大键集删除（万级以上）      | `BulkDeleteAsync`                          | 逐条 Delete              | 自动按方言参数上限分批（SQLite 999 / MySQL·PG 数千），整批单事务，满批命令与参数池化复用 |

### 按形态选 API：会话池与批量路径

同一 API 在不同使用形态下的表观开销构成不同，按形态选对入口的收益大于任何库内微优化。

| 场景 | 入口 | 关键收益 |
|---|---|---|
| 一请求一会话（Web/API） | `DataSessionPool.RentAsync` | 复用槽跨租期激活；租还约 1 µs |
| 高吞吐逐行写 | `BulkInsertAsync` / `BulkUpdateBatchAsync` | 会话/命令成本摊到 1/N |
| 大结果集 / 无界流 | `QueryAsyncEnumerable<T>` | 恒定内存，每行分配更低 |
| 单键查询 | `GetAsync` | 直查槽 + 单行物化 |
| 分页 | `ToPageAsync` | 单往返分页（PG COUNT+页 SELECT 打包） |

下面按场景展开；批量选型见上方“批量 API 选型速查”。

**一请求一会话（Web/API 服务）用会话池**（v6.5 `DataSessionPool`，API 细节见[API 参考 · 会话池](docs/API参考.md)）：

```csharp
// 启动时创建一次（maxIdleSize 按峰值并发调整）
var pool = DataSessionPool<PostgreSqlProvider>.Create(options, maxIdleSize: 16);

// 每请求：租出即用，DisposeAsync 即归还
await using var db = await pool.RentAsync();
var user = await db.GetAsync<User>(id);
```

归还时瞬态状态（租户/超时/重试等 `With*` 覆写、拦截器加项）全部复位，命令复用槽、读连接、物化容量跨租期保留——复用槽在"一请求一会话"形态下不经池化永不激活（同形状第 3 次操作才晋升）。32 并发压测实测：P99 −18%、Gen0 回收 −58%、每操作分配 −2576 B；池化形态单键直查 P/ADO 比值 MySQL 0.99 / PG 0.87（台账批），租还自身约 1 µs。

DI 容器场景把 `IDataSessionFactory<TProvider>` 注册为单例即可，不依赖额外容器包：

```csharp
var factory = new DataSessionFactory<PostgreSqlProvider>(options, maxIdleSize: 16);
services.AddSingleton<IDataSessionFactory<PostgreSqlProvider>>(factory);

// 每请求（scoped 服务的构造/方法内）
await using var db = await factory.RentAsync(ct);
```

**高吞吐逐行写换批量 API，不要在逐条循环里找损耗**。逐条 `UpdateAsync` 每行各付一次会话门禁与命令往返；合成单语句多行（`BulkUpdateBatchAsync`）或走批量路径（见上方速查表）后，这部分成本摊薄到 1/N。逐条循环里的每行开销是形态决定的，不是可挤的库内水分。

**长会话顺序循环的对比数字要读连接态**。基准中"每操作新建会话"与"长会话复用"的表观差距主要来自连接/命令冷态与驱动侧成本：库侧单键执行成本已与 ADO 地板重合（见[性能](#-性能)），评估自身业务收益时以同形态对照为准。

## ✅ 最佳实践速查

按使用频率收拢，详细依据见各链接位置：

**会话与连接**

- 同一作用域复用一个 `DataSession`，不要每操作 `CreateAsync`（会话内同形状操作第 3 次起自动复用命令与参数槽）
- Web/API"一请求一会话"部署用 `DataSessionPool`（启动建池、`RentAsync` 租用、`DisposeAsync` 归还）：复用槽跨租期保留是池化的核心收益，不池化则该形态下永不激活
- 启动期预热连接池 `PreWarmAsync`（远程建连实测约 8.5 ms/条），配合 `MinPoolSize` 保持暖态
- 高频单键查询用 `GetAsync`（比链式等价写法快 25~30%、省 880 B）；带过滤条件才用 `From<T>()`
- 只读副本配置 `ReadConnectionString` 后，报表/列表查询显式 `.ForRead()`，主从延迟敏感的强读留在主库
- 并发查询先声明 `await using (db.ForParallelReads())`；作用域内 `GetAsync` 与查询族走作用域局部读连接池（上限 8）；与在飞读并发的写操作会被门禁拒绝
- 跨程序集模型报 "not registered"：各模型程序集经 `ModuleInitializer` 在首次被触达时注册，确保实体类型被真实引用（类型实例化/成员调用均算触达）

**查询与结果集**

- 行数不可控的大表用 `QueryAsyncEnumerable<T>` 流式消费（峰值内存 O(1)），不要 `ToListAsync` 全量物化
- 深翻页用 `ToPageAsync` 游标（Keyset），不要大 OFFSET（扫描量随偏移线性增长）
- 大键集过滤用 `WhereIn`/`WhereNotIn`（自动按 500 键分批、空集安全恒假；总绑定参数仍受方言上限约束，SQLite 999 / 通用 65535，超限会提前抛 `ArgumentException`，更大的键集用临时表 JOIN）
- 热路径的 `OrderBy`/`Select` 表达式提到静态字段（每棵树 512 B + 0.5~1.6 µs 的调用点成本可消除）
- 排查 SQL 用 `AsDryRun()`/`ToSql` 预览（SQL + 参数，不执行）；生产定位慢查询可开 AutoTagging（`PalORMAutoTagging` 编译开关，SQL 自动注入「文件:行号 成员」注释）
- `Include`/`ThenInclude` 只生成 JOIN 不装配导航（按父分组自行配对）；列裁剪物化用 `[Projection]` DTO + `QueryAsync<T>`（大结果集用 struct 投影 + `QueryStructsAsync<T>`；`Select` 投影仅 DryRun/ToSql 支持）
- `WithCache` 默认关；启用后写后读一致用 `db.EvictQueryCache()` 显式收窄

**写入与批量**

- 批量 API 按上方选型速查表选择；不要在循环里逐条 `InsertAsync`（连接往返是数量级差异）
- 唯一键冲突捕获 `UniqueConstraintViolationException`（跨方言统一翻译，不用记驱动错误码）；乐观锁冲突捕获 `ConcurrencyConflictException`（重读数据合并后重试）
- `[ConcurrencyCheck]` 实体不要调 `BulkUpdateBatchAsync`（显式单语句 API 不承载逐行版本谓词，PALORM031 编译期已拦），批量更新走 `BulkUpdateAsync`（自动路由到版本化单语句，能力不全回退 DbBatch）
- `BulkInsertAsync` 不回填自增 ID（COPY / LOAD DATA 契约）；需逐行回填用 `BulkMergeAsync`（默认键走逐条）或 `SessionBatch`
- 可能存 NULL 的列声明可空属性（`string?`），非可空遇 NULL 会响亮抛 `SqlNullValueException`（不出静默 null）
- 敏感列标 `[SensitiveData]`（`Set()` 写入该列的参数值在审计日志中掩码；Where 手写 SQL 的洞值无列映射，不掩码）；非幂等写不要指望自动重试（写入路径直连语义），确需弹性用 `ExecuteWithResilience` 包裹并自行评估重复执行风险

**事务**

- 事务内批量优先 `BulkInsertAsync` 而非 `SessionBatch`（同事务 100 行 5.4ms vs 16.8ms）
- 事务内查询 / 原始 SQL 不自动重试（重试会以次生异常掩盖根因）；确需事务级容错用 `WithTransactionRetry`（方言瞬时错误码整事务重放），回调必须幂等（at-least-once 契约）
- 不支持嵌套事务（重复 `BeginTransactionAsync` 响亮抛异常）；长事务内部分回滚用保存点 `SavepointAsync`/`RollbackToAsync`
- 外部事务经 `UseTransaction` 接入后，外部已 Dispose 再用本会话会响亮失败（不静默自动提交）

**多租户与横切**

- `[TenantAware]` 实体会话创建后立即 `WithTenant(id)`，不要延后到首个查询（缓存 key 前缀随租户切换失效）
- `InsertAsync`/`BulkInsertAsync`/`BulkMergeAsync` 不附加租户谓词，`tenant_id` 必须由调用方赋值（PALORM040 只拦可空列）；租户列在 Update/BulkUpdate 与 UPSERT 冲突更新中不可写（防跨租户搬移），确需迁移用原生 SQL
- 需要跨租户管理查询时用 `IgnoreFilters()`；该调用把会话切到全量视图且当前无公开复位 API，管理查询结束请换一个会话（池化会话归还后重租即可），不要在同一个会话里继续做租户过滤查询
- `AuditInterceptor` 覆盖面 = 实体 SELECT 管线 / QueryBuilder UPDATE / `ExecuteAsync`；`InsertAsync`/`DeleteAsync`/Bulk 族/存储过程不产生审计记录，完整写入审计用数据库层审计或 OpenTelemetry
- PG 连接串调优 `NoResetOnClose=true` 下 raw SQL 的 `SET`/临时表跨池租客可见（ITM-652）：需要隔离时用独立连接（`Max Pool Size=1`）或显式 `DISCARD`

**AOT 与部署**

- `[OwnedJson]` 对象形态必须配 System.Text.Json 源生成 `JsonSerializerContext`（AOT 安全的硬要求，缺失编译期报 PALORM008-010）
- 高吞吐服务端开 `<ServerGarbageCollection>true</ServerGarbageCollection>`；容器配 `GarbageCollectionHeapHardLimit` 防 OOM（Server GC 下分配数字不变、GC 频率与暂停分布不同）
- 只部署单方言时可设 MSBuild 属性 `PalORMTargetDialects` 裁剪 CRUD SQL 发射面（DDL 恒全方言）；AOT 发布需约 4 GB 内存（ILC 编译器）

**运维与排查**

- 生产用 `DbOptions.Production`（已含弹性/连接池预设），只覆盖确需自定义的项
- 一请求一个 `DataSession` 的短会话部署：`Production` 预设已默认 `CircuitBreakerScope.Process`（熔断跨会话累计，数据库持续故障时快速失败）；自建 `DbOptions` 的等价部署请显式设 `CircuitBreakerScope.Process`，Session 级计数随会话销毁、短会话下阈值几乎不可能达到
- 健康检查端点接 `HealthCheckAsync`（SELECT 1 探活，错误消息只透出异常类型名，防数据库拓扑泄露）
- 可观测性接 OTel：`WithTracing` + `WithMetrics`（指标族 `palorm.query.executions`/`palorm.query.duration`，查询缓存另有 `palorm.cache.*` 四指标）
- 超时异常带 `PalORM.InfrastructureTimeout` 标记的属基础设施侧（网络/服务器），先查环境再查代码；瞬时故障判定用 `Data["PalORM.IsTransient"]` 标记
- MySQL 批量写报 `ER_NET_PACKET_TOO_LARGE` 先查服务端 `max_allowed_packet`；LOAD DATA 回退多值 INSERT 是 `local_infile` 关闭时的预期行为（非错误）

## 🔧 配置

> 三预设起步（Development / Production / Testing），20 项配置按需覆盖；三方言连接串自动调优仅补默认、不覆盖显式设置。

### 预设配置

```csharp
var dev  = DbOptions.Development(connectionString);            // 开发环境
var prod = DbOptions.Production(connectionString, readConn);  // 生产（含连接池 + 读写分离）
var test = DbOptions.Testing(connectionString);               // 测试（零重试 + 短超时）
var env  = DbOptions.FromEnvironment("PALORM_CONNECTION");    // 环境变量（Docker/K8s 友好）
```

`FromEnvironment` 支持的环境变量：`PALORM_CONNECTION`（必需）、`PALORM_READ_CONNECTION`、`PALORM_COMMAND_TIMEOUT`、`PALORM_CONNECTION_TIMEOUT`、`PALORM_MAX_RETRIES`、`PALORM_CIRCUIT_BREAKER_THRESHOLD`、`PALORM_MAX_POOL_SIZE`；组装后自动 `Validate()`，非法值在启动期报错。

### 配置项

| 属性                         | 类型                                  | 默认值           | 说明                                                                                                                                                        |
| -------------------------- | ----------------------------------- |:-------------:| --------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ConnectionString`         | `string`（必需）                        | —             | 主库连接串。支持 `$ENV:VAR_NAME` 环境变量引用                                                                                                                           |
| `ReadConnectionString`     | `string?`                           | null          | 只读副本连接串。配置后 `ForRead()` 自动路由                                                                                                                              |
| `ConnectionTimeout`        | `TimeSpan`                          | 15s           | 连接建立超时（含重试），超时抛 `TimeoutException`                                                                                                                        |
| `CommandTimeout`           | `TimeSpan`                          | 30s           | 命令执行超时；亚秒值向上取整为 1 秒                                                                                                                                       |
| `MaxRetries`               | `int`                               | 3             | 瞬时故障最大重试次数，0=禁用，上限 10（超出 `Validate()` 抛错）。覆盖只读查询管线；写入不自动重试                                                                                                |
| `RetryBackoff`             | `Func<int, TimeSpan>?`              | 100→200→400ms | 默认退避含 50~100% 抖动；自定义重试间隔（参数=重试次数），负值抛异常                                                                                                                   |
| `OverallDeadline`          | `TimeSpan`                          | Zero（不设）      | 整次操作总预算：到期停止重试并抛带 `PalORM.InfrastructureTimeout` 标记的 `TimeoutException`。不设时最坏墙钟 ≈ (MaxRetries+1)×CommandTimeout+Σ退避（默认配置约 120 秒）；取值应大于单次 `CommandTimeout` |
| `MaxPoolSize`              | `int`                               | 100           | 连接池上限。SQLite 忽略（嵌入式库无服务端池）                                                                                                                                |
| `MinPoolSize`              | `int`                               | 0             | v5.6.0。0=不覆盖驱动默认；正数透传，空闲修剪后池内至少保留这么多暖连接，消除突发查询重建连接的延迟尖峰。SQLite 忽略                                                                                         |
| `PoolIdleTimeoutSeconds`   | `int`                               | 0             | v5.6.0 起默认 0（更早为 30）。0=保留驱动默认（Npgsql 300s / MySqlConnector 180s）；正数才覆盖，代价是空闲超时后首个查询需重建物理连接（实测跨网段 `SELECT 1` 池内 0.3ms vs 新建 13.5ms）                        |
| `PoolLifetimeMinutes`      | `int`                               | 60            | 连接最大生命周期，到期强制重建                                                                                                                                           |
| `CircuitBreakerThreshold`  | `int`                               | 5             | 连续失败次数阈值，0=禁用熔断                                                                                                                                           |
| `CircuitBreakerResetAfter` | `TimeSpan`                          | 30s           | 熔断后进入半开的等待时间                                                                                                                                              |
| `CircuitBreakerScope`      | `CircuitBreakerScope`               | Session       | 熔断作用域：Session 的失败计数随会话销毁（长会话可用）；Process 按 (Provider, 连接串, 阈值, 冷却) 跨会话累计，一请求一会话的短会话部署必选，否则熔断形同虚设。`Production` 预设已默认 `Process`                              |
| `Interceptors`             | `IReadOnlyList<IQueryInterceptor>?` | null          | 按 `Priority` 升序执行（`AuditInterceptor` 默认 200）                                                                                                              |
| `ValidateQueryColumnOrder` | `bool`                              | true          | `QueryAsync` 首行列序与实体声明序比对，不匹配抛异常；用列别名/表达式列时需关闭                                                                                                            |
| `UseSynchronousRowRead`    | `bool`                              | false         | 全量物化逐行读走同步 `Read()`（MySQL 需开启；SQLite 因进程内引擎恒同步）。MySQL 实测约 998 vs 1846~2043 ns/行；流式 API 始终异步。高并发服务端请先做线程池压力验证                                   |
| `QueryCache`               | `IQueryCache?`                      | 1024 条        | 默认进程级共享；注入独立实例实现会话/租户级隔离（需线程安全）                                                                                                                           |
| `SessionSetupSql`          | `string?`                           | null          | 主连接首次激活后执行的 SQL（`SET TIME ZONE` / `search_path` 等，分号分隔）                                                                                                   |
| `ReadSessionSetupSql`      | `string?`                           | null          | 读副本连接的同类设置；读连接按会话复用，每会话执行一次                                                                                                                               |
| `LoggerFactory`            | `ILoggerFactory?`                   | null          | 设置后 `DataSession` 创建 `ILogger`                                                                                                                            |

> **SQLite 并发写扩展性**（2026-09-21 实测）：80/20 读写混合负载下，SQLite 从 1 到 8 线程吞吐下降 69%（WAL 单写者模型，裸 ADO.NET 对照同为 −69%，非 PalORM 引入的锁）；同期 PostgreSQL +7.2×、MySQL +4.6×。高并发写场景选 PG/MySQL，SQLite 适合读密集或低并发写的嵌入式场景。详见 [docs/架构设计.md](docs/架构设计.md)。

### 连接串自动调优

`CreateConnection` 时自动调优，仅当用户未显式设置时覆盖默认值。

**PostgreSQL**（6 项）：

| 参数                     | 默认 → 调优值     | 收益                                   |
| ---------------------- |:------------:| ------------------------------------ |
| `MaxAutoPrepare`       | 0 → 100      | 自动预编译，查询延迟 −30~50%                   |
| `AutoPrepareMinUsages` | 5 → 2        | 第 2 次执行起 Prepare                     |
| `NoResetOnClose`       | false → true | 归还连接跳过 DISCARD ALL，+30% localhost 吞吐 |
| `ReadBufferSize`       | 8192 → 16384 | 大结果集吞吐                               |
| `WriteBufferSize`      | 8192 → 16384 | 大值写入吞吐                               |
| `Enlist`               | true → false | 跳过 TransactionScope 检查               |

**MySQL**（5 项）：

| 参数                      | 默认 → 调优值             | 收益                      |
| ----------------------- |:--------------------:| ----------------------- |
| `AutoEnlist`            | true → false         | 跳过 TransactionScope     |
| `ConnectionReset`       | true → false         | 跳过 COM_RESET_CONNECTION |
| `CancellationTimeout`   | 2 → 5                | 防连接泄漏                   |
| `AllowLoadLocalInfile`  | false → true         | MySqlBulkCopy 前提        |
| `ServerRedirectionMode` | Disabled → Preferred | Azure MySQL 直连          |

**SQLite PRAGMA**（9 项）：

| PRAGMA               | 默认 → 调优值         | 收益                                                                                                    |
| -------------------- |:----------------:| ----------------------------------------------------------------------------------------------------- |
| `busy_timeout`       | 0 → 5000ms       | 并发写 BUSY 在引擎内等待                                                                                       |
| `synchronous`        | FULL → NORMAL    | WAL 下安全，减少 fsync                                                                                      |
| `cache_size`         | 2MB → 64MB       | 读密集型提升                                                                                                |
| `temp_store`         | DEFAULT → MEMORY | 临时表走内存                                                                                                |
| `wal_autocheckpoint` | 1000 → 1000      | 显式固定防漂移                                                                                               |
| `journal_size_limit` | -1 → 64MB        | 防 WAL 无界膨胀拖慢检查点                                                                                       |
| `mmap_size`          | 0 → 256MB        | 文件库 I/O 加速（`:memory:` 跳过）                                                                             |
| `analysis_limit`     | 1000 → 400       | 约束 optimize/ANALYZE 采样成本                                                                              |
| `optimize`           | 0 → 0x10002      | 连接打开时自动统计优化（官方对长生命周期连接的推荐形态，采样成本由 `analysis_limit` 约束）；仅文件库宽分支——内存库无持久统计面，只读连接写统计面会 `SQLITE_READONLY` |

`NoResetOnClose=true` 的会话状态泄漏取舍（raw SQL 的 SET/临时表跨池租客可见）见 ITM-652：需要隔离时用独立连接（连接串 `Max Pool Size=1`）或显式 `DISCARD`。

### 进阶配方

**SQLite**（经 `DbOptions.SessionSetupSql` 执行，可覆盖上述默认）：

| 场景        | 配方                            | 说明                                                        |
| --------- | ----------------------------- | --------------------------------------------------------- |
| 批量/大行负载建库 | `PRAGMA page_size=16384`      | 须在库首次创建前生效；既有库静默 no-op                                    |
| 读密集       | `PRAGMA mmap_size=1073741824` | 提至 1GB（引擎上限 2GB−64KB）                                     |
| 删除密集      | `PRAGMA secure_delete=OFF`    | 消除删页覆写写放大；已删内容不再清零，加密库上有 forensic 残留                      |
| 本地弹性直通    | `MaxRetries = 0`              | 本地 BUSY 已由 `busy_timeout` 吸收，弹性机构在 RTT≈0 的本地库是净开销；远程库维持默认 |

**PostgreSQL**（连接串层显式启用，各有触发条件与取舍）：

| 场景         | 配方                                          | 说明                                                                                       |
| ---------- | ------------------------------------------- | ---------------------------------------------------------------------------------------- |
| 本机/同容器 PG  | `Host=/var/run/postgresql`                  | Unix domain socket 替代 TCP（Npgsql 官方性能文档）；`Host` 以斜杠或盘符开头即按 socket 目录解析                   |
| GSS 协商长尾削峰 | `GssEncryptionMode=Disable`                 | 实测（2026-09-26，Npgsql 10.0.3 × PG 18.4）中位建连不变，长尾 149ms → ≤21ms；安全策略变更，仅服务端不要求 GSS 加密时使用   |
| 非关键表批量写    | 事务内首条 `SET LOCAL synchronous_commit TO off` | PG 官方 28.4：短事务吞吐的最大应用层杠杆；崩溃丢最近约 600ms 提交（不损数据）。事件日志/缓存类可接受，账务类不可；须显式 opt-in，PalORM 不默认开启 |

**MySQL**（服务端/DBA 决策或显式操作）：

| 场景               | 配方                                                     | 说明                                                                                                     |
| ---------------- | ------------------------------------------------------ | ------------------------------------------------------------------------------------------------------ |
| 计划器统计刷新          | `ANALYZE TABLE 表名`（经 `ExecuteAsync`）                   | InnoDB 默认 10% 行数变化自动重算，多数场景无需手工；MigrateAsync 不自动跑                                                      |
| 批量写提交削峰          | `innodb_flush_log_at_trx_commit=2`（服务端全局变量）            | 提交只写 OS 缓存不 fsync；崩溃丢最近约 1 秒提交，判据同 PG `synchronous_commit=off`                                         |
| 锁等待              | 无需配置                                                   | `innodb_lock_wait_timeout`（默认 50s）+ PalORM 弹性重试（死锁 1213 / 锁超时 1205）两层兜底                                |
| 批量写报文上限          | 服务端 `max_allowed_packet` ≥ 16MB                        | LOAD DATA 与大参数批单包可达数 MB，超限报 `ER_NET_PACKET_TOO_LARGE` 或静默断连；报此错先查服务端值                                  |
| 批量写 LOAD DATA 开关 | 服务端 `local_infile=ON`                                  | 缺一即回退多值 INSERT（检测按连接缓存 60s）；本机实测 LOAD DATA 比多值 INSERT 快 1.39×，远程库差距更大（官方基准 4~5× 量级）                    |
| 事务内多行插入选型        | 优先 `BulkInsertAsync`                                   | 同一事务 100 行：逐条 45.7ms → BulkInsert 5.4ms（8.44×）→ `SessionBatch` 16.8ms；需逐条回填自增 ID 或混合语句时才用 SessionBatch |
| 排序/连接内存缓冲        | `sort_buffer_size` / `join_buffer_size` ≥ 256KB（默认即此值） | 远程大结果集 / 百万行级排序 join 先 `EXPLAIN` 确认无 `Using filesort` 再按需调（DBA 决策）                                     |
| 隔离级别 RR→RC       | `WithIsolationLevel(IsolationLevel.ReadCommitted)`     | 引擎默认 RR（间隙锁）放大热点行锁竞争；取舍为 MVCC 快照语义变化，属业务决策                                                             |

## 🔄 升级指南

> 按版本倒序排列；数据正确性修复优先阅读对应小节。

### 从 6.4.x 升级到 6.5.0：查询语义两处修复（结果可能变化）

两项**用户可见行为修复**，升级前请核对（详见 [CHANGELOG](CHANGELOG.md)）：

- **WHERE/OR 链式优先级（C16）**：`Where(A).OrWhere(B).Where(C)` 此前生成 `(A) OR (B) AND (C)`（SQL 求值为 `A OR (B AND C)`，与方法文档承诺的链式形态不符），本版修复为 `((A) OR (B)) AND (C)`。**混合 Where/OrWhere 链的查询结果可能变化**：纯 AND 链、纯 OR 链与 OR 收尾链的 SQL 文本逐字不变，无需动作。`WhereIn`/`WhereNotIn` 同受此修复覆盖。**升级动作**：检索代码中混合 `Where`/`OrWhere` 链，确认查询结果符合链式直觉预期。
- **FormattableString 引号内花括号（C17）**：引号内 `{N}`/`}` 此前被替换或误抛 `FormatException`（JSON 文本 `'{"a":1}'` 无法书写），修复后引号内一律原样透传。此前为绕过该缺陷手写拆串的代码可还原为直书 JSON 字面量。

其余为纯新增（会话池 `DataSessionPool`）与内部性能/修复，无迁移动作。

### 从 6.3.x 升级到 6.4.0：Production 预设熔断作用域变更

`DbOptions.Production` 预设自 6.4.0 起显式设 `CircuitBreakerScope = Process`（此前为 Session）。影响与动因：

- **变化**：使用 `Production` 预设时，熔断失败计数按 (Provider, 连接串, 阈值, 冷却) 跨会话累计。一请求一会话的部署下，数据库持续故障时请求快速失败（熔断开闸），而非此前每个请求各自重试 5 次再失败、熔断从不触发的虚设形态。
- **不变**：裸 `new DbOptions { ... }` 默认仍为 `Session`；长生命周期会话与测试场景零变化。
- **多租户注意**：多租户共用一个连接串时熔断器为租户间共享粒度，需要租户级隔离的部署用独立连接串，或 `with { CircuitBreakerScope = CircuitBreakerScope.Session }` 显式回退。

### 从 6.2.x 及更早升级到 6.3.x（数据正确性修复，强烈建议）

6.3.0 修复了 **R-UNNESTB：PG auto-prepare × 命令复用槽交互下的批量操作静默错数**（影响 5.7.0 ~ 6.2.0 共 7 个版本）；6.3.1 在此之上恢复了 BulkDelete IN 形态的池化性能（20K 键 37 ms 快态带，0.55× 地板，见[性能](#-性能)）。R-UNNESTB 触发条件：连接串启用 `MaxAutoPrepare`（PalORM 默认调优会设为 100，见[连接串自动调优](#-连接串自动调优)）且同一会话执行第 3 个及后续等长批量批次；后果是第 3 批起静默重发第 2 批的参数（批量删除少删行、批量 UPSERT 写错键），无异常无告警。

- **升级到 6.3.1** 即彻底修复（参数对象从建立到释放全程稳定，auto-prepare 缓存引用不再错配）
- **暂不能升级的止血**：连接串显式加 `MaxAutoPrepare=0`（代价是失去自动预编译的查询延迟收益，Npgsql 默认即 0）
- 风险面自查：若历史负载满足触发条件，建议对受影响表的批量操作结果做一次对账（行数/键集合核对）

6.3.0 同时包含 PG 三条批量写走数组形态（`UPDATE FROM VALUES(...)` 的 UNNEST 化与 `DELETE ... = ANY(数组)`，批量族时延 −40~74%），行为面无破坏。

### 从 5.x 升级到 6.0

<details>
<summary><b>四项破坏性变更（详见 ADR-N）</b></summary>

| 删除/变更                                                       | 迁移动作                                                                              |
| ----------------------------------------------------------- | --------------------------------------------------------------------------------- |
| `IRowFactory<T>` 接口                                         | 无——零实现零消费，若外部代码引用了它（不存在官方用法），删除引用即可                                               |
| `DataSession.DiffAsync<T>()`                                | 换用 `ValidateSchemaAsync<T>()`，需要 `[DIFF]` 前缀时自行拼接                                 |
| `DbOptions.NamingConvention`（含 `ApplyNaming`）               | **删除该设置即可——行为从未生效过**（列名映射在编译期由 `[Table]`/`[Column]` 注解决定）；要改列名用 `[Column("...")]` |
| `DbOptions.PoolExplicitlyConfigured`                        | 无——零读方内部标记；`WithPool`/`PALORM_MAX_POOL_SIZE` 行为不变                                 |
| `[Column]` 的 `Length/Precision/Scale`：`int?` → `int`（0=未设置） | 无——`[Column(Length = 64)]` 语法在 5.x 从未可编译过（CS0655）；v6.0 起该语法首次真正可用并参与 DDL          |

破坏性变更全表见 [ADR-N：v6.0 破坏性变更汇总](docs/adr/ADR-N-v6.0-破坏性变更汇总.md)。

</details>

完整版本历史见 [CHANGELOG.md](CHANGELOG.md)。

## 🧰 开发

> 构建环境、项目结构与注册契约；完整构建路径与提交防线见 [CONTRIBUTING.md](CONTRIBUTING.md)。

### 环境要求

| 维度     | JIT 运行时     | Native AOT 编译                                          |
| ------ |:-----------:|:------------------------------------------------------:|
| CPU 架构 | x64 / ARM64 | x64 已验证（ARM64 未实测），需指定 RID（`-r win-x64` / `linux-x64`） |
| 内存     | 256 MB      | 4 GB（ILC 编译器；运行时仅需约 100 MB）                            |
| 磁盘     | 50 MB       | 100 MB，SSD 推荐（ILC 大量临时文件，HDD 编译慢 5-10x）                |

软件：.NET SDK 11.0.100-rc.2+（`global.json` 锁定）· C# `latest` · Windows 10+ / Linux / macOS · Visual Studio 2026 / Rider / VS Code（需支持 Roslyn 源生成器）。

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

<div align="right">

[⬆ 回到顶部](#palorm)

</div>
