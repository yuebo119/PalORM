# ADR-B：Index/FK 迁移 DDL 支持范围

> 状态：已实施（2026-07-17 批准并实施）· 来源：评审 ITM-104/151（GEN-03）

## 背景

`[Index]`/`[Unique]`/`[ForeignKey]`/`[DefaultValue]`/`[Column(Length/Precision/Scale/TypeName/StoreAs)]` 注解可声明但不参与迁移 DDL：
- MigrationEmitter 曾生成 `CreateIndex_*`/`FK_*` 常量但 `MigrateAsync` 从不执行（零消费者死代码，已删除）；
- FK DDL 用 `ALTER TABLE ADD CONSTRAINT`——SQLite 不支持该语法（FK 必须内联在 CREATE TABLE）；
- FK 列名取属性名而非 `[Column]` 映射名（错列引用）；
- TableModel 对 Length/Precision/Scale/StoreAs 传 null 占位，Indexes 恒空。

## 现状（2026-07-17 已实施的缓解）

新增 **PALORM017** 编译期警告：标注上述注解即告知"不参与 DDL 生成"，静默失效改为编译期显式。文档 M4/M6/BA11 已同步标注。

## 选项

| 选项 | 范围 | 工作量 | 风险 |
|------|------|--------|------|
| B1 完整实现 | Index/Unique DDL 三方言 + FK（SQLite 内联进 CREATE TABLE、PG/MySQL 走 ALTER）+ Length→VARCHAR(n) 等类型细化 | 大（TableModel 解析补全 + 三方言 Emitter + MigrateAsync 执行顺序 + 组合测试） | SQLite FK 内联意味着 CREATE TABLE 生成逻辑重构；已建表的增量迁移不支持会产生新的"静默不生效"面 |
| B2 仅实现 Index/Unique | 索引 DDL 三方言语法差异小（CREATE [UNIQUE] INDEX IF NOT EXISTS 通用） | 中 | FK/DefaultValue/Length 保持 PALORM017 告警 |
| B3 移除注解 | 删除上述注解，用户自管 DDL | 小 | binary-breaking（3.0）；损失声明式表达 |
| B4 维持现状 | PALORM017 告警 + 注解保留为元数据 | 零 | 注解语义"仅文档化"需长期维持一致 |

## 推荐

**B2**：索引是最高频需求且三方言实现代价低；FK 的 SQLite 内联重构与增量迁移问题留待迁移系统整体设计（当前 MigrateAsync 本就只支持 CREATE IF NOT EXISTS 级别）。实施后 PALORM017 对 `[Index]`/`[Unique]` 停报，其余注解维持告警。

## 待用户决策

- 采纳 B2 还是 B1/B3/B4？
- 若 B2：`[Unique]`（属性级）是否自动升为单列唯一索引？


## 实施记录（2026-07-17 · 已实施）

按用户批准的推荐方案 **B2 先实现 Index/Unique，FK 留待迁移系统整体设计** 落地：

- `TableModel` 解析 `[Index("name", cols…, Unique=…)]`（类级复合索引）与 `[Unique]`（属性级，升为 `ux_<table>_<column>` 单列唯一索引）。
- `MigrationEmitter.BuildCreateIndex` 生成三方言 DDL：SQLite/PG 带 `IF NOT EXISTS`；MySQL 不支持该语法，幂等由运行时 `IDbProvider.IsDuplicateSchemaObject`（MySqlProvider 识别 1061 DuplicateKeyName）兜底跳过。
- 注册链新增 `CreateIndexSqlSet` / `PalORM_Runtime.CreateIndexSqlByDialect`（可选键）；`MigrateAsync` 建表后按 Provider 方言执行索引 DDL。
- PALORM017 对 `[Index]`/`[Unique]` 停报（已参与 DDL）；`[ForeignKey]`/`[DefaultValue]`/`[Column]` 架构参数继续告警。
- 测试：生成端 2 用例（三方言 DDL 形状、无索引实体空数组且不入注册字典）+ SQLite 集成 3 用例（真实建索引、二次迁移幂等、唯一索引数据库强制生效）。

## 实施记录二（2026-09-30 · FK 挂起解除，用户指令"未完成任务完整实现"）

FK DDL 落地，方案对齐 `MigrateAsync` 既有的幂等新建哲学（`CREATE TABLE IF NOT EXISTS`）：

- **生成**：`MigrationEmitter.BuildCreateTable` 追加表级 `FOREIGN KEY ("col") REFERENCES "reftable" ("refcol") ON DELETE <action>` 子句——三方言同构（PG/MySQL 的 CREATE TABLE 原生支持表级 FK，无需 ALTER；当初 B1 设想的"PG/MySQL 走 ALTER"只在增量补齐已建表时才需要，而增量变更本就属迁移系统整体设计范围）。
- **OnDelete 映射**：NoAction/Cascade/SetNull/Restrict → NO ACTION/CASCADE/SET NULL/RESTRICT（默认 NO ACTION，G16 级联显式启用语义；PALORM004 已强制 OnDelete 必填）。
- **列名解析**：FK 属性经 `[Column]` 映射名进 DDL（错列引用源头消除）；引用表/列已过 PALORM043 控制字符扫描与 PALORM003 存在性校验。
- **限制口径（文档明示）**：① 已建表不补齐 FK（与"已建表不加列"同限制）；② SQLite 引擎强制需连接级 `PRAGMA foreign_keys=ON`（引擎默认 OFF，声明存在但不强制）；③ **引用表/列名是字面 DDL 契约**——实测教训：PG 引号标识符大小写敏感，引用列写 `"id"` 而被引用实体实际列名为 `"Id"` 时报 42703，且 Integration 全实体共享 Registry 使一处坏 FK 炸掉全部 `MigrateAsync`；引用列编译期对齐校验（PALORM003 扩展到列级）登记为诊断候选。
- 测试：快照基线 `FkParentEntity`/`FkChildEntity` 三方言形态（含 CASCADE/RESTRICT/SET NULL 三形态）+ Integration 5 用例（SQLite PRAGMA 开启后孤儿拒绝、CASCADE 真传播、二次迁移幂等、PG/MySQL 真库强制）。
