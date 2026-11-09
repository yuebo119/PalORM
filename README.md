<div align="center">

# PalORM

**面向 Native AOT 的 .NET 11 微 ORM：编译时生成一切，运行时零反射**

[![.NET](https://img.shields.io/badge/.NET-11.0.0--rc.2-512BD4)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/PalORM.Core)](https://www.nuget.org/packages/PalORM.Core)
[![CI](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml/badge.svg)](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

[![Native AOT](https://img.shields.io/badge/Native_AOT-3_dialects_verified-512BD4)](docs/AOT部署指南.md)
[![Diagnostics](https://img.shields.io/badge/Compile--time_diagnostics-46_rules-512BD4)](docs/API参考.md)
[![Dialects](https://img.shields.io/badge/Dialects-PostgreSQL_·_MySQL_·_SQLite-512BD4)](#数据库兼容性)

简体中文 | [English](README.en.md)

*AOT 原生、编译期可验证、SQL 可控的微 ORM*

</div>

Roslyn 源生成器在编译期产出 SQL 构造、参数绑定、对象映射与迁移 DDL，运行时零反射、零 IL Emit；支持 PostgreSQL / MySQL / SQLite 三方言，多租户、乐观锁、软删除、审计、咨询锁等企业级特性开箱即用。

---

深度文档：[最佳实践](docs/最佳实践.md) · [功能特性](docs/功能特性.md) · [API 参考](docs/API参考.md) · [配置参考](docs/配置参考.md) · [AOT 部署指南](docs/AOT部署指南.md) · [架构设计](docs/架构设计.md) · [性能基准规范](docs/性能基准规范.md) · [编码规范](docs/编码规范.md) · [发布规范](docs/发布规范.md) · [升级指南](docs/升级指南.md) · [ADR](docs/adr/) · [贡献指南](CONTRIBUTING.md) · [变更日志](CHANGELOG.md)

---

## ✨ 特性

- **编译期生成**：Roslyn 源生成器为每个 `[Table]` 实体生成 RowFactory、CommandFactory 与三方言 Migration DDL，为 `[Projection]` DTO 生成物化委托，运行时零反射、零 IL Emit。
- **编译期安全网**：46 条 Roslyn 诊断把缺主键、租户列可空绕过隔离、乐观锁基线为 0、循环内 N+1 等问题拦在构建期，错误可跳转声明位置。
- **全链路 Native AOT**：SQLite / PostgreSQL / MySQL 三方言 AOT 发布与原生运行全部通过，消费方不需要 TrimmerRootAssembly。
- **基准级性能**：单行 CRUD 与 ADO.NET 手写基准同量级，批量写按方言自动选择 PG Binary COPY、MySQL LOAD DATA、SQLite 行预编译与 json_each 批删，Web 一请求一会话形态内置会话池。
- **企业级横切**：多租户列隔离、乐观锁、软删除、审计拦截器、读写分离、咨询锁、弹性重试与熔断开箱即用。
- **SQL 资产编译期管理**：`[SqlFile]` 嵌入并校验 .sql，`[SqlTemplate]` 把插值 SQL 常量化，AutoTagging 自动注入“文件:行号 成员”定位注释。
- **运维与可观测**：健康检查、跨方言统一异常族、OpenTelemetry tracing 与 metrics 内置。
- **方言专属能力**：PG 的 UNNEST、咨询锁、LISTEN/NOTIFY、pgvector 与 JSONB 路径查询，MySQL 的 local_infile 降级与 UPDATE JOIN，SQLite 的 AES-256 加密与 PRAGMA 自动调优。
- **Schema 与种子**：`MigrateAsync` 按外键拓扑序执行三方言 DDL，`SeedAsync` 事务内幂等种子，`ValidateSchemaAsync` 对照实体返回缺失列。
- **零依赖核心**：PalORM.Core 不引用第三方 NuGet 包，仅 BCL 与 ADO.NET 抽象。

> **审计边界**：`IQueryInterceptor` 覆盖 SELECT、QueryBuilder UPDATE 与 `ExecuteAsync`；实体 Insert/Delete/Save、Bulk 家族、存储过程与多结果集不经过拦截器。写入审计请使用数据库层审计或 OpenTelemetry。

## 🆚 与主流 ORM 对比

> 版本（2026-10-26 核对）：PalORM 6.8.0 · Dapper 2.1.89 + Dapper.AOT 1.1.0 · EF Core 10.0.14 · RepoDb 1.17.0。

| 特性                    | **PalORM**                                                      | Dapper                                                               | EF Core                                                         | RepoDb                                       |
| ----------------------- |:---------------------------------------------------------------:|:--------------------------------------------------------------------:|:---------------------------------------------------------------:|:--------------------------------------------:|
| **AOT 与生成时机**      | ✓ 编译期源生成全部（SQL / 参数绑定 / 映射 / DDL），运行时零反射 | △ 手写 SQL；Dapper.AOT 可选（拦截器式源生成）                        | △ 运行时表达式树 + 模型构建；AOT 须 Compiled Model + 预编译查询 | △ 运行时反射 + 表达式编译，AOT 靠裁剪注解    |
| **编译时类型诊断**      | ✓ 46 条（43 分析器 + 3 生成器），可跳转声明                     | ❌ 运行时失败                                                         | △ 迁移检查（设计时）                                            | ❌ 运行时失败                                 |
| **SQL 注入面**          | ✓ raw SQL 唯一形态 `FormattableString`，无裸 `string` 重载      | △ `string` + 匿名对象参数（反射绑定）                                | △ LINQ 参数化，`FromSqlRaw` 存裸串形态                          | △ 值进运行时表达式树                         |
| **查询书写范式**        | ✓ 链式构建器（SQL 显式可见）+ `FormattableString`               | △ 手写 SQL                                                           | ✓ LINQ 强类型翻译                                               | △ QueryBuilder                               |
| **三方言批量策略**      | ✓ COPY / BulkCopy / 行预编译 / `= ANY`；IN 集合 500 键自动分批  | ❌ 手写多值 SQL 与 IN 拼串                                            | △ 按 Provider 而异                                              | △ Bulk 扩展包覆盖 17 个 Provider，无自动分批 |
| **单语句多行 UPDATE**   | ✓ FROM VALUES / JOIN VALUES ROW / CASE WHEN                     | ❌                                                                    | ❌ `ExecuteUpdate` 仅按 WHERE 单值                               | ❌                                            |
| **批量 UPSERT**         | ✓ `BulkMergeAsync`（冲突更新限定本租户）                        | ❌ 手写                                                               | ❌ 需 raw SQL                                                    | △ `Merge` 在核心包                           |
| **乐观锁**              | ✓ `[ConcurrencyCheck]`；批量走版本化单语句                      | ❌ 手写                                                               | ✓ `RowVersion` 自动                                             | ❌ 手写                                       |
| **软删除**              | ✓ `[SoftDelete]` 自动过滤（批量删转 UPDATE）                    | ❌                                                                    | ✓ 全局过滤器                                                    | ❌                                            |
| **多租户隔离**          | ✓ `[TenantAware]` 编译期列隔离 + 跨租户写入护栏                 | ❌                                                                    | △ 需手动实现                                                    | ❌                                            |
| **Schema 迁移**         | ✓ `MigrateAsync`（外键拓扑序 + 竞态容错）                       | ❌ 无迁移系统                                                         | ✓ Migrations（最全）                                            | ❌                                            |
| **JSON 列**             | ✓ `[OwnedJson]` 源生成；PG `WhereJson` 路径查询                 | △ PropertyHandler（AOT 需 `JsonTypeInfo`）                           | ✓ EF7+ 映射 + 路径查询                                          | △ PropertyHandler，无路径查询                |
| **导航装配**            | △ `Include` 出 JOIN 不装配（按父分组自行配对）                  | △ multi-mapping 手写                                                 | ✓ 完整装配 + 变更追踪                                           | ❌                                            |
| **变更追踪 / 工作单元** | ❌ 无状态设计（零追踪开销）                                      | ❌                                                                    | ✓ ChangeTracker                                                 | ❌                                            |
| **流式消费**            | ✓ `QueryAsyncEnumerable` / `ForEachAsync`（O(1) 内存）          | △ 手写 CommandBehavior                                               | ✓ `AsAsyncEnumerable`                                           | ❌                                            |
| **覆盖广度与依赖**      | 三方言深度调优；核心零第三方依赖；`net11.0` 单目标；Apache-2.0  | 任意 ADO.NET；多目标（netstandard2.0 / net8.0 / net461）；Apache-2.0 | Provider 矩阵；多包依赖重；`net10.0`；MIT                       | 20 个 Provider 包；核心多目标；Apache-2.0    |

### AOT 实现路线对比（PalORM / Dapper.AOT / RepoDb / EF Core / FreeSql）

AOT 选型的核心变量是各库对运行时反射的替代策略：**编译期源生成**（PalORM、Dapper.AOT）、**表达式解释 + 裁剪注解**（RepoDb）、**运行时构建 + 可选预生成**（EF Core）、**运行时反射无适配**（FreeSql）。

| 选型维度             | PalORM 6.8.0                                                                    | Dapper.AOT 1.1.0                                                                    | RepoDb 1.17.0                                              | EF Core 10.0.14                                     | FreeSql 3.5.311                                                                  |
| -------------------- | ------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- | ---------------------------------------------------------- | --------------------------------------------------- | -------------------------------------------------------------------------------- |
| **实现路线**         | 编译期源生成，运行时零反射                                                      | 源生成 + 拦截器替换调用点                                                           | 运行时表达式树，AOT 下解释执行                             | 运行时表达式树编译 + 模型构建                       | 运行时反射，无 AOT 适配                                                          |
| **AOT 支持程度**     | ✅ **完全支持**：三方言发布通过，零 IL 告警，4 条 CI 门禁                        | 🟦 **高**：核心 API 生成实现，须显式启用                                             | 🟨 **中**：常规实体与 LINQ 可用                             | 🟧 **低**：Compiled Model + 预编译查询，实验性       | 🟥 **不支持**：零注解、无 AOT CI，默认发布 9 项中 7 项失败                        |
| **缺陷与失败模式**   | 不支持形态编译期拒绝，无运行时失败面                                            | 未覆盖 API 回退反射（不安全）；Contrib 与非泛型 `typeof` 不支持；拦截器配错静默失效 | dynamic / Expando 标注不安全；仅 SQLite 冒烟；解释执行降速 | 预编译查询与模型须手写并随模型维护；能力为 JIT 子集 | 须 TrimmerRootAssembly 保根；开分析器 212 条 IL 警告；执行期崩溃（构造函数被裁） |
| **AOT 下性能**       | 与 JIT 等价                                                                     | 绕开反射，官方定位 AOT 安全                                                         | 单查询高于 JIT                                             | 依赖预编译，无性能承诺                              | 解释降级 + 首次元数据构建成本                                                    |
| **集成约束**         | `[Table]` / `[Projection]` 注解 + 公共无参构造；生成器随包分发                  | 存量调用零改写；显式 opt-in；TypeHandler 声明式注册                                 | 开箱即用；AOT 下规避动态 API                               | 须预编译查询 + Compiled Model                       | 须 TrimmerRootAssembly + 逐项原生冒烟                                            |
| **逃生舱与功能边界** | `FormattableString` / `[SqlFile]` 是 AOT 安全出口；无动态实体、无完整 LINQ 翻译 | 未覆盖调用只能改写形态；与主包允许细微行为差异                                      | 无安全出口；JIT 下 API 面宽，20 个 Provider                | 无逃生舱承诺；JIT 功能最全                          | 无逃生舱；功能面最广，AOT 责任在使用方                                           |

> 色标（AOT 下）：✅ 完全支持 · 🟦 高（有缺陷）· 🟨 中 · 🟧 低 · 🟥 不支持

## 📊 性能

> **测试环境**：AMD Ryzen 9 8945HX · Windows 10 22H2 · .NET 11 RC2 · SQLite 临时文件库（WAL + 64 MB cache + mmap）· PostgreSQL 18.6 / MySQL 8.4.11 本机容器。
>
> **数据来源**：9 组同批同轮采集，2026-11-02，批次 `crossbench-20261102-172156.json`（新测量协议首个完整批：预热 0.375s、批量族预算 0.6~0.75s；与 2026-11-02 早批不可逐格对比）。9 组为 PalORM / Dapper / Dapper.AOT / linq2db / EF Core / SqlSugar / FreeSql / RepoDb 与 ADO.NET 手写 SQL（基准），各用生态惯用写法。完整报告见 [性能全量报告-2026-11-02.md](性能全量报告-2026-11-02.md)。

### 总览

9 组 × 三方言 × 两档（2000 / 20000）× 17 操作（事务仅最小档）= **87 格**，每操作两遍反序取平均；所有倍数以同批 ADO.NET 手写 SQL 为基准 1.00，小于 1 为更快。

| 组 | 胜场 /87 | 时延中位比 | 内存中位比 |
|---|---:|---:|---:|
| ADO | — | 1.00 | 1.00 |
| PalORM | 22 | 1.01 | 1.00 |
| Dapper.AOT | 11 | 1.07 | 1.15 |
| RepoDb | 2 | 1.15 | 1.47 |
| EF Core | 8 | 2.95 | 8.05 |
| Dapper | 6 | 1.10 | 1.62 |
| FreeSql | 0 | 1.45 | 2.00 |
| linq2db | 0 | 1.40 | 3.02 |
| SqlSugar | 1 | 2.25 | 3.52 |

- **PalORM**：胜场 22/87（25%），时延中位比 **1.01**（全距 0.41 ~ 1.14），内存中位比 **1.00**，无 1.5 倍以上的劣化格。单行读写与批量写与 ADO 地板持平，PG 批删 0.41 倍（UNNEST 形态）。
- **最大离群**：SqlSugar UpsertBatch（SQLite 109.2 倍、101.0 倍）、EF Core 单条 Insert（65.9 ~ 67.2 倍，每操作 DbContext 形态）。

### 操作对照（时延 [倍数] 与内存 [相对基准]，颜色同百分比六档）

> 🏆 = 时延最快组 · 💾 = 内存最省组（均含 ADO 基准）

#### SQLite（2000 与 20000 档）

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 7.2&nbsp;µs<br>1.00<br>2.1KB<br>1.00 | 🏆&nbsp;5.8&nbsp;µs<br>$\color{#65a30d}{0.81}$<br>2.2KB<br>$\color{#d97706}{1.03}$ | 💾&nbsp;6.5&nbsp;µs<br>0.90<br>2.0KB<br>0.97 | 15.1&nbsp;µs<br>**2.08**<br>4.5KB<br>**2.13** | 130.8&nbsp;µs<br>**18.03**<br>49.1KB<br>**23.34** | 9.7&nbsp;µs<br>1.34<br>2.8KB<br>1.34 | 34.5&nbsp;µs<br>**4.75**<br>10.2KB<br>**4.85** | 21.0&nbsp;µs<br>**2.90**<br>6.4KB<br>**3.03** | 78.0&nbsp;µs<br>**10.75**<br>30.0KB<br>**14.24** |
| GetByKey | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 6.2&nbsp;µs<br>1.00<br>2.1KB<br>1.00 | 🏆&nbsp;5.9&nbsp;µs<br>$\color{#2563eb}{0.96}$<br>2.2KB<br>$\color{#d97706}{1.03}$ | 💾&nbsp;6.7&nbsp;µs<br>1.09<br>2.0KB<br>0.97 | 8.4&nbsp;µs<br>1.37<br>4.4KB<br>**2.07** | 80.3&nbsp;µs<br>**13.06**<br>48.9KB<br>**23.20** | 7.0&nbsp;µs<br>1.15<br>2.8KB<br>1.33 | 30.9&nbsp;µs<br>**5.03**<br>10.4KB<br>**4.93** | 16.1&nbsp;µs<br>**2.61**<br>6.4KB<br>**3.02** | 70.7&nbsp;µs<br>**11.49**<br>29.8KB<br>**14.15** |
| GetAllAsync | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 932.8&nbsp;µs<br>1.00<br>308KB<br>1.00 | 🏆&nbsp;💾&nbsp;930.7&nbsp;µs<br>$\color{#6b7280}{1.00}$<br>292KB<br>$\color{#2563eb}{0.95}$ | 1.09&nbsp;ms<br>1.17<br>355KB<br>1.15 | 1.13&nbsp;ms<br>1.22<br>372KB<br>1.21 | 1.34&nbsp;ms<br>1.43<br>728KB<br>**2.36** | 1.33&nbsp;ms<br>1.42<br>513KB<br>1.66 | 1.32&nbsp;ms<br>1.42<br>313KB<br>1.02 | 1.30&nbsp;ms<br>1.39<br>311KB<br>1.01 | 3.07&nbsp;ms<br>**3.29**<br>579KB<br>1.88 |
| GetAllAsync | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 9.41&nbsp;ms<br>1.00<br>3337KB<br>1.00 | 🏆&nbsp;💾&nbsp;9.37&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>2981KB<br>$\color{#65a30d}{0.89}$ | 11.05&nbsp;ms<br>1.17<br>3811KB<br>1.14 | 11.40&nbsp;ms<br>1.21<br>3969KB<br>1.19 | 12.78&nbsp;ms<br>1.36<br>7141KB<br>**2.14** | 13.49&nbsp;ms<br>1.43<br>5381KB<br>1.61 | 13.35&nbsp;ms<br>1.42<br>3353KB<br>1.00 | 13.06&nbsp;ms<br>1.39<br>3348KB<br>1.00 | 30.51&nbsp;ms<br>**3.24**<br>5912KB<br>1.77 |
| QueryAll | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;927.9&nbsp;µs<br>1.00<br>308KB<br>1.00 | 💾&nbsp;929.0&nbsp;µs<br>$\color{#6b7280}{1.00}$<br>292KB<br>$\color{#2563eb}{0.95}$ | 1.09&nbsp;ms<br>1.18<br>355KB<br>1.15 | 1.12&nbsp;ms<br>1.21<br>372KB<br>1.21 | 1.31&nbsp;ms<br>1.41<br>728KB<br>**2.36** | 1.32&nbsp;ms<br>1.42<br>513KB<br>1.66 | 1.34&nbsp;ms<br>1.44<br>314KB<br>1.02 | 1.30&nbsp;ms<br>1.40<br>311KB<br>1.01 | 3.06&nbsp;ms<br>**3.30**<br>579KB<br>1.88 |
| QueryAll | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 9.53&nbsp;ms<br>1.00<br>3338KB<br>1.00 | 🏆&nbsp;💾&nbsp;9.42&nbsp;ms<br>$\color{#2563eb}{0.99}$<br>2981KB<br>$\color{#65a30d}{0.89}$ | 11.07&nbsp;ms<br>1.16<br>3811KB<br>1.14 | 11.31&nbsp;ms<br>1.19<br>3968KB<br>1.19 | 12.92&nbsp;ms<br>1.35<br>7140KB<br>**2.14** | 13.51&nbsp;ms<br>1.42<br>5381KB<br>1.61 | 13.29&nbsp;ms<br>1.39<br>3352KB<br>1.00 | 13.08&nbsp;ms<br>1.37<br>3348KB<br>1.00 | 30.91&nbsp;ms<br>**3.24**<br>5913KB<br>1.77 |
| Insert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 17.1&nbsp;µs<br>1.00<br>2.8KB<br>1.00 | 🏆&nbsp;💾&nbsp;17.1&nbsp;µs<br>$\color{#d97706}{0.99}$<br>2.7KB<br>$\color{#2563eb}{0.98}$ | 17.3&nbsp;µs<br>1.01<br>2.9KB<br>1.04 | 19.4&nbsp;µs<br>1.13<br>3.0KB<br>1.08 | 1.15&nbsp;ms<br>**67.15**<br>55.1KB<br>**19.91** | 18.1&nbsp;µs<br>1.05<br>3.5KB<br>1.28 | 29.9&nbsp;µs<br>1.74<br>5.7KB<br>**2.06** | 1.11&nbsp;ms<br>**64.61**<br>19.3KB<br>**6.98** | 1.11&nbsp;ms<br>**64.66**<br>14.9KB<br>**5.38** |
| Insert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;17.4&nbsp;µs<br>1.00<br>2.8KB<br>1.00 | 💾&nbsp;18.4&nbsp;µs<br>$\color{#d97706}{1.06}$<br>2.7KB<br>$\color{#2563eb}{0.97}$ | 17.9&nbsp;µs<br>1.03<br>2.8KB<br>1.00 | 19.0&nbsp;µs<br>1.10<br>3.0KB<br>1.06 | 1.14&nbsp;ms<br>**65.90**<br>55.1KB<br>**19.49** | 18.1&nbsp;µs<br>1.04<br>3.6KB<br>1.28 | 30.4&nbsp;µs<br>1.76<br>5.7KB<br>**2.02** | 1.05&nbsp;ms<br>**60.76**<br>19.4KB<br>**6.85** | 1.14&nbsp;ms<br>**65.52**<br>15.0KB<br>**5.30** |
| Update | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;7.0&nbsp;µs<br>1.00<br>2.5KB<br>1.00 | 7.2&nbsp;µs<br>$\color{#d97706}{1.02}$<br>2.9KB<br>$\color{#ea580c}{1.16}$ | 7.4&nbsp;µs<br>1.05<br>2.8KB<br>1.11 | 8.9&nbsp;µs<br>1.27<br>4.1KB<br>1.62 | 80.5&nbsp;µs<br>**11.42**<br>52.5KB<br>**20.90** | 7.8&nbsp;µs<br>1.10<br>3.3KB<br>1.30 | 19.2&nbsp;µs<br>**2.72**<br>7.4KB<br>**2.94** | 14.2&nbsp;µs<br>**2.01**<br>19.7KB<br>**7.86** | 32.0&nbsp;µs<br>**4.54**<br>14.5KB<br>**5.79** |
| Update | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;8.2&nbsp;µs<br>1.00<br>2.5KB<br>1.00 | 🏆&nbsp;7.3&nbsp;µs<br>$\color{#65a30d}{0.88}$<br>2.9KB<br>$\color{#ea580c}{1.16}$ | 7.7&nbsp;µs<br>0.93<br>2.8KB<br>1.11 | 9.2&nbsp;µs<br>1.12<br>4.1KB<br>1.62 | 85.2&nbsp;µs<br>**10.33**<br>52.5KB<br>**20.85** | 8.0&nbsp;µs<br>0.96<br>3.3KB<br>1.30 | 22.1&nbsp;µs<br>**2.68**<br>7.4KB<br>**2.95** | 16.2&nbsp;µs<br>1.97<br>19.8KB<br>**7.85** | 34.6&nbsp;µs<br>**4.19**<br>14.6KB<br>**5.78** |
| BulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;linq2db | 🏆&nbsp;2.45&nbsp;ms<br>1.00<br>1757KB<br>1.00 | 2.53&nbsp;ms<br>$\color{#d97706}{1.03}$<br>1757KB<br>$\color{#6b7280}{1.00}$ | 4.11&nbsp;ms<br>1.68<br>4353KB<br>**2.48** | 15.76&nbsp;ms<br>**6.44**<br>2703KB<br>1.54 | 21.80&nbsp;ms<br>**8.91**<br>17740KB<br>**10.10** | 4.05&nbsp;ms<br>1.66<br>4354KB<br>**2.48** | 9.64&nbsp;ms<br>**3.94**<br>6645KB<br>**3.78** | 💾&nbsp;5.02&nbsp;ms<br>**2.05**<br>1275KB<br>0.73 | 7.00&nbsp;ms<br>**2.86**<br>4510KB<br>**2.57** |
| BulkInsert | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;linq2db | 27.23&nbsp;ms<br>1.00<br>17560KB<br>1.00 | 🏆&nbsp;26.77&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>17558KB<br>$\color{#6b7280}{1.00}$ | 45.05&nbsp;ms<br>1.65<br>43530KB<br>**2.48** | 151.15&nbsp;ms<br>**5.55**<br>26704KB<br>1.52 | 238.39&nbsp;ms<br>**8.75**<br>176823KB<br>**10.07** | 45.42&nbsp;ms<br>1.67<br>43531KB<br>**2.48** | 107.56&nbsp;ms<br>**3.95**<br>67657KB<br>**3.85** | 💾&nbsp;51.62&nbsp;ms<br>1.90<br>11161KB<br>0.64 | 75.97&nbsp;ms<br>**2.79**<br>45939KB<br>**2.62** |
| BulkUpdate | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;2.21&nbsp;ms<br>1.00<br>1734KB<br>1.00 | 2.22&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>1735KB<br>$\color{#6b7280}{1.00}$ | 8.18&nbsp;ms<br>**3.70**<br>5640KB<br>**3.25** | 6.59&nbsp;ms<br>**2.98**<br>5306KB<br>**3.06** | 25.29&nbsp;ms<br>**11.42**<br>18363KB<br>**10.59** | 8.46&nbsp;ms<br>**3.82**<br>6500KB<br>**3.75** | 12.73&nbsp;ms<br>**5.75**<br>7198KB<br>**4.15** | 20.93&nbsp;ms<br>**9.45**<br>39441KB<br>**22.74** | 5.57&nbsp;ms<br>**2.51**<br>4501KB<br>**2.60** |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;24.27&nbsp;ms<br>1.00<br>17394KB<br>1.00 | 24.33&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>17396KB<br>$\color{#6b7280}{1.00}$ | 88.46&nbsp;ms<br>**3.64**<br>56468KB<br>**3.25** | 70.47&nbsp;ms<br>**2.90**<br>53086KB<br>**3.05** | 301.06&nbsp;ms<br>**12.40**<br>183055KB<br>**10.52** | 90.43&nbsp;ms<br>**3.73**<br>65067KB<br>**3.74** | 131.66&nbsp;ms<br>**5.42**<br>75102KB<br>**4.32** | 211.14&nbsp;ms<br>**8.70**<br>394472KB<br>**22.68** | 69.38&nbsp;ms<br>**2.86**<br>45831KB<br>**2.63** |
| BulkDelete | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;748.5&nbsp;µs<br>1.00<br>197KB<br>1.00 | 769.4&nbsp;µs<br>$\color{#d97706}{1.03}$<br>198KB<br>$\color{#d97706}{1.01}$ | 21.86&nbsp;ms<br>**29.21**<br>1600KB<br>**8.12** | 18.52&nbsp;ms<br>**24.75**<br>3053KB<br>**15.51** | 11.04&nbsp;ms<br>**14.75**<br>2518KB<br>**12.79** | 21.26&nbsp;ms<br>**28.41**<br>1598KB<br>**8.11** | 1.54&nbsp;ms<br>**2.05**<br>569KB<br>**2.89** | 2.49&nbsp;ms<br>**3.33**<br>271KB<br>1.37 | 2.37&nbsp;ms<br>**3.17**<br>353KB<br>1.79 |
| BulkDelete | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;8.20&nbsp;ms<br>1.00<br>2075KB<br>1.00 | 8.33&nbsp;ms<br>$\color{#d97706}{1.02}$<br>2076KB<br>$\color{#6b7280}{1.00}$ | 218.37&nbsp;ms<br>**26.63**<br>15970KB<br>**7.70** | 189.45&nbsp;ms<br>**23.11**<br>31914KB<br>**15.38** | 114.07&nbsp;ms<br>**13.91**<br>25170KB<br>**12.13** | 215.76&nbsp;ms<br>**26.32**<br>15965KB<br>**7.70** | 18.20&nbsp;ms<br>**2.22**<br>6096KB<br>**2.94** | 15.96&nbsp;ms<br>1.95<br>2788KB<br>1.34 | 35.12&nbsp;ms<br>**4.28**<br>4337KB<br>**2.09** |
| UpsertBatch | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;2.29&nbsp;ms<br>1.00<br>1735KB<br>1.00 | 2.32&nbsp;ms<br>$\color{#d97706}{1.01}$<br>1752KB<br>$\color{#d97706}{1.01}$ | 103.11&nbsp;ms<br>**45.09**<br>8138KB<br>**4.69** | 17.54&nbsp;ms<br>**7.67**<br>11899KB<br>**6.86** | 18.82&nbsp;ms<br>**8.23**<br>5499KB<br>**3.17** | 3.85&nbsp;ms<br>1.68<br>4333KB<br>**2.50** | 3.52&nbsp;ms<br>1.54<br>2555KB<br>1.47 | 21.63&nbsp;ms<br>**9.46**<br>43786KB<br>**25.24** | 249.72&nbsp;ms<br>**109.20**<br>225131KB<br>**129.75** |
| UpsertBatch | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;24.86&nbsp;ms<br>1.00<br>17397KB<br>1.00 | 25.40&nbsp;ms<br>$\color{#d97706}{1.02}$<br>17556KB<br>$\color{#d97706}{1.01}$ | 1.02&nbsp;s<br>**41.12**<br>81381KB<br>**4.68** | 173.71&nbsp;ms<br>**6.99**<br>119047KB<br>**6.84** | 1.45&nbsp;s<br>**58.45**<br>58580KB<br>**3.37** | 43.18&nbsp;ms<br>1.74<br>43382KB<br>**2.49** | 38.05&nbsp;ms<br>1.53<br>26421KB<br>1.52 | 206.65&nbsp;ms<br>**8.31**<br>437899KB<br>**25.17** | 2.51&nbsp;s<br>**101.05**<br>2364.4MB<br>**139.17** |
| KeysetPage | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;52.2&nbsp;µs<br>1.00<br>17.9KB<br>1.00 | 57.0&nbsp;µs<br>$\color{#ea580c}{1.09}$<br>18.3KB<br>$\color{#d97706}{1.02}$ | 63.5&nbsp;µs<br>1.22<br>20.7KB<br>1.15 | 65.8&nbsp;µs<br>1.26<br>25.0KB<br>1.39 | 152.4&nbsp;µs<br>**2.92**<br>86.6KB<br>**4.83** | 76.2&nbsp;µs<br>1.46<br>29.2KB<br>1.63 | 103.8&nbsp;µs<br>1.99<br>28.0KB<br>1.56 | 81.0&nbsp;µs<br>1.55<br>23.2KB<br>1.29 | 225.7&nbsp;µs<br>**4.32**<br>63.2KB<br>**3.52** |
| KeysetPage | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;51.9&nbsp;µs<br>1.00<br>17.9KB<br>1.00 | 57.5&nbsp;µs<br>$\color{#ea580c}{1.11}$<br>18.3KB<br>$\color{#d97706}{1.02}$ | 63.8&nbsp;µs<br>1.23<br>20.7KB<br>1.15 | 65.2&nbsp;µs<br>1.26<br>24.9KB<br>1.39 | 152.9&nbsp;µs<br>**2.95**<br>86.6KB<br>**4.83** | 76.0&nbsp;µs<br>1.46<br>29.2KB<br>1.63 | 103.7&nbsp;µs<br>2.00<br>28.0KB<br>1.56 | 81.3&nbsp;µs<br>1.57<br>23.2KB<br>1.29 | 225.5&nbsp;µs<br>**4.34**<br>63.2KB<br>**3.52** |
| WhereIn | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 125.8&nbsp;µs<br>1.00<br>47.3KB<br>1.00 | 🏆&nbsp;💾&nbsp;121.8&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>41.7KB<br>$\color{#65a30d}{0.88}$ | 237.7&nbsp;µs<br>1.89<br>101KB<br>**2.13** | 245.1&nbsp;µs<br>1.95<br>90.8KB<br>1.92 | 270.6&nbsp;µs<br>**2.15**<br>203KB<br>**4.29** | 236.2&nbsp;µs<br>1.88<br>101KB<br>**2.14** | 156.1&nbsp;µs<br>1.24<br>45.2KB<br>0.96 | 135.1&nbsp;µs<br>1.07<br>41.9KB<br>0.89 | 335.6&nbsp;µs<br>**2.67**<br>108KB<br>**2.29** |
| WhereIn | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;123.9&nbsp;µs<br>1.00<br>47.9KB<br>1.00 | 💾&nbsp;124.2&nbsp;µs<br>$\color{#6b7280}{1.00}$<br>42.3KB<br>$\color{#65a30d}{0.88}$ | 236.6&nbsp;µs<br>1.91<br>102KB<br>**2.12** | 245.3&nbsp;µs<br>1.98<br>91.4KB<br>1.91 | 276.2&nbsp;µs<br>**2.23**<br>204KB<br>**4.25** | 235.2&nbsp;µs<br>1.90<br>102KB<br>**2.13** | 158.0&nbsp;µs<br>1.28<br>49.4KB<br>1.03 | 138.6&nbsp;µs<br>1.12<br>43.3KB<br>0.90 | 342.6&nbsp;µs<br>**2.76**<br>111KB<br>**2.31** |
| Count | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;4.3&nbsp;µs<br>1.00<br>1017B<br>1.00 | 5.0&nbsp;µs<br>$\color{#ea580c}{1.14}$<br>1.8KB<br>$\color{#dc2626}{1.79}$ | 4.8&nbsp;µs<br>1.10<br>1.0KB<br>1.04 | 5.2&nbsp;µs<br>1.20<br>1.3KB<br>1.33 | 51.9&nbsp;µs<br>**11.92**<br>46.1KB<br>**46.48** | 4.9&nbsp;µs<br>1.13<br>1.1KB<br>1.07 | 31.1&nbsp;µs<br>**7.15**<br>5.8KB<br>**5.87** | 6.0&nbsp;µs<br>1.39<br>2.2KB<br>**2.25** | 55.6&nbsp;µs<br>**12.79**<br>25.5KB<br>**25.64** |
| Count | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;9.2&nbsp;µs<br>1.00<br>1.0KB<br>1.00 | 9.4&nbsp;µs<br>$\color{#d97706}{1.02}$<br>1.8KB<br>$\color{#dc2626}{1.78}$ | 🏆&nbsp;8.1&nbsp;µs<br>0.88<br>1.0KB<br>1.04 | 11.2&nbsp;µs<br>1.22<br>1.3KB<br>1.33 | 58.5&nbsp;µs<br>**6.35**<br>46.2KB<br>**45.85** | 9.3&nbsp;µs<br>1.01<br>1.1KB<br>1.07 | 214.2&nbsp;µs<br>**23.28**<br>6.3KB<br>**6.30** | 8.5&nbsp;µs<br>0.92<br>2.2KB<br>**2.23** | 236.2&nbsp;µs<br>**25.67**<br>26.0KB<br>**25.79** |

#### MySQL（2000 与 20000 档）

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;190.1&nbsp;µs<br>1.00<br>4.5KB<br>1.00 | 191.1&nbsp;µs<br>$\color{#d97706}{1.01}$<br>5.0KB<br>$\color{#ea580c}{1.10}$ | 195.7&nbsp;µs<br>1.03<br>5.2KB<br>1.16 | 206.9&nbsp;µs<br>1.09<br>8.5KB<br>1.88 | 677.9&nbsp;µs<br>**3.57**<br>94.6KB<br>**20.98** | 198.4&nbsp;µs<br>1.04<br>5.8KB<br>1.29 | 403.7&nbsp;µs<br>**2.12**<br>16.6KB<br>**3.68** | 240.8&nbsp;µs<br>1.27<br>25.6KB<br>**5.68** | 448.6&nbsp;µs<br>**2.36**<br>36.3KB<br>**8.04** |
| GetByKey | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;178.9&nbsp;µs<br>1.00<br>4.5KB<br>1.00 | 🏆&nbsp;175.4&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>4.9KB<br>$\color{#ea580c}{1.10}$ | 177.2&nbsp;µs<br>0.99<br>5.2KB<br>1.16 | 195.9&nbsp;µs<br>1.09<br>8.4KB<br>1.88 | 604.5&nbsp;µs<br>**3.38**<br>94.5KB<br>**21.11** | 185.4&nbsp;µs<br>1.04<br>5.8KB<br>1.30 | 397.3&nbsp;µs<br>**2.22**<br>16.7KB<br>**3.73** | 250.6&nbsp;µs<br>1.40<br>25.8KB<br>**5.77** | 455.6&nbsp;µs<br>**2.55**<br>36.3KB<br>**8.10** |
| GetAllAsync | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 1.07&nbsp;ms<br>1.00<br>398KB<br>1.00 | 💾&nbsp;1.07&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>382KB<br>$\color{#2563eb}{0.96}$ | 🏆&nbsp;1.06&nbsp;ms<br>1.00<br>399KB<br>1.00 | 1.12&nbsp;ms<br>1.05<br>401KB<br>1.01 | 3.16&nbsp;ms<br>**2.96**<br>4502KB<br>**11.31** | 1.14&nbsp;ms<br>1.06<br>447KB<br>1.12 | 1.36&nbsp;ms<br>1.27<br>407KB<br>1.02 | 2.62&nbsp;ms<br>**2.46**<br>4058KB<br>**10.19** | 1.63&nbsp;ms<br>1.53<br>418KB<br>1.05 |
| GetAllAsync | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 9.71&nbsp;ms<br>1.00<br>4143KB<br>1.00 | 💾&nbsp;9.45&nbsp;ms<br>$\color{#2563eb}{0.97}$<br>3787KB<br>$\color{#2563eb}{0.91}$ | 🏆&nbsp;9.29&nbsp;ms<br>0.96<br>4143KB<br>1.00 | 9.84&nbsp;ms<br>1.01<br>4146KB<br>1.00 | 32.68&nbsp;ms<br>**3.37**<br>47382KB<br>**11.44** | 10.11&nbsp;ms<br>1.04<br>4615KB<br>1.11 | 10.78&nbsp;ms<br>1.11<br>4154KB<br>1.00 | 30.42&nbsp;ms<br>**3.13**<br>44257KB<br>**10.68** | 12.99&nbsp;ms<br>1.34<br>4170KB<br>1.01 |
| QueryAll | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 1.07&nbsp;ms<br>1.00<br>398KB<br>1.00 | 💾&nbsp;1.07&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>384KB<br>$\color{#2563eb}{0.96}$ | 🏆&nbsp;1.06&nbsp;ms<br>0.99<br>399KB<br>1.00 | 1.12&nbsp;ms<br>1.04<br>401KB<br>1.01 | 3.13&nbsp;ms<br>**2.91**<br>4502KB<br>**11.31** | 1.14&nbsp;ms<br>1.06<br>447KB<br>1.12 | 1.36&nbsp;ms<br>1.26<br>407KB<br>1.02 | 2.65&nbsp;ms<br>**2.47**<br>4058KB<br>**10.19** | 1.61&nbsp;ms<br>1.50<br>418KB<br>1.05 |
| QueryAll | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 9.50&nbsp;ms<br>1.00<br>4143KB<br>1.00 | 💾&nbsp;9.64&nbsp;ms<br>$\color{#d97706}{1.01}$<br>3789KB<br>$\color{#2563eb}{0.91}$ | 🏆&nbsp;9.41&nbsp;ms<br>0.99<br>4143KB<br>1.00 | 9.77&nbsp;ms<br>1.03<br>4145KB<br>1.00 | 32.00&nbsp;ms<br>**3.37**<br>47383KB<br>**11.44** | 10.23&nbsp;ms<br>1.08<br>4615KB<br>1.11 | 10.76&nbsp;ms<br>1.13<br>4155KB<br>1.00 | 30.44&nbsp;ms<br>**3.20**<br>44258KB<br>**10.68** | 13.11&nbsp;ms<br>1.38<br>4171KB<br>1.01 |
| Insert | 2,000 | 🏆&nbsp;Dapper<br>💾&nbsp;Dapper.AOT | 3.92&nbsp;ms<br>1.00<br>17.0KB<br>1.00 | 3.75&nbsp;ms<br>$\color{#2563eb}{0.96}$<br>16.0KB<br>$\color{#2563eb}{0.94}$ | 💾&nbsp;3.73&nbsp;ms<br>0.95<br>15.4KB<br>0.91 | 3.70&nbsp;ms<br>0.94<br>18.7KB<br>1.10 | 4.99&nbsp;ms<br>1.27<br>120KB<br>**7.05** | 🏆&nbsp;3.66&nbsp;ms<br>0.93<br>16.1KB<br>0.95 | 4.04&nbsp;ms<br>1.03<br>22.0KB<br>1.30 | 4.07&nbsp;ms<br>1.04<br>36.4KB<br>**2.15** | 4.12&nbsp;ms<br>1.05<br>28.8KB<br>1.69 |
| Insert | 20,000 | 🏆&nbsp;Dapper<br>💾&nbsp;Dapper.AOT | 3.77&nbsp;ms<br>1.00<br>16.1KB<br>1.00 | 3.74&nbsp;ms<br>$\color{#d97706}{0.99}$<br>15.9KB<br>$\color{#2563eb}{0.99}$ | 💾&nbsp;3.78&nbsp;ms<br>1.00<br>15.5KB<br>0.96 | 3.78&nbsp;ms<br>1.00<br>16.7KB<br>1.04 | 5.62&nbsp;ms<br>1.49<br>121KB<br>**7.49** | 🏆&nbsp;3.68&nbsp;ms<br>0.98<br>15.7KB<br>0.98 | 3.98&nbsp;ms<br>1.06<br>22.0KB<br>1.37 | 3.92&nbsp;ms<br>1.04<br>36.4KB<br>**2.26** | 4.16&nbsp;ms<br>1.10<br>30.5KB<br>1.89 |
| Update | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;174.6&nbsp;µs<br>1.00<br>5.1KB<br>1.00 | 185.7&nbsp;µs<br>$\color{#d97706}{1.06}$<br>5.7KB<br>$\color{#ea580c}{1.11}$ | 💾&nbsp;190.1&nbsp;µs<br>1.09<br>4.7KB<br>0.91 | 210.8&nbsp;µs<br>1.21<br>7.6KB<br>1.48 | 654.6&nbsp;µs<br>**3.75**<br>92.9KB<br>**18.08** | 191.4&nbsp;µs<br>1.10<br>5.2KB<br>1.01 | 400.1&nbsp;µs<br>**2.29**<br>13.1KB<br>**2.56** | 248.4&nbsp;µs<br>1.42<br>30.9KB<br>**6.02** | 426.8&nbsp;µs<br>**2.44**<br>20.3KB<br>**3.95** |
| Update | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;177.5&nbsp;µs<br>1.00<br>5.2KB<br>1.00 | 182.4&nbsp;µs<br>$\color{#d97706}{1.03}$<br>5.7KB<br>$\color{#ea580c}{1.11}$ | 💾&nbsp;181.8&nbsp;µs<br>1.02<br>4.7KB<br>0.91 | 199.3&nbsp;µs<br>1.12<br>7.6KB<br>1.47 | 645.1&nbsp;µs<br>**3.63**<br>92.9KB<br>**18.00** | 179.8&nbsp;µs<br>1.01<br>5.1KB<br>1.00 | 376.1&nbsp;µs<br>**2.12**<br>13.1KB<br>**2.54** | 253.6&nbsp;µs<br>1.43<br>31.0KB<br>**6.00** | 423.9&nbsp;µs<br>**2.39**<br>20.3KB<br>**3.94** |
| BulkInsert | 2,000 | 🏆&nbsp;RepoDb<br>💾&nbsp;ADO | 💾&nbsp;10.98&nbsp;ms<br>1.00<br>477KB<br>1.00 | 11.15&nbsp;ms<br>$\color{#d97706}{1.02}$<br>483KB<br>$\color{#d97706}{1.01}$ | 29.32&nbsp;ms<br>**2.67**<br>6821KB<br>**14.31** | 🏆&nbsp;10.62&nbsp;ms<br>0.97<br>479KB<br>1.00 | 83.06&nbsp;ms<br>**7.56**<br>20545KB<br>**43.10** | 32.63&nbsp;ms<br>**2.97**<br>6826KB<br>**14.32** | 16.83&nbsp;ms<br>1.53<br>3306KB<br>**6.94** | 21.06&nbsp;ms<br>1.92<br>3348KB<br>**7.02** | 14.30&nbsp;ms<br>1.30<br>2385KB<br>**5.00** |
| BulkInsert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;54.54&nbsp;ms<br>1.00<br>4498KB<br>1.00 | 56.65&nbsp;ms<br>$\color{#d97706}{1.04}$<br>4504KB<br>$\color{#6b7280}{1.00}$ | 254.13&nbsp;ms<br>**4.66**<br>68007KB<br>**15.12** | 58.11&nbsp;ms<br>1.07<br>4510KB<br>1.00 | 566.76&nbsp;ms<br>**10.39**<br>203901KB<br>**45.33** | 248.92&nbsp;ms<br>**4.56**<br>68003KB<br>**15.12** | 124.52&nbsp;ms<br>**2.28**<br>33923KB<br>**7.54** | 214.66&nbsp;ms<br>**3.94**<br>31973KB<br>**7.11** | 92.94&nbsp;ms<br>1.70<br>24920KB<br>**5.54** |
| BulkUpdate | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 9.14&nbsp;ms<br>1.00<br>4686KB<br>1.00 | 🏆&nbsp;8.58&nbsp;ms<br>$\color{#2563eb}{0.94}$<br>3741KB<br>$\color{#65a30d}{0.80}$ | 10.87&nbsp;ms<br>1.19<br>6606KB<br>1.41 | 💾&nbsp;163.35&nbsp;ms<br>**17.88**<br>919KB<br>**0.20** | 131.11&nbsp;ms<br>**14.35**<br>36770KB<br>**7.85** | 10.66&nbsp;ms<br>1.17<br>6607KB<br>1.41 | 9.56&nbsp;ms<br>1.05<br>2124KB<br>**0.45** | 30.63&nbsp;ms<br>**3.35**<br>7741KB<br>1.65 | 10.93&nbsp;ms<br>1.20<br>2350KB<br>0.50 |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;RepoDb | 🏆&nbsp;79.84&nbsp;ms<br>1.00<br>14944KB<br>1.00 | 86.50&nbsp;ms<br>$\color{#d97706}{1.08}$<br>14012KB<br>$\color{#2563eb}{0.94}$ | 144.35&nbsp;ms<br>1.81<br>66077KB<br>**4.42** | 💾&nbsp;464.75&nbsp;ms<br>**5.82**<br>5527KB<br>**0.37** | 1.26&nbsp;s<br>**15.84**<br>363928KB<br>**24.35** | 140.20&nbsp;ms<br>1.76<br>66069KB<br>**4.42** | 90.06&nbsp;ms<br>1.13<br>22299KB<br>1.49 | 295.87&nbsp;ms<br>**3.71**<br>77628KB<br>**5.19** | 102.49&nbsp;ms<br>1.28<br>24525KB<br>1.64 |
| BulkDelete | 2,000 | 🏆&nbsp;SqlSugar<br>💾&nbsp;SqlSugar | 11.75&nbsp;ms<br>1.00<br>885KB<br>1.00 | 11.75&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>806KB<br>$\color{#2563eb}{0.91}$ | 11.90&nbsp;ms<br>1.01<br>1374KB<br>1.55 | 26.53&nbsp;ms<br>**2.26**<br>19752KB<br>**22.31** | 23.00&nbsp;ms<br>1.96<br>3778KB<br>**4.27** | 11.56&nbsp;ms<br>0.98<br>1376KB<br>1.55 | 16.41&nbsp;ms<br>1.40<br>609KB<br>0.69 | 11.99&nbsp;ms<br>1.02<br>710KB<br>0.80 | 🏆&nbsp;💾&nbsp;10.78&nbsp;ms<br>0.92<br>370KB<br>**0.42** |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 67.43&nbsp;ms<br>1.00<br>8628KB<br>1.00 | 🏆&nbsp;💾&nbsp;62.77&nbsp;ms<br>$\color{#2563eb}{0.93}$<br>3570KB<br>$\color{#16a34a}{0.41}$ | 71.15&nbsp;ms<br>1.06<br>13550KB<br>1.57 | 219.30&nbsp;ms<br>**3.25**<br>199860KB<br>**23.16** | 242.84&nbsp;ms<br>**3.60**<br>37818KB<br>**4.38** | 73.49&nbsp;ms<br>1.09<br>13550KB<br>1.57 | 190.74&nbsp;ms<br>**2.83**<br>6067KB<br>0.70 | 75.29&nbsp;ms<br>1.12<br>7054KB<br>0.82 | 166.81&nbsp;ms<br>**2.47**<br>4391KB<br>0.51 |
| UpsertBatch | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 8.26&nbsp;ms<br>1.00<br>2966KB<br>1.00 | 🏆&nbsp;7.81&nbsp;ms<br>$\color{#2563eb}{0.95}$<br>2518KB<br>$\color{#65a30d}{0.85}$ | 10.85&nbsp;ms<br>1.31<br>6776KB<br>**2.28** | 💾&nbsp;156.86&nbsp;ms<br>**18.99**<br>903KB<br>**0.30** | 14.57&nbsp;ms<br>1.76<br>10422KB<br>**3.51** | 11.85&nbsp;ms<br>1.44<br>6778KB<br>**2.29** | 8.60&nbsp;ms<br>1.04<br>2551KB<br>0.86 | 30.58&nbsp;ms<br>**3.70**<br>8090KB<br>**2.73** | 253.92&nbsp;ms<br>**30.74**<br>224627KB<br>**75.73** |
| UpsertBatch | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;RepoDb | 🏆&nbsp;73.19&nbsp;ms<br>1.00<br>13158KB<br>1.00 | 76.23&nbsp;ms<br>$\color{#d97706}{1.04}$<br>12859KB<br>$\color{#2563eb}{0.98}$ | 151.03&nbsp;ms<br>**2.06**<br>67744KB<br>**5.15** | 💾&nbsp;397.78&nbsp;ms<br>**5.44**<br>5546KB<br>**0.42** | 173.91&nbsp;ms<br>**2.38**<br>105874KB<br>**8.05** | 140.15&nbsp;ms<br>1.92<br>67734KB<br>**5.15** | 119.98&nbsp;ms<br>1.64<br>26381KB<br>**2.00** | 325.72&nbsp;ms<br>**4.45**<br>81073KB<br>**6.16** | 2.64&nbsp;s<br>**36.04**<br>2359.3MB<br>**183.61** |
| KeysetPage | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;248.7&nbsp;µs<br>1.00<br>24.7KB<br>1.00 | 254.9&nbsp;µs<br>$\color{#d97706}{1.03}$<br>26.5KB<br>$\color{#d97706}{1.07}$ | 251.2&nbsp;µs<br>1.01<br>25.6KB<br>1.04 | 272.8&nbsp;µs<br>1.10<br>30.3KB<br>1.23 | 851.6&nbsp;µs<br>**3.43**<br>318KB<br>**12.88** | 263.2&nbsp;µs<br>1.06<br>28.6KB<br>1.16 | 494.1&nbsp;µs<br>1.99<br>38.3KB<br>1.55 | 380.4&nbsp;µs<br>1.53<br>228KB<br>**9.22** | 512.5&nbsp;µs<br>**2.06**<br>61.3KB<br>**2.48** |
| KeysetPage | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;264.6&nbsp;µs<br>1.00<br>24.8KB<br>1.00 | 258.9&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>26.6KB<br>$\color{#d97706}{1.07}$ | 🏆&nbsp;253.2&nbsp;µs<br>0.96<br>25.7KB<br>1.04 | 275.2&nbsp;µs<br>1.04<br>30.3KB<br>1.22 | 835.7&nbsp;µs<br>**3.16**<br>318KB<br>**12.84** | 265.4&nbsp;µs<br>1.00<br>28.6KB<br>1.15 | 491.6&nbsp;µs<br>1.86<br>38.3KB<br>1.55 | 391.4&nbsp;µs<br>1.48<br>228KB<br>**9.20** | 536.1&nbsp;µs<br>**2.03**<br>61.3KB<br>**2.48** |
| WhereIn | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;FreeSql | 🏆&nbsp;344.4&nbsp;µs<br>1.00<br>66.0KB<br>1.00 | 348.3&nbsp;µs<br>$\color{#d97706}{1.01}$<br>61.8KB<br>$\color{#2563eb}{0.94}$ | 367.6&nbsp;µs<br>1.07<br>90.5KB<br>1.37 | 469.4&nbsp;µs<br>1.36<br>109KB<br>1.65 | 1.12&nbsp;ms<br>**3.24**<br>490KB<br>**7.43** | 371.4&nbsp;µs<br>1.08<br>91.0KB<br>1.38 | 💾&nbsp;593.2&nbsp;µs<br>1.72<br>55.7KB<br>0.84 | 492.2&nbsp;µs<br>1.43<br>266KB<br>**4.03** | 661.7&nbsp;µs<br>1.92<br>106KB<br>1.61 |
| WhereIn | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;FreeSql | 🏆&nbsp;340.0&nbsp;µs<br>1.00<br>66.0KB<br>1.00 | 347.1&nbsp;µs<br>$\color{#d97706}{1.02}$<br>61.8KB<br>$\color{#2563eb}{0.94}$ | 357.5&nbsp;µs<br>1.05<br>90.5KB<br>1.37 | 450.4&nbsp;µs<br>1.32<br>109KB<br>1.65 | 1.18&nbsp;ms<br>**3.46**<br>506KB<br>**7.66** | 361.0&nbsp;µs<br>1.06<br>91.0KB<br>1.38 | 💾&nbsp;588.6&nbsp;µs<br>1.73<br>59.0KB<br>0.89 | 515.4&nbsp;µs<br>1.52<br>285KB<br>**4.32** | 676.8&nbsp;µs<br>1.99<br>108KB<br>1.64 |
| Count | 2,000 | 🏆&nbsp;Dapper<br>💾&nbsp;ADO | 💾&nbsp;515.8&nbsp;µs<br>1.00<br>4.0KB<br>1.00 | 518.7&nbsp;µs<br>$\color{#d97706}{1.01}$<br>5.1KB<br>$\color{#ea580c}{1.28}$ | 515.0&nbsp;µs<br>1.00<br>4.1KB<br>1.03 | 522.8&nbsp;µs<br>1.01<br>4.7KB<br>1.16 | 964.3&nbsp;µs<br>1.87<br>81.4KB<br>**20.27** | 🏆&nbsp;511.8&nbsp;µs<br>0.99<br>4.1KB<br>1.01 | 700.8&nbsp;µs<br>1.36<br>12.1KB<br>**3.02** | 523.8&nbsp;µs<br>1.02<br>6.6KB<br>1.65 | 751.2&nbsp;µs<br>1.46<br>30.8KB<br>**7.68** |
| Count | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;692.3&nbsp;µs<br>1.00<br>4.5KB<br>1.00 | 704.8&nbsp;µs<br>$\color{#d97706}{1.02}$<br>5.7KB<br>$\color{#ea580c}{1.25}$ | 702.0&nbsp;µs<br>1.01<br>4.7KB<br>1.04 | 701.4&nbsp;µs<br>1.01<br>5.2KB<br>1.14 | 1.17&nbsp;ms<br>1.69<br>82.0KB<br>**18.07** | 699.9&nbsp;µs<br>1.01<br>4.6KB<br>1.02 | 897.5&nbsp;µs<br>1.30<br>12.7KB<br>**2.81** | 694.5&nbsp;µs<br>1.00<br>7.1KB<br>1.57 | 928.1&nbsp;µs<br>1.34<br>31.3KB<br>**6.90** |

#### PostgreSQL（2000 与 20000 档）

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;Dapper<br>💾&nbsp;Dapper.AOT | 187.3&nbsp;µs<br>1.00<br>3.0KB<br>1.00 | 188.5&nbsp;µs<br>$\color{#d97706}{1.01}$<br>3.4KB<br>$\color{#ea580c}{1.16}$ | 💾&nbsp;188.3&nbsp;µs<br>1.01<br>2.6KB<br>0.89 | 200.5&nbsp;µs<br>1.07<br>5.7KB<br>1.94 | 300.9&nbsp;µs<br>1.61<br>44.0KB<br>**14.87** | 🏆&nbsp;184.3&nbsp;µs<br>0.98<br>3.2KB<br>1.09 | 262.9&nbsp;µs<br>1.40<br>12.0KB<br>**4.06** | 231.9&nbsp;µs<br>1.24<br>7.7KB<br>**2.61** | 318.4&nbsp;µs<br>1.70<br>31.4KB<br>**10.61** |
| GetByKey | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;197.9&nbsp;µs<br>1.00<br>3.0KB<br>1.00 | 201.4&nbsp;µs<br>$\color{#d97706}{1.02}$<br>3.5KB<br>$\color{#ea580c}{1.16}$ | 💾&nbsp;200.8&nbsp;µs<br>1.01<br>2.7KB<br>0.89 | 213.5&nbsp;µs<br>1.08<br>5.8KB<br>1.94 | 303.4&nbsp;µs<br>1.53<br>44.0KB<br>**14.76** | 199.4&nbsp;µs<br>1.01<br>3.3KB<br>1.10 | 265.9&nbsp;µs<br>1.34<br>12.1KB<br>**4.05** | 229.1&nbsp;µs<br>1.16<br>7.7KB<br>**2.59** | 324.9&nbsp;µs<br>1.64<br>31.4KB<br>**10.55** |
| GetAllAsync | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 667.3&nbsp;µs<br>1.00<br>239KB<br>1.00 | 💾&nbsp;646.4&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>224KB<br>$\color{#2563eb}{0.94}$ | 🏆&nbsp;642.8&nbsp;µs<br>0.96<br>239KB<br>1.00 | 643.3&nbsp;µs<br>0.96<br>241KB<br>1.00 | 962.5&nbsp;µs<br>1.44<br>652KB<br>**2.72** | 655.5&nbsp;µs<br>0.98<br>442KB<br>1.85 | 746.1&nbsp;µs<br>1.12<br>244KB<br>1.02 | 670.0&nbsp;µs<br>1.00<br>241KB<br>1.01 | 929.1&nbsp;µs<br>1.39<br>255KB<br>1.06 |
| GetAllAsync | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;4.88&nbsp;ms<br>1.00<br>2566KB<br>1.00 | 💾&nbsp;5.01&nbsp;ms<br>$\color{#d97706}{1.02}$<br>2212KB<br>$\color{#65a30d}{0.86}$ | 4.99&nbsp;ms<br>1.02<br>2561KB<br>1.00 | 5.05&nbsp;ms<br>1.03<br>2564KB<br>1.00 | 7.69&nbsp;ms<br>1.58<br>6358KB<br>**2.48** | 5.36&nbsp;ms<br>1.10<br>4595KB<br>1.79 | 5.54&nbsp;ms<br>1.13<br>2567KB<br>1.00 | 5.23&nbsp;ms<br>1.07<br>2564KB<br>1.00 | 7.41&nbsp;ms<br>1.52<br>2583KB<br>1.01 |
| QueryAll | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;659.9&nbsp;µs<br>1.00<br>239KB<br>1.00 | 💾&nbsp;674.4&nbsp;µs<br>$\color{#d97706}{1.02}$<br>225KB<br>$\color{#2563eb}{0.94}$ | 699.9&nbsp;µs<br>1.06<br>239KB<br>1.00 | 680.3&nbsp;µs<br>1.03<br>241KB<br>1.01 | 937.0&nbsp;µs<br>1.42<br>652KB<br>**2.72** | 673.9&nbsp;µs<br>1.02<br>442KB<br>1.85 | 726.8&nbsp;µs<br>1.10<br>244KB<br>1.02 | 670.5&nbsp;µs<br>1.02<br>241KB<br>1.01 | 922.5&nbsp;µs<br>1.40<br>255KB<br>1.06 |
| QueryAll | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;4.94&nbsp;ms<br>1.00<br>2566KB<br>1.00 | 💾&nbsp;4.94&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>2211KB<br>$\color{#65a30d}{0.86}$ | 4.99&nbsp;ms<br>1.01<br>2561KB<br>1.00 | 5.08&nbsp;ms<br>1.03<br>2564KB<br>1.00 | 7.78&nbsp;ms<br>1.58<br>6358KB<br>**2.48** | 5.36&nbsp;ms<br>1.09<br>4595KB<br>1.79 | 5.50&nbsp;ms<br>1.12<br>2568KB<br>1.00 | 5.29&nbsp;ms<br>1.07<br>2564KB<br>1.00 | 7.50&nbsp;ms<br>1.52<br>2583KB<br>1.01 |
| Insert | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;Dapper.AOT | 1.34&nbsp;ms<br>1.00<br>7.5KB<br>1.00 | 1.38&nbsp;ms<br>$\color{#d97706}{1.03}$<br>7.6KB<br>$\color{#d97706}{1.01}$ | 🏆&nbsp;💾&nbsp;1.33&nbsp;ms<br>1.00<br>6.6KB<br>0.88 | 1.38&nbsp;ms<br>1.03<br>7.8KB<br>1.05 | 1.49&nbsp;ms<br>1.11<br>52.0KB<br>**6.96** | 1.38&nbsp;ms<br>1.03<br>7.2KB<br>0.97 | 1.43&nbsp;ms<br>1.07<br>11.0KB<br>1.47 | 1.40&nbsp;ms<br>1.04<br>21.7KB<br>**2.90** | 1.51&nbsp;ms<br>1.13<br>18.9KB<br>**2.53** |
| Insert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;1.34&nbsp;ms<br>1.00<br>7.5KB<br>1.00 | 1.35&nbsp;ms<br>$\color{#d97706}{1.01}$<br>7.5KB<br>$\color{#6b7280}{1.00}$ | 💾&nbsp;1.39&nbsp;ms<br>1.04<br>6.5KB<br>0.87 | 1.39&nbsp;ms<br>1.04<br>7.8KB<br>1.04 | 1.47&nbsp;ms<br>1.10<br>52.0KB<br>**6.94** | 1.39&nbsp;ms<br>1.04<br>7.2KB<br>0.96 | 1.42&nbsp;ms<br>1.06<br>10.9KB<br>1.46 | 1.36&nbsp;ms<br>1.02<br>21.6KB<br>**2.88** | 1.47&nbsp;ms<br>1.10<br>18.8KB<br>**2.51** |
| Update | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 1.35&nbsp;ms<br>1.00<br>7.3KB<br>1.00 | 🏆&nbsp;1.35&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>7.8KB<br>$\color{#d97706}{1.07}$ | 💾&nbsp;1.39&nbsp;ms<br>1.03<br>6.6KB<br>0.91 | 1.36&nbsp;ms<br>1.01<br>9.5KB<br>1.30 | 1.49&nbsp;ms<br>1.11<br>52.0KB<br>**7.12** | 1.37&nbsp;ms<br>1.02<br>7.0KB<br>0.96 | 1.45&nbsp;ms<br>1.07<br>13.1KB<br>1.79 | 1.42&nbsp;ms<br>1.05<br>25.0KB<br>**3.42** | 1.47&nbsp;ms<br>1.09<br>21.2KB<br>**2.90** |
| Update | 20,000 | 🏆&nbsp;Dapper<br>💾&nbsp;Dapper.AOT | 1.41&nbsp;ms<br>1.00<br>7.4KB<br>1.00 | 1.38&nbsp;ms<br>$\color{#2563eb}{0.99}$<br>7.9KB<br>$\color{#d97706}{1.07}$ | 💾&nbsp;1.36&nbsp;ms<br>0.97<br>6.5KB<br>0.89 | 1.42&nbsp;ms<br>1.01<br>9.5KB<br>1.29 | 1.49&nbsp;ms<br>1.06<br>52.1KB<br>**7.08** | 🏆&nbsp;1.35&nbsp;ms<br>0.96<br>7.0KB<br>0.95 | 1.44&nbsp;ms<br>1.02<br>13.1KB<br>1.78 | 1.41&nbsp;ms<br>1.01<br>25.0KB<br>**3.40** | 1.50&nbsp;ms<br>1.07<br>21.3KB<br>**2.89** |
| BulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;4.64&nbsp;ms<br>1.00<br>339KB<br>1.00 | 4.81&nbsp;ms<br>$\color{#d97706}{1.04}$<br>340KB<br>$\color{#6b7280}{1.00}$ | 16.20&nbsp;ms<br>**3.49**<br>6847KB<br>**20.21** | 5.35&nbsp;ms<br>1.15<br>844KB<br>**2.49** | 42.85&nbsp;ms<br>**9.24**<br>16208KB<br>**47.85** | 16.47&nbsp;ms<br>**3.55**<br>6848KB<br>**20.22** | 12.74&nbsp;ms<br>**2.75**<br>3931KB<br>**11.61** | 12.07&nbsp;ms<br>**2.60**<br>1171KB<br>**3.46** | 26.01&nbsp;ms<br>**5.61**<br>7571KB<br>**22.35** |
| BulkInsert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;19.55&nbsp;ms<br>1.00<br>3326KB<br>1.00 | 19.68&nbsp;ms<br>$\color{#d97706}{1.01}$<br>3328KB<br>$\color{#6b7280}{1.00}$ | 193.33&nbsp;ms<br>**9.89**<br>68426KB<br>**20.57** | 22.52&nbsp;ms<br>1.15<br>8196KB<br>**2.46** | 419.91&nbsp;ms<br>**21.48**<br>156595KB<br>**47.09** | 196.21&nbsp;ms<br>**10.04**<br>68434KB<br>**20.58** | 108.15&nbsp;ms<br>**5.53**<br>40466KB<br>**12.17** | 120.24&nbsp;ms<br>**6.15**<br>10240KB<br>**3.08** | 269.25&nbsp;ms<br>**13.77**<br>76672KB<br>**23.05** |
| BulkUpdate | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;5.76&nbsp;ms<br>1.00<br>419KB<br>1.00 | 🏆&nbsp;5.28&nbsp;ms<br>$\color{#2563eb}{0.92}$<br>421KB<br>$\color{#d97706}{1.01}$ | 16.93&nbsp;ms<br>**2.94**<br>6688KB<br>**15.96** | 49.34&nbsp;ms<br>**8.56**<br>3876KB<br>**9.25** | 58.49&nbsp;ms<br>**10.15**<br>16330KB<br>**38.98** | 16.22&nbsp;ms<br>**2.81**<br>6688KB<br>**15.96** | 13.89&nbsp;ms<br>**2.41**<br>2777KB<br>**6.63** | 14.30&nbsp;ms<br>**2.48**<br>2135KB<br>**5.10** | 37.59&nbsp;ms<br>**6.52**<br>12097KB<br>**28.88** |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;58.20&nbsp;ms<br>1.00<br>4182KB<br>1.00 | 💾&nbsp;64.24&nbsp;ms<br>$\color{#ea580c}{1.10}$<br>4174KB<br>$\color{#6b7280}{1.00}$ | 215.66&nbsp;ms<br>**3.71**<br>66917KB<br>**16.00** | 539.12&nbsp;ms<br>**9.26**<br>38822KB<br>**9.28** | 603.42&nbsp;ms<br>**10.37**<br>157910KB<br>**37.76** | 220.23&nbsp;ms<br>**3.78**<br>66936KB<br>**16.00** | 125.49&nbsp;ms<br>**2.16**<br>28941KB<br>**6.92** | 141.40&nbsp;ms<br>**2.43**<br>21609KB<br>**5.17** | 373.84&nbsp;ms<br>**6.42**<br>123344KB<br>**29.49** |
| BulkDelete | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 3.43&nbsp;ms<br>1.00<br>784KB<br>1.00 | 🏆&nbsp;💾&nbsp;2.40&nbsp;ms<br>$\color{#65a30d}{0.70}$<br>89.3KB<br>$\color{#16a34a}{0.11}$ | 3.81&nbsp;ms<br>1.11<br>1338KB<br>1.71 | 18.84&nbsp;ms<br>**5.50**<br>19621KB<br>**25.04** | 4.16&nbsp;ms<br>1.21<br>177KB<br>**0.23** | 3.65&nbsp;ms<br>1.07<br>1338KB<br>1.71 | 5.85&nbsp;ms<br>1.71<br>573KB<br>0.73 | 4.21&nbsp;ms<br>1.23<br>263KB<br>**0.34** | 3.76&nbsp;ms<br>1.10<br>346KB<br>**0.44** |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 24.48&nbsp;ms<br>1.00<br>7793KB<br>1.00 | 🏆&nbsp;💾&nbsp;10.07&nbsp;ms<br>$\color{#16a34a}{0.41}$<br>813KB<br>$\color{#16a34a}{0.10}$ | 28.61&nbsp;ms<br>1.17<br>13336KB<br>1.71 | 173.68&nbsp;ms<br>**7.10**<br>197574KB<br>**25.35** | 40.23&nbsp;ms<br>1.64<br>1749KB<br>**0.22** | 28.25&nbsp;ms<br>1.15<br>13338KB<br>1.71 | 60.19&nbsp;ms<br>**2.46**<br>6091KB<br>0.78 | 31.49&nbsp;ms<br>1.29<br>2698KB<br>**0.35** | 28.76&nbsp;ms<br>1.17<br>4450KB<br>0.57 |
| UpsertBatch | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 15.80&nbsp;ms<br>1.00<br>2779KB<br>1.00 | 💾&nbsp;8.20&nbsp;ms<br>$\color{#16a34a}{0.52}$<br>447KB<br>$\color{#16a34a}{0.16}$ | 20.03&nbsp;ms<br>1.27<br>6860KB<br>**2.47** | 25.09&nbsp;ms<br>1.59<br>5910KB<br>**2.13** | 🏆&nbsp;3.04&nbsp;ms<br>**0.19**<br>3040KB<br>1.09 | 19.65&nbsp;ms<br>1.24<br>6859KB<br>**2.47** | 18.15&nbsp;ms<br>1.15<br>2806KB<br>1.01 | 15.81&nbsp;ms<br>1.00<br>2147KB<br>0.77 | 267.77&nbsp;ms<br>**16.95**<br>224417KB<br>**80.75** |
| UpsertBatch | 20,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 141.12&nbsp;ms<br>1.00<br>11846KB<br>1.00 | 💾&nbsp;92.34&nbsp;ms<br>$\color{#16a34a}{0.65}$<br>4400KB<br>$\color{#16a34a}{0.37}$ | 222.25&nbsp;ms<br>1.57<br>68529KB<br>**5.79** | 244.68&nbsp;ms<br>1.73<br>58708KB<br>**4.96** | 🏆&nbsp;48.36&nbsp;ms<br>**0.34**<br>29750KB<br>**2.51** | 228.20&nbsp;ms<br>1.62<br>68540KB<br>**5.79** | 149.21&nbsp;ms<br>1.06<br>29017KB<br>**2.45** | 159.93&nbsp;ms<br>1.13<br>21670KB<br>1.83 | 2.81&nbsp;s<br>**19.91**<br>2358.0MB<br>**203.83** |
| KeysetPage | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;238.9&nbsp;µs<br>1.00<br>15.4KB<br>1.00 | 242.1&nbsp;µs<br>$\color{#d97706}{1.01}$<br>17.3KB<br>$\color{#ea580c}{1.12}$ | 💾&nbsp;240.4&nbsp;µs<br>1.01<br>15.4KB<br>1.00 | 248.8&nbsp;µs<br>1.04<br>19.8KB<br>1.28 | 373.9&nbsp;µs<br>1.56<br>78.4KB<br>**5.08** | 240.4&nbsp;µs<br>1.01<br>26.0KB<br>1.69 | 333.9&nbsp;µs<br>1.40<br>26.0KB<br>1.69 | 276.2&nbsp;µs<br>1.16<br>21.1KB<br>1.36 | 396.6&nbsp;µs<br>1.66<br>48.9KB<br>**3.17** |
| KeysetPage | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;Dapper.AOT | 248.2&nbsp;µs<br>1.00<br>15.5KB<br>1.00 | 253.6&nbsp;µs<br>$\color{#d97706}{1.02}$<br>17.3KB<br>$\color{#ea580c}{1.12}$ | 🏆&nbsp;💾&nbsp;246.2&nbsp;µs<br>0.99<br>15.4KB<br>1.00 | 266.4&nbsp;µs<br>1.07<br>19.8KB<br>1.28 | 370.1&nbsp;µs<br>1.49<br>78.4KB<br>**5.07** | 252.3&nbsp;µs<br>1.02<br>26.1KB<br>1.69 | 337.2&nbsp;µs<br>1.36<br>26.1KB<br>1.69 | 272.9&nbsp;µs<br>1.10<br>21.0KB<br>1.36 | 389.4&nbsp;µs<br>1.57<br>48.9KB<br>**3.16** |
| WhereIn | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 312.4&nbsp;µs<br>1.00<br>52.7KB<br>1.00 | 🏆&nbsp;💾&nbsp;290.8&nbsp;µs<br>$\color{#2563eb}{0.93}$<br>17.8KB<br>$\color{#16a34a}{0.34}$ | 361.8&nbsp;µs<br>1.16<br>87.2KB<br>1.65 | 447.9&nbsp;µs<br>1.43<br>93.9KB<br>1.78 | 421.6&nbsp;µs<br>1.35<br>76.8KB<br>1.46 | 351.4&nbsp;µs<br>1.12<br>87.6KB<br>1.66 | 452.6&nbsp;µs<br>1.45<br>43.4KB<br>0.82 | 414.1&nbsp;µs<br>1.33<br>40.4KB<br>0.77 | 564.2&nbsp;µs<br>1.81<br>93.2KB<br>1.77 |
| WhereIn | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 366.1&nbsp;µs<br>1.00<br>52.9KB<br>1.00 | 🏆&nbsp;💾&nbsp;330.0&nbsp;µs<br>$\color{#2563eb}{0.90}$<br>17.9KB<br>$\color{#16a34a}{0.34}$ | 400.6&nbsp;µs<br>1.09<br>87.3KB<br>1.65 | 478.0&nbsp;µs<br>1.31<br>94.1KB<br>1.78 | 447.6&nbsp;µs<br>1.22<br>76.9KB<br>1.45 | 382.8&nbsp;µs<br>1.05<br>87.7KB<br>1.66 | 503.1&nbsp;µs<br>1.37<br>47.0KB<br>0.89 | 445.0&nbsp;µs<br>1.22<br>41.0KB<br>0.78 | 604.5&nbsp;µs<br>1.65<br>95.3KB<br>1.80 |
| Count | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;227.4&nbsp;µs<br>1.00<br>1.9KB<br>1.00 | 218.7&nbsp;µs<br>$\color{#2563eb}{0.96}$<br>3.0KB<br>$\color{#dc2626}{1.57}$ | 🏆&nbsp;216.2&nbsp;µs<br>0.95<br>2.0KB<br>1.02 | 223.3&nbsp;µs<br>0.98<br>2.7KB<br>1.39 | 333.1&nbsp;µs<br>1.46<br>41.0KB<br>**21.44** | 227.5&nbsp;µs<br>1.00<br>2.0KB<br>1.04 | 288.4&nbsp;µs<br>1.27<br>7.6KB<br>**3.98** | 234.7&nbsp;µs<br>1.03<br>2.2KB<br>1.16 | 339.3&nbsp;µs<br>1.49<br>26.5KB<br>**13.86** |
| Count | 20,000 | 🏆&nbsp;RepoDb<br>💾&nbsp;ADO | 💾&nbsp;630.9&nbsp;µs<br>1.00<br>3.1KB<br>1.00 | 629.8&nbsp;µs<br>$\color{#6b7280}{1.00}$<br>4.2KB<br>$\color{#dc2626}{1.36}$ | 630.6&nbsp;µs<br>1.00<br>3.1KB<br>1.02 | 🏆&nbsp;629.7&nbsp;µs<br>1.00<br>3.8KB<br>1.25 | 760.2&nbsp;µs<br>1.20<br>42.2KB<br>**13.84** | 718.6&nbsp;µs<br>1.14<br>3.4KB<br>1.10 | 711.5&nbsp;µs<br>1.13<br>8.8KB<br>**2.89** | 633.8&nbsp;µs<br>1.00<br>3.4KB<br>1.11 | 762.2&nbsp;µs<br>1.21<br>27.8KB<br>**9.09** |

### 事务对照（仅 2000 档）

#### SQLite

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;19.4&nbsp;µs<br>1.00<br>3.9KB<br>1.00 | 20.1&nbsp;µs<br>$\color{#d97706}{1.04}$<br>4.4KB<br>$\color{#ea580c}{1.11}$ | 🏆&nbsp;19.4&nbsp;µs<br>0.99<br>4.0KB<br>1.02 | 21.1&nbsp;µs<br>1.09<br>4.5KB<br>1.14 | 1.20&nbsp;ms<br>**61.85**<br>59.1KB<br>**15.00** | 20.2&nbsp;µs<br>1.04<br>4.7KB<br>1.20 | 32.9&nbsp;µs<br>1.69<br>7.0KB<br>1.78 | 1.12&nbsp;ms<br>**57.45**<br>21.5KB<br>**5.45** | 1.15&nbsp;ms<br>**59.27**<br>16.4KB<br>**4.15** |
| TxHundredInserts | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 181.3&nbsp;µs<br>1.00<br>137KB<br>1.00 | 🏆&nbsp;💾&nbsp;177.8&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>92.8KB<br>$\color{#16a34a}{0.68}$ | 429.4&nbsp;µs<br>**2.37**<br>280KB<br>**2.05** | 551.6&nbsp;µs<br>**3.04**<br>295KB<br>**2.16** | 1.98&nbsp;ms<br>**10.93**<br>931KB<br>**6.80** | 239.3&nbsp;µs<br>1.32<br>220KB<br>1.61 | 1.50&nbsp;ms<br>**8.25**<br>541KB<br>**3.96** | 1.92&nbsp;ms<br>**10.58**<br>1646KB<br>**12.04** | 3.37&nbsp;ms<br>**18.58**<br>1198KB<br>**8.76** |
| TxBulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;linq2db | 🏆&nbsp;2.46&nbsp;ms<br>1.00<br>1757KB<br>1.00 | 2.53&nbsp;ms<br>$\color{#d97706}{1.03}$<br>1758KB<br>$\color{#6b7280}{1.00}$ | 4.10&nbsp;ms<br>1.67<br>4353KB<br>**2.48** | 15.65&nbsp;ms<br>**6.37**<br>2702KB<br>1.54 | 21.53&nbsp;ms<br>**8.77**<br>17694KB<br>**10.07** | 4.09&nbsp;ms<br>1.67<br>4354KB<br>**2.48** | 7.28&nbsp;ms<br>**2.97**<br>3566KB<br>**2.03** | 💾&nbsp;4.02&nbsp;ms<br>1.64<br>1281KB<br>0.73 | 8.54&nbsp;ms<br>**3.48**<br>5566KB<br>**3.17** |
| TxRollback | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;556.4&nbsp;µs<br>1.00<br>435KB<br>1.00 | 618.5&nbsp;µs<br>$\color{#ea580c}{1.11}$<br>437KB<br>$\color{#d97706}{1.01}$ | 2.04&nbsp;ms<br>**3.67**<br>1410KB<br>**3.24** | 2.67&nbsp;ms<br>**4.80**<br>2021KB<br>**4.65** | 5.22&nbsp;ms<br>**9.37**<br>4600KB<br>**10.58** | 2.06&nbsp;ms<br>**3.70**<br>1625KB<br>**3.74** | 7.46&nbsp;ms<br>**13.41**<br>3547KB<br>**8.16** | 5.18&nbsp;ms<br>**9.32**<br>9860KB<br>**22.68** | 12.67&nbsp;ms<br>**22.77**<br>7226KB<br>**16.62** |
| EnumInserts | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;129.8&nbsp;µs<br>1.00<br>59.1KB<br>1.00 | 146.5&nbsp;µs<br>$\color{#ea580c}{1.13}$<br>59.2KB<br>$\color{#6b7280}{1.00}$ | 330.6&nbsp;µs<br>**2.55**<br>190KB<br>**3.21** | 469.2&nbsp;µs<br>**3.62**<br>232KB<br>**3.92** | 1.92&nbsp;ms<br>**14.76**<br>755KB<br>**12.77** | 353.1&nbsp;µs<br>**2.72**<br>223KB<br>**3.77** | 1.25&nbsp;ms<br>**9.60**<br>497KB<br>**8.41** | 1.74&nbsp;ms<br>**13.38**<br>1252KB<br>**21.18** | 2.82&nbsp;ms<br>**21.74**<br>940KB<br>**15.91** |

#### MySQL

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;Dapper<br>💾&nbsp;Dapper.AOT | 4.33&nbsp;ms<br>1.00<br>20.9KB<br>1.00 | 4.26&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>21.5KB<br>$\color{#d97706}{1.03}$ | 💾&nbsp;4.27&nbsp;ms<br>0.99<br>20.0KB<br>0.95 | 4.34&nbsp;ms<br>1.00<br>21.6KB<br>1.03 | 5.85&nbsp;ms<br>1.35<br>133KB<br>**6.35** | 🏆&nbsp;4.07&nbsp;ms<br>0.94<br>20.8KB<br>1.00 | 4.41&nbsp;ms<br>1.02<br>26.6KB<br>1.27 | 4.37&nbsp;ms<br>1.01<br>52.7KB<br>**2.52** | 4.48&nbsp;ms<br>1.03<br>33.6KB<br>1.60 |
| TxHundredInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 24.94&nbsp;ms<br>1.00<br>353KB<br>1.00 | 💾&nbsp;25.07&nbsp;ms<br>$\color{#d97706}{1.01}$<br>303KB<br>$\color{#65a30d}{0.86}$ | 25.44&nbsp;ms<br>1.02<br>457KB<br>1.29 | 26.77&nbsp;ms<br>1.07<br>611KB<br>1.73 | 🏆&nbsp;9.95&nbsp;ms<br>**0.40**<br>1137KB<br>**3.22** | 24.34&nbsp;ms<br>0.98<br>347KB<br>0.98 | 27.59&nbsp;ms<br>1.11<br>810KB<br>**2.30** | 31.38&nbsp;ms<br>1.26<br>2524KB<br>**7.16** | 30.61&nbsp;ms<br>1.23<br>1503KB<br>**4.26** |
| TxBulkInsert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;11.06&nbsp;ms<br>1.00<br>477KB<br>1.00 | 🏆&nbsp;10.85&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>481KB<br>$\color{#d97706}{1.01}$ | 17.74&nbsp;ms<br>1.60<br>6803KB<br>**14.28** | 10.95&nbsp;ms<br>0.99<br>488KB<br>1.02 | 84.51&nbsp;ms<br>**7.64**<br>20508KB<br>**43.04** | 18.68&nbsp;ms<br>1.69<br>6825KB<br>**14.32** | 18.83&nbsp;ms<br>1.70<br>3494KB<br>**7.33** | 15.81&nbsp;ms<br>1.43<br>3200KB<br>**6.71** | 24.83&nbsp;ms<br>**2.25**<br>6995KB<br>**14.68** |
| TxRollback | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 💾&nbsp;91.02&nbsp;ms<br>1.00<br>1301KB<br>1.00 | 93.25&nbsp;ms<br>$\color{#d97706}{1.02}$<br>1411KB<br>$\color{#d97706}{1.08}$ | 97.45&nbsp;ms<br>1.07<br>2238KB<br>1.72 | 105.11&nbsp;ms<br>1.15<br>3660KB<br>**2.81** | 🏆&nbsp;34.52&nbsp;ms<br>**0.38**<br>9201KB<br>**7.07** | 100.38&nbsp;ms<br>1.10<br>2462KB<br>1.89 | 103.17&nbsp;ms<br>1.13<br>4958KB<br>**3.81** | 125.96&nbsp;ms<br>1.38<br>15170KB<br>**11.66** | 127.82&nbsp;ms<br>1.40<br>8718KB<br>**6.70** |
| EnumInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 💾&nbsp;23.28&nbsp;ms<br>1.00<br>260KB<br>1.00 | 24.27&nbsp;ms<br>$\color{#d97706}{1.04}$<br>283KB<br>$\color{#ea580c}{1.09}$ | 24.95&nbsp;ms<br>1.07<br>388KB<br>1.49 | 26.77&nbsp;ms<br>1.15<br>520KB<br>**2.00** | 🏆&nbsp;8.49&nbsp;ms<br>**0.36**<br>836KB<br>**3.22** | 24.81&nbsp;ms<br>1.07<br>421KB<br>1.62 | 27.20&nbsp;ms<br>1.17<br>767KB<br>**2.95** | 28.88&nbsp;ms<br>1.24<br>2034KB<br>**7.83** | 30.45&nbsp;ms<br>1.31<br>1217KB<br>**4.69** |

#### PostgreSQL

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;1.47&nbsp;ms<br>1.00<br>8.4KB<br>1.00 | 1.49&nbsp;ms<br>$\color{#d97706}{1.01}$<br>9.3KB<br>$\color{#ea580c}{1.11}$ | 💾&nbsp;1.52&nbsp;ms<br>1.03<br>7.4KB<br>0.89 | 1.48&nbsp;ms<br>1.01<br>8.6KB<br>1.03 | 1.97&nbsp;ms<br>1.34<br>57.3KB<br>**6.86** | 1.52&nbsp;ms<br>1.03<br>8.1KB<br>0.97 | 1.54&nbsp;ms<br>1.05<br>12.2KB<br>1.46 | 1.54&nbsp;ms<br>1.05<br>23.3KB<br>**2.79** | 1.54&nbsp;ms<br>1.05<br>20.0KB<br>**2.39** |
| TxHundredInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 20.57&nbsp;ms<br>1.00<br>314KB<br>1.00 | 💾&nbsp;20.56&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>212KB<br>$\color{#16a34a}{0.68}$ | 20.84&nbsp;ms<br>1.01<br>310KB<br>0.99 | 22.36&nbsp;ms<br>1.09<br>433KB<br>1.38 | 🏆&nbsp;3.61&nbsp;ms<br>**0.18**<br>875KB<br>**2.79** | 20.55&nbsp;ms<br>1.00<br>282KB<br>0.90 | 24.49&nbsp;ms<br>1.19<br>704KB<br>**2.24** | 23.75&nbsp;ms<br>1.15<br>1817KB<br>**5.80** | 27.14&nbsp;ms<br>1.32<br>1520KB<br>**4.85** |
| TxBulkInsert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;4.97&nbsp;ms<br>1.00<br>340KB<br>1.00 | 🏆&nbsp;4.77&nbsp;ms<br>$\color{#2563eb}{0.96}$<br>341KB<br>$\color{#6b7280}{1.00}$ | 15.87&nbsp;ms<br>**3.19**<br>6844KB<br>**20.12** | 5.36&nbsp;ms<br>1.08<br>844KB<br>**2.48** | 42.87&nbsp;ms<br>**8.62**<br>16151KB<br>**47.47** | 15.71&nbsp;ms<br>**3.16**<br>6846KB<br>**20.12** | 21.21&nbsp;ms<br>**4.27**<br>3487KB<br>**10.25** | 11.01&nbsp;ms<br>**2.21**<br>1177KB<br>**3.46** | 26.21&nbsp;ms<br>**5.27**<br>7575KB<br>**22.26** |
| TxRollback | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 💾&nbsp;100.97&nbsp;ms<br>1.00<br>924KB<br>1.00 | 101.67&nbsp;ms<br>$\color{#d97706}{1.01}$<br>1030KB<br>$\color{#ea580c}{1.11}$ | 105.52&nbsp;ms<br>1.05<br>1541KB<br>1.67 | 113.29&nbsp;ms<br>1.12<br>2973KB<br>**3.22** | 🏆&nbsp;13.70&nbsp;ms<br>**0.14**<br>4220KB<br>**4.57** | 105.04&nbsp;ms<br>1.04<br>1754KB<br>1.90 | 117.07&nbsp;ms<br>1.16<br>4516KB<br>**4.89** | 126.14&nbsp;ms<br>1.25<br>10776KB<br>**11.66** | 128.53&nbsp;ms<br>1.27<br>8737KB<br>**9.46** |
| EnumInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 💾&nbsp;20.15&nbsp;ms<br>1.00<br>164KB<br>1.00 | 20.27&nbsp;ms<br>$\color{#d97706}{1.01}$<br>185KB<br>$\color{#ea580c}{1.12}$ | 20.74&nbsp;ms<br>1.03<br>254KB<br>1.54 | 20.93&nbsp;ms<br>1.04<br>351KB<br>**2.14** | 🏆&nbsp;3.23&nbsp;ms<br>**0.16**<br>657KB<br>**4.00** | 20.58&nbsp;ms<br>1.02<br>280KB<br>1.71 | 23.83&nbsp;ms<br>1.18<br>661KB<br>**4.02** | 23.01&nbsp;ms<br>1.14<br>1441KB<br>**8.77** | 25.01&nbsp;ms<br>1.24<br>1166KB<br>**7.09** |


### 调用形态对照（读取三态 / 写入两态）

同一个查询或写入，会话形态不同，开销差一个量级。下表是 PerfHub 同批实测（2,000 档，批次 `history-20261105-152440.json`，2026-11-05），读取按**每键**折算、写入按**每操作**，括号内为相对 per-op 形态的变化。

| 方言 | per-op（每操作新建会话） | 长会话（单会话 32 键循环） | 池化租还（每操作一租期） |
|---|---:|---:|---:|
| SQLite | 6.3 µs / 2.3KB | 3.7 µs / 744B（−41% / −67%） | 3.8 µs / 882B（−39% / −61%） |
| MySQL | 209 µs / 5.3KB | 195 µs / 3.2KB（−7% / −40%） | 196 µs / 3.9KB（−6% / −27%） |
| PostgreSQL | 211 µs / 3.7KB | 210 µs / 2.2KB（−1% / −42%） | 178 µs / 2.4KB（−16% / −34%） |

| 方言 | Insert per-op | Insert 池化 | Update per-op | Update 池化 | BulkInsert（2,000 行） | BulkInsert 池化 |
|---|---:|---:|---:|---:|---:|---:|
| SQLite | 19.2 µs / 2.8KB | 15.3 µs / 1.1KB（−20% / −60%） | 7.8 µs / 3.1KB | 5.4 µs / 1.1KB（−31% / −64%） | 2.63 ms / 1.7MB | 2.82 ms / 1.7MB（+7% / 0%） |
| MySQL | 4.23 ms / 17.8KB | 3.94 ms / 14.7KB（−7% / −18%） | 207 µs / 6.5KB | 166 µs / 4.0KB（−20% / −39%） | 12.1 ms / 505KB | 11.6 ms / 501KB（−5% / −1%） |
| PostgreSQL | 1.36 ms / 7.9KB | 1.31 ms / 5.3KB（−4% / −33%） | 1.46 ms / 8.8KB | 1.33 ms / 6.2KB（−9% / −29%） | 4.56 ms / 342KB | 4.93 ms / 348KB（+8% / +2%） |

**多键点读**：同形状的多键读改用 `GetByKeyManyAsync`（SQLite 单参数 `json_each`、PostgreSQL `= ANY(数组)`、MySQL 分批 `IN`），比逐键 `GetAsync` 快 5.0 至 7.8 倍、内存低 3.2 至 3.5 倍（`bench/crossbench/ReadManyProbe`，100 / 1000 / 10000 三档，2026-11-05）。

**形态选择判据**：

- 会话寿命跨多个操作（同一请求里多次读写、报表批次）：用长会话，别每操作新建。读取省约四成时延与六成内存（SQLite），写入省两三成。
- 每请求一会话（Web/服务端的典型形状）：用 `DataSessionPool` 租还，收益接近长会话，且不需要在业务代码里传会话对象。
- 一次性调用或低频操作：per-op 形态即可，池化只会多一次租还。
- 单次大调用（整批批量插入）：池化不划算，租还开销摊在单次调用上，实测 SQLite/PostgreSQL 反而慢 7% 至 8%，MySQL 持平。

### 复现

```bash
# 周度全量（横评 + 微基准 + PerfHub + 门禁 + 报告，约 34 分钟，需本机 PG / MySQL 容器）
dotnet run --project tools/PalORM.PerfCli -c Release -- full --with-crossbench
# 只跑横评批
dotnet run --project tools/PalORM.PerfCli -c Release -- crossbench
```

说明：9 组同批同轮，比值只在同一批内可比；跨机绝对值不可比。87 行完整明细（全部 17 操作、两档、三方言，含时延与内存百分比）见 [性能全量报告-2026-11-02.md](性能全量报告-2026-11-02.md)。PalORM 自身回归（微基准、并发、内存曲线、长稳）由 PerfHub 夹具单独度量，见 [bench/perfhub/README.md](../bench/perfhub/README.md)。

## 🚀 快速开始

> `.NET 11`（全部包单目标 `net11.0`）+ SDK 版本由 `global.json` 精确钉住（当前 `11.0.100-rc.3.26522.148`），IDE 需支持 Roslyn 源生成器；Native AOT 发布另需约 4 GB 内存（ILC 编译期，非运行时）。

```xml
<PackageReference Include="PalORM.PostgreSql" Version="6.8.0" />   <!-- 或 PalORM.MySql / PalORM.Sqlite -->
```

包内含 `PalORM.Core` 与 `PalORM.SourceGen`；五步跑通，完整写法与决策项见[最佳实践](docs/最佳实践.md)。

### 定义实体

```csharp
using PalORM;

[Table("users")]
public partial class User
{
    [Key] public long Id { get; set; }
    [Column("email")] public string Email { get; set; } = "";
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}
```

> 非可空引用属性遇数据库 NULL 会抛 `SqlNullValueException`（响亮失败，不返 null）；可能存 NULL 的列请声明为可空（`string?`）。

### 创建会话

```csharp
using var db = await DataSession<PostgreSqlProvider>.CreateAsync(new DbOptions
{
    ConnectionString = "Host=localhost;Username=user;Password=xxx;Database=mydb"
});
```

同一作用域复用一个 `db`（第 3 次同形状操作自动晋升命令复用槽）；启动期可 `PreWarmAsync(options, count: 10)` 预热连接池。

按请求创建会话（Web / API）建议用 `DataSessionPool`：会话、连接、命令复用槽跨请求存活，归还时瞬态状态复位。本机 SQLite 基准中，`GetAsync` 比每操作新建会话快约 3.8 倍、分配低约 45%。

```csharp
var pool = DataSessionPool<PostgreSqlProvider>.Create(options, maxIdleSize: 16);

await using var db = await pool.RentAsync();   // DisposeAsync 即归还，不关闭物理连接
```

### 建表与种子

```csharp
await db.MigrateAsync();                                                                    // 三方言 DDL，幂等；FK 按依赖拓扑序
await db.SeedAsync([new User { Id = 1, Email = "admin@example.com", CreatedAt = DateTime.UtcNow }]);   // 事务内幂等 Upsert
List<string> missing = await db.ValidateSchemaAsync<User>();                                // 对照实体查缺列
```

### CRUD

```csharp
var user  = await db.InsertAsync(new User { Email = "alice@example.com", CreatedAt = DateTime.UtcNow });  // 自增 ID 回填
var alice = await db.GetAsync<User>(user.Id);                                                             // 单键直查
var all   = await db.From<User>().Where($"email LIKE {"%@example.com%"}").ToListAsync();

alice.Email = "new@example.com";
await db.UpdateAsync(alice);            // 乐观锁实体自动校验版本，冲突抛 ConcurrencyConflictException
await db.SaveAsync(alice);              // 默认主键走 Insert，非默认主键单行 UPSERT
await db.DeleteAsync<User>(alice.Id);   // 软删实体自动转 UPDATE
```

**单键直查用 `GetAsync`**：同口径比 `From<T>().Where(Id==x).FirstOrDefaultAsync()` 省约 880 B/操作、快 25~30%。

### 编译查询（重复形状）

```csharp
using var plan = db.Compile<User>(
    """SELECT "Id", "Email" FROM "Users" WHERE "Id" = @p0""", "@p0");
var user = (await plan.ExecuteAsync([userId]))[0];
```

计划把 SQL 与参数模板固定为会话级对象，重复执行只写参数值并复用命令。本机 SQLite 基准中，单行查询比优化后的 Fluent 路径快 22.6%、分配低 29.8%。SQL 逐字执行，默认过滤不适用；参数运行时类型须稳定，计划须在会话释放前 `Dispose`。

### 批量操作

```csharp
await db.BulkInsertAsync(users);          // PG Binary COPY / MySQL BulkCopy 或多值 INSERT / SQLite 行预编译
await db.BulkUpdateAsync(users);          // 乐观锁实体：版本化单语句（能力不全回退 DbBatch）
await db.BulkUpdateBatchAsync(users);     // 单语句多行 SET（PG FROM VALUES / MySQL JOIN VALUES ROW）
await db.BulkDeleteAsync<User>(keyList);  // PG = ANY / SQLite json_each / MySQL IN 分批
await db.BulkMergeAsync(users);           // 多行 UPSERT
```

默认键的新行走逐条 INSERT 保留 ID 回填；`[ConcurrencyCheck]` 实体维持逐条。

## 🔧 配置

> 三预设起步（Development / Production / Testing），24 项配置按需覆盖；连接串自动调优只补默认。全量项见 [docs/配置参考.md](docs/配置参考.md)。

```csharp
var dev  = DbOptions.Development(connectionString);            // 开发环境
var prod = DbOptions.Production(connectionString, readConn);  // 生产（含连接池 + 读写分离）
var test = DbOptions.Testing(connectionString);               // 测试（零重试 + 短超时）
var env  = DbOptions.FromEnvironment("PALORM_CONNECTION");    // 环境变量（Docker/K8s 友好）
```

`FromEnvironment` 读取 `PALORM_*` 环境变量（`PALORM_CONNECTION` 必需），组装后自动 `Validate()`；全量清单见 [docs/配置参考.md](docs/配置参考.md)。

## 数据库兼容性

| 数据库     | 已验证版本                    | 驱动                                   | 硬门槛                                                                     |
| ---------- | ----------------------------- | -------------------------------------- | -------------------------------------------------------------------------- |
| PostgreSQL | **18.6**（CI `postgres:18`）  | Npgsql 10.0.3                          | 未设；批量写需 9.3+，JSONB 路径查询 9.4+，咨询锁 9.5+                      |
| MySQL      | **8.4.11**（CI `mysql:8.4`）  | MySqlConnector 2.6.2                   | **8.0.19**（`UPDATE JOIN VALUES ROW`，更低回退 CASE WHEN）；8.0 以下不支持 |
| SQLite     | **3.54.0**（SQLite3MC 2.6.1） | Microsoft.Data.Sqlite.Core 11.0.0-rc.2 | **3.35+**（`INSERT ... RETURNING`；批删 `json_each` 需 3.38+）             |

> 已验证版本为 CI 实测；门槛是源码可查的语法下限。构建与基准环境见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 🤝 贡献

欢迎 issue 与 PR；提交前保持 0 警告（`TreatWarningsAsErrors`），性能改动附前后对比。流程见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 💬 社区

<div align="center">

<img src="docs/qq-group.jpg" width="260" alt="QQ 群二维码：C#/.NET 新技术交流群（群号 1125599744）">

**C#/.NET 新技术交流群**（群号 **1125599744**）

</div>

## 🤖 AI 代码声明

本项目的全部代码均由 AI 生成。

这是一个实验性质项目，不建议在生产环境使用。

## 📄 许可证

[Apache-2.0](LICENSE)

v6.7.0 及以前的已发布版本仍为 AGPL-3.0-only；<br>
v6.8.0 及以后的新版本统一变更为 Apache-2.0 发布。
