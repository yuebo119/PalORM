# ADR-V：迁移原子性与方言差异

- 状态：已实施（2026-10-31）
- 适用范围：`src/PalORM.Core/DataSession.Schema.cs`、`MigrateWithResultAsync`、`MigrateAsync`

## 背景

原 `MigrateAsync` 逐条执行建表与索引 DDL。中途失败会留下半成品 schema，调用方只能通过异常
判断失败，拿不到已建表、已跳过索引与失败语句的结构化信息。并发迁移还存在建表竞态：
`CREATE TABLE IF NOT EXISTS` 的存在性检查与系统目录插入不是原子操作，后到者可能撞重名对象。

## 决策

按架构评审 D2 推荐 A 实施：

1. 新增 `MigrateWithResultAsync`，返回 `MigrationResult`（表、已建索引、跳过索引、失败索引、
   是否事务、是否回滚）；
2. `MigrateAsync` 保留原签名，内部调用新方法并丢弃结果；
3. PostgreSQL 与 SQLite 在单事务内执行建表与索引，失败回滚，不留半成品；
4. MySQL 不支持事务 DDL，逐条执行并记录失败，结果对象明确标记 `Transactional=false`；
5. 事务内遇到并发建表重名异常时，用保存点回滚到语句前再继续，避免 PostgreSQL 事务进入
   aborted 态（25P02）。索引 DDL 同样按条套保存点；
6. 重名索引按幂等跳过并记入 `SkippedIndexes`，非重名失败记入 `FailedIndexes` 后照常抛出。

`MigrationResult` 同时写入 `LastMigrationResult`，异常路径写入 `exception.Data["PalORM.MigrationResult"]`，
便于调用方在 catch 中读取部分结果。

## 后果

- PG/SQLite 迁移失败不再留下半成品 schema，但 DDL 事务会持锁到提交，长迁移需评估锁等待。
- MySQL 仍可能留下部分对象，调用方必须检查 `MigrationResult.FailedIndexes` 与 `RolledBack`。
- 保存点只用于并发重名竞态，正常路径仍是单批往返，不增加额外往返。
- 旧 `MigrateAsync` 行为兼容；需要结构化结果的新代码改用 `MigrateWithResultAsync`。