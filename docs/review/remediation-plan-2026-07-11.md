# PalORM 严格 Native AOT 审计整改清单

> 基线提交：`56e3cbcaa01c1e70aaf59bb8b764e523ffd31835`
>
> 基线 SDK：`.NET SDK 11.0.100-preview.5.26302.115`
>
> 建立日期：2026-07-11
>
> 状态：实施中

## 完成标准

生产路径不得使用运行时反射物化、反射序列化、`dynamic`、`Expression.Compile()`、运行时泛型构造、程序集扫描或运行时代码生成。

Native AOT 能力只有在以下条件同时满足时才标记完成：

1. 干净检出能够由真实 Source Generator 生成代码；
2. `PublishAot=true` 且没有未批准的裁剪或 AOT 警告；
3. `JsonSerializerIsReflectionEnabledByDefault=false`；
4. 发布后的原生二进制实际运行；
5. CRUD、对象型 OwnedJson、乐观锁和多程序集注册路径均被执行。

仅编译或发布成功不等于 AOT 全链路通过。

## S1 基线

| 检查 | 结果 | 证据 |
|---|---|---|
| Git 工作树 | 干净 | `git status --short` 无输出 |
| Release 全量构建 | 0 错误，1,289 警告 | `dotnet build PalORM.slnx -c Release --no-incremental` |
| Core 测试 | 26/26 通过 | `PalORM.Core.Tests` |
| SourceGen 测试 | 12/12 通过 | `PalORM.SourceGen.Tests` |
| gate | 失败 | `scripts/gate-check.sh` exit 1 |
| stub check | 失败 | `scripts/stub-check.sh` exit 1 |
| action verifier | 失败 | `scripts/verify-action-items.sh` exit 1 |
| SQLite Native AOT | 旧审计记录为原生运行通过 | 尚未证明真实 SourceGen 与完整 CRUD |
| PostgreSQL/MySQL Native AOT | 仅发布，未运行 | 本轮改由 CI 临时服务容器验证 |

## 边界与阻断项

### AUD-001：版本控制中的数据库凭据

状态：**未处理，P0 阻断**。

用户选择本轮仅记录，不修改凭据脚本、不轮换数据库凭据、不改写 Git 历史。代码整改不能证明旧凭据已经失效，因此最终不得宣称安全事件闭环或总体完成。

任何报告、日志和提交均不得复制连接串或凭据值。

## 整改阶段

| 阶段 | 范围 | 审计映射 | 状态 |
|---|---|---|---|
| 0 | 真实任务账本与基线 | AUD-022、AUD-024 | 已完成 |
| 1 | 质量脚本与严格 AOT 门禁 | AUD-022、AUD-023 | 已完成，门禁准确报告现存阻断 |
| 2 | 编译期实体元数据与多程序集注册 | AUD-012、AUD-023 | 已实施；定向测试及两个独立模型程序集的 SQLite Native AOT 注册合并验证通过 |
| 3 | OwnedJson STJ Source Generation | AUD-013、AUD-023 | 已实施：raw string 不编码，对象显式 context，PALORM008-010 编译诊断，严格 STJ 定向测试通过 |
| 4 | Provider 契约与 SQL 结构安全 | AUD-014 至 AUD-017 | 已实施：结构名称引用、非法注释拒绝、批次校验、Provider Schema 命令、池配置和 PALORM011；PG/MySQL 真实 Schema 运行待 CI |
| 5 | 连接租约、读写路由、事务和取消 | AUD-004、AUD-005、AUD-020、AUD-021 | 已实施：执行时读连接租约、GridReader 所有权、重试连接释放、事务绑定、取消传播和清理异常保留；定向验证通过 |
| 6 | CRUD、并发、软删除和批量语义 | AUD-002、AUD-003、AUD-006、AUD-019 | 已实施：独立 Insert/Upsert 元数据、版本并发、软删除过滤与 PALORM014、事务化 Merge/Seed/Bulk；PG/MySQL 真实运行待 CI |
| 7 | QueryBuilder 单一 Clause 模型 | AUD-007 至 AUD-010 | 已实施：QueryClause 原子持有类别/SQL/参数，JOIN 与 QueryMultiple 统一 binder，SplitQuery 根查询语义和 ThenInclude 双键契约已明确 |
| 8 | 弹性、可观测性、Prepared、PG 重连 | AUD-011、AUD-018、提交评审项 | 已实施：会话级熔断、静态 Activity/Meter、参数绑定后 PrepareAsync、PG 新连接重连并重新 LISTEN；真实 PG 通知运行待 CI |
| 9 | 三 Provider Native AOT 运行矩阵 | AUD-012、AUD-013、AUD-023 | 实现与 CI 配置已完成：三工程使用真实 Analyzer 并移除手工 `.g.cs`；SQLite 完整 CRUD、OwnedJson、并发和跨程序集原生运行通过；NuGet consumer 原生运行通过；PG/MySQL 原生 publish 通过，服务容器运行待证据 |
| 10 | CI、零警告、许可证和三方一致 | AUD-022、AUD-024、评审基础设施项 | 实现与配置已完成，远端证据待产生：CI 已分离 build/unit/integration/gate/AOT；NuGet 元数据为 AGPL-3.0-only 并打包 README；全解决方案严格构建已从 1630 warning 收口为 0 warning / 0 error |

### NuGet 来源元数据

当前检出没有 Git remote 或 upstream，GitHub CLI 也未认证；原 `RepositoryUrl` 无本地配置或公开仓库证据支持，原 `PackageProjectUrl` 无法完成 DNS 解析。为避免发布无法验证的来源链接，中央包属性已移除这两个 URL 及对应 `RepositoryType`，保留已验证的 AGPL-3.0-only 许可证和包内 README。配置可公开访问的真实 remote 后，再恢复项目页和仓库元数据。

## 缺陷到验收证据

| ID | 根因级目标 | 必须新增或强化的验证 | 状态 |
|---|---|---|---|
| AUD-002 | Insert 与 Upsert 使用不同的源生成列和 binder | 已有 ID 保存后总行数仍为 1，原行被更新 | 已实施并通过 SQLite Save 定向测试；CrudMetadata 生成 InsertColumns/UpsertColumns 与独立 binder |
| AUD-003 | 版本列原子递增，0 行抛冲突异常 | 两份旧版本实体第二次更新抛 `ConcurrencyConflictException` | 已实施；PALORM012-013 约束模型，SQLite 成功与陈旧版本测试通过 |
| AUD-004 | 执行时打开读连接，所有终结路径释放租约 | fake ADO 覆盖成功、失败、取消和 GridReader 所有权 | 已实施；SQLite 执行时路由、ForWrite 回主连接、GridReader 释放和重复执行验证通过 |
| AUD-005 | 每次重试拥有并释放局部连接 | `MaxRetries=1` 恰好两次，首轮连接已释放 | 已实施；Lifecycle fake Provider 2/2 通过 |
| AUD-006 | Provider 方言软删除与统一默认过滤 | 三 Provider 删除后普通查询不可见，IgnoreFilters 可见 | 已实施 Provider 时间表达式、From/Get/GetAll/Count/Sum/Max/Min/Avg 默认过滤和 PALORM014；SQLite 行为通过，PG/MySQL 待 CI |
| AUD-007 | JOIN SQL 与参数原子添加 | 带插值值的 JOIN 实际执行成功 | 已实施并通过 SQLite：WHERE 后 JOIN 自动按语法顺序输出，JOIN 参数执行成功 |
| AUD-008 | QueryMultiple 统一 FormattableString binder | 插值参数执行成功，失败不泄漏 | 已实施统一复合格式 binder；普通插值、格式说明符和读连接租约测试通过 |
| AUD-009 | 单一 `QueryClause` 保存 kind、SQL 和参数 | Join/Include/CTE/Window 与 SplitQuery 组合无越界 | 已实施；SplitQuery 仅投影实际 SQL 参数，分页 COUNT 保留 JOIN/GROUP/HAVING |
| AUD-010 | ThenInclude 明确表达 JOIN 两端 | 生成 SQL 不含 `...` 且实际执行 | 双键重载已实施并生成完整 JOIN；旧单参数重载已弃用并明确失败 |
| AUD-011 | 会话级持久熔断状态 | 同一 DataSession 连续失败后第三次快速失败 | 已实施；仅重试 Provider 瞬时故障和内部 timeout，确定性异常立即失败；MaxRetries=N 精确 N+1 次、最终调用只计一次失败、外部取消不计失败、half-open 单探针、代际隔离在途成功、默认退避封顶 30 秒和会话跨调用测试通过 |
| AUD-012 | RegistryFragment 合并为单一不可变状态 | 同进程显式注册两个完整 fragment 后实体与 PropertyToColumn 均可同时查询；重复实体确定性失败且不覆盖原值 | 已实施；锁内构造完整状态后一次引用发布，公开注册表只读，列集合在注册边界防御性复制；两个独立模型程序集与宿主实体在 SQLite Native AOT 中同时 Insert/Get 通过 |
| AUD-013 | 只使用 `JsonTypeInfo<T>` | 关闭 STJ 反射后对象图原生往返；raw string 数据库原文不双编码 | 已实施并通过 SourceGen/Core/SQLite 定向测试 |
| AUD-014 | 标识符、注释、保存点和 Raw 片段分型处理 | 恶意结构输入在发送命令前失败 | 已实施并通过 Core/SQLite 定向测试；窗口原始片段改为 `UnsafeWindowOver` |
| AUD-015 | 三 Provider 统一拒绝非正批次 | 数据库访问前抛 `ArgumentOutOfRangeException` | 已实施并通过三 Provider 单元测试与 SQLite 定向测试 |
| AUD-016 | Provider 配置 Schema 命令和列读取 | PG/MySQL Schema 校验零误报 | Provider 命令、参数和列名序号已实施；限定表名由 PALORM011 编译期拒绝；PG/MySQL 真实运行待 CI |
| AUD-017 | Provider 真实应用池配置 | 连接串 builder 行为测试 | PG/MySQL 已应用并通过 builder 测试；SQLite 不支持项明确失败 |
| AUD-018 | 真实 Activity、Meter 和 PrepareAsync | Listener 与 fake command 捕获真实事件 | 已实施；ActivityListener/MeterListener 捕获低基数脱敏标签及三种 outcome，分页 COUNT 与 SELECT 分别计命令，QueryMultiple 由 GridReader 完成生命周期且未注册类型记录 error；fake DbCommand 验证参数绑定后 Prepare 和取消传播；PG fake 连接验证启动恢复、取消、重连重新 LISTEN、正常停止不触发 OnError、后台故障 OnError、null payload 类型和订阅者隔离 |
| AUD-019 | Merge 使用 Upsert，Seed 要求稳定键 | 重复执行行数不变且值可更新 | 已实施并通过 SQLite 幂等测试；BulkInsert/Merge/Update/Delete 复用外部事务，自有事务统一清理 |
| AUD-020 | 取消传播，清理 token 独立 | OCE 不被吞，回滚失败不覆盖主异常 | 已实施；CreateAsync/HealthCheck 取消传播，回滚统一使用独立 token 并附加清理异常 |
| AUD-021 | 清理采用 finally/聚合错误 | 拦截器失败后连接仍释放 | 已实施；会话、命令、reader、租约和拦截器按所有权释放，主异常不被清理异常覆盖 |
| AUD-022 | 门禁真实传播结果 | 故障夹具失败、移除后恢复，统计准确 | CI 已接入质量脚本夹具与真实 gate；G12 隔离 Git 夹具完成 clean → 1 项违规精确失败 → recover，真实 gate 为 22 通过、0 警告、1 失败，并如实保留 AUD-001/G9 阻断 |
| AUD-023 | AOT 项目真实引用 SourceGen | 三 Provider 原生 CRUD 矩阵与包消费 smoke | 实现与配置已完成；G23 已通过，三工程真实 Analyzer + warning-as-error 构建和 Native AOT publish 均通过；SQLite 完整原生运行与独立 NuGet consumer 原生运行通过；PG/MySQL 原生 CRUD、OwnedJson、并发运行待 CI 服务容器证据 |
| AUD-024 | 文档、元数据、注释与行为一致 | 旧数字、旧路径、错误许可证零残留 | 已实施：许可证与包 README 已同步；旧阶段、占位命令、错误许可证和旧 API 口径扫描零残留 |

## 评审增量项

以下项目并入对应阶段，不单独重复修复：

- `PgNotificationListener` 的注释和行为统一为真实重连、重新 LISTEN；
- PostgreSQL 批量注释不得把单批次称为全部行；
- 不得声称复用参数对象，除非实现确实复用；
- 删除没有仓库 benchmark 支撑的固定性能倍数；
- `RETURNING` 注释必须与实际投影列一致；
- BuildSql 的 SplitQuery 注释必须包含真实条件；
- Prepared 注释必须描述 `PrepareAsync`，不能描述为字符串缓存。

## 提交和进度规则

- 每个阶段独立提交，提交前运行该阶段最相关测试、stub scan 和门禁。
- 公共 API、行为、配置、计数或命令发生变化时，同一提交同步 README、docs、XML 和行内注释。
- 失败验证不标完成；外部 CI 未实际运行时标记“已配置，待验证”。
- AUD-001 未完成期间，整体进度上限不能写为 100%。
