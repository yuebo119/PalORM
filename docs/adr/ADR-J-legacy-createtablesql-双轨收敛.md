# ADR-J：legacy CreateTableSql 双轨收敛

> 状态：已裁决（2026-09-02 架构评审第二批，随本批次实施）
> 关联：src/PalORM.SourceGen/MigrationEmitter.cs · RegistryEmitter.cs · DataSession.Schema.cs MigrateAsync · PalORM_Runtime.RegistryFragment
> 先例：ADR-I（legacy CommandSqls 移除，2026-09-02 修订提前实施）——本 ADR 是其同型扩展

## 背景

RegistryEmitter 同时生成 legacy `CreateTableSql`（单方言、SQLite 风格双引号、列型走
`DbTypeName` 原值）与 `CreateTableSqlByDialect`（三方言、含 MySQL 精度/索引列型等差异
映射）双份 DDL。运行时 `MigrateAsync` 自 ITM-569 起**拒绝回退 legacy DDL**（SQLite 风格
双引号在 MySQL 上报语法错而非清晰的"请重新编译"）——legacy 集合的执行路径已死，仅余
两个残余用途：① 作为 `MigrateAsync` 的实体枚举键源；② `RegistryFragment` 的 required
注册载荷（无消费）。

## 决策

**legacy CreateTableSql 生成段随本批次移除**（复用 ADR-I 修订的处置模板）：

1. 生成器不再发射 legacy `CreateTableSql` 字典与 `Migration_X.CreateTable` 常量；
   `BuildCreateTable(TableModel)` 无方言重载删除，方言参数必填——方言 DDL 是唯一真源。
2. `RegistryFragment.CreateTableSql` 由 required 放宽为可选（默认空集）——旧版本生成器
   片段仍可注册（载荷保留、运行时继续不执行）；键校验由 ValidateRequiredKeys 放宽为
   ValidateOptionalKeys。`PalORM_Runtime.CreateTableSql` 公共读面保留并标注 legacy。
3. `MigrateAsync` 实体枚举源由 `CreateTableSql.Keys` 改为 `TableNames.Keys`
   （片段实体全集的校验锚），执行仍只走 `CreateTableSqlByDialect` / `CreateIndexSqlByDialect`，
   缺方言键的 "recompile" 错误保持不变。

## 理由

- 与 CommandSqls 完全同构的"死数据 + 键集代理"形态——ITM-569 已在运行时拒绝执行，
  集合继续存在的唯一作用是误导（XML doc 需要专门解释"不要消费它"）。
- ADR-I 修订（2026-09-02）已确立处置先例：v5.x minor 内消化 + CHANGELOG 注明 +
  可选化兼容旧片段。本项目同一 [未发布] 批次内保持口径一致，避免"SQL 收敛了、DDL
  还留着"的半完成状态。
- 混合场景影响与 ADR-I 修订节分析相同："旧运行时 + 重编译模型程序集"在注册期
  响亮失败（键集校验），触发时点更早且用户处于主动升级动作中。

## 后果

- 生成物体积下降（每实体一份单方言建表 DDL）。
- 本批次移除含快照 diff 审阅（净删除）+ SourceGen/Core 测试全绿；包契约测试
  （test-package-contract.sh）与 NuGet 包消费者 AOT 冒烟同步补入 SqlFile
  AdditionalFiles 用例，覆盖新增的包分发契约。
