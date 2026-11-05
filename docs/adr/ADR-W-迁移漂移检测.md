# ADR-W：迁移漂移检测的默认关闭与复用性取舍

- 状态：已采纳（2026-11-06）
- 适用范围：`src/PalORM.Core/DataSession.Schema.cs`、`src/PalORM.Core/DbOptions.cs`、`src/PalORM.Core/DataSession.Schema.cs` 的 `MigrationResult`

## 背景

`MigrateAsync` 是幂等新建哲学：只执行 `CREATE TABLE IF NOT EXISTS` 与 `CREATE INDEX`，不做增量 ALTER
（`docs/功能特性.md` §14 已登记为能力边界）。由此产生一个静默失败面：

实体新增一个属性后调用 `MigrateAsync`，返回的 `MigrationResult.Tables` 正常、无异常、无失败项，
但新列并不存在。错误要到第一次 `InsertAsync` 才以驱动错误的形式暴露，且现场与根因相隔一次部署。

`ValidateSchemaAsync<T>` 能查这个问题，但它是显式调用的单实体入口，不是迁移的自然组成部分。
调用方要在迁移之后额外记住调它，属于"文档约定"而非"机制保证"。

## 决策

1. `MigrationResult` 增加 `SchemaDrift` 属性（`init`，默认空集合），承载"实体声明了而库内不存在"的列；
2. `DbOptions` 增加 `ValidateMigrationDrift` 开关，**默认 false**；
3. 开启时 `MigrateWithResultAsync` 在 ANALYZE 之后逐表比对，检出即写入 `SchemaDrift`；
4. 检测复用 `IDbProvider.ConfigureSchemaCommand`，不新增 Provider 契约，也不新写方言分支；
5. 同期修复 SQLite 的 `ConfigureSchemaCommand`：`PRAGMA table_info` 改为 `table_xinfo`。

## 为什么默认关闭

迁移常见于应用启动路径。逐表列查询是 N 次往返：50 表在本地 SQLite 约 5ms、本地 PG 约 50ms、
远程 PG 可到 100ms 以上。把这个成本放进默认启动路径，会让"迁移"从一次 DDL 批量变成一次 schema 巡检。

需要"迁移即校验"的部署显式打开；只需一次性检查的场景直接调 `ValidateSchemaAsync`，两者语义同源。

## 为什么不用批量 information_schema 查询

方案阶段考虑过 PG/MySQL 各发一次 `information_schema.columns WHERE table_name = ANY(...)`、
SQLite 逐表 `PRAGMA` 的分层形态。放弃的理由：

- `ConfigureSchemaCommand` 已在三方言就绪，表名在 MySQL/SQLite 走标识符转义、在 PG 走参数，
  注入面为零；批量形态需要另写一套表名参数化逻辑；
- 批量形态要为 PG/MySQL/SQLite 各写一条 SQL 与一个读取分支，约 80 行，且与 `ValidateSchemaAsync`
  形成第二套 schema 查询真源；
- 开关默认关闭，N 次往返不是默认成本。

若未来出现表数量级跃升（例如 200 表以上且迁移在每次启动执行），再评估批量形态。
重开触发条件：单次 `MigrateAsync` 的漂移检测实际耗时超过 200ms 且该调用在启动路径。

## SQLite 的 `table_info` → `table_xinfo` 修复

实施首个测试就检出：迁移刚建好的库被报告两条漂移，两条都落在 `[Computed]` 列上。

根因是 `PRAGMA table_info` **不返回 GENERATED 列**。`[Computed]` 映射为
`GENERATED ALWAYS AS (...) STORED`，因此列永远"查不到"。

这个缺陷不止影响新开关：`ValidateSchemaAsync` 走同一个 Provider 方法，对含计算列的实体同样会
误报缺失列，只是既有测试只有一条不含计算列的正向用例，从未覆盖。

改为 `PRAGMA table_xinfo` 后，返回列集包含 hidden 与 generated 列；普通表（本项目不生成虚拟表）
的列集与 `table_info` 逐字一致，返回的列名序号仍为 1。最低引擎版本 3.26。

## 后果

- 迁移返回对象首次能表达"迁移成功但 schema 未跟上"，错误从运行期提前到调用点；
- 默认路径零成本，开关关闭时不构造漂移清单；
- `ValidateSchemaAsync` 对计算列的既有误报一并修复；
- `MigrationResult` 与 `DbOptions` 各新增一个成员，均向后兼容（SemVer MINOR）；
- 未解决：只检列集合，不检列类型、多余列与索引缺失。这三项需要独立的类型比对机制，
  留作后续议题，不在本 ADR 范围内。
