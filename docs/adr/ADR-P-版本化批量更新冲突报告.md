# ADR-P：版本化批量更新的单语句路由与冲突报告

> 状态：**已采纳（2026-10-20 用户裁决六项推荐）** · 来源：S6 版本化批量更新探针 + 冲突报告设计评审
> 关联：`BulkUpdateAsync` 自动路由 · `BatchUpdateSqlBuilder` · `VersionedBulkUpdateProbe`

## 背景

`[ConcurrencyCheck]` 实体的 `BulkUpdateAsync` 此前恒走逐条路径（每行一条带
`WHERE pk = @p AND version = @v` 的 UPDATE，PG/MySQL 按 100 命令一个 DbBatch 打包）。
2026-10-20 探针（`--versioned-update`，真库 15 轮中位）实测单语句形态的上限差：

| 行数 | PG（产品 → 单语句） | MySQL（产品 → 单语句） |
|---|---|---|
| 1000 | 18.6ms → 6.07ms（3.06×） | 52.4ms → 11.1ms（4.73×） |
| 5000 | 79.4ms → 18.6ms（4.26×） | 204.7ms → 31.9ms（6.42×） |

单语句形态需要回答冲突归属：PG 可用 `RETURNING` 精确拿到已更新集合；MySQL 没有
`RETURNING`，只有受影响行数，且 MySqlConnector 默认 `UseAffectedRows=false` 上报
found rows（匹配行），不是 changed rows。版本列恒递增使两者对本操作等价。

现行契约本身是计数级：100 命令 DbBatch 路径只报“第几批、多少行匹配”，不提供行级归属。
因此单语句路由不必以行级归属为前置条件。

## 选项

| 选项 | 冲突报告能力 | 快乐路径成本 | 代价 |
|---|---|---|---|
| A 计数级（采纳） | 受影响数 < 输入数即冲突，抛 `ConcurrencyConflictException` 并整批回滚 | 零额外往返 | 不告诉调用方具体哪些键冲突 |
| B PG `RETURNING` + MySQL 计数 | PG 可精确得到已更新键集合，MySQL 仍只有计数 | PG 每次多一个 reader 与 N 个主键值 | 两方言能力分叉；主键类型读取与多重集比较 |
| C 两方言行级归属 | PG `RETURNING`；MySQL 更新前 `SELECT ... FOR UPDATE` | MySQL 每次多一次往返并持锁 | 收益缩水、锁面扩大；MySQL 后置诊断在并发竞态下无法精确归因 |
| D 只做检测不做归属 | 同 A | 零 | 与 A 等价，A 已含检测 |

## 决策

1. **v1 采用计数级契约**：受影响数等于本批输入数即成功；小于抛
   `ConcurrencyConflictException`，大于抛 `InvalidOperationException`。异常类型与消息
   前缀保持与逐条 DbBatch 路径一致。不新增冲突键列表。
2. **v1 不使用 PG `RETURNING`**，MySQL 不依赖 `UseAffectedRows` 选项的“changed rows”
   语义。SQL 形态保证 version 在 SET 中递增；MySqlConnector 默认与 `UseAffectedRows=true`
   两档都测。
3. **重复主键按冲突处理**：PG `UPDATE ... FROM` 与 MySQL 多表 UPDATE 对同一目标行只更新
   一次（MySQL 8.4 真库 `ROW_COUNT()=1` 复核），受影响数小于输入数，落同一异常类型。
4. **不扩展显式 `BulkUpdateBatchAsync`**：该 API 对 PG/MySQL 版本化实体继续抛
   `NotSupportedException`；版本化能力只经 `BulkUpdateAsync` 的隐式路由提供。
5. **支持范围**：PG UNNEST 数组形态与 MySQL 8.0.19+ JoinValuesRow；生成物缺少并发令牌
   绑定能力、SQLite、低版本 MySQL 一律回退逐条。版本列由 PALORM012 限定非空 int/long，
   因此不需要运行期类型分支。
6. **行级归属留到 v2 可选诊断能力**：触发条件为调用方无法在冲突后自行查询、冲突路径
   成本成为瓶颈、或需要一等冲突键观测。届时的实现形态是显式诊断 API，不是默认热路径。

## 理由

- 现行契约没有行级归属，v1 保持计数级不构成能力回退；探针的 3.06× / 4.73× 正是计数
  路径的实测值，与行级归属正交。
- 调用方在捕获异常后可用一条 `SELECT pk, version FROM t WHERE pk IN (...)` 自行定位
  冲突集合（版本不同或行缺失），只在失败路径付一次查询，无需库内 API 与方言分叉。
- MySQL 没有 `RETURNING`，任何行级方案都要求在热路径多一次查询或持锁；这会抵消单语句
  收益并引入锁竞争面。
- 重复主键、缺失行、租户不匹配在计数级都会落到同一异常，语义与逐条路径一致。

## 后果

- 快乐路径：PG 产品 18.6ms → 7.09ms（1.60MB → 30KB），MySQL 52.4ms → 19.0ms
  （1.84MB → 1.0MB），1000 行实测。产品与单语句地板的差距来自参数池创建与冲突校验。
- 冲突路径：异常与整批回滚语义不变；调用方需要行级定位时按上述查询自行完成。
- 维护面：生成物新增并发列名、版本值参数绑定、PG 版本数组三件套；注册片段为可选键，
  旧模型程序集缺失时回退逐条。`PrepareBatchUpdateContext` 的 `BindUpdate` 参数哨兵按
  并发令牌 +1 修正。
- 文档：`BulkUpdateBatchAsync` 的拒绝消息与 README 的“批量更新走 DbBatch”表述同步改为
  自动路由；`--versioned-update` 探针从“决策量具”转为“产品路径与地板距离”的观测。

## 被否方案

- **PG `RETURNING` 进入 v1**：只解决 PG 的归属，MySQL 无等价物；热路径多读 N 个主键值。
  补测成本前不作为默认路径。
- **MySQL 更新前 `SELECT ... FOR UPDATE`**：精确但每次多一次往返与锁；并发写场景锁面
  扩大。若 v2 需要精确 MySQL 归属，先做成本探针再定。
- **冲突键列表进入异常**：`ConcurrencyConflictException` 只有消息构造器；新增负载是 API
  面扩张，且无法在两方言间对称实现。
- **扩展 `BulkUpdateBatchAsync`**：显式 API 的契约是不承载逐行版本谓词；保持拒绝，版本化
  能力收敛到隐式路由。

## 验证锚

- 探针：`bench/PalORM.Benchmarks --versioned-update pg|mysql 1000`，信封
  `bench/results/benchmarks-20261020-093154.json` / `-093155.json`。
- 集成：`PgBatchedUpdatePathTests` / `MySqlBatchedUpdatePathTests`（跨批、冲突、重复键、
  缺失行、延迟回填、`UseAffectedRows` 两档、单行回退）；`VersionedBulkUpdateBusinessShapeTests`
  以订单行宽实体在 1000/5000 行上验证外部并发写入、调用方诊断合并重试、重复键去重重试。
- 单元：`BatchUpdateSqlBuilderTests` 版本化 SQL 快照；SourceGen 快照新增
  `FullFeatureEntity.Version` 覆盖生成方法。

## 裁决记录

| 日期 | 裁决 | 依据 |
|---|---|---|
| 2026-10-20 | 计数级 v1、无 RETURNING、重复键按冲突、不扩展显式 API、PG UNNEST + MySQL JoinValuesRow、落 ADR-P（六项均为推荐项） | 用户逐项确认；探针上限差与现行计数级契约 |
