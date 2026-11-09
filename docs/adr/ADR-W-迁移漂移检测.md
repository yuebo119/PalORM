# ADR-W：迁移漂移检测的默认开启与批量查询形态

- 状态：已采纳（2026-11-06）
- 适用范围：`src/PalORM.Core/DataSession.Schema.cs`、`src/PalORM.Core/DbOptions.cs`

## 背景

`MigrateAsync` 是幂等新建哲学：只执行 `CREATE TABLE IF NOT EXISTS` 与 `CREATE INDEX`，不做增量 ALTER
（`docs/功能特性.md` §14 已登记为能力边界）。由此产生一个静默失败面：

实体新增一个属性后调用 `MigrateAsync`，返回的 `MigrationResult.Tables` 正常、无异常、无失败项，
但新列并不存在。错误要到第一次 `InsertAsync` 才以驱动错误的形式暴露，且现场与根因相隔一次部署。

`ValidateSchemaAsync<T>` 能查这个问题，但它是显式调用的单实体入口，不是迁移的自然组成部分。

## 决策

1. `MigrationResult` 增加 `SchemaDrift` 属性（`init`，默认空集合）；
2. `DbOptions` 增加 `ValidateMigrationDrift`，**默认 true**；
3. 检测形态按方言分层：PostgreSQL/MySQL 一次 `information_schema` 查询取回当前 schema 全部表列，
   在内存里按注册表筛选；SQLite 逐表 `PRAGMA table_xinfo`（无法跨表聚合，但本地库与内存库没有网络往返）；
4. 检出漂移时写 `SchemaDrift`，并调用 `_logger.LogWarning`（默认会话是 NullLogger，零分配静默）；
5. 同期修复 SQLite 的 `ConfigureSchemaCommand`：`PRAGMA table_info` 改为 `table_xinfo`。

## 为什么默认开启

初版曾定为默认关闭，理由是"逐表列查询是 N 次往返，而迁移常在启动路径"。改判的依据是成本被
形态变更抹掉了：

| 形态 | PostgreSQL/MySQL 往返 | SQLite 往返 | 是否值得默认开 |
|---|---|---|---|
| 逐表查询（初版） | N 次（50 表约 50~150ms） | N 次（本地，约 5~15ms） | 否 |
| 一次 information_schema（本版） | 1 次（约 1~3ms） | N 次（本地，无网络成本） | 是 |

一次往返对启动路径可忽略，而"迁移返回成功但 schema 没跟上"是高代价静默失败。默认关闭时的
真实风险是：没有人会记得打开开关，缺陷继续在生产写入时才暴露。

默认开启同时配合日志：只填结果对象、无人读取，等于白付一次往返。检出时写 Warning，让配置了
LoggerFactory 的部署在默认路径上就有声音。

## 为什么批量查询不走 IDbProvider 契约

`ConfigureSchemaCommand` 是"按表取列名"的 Provider 方法，天然逐表。批量形态需要"一次取多表列"，
若加进 `IDbProvider`（`static abstract` 接口）会让所有实现者承担新成员，属于破坏性契约变更。

因此批量查询写在 `DataSession` 内，按 `SqlDialect` 分支（PostgreSQL 用 `current_schema()`，
MySQL 用 `DATABASE()`）。两条 SQL 都是常量、零参数，表名在内存里与注册表比对，注入面为零。
G31 方言感知门禁覆盖该分支。

SQLite 仍复用 `ConfigureSchemaCommand`，保留单一真源；这也是为什么 `table_xinfo` 修复放在 Provider 层。

## SQLite 的 `table_info` → `table_xinfo` 修复

实施首个测试就检出：迁移刚建好的库被报告两条漂移，两条都落在 `[Computed]` 列上。

根因是 `PRAGMA table_info` **不返回 GENERATED 列**。`[Computed]` 映射为
`GENERATED ALWAYS AS (...) STORED`，因此列永远"查不到"。

这个缺陷不止影响新特性：`ValidateSchemaAsync` 走同一个 Provider 方法，对含计算列的实体同样误报
缺失列，只是既有测试只有一条不含计算列的正向用例，从未覆盖。

改为 `PRAGMA table_xinfo` 后，返回列集包含 hidden 与 generated 列；普通表（本项目不生成虚拟表）
的列集与 `table_info` 逐字一致，返回的列名序号仍为 1。最低引擎 3.26。

## 后果

- 迁移返回对象首次能表达"迁移成功但 schema 未跟上"，错误从运行期提前到调用点；
- 默认路径成本为 PostgreSQL/MySQL 一次往返、SQLite N 次本地 PRAGMA；
- 检出漂移不抛异常，只写结果与日志：迁移的成功/失败语义不变，避免把"配置漂移"升级为启动失败；
- `ValidateSchemaAsync` 对计算列的既有误报一并修复；
- `MigrationResult` 与 `DbOptions` 各新增一个成员，均向后兼容（SemVer MINOR）；
- 未解决：只检列集合，不检列类型、多余列与索引缺失，留作后续议题。

## 权限要求（2026-11-08 补）

PostgreSQL/MySQL 的 `information_schema.COLUMNS` 只返回当前数据库用户**有权限**读到的列
（MySQL 官方文档明确此行为；PostgreSQL 同样按当前用户可访问性过滤）。最小权限部署里，
若迁移账号不是表所有者，查询结果会缺列，被漂移检测判为"列不存在"而误报。

处置：

- 部署时迁移账号需具备 `information_schema` 读权限（或表所有者）；
- 权限不足时检出项不可信，可显式关闭 `DbOptions.ValidateMigrationDrift` 规避；
- 读失败（权限/连接异常）已按 ADR-W 语义记 `Validation failed` 而不抛异常（H1，2026-11-08）。

## 修订记录

- 初稿（同日）：默认关闭 + 逐表查询，理由是避免启动路径 N 次往返；
- 修订（同日，用户要求按最优方案处理）：改为默认开启 + 一次 `information_schema` 查询。
  改判依据是往返次数从 N 降到 1，初稿的成本前提不再成立；同时补日志出口，
  解决"默认开启但无人读取"的空转风险。
