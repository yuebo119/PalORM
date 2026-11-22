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

> 版本（2026-11-22 核对）：PalORM 6.9.0 · Dapper 2.1.100 + Dapper.AOT 1.1.0 · EF Core 10.0.14 · RepoDb 1.17.0。

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

| 选型维度             | PalORM 6.9.0                                                                    | Dapper.AOT 1.1.0                                                                    | RepoDb 1.17.0                                              | EF Core 10.0.14                                     | FreeSql 3.5.311                                                                  |
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
> **数据来源**：9 组同批同轮采集，2026-11-22，批次 `crossbench-20261122-214255.json`（依赖 GA 线升级后首个完整批：Npgsql 11 / MySqlConnector 3.0）。9 组为 PalORM / Dapper / Dapper.AOT / linq2db / EF Core / SqlSugar / FreeSql / RepoDb 与 ADO.NET 手写 SQL（基准），各用生态惯用写法。完整报告见 [性能全量报告-2026-11-22.md](性能全量报告-2026-11-22.md)。

### 总览

9 组 × 三方言 × 两档（2000 / 20000）× 17 操作（事务仅最小档）= **87 格**，每操作两遍反序取平均；所有倍数以同批 ADO.NET 手写 SQL 为基准 1.00，小于 1 为更快。

| 组 | 胜场 /87 | 时延中位比 | 内存中位比 |
|---|---:|---:|---:|
| ADO | — | 1.00 | 1.00 |
| PalORM | 32 | 1.00 | 1.00 |
| Dapper.AOT | 8 | 1.06 | 1.15 |
| RepoDb | 1 | 1.16 | 1.52 |
| EF Core | 8 | 3.14 | 8.77 |
| Dapper | 1 | 1.10 | 1.69 |
| FreeSql | 1 | 1.44 | 1.99 |
| linq2db | 1 | 1.41 | 2.89 |
| SqlSugar | 1 | 2.06 | 3.52 |

- **PalORM**：胜场 32/87（37%），时延中位比 **1.00**（全距 0.48 ~ 1.14），内存中位比 **1.00**，无 1.5 倍以上的劣化格。单行读写与批量写与 ADO 地板持平，PG 批删 0.48 倍（json_each 形态）、PG 批量 UPSERT 0.50 倍（UNNEST 形态）。
- **最大离群**：SqlSugar UpsertBatch（SQLite 110.4 倍、101.3 倍）、EF Core 单条 Insert（62.5 ~ 63.3 倍，每操作 DbContext 形态）。

### 操作对照（时延 [倍数] 与内存 [相对基准]，颜色同百分比六档）

> 🏆 = 时延最快组 · 💾 = 内存最省组（均含 ADO 基准）

#### SQLite（2000 与 20000 档）

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 7.8&nbsp;µs<br>1.00<br>2.1&nbsp;KB<br>1.00 | 🏆&nbsp;6.3&nbsp;µs<br>$\color{#65a30d}{0.80}$<br>2.2&nbsp;KB<br>$\color{#d97706}{1.03}$ | 💾&nbsp;7.0&nbsp;µs<br>0.90<br>2.0&nbsp;KB<br>0.97 | 16.6&nbsp;µs<br>**2.13**<br>4.5&nbsp;KB<br>**2.13** | 142.1&nbsp;µs<br>**18.22**<br>49.2&nbsp;KB<br>**23.29** | 10.6&nbsp;µs<br>1.36<br>2.8&nbsp;KB<br>1.34 | 37.1&nbsp;µs<br>**4.76**<br>10.2&nbsp;KB<br>**4.84** | 23.8&nbsp;µs<br>**3.04**<br>6.4&nbsp;KB<br>**3.03** | 84.2&nbsp;µs<br>**10.79**<br>30.0&nbsp;KB<br>**14.21** |
| GetByKey | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 6.6&nbsp;µs<br>1.00<br>2.1&nbsp;KB<br>1.00 | 🏆&nbsp;6.5&nbsp;µs<br>$\color{#2563eb}{0.99}$<br>2.2&nbsp;KB<br>$\color{#d97706}{1.03}$ | 💾&nbsp;7.3&nbsp;µs<br>1.10<br>2.1&nbsp;KB<br>0.97 | 9.3&nbsp;µs<br>1.40<br>4.4&nbsp;KB<br>**2.07** | 82.3&nbsp;µs<br>**12.48**<br>48.9&nbsp;KB<br>**23.13** | 7.7&nbsp;µs<br>1.17<br>2.8&nbsp;KB<br>1.33 | 33.0&nbsp;µs<br>**4.99**<br>10.4&nbsp;KB<br>**4.92** | 17.1&nbsp;µs<br>**2.59**<br>6.4&nbsp;KB<br>**3.02** | 76.5&nbsp;µs<br>**11.58**<br>29.8&nbsp;KB<br>**14.11** |
| GetAllAsync | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 1.04&nbsp;ms<br>1.00<br>309&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;1.03&nbsp;ms<br>$\color{#2563eb}{0.99}$<br>292&nbsp;KB<br>$\color{#2563eb}{0.95}$ | 1.21&nbsp;ms<br>1.16<br>356&nbsp;KB<br>1.15 | 1.24&nbsp;ms<br>1.20<br>372&nbsp;KB<br>1.21 | 1.48&nbsp;ms<br>1.42<br>728&nbsp;KB<br>**2.36** | 1.48&nbsp;ms<br>1.42<br>513&nbsp;KB<br>1.66 | 1.48&nbsp;ms<br>1.42<br>314&nbsp;KB<br>1.02 | 1.45&nbsp;ms<br>1.39<br>311&nbsp;KB<br>1.01 | 3.42&nbsp;ms<br>**3.29**<br>580&nbsp;KB<br>1.88 |
| GetAllAsync | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 10.45&nbsp;ms<br>1.00<br>3343&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;10.29&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>2984&nbsp;KB<br>$\color{#65a30d}{0.89}$ | 12.17&nbsp;ms<br>1.16<br>3814&nbsp;KB<br>1.14 | 12.56&nbsp;ms<br>1.20<br>3972&nbsp;KB<br>1.19 | 14.38&nbsp;ms<br>1.38<br>7146&nbsp;KB<br>**2.14** | 14.88&nbsp;ms<br>1.42<br>5385&nbsp;KB<br>1.61 | 14.75&nbsp;ms<br>1.41<br>3356&nbsp;KB<br>1.00 | 14.33&nbsp;ms<br>1.37<br>3353&nbsp;KB<br>1.00 | 33.93&nbsp;ms<br>**3.25**<br>5920&nbsp;KB<br>1.77 |
| QueryAll | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 1.02&nbsp;ms<br>1.00<br>308&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;1.02&nbsp;ms<br>$\color{#2563eb}{1.00}$<br>293&nbsp;KB<br>$\color{#2563eb}{0.95}$ | 1.21&nbsp;ms<br>1.18<br>356&nbsp;KB<br>1.15 | 1.23&nbsp;ms<br>1.21<br>372&nbsp;KB<br>1.21 | 1.43&nbsp;ms<br>1.40<br>728&nbsp;KB<br>**2.36** | 1.46&nbsp;ms<br>1.43<br>513&nbsp;KB<br>1.66 | 1.47&nbsp;ms<br>1.44<br>314&nbsp;KB<br>1.02 | 1.44&nbsp;ms<br>1.41<br>311&nbsp;KB<br>1.01 | 3.40&nbsp;ms<br>**3.33**<br>580&nbsp;KB<br>1.88 |
| QueryAll | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 10.96&nbsp;ms<br>1.00<br>3344&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;10.33&nbsp;ms<br>$\color{#2563eb}{0.94}$<br>2984&nbsp;KB<br>$\color{#65a30d}{0.89}$ | 12.24&nbsp;ms<br>1.12<br>3814&nbsp;KB<br>1.14 | 12.37&nbsp;ms<br>1.13<br>3972&nbsp;KB<br>1.19 | 14.67&nbsp;ms<br>1.34<br>7146&nbsp;KB<br>**2.14** | 14.85&nbsp;ms<br>1.36<br>5385&nbsp;KB<br>1.61 | 14.91&nbsp;ms<br>1.36<br>3357&nbsp;KB<br>1.00 | 14.75&nbsp;ms<br>1.35<br>3354&nbsp;KB<br>1.00 | 33.64&nbsp;ms<br>**3.07**<br>5921&nbsp;KB<br>1.77 |
| Insert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 18.9&nbsp;µs<br>1.00<br>2.8&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;18.4&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>2.7&nbsp;KB<br>$\color{#2563eb}{0.98}$ | 18.5&nbsp;µs<br>0.98<br>2.9&nbsp;KB<br>1.04 | 20.3&nbsp;µs<br>1.07<br>3.0&nbsp;KB<br>1.09 | 1.18&nbsp;ms<br>**62.51**<br>55.3&nbsp;KB<br>**19.89** | 19.9&nbsp;µs<br>1.05<br>3.6&nbsp;KB<br>1.30 | 31.1&nbsp;µs<br>1.65<br>5.8&nbsp;KB<br>**2.07** | 1.12&nbsp;ms<br>**59.29**<br>19.4&nbsp;KB<br>**6.98** | 1.13&nbsp;ms<br>**59.71**<br>15.0&nbsp;KB<br>**5.40** |
| Insert | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 18.7&nbsp;µs<br>1.00<br>2.9&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;17.9&nbsp;µs<br>$\color{#2563eb}{0.96}$<br>2.7&nbsp;KB<br>$\color{#2563eb}{0.95}$ | 18.2&nbsp;µs<br>0.97<br>2.8&nbsp;KB<br>0.99 | 20.6&nbsp;µs<br>1.10<br>3.0&nbsp;KB<br>1.05 | 1.18&nbsp;ms<br>**63.32**<br>55.6&nbsp;KB<br>**19.41** | 19.8&nbsp;µs<br>1.06<br>3.6&nbsp;KB<br>1.27 | 31.6&nbsp;µs<br>1.69<br>5.7&nbsp;KB<br>1.99 | 1.09&nbsp;ms<br>**58.20**<br>19.5&nbsp;KB<br>**6.79** | 1.15&nbsp;ms<br>**61.48**<br>15.8&nbsp;KB<br>**5.50** |
| Update | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;7.6&nbsp;µs<br>1.00<br>2.5&nbsp;KB<br>1.00 | 7.8&nbsp;µs<br>$\color{#d97706}{1.02}$<br>2.9&nbsp;KB<br>$\color{#ea580c}{1.16}$ | 8.1&nbsp;µs<br>1.06<br>2.8&nbsp;KB<br>1.11 | 9.6&nbsp;µs<br>1.26<br>4.1&nbsp;KB<br>1.62 | 85.6&nbsp;µs<br>**11.26**<br>52.5&nbsp;KB<br>**20.85** | 8.6&nbsp;µs<br>1.13<br>3.3&nbsp;KB<br>1.30 | 21.0&nbsp;µs<br>**2.76**<br>7.4&nbsp;KB<br>**2.94** | 14.4&nbsp;µs<br>1.90<br>19.7&nbsp;KB<br>**7.84** | 35.6&nbsp;µs<br>**4.69**<br>14.6&nbsp;KB<br>**5.78** |
| Update | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;8.7&nbsp;µs<br>1.00<br>2.5&nbsp;KB<br>1.00 | 🏆&nbsp;8.0&nbsp;µs<br>$\color{#2563eb}{0.91}$<br>2.9&nbsp;KB<br>$\color{#ea580c}{1.16}$ | 8.4&nbsp;µs<br>0.97<br>2.8&nbsp;KB<br>1.11 | 9.8&nbsp;µs<br>1.12<br>4.1&nbsp;KB<br>1.62 | 93.0&nbsp;µs<br>**10.68**<br>52.6&nbsp;KB<br>**20.82** | 8.6&nbsp;µs<br>0.98<br>3.3&nbsp;KB<br>1.30 | 23.9&nbsp;µs<br>**2.75**<br>7.4&nbsp;KB<br>**2.94** | 17.6&nbsp;µs<br>**2.02**<br>19.8&nbsp;KB<br>**7.83** | 38.0&nbsp;µs<br>**4.37**<br>14.6&nbsp;KB<br>**5.77** |
| BulkInsert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;linq2db | 2.74&nbsp;ms<br>1.00<br>1758&nbsp;KB<br>1.00 | 🏆&nbsp;2.68&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>1758&nbsp;KB<br>$\color{#2563eb}{1.00}$ | 4.52&nbsp;ms<br>1.65<br>4353&nbsp;KB<br>**2.48** | 18.11&nbsp;ms<br>**6.61**<br>2709&nbsp;KB<br>1.54 | 23.42&nbsp;ms<br>**8.55**<br>17.4&nbsp;MB<br>**10.12** | 4.54&nbsp;ms<br>1.66<br>4354&nbsp;KB<br>**2.48** | 10.78&nbsp;ms<br>**3.94**<br>6647&nbsp;KB<br>**3.78** | 💾&nbsp;5.05&nbsp;ms<br>1.84<br>1278&nbsp;KB<br>0.73 | 7.56&nbsp;ms<br>**2.76**<br>4509&nbsp;KB<br>**2.57** |
| BulkInsert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;linq2db | 🏆&nbsp;29.16&nbsp;ms<br>1.00<br>17.2&nbsp;MB<br>1.00 | 29.55&nbsp;ms<br>$\color{#d97706}{1.01}$<br>17.2&nbsp;MB<br>$\color{#2563eb}{1.00}$ | 48.28&nbsp;ms<br>1.66<br>42.5&nbsp;MB<br>**2.48** | 172.80&nbsp;ms<br>**5.92**<br>26.1&nbsp;MB<br>1.52 | 255.50&nbsp;ms<br>**8.76**<br>173.3&nbsp;MB<br>**10.10** | 50.07&nbsp;ms<br>1.72<br>42.5&nbsp;MB<br>**2.48** | 118.80&nbsp;ms<br>**4.07**<br>66.1&nbsp;MB<br>**3.85** | 💾&nbsp;53.80&nbsp;ms<br>1.84<br>10.9&nbsp;MB<br>0.63 | 82.14&nbsp;ms<br>**2.82**<br>44.9&nbsp;MB<br>**2.62** |
| BulkUpdate | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;2.44&nbsp;ms<br>1.00<br>1735&nbsp;KB<br>1.00 | 2.46&nbsp;ms<br>$\color{#d97706}{1.01}$<br>1736&nbsp;KB<br>$\color{#d97706}{1.00}$ | 8.95&nbsp;ms<br>**3.67**<br>5642&nbsp;KB<br>**3.25** | 7.33&nbsp;ms<br>**3.00**<br>5308&nbsp;KB<br>**3.06** | 27.95&nbsp;ms<br>**11.45**<br>18.0&nbsp;MB<br>**10.62** | 9.24&nbsp;ms<br>**3.79**<br>6502&nbsp;KB<br>**3.75** | 14.00&nbsp;ms<br>**5.74**<br>7202&nbsp;KB<br>**4.15** | 23.39&nbsp;ms<br>**9.58**<br>38.5&nbsp;MB<br>**22.74** | 5.96&nbsp;ms<br>**2.44**<br>4503&nbsp;KB<br>**2.60** |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;27.03&nbsp;ms<br>1.00<br>17.0&nbsp;MB<br>1.00 | 27.42&nbsp;ms<br>$\color{#d97706}{1.01}$<br>17.0&nbsp;MB<br>$\color{#d97706}{1.00}$ | 95.63&nbsp;ms<br>**3.54**<br>55.2&nbsp;MB<br>**3.25** | 77.02&nbsp;ms<br>**2.85**<br>51.9&nbsp;MB<br>**3.05** | 337.24&nbsp;ms<br>**12.47**<br>179.5&nbsp;MB<br>**10.56** | 100.37&nbsp;ms<br>**3.71**<br>63.6&nbsp;MB<br>**3.74** | 146.32&nbsp;ms<br>**5.41**<br>73.4&nbsp;MB<br>**4.32** | 232.00&nbsp;ms<br>**8.58**<br>385.3&nbsp;MB<br>**22.67** | 75.08&nbsp;ms<br>**2.78**<br>44.8&nbsp;MB<br>**2.63** |
| BulkDelete | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;832.8&nbsp;µs<br>1.00<br>197&nbsp;KB<br>1.00 | 🏆&nbsp;831.0&nbsp;µs<br>$\color{#2563eb}{1.00}$<br>198&nbsp;KB<br>$\color{#d97706}{1.01}$ | 23.73&nbsp;ms<br>**28.49**<br>1605&nbsp;KB<br>**8.14** | 21.43&nbsp;ms<br>**25.73**<br>3207&nbsp;KB<br>**16.27** | 11.66&nbsp;ms<br>**14.00**<br>2520&nbsp;KB<br>**12.78** | 23.69&nbsp;ms<br>**28.45**<br>1605&nbsp;KB<br>**8.14** | 1.61&nbsp;ms<br>1.93<br>569&nbsp;KB<br>**2.89** | 2.55&nbsp;ms<br>**3.06**<br>271&nbsp;KB<br>1.37 | 2.43&nbsp;ms<br>**2.92**<br>353&nbsp;KB<br>1.79 |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;9.25&nbsp;ms<br>1.00<br>2077&nbsp;KB<br>1.00 | 🏆&nbsp;9.03&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>2078&nbsp;KB<br>$\color{#d97706}{1.00}$ | 235.14&nbsp;ms<br>**25.42**<br>15.6&nbsp;MB<br>**7.71** | 213.97&nbsp;ms<br>**23.13**<br>31.0&nbsp;MB<br>**15.27** | 122.99&nbsp;ms<br>**13.30**<br>24.6&nbsp;MB<br>**12.13** | 238.29&nbsp;ms<br>**25.76**<br>15.7&nbsp;MB<br>**7.72** | 20.51&nbsp;ms<br>**2.22**<br>6101&nbsp;KB<br>**2.94** | 16.93&nbsp;ms<br>1.83<br>2790&nbsp;KB<br>1.34 | 37.29&nbsp;ms<br>**4.03**<br>4341&nbsp;KB<br>**2.09** |
| UpsertBatch | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;2.52&nbsp;ms<br>1.00<br>1736&nbsp;KB<br>1.00 | 2.60&nbsp;ms<br>$\color{#d97706}{1.03}$<br>1753&nbsp;KB<br>$\color{#d97706}{1.01}$ | 114.97&nbsp;ms<br>**45.61**<br>8172&nbsp;KB<br>**4.71** | 19.62&nbsp;ms<br>**7.78**<br>11.6&nbsp;MB<br>**6.85** | 21.01&nbsp;ms<br>**8.34**<br>5506&nbsp;KB<br>**3.17** | 4.38&nbsp;ms<br>1.74<br>4334&nbsp;KB<br>**2.50** | 3.82&nbsp;ms<br>1.52<br>2556&nbsp;KB<br>1.47 | 23.36&nbsp;ms<br>**9.27**<br>42.8&nbsp;MB<br>**25.23** | 278.23&nbsp;ms<br>**110.37**<br>219.9&nbsp;MB<br>**129.73** |
| UpsertBatch | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;27.59&nbsp;ms<br>1.00<br>17.0&nbsp;MB<br>1.00 | 28.72&nbsp;ms<br>$\color{#d97706}{1.04}$<br>17.2&nbsp;MB<br>$\color{#d97706}{1.01}$ | 1.14&nbsp;s<br>**41.29**<br>79.8&nbsp;MB<br>**4.69** | 194.76&nbsp;ms<br>**7.06**<br>116.2&nbsp;MB<br>**6.83** | 1.59&nbsp;s<br>**57.60**<br>57.6&nbsp;MB<br>**3.39** | 47.86&nbsp;ms<br>1.73<br>42.4&nbsp;MB<br>**2.49** | 41.31&nbsp;ms<br>1.50<br>25.8&nbsp;MB<br>1.52 | 231.51&nbsp;ms<br>**8.39**<br>427.7&nbsp;MB<br>**25.16** | 2.80&nbsp;s<br>**101.35**<br>2365.1&nbsp;MB<br>**139.14** |
| KeysetPage | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;62.4&nbsp;µs<br>1.00<br>18.0&nbsp;KB<br>1.00 | 🏆&nbsp;62.3&nbsp;µs<br>$\color{#2563eb}{1.00}$<br>18.3&nbsp;KB<br>$\color{#d97706}{1.02}$ | 96.7&nbsp;µs<br>1.55<br>20.7&nbsp;KB<br>1.15 | 73.4&nbsp;µs<br>1.18<br>25.0&nbsp;KB<br>1.39 | 167.3&nbsp;µs<br>**2.68**<br>86.7&nbsp;KB<br>**4.83** | 85.3&nbsp;µs<br>1.37<br>29.3&nbsp;KB<br>1.63 | 112.8&nbsp;µs<br>1.81<br>28.0&nbsp;KB<br>1.56 | 89.1&nbsp;µs<br>1.43<br>23.3&nbsp;KB<br>1.30 | 248.7&nbsp;µs<br>**3.99**<br>63.2&nbsp;KB<br>**3.52** |
| KeysetPage | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;60.7&nbsp;µs<br>1.00<br>18.0&nbsp;KB<br>1.00 | 61.3&nbsp;µs<br>$\color{#d97706}{1.01}$<br>18.3&nbsp;KB<br>$\color{#d97706}{1.02}$ | 94.7&nbsp;µs<br>1.56<br>20.7&nbsp;KB<br>1.15 | 71.0&nbsp;µs<br>1.17<br>25.0&nbsp;KB<br>1.39 | 166.9&nbsp;µs<br>**2.75**<br>86.7&nbsp;KB<br>**4.83** | 84.0&nbsp;µs<br>1.38<br>29.3&nbsp;KB<br>1.63 | 112.3&nbsp;µs<br>1.85<br>28.0&nbsp;KB<br>1.56 | 89.1&nbsp;µs<br>1.47<br>23.3&nbsp;KB<br>1.30 | 251.5&nbsp;µs<br>**4.14**<br>63.3&nbsp;KB<br>**3.52** |
| WhereIn | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 140.9&nbsp;µs<br>1.00<br>47.4&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;137.9&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>41.7&nbsp;KB<br>$\color{#65a30d}{0.88}$ | 311.0&nbsp;µs<br>**2.21**<br>101&nbsp;KB<br>**2.13** | 275.6&nbsp;µs<br>1.95<br>90.9&nbsp;KB<br>1.92 | 292.3&nbsp;µs<br>**2.07**<br>203&nbsp;KB<br>**4.29** | 263.6&nbsp;µs<br>1.87<br>101&nbsp;KB<br>**2.14** | 175.6&nbsp;µs<br>1.25<br>45.3&nbsp;KB<br>0.96 | 152.2&nbsp;µs<br>1.08<br>42.0&nbsp;KB<br>0.89 | 371.4&nbsp;µs<br>**2.63**<br>108&nbsp;KB<br>**2.29** |
| WhereIn | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 136.4&nbsp;µs<br>1.00<br>48.0&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;133.3&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>42.3&nbsp;KB<br>$\color{#65a30d}{0.88}$ | 260.8&nbsp;µs<br>1.91<br>102&nbsp;KB<br>**2.12** | 272.5&nbsp;µs<br>2.00<br>91.7&nbsp;KB<br>1.91 | 294.4&nbsp;µs<br>**2.16**<br>204&nbsp;KB<br>**4.25** | 262.1&nbsp;µs<br>1.92<br>102&nbsp;KB<br>**2.13** | 172.3&nbsp;µs<br>1.26<br>49.4&nbsp;KB<br>1.03 | 152.8&nbsp;µs<br>1.12<br>43.3&nbsp;KB<br>0.90 | 376.8&nbsp;µs<br>**2.76**<br>111&nbsp;KB<br>**2.31** |
| Count | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;4.7&nbsp;µs<br>1.00<br>1.0&nbsp;KB<br>1.00 | 5.3&nbsp;µs<br>$\color{#ea580c}{1.13}$<br>1.8&nbsp;KB<br>$\color{#dc2626}{1.78}$ | 5.2&nbsp;µs<br>1.10<br>1.0&nbsp;KB<br>1.04 | 5.5&nbsp;µs<br>1.16<br>1.3&nbsp;KB<br>1.33 | 55.9&nbsp;µs<br>**11.89**<br>46.2&nbsp;KB<br>**46.20** | 5.0&nbsp;µs<br>1.06<br>1.1&nbsp;KB<br>1.07 | 35.3&nbsp;µs<br>**7.51**<br>5.8&nbsp;KB<br>**5.85** | 6.7&nbsp;µs<br>1.41<br>2.2&nbsp;KB<br>**2.24** | 64.4&nbsp;µs<br>**13.70**<br>25.5&nbsp;KB<br>**25.51** |
| Count | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;9.8&nbsp;µs<br>1.00<br>1.0&nbsp;KB<br>1.00 | 10.2&nbsp;µs<br>$\color{#d97706}{1.04}$<br>1.8&nbsp;KB<br>$\color{#dc2626}{1.77}$ | 🏆&nbsp;8.7&nbsp;µs<br>0.89<br>1.1&nbsp;KB<br>1.04 | 10.2&nbsp;µs<br>1.04<br>1.3&nbsp;KB<br>1.32 | 65.3&nbsp;µs<br>**6.70**<br>46.2&nbsp;KB<br>**45.52** | 9.7&nbsp;µs<br>0.99<br>1.1&nbsp;KB<br>1.07 | 248.9&nbsp;µs<br>**25.53**<br>6.4&nbsp;KB<br>**6.35** | 8.9&nbsp;µs<br>0.91<br>2.3&nbsp;KB<br>**2.22** | 272.3&nbsp;µs<br>**27.92**<br>26.1&nbsp;KB<br>**25.71** |

#### MySQL（2000 与 20000 档）

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;209.2&nbsp;µs<br>1.00<br>3.9&nbsp;KB<br>1.00 | 🏆&nbsp;194.6&nbsp;µs<br>$\color{#2563eb}{0.93}$<br>4.3&nbsp;KB<br>$\color{#ea580c}{1.11}$ | 233.2&nbsp;µs<br>1.11<br>4.9&nbsp;KB<br>1.26 | 228.6&nbsp;µs<br>1.09<br>8.1&nbsp;KB<br>**2.07** | 739.8&nbsp;µs<br>**3.54**<br>94.8&nbsp;KB<br>**24.33** | 217.9&nbsp;µs<br>1.04<br>5.5&nbsp;KB<br>1.42 | 434.9&nbsp;µs<br>**2.08**<br>15.8&nbsp;KB<br>**4.04** | 267.4&nbsp;µs<br>1.28<br>25.7&nbsp;KB<br>**6.60** | 494.4&nbsp;µs<br>**2.36**<br>35.5&nbsp;KB<br>**9.11** |
| GetByKey | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;218.1&nbsp;µs<br>1.00<br>3.9&nbsp;KB<br>1.00 | 222.1&nbsp;µs<br>$\color{#d97706}{1.02}$<br>4.4&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 226.7&nbsp;µs<br>1.04<br>4.9&nbsp;KB<br>1.24 | 263.5&nbsp;µs<br>1.21<br>8.1&nbsp;KB<br>**2.08** | 776.3&nbsp;µs<br>**3.56**<br>95.0&nbsp;KB<br>**24.25** | 227.9&nbsp;µs<br>1.05<br>5.5&nbsp;KB<br>1.41 | 463.3&nbsp;µs<br>**2.12**<br>16.0&nbsp;KB<br>**4.08** | 284.6&nbsp;µs<br>1.30<br>25.9&nbsp;KB<br>**6.61** | 513.4&nbsp;µs<br>**2.35**<br>35.6&nbsp;KB<br>**9.10** |
| GetAllAsync | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;979.9&nbsp;µs<br>1.00<br>241&nbsp;KB<br>1.00 | 💾&nbsp;983.1&nbsp;µs<br>$\color{#d97706}{1.00}$<br>226&nbsp;KB<br>$\color{#2563eb}{0.94}$ | 981.7&nbsp;µs<br>1.00<br>243&nbsp;KB<br>1.01 | 1.03&nbsp;ms<br>1.06<br>244&nbsp;KB<br>1.01 | 3.46&nbsp;ms<br>**3.53**<br>4503&nbsp;KB<br>**18.65** | 1.10&nbsp;ms<br>1.12<br>446&nbsp;KB<br>1.85 | 1.29&nbsp;ms<br>1.32<br>249&nbsp;KB<br>1.03 | 2.86&nbsp;ms<br>**2.92**<br>4058&nbsp;KB<br>**16.81** | 1.44&nbsp;ms<br>1.47<br>260&nbsp;KB<br>1.08 |
| GetAllAsync | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 8.21&nbsp;ms<br>1.00<br>2572&nbsp;KB<br>1.00 | 💾&nbsp;8.23&nbsp;ms<br>$\color{#d97706}{1.00}$<br>2217&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 🏆&nbsp;8.18&nbsp;ms<br>1.00<br>2574&nbsp;KB<br>1.00 | 8.36&nbsp;ms<br>1.02<br>2576&nbsp;KB<br>1.00 | 35.79&nbsp;ms<br>**4.36**<br>46.3&nbsp;MB<br>**18.42** | 10.21&nbsp;ms<br>1.24<br>4609&nbsp;KB<br>1.79 | 9.21&nbsp;ms<br>1.12<br>2582&nbsp;KB<br>1.00 | 33.52&nbsp;ms<br>**4.08**<br>43.2&nbsp;MB<br>**17.21** | 10.78&nbsp;ms<br>1.31<br>2596&nbsp;KB<br>1.01 |
| QueryAll | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 997.3&nbsp;µs<br>1.00<br>242&nbsp;KB<br>1.00 | 💾&nbsp;978.1&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>227&nbsp;KB<br>$\color{#2563eb}{0.94}$ | 🏆&nbsp;975.5&nbsp;µs<br>0.98<br>243&nbsp;KB<br>1.01 | 1.01&nbsp;ms<br>1.01<br>244&nbsp;KB<br>1.01 | 3.57&nbsp;ms<br>**3.58**<br>4503&nbsp;KB<br>**18.64** | 1.08&nbsp;ms<br>1.08<br>446&nbsp;KB<br>1.85 | 1.27&nbsp;ms<br>1.27<br>249&nbsp;KB<br>1.03 | 2.88&nbsp;ms<br>**2.89**<br>4058&nbsp;KB<br>**16.80** | 1.43&nbsp;ms<br>1.43<br>260&nbsp;KB<br>1.08 |
| QueryAll | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 8.31&nbsp;ms<br>1.00<br>2573&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;8.12&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>2219&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 8.17&nbsp;ms<br>0.98<br>2574&nbsp;KB<br>1.00 | 8.20&nbsp;ms<br>0.99<br>2576&nbsp;KB<br>1.00 | 34.70&nbsp;ms<br>**4.18**<br>46.3&nbsp;MB<br>**18.42** | 9.02&nbsp;ms<br>1.09<br>4607&nbsp;KB<br>1.79 | 9.06&nbsp;ms<br>1.09<br>2582&nbsp;KB<br>1.00 | 31.64&nbsp;ms<br>**3.81**<br>43.2&nbsp;MB<br>**17.20** | 10.77&nbsp;ms<br>1.30<br>2596&nbsp;KB<br>1.01 |
| Insert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;3.85&nbsp;ms<br>1.00<br>17.2&nbsp;KB<br>1.00 | 3.90&nbsp;ms<br>$\color{#d97706}{1.01}$<br>16.4&nbsp;KB<br>$\color{#2563eb}{0.95}$ | 💾&nbsp;4.08&nbsp;ms<br>1.06<br>16.1&nbsp;KB<br>0.94 | 3.93&nbsp;ms<br>1.02<br>16.9&nbsp;KB<br>0.98 | 5.90&nbsp;ms<br>1.54<br>121&nbsp;KB<br>**7.07** | 3.86&nbsp;ms<br>1.00<br>16.2&nbsp;KB<br>0.94 | 4.28&nbsp;ms<br>1.11<br>21.7&nbsp;KB<br>1.26 | 4.04&nbsp;ms<br>1.05<br>36.7&nbsp;KB<br>**2.14** | 4.27&nbsp;ms<br>1.11<br>28.3&nbsp;KB<br>1.65 |
| Insert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;3.88&nbsp;ms<br>1.00<br>16.2&nbsp;KB<br>1.00 | 3.88&nbsp;ms<br>$\color{#d97706}{1.00}$<br>16.0&nbsp;KB<br>$\color{#2563eb}{0.99}$ | 💾&nbsp;4.14&nbsp;ms<br>1.07<br>15.6&nbsp;KB<br>0.97 | 3.90&nbsp;ms<br>1.01<br>16.6&nbsp;KB<br>1.02 | 5.46&nbsp;ms<br>1.41<br>121&nbsp;KB<br>**7.46** | 3.89&nbsp;ms<br>1.00<br>16.2&nbsp;KB<br>1.00 | 4.49&nbsp;ms<br>1.16<br>22.4&nbsp;KB<br>1.38 | 3.99&nbsp;ms<br>1.03<br>36.7&nbsp;KB<br>**2.27** | 4.30&nbsp;ms<br>1.11<br>28.5&nbsp;KB<br>1.76 |
| Update | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 200.6&nbsp;µs<br>1.00<br>4.7&nbsp;KB<br>1.00 | 🏆&nbsp;199.8&nbsp;µs<br>$\color{#2563eb}{1.00}$<br>5.5&nbsp;KB<br>$\color{#ea580c}{1.17}$ | 💾&nbsp;213.8&nbsp;µs<br>1.07<br>4.5&nbsp;KB<br>0.94 | 220.4&nbsp;µs<br>1.10<br>7.3&nbsp;KB<br>1.55 | 729.9&nbsp;µs<br>**3.64**<br>93.2&nbsp;KB<br>**19.64** | 204.7&nbsp;µs<br>1.02<br>4.9&nbsp;KB<br>1.04 | 445.4&nbsp;µs<br>**2.22**<br>12.6&nbsp;KB<br>**2.65** | 274.6&nbsp;µs<br>1.37<br>31.0&nbsp;KB<br>**6.54** | 463.3&nbsp;µs<br>**2.31**<br>19.7&nbsp;KB<br>**4.15** |
| Update | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;206.6&nbsp;µs<br>1.00<br>4.7&nbsp;KB<br>1.00 | 211.8&nbsp;µs<br>$\color{#d97706}{1.03}$<br>5.6&nbsp;KB<br>$\color{#ea580c}{1.17}$ | 💾&nbsp;219.6&nbsp;µs<br>1.06<br>4.5&nbsp;KB<br>0.94 | 235.2&nbsp;µs<br>1.14<br>7.3&nbsp;KB<br>1.54 | 724.0&nbsp;µs<br>**3.51**<br>93.1&nbsp;KB<br>**19.62** | 217.6&nbsp;µs<br>1.05<br>5.0&nbsp;KB<br>1.04 | 444.1&nbsp;µs<br>**2.15**<br>12.6&nbsp;KB<br>**2.65** | 281.9&nbsp;µs<br>1.36<br>31.1&nbsp;KB<br>**6.54** | 466.9&nbsp;µs<br>**2.26**<br>19.7&nbsp;KB<br>**4.16** |
| BulkInsert | 2,000 | 🏆&nbsp;RepoDb<br>💾&nbsp;RepoDb | 11.83&nbsp;ms<br>1.00<br>485&nbsp;KB<br>1.00 | 11.82&nbsp;ms<br>$\color{#2563eb}{1.00}$<br>478&nbsp;KB<br>$\color{#2563eb}{0.99}$ | 35.38&nbsp;ms<br>**2.99**<br>6828&nbsp;KB<br>**14.09** | 🏆&nbsp;💾&nbsp;11.37&nbsp;ms<br>0.96<br>474&nbsp;KB<br>0.98 | 88.82&nbsp;ms<br>**7.51**<br>20.1&nbsp;MB<br>**42.56** | 34.08&nbsp;ms<br>**2.88**<br>6826&nbsp;KB<br>**14.08** | 17.98&nbsp;ms<br>1.52<br>3308&nbsp;KB<br>**6.82** | 22.22&nbsp;ms<br>1.88<br>3344&nbsp;KB<br>**6.90** | 16.07&nbsp;ms<br>1.36<br>2394&nbsp;KB<br>**4.94** |
| BulkInsert | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 61.80&nbsp;ms<br>1.00<br>4581&nbsp;KB<br>1.00 | 🏆&nbsp;58.81&nbsp;ms<br>$\color{#2563eb}{0.95}$<br>4506&nbsp;KB<br>$\color{#2563eb}{0.98}$ | 251.50&nbsp;ms<br>**4.07**<br>66.4&nbsp;MB<br>**14.84** | 💾&nbsp;61.85&nbsp;ms<br>1.00<br>4499&nbsp;KB<br>0.98 | 600.39&nbsp;ms<br>**9.72**<br>199.8&nbsp;MB<br>**44.67** | 259.43&nbsp;ms<br>**4.20**<br>66.4&nbsp;MB<br>**14.85** | 128.28&nbsp;ms<br>**2.08**<br>33.2&nbsp;MB<br>**7.42** | 234.57&nbsp;ms<br>**3.80**<br>31.3&nbsp;MB<br>**6.99** | 99.08&nbsp;ms<br>1.60<br>24.4&nbsp;MB<br>**5.44** |
| BulkUpdate | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 10.15&nbsp;ms<br>1.00<br>4690&nbsp;KB<br>1.00 | 🏆&nbsp;9.62&nbsp;ms<br>$\color{#2563eb}{0.95}$<br>3738&nbsp;KB<br>$\color{#65a30d}{0.80}$ | 11.38&nbsp;ms<br>1.12<br>6610&nbsp;KB<br>1.41 | 💾&nbsp;162.63&nbsp;ms<br>**16.02**<br>906&nbsp;KB<br>**0.19** | 161.36&nbsp;ms<br>**15.89**<br>36.1&nbsp;MB<br>**7.89** | 11.45&nbsp;ms<br>1.13<br>6609&nbsp;KB<br>1.41 | 10.67&nbsp;ms<br>1.05<br>2135&nbsp;KB<br>**0.46** | 32.84&nbsp;ms<br>**3.23**<br>7746&nbsp;KB<br>1.65 | 12.64&nbsp;ms<br>1.24<br>2353&nbsp;KB<br>0.50 |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;RepoDb | 🏆&nbsp;86.73&nbsp;ms<br>1.00<br>14.6&nbsp;MB<br>1.00 | 87.87&nbsp;ms<br>$\color{#d97706}{1.01}$<br>13.7&nbsp;MB<br>$\color{#2563eb}{0.94}$ | 145.35&nbsp;ms<br>1.68<br>64.5&nbsp;MB<br>**4.42** | 💾&nbsp;401.75&nbsp;ms<br>**4.63**<br>5562&nbsp;KB<br>**0.37** | 1.60&nbsp;s<br>**18.49**<br>359.1&nbsp;MB<br>**24.59** | 145.77&nbsp;ms<br>1.68<br>64.5&nbsp;MB<br>**4.42** | 99.81&nbsp;ms<br>1.15<br>21.8&nbsp;MB<br>1.49 | 322.73&nbsp;ms<br>**3.72**<br>75.9&nbsp;MB<br>**5.20** | 106.65&nbsp;ms<br>1.23<br>24.0&nbsp;MB<br>1.64 |
| BulkDelete | 2,000 | 🏆&nbsp;SqlSugar<br>💾&nbsp;SqlSugar | 12.74&nbsp;ms<br>1.00<br>882&nbsp;KB<br>1.00 | 12.49&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>806&nbsp;KB<br>$\color{#2563eb}{0.91}$ | 12.96&nbsp;ms<br>1.02<br>1377&nbsp;KB<br>1.56 | 27.95&nbsp;ms<br>**2.19**<br>19.3&nbsp;MB<br>**22.41** | 24.89&nbsp;ms<br>1.95<br>3784&nbsp;KB<br>**4.29** | 12.85&nbsp;ms<br>1.01<br>1377&nbsp;KB<br>1.56 | 17.93&nbsp;ms<br>1.41<br>613&nbsp;KB<br>0.70 | 13.48&nbsp;ms<br>1.06<br>715&nbsp;KB<br>0.81 | 🏆&nbsp;💾&nbsp;12.17&nbsp;ms<br>0.96<br>374&nbsp;KB<br>**0.42** |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 86.14&nbsp;ms<br>1.00<br>8701&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;72.71&nbsp;ms<br>$\color{#65a30d}{0.84}$<br>3622&nbsp;KB<br>$\color{#16a34a}{0.42}$ | 81.38&nbsp;ms<br>0.94<br>13.2&nbsp;MB<br>1.56 | 235.61&nbsp;ms<br>**2.74**<br>195.2&nbsp;MB<br>**22.97** | 270.78&nbsp;ms<br>**3.14**<br>37.0&nbsp;MB<br>**4.36** | 177.36&nbsp;ms<br>**2.06**<br>13.6&nbsp;MB<br>1.60 | 190.00&nbsp;ms<br>**2.21**<br>6146&nbsp;KB<br>0.71 | 79.10&nbsp;ms<br>0.92<br>7068&nbsp;KB<br>0.81 | 170.20&nbsp;ms<br>1.98<br>4398&nbsp;KB<br>0.51 |
| UpsertBatch | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 8.48&nbsp;ms<br>1.00<br>2965&nbsp;KB<br>1.00 | 🏆&nbsp;8.13&nbsp;ms<br>$\color{#2563eb}{0.96}$<br>2518&nbsp;KB<br>$\color{#65a30d}{0.85}$ | 11.36&nbsp;ms<br>1.34<br>6778&nbsp;KB<br>**2.29** | 💾&nbsp;162.47&nbsp;ms<br>**19.17**<br>917&nbsp;KB<br>**0.31** | 16.16&nbsp;ms<br>1.91<br>10.2&nbsp;MB<br>**3.52** | 11.61&nbsp;ms<br>1.37<br>6779&nbsp;KB<br>**2.29** | 9.85&nbsp;ms<br>1.16<br>2554&nbsp;KB<br>0.86 | 32.79&nbsp;ms<br>**3.87**<br>8095&nbsp;KB<br>**2.73** | 289.55&nbsp;ms<br>**34.16**<br>219.3&nbsp;MB<br>**75.74** |
| UpsertBatch | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 78.18&nbsp;ms<br>1.00<br>12.9&nbsp;MB<br>1.00 | 🏆&nbsp;77.17&nbsp;ms<br>$\color{#2563eb}{0.99}$<br>12.6&nbsp;MB<br>$\color{#2563eb}{0.98}$ | 135.03&nbsp;ms<br>1.73<br>66.1&nbsp;MB<br>**5.15** | 💾&nbsp;419.83&nbsp;ms<br>**5.37**<br>5597&nbsp;KB<br>**0.43** | 169.89&nbsp;ms<br>**2.17**<br>103.4&nbsp;MB<br>**8.04** | 139.64&nbsp;ms<br>1.79<br>66.1&nbsp;MB<br>**5.15** | 134.05&nbsp;ms<br>1.71<br>25.8&nbsp;MB<br>**2.01** | 320.35&nbsp;ms<br>**4.10**<br>79.2&nbsp;MB<br>**6.16** | 2.85&nbsp;s<br>**36.50**<br>2358.3&nbsp;MB<br>**183.51** |
| KeysetPage | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;278.2&nbsp;µs<br>1.00<br>16.4&nbsp;KB<br>1.00 | 291.4&nbsp;µs<br>$\color{#d97706}{1.05}$<br>18.3&nbsp;KB<br>$\color{#ea580c}{1.11}$ | 279.4&nbsp;µs<br>1.00<br>17.5&nbsp;KB<br>1.07 | 299.6&nbsp;µs<br>1.08<br>22.1&nbsp;KB<br>1.35 | 948.6&nbsp;µs<br>**3.41**<br>318&nbsp;KB<br>**19.41** | 293.4&nbsp;µs<br>1.05<br>28.3&nbsp;KB<br>1.73 | 532.8&nbsp;µs<br>1.92<br>29.9&nbsp;KB<br>1.82 | 439.7&nbsp;µs<br>1.58<br>228&nbsp;KB<br>**13.91** | 572.3&nbsp;µs<br>**2.06**<br>52.8&nbsp;KB<br>**3.22** |
| KeysetPage | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;277.9&nbsp;µs<br>1.00<br>16.4&nbsp;KB<br>1.00 | 🏆&nbsp;269.3&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>18.2&nbsp;KB<br>$\color{#ea580c}{1.11}$ | 281.5&nbsp;µs<br>1.01<br>17.5&nbsp;KB<br>1.07 | 290.4&nbsp;µs<br>1.05<br>22.1&nbsp;KB<br>1.35 | 916.5&nbsp;µs<br>**3.30**<br>318&nbsp;KB<br>**19.40** | 286.8&nbsp;µs<br>1.03<br>28.3&nbsp;KB<br>1.72 | 543.1&nbsp;µs<br>1.95<br>29.9&nbsp;KB<br>1.82 | 414.4&nbsp;µs<br>1.49<br>228&nbsp;KB<br>**13.91** | 563.6&nbsp;µs<br>**2.03**<br>52.8&nbsp;KB<br>**3.22** |
| WhereIn | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;FreeSql | 🏆&nbsp;372.0&nbsp;µs<br>1.00<br>57.7&nbsp;KB<br>1.00 | 380.3&nbsp;µs<br>$\color{#d97706}{1.02}$<br>53.5&nbsp;KB<br>$\color{#2563eb}{0.93}$ | 395.1&nbsp;µs<br>1.06<br>90.2&nbsp;KB<br>1.56 | 488.9&nbsp;µs<br>1.31<br>101&nbsp;KB<br>1.75 | 1.28&nbsp;ms<br>**3.45**<br>491&nbsp;KB<br>**8.51** | 395.9&nbsp;µs<br>1.06<br>90.7&nbsp;KB<br>1.57 | 💾&nbsp;613.9&nbsp;µs<br>1.65<br>47.1&nbsp;KB<br>0.82 | 551.7&nbsp;µs<br>1.48<br>266&nbsp;KB<br>**4.61** | 728.3&nbsp;µs<br>1.96<br>97.8&nbsp;KB<br>1.70 |
| WhereIn | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;FreeSql | 387.8&nbsp;µs<br>1.00<br>57.7&nbsp;KB<br>1.00 | 🏆&nbsp;370.3&nbsp;µs<br>$\color{#2563eb}{0.95}$<br>53.5&nbsp;KB<br>$\color{#2563eb}{0.93}$ | 388.5&nbsp;µs<br>1.00<br>90.2&nbsp;KB<br>1.56 | 484.6&nbsp;µs<br>1.25<br>101&nbsp;KB<br>1.75 | 1.29&nbsp;ms<br>**3.33**<br>506&nbsp;KB<br>**8.77** | 398.6&nbsp;µs<br>1.03<br>90.7&nbsp;KB<br>1.57 | 💾&nbsp;623.0&nbsp;µs<br>1.61<br>50.5&nbsp;KB<br>0.88 | 567.9&nbsp;µs<br>1.46<br>285&nbsp;KB<br>**4.94** | 705.9&nbsp;µs<br>1.82<br>99.7&nbsp;KB<br>1.73 |
| Count | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;590.5&nbsp;µs<br>1.00<br>3.7&nbsp;KB<br>1.00 | 605.7&nbsp;µs<br>$\color{#d97706}{1.03}$<br>5.0&nbsp;KB<br>$\color{#dc2626}{1.33}$ | 596.4&nbsp;µs<br>1.01<br>4.1&nbsp;KB<br>1.10 | 601.4&nbsp;µs<br>1.02<br>4.6&nbsp;KB<br>1.24 | 1.15&nbsp;ms<br>1.95<br>82.0&nbsp;KB<br>**21.89** | 610.7&nbsp;µs<br>1.03<br>4.1&nbsp;KB<br>1.10 | 816.0&nbsp;µs<br>1.38<br>11.7&nbsp;KB<br>**3.11** | 615.0&nbsp;µs<br>1.04<br>6.9&nbsp;KB<br>1.84 | 883.9&nbsp;µs<br>1.50<br>30.4&nbsp;KB<br>**8.12** |
| Count | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;778.2&nbsp;µs<br>1.00<br>4.3&nbsp;KB<br>1.00 | 789.8&nbsp;µs<br>$\color{#d97706}{1.01}$<br>5.5&nbsp;KB<br>$\color{#ea580c}{1.28}$ | 790.5&nbsp;µs<br>1.02<br>4.6&nbsp;KB<br>1.08 | 799.8&nbsp;µs<br>1.03<br>5.2&nbsp;KB<br>1.22 | 1.32&nbsp;ms<br>1.69<br>82.5&nbsp;KB<br>**19.21** | 798.7&nbsp;µs<br>1.03<br>4.6&nbsp;KB<br>1.07 | 1.03&nbsp;ms<br>1.32<br>12.3&nbsp;KB<br>**2.85** | 794.3&nbsp;µs<br>1.02<br>7.4&nbsp;KB<br>1.73 | 1.08&nbsp;ms<br>1.39<br>31.0&nbsp;KB<br>**7.21** |

#### PostgreSQL（2000 与 20000 档）

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;Dapper.AOT | 233.6&nbsp;µs<br>1.00<br>3.0&nbsp;KB<br>1.00 | 233.2&nbsp;µs<br>$\color{#2563eb}{1.00}$<br>3.4&nbsp;KB<br>$\color{#ea580c}{1.15}$ | 🏆&nbsp;💾&nbsp;220.8&nbsp;µs<br>0.95<br>2.6&nbsp;KB<br>0.88 | 234.3&nbsp;µs<br>1.00<br>5.7&nbsp;KB<br>1.91 | 335.9&nbsp;µs<br>1.44<br>43.9&nbsp;KB<br>**14.68** | 225.1&nbsp;µs<br>0.96<br>3.2&nbsp;KB<br>1.08 | 301.9&nbsp;µs<br>1.29<br>12.0&nbsp;KB<br>**4.00** | 250.6&nbsp;µs<br>1.07<br>7.7&nbsp;KB<br>**2.56** | 359.6&nbsp;µs<br>1.54<br>31.4&nbsp;KB<br>**10.48** |
| GetByKey | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;219.8&nbsp;µs<br>1.00<br>2.9&nbsp;KB<br>1.00 | 223.8&nbsp;µs<br>$\color{#d97706}{1.02}$<br>3.4&nbsp;KB<br>$\color{#ea580c}{1.15}$ | 💾&nbsp;224.3&nbsp;µs<br>1.02<br>2.6&nbsp;KB<br>0.90 | 237.5&nbsp;µs<br>1.08<br>5.7&nbsp;KB<br>1.93 | 352.3&nbsp;µs<br>1.60<br>43.9&nbsp;KB<br>**14.90** | 230.2&nbsp;µs<br>1.05<br>3.3&nbsp;KB<br>1.11 | 311.5&nbsp;µs<br>1.42<br>12.0&nbsp;KB<br>**4.08** | 254.7&nbsp;µs<br>1.16<br>7.7&nbsp;KB<br>**2.61** | 367.9&nbsp;µs<br>1.67<br>31.4&nbsp;KB<br>**10.64** |
| GetAllAsync | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 713.9&nbsp;µs<br>1.00<br>240&nbsp;KB<br>1.00 | 💾&nbsp;752.5&nbsp;µs<br>$\color{#d97706}{1.05}$<br>224&nbsp;KB<br>$\color{#2563eb}{0.93}$ | 🏆&nbsp;708.3&nbsp;µs<br>0.99<br>239&nbsp;KB<br>1.00 | 793.1&nbsp;µs<br>1.11<br>241&nbsp;KB<br>1.00 | 893.6&nbsp;µs<br>1.25<br>652&nbsp;KB<br>**2.72** | 824.9&nbsp;µs<br>1.16<br>443&nbsp;KB<br>1.84 | 777.5&nbsp;µs<br>1.09<br>244&nbsp;KB<br>1.02 | 729.3&nbsp;µs<br>1.02<br>241&nbsp;KB<br>1.01 | 934.6&nbsp;µs<br>1.31<br>254&nbsp;KB<br>1.06 |
| GetAllAsync | 20,000 | 🏆&nbsp;FreeSql<br>💾&nbsp;PalORM | 6.04&nbsp;ms<br>1.00<br>2575&nbsp;KB<br>1.00 | 💾&nbsp;5.91&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>2220&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 6.29&nbsp;ms<br>1.04<br>2565&nbsp;KB<br>1.00 | 5.88&nbsp;ms<br>0.97<br>2574&nbsp;KB<br>1.00 | 6.92&nbsp;ms<br>1.15<br>6356&nbsp;KB<br>**2.47** | 6.84&nbsp;ms<br>1.13<br>4599&nbsp;KB<br>1.79 | 🏆&nbsp;5.88&nbsp;ms<br>0.97<br>2575&nbsp;KB<br>1.00 | 6.40&nbsp;ms<br>1.06<br>2574&nbsp;KB<br>1.00 | 7.72&nbsp;ms<br>1.28<br>2585&nbsp;KB<br>1.00 |
| QueryAll | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 🏆&nbsp;702.9&nbsp;µs<br>1.00<br>240&nbsp;KB<br>1.00 | 💾&nbsp;717.1&nbsp;µs<br>$\color{#d97706}{1.02}$<br>225&nbsp;KB<br>$\color{#2563eb}{0.94}$ | 725.0&nbsp;µs<br>1.03<br>239&nbsp;KB<br>1.00 | 795.5&nbsp;µs<br>1.13<br>241&nbsp;KB<br>1.01 | 904.2&nbsp;µs<br>1.29<br>652&nbsp;KB<br>**2.72** | 787.0&nbsp;µs<br>1.12<br>443&nbsp;KB<br>1.85 | 797.0&nbsp;µs<br>1.13<br>245&nbsp;KB<br>1.02 | 750.4&nbsp;µs<br>1.07<br>241&nbsp;KB<br>1.01 | 937.1&nbsp;µs<br>1.33<br>254&nbsp;KB<br>1.06 |
| QueryAll | 20,000 | 🏆&nbsp;linq2db<br>💾&nbsp;PalORM | 6.31&nbsp;ms<br>1.00<br>2576&nbsp;KB<br>1.00 | 💾&nbsp;6.34&nbsp;ms<br>$\color{#d97706}{1.01}$<br>2221&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 6.67&nbsp;ms<br>1.06<br>2567&nbsp;KB<br>1.00 | 6.02&nbsp;ms<br>0.96<br>2575&nbsp;KB<br>1.00 | 7.00&nbsp;ms<br>1.11<br>6356&nbsp;KB<br>**2.47** | 6.63&nbsp;ms<br>1.05<br>4599&nbsp;KB<br>1.79 | 6.00&nbsp;ms<br>0.95<br>2575&nbsp;KB<br>1.00 | 🏆&nbsp;5.95&nbsp;ms<br>0.94<br>2570&nbsp;KB<br>1.00 | 9.52&nbsp;ms<br>1.51<br>2586&nbsp;KB<br>1.00 |
| Insert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;1.38&nbsp;ms<br>1.00<br>7.6&nbsp;KB<br>1.00 | 1.44&nbsp;ms<br>$\color{#d97706}{1.05}$<br>7.8&nbsp;KB<br>$\color{#d97706}{1.03}$ | 💾&nbsp;1.43&nbsp;ms<br>1.04<br>6.7&nbsp;KB<br>0.88 | 1.44&nbsp;ms<br>1.04<br>7.9&nbsp;KB<br>1.05 | 1.52&nbsp;ms<br>1.10<br>52.0&nbsp;KB<br>**6.88** | 1.41&nbsp;ms<br>1.02<br>7.3&nbsp;KB<br>0.97 | 1.46&nbsp;ms<br>1.06<br>11.0&nbsp;KB<br>1.45 | 1.48&nbsp;ms<br>1.08<br>21.8&nbsp;KB<br>**2.89** | 1.50&nbsp;ms<br>1.09<br>18.8&nbsp;KB<br>**2.48** |
| Insert | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;Dapper.AOT | 1.41&nbsp;ms<br>1.00<br>7.6&nbsp;KB<br>1.00 | 1.39&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>7.6&nbsp;KB<br>$\color{#d97706}{1.01}$ | 🏆&nbsp;💾&nbsp;1.38&nbsp;ms<br>0.98<br>6.7&nbsp;KB<br>0.88 | 1.45&nbsp;ms<br>1.03<br>7.9&nbsp;KB<br>1.05 | 1.56&nbsp;ms<br>1.11<br>52.0&nbsp;KB<br>**6.89** | 1.41&nbsp;ms<br>1.00<br>7.3&nbsp;KB<br>0.97 | 1.48&nbsp;ms<br>1.05<br>11.0&nbsp;KB<br>1.45 | 1.46&nbsp;ms<br>1.03<br>21.8&nbsp;KB<br>**2.88** | 1.48&nbsp;ms<br>1.05<br>18.8&nbsp;KB<br>**2.48** |
| Update | 2,000 | 🏆&nbsp;Dapper<br>💾&nbsp;Dapper.AOT | 1.48&nbsp;ms<br>1.00<br>9.1&nbsp;KB<br>1.00 | 1.45&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>8.0&nbsp;KB<br>$\color{#65a30d}{0.89}$ | 💾&nbsp;1.42&nbsp;ms<br>0.96<br>6.6&nbsp;KB<br>0.73 | 1.47&nbsp;ms<br>0.99<br>9.9&nbsp;KB<br>1.09 | 1.56&nbsp;ms<br>1.05<br>52.1&nbsp;KB<br>**5.75** | 🏆&nbsp;1.42&nbsp;ms<br>0.96<br>7.6&nbsp;KB<br>0.84 | 1.50&nbsp;ms<br>1.01<br>13.7&nbsp;KB<br>1.51 | 1.46&nbsp;ms<br>0.99<br>25.1&nbsp;KB<br>**2.77** | 1.58&nbsp;ms<br>1.07<br>21.3&nbsp;KB<br>**2.35** |
| Update | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;1.40&nbsp;ms<br>1.00<br>7.4&nbsp;KB<br>1.00 | 1.45&nbsp;ms<br>$\color{#d97706}{1.03}$<br>8.1&nbsp;KB<br>$\color{#ea580c}{1.10}$ | 💾&nbsp;1.41&nbsp;ms<br>1.01<br>6.6&nbsp;KB<br>0.90 | 1.51&nbsp;ms<br>1.08<br>9.6&nbsp;KB<br>1.31 | 1.59&nbsp;ms<br>1.13<br>52.2&nbsp;KB<br>**7.09** | 1.43&nbsp;ms<br>1.02<br>7.2&nbsp;KB<br>0.98 | 1.44&nbsp;ms<br>1.03<br>13.2&nbsp;KB<br>1.79 | 1.51&nbsp;ms<br>1.08<br>25.2&nbsp;KB<br>**3.43** | 1.53&nbsp;ms<br>1.09<br>21.2&nbsp;KB<br>**2.89** |
| BulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;5.00&nbsp;ms<br>1.00<br>339&nbsp;KB<br>1.00 | 5.26&nbsp;ms<br>$\color{#d97706}{1.05}$<br>341&nbsp;KB<br>$\color{#d97706}{1.00}$ | 18.09&nbsp;ms<br>**3.62**<br>6849&nbsp;KB<br>**20.19** | 6.12&nbsp;ms<br>1.22<br>845&nbsp;KB<br>**2.49** | 47.00&nbsp;ms<br>**9.41**<br>15.9&nbsp;MB<br>**47.98** | 17.46&nbsp;ms<br>**3.49**<br>6851&nbsp;KB<br>**20.19** | 14.87&nbsp;ms<br>**2.98**<br>3937&nbsp;KB<br>**11.60** | 12.81&nbsp;ms<br>**2.56**<br>1172&nbsp;KB<br>**3.45** | 28.65&nbsp;ms<br>**5.74**<br>7576&nbsp;KB<br>**22.33** |
| BulkInsert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;22.34&nbsp;ms<br>1.00<br>3329&nbsp;KB<br>1.00 | 23.15&nbsp;ms<br>$\color{#d97706}{1.04}$<br>3333&nbsp;KB<br>$\color{#d97706}{1.00}$ | 204.54&nbsp;ms<br>**9.16**<br>66.9&nbsp;MB<br>**20.56** | 29.54&nbsp;ms<br>1.32<br>8205&nbsp;KB<br>**2.46** | 477.21&nbsp;ms<br>**21.36**<br>153.7&nbsp;MB<br>**47.26** | 211.77&nbsp;ms<br>**9.48**<br>66.9&nbsp;MB<br>**20.57** | 126.99&nbsp;ms<br>**5.69**<br>39.5&nbsp;MB<br>**12.16** | 132.78&nbsp;ms<br>**5.94**<br>10.0&nbsp;MB<br>**3.09** | 306.42&nbsp;ms<br>**13.72**<br>75.0&nbsp;MB<br>**23.06** |
| BulkUpdate | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 💾&nbsp;5.71&nbsp;ms<br>1.00<br>419&nbsp;KB<br>1.00 | 🏆&nbsp;5.40&nbsp;ms<br>$\color{#2563eb}{0.95}$<br>422&nbsp;KB<br>$\color{#d97706}{1.01}$ | 16.62&nbsp;ms<br>**2.91**<br>6688&nbsp;KB<br>**15.95** | 58.38&nbsp;ms<br>**10.22**<br>3883&nbsp;KB<br>**9.26** | 65.24&nbsp;ms<br>**11.42**<br>16.0&nbsp;MB<br>**39.13** | 16.61&nbsp;ms<br>**2.91**<br>6689&nbsp;KB<br>**15.95** | 14.66&nbsp;ms<br>**2.57**<br>2779&nbsp;KB<br>**6.63** | 15.22&nbsp;ms<br>**2.66**<br>2138&nbsp;KB<br>**5.10** | 40.76&nbsp;ms<br>**7.13**<br>11.8&nbsp;MB<br>**28.88** |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;72.18&nbsp;ms<br>1.00<br>4193&nbsp;KB<br>1.00 | 73.61&nbsp;ms<br>$\color{#d97706}{1.02}$<br>4200&nbsp;KB<br>$\color{#d97706}{1.00}$ | 227.57&nbsp;ms<br>**3.15**<br>65.4&nbsp;MB<br>**15.97** | 576.54&nbsp;ms<br>**7.99**<br>37.9&nbsp;MB<br>**9.25** | 686.41&nbsp;ms<br>**9.51**<br>155.0&nbsp;MB<br>**37.85** | 228.99&nbsp;ms<br>**3.17**<br>65.4&nbsp;MB<br>**15.97** | 142.37&nbsp;ms<br>1.97<br>28.3&nbsp;MB<br>**6.91** | 168.21&nbsp;ms<br>**2.33**<br>21.2&nbsp;MB<br>**5.17** | 475.63&nbsp;ms<br>**6.59**<br>120.7&nbsp;MB<br>**29.47** |
| BulkDelete | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 3.71&nbsp;ms<br>1.00<br>784&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;2.42&nbsp;ms<br>$\color{#16a34a}{0.65}$<br>89.5&nbsp;KB<br>$\color{#16a34a}{0.11}$ | 3.92&nbsp;ms<br>1.06<br>1338&nbsp;KB<br>1.71 | 20.17&nbsp;ms<br>**5.44**<br>19.2&nbsp;MB<br>**25.02** | 4.43&nbsp;ms<br>1.20<br>177&nbsp;KB<br>**0.23** | 3.87&nbsp;ms<br>1.04<br>1339&nbsp;KB<br>1.71 | 5.81&nbsp;ms<br>1.57<br>570&nbsp;KB<br>0.73 | 4.65&nbsp;ms<br>1.25<br>264&nbsp;KB<br>**0.34** | 4.09&nbsp;ms<br>1.10<br>346&nbsp;KB<br>**0.44** |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 27.73&nbsp;ms<br>1.00<br>7881&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;13.28&nbsp;ms<br>$\color{#16a34a}{0.48}$<br>865&nbsp;KB<br>$\color{#16a34a}{0.11}$ | 32.55&nbsp;ms<br>1.17<br>13.0&nbsp;MB<br>1.69 | 189.75&nbsp;ms<br>**6.84**<br>193.8&nbsp;MB<br>**25.19** | 43.01&nbsp;ms<br>1.55<br>1753&nbsp;KB<br>**0.22** | 30.61&nbsp;ms<br>1.10<br>13.1&nbsp;MB<br>1.70 | 60.72&nbsp;ms<br>**2.19**<br>6092&nbsp;KB<br>0.77 | 35.04&nbsp;ms<br>1.26<br>2705&nbsp;KB<br>**0.34** | 31.05&nbsp;ms<br>1.12<br>4454&nbsp;KB<br>0.57 |
| UpsertBatch | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 17.81&nbsp;ms<br>1.00<br>2785&nbsp;KB<br>1.00 | 💾&nbsp;8.94&nbsp;ms<br>$\color{#16a34a}{0.50}$<br>449&nbsp;KB<br>$\color{#16a34a}{0.16}$ | 21.52&nbsp;ms<br>1.21<br>6865&nbsp;KB<br>**2.47** | 27.93&nbsp;ms<br>1.57<br>5918&nbsp;KB<br>**2.13** | 🏆&nbsp;3.07&nbsp;ms<br>**0.17**<br>3040&nbsp;KB<br>1.09 | 21.55&nbsp;ms<br>1.21<br>6866&nbsp;KB<br>**2.47** | 18.72&nbsp;ms<br>1.05<br>2803&nbsp;KB<br>1.01 | 17.69&nbsp;ms<br>0.99<br>2151&nbsp;KB<br>0.77 | 291.86&nbsp;ms<br>**16.39**<br>219.2&nbsp;MB<br>**80.61** |
| UpsertBatch | 20,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 153.15&nbsp;ms<br>1.00<br>11.6&nbsp;MB<br>1.00 | 💾&nbsp;106.38&nbsp;ms<br>$\color{#16a34a}{0.69}$<br>4439&nbsp;KB<br>$\color{#16a34a}{0.37}$ | 246.42&nbsp;ms<br>1.61<br>67.0&nbsp;MB<br>**5.78** | 270.12&nbsp;ms<br>1.76<br>57.4&nbsp;MB<br>**4.95** | 🏆&nbsp;50.10&nbsp;ms<br>**0.33**<br>29.1&nbsp;MB<br>**2.51** | 242.61&nbsp;ms<br>1.58<br>67.0&nbsp;MB<br>**5.78** | 170.61&nbsp;ms<br>1.11<br>28.4&nbsp;MB<br>**2.45** | 188.92&nbsp;ms<br>1.23<br>21.2&nbsp;MB<br>1.83 | 3.12&nbsp;s<br>**20.38**<br>2358.6&nbsp;MB<br>**203.38** |
| KeysetPage | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;267.6&nbsp;µs<br>1.00<br>15.4&nbsp;KB<br>1.00 | 293.9&nbsp;µs<br>$\color{#ea580c}{1.10}$<br>17.3&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 💾&nbsp;274.7&nbsp;µs<br>1.03<br>15.4&nbsp;KB<br>1.00 | 267.8&nbsp;µs<br>1.00<br>19.7&nbsp;KB<br>1.28 | 407.6&nbsp;µs<br>1.52<br>78.3&nbsp;KB<br>**5.08** | 284.1&nbsp;µs<br>1.06<br>26.1&nbsp;KB<br>1.69 | 358.9&nbsp;µs<br>1.34<br>25.9&nbsp;KB<br>1.68 | 301.9&nbsp;µs<br>1.13<br>21.2&nbsp;KB<br>1.37 | 422.1&nbsp;µs<br>1.58<br>48.8&nbsp;KB<br>**3.16** |
| KeysetPage | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;268.2&nbsp;µs<br>1.00<br>15.4&nbsp;KB<br>1.00 | 274.6&nbsp;µs<br>$\color{#d97706}{1.02}$<br>17.3&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 💾&nbsp;269.6&nbsp;µs<br>1.01<br>15.4&nbsp;KB<br>1.00 | 286.3&nbsp;µs<br>1.07<br>19.8&nbsp;KB<br>1.28 | 440.6&nbsp;µs<br>1.64<br>78.4&nbsp;KB<br>**5.08** | 282.4&nbsp;µs<br>1.05<br>26.1&nbsp;KB<br>1.69 | 380.9&nbsp;µs<br>1.42<br>26.0&nbsp;KB<br>1.68 | 313.6&nbsp;µs<br>1.17<br>21.2&nbsp;KB<br>1.37 | 456.6&nbsp;µs<br>1.70<br>48.9&nbsp;KB<br>**3.17** |
| WhereIn | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 383.9&nbsp;µs<br>1.00<br>52.8&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;322.9&nbsp;µs<br>$\color{#65a30d}{0.84}$<br>17.8&nbsp;KB<br>$\color{#16a34a}{0.34}$ | 413.9&nbsp;µs<br>1.08<br>87.2&nbsp;KB<br>1.65 | 503.6&nbsp;µs<br>1.31<br>93.9&nbsp;KB<br>1.78 | 452.9&nbsp;µs<br>1.18<br>76.8&nbsp;KB<br>1.45 | 412.4&nbsp;µs<br>1.07<br>87.7&nbsp;KB<br>1.66 | 513.3&nbsp;µs<br>1.34<br>43.4&nbsp;KB<br>0.82 | 463.1&nbsp;µs<br>1.21<br>40.4&nbsp;KB<br>0.76 | 628.3&nbsp;µs<br>1.64<br>93.2&nbsp;KB<br>1.76 |
| WhereIn | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 427.6&nbsp;µs<br>1.00<br>53.0&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;365.4&nbsp;µs<br>$\color{#65a30d}{0.85}$<br>18.0&nbsp;KB<br>$\color{#16a34a}{0.34}$ | 443.0&nbsp;µs<br>1.04<br>87.3&nbsp;KB<br>1.65 | 545.5&nbsp;µs<br>1.28<br>94.4&nbsp;KB<br>1.78 | 500.1&nbsp;µs<br>1.17<br>76.9&nbsp;KB<br>1.45 | 444.6&nbsp;µs<br>1.04<br>87.8&nbsp;KB<br>1.66 | 569.1&nbsp;µs<br>1.33<br>47.0&nbsp;KB<br>0.89 | 493.6&nbsp;µs<br>1.15<br>41.1&nbsp;KB<br>0.78 | 664.4&nbsp;µs<br>1.55<br>95.3&nbsp;KB<br>1.80 |
| Count | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;255.2&nbsp;µs<br>1.00<br>1.9&nbsp;KB<br>1.00 | 258.8&nbsp;µs<br>$\color{#d97706}{1.01}$<br>3.1&nbsp;KB<br>$\color{#dc2626}{1.63}$ | 258.0&nbsp;µs<br>1.01<br>2.0&nbsp;KB<br>1.03 | 260.7&nbsp;µs<br>1.02<br>2.7&nbsp;KB<br>1.41 | 371.6&nbsp;µs<br>1.46<br>41.0&nbsp;KB<br>**21.54** | 258.3&nbsp;µs<br>1.01<br>2.0&nbsp;KB<br>1.05 | 333.7&nbsp;µs<br>1.31<br>7.6&nbsp;KB<br>**4.00** | 260.4&nbsp;µs<br>1.02<br>2.3&nbsp;KB<br>1.21 | 365.5&nbsp;µs<br>1.43<br>26.4&nbsp;KB<br>**13.90** |
| Count | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;711.6&nbsp;µs<br>1.00<br>3.2&nbsp;KB<br>1.00 | 749.2&nbsp;µs<br>$\color{#d97706}{1.05}$<br>4.5&nbsp;KB<br>$\color{#dc2626}{1.40}$ | 🏆&nbsp;705.7&nbsp;µs<br>0.99<br>3.2&nbsp;KB<br>1.01 | 711.1&nbsp;µs<br>1.00<br>4.0&nbsp;KB<br>1.24 | 907.6&nbsp;µs<br>1.28<br>42.5&nbsp;KB<br>**13.23** | 713.7&nbsp;µs<br>1.00<br>3.3&nbsp;KB<br>1.02 | 868.6&nbsp;µs<br>1.22<br>9.1&nbsp;KB<br>**2.83** | 712.4&nbsp;µs<br>1.00<br>3.6&nbsp;KB<br>1.12 | 931.5&nbsp;µs<br>1.31<br>28.1&nbsp;KB<br>**8.73** |

### 事务对照（仅 2000 档）

#### SQLite

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 💾&nbsp;20.8&nbsp;µs<br>1.00<br>3.9&nbsp;KB<br>1.00 | 21.0&nbsp;µs<br>$\color{#d97706}{1.01}$<br>4.4&nbsp;KB<br>$\color{#ea580c}{1.11}$ | 🏆&nbsp;20.6&nbsp;µs<br>0.99<br>4.0&nbsp;KB<br>1.03 | 22.4&nbsp;µs<br>1.08<br>4.2&nbsp;KB<br>1.07 | 1.19&nbsp;ms<br>**57.20**<br>58.6&nbsp;KB<br>**14.84** | 21.1&nbsp;µs<br>1.02<br>4.7&nbsp;KB<br>1.20 | 34.9&nbsp;µs<br>1.68<br>7.0&nbsp;KB<br>1.77 | 1.07&nbsp;ms<br>**51.75**<br>20.9&nbsp;KB<br>**5.28** | 1.14&nbsp;ms<br>**54.95**<br>16.3&nbsp;KB<br>**4.13** |
| TxHundredInserts | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 197.3&nbsp;µs<br>1.00<br>137&nbsp;KB<br>1.00 | 🏆&nbsp;💾&nbsp;192.3&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>92.9&nbsp;KB<br>$\color{#16a34a}{0.68}$ | 467.8&nbsp;µs<br>**2.37**<br>280&nbsp;KB<br>**2.05** | 607.8&nbsp;µs<br>**3.08**<br>295&nbsp;KB<br>**2.16** | 2.04&nbsp;ms<br>**10.32**<br>934&nbsp;KB<br>**6.83** | 263.4&nbsp;µs<br>1.33<br>220&nbsp;KB<br>1.61 | 1.63&nbsp;ms<br>**8.26**<br>541&nbsp;KB<br>**3.96** | 2.00&nbsp;ms<br>**10.15**<br>1646&nbsp;KB<br>**12.03** | 3.48&nbsp;ms<br>**17.61**<br>1198&nbsp;KB<br>**8.76** |
| TxBulkInsert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;linq2db | 2.71&nbsp;ms<br>1.00<br>1758&nbsp;KB<br>1.00 | 🏆&nbsp;2.66&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>1758&nbsp;KB<br>$\color{#d97706}{1.00}$ | 4.45&nbsp;ms<br>1.64<br>4353&nbsp;KB<br>**2.48** | 17.99&nbsp;ms<br>**6.63**<br>2709&nbsp;KB<br>1.54 | 23.80&nbsp;ms<br>**8.77**<br>17.3&nbsp;MB<br>**10.10** | 4.56&nbsp;ms<br>1.68<br>4354&nbsp;KB<br>**2.48** | 8.14&nbsp;ms<br>**3.00**<br>3563&nbsp;KB<br>**2.03** | 💾&nbsp;4.13&nbsp;ms<br>1.52<br>1280&nbsp;KB<br>0.73 | 9.31&nbsp;ms<br>**3.43**<br>5569&nbsp;KB<br>**3.17** |
| TxRollback | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;606.0&nbsp;µs<br>1.00<br>435&nbsp;KB<br>1.00 | 674.4&nbsp;µs<br>$\color{#ea580c}{1.11}$<br>437&nbsp;KB<br>$\color{#d97706}{1.01}$ | 2.20&nbsp;ms<br>**3.63**<br>1410&nbsp;KB<br>**3.24** | 2.90&nbsp;ms<br>**4.78**<br>2025&nbsp;KB<br>**4.66** | 5.69&nbsp;ms<br>**9.39**<br>4617&nbsp;KB<br>**10.62** | 2.26&nbsp;ms<br>**3.73**<br>1625&nbsp;KB<br>**3.74** | 8.20&nbsp;ms<br>**13.53**<br>3549&nbsp;KB<br>**8.16** | 5.80&nbsp;ms<br>**9.57**<br>9862&nbsp;KB<br>**22.68** | 14.45&nbsp;ms<br>**23.85**<br>7231&nbsp;KB<br>**16.63** |

#### MySQL

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;4.24&nbsp;ms<br>1.00<br>19.4&nbsp;KB<br>1.00 | 4.85&nbsp;ms<br>$\color{#ea580c}{1.14}$<br>21.7&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 4.64&nbsp;ms<br>1.09<br>19.6&nbsp;KB<br>1.01 | 4.61&nbsp;ms<br>1.09<br>21.5&nbsp;KB<br>1.10 | 6.05&nbsp;ms<br>1.43<br>134&nbsp;KB<br>**6.90** | 4.51&nbsp;ms<br>1.06<br>20.2&nbsp;KB<br>1.04 | 4.97&nbsp;ms<br>1.17<br>26.6&nbsp;KB<br>1.37 | 4.85&nbsp;ms<br>1.15<br>53.5&nbsp;KB<br>**2.75** | 4.85&nbsp;ms<br>1.14<br>32.8&nbsp;KB<br>1.69 |
| TxHundredInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 28.08&nbsp;ms<br>1.00<br>317&nbsp;KB<br>1.00 | 💾&nbsp;29.18&nbsp;ms<br>$\color{#d97706}{1.04}$<br>274&nbsp;KB<br>$\color{#65a30d}{0.86}$ | 28.82&nbsp;ms<br>1.03<br>437&nbsp;KB<br>1.38 | 30.72&nbsp;ms<br>1.09<br>586&nbsp;KB<br>1.85 | 🏆&nbsp;10.62&nbsp;ms<br>**0.38**<br>1134&nbsp;KB<br>**3.58** | 30.45&nbsp;ms<br>1.08<br>339&nbsp;KB<br>1.07 | 33.10&nbsp;ms<br>1.18<br>794&nbsp;KB<br>**2.51** | 34.90&nbsp;ms<br>1.24<br>2539&nbsp;KB<br>**8.01** | 35.02&nbsp;ms<br>1.25<br>1485&nbsp;KB<br>**4.69** |
| TxBulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;11.75&nbsp;ms<br>1.00<br>475&nbsp;KB<br>1.00 | 12.10&nbsp;ms<br>$\color{#d97706}{1.03}$<br>481&nbsp;KB<br>$\color{#d97706}{1.01}$ | 26.05&nbsp;ms<br>**2.22**<br>6813&nbsp;KB<br>**14.33** | 12.42&nbsp;ms<br>1.06<br>479&nbsp;KB<br>1.01 | 95.31&nbsp;ms<br>**8.11**<br>20.1&nbsp;MB<br>**43.33** | 29.27&nbsp;ms<br>**2.49**<br>6813&nbsp;KB<br>**14.33** | 20.97&nbsp;ms<br>1.79<br>3498&nbsp;KB<br>**7.36** | 17.71&nbsp;ms<br>1.51<br>3203&nbsp;KB<br>**6.74** | 27.81&nbsp;ms<br>**2.37**<br>7011&nbsp;KB<br>**14.75** |
| TxRollback | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 💾&nbsp;103.81&nbsp;ms<br>1.00<br>1117&nbsp;KB<br>1.00 | 105.95&nbsp;ms<br>$\color{#d97706}{1.02}$<br>1264&nbsp;KB<br>$\color{#ea580c}{1.13}$ | 110.34&nbsp;ms<br>1.06<br>2125&nbsp;KB<br>1.90 | 120.05&nbsp;ms<br>1.16<br>3554&nbsp;KB<br>**3.18** | 🏆&nbsp;37.16&nbsp;ms<br>**0.36**<br>9205&nbsp;KB<br>**8.24** | 111.35&nbsp;ms<br>1.07<br>2339&nbsp;KB<br>**2.09** | 130.30&nbsp;ms<br>1.26<br>4880&nbsp;KB<br>**4.37** | 149.66&nbsp;ms<br>1.44<br>14.9&nbsp;MB<br>**13.64** | 143.18&nbsp;ms<br>1.38<br>8608&nbsp;KB<br>**7.70** |

#### PostgreSQL

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 🏆&nbsp;1.53&nbsp;ms<br>1.00<br>8.4&nbsp;KB<br>1.00 | 1.56&nbsp;ms<br>$\color{#d97706}{1.02}$<br>9.3&nbsp;KB<br>$\color{#ea580c}{1.12}$ | 💾&nbsp;1.55&nbsp;ms<br>1.01<br>7.4&nbsp;KB<br>0.89 | 1.58&nbsp;ms<br>1.03<br>8.6&nbsp;KB<br>1.03 | 2.01&nbsp;ms<br>1.32<br>57.1&nbsp;KB<br>**6.83** | 1.57&nbsp;ms<br>1.02<br>8.1&nbsp;KB<br>0.97 | 1.63&nbsp;ms<br>1.06<br>12.1&nbsp;KB<br>1.45 | 1.61&nbsp;ms<br>1.05<br>23.4&nbsp;KB<br>**2.80** | 1.64&nbsp;ms<br>1.07<br>19.9&nbsp;KB<br>**2.38** |
| TxHundredInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 22.61&nbsp;ms<br>1.00<br>310&nbsp;KB<br>1.00 | 💾&nbsp;22.73&nbsp;ms<br>$\color{#d97706}{1.01}$<br>210&nbsp;KB<br>$\color{#16a34a}{0.68}$ | 24.15&nbsp;ms<br>1.07<br>311&nbsp;KB<br>1.00 | 25.00&nbsp;ms<br>1.11<br>431&nbsp;KB<br>1.39 | 🏆&nbsp;3.76&nbsp;ms<br>**0.17**<br>878&nbsp;KB<br>**2.83** | 23.28&nbsp;ms<br>1.03<br>282&nbsp;KB<br>0.91 | 26.30&nbsp;ms<br>1.16<br>694&nbsp;KB<br>**2.24** | 26.64&nbsp;ms<br>1.18<br>1817&nbsp;KB<br>**5.86** | 29.94&nbsp;ms<br>1.32<br>1511&nbsp;KB<br>**4.88** |
| TxBulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 🏆&nbsp;💾&nbsp;5.19&nbsp;ms<br>1.00<br>340&nbsp;KB<br>1.00 | 5.33&nbsp;ms<br>$\color{#d97706}{1.03}$<br>342&nbsp;KB<br>$\color{#d97706}{1.00}$ | 17.08&nbsp;ms<br>**3.29**<br>6849&nbsp;KB<br>**20.14** | 6.38&nbsp;ms<br>1.23<br>846&nbsp;KB<br>**2.49** | 45.19&nbsp;ms<br>**8.71**<br>15.8&nbsp;MB<br>**47.71** | 17.11&nbsp;ms<br>**3.30**<br>6852&nbsp;KB<br>**20.15** | 24.64&nbsp;ms<br>**4.75**<br>3496&nbsp;KB<br>**10.28** | 11.83&nbsp;ms<br>**2.28**<br>1174&nbsp;KB<br>**3.45** | 28.52&nbsp;ms<br>**5.50**<br>7578&nbsp;KB<br>**22.29** |
| TxRollback | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 💾&nbsp;115.56&nbsp;ms<br>1.00<br>927&nbsp;KB<br>1.00 | 118.11&nbsp;ms<br>$\color{#d97706}{1.02}$<br>1068&nbsp;KB<br>$\color{#ea580c}{1.15}$ | 119.47&nbsp;ms<br>1.03<br>1531&nbsp;KB<br>1.65 | 118.21&nbsp;ms<br>1.02<br>2943&nbsp;KB<br>**3.18** | 🏆&nbsp;15.58&nbsp;ms<br>**0.13**<br>4240&nbsp;KB<br>**4.58** | 118.68&nbsp;ms<br>1.03<br>1746&nbsp;KB<br>1.88 | 137.67&nbsp;ms<br>1.19<br>4492&nbsp;KB<br>**4.85** | 137.51&nbsp;ms<br>1.19<br>10.5&nbsp;MB<br>**11.62** | 159.57&nbsp;ms<br>1.38<br>8740&nbsp;KB<br>**9.43** |

### 调用形态对照（读取三态 / 写入两态）

同一个查询或写入，会话形态不同，开销差一个量级。下表是 PerfHub 同批实测（2,000 档，批次 `history-20261122-213611.json`，2026-11-22），读取按**每键**折算、写入按**每操作**，括号内为相对 per-op 形态的变化。

| 方言 | per-op（每操作新建会话） | 长会话（单会话 32 键循环） | 池化租还（每操作一租期） |
|---|---:|---:|---:|
| SQLite | 6.3 µs / 2.4KB | 4.5 µs / 1.4KB（−29% / −39%） | 4.5 µs / 1.5KB（−28% / −37%） |
| MySQL | 182.7 µs / 4.6KB | 195.2 µs / 3.3KB（+7% / −29%） | 196.8 µs / 3.5KB（+8% / −24%） |
| PostgreSQL | 202.4 µs / 3.2KB | 203.6 µs / 2.2KB（+1% / −33%） | 193.6 µs / 2.3KB（−4% / −30%） |

| 方言 | Insert per-op | Insert 池化 | Update per-op | Update 池化 | BulkInsert（2,000 行） | BulkInsert 池化 |
|---|---:|---:|---:|---:|---:|---:|
| SQLite | 17.3 µs / 3.1KB | 16.7 µs / 2.5KB（−3% / −21%） | 7.4 µs / 3.0KB | 6.7 µs / 2.3KB（−10% / −22%） | 3.12 ms / 2623KB | 3.19 ms / 2627KB（+2% / +0%） |
| MySQL | 4.04 ms / 16.2KB | 3.82 ms / 15.1KB（−5% / −7%） | 187.6 µs / 5.2KB | 190.4 µs / 4.4KB（+2% / −15%） | 17.19 ms / 2605KB | 15.22 ms / 2607KB（−11% / +0%） |
| PostgreSQL | 1.36 ms / 9.3KB | 1.32 ms / 7.6KB（−3% / −19%） | 1.37 ms / 9.3KB | 1.42 ms / 8.7KB（+3% / −6%） | 8.80 ms / 2512KB | 9.47 ms / 2546KB（+8% / +1%） |

**多键点读**：同形状的多键读改用 `GetByKeyManyAsync`（SQLite 单参数 `json_each`、PostgreSQL `= ANY(数组)`、MySQL 分批 `IN`），比逐键 `GetAsync` 快 5.0 至 7.8 倍、内存低 3.2 至 3.5 倍（`bench/crossbench/ReadManyProbe`，100 / 1000 / 10000 三档，2026-11-05）。

**形态选择判据**：

- 会话寿命跨多个操作（同一请求里多次读写、报表批次）：用长会话，别每操作新建。读取省约三成时延与四成内存（SQLite），写入省 3% 至 10%。
- 每请求一会话（Web/服务端的典型形状）：用 `DataSessionPool` 租还，收益接近长会话，且不需要在业务代码里传会话对象。
- 一次性调用或低频操作：per-op 形态即可，池化只会多一次租还。
- 单次大调用（整批批量插入）：池化不划算，租还开销摊在单次调用上，实测 PostgreSQL 慢约 8%，SQLite 持平至 +2%，MySQL 略省（BulkInsert −11%）。

### 复现

```bash
# 周度全量（横评 + 微基准 + PerfHub + 门禁 + 报告，约 34 分钟，需本机 PG / MySQL 容器）
dotnet run --project tools/PalORM.PerfCli -c Release -- full --with-crossbench
# 只跑横评批
dotnet run --project tools/PalORM.PerfCli -c Release -- crossbench
```

说明：9 组同批同轮，比值只在同一批内可比；跨机绝对值不可比。87 行完整明细（全部 17 操作、两档、三方言，含时延与内存百分比）见 [性能全量报告-2026-11-22.md](性能全量报告-2026-11-22.md)。PalORM 自身回归（微基准、并发、内存曲线、长稳）由 PerfHub 夹具单独度量，见 [bench/perfhub/README.md](../bench/perfhub/README.md)。

## 🚀 快速开始

> `.NET 11`（全部包单目标 `net11.0`）+ SDK 版本由 `global.json` 精确钉住（当前 `11.0.100-rc.3.26522.148`），IDE 需支持 Roslyn 源生成器；Native AOT 发布另需约 4 GB 内存（ILC 编译期，非运行时）。

```xml
<PackageReference Include="PalORM.PostgreSql" Version="6.9.0" />   <!-- 或 PalORM.MySql / PalORM.Sqlite -->
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
| PostgreSQL | **18.6**（CI `postgres:18`）  | Npgsql 11.0.0                          | 未设；批量写需 9.3+，JSONB 路径查询 9.4+，咨询锁 9.5+                      |
| MySQL      | **8.4.11**（CI `mysql:8.4`）  | MySqlConnector 3.0.0                   | **8.0.19**（`UPDATE JOIN VALUES ROW`，更低回退 CASE WHEN）；8.0 以下不支持 |
| SQLite     | **3.54.1**（SQLite3MC 2.7.1） | Microsoft.Data.Sqlite.Core 11.0.0      | **3.35+**（`INSERT ... RETURNING`；批删 `json_each` 需 3.38+）             |

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
