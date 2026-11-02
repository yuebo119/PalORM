<div align="center">

# PalORM

**面向 Native AOT 的 .NET 11 微 ORM：编译时生成一切，运行时零反射**

[![.NET](https://img.shields.io/badge/.NET-11.0.0--rc.2-512BD4)](https://dotnet.microsoft.com)
[![NuGet](https://img.shields.io/nuget/v/PalORM.Core)](https://www.nuget.org/packages/PalORM.Core)
[![CI](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml/badge.svg)](https://github.com/yuebo119/PalORM/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

[![Native AOT](https://img.shields.io/badge/Native_AOT-3_dialects_verified-512BD4)](docs/AOT部署指南.md)
[![Diagnostics](https://img.shields.io/badge/Compile--time_diagnostics-45_rules-512BD4)](docs/API参考.md)
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
> **数据来源**：9 组同批同轮采集，2026-11-02，批次 `crossbench-20261102-110855.json`。9 组为 PalORM / Dapper / Dapper.AOT / linq2db / EF Core / SqlSugar / FreeSql / RepoDb 与 ADO.NET 手写 SQL（基准），各用生态惯用写法。完整报告见 [性能全量报告-2026-11-02.md](性能全量报告-2026-11-02.md)。

### 总览

9 组 × 三方言 × 两档（2000 / 20000）× 17 操作（事务仅最小档）= **87 格**，每操作两遍反序取平均；所有倍数以同批 ADO.NET 手写 SQL 为基准 1.00，小于 1 为更快。

| 组 | 胜场 /87 | 时延中位比 | 内存中位比 |
|---|---:|---:|---:|
| ADO | — | 1.00 | 1.00 |
| PalORM | 51 | 1.01 | 1.00 |
| Dapper.AOT | 11 | 1.08 | 1.15 |
| RepoDb | 6 | 1.14 | 1.46 |
| EF Core | 8 | 3.00 | 8.04 |
| Dapper | 8 | 1.08 | 1.61 |
| FreeSql | 2 | 1.43 | 2.01 |
| linq2db | 1 | 1.40 | 3.02 |
| SqlSugar | 0 | 2.18 | 3.52 |

- **PalORM**：胜场 51/87（59%），时延中位比 **1.01**（全距 0.46 ~ 1.16），内存中位比 **1.00**，无 1.5 倍以上的劣化格。
- **最大离群**：SqlSugar UpsertBatch（SQLite 107.5 倍、MySQL 35.8 倍）、EF Core 单条 Insert（66.5 ~ 67.2 倍，每操作 DbContext 形态）。

### 操作对照（时延 [倍数] 与内存 [相对基准]，颜色同百分比六档）

> 🏆 = 时延最快组 · 💾 = 内存最省组（均含 ADO 基准）

#### SQLite（2000 与 20000 档）

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 8.4&nbsp;µs<br>1.00<br>2.1KB<br>1.00 | 6.0&nbsp;µs<br>$\color{#65a30d}{0.71}$<br>2.2KB<br>$\color{#d97706}{1.03}$ | 6.8&nbsp;µs<br>0.82<br>2.0KB<br>0.97 | 10.4&nbsp;µs<br>1.24<br>4.4KB<br>**2.10** | 135.1&nbsp;µs<br>**16.08**<br>49.7KB<br>**23.57** | 10.9&nbsp;µs<br>1.30<br>2.8KB<br>1.34 | 44.1&nbsp;µs<br>**5.25**<br>10.2KB<br>**4.85** | 15.8&nbsp;µs<br>1.88<br>6.4KB<br>**3.02** | 84.0&nbsp;µs<br>**10.00**<br>30.0KB<br>**14.22** |
| GetByKey | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 6.5&nbsp;µs<br>1.00<br>2.1KB<br>1.00 | 6.5&nbsp;µs<br>$\color{#d97706}{1.01}$<br>2.2KB<br>$\color{#d97706}{1.03}$ | 7.1&nbsp;µs<br>1.10<br>2.0KB<br>0.97 | 8.9&nbsp;µs<br>1.38<br>4.4KB<br>**2.09** | 80.5&nbsp;µs<br>**12.48**<br>50.0KB<br>**23.73** | 7.7&nbsp;µs<br>1.19<br>2.8KB<br>1.33 | 34.7&nbsp;µs<br>**5.38**<br>10.4KB<br>**4.93** | 16.6&nbsp;µs<br>**2.57**<br>6.4KB<br>**3.02** | 75.9&nbsp;µs<br>**11.78**<br>29.8KB<br>**14.15** |
| GetAllAsync | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 991.9&nbsp;µs<br>1.00<br>308KB<br>1.00 | 1.02&nbsp;ms<br>$\color{#d97706}{1.02}$<br>292KB<br>$\color{#2563eb}{0.95}$ | 1.15&nbsp;ms<br>1.16<br>356KB<br>1.15 | 1.17&nbsp;ms<br>1.18<br>372KB<br>1.21 | 1.39&nbsp;ms<br>1.40<br>729KB<br>**2.36** | 1.40&nbsp;ms<br>1.41<br>513KB<br>1.66 | 1.39&nbsp;ms<br>1.40<br>314KB<br>1.02 | 1.38&nbsp;ms<br>1.39<br>311KB<br>1.01 | 3.19&nbsp;ms<br>**3.22**<br>580KB<br>1.88 |
| GetAllAsync | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 10.42&nbsp;ms<br>1.00<br>3.3MB<br>1.00 | 10.46&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>2.9MB<br>$\color{#65a30d}{0.89}$ | 12.07&nbsp;ms<br>1.16<br>3.7MB<br>1.14 | 12.13&nbsp;ms<br>1.16<br>3.9MB<br>1.19 | 14.53&nbsp;ms<br>1.39<br>7.0MB<br>**2.14** | 14.41&nbsp;ms<br>1.38<br>5.3MB<br>1.61 | 14.94&nbsp;ms<br>1.43<br>3.3MB<br>1.00 | 14.73&nbsp;ms<br>1.41<br>3.3MB<br>1.00 | 33.34&nbsp;ms<br>**3.20**<br>5.8MB<br>1.77 |
| Insert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 17.5&nbsp;µs<br>1.00<br>2.8KB<br>1.00 | 17.6&nbsp;µs<br>$\color{#d97706}{1.01}$<br>2.8KB<br>$\color{#2563eb}{0.98}$ | 19.1&nbsp;µs<br>1.09<br>2.8KB<br>1.00 | 19.6&nbsp;µs<br>1.13<br>3.0KB<br>1.05 | 1.16&nbsp;ms<br>**66.54**<br>56.3KB<br>**19.86** | 18.6&nbsp;µs<br>1.06<br>3.6KB<br>1.27 | 32.2&nbsp;µs<br>1.84<br>5.7KB<br>**2.01** | 1.14&nbsp;ms<br>**65.17**<br>19.9KB<br>**7.02** | 1.10&nbsp;ms<br>**63.23**<br>14.9KB<br>**5.27** |
| Insert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 18.0&nbsp;µs<br>1.00<br>2.8KB<br>1.00 | 18.4&nbsp;µs<br>$\color{#d97706}{1.02}$<br>2.7KB<br>$\color{#2563eb}{0.98}$ | 18.9&nbsp;µs<br>1.05<br>2.9KB<br>1.04 | 20.5&nbsp;µs<br>1.14<br>3.0KB<br>1.08 | 1.21&nbsp;ms<br>**67.24**<br>56.5KB<br>**20.33** | 19.6&nbsp;µs<br>1.09<br>3.6KB<br>1.30 | 32.7&nbsp;µs<br>1.81<br>5.7KB<br>**2.06** | 1.10&nbsp;ms<br>**61.14**<br>19.5KB<br>**7.01** | 1.16&nbsp;ms<br>**64.50**<br>15.1KB<br>**5.42** |
| Update | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 7.4&nbsp;µs<br>1.00<br>2.5KB<br>1.00 | 7.2&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>2.9KB<br>$\color{#ea580c}{1.16}$ | 7.7&nbsp;µs<br>1.04<br>2.8KB<br>1.11 | 9.4&nbsp;µs<br>1.26<br>4.0KB<br>1.60 | 82.8&nbsp;µs<br>**11.18**<br>53.6KB<br>**21.31** | 8.1&nbsp;µs<br>1.09<br>3.3KB<br>1.30 | 22.9&nbsp;µs<br>**3.10**<br>7.4KB<br>**2.94** | 14.8&nbsp;µs<br>**2.01**<br>19.7KB<br>**7.85** | 33.8&nbsp;µs<br>**4.57**<br>14.6KB<br>**5.79** |
| Update | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 8.6&nbsp;µs<br>1.00<br>2.5KB<br>1.00 | 7.9&nbsp;µs<br>$\color{#2563eb}{0.92}$<br>2.9KB<br>$\color{#ea580c}{1.16}$ | 8.0&nbsp;µs<br>0.94<br>2.8KB<br>1.11 | 9.9&nbsp;µs<br>1.15<br>4.0KB<br>1.60 | 87.5&nbsp;µs<br>**10.23**<br>53.6KB<br>**21.27** | 10.6&nbsp;µs<br>1.23<br>3.3KB<br>1.30 | 23.4&nbsp;µs<br>**2.74**<br>7.4KB<br>**2.94** | 16.6&nbsp;µs<br>1.95<br>19.8KB<br>**7.84** | 36.3&nbsp;µs<br>**4.25**<br>14.6KB<br>**5.78** |
| BulkInsert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;linq2db | 2.64&nbsp;ms<br>1.00<br>1.7MB<br>1.00 | 2.63&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>1.7MB<br>$\color{#6b7280}{1.00}$ | 4.88&nbsp;ms<br>1.85<br>4.3MB<br>**2.48** | 16.27&nbsp;ms<br>**6.17**<br>2.6MB<br>1.54 | 22.13&nbsp;ms<br>**8.39**<br>17.3MB<br>**10.07** | 5.34&nbsp;ms<br>**2.03**<br>4.3MB<br>**2.48** | 10.84&nbsp;ms<br>**4.11**<br>6.5MB<br>**3.79** | 4.95&nbsp;ms<br>1.88<br>1.3MB<br>0.73 | 7.49&nbsp;ms<br>**2.84**<br>4.4MB<br>**2.57** |
| BulkInsert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;linq2db | 32.08&nbsp;ms<br>1.00<br>17.2MB<br>1.00 | 36.14&nbsp;ms<br>$\color{#ea580c}{1.13}$<br>17.2MB<br>$\color{#6b7280}{1.00}$ | 58.03&nbsp;ms<br>1.81<br>42.6MB<br>**2.48** | 180.66&nbsp;ms<br>**5.63**<br>26.2MB<br>1.53 | 281.84&nbsp;ms<br>**8.78**<br>172.2MB<br>**10.03** | 55.08&nbsp;ms<br>1.72<br>42.6MB<br>**2.48** | 141.77&nbsp;ms<br>**4.42**<br>66.2MB<br>**3.85** | 54.07&nbsp;ms<br>1.69<br>11.0MB<br>0.64 | 90.32&nbsp;ms<br>**2.82**<br>44.9MB<br>**2.62** |
| BulkUpdate | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 2.46&nbsp;ms<br>1.00<br>1.7MB<br>1.00 | 2.37&nbsp;ms<br>$\color{#2563eb}{0.96}$<br>1.7MB<br>$\color{#6b7280}{1.00}$ | 8.60&nbsp;ms<br>**3.49**<br>5.5MB<br>**3.25** | 7.61&nbsp;ms<br>**3.09**<br>5.2MB<br>**3.06** | 27.41&nbsp;ms<br>**11.14**<br>17.9MB<br>**10.59** | 9.37&nbsp;ms<br>**3.81**<br>6.3MB<br>**3.75** | 13.48&nbsp;ms<br>**5.48**<br>7.0MB<br>**4.15** | 23.37&nbsp;ms<br>**9.50**<br>38.5MB<br>**22.73** | 6.25&nbsp;ms<br>**2.54**<br>4.4MB<br>**2.59** |
| BulkUpdate | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 28.39&nbsp;ms<br>1.00<br>17.0MB<br>1.00 | 27.66&nbsp;ms<br>$\color{#2563eb}{0.97}$<br>17.0MB<br>$\color{#6b7280}{1.00}$ | 99.50&nbsp;ms<br>**3.50**<br>55.2MB<br>**3.25** | 83.54&nbsp;ms<br>**2.94**<br>51.9MB<br>**3.05** | 357.82&nbsp;ms<br>**12.60**<br>178.8MB<br>**10.52** | 102.56&nbsp;ms<br>**3.61**<br>63.6MB<br>**3.74** | 150.44&nbsp;ms<br>**5.30**<br>73.4MB<br>**4.32** | 246.77&nbsp;ms<br>**8.69**<br>385.3MB<br>**22.67** | 81.36&nbsp;ms<br>**2.87**<br>44.8MB<br>**2.63** |
| BulkDelete | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 848.1&nbsp;µs<br>1.00<br>197KB<br>1.00 | 832.0&nbsp;µs<br>$\color{#2563eb}{0.98}$<br>199KB<br>$\color{#d97706}{1.01}$ | 22.52&nbsp;ms<br>**26.55**<br>1.6MB<br>**8.13** | 19.46&nbsp;ms<br>**22.94**<br>3.0MB<br>**15.69** | 12.00&nbsp;ms<br>**14.15**<br>2.5MB<br>**12.81** | 22.23&nbsp;ms<br>**26.22**<br>1.6MB<br>**8.12** | 1.60&nbsp;ms<br>1.88<br>569KB<br>**2.89** | 2.56&nbsp;ms<br>**3.01**<br>271KB<br>1.37 | 2.61&nbsp;ms<br>**3.07**<br>354KB<br>1.80 |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;ADO | 10.21&nbsp;ms<br>1.00<br>2.0MB<br>1.00 | 9.51&nbsp;ms<br>$\color{#2563eb}{0.93}$<br>2.0MB<br>$\color{#6b7280}{1.00}$ | 239.71&nbsp;ms<br>**23.48**<br>15.7MB<br>**7.72** | 215.71&nbsp;ms<br>**21.13**<br>31.6MB<br>**15.58** | 130.08&nbsp;ms<br>**12.74**<br>24.6MB<br>**12.14** | 245.81&nbsp;ms<br>**24.08**<br>15.7MB<br>**7.73** | 19.65&nbsp;ms<br>1.92<br>6.0MB<br>**2.94** | 18.08&nbsp;ms<br>1.77<br>2.7MB<br>1.34 | 41.26&nbsp;ms<br>**4.04**<br>4.2MB<br>**2.09** |
| UpsertBatch | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 2.44&nbsp;ms<br>1.00<br>1.7MB<br>1.00 | 2.51&nbsp;ms<br>$\color{#d97706}{1.03}$<br>1.7MB<br>$\color{#d97706}{1.01}$ | 111.43&nbsp;ms<br>**45.70**<br>8.0MB<br>**4.70** | 18.67&nbsp;ms<br>**7.66**<br>11.5MB<br>**6.79** | 19.31&nbsp;ms<br>**7.92**<br>5.4MB<br>**3.17** | 4.18&nbsp;ms<br>1.71<br>4.2MB<br>**2.50** | 3.68&nbsp;ms<br>1.51<br>2.5MB<br>1.47 | 22.90&nbsp;ms<br>**9.39**<br>42.8MB<br>**25.23** | 262.15&nbsp;ms<br>**107.53**<br>219.9MB<br>**129.74** |
| UpsertBatch | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 28.14&nbsp;ms<br>1.00<br>17.0MB<br>1.00 | 29.04&nbsp;ms<br>$\color{#d97706}{1.03}$<br>17.2MB<br>$\color{#d97706}{1.01}$ | 1.17&nbsp;s<br>**41.74**<br>79.9MB<br>**4.70** | 198.99&nbsp;ms<br>**7.07**<br>115.1MB<br>**6.77** | 1.60&nbsp;s<br>**56.72**<br>57.0MB<br>**3.35** | 50.39&nbsp;ms<br>1.79<br>42.4MB<br>**2.49** | 41.87&nbsp;ms<br>1.49<br>25.8MB<br>1.52 | 247.59&nbsp;ms<br>**8.80**<br>427.7MB<br>**25.16** | 2.78&nbsp;s<br>**98.72**<br>2365.1MB<br>**139.13** |

#### MySQL（2000 与 20000 档）

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 205.5&nbsp;µs<br>1.00<br>4.6KB<br>1.00 | 220.2&nbsp;µs<br>$\color{#d97706}{1.07}$<br>5.1KB<br>$\color{#ea580c}{1.11}$ | 204.9&nbsp;µs<br>1.00<br>5.3KB<br>1.16 | 246.9&nbsp;µs<br>1.20<br>8.6KB<br>1.90 | 742.7&nbsp;µs<br>**3.61**<br>94.9KB<br>**20.85** | 216.3&nbsp;µs<br>1.05<br>5.9KB<br>1.30 | 456.1&nbsp;µs<br>**2.22**<br>16.8KB<br>**3.68** | 272.3&nbsp;µs<br>1.32<br>25.7KB<br>**5.65** | 496.1&nbsp;µs<br>**2.41**<br>36.4KB<br>**8.00** |
| GetByKey | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 183.6&nbsp;µs<br>1.00<br>4.5KB<br>1.00 | 196.7&nbsp;µs<br>$\color{#d97706}{1.07}$<br>5.0KB<br>$\color{#ea580c}{1.11}$ | 198.1&nbsp;µs<br>1.08<br>5.2KB<br>1.17 | 204.3&nbsp;µs<br>1.11<br>8.5KB<br>1.90 | 662.5&nbsp;µs<br>**3.61**<br>94.7KB<br>**21.13** | 188.2&nbsp;µs<br>1.03<br>5.8KB<br>1.29 | 420.9&nbsp;µs<br>**2.29**<br>16.7KB<br>**3.73** | 251.1&nbsp;µs<br>1.37<br>25.8KB<br>**5.76** | 444.5&nbsp;µs<br>**2.42**<br>36.2KB<br>**8.07** |
| GetAllAsync | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 1.16&nbsp;ms<br>1.00<br>398KB<br>1.00 | 1.18&nbsp;ms<br>$\color{#d97706}{1.02}$<br>383KB<br>$\color{#2563eb}{0.96}$ | 1.18&nbsp;ms<br>1.02<br>400KB<br>1.00 | 1.28&nbsp;ms<br>1.10<br>401KB<br>1.01 | 3.48&nbsp;ms<br>**3.01**<br>4.4MB<br>**11.30** | 1.24&nbsp;ms<br>1.07<br>447KB<br>1.12 | 1.55&nbsp;ms<br>1.34<br>407KB<br>1.02 | 2.89&nbsp;ms<br>**2.49**<br>4.0MB<br>**10.19** | 1.75&nbsp;ms<br>1.51<br>419KB<br>1.05 |
| GetAllAsync | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;PalORM | 9.64&nbsp;ms<br>1.00<br>4.0MB<br>1.00 | 9.78&nbsp;ms<br>$\color{#d97706}{1.02}$<br>3.7MB<br>$\color{#2563eb}{0.91}$ | 9.41&nbsp;ms<br>0.98<br>4.0MB<br>1.00 | 10.11&nbsp;ms<br>1.05<br>4.0MB<br>1.00 | 32.49&nbsp;ms<br>**3.37**<br>46.3MB<br>**11.44** | 10.26&nbsp;ms<br>1.06<br>4.5MB<br>1.11 | 10.75&nbsp;ms<br>1.12<br>4.1MB<br>1.00 | 29.78&nbsp;ms<br>**3.09**<br>43.2MB<br>**10.68** | 12.88&nbsp;ms<br>1.34<br>4.1MB<br>1.01 |
| Insert | 2,000 | 🏆&nbsp;RepoDb<br>💾&nbsp;Dapper.AOT | 4.43&nbsp;ms<br>1.00<br>17.9KB<br>1.00 | 4.22&nbsp;ms<br>$\color{#2563eb}{0.95}$<br>17.3KB<br>$\color{#2563eb}{0.97}$ | 4.07&nbsp;ms<br>0.92<br>16.4KB<br>0.92 | 4.06&nbsp;ms<br>0.92<br>17.7KB<br>0.99 | 6.02&nbsp;ms<br>1.36<br>122KB<br>**6.84** | 4.20&nbsp;ms<br>0.95<br>17.0KB<br>0.95 | 4.43&nbsp;ms<br>1.00<br>23.2KB<br>1.30 | 4.12&nbsp;ms<br>0.93<br>37.2KB<br>**2.08** | 4.42&nbsp;ms<br>1.00<br>32.1KB<br>1.79 |
| Insert | 20,000 | 🏆&nbsp;RepoDb<br>💾&nbsp;Dapper.AOT | 3.87&nbsp;ms<br>1.00<br>16.2KB<br>1.00 | 3.97&nbsp;ms<br>$\color{#d97706}{1.03}$<br>18.4KB<br>$\color{#ea580c}{1.13}$ | 3.77&nbsp;ms<br>0.97<br>15.3KB<br>0.95 | 3.73&nbsp;ms<br>0.96<br>16.5KB<br>1.02 | 5.44&nbsp;ms<br>1.40<br>121KB<br>**7.43** | 3.87&nbsp;ms<br>1.00<br>15.9KB<br>0.98 | 4.16&nbsp;ms<br>1.08<br>22.8KB<br>1.41 | 4.00&nbsp;ms<br>1.03<br>36.3KB<br>**2.24** | 4.17&nbsp;ms<br>1.08<br>29.0KB<br>1.79 |
| Update | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 201.3&nbsp;µs<br>1.00<br>5.2KB<br>1.00 | 230.5&nbsp;µs<br>$\color{#ea580c}{1.15}$<br>5.8KB<br>$\color{#ea580c}{1.12}$ | 211.5&nbsp;µs<br>1.05<br>4.7KB<br>0.91 | 243.8&nbsp;µs<br>1.21<br>7.7KB<br>1.47 | 745.3&nbsp;µs<br>**3.70**<br>93.2KB<br>**17.85** | 216.7&nbsp;µs<br>1.08<br>5.3KB<br>1.01 | 457.3&nbsp;µs<br>**2.27**<br>13.3KB<br>**2.55** | 272.9&nbsp;µs<br>1.36<br>31.0KB<br>**5.94** | 439.4&nbsp;µs<br>**2.18**<br>20.4KB<br>**3.90** |
| Update | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 188.9&nbsp;µs<br>1.00<br>5.2KB<br>1.00 | 189.7&nbsp;µs<br>$\color{#6b7280}{1.00}$<br>5.7KB<br>$\color{#ea580c}{1.10}$ | 198.1&nbsp;µs<br>1.05<br>4.7KB<br>0.91 | 209.3&nbsp;µs<br>1.11<br>7.6KB<br>1.46 | 685.4&nbsp;µs<br>**3.63**<br>93.0KB<br>**17.93** | 196.6&nbsp;µs<br>1.04<br>5.2KB<br>1.00 | 400.6&nbsp;µs<br>**2.12**<br>13.2KB<br>**2.54** | 255.0&nbsp;µs<br>1.35<br>31.0KB<br>**5.97** | 423.3&nbsp;µs<br>**2.24**<br>20.3KB<br>**3.92** |
| BulkInsert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 14.57&nbsp;ms<br>1.00<br>504KB<br>1.00 | 12.30&nbsp;ms<br>$\color{#65a30d}{0.84}$<br>486KB<br>$\color{#2563eb}{0.96}$ | 26.00&nbsp;ms<br>1.78<br>6.7MB<br>**13.51** | 13.20&nbsp;ms<br>0.91<br>488KB<br>0.97 | 65.92&nbsp;ms<br>**4.52**<br>20.0MB<br>**40.60** | 30.22&nbsp;ms<br>**2.07**<br>6.7MB<br>**13.53** | 18.63&nbsp;ms<br>1.28<br>3.2MB<br>**6.58** | 22.96&nbsp;ms<br>1.58<br>3.3MB<br>**6.66** | 15.02&nbsp;ms<br>1.03<br>2.3MB<br>**4.73** |
| BulkInsert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 57.08&nbsp;ms<br>1.00<br>4.4MB<br>1.00 | 58.60&nbsp;ms<br>$\color{#d97706}{1.03}$<br>4.4MB<br>$\color{#6b7280}{1.00}$ | 255.84&nbsp;ms<br>**4.48**<br>66.5MB<br>**15.01** | 57.66&nbsp;ms<br>1.01<br>4.4MB<br>1.00 | 560.12&nbsp;ms<br>**9.81**<br>198.5MB<br>**44.78** | 258.43&nbsp;ms<br>**4.53**<br>66.5MB<br>**15.00** | 120.52&nbsp;ms<br>**2.11**<br>33.2MB<br>**7.49** | 261.31&nbsp;ms<br>**4.58**<br>31.9MB<br>**7.19** | 101.89&nbsp;ms<br>1.79<br>24.6MB<br>**5.55** |
| BulkUpdate | 2,000 | 🏆&nbsp;FreeSql<br>💾&nbsp;RepoDb | 10.82&nbsp;ms<br>1.00<br>4.6MB<br>1.00 | 10.45&nbsp;ms<br>$\color{#2563eb}{0.97}$<br>3.7MB<br>$\color{#65a30d}{0.80}$ | 11.72&nbsp;ms<br>1.08<br>6.5MB<br>1.41 | 175.45&nbsp;ms<br>**16.22**<br>950KB<br>**0.20** | 130.18&nbsp;ms<br>**12.04**<br>35.8MB<br>**7.81** | 12.35&nbsp;ms<br>1.14<br>6.5MB<br>1.41 | 10.31&nbsp;ms<br>0.95<br>2.1MB<br>**0.45** | 30.51&nbsp;ms<br>**2.82**<br>7.6MB<br>1.65 | 11.98&nbsp;ms<br>1.11<br>2.3MB<br>**0.50** |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;RepoDb | 82.60&nbsp;ms<br>1.00<br>14.6MB<br>1.00 | 83.49&nbsp;ms<br>$\color{#d97706}{1.01}$<br>13.7MB<br>$\color{#2563eb}{0.94}$ | 144.20&nbsp;ms<br>1.75<br>64.5MB<br>**4.42** | 376.72&nbsp;ms<br>**4.56**<br>5.2MB<br>**0.36** | 1.29&nbsp;s<br>**15.64**<br>358.5MB<br>**24.56** | 142.38&nbsp;ms<br>1.72<br>64.5MB<br>**4.42** | 92.41&nbsp;ms<br>1.12<br>21.8MB<br>1.49 | 296.07&nbsp;ms<br>**3.58**<br>75.8MB<br>**5.19** | 102.99&nbsp;ms<br>1.25<br>23.9MB<br>1.63 |
| BulkDelete | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;SqlSugar | 12.81&nbsp;ms<br>1.00<br>884KB<br>1.00 | 12.71&nbsp;ms<br>$\color{#2563eb}{0.99}$<br>810KB<br>$\color{#2563eb}{0.92}$ | 13.00&nbsp;ms<br>1.01<br>1.3MB<br>1.56 | 27.85&nbsp;ms<br>**2.17**<br>19.3MB<br>**22.34** | 25.15&nbsp;ms<br>1.96<br>3.7MB<br>**4.28** | 12.94&nbsp;ms<br>1.01<br>1.3MB<br>1.56 | 18.31&nbsp;ms<br>1.43<br>616KB<br>0.70 | 14.23&nbsp;ms<br>1.11<br>717KB<br>0.81 | 13.03&nbsp;ms<br>1.02<br>379KB<br>**0.43** |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 73.08&nbsp;ms<br>1.00<br>8.4MB<br>1.00 | 68.18&nbsp;ms<br>$\color{#2563eb}{0.93}$<br>3.5MB<br>$\color{#16a34a}{0.41}$ | 75.55&nbsp;ms<br>1.03<br>13.2MB<br>1.57 | 228.94&nbsp;ms<br>**3.13**<br>193.5MB<br>**22.94** | 282.26&nbsp;ms<br>**3.86**<br>37.0MB<br>**4.39** | 77.97&nbsp;ms<br>1.07<br>13.2MB<br>1.57 | 187.25&nbsp;ms<br>**2.56**<br>6.3MB<br>0.74 | 76.04&nbsp;ms<br>1.04<br>6.9MB<br>0.82 | 167.45&nbsp;ms<br>**2.29**<br>4.6MB<br>0.54 |
| UpsertBatch | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;RepoDb | 9.06&nbsp;ms<br>1.00<br>2.9MB<br>1.00 | 8.59&nbsp;ms<br>$\color{#2563eb}{0.95}$<br>2.5MB<br>$\color{#65a30d}{0.85}$ | 12.32&nbsp;ms<br>1.36<br>6.6MB<br>**2.28** | 167.88&nbsp;ms<br>**18.53**<br>995KB<br>**0.34** | 16.39&nbsp;ms<br>1.81<br>10.2MB<br>**3.51** | 12.02&nbsp;ms<br>1.33<br>6.6MB<br>**2.28** | 9.16&nbsp;ms<br>1.01<br>2.5MB<br>0.86 | 32.36&nbsp;ms<br>**3.57**<br>7.9MB<br>**2.73** | 264.23&nbsp;ms<br>**29.17**<br>219.5MB<br>**75.72** |
| UpsertBatch | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;RepoDb | 74.50&nbsp;ms<br>1.00<br>12.9MB<br>1.00 | 81.44&nbsp;ms<br>$\color{#d97706}{1.09}$<br>12.6MB<br>$\color{#2563eb}{0.98}$ | 140.02&nbsp;ms<br>1.88<br>66.2MB<br>**5.15** | 391.70&nbsp;ms<br>**5.26**<br>5.5MB<br>**0.43** | 182.47&nbsp;ms<br>**2.45**<br>103.4MB<br>**8.04** | 145.07&nbsp;ms<br>1.95<br>66.2MB<br>**5.15** | 128.93&nbsp;ms<br>1.73<br>25.8MB<br>**2.01** | 313.72&nbsp;ms<br>**4.21**<br>79.1MB<br>**6.16** | 2.67&nbsp;s<br>**35.77**<br>2359.3MB<br>**183.57** |

#### PostgreSQL（2000 与 20000 档）

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| GetByKey | 2,000 | 🏆&nbsp;Dapper<br>💾&nbsp;Dapper.AOT | 202.6&nbsp;µs<br>1.00<br>3.0KB<br>1.00 | 201.9&nbsp;µs<br>$\color{#6b7280}{1.00}$<br>3.4KB<br>$\color{#ea580c}{1.15}$ | 200.8&nbsp;µs<br>0.99<br>2.7KB<br>0.89 | 212.4&nbsp;µs<br>1.05<br>5.8KB<br>1.95 | 304.5&nbsp;µs<br>1.50<br>44.0KB<br>**14.73** | 190.9&nbsp;µs<br>0.94<br>3.2KB<br>1.08 | 267.9&nbsp;µs<br>1.32<br>12.0KB<br>**4.03** | 221.4&nbsp;µs<br>1.09<br>7.7KB<br>**2.57** | 321.8&nbsp;µs<br>1.59<br>31.4KB<br>**10.51** |
| GetByKey | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 200.3&nbsp;µs<br>1.00<br>3.0KB<br>1.00 | 203.1&nbsp;µs<br>$\color{#d97706}{1.01}$<br>3.4KB<br>$\color{#ea580c}{1.16}$ | 202.3&nbsp;µs<br>1.01<br>2.7KB<br>0.89 | 217.8&nbsp;µs<br>1.09<br>5.8KB<br>1.96 | 304.7&nbsp;µs<br>1.52<br>44.0KB<br>**14.79** | 207.9&nbsp;µs<br>1.04<br>3.3KB<br>1.10 | 264.9&nbsp;µs<br>1.32<br>12.1KB<br>**4.05** | 217.9&nbsp;µs<br>1.09<br>7.7KB<br>**2.58** | 323.5&nbsp;µs<br>1.62<br>31.4KB<br>**10.56** |
| GetAllAsync | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 629.3&nbsp;µs<br>1.00<br>239KB<br>1.00 | 633.8&nbsp;µs<br>$\color{#d97706}{1.01}$<br>224KB<br>$\color{#2563eb}{0.94}$ | 673.6&nbsp;µs<br>1.07<br>239KB<br>1.00 | 634.2&nbsp;µs<br>1.01<br>240KB<br>1.01 | 958.5&nbsp;µs<br>1.52<br>652KB<br>**2.73** | 635.7&nbsp;µs<br>1.01<br>442KB<br>1.85 | 736.5&nbsp;µs<br>1.17<br>244KB<br>1.02 | 697.8&nbsp;µs<br>1.11<br>241KB<br>1.01 | 915.2&nbsp;µs<br>1.45<br>255KB<br>1.06 |
| GetAllAsync | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;PalORM | 5.04&nbsp;ms<br>1.00<br>2.5MB<br>1.00 | 5.09&nbsp;ms<br>$\color{#d97706}{1.01}$<br>2.2MB<br>$\color{#65a30d}{0.86}$ | 5.11&nbsp;ms<br>1.01<br>2.5MB<br>1.00 | 5.22&nbsp;ms<br>1.04<br>2.5MB<br>1.00 | 7.96&nbsp;ms<br>1.58<br>6.2MB<br>**2.48** | 5.24&nbsp;ms<br>1.04<br>4.5MB<br>1.79 | 5.90&nbsp;ms<br>1.17<br>2.5MB<br>1.00 | 5.60&nbsp;ms<br>1.11<br>2.5MB<br>1.00 | 7.31&nbsp;ms<br>1.45<br>2.5MB<br>1.01 |
| Insert | 2,000 | 🏆&nbsp;Dapper<br>💾&nbsp;Dapper.AOT | 1.35&nbsp;ms<br>1.00<br>7.4KB<br>1.00 | 1.37&nbsp;ms<br>$\color{#d97706}{1.01}$<br>7.4KB<br>$\color{#6b7280}{1.00}$ | 1.40&nbsp;ms<br>1.04<br>7.0KB<br>0.94 | 1.34&nbsp;ms<br>0.99<br>7.7KB<br>1.04 | 1.49&nbsp;ms<br>1.10<br>51.9KB<br>**6.97** | 1.34&nbsp;ms<br>0.99<br>7.1KB<br>0.96 | 1.42&nbsp;ms<br>1.05<br>10.9KB<br>1.46 | 1.39&nbsp;ms<br>1.03<br>21.5KB<br>**2.89** | 1.45&nbsp;ms<br>1.07<br>18.6KB<br>**2.50** |
| Insert | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 1.47&nbsp;ms<br>1.00<br>7.8KB<br>1.00 | 1.36&nbsp;ms<br>$\color{#2563eb}{0.93}$<br>7.5KB<br>$\color{#2563eb}{0.96}$ | 1.38&nbsp;ms<br>0.94<br>6.6KB<br>0.85 | 1.51&nbsp;ms<br>1.03<br>8.2KB<br>1.05 | 1.54&nbsp;ms<br>1.05<br>54.1KB<br>**6.94** | 1.48&nbsp;ms<br>1.01<br>7.5KB<br>0.96 | 1.52&nbsp;ms<br>1.04<br>11.2KB<br>1.43 | 1.45&nbsp;ms<br>0.99<br>21.8KB<br>**2.79** | 1.65&nbsp;ms<br>1.12<br>19.3KB<br>**2.48** |
| Update | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;Dapper.AOT | 1.38&nbsp;ms<br>1.00<br>7.2KB<br>1.00 | 1.34&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>7.7KB<br>$\color{#d97706}{1.07}$ | 1.37&nbsp;ms<br>1.00<br>6.5KB<br>0.91 | 1.38&nbsp;ms<br>1.00<br>9.4KB<br>1.30 | 1.53&nbsp;ms<br>1.11<br>52.8KB<br>**7.32** | 1.40&nbsp;ms<br>1.02<br>7.1KB<br>0.99 | 1.44&nbsp;ms<br>1.05<br>13.0KB<br>1.81 | 1.37&nbsp;ms<br>1.00<br>25.8KB<br>**3.58** | 1.51&nbsp;ms<br>1.10<br>21.2KB<br>**2.93** |
| Update | 20,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;Dapper.AOT | 1.53&nbsp;ms<br>1.00<br>7.7KB<br>1.00 | 1.43&nbsp;ms<br>$\color{#2563eb}{0.94}$<br>8.0KB<br>$\color{#d97706}{1.05}$ | 1.43&nbsp;ms<br>0.94<br>6.8KB<br>0.88 | 1.46&nbsp;ms<br>0.96<br>9.6KB<br>1.25 | 1.63&nbsp;ms<br>1.07<br>52.5KB<br>**6.83** | 1.45&nbsp;ms<br>0.95<br>7.2KB<br>0.94 | 1.47&nbsp;ms<br>0.96<br>13.3KB<br>1.73 | 1.54&nbsp;ms<br>1.01<br>25.4KB<br>**3.30** | 1.64&nbsp;ms<br>1.08<br>21.7KB<br>**2.82** |
| BulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 4.83&nbsp;ms<br>1.00<br>341KB<br>1.00 | 4.99&nbsp;ms<br>$\color{#d97706}{1.03}$<br>344KB<br>$\color{#d97706}{1.01}$ | 19.11&nbsp;ms<br>**3.96**<br>6.7MB<br>**20.10** | 5.41&nbsp;ms<br>1.12<br>847KB<br>**2.48** | 46.54&nbsp;ms<br>**9.64**<br>15.8MB<br>**47.33** | 17.84&nbsp;ms<br>**3.69**<br>6.7MB<br>**20.10** | 13.33&nbsp;ms<br>**2.76**<br>3.8MB<br>**11.55** | 13.20&nbsp;ms<br>**2.73**<br>1.2MB<br>**3.56** | 27.29&nbsp;ms<br>**5.65**<br>7.4MB<br>**22.30** |
| BulkInsert | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 20.56&nbsp;ms<br>1.00<br>3.3MB<br>1.00 | 21.23&nbsp;ms<br>$\color{#d97706}{1.03}$<br>3.3MB<br>$\color{#6b7280}{1.00}$ | 203.70&nbsp;ms<br>**9.91**<br>66.9MB<br>**20.54** | 23.02&nbsp;ms<br>1.12<br>8.0MB<br>**2.46** | 484.26&nbsp;ms<br>**23.55**<br>152.4MB<br>**46.79** | 219.39&nbsp;ms<br>**10.67**<br>67.0MB<br>**20.56** | 113.97&nbsp;ms<br>**5.54**<br>39.6MB<br>**12.15** | 123.02&nbsp;ms<br>**5.98**<br>10.3MB<br>**3.16** | 298.59&nbsp;ms<br>**14.52**<br>75.5MB<br>**23.17** |
| BulkUpdate | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 5.54&nbsp;ms<br>1.00<br>419KB<br>1.00 | 6.35&nbsp;ms<br>$\color{#ea580c}{1.15}$<br>425KB<br>$\color{#d97706}{1.01}$ | 17.76&nbsp;ms<br>**3.21**<br>6.5MB<br>**15.96** | 54.52&nbsp;ms<br>**9.85**<br>3.8MB<br>**9.28** | 61.45&nbsp;ms<br>**11.10**<br>16.0MB<br>**38.98** | 17.33&nbsp;ms<br>**3.13**<br>6.5MB<br>**15.97** | 15.23&nbsp;ms<br>**2.75**<br>2.7MB<br>**6.64** | 13.04&nbsp;ms<br>**2.35**<br>2.1MB<br>**5.09** | 37.61&nbsp;ms<br>**6.79**<br>11.8MB<br>**28.86** |
| BulkUpdate | 20,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 69.89&nbsp;ms<br>1.00<br>4.1MB<br>1.00 | 71.32&nbsp;ms<br>$\color{#d97706}{1.02}$<br>4.1MB<br>$\color{#6b7280}{1.00}$ | 237.65&nbsp;ms<br>**3.40**<br>65.4MB<br>**15.99** | 517.93&nbsp;ms<br>**7.41**<br>37.9MB<br>**9.27** | 692.38&nbsp;ms<br>**9.91**<br>154.4MB<br>**37.75** | 230.11&nbsp;ms<br>**3.29**<br>65.4MB<br>**15.99** | 134.12&nbsp;ms<br>1.92<br>28.3MB<br>**6.92** | 174.71&nbsp;ms<br>**2.50**<br>21.2MB<br>**5.18** | 393.38&nbsp;ms<br>**5.63**<br>120.4MB<br>**29.44** |
| BulkDelete | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 3.65&nbsp;ms<br>1.00<br>784KB<br>1.00 | 2.47&nbsp;ms<br>$\color{#16a34a}{0.68}$<br>89.7KB<br>$\color{#16a34a}{0.11}$ | 3.94&nbsp;ms<br>1.08<br>1.3MB<br>1.71 | 19.67&nbsp;ms<br>**5.39**<br>19.2MB<br>**25.02** | 4.05&nbsp;ms<br>1.11<br>177KB<br>**0.23** | 4.38&nbsp;ms<br>1.20<br>1.3MB<br>1.71 | 5.70&nbsp;ms<br>1.56<br>571KB<br>0.73 | 4.67&nbsp;ms<br>1.28<br>264KB<br>**0.34** | 4.28&nbsp;ms<br>1.17<br>347KB<br>**0.44** |
| BulkDelete | 20,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 27.82&nbsp;ms<br>1.00<br>7.6MB<br>1.00 | 12.71&nbsp;ms<br>$\color{#16a34a}{0.46}$<br>820KB<br>$\color{#16a34a}{0.11}$ | 31.28&nbsp;ms<br>1.12<br>13.0MB<br>1.71 | 193.38&nbsp;ms<br>**6.95**<br>192.5MB<br>**25.27** | 44.31&nbsp;ms<br>1.59<br>1.7MB<br>**0.23** | 36.00&nbsp;ms<br>1.29<br>13.0MB<br>1.71 | 61.58&nbsp;ms<br>**2.21**<br>6.0MB<br>0.78 | 46.82&nbsp;ms<br>1.68<br>2.7MB<br>**0.35** | 29.30&nbsp;ms<br>1.05<br>4.3MB<br>0.57 |
| UpsertBatch | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 15.49&nbsp;ms<br>1.00<br>2.7MB<br>1.00 | 7.75&nbsp;ms<br>$\color{#16a34a}{0.50}$<br>446KB<br>$\color{#16a34a}{0.16}$ | 19.62&nbsp;ms<br>1.27<br>6.7MB<br>**2.47** | 24.88&nbsp;ms<br>1.61<br>5.8MB<br>**2.12** | 3.02&nbsp;ms<br>**0.20**<br>3.0MB<br>1.09 | 19.63&nbsp;ms<br>1.27<br>6.7MB<br>**2.47** | 16.29&nbsp;ms<br>1.05<br>2.7MB<br>1.01 | 12.72&nbsp;ms<br>0.82<br>2.1MB<br>0.77 | 271.03&nbsp;ms<br>**17.50**<br>219.2MB<br>**80.74** |
| UpsertBatch | 20,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 143.81&nbsp;ms<br>1.00<br>11.6MB<br>1.00 | 95.36&nbsp;ms<br>$\color{#16a34a}{0.66}$<br>4.3MB<br>$\color{#16a34a}{0.37}$ | 250.37&nbsp;ms<br>1.74<br>67.0MB<br>**5.78** | 267.79&nbsp;ms<br>1.86<br>57.4MB<br>**4.95** | 55.78&nbsp;ms<br>**0.39**<br>29.1MB<br>**2.51** | 231.10&nbsp;ms<br>1.61<br>67.0MB<br>**5.78** | 164.07&nbsp;ms<br>1.14<br>28.4MB<br>**2.45** | 195.67&nbsp;ms<br>1.36<br>21.2MB<br>1.83 | 4.01&nbsp;s<br>**27.90**<br>2360.7MB<br>**203.64** |

### 事务对照（仅 2000 档）

> 🏆 = 时延最快组 · 💾 = 内存最省组

#### SQLite

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;Dapper.AOT<br>💾&nbsp;ADO | 20.4&nbsp;µs<br>1.00<br>3.9KB<br>1.00 | 20.3&nbsp;µs<br>$\color{#6b7280}{1.00}$<br>4.4KB<br>$\color{#ea580c}{1.11}$ | 19.8&nbsp;µs<br>0.97<br>4.1KB<br>1.03 | 21.6&nbsp;µs<br>1.06<br>4.2KB<br>1.07 | 1.17&nbsp;ms<br>**57.30**<br>59.6KB<br>**15.12** | 21.4&nbsp;µs<br>1.05<br>4.7KB<br>1.20 | 33.9&nbsp;µs<br>1.67<br>7.0KB<br>1.78 | 1.11&nbsp;ms<br>**54.60**<br>20.9KB<br>**5.31** | 1.14&nbsp;ms<br>**55.99**<br>16.3KB<br>**4.15** |
| TxHundredInserts | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;PalORM | 195.0&nbsp;µs<br>1.00<br>137KB<br>1.00 | 188.9&nbsp;µs<br>$\color{#2563eb}{0.97}$<br>92.8KB<br>$\color{#16a34a}{0.68}$ | 438.0&nbsp;µs<br>**2.25**<br>280KB<br>**2.04** | 582.3&nbsp;µs<br>**2.99**<br>295KB<br>**2.16** | 1.99&nbsp;ms<br>**10.20**<br>932KB<br>**6.81** | 283.9&nbsp;µs<br>1.46<br>220KB<br>1.61 | 1.58&nbsp;ms<br>**8.09**<br>541KB<br>**3.95** | 1.95&nbsp;ms<br>**10.00**<br>1.6MB<br>**12.02** | 3.30&nbsp;ms<br>**16.93**<br>1.2MB<br>**8.75** |
| TxBulkInsert | 2,000 | 🏆&nbsp;PalORM<br>💾&nbsp;linq2db | 2.73&nbsp;ms<br>1.00<br>1.7MB<br>1.00 | 2.62&nbsp;ms<br>$\color{#2563eb}{0.96}$<br>1.7MB<br>$\color{#6b7280}{1.00}$ | 4.87&nbsp;ms<br>1.78<br>4.3MB<br>**2.48** | 16.31&nbsp;ms<br>**5.97**<br>2.6MB<br>1.54 | 23.17&nbsp;ms<br>**8.48**<br>17.3MB<br>**10.07** | 4.88&nbsp;ms<br>1.79<br>4.3MB<br>**2.48** | 7.80&nbsp;ms<br>**2.85**<br>3.5MB<br>**2.02** | 4.04&nbsp;ms<br>1.48<br>1.2MB<br>0.73 | 8.58&nbsp;ms<br>**3.14**<br>5.4MB<br>**3.16** |
| TxRollback | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 606.7&nbsp;µs<br>1.00<br>435KB<br>1.00 | 654.4&nbsp;µs<br>$\color{#d97706}{1.08}$<br>437KB<br>$\color{#d97706}{1.01}$ | 2.07&nbsp;ms<br>**3.40**<br>1.4MB<br>**3.24** | 2.72&nbsp;ms<br>**4.48**<br>2.0MB<br>**4.60** | 5.45&nbsp;ms<br>**8.98**<br>4.5MB<br>**10.58** | 2.27&nbsp;ms<br>**3.74**<br>1.6MB<br>**3.74** | 7.82&nbsp;ms<br>**12.88**<br>3.5MB<br>**8.16** | 5.57&nbsp;ms<br>**9.18**<br>9.6MB<br>**22.68** | 13.98&nbsp;ms<br>**23.04**<br>7.1MB<br>**16.63** |
| EnumInserts | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 134.8&nbsp;µs<br>1.00<br>59.1KB<br>1.00 | 155.9&nbsp;µs<br>$\color{#ea580c}{1.16}$<br>59.2KB<br>$\color{#6b7280}{1.00}$ | 351.7&nbsp;µs<br>**2.61**<br>190KB<br>**3.21** | 508.2&nbsp;µs<br>**3.77**<br>232KB<br>**3.92** | 1.86&nbsp;ms<br>**13.79**<br>756KB<br>**12.80** | 354.4&nbsp;µs<br>**2.63**<br>223KB<br>**3.77** | 1.31&nbsp;ms<br>**9.75**<br>497KB<br>**8.42** | 1.74&nbsp;ms<br>**12.94**<br>1.2MB<br>**21.22** | 2.71&nbsp;ms<br>**20.10**<br>940KB<br>**15.91** |

#### MySQL

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;RepoDb<br>💾&nbsp;Dapper.AOT | 4.71&nbsp;ms<br>1.00<br>21.8KB<br>1.00 | 4.93&nbsp;ms<br>$\color{#d97706}{1.05}$<br>24.1KB<br>$\color{#ea580c}{1.10}$ | 4.82&nbsp;ms<br>1.02<br>21.2KB<br>0.97 | 4.62&nbsp;ms<br>0.98<br>22.7KB<br>1.04 | 5.86&nbsp;ms<br>1.24<br>133KB<br>**6.11** | 4.76&nbsp;ms<br>1.01<br>21.9KB<br>1.00 | 4.99&nbsp;ms<br>1.06<br>28.4KB<br>1.30 | 4.72&nbsp;ms<br>1.00<br>53.2KB<br>**2.44** | 5.13&nbsp;ms<br>1.09<br>35.4KB<br>1.63 |
| TxHundredInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 28.30&nbsp;ms<br>1.00<br>371KB<br>1.00 | 27.93&nbsp;ms<br>$\color{#2563eb}{0.99}$<br>311KB<br>$\color{#65a30d}{0.84}$ | 27.51&nbsp;ms<br>0.97<br>462KB<br>1.24 | 30.48&nbsp;ms<br>1.08<br>623KB<br>1.68 | 10.43&nbsp;ms<br>**0.37**<br>1.1MB<br>**3.05** | 27.99&nbsp;ms<br>0.99<br>357KB<br>0.96 | 31.04&nbsp;ms<br>1.10<br>820KB<br>**2.21** | 34.97&nbsp;ms<br>1.24<br>2.5MB<br>**6.81** | 33.29&nbsp;ms<br>1.18<br>1.5MB<br>**4.07** |
| TxBulkInsert | 2,000 | 🏆&nbsp;RepoDb<br>💾&nbsp;RepoDb | 11.88&nbsp;ms<br>1.00<br>489KB<br>1.00 | 11.65&nbsp;ms<br>$\color{#2563eb}{0.98}$<br>490KB<br>$\color{#6b7280}{1.00}$ | 31.41&nbsp;ms<br>**2.64**<br>6.7MB<br>**13.94** | 10.97&nbsp;ms<br>0.92<br>488KB<br>1.00 | 89.27&nbsp;ms<br>**7.51**<br>20.0MB<br>**41.94** | 22.65&nbsp;ms<br>1.91<br>6.7MB<br>**13.93** | 21.66&nbsp;ms<br>1.82<br>3.4MB<br>**7.16** | 19.79&nbsp;ms<br>1.67<br>3.1MB<br>**6.53** | 41.63&nbsp;ms<br>**3.50**<br>6.9MB<br>**14.35** |
| TxRollback | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 100.18&nbsp;ms<br>1.00<br>1.3MB<br>1.00 | 105.48&nbsp;ms<br>$\color{#d97706}{1.05}$<br>1.4MB<br>$\color{#d97706}{1.09}$ | 109.57&nbsp;ms<br>1.09<br>2.2MB<br>1.71 | 112.66&nbsp;ms<br>1.12<br>3.6MB<br>**2.76** | 38.15&nbsp;ms<br>**0.38**<br>9.0MB<br>**6.91** | 116.51&nbsp;ms<br>1.16<br>2.4MB<br>1.89 | 129.54&nbsp;ms<br>1.29<br>4.9MB<br>**3.79** | 136.02&nbsp;ms<br>1.36<br>14.8MB<br>**11.46** | 126.38&nbsp;ms<br>1.26<br>8.5MB<br>**6.57** |
| EnumInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 25.08&nbsp;ms<br>1.00<br>265KB<br>1.00 | 26.81&nbsp;ms<br>$\color{#d97706}{1.07}$<br>291KB<br>$\color{#ea580c}{1.10}$ | 27.20&nbsp;ms<br>1.08<br>396KB<br>1.49 | 28.88&nbsp;ms<br>1.15<br>528KB<br>1.99 | 9.73&nbsp;ms<br>**0.39**<br>839KB<br>**3.16** | 26.09&nbsp;ms<br>1.04<br>424KB<br>1.60 | 31.71&nbsp;ms<br>1.26<br>779KB<br>**2.93** | 33.33&nbsp;ms<br>1.33<br>2.0MB<br>**7.71** | 34.37&nbsp;ms<br>1.37<br>1.2MB<br>**4.63** |

#### PostgreSQL

| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ADO | PalORM | Dapper.AOT | RepoDb | EF Core | Dapper | FreeSql | linq2db | SqlSugar |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| TxSingleInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;Dapper.AOT | 1.46&nbsp;ms<br>1.00<br>8.3KB<br>1.00 | 1.52&nbsp;ms<br>$\color{#d97706}{1.04}$<br>9.3KB<br>$\color{#ea580c}{1.12}$ | 1.53&nbsp;ms<br>1.05<br>7.5KB<br>0.90 | 1.50&nbsp;ms<br>1.03<br>8.7KB<br>1.04 | 1.93&nbsp;ms<br>1.33<br>57.3KB<br>**6.90** | 1.51&nbsp;ms<br>1.04<br>8.1KB<br>0.97 | 1.48&nbsp;ms<br>1.02<br>12.1KB<br>1.46 | 1.50&nbsp;ms<br>1.03<br>23.3KB<br>**2.81** | 1.58&nbsp;ms<br>1.08<br>19.9KB<br>**2.40** |
| TxHundredInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;PalORM | 20.62&nbsp;ms<br>1.00<br>314KB<br>1.00 | 20.55&nbsp;ms<br>$\color{#6b7280}{1.00}$<br>212KB<br>$\color{#16a34a}{0.68}$ | 20.65&nbsp;ms<br>1.00<br>310KB<br>0.99 | 21.21&nbsp;ms<br>1.03<br>430KB<br>1.37 | 3.47&nbsp;ms<br>**0.17**<br>875KB<br>**2.79** | 20.90&nbsp;ms<br>1.01<br>283KB<br>0.90 | 23.93&nbsp;ms<br>1.16<br>703KB<br>**2.24** | 23.13&nbsp;ms<br>1.12<br>1.8MB<br>**5.80** | 26.78&nbsp;ms<br>1.30<br>1.5MB<br>**4.84** |
| TxBulkInsert | 2,000 | 🏆&nbsp;ADO<br>💾&nbsp;ADO | 4.69&nbsp;ms<br>1.00<br>340KB<br>1.00 | 4.74&nbsp;ms<br>$\color{#d97706}{1.01}$<br>341KB<br>$\color{#6b7280}{1.00}$ | 15.30&nbsp;ms<br>**3.26**<br>6.7MB<br>**20.12** | 5.29&nbsp;ms<br>1.13<br>843KB<br>**2.48** | 41.93&nbsp;ms<br>**8.95**<br>15.8MB<br>**47.48** | 15.91&nbsp;ms<br>**3.39**<br>6.7MB<br>**20.12** | 21.57&nbsp;ms<br>**4.60**<br>3.4MB<br>**10.26** | 10.78&nbsp;ms<br>**2.30**<br>1.1MB<br>**3.46** | 26.51&nbsp;ms<br>**5.66**<br>7.4MB<br>**22.27** |
| TxRollback | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 100.58&nbsp;ms<br>1.00<br>928KB<br>1.00 | 105.03&nbsp;ms<br>$\color{#d97706}{1.04}$<br>1.0MB<br>$\color{#ea580c}{1.12}$ | 105.15&nbsp;ms<br>1.05<br>1.5MB<br>1.66 | 109.78&nbsp;ms<br>1.09<br>2.9MB<br>**3.17** | 13.81&nbsp;ms<br>**0.14**<br>4.1MB<br>**4.55** | 104.20&nbsp;ms<br>1.04<br>1.7MB<br>1.89 | 125.77&nbsp;ms<br>1.25<br>4.4MB<br>**4.89** | 114.19&nbsp;ms<br>1.14<br>10.5MB<br>**11.58** | 138.58&nbsp;ms<br>1.38<br>8.6MB<br>**9.44** |
| EnumInserts | 2,000 | 🏆&nbsp;EF Core<br>💾&nbsp;ADO | 21.42&nbsp;ms<br>1.00<br>166KB<br>1.00 | 18.99&nbsp;ms<br>$\color{#65a30d}{0.89}$<br>181KB<br>$\color{#d97706}{1.09}$ | 20.03&nbsp;ms<br>0.94<br>251KB<br>1.51 | 22.05&nbsp;ms<br>1.03<br>353KB<br>**2.12** | 3.16&nbsp;ms<br>**0.15**<br>657KB<br>**3.94** | 20.46&nbsp;ms<br>0.95<br>281KB<br>1.69 | 23.11&nbsp;ms<br>1.08<br>659KB<br>**3.96** | 20.20&nbsp;ms<br>0.94<br>1.4MB<br>**8.61** | 24.05&nbsp;ms<br>1.12<br>1.1MB<br>**6.99** |

### 复现

```bash
# 全量横评批（约 40 分钟，需本机 PG / MySQL 容器）
dotnet run --project tools/PalORM.PerfCli -c Release -- full
# 只跑横评批
dotnet run --project tools/PalORM.PerfCli -c Release -- crossbench
```

说明：9 组同批同轮，比值只在同一批内可比；跨机绝对值不可比。87 行完整明细（全部 17 操作、两档、三方言，含时延与内存百分比）见 [性能全量报告-2026-11-02.md](性能全量报告-2026-11-02.md)。PalORM 自身回归（微基准、并发、内存曲线、长稳）由 PerfHub 夹具单独度量，见 [bench/perfhub/README.md](../bench/perfhub/README.md)。

## 🚀 快速开始

> `.NET 11`（全部包单目标 `net11.0`）+ SDK `11.0.100-rc.2` 或更高，IDE 需支持 Roslyn 源生成器；Native AOT 发布另需约 4 GB 内存（ILC 编译期，非运行时）。

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

## 📄 许可证

[Apache-2.0](LICENSE)

v6.7.0 及以前的已发布版本仍为 AGPL-3.0-only；<br>
v6.8.0 及以后的新版本统一变更为 Apache-2.0 发布。
