# ADR-G：MySQL AllowLoadLocalInfile 安全策略

> 状态：已修订（2026-08-15 裁决；2026-11-24 按 PROV-001 落地"显式意图优先"，评审 ITM-612/EVAL-1）
> 关联：MySqlProvider.CreateConnection · MySqlBulkCopyInserter（v5.0 阶段 4.2 引入）

## 背景

MySqlBulkCopy 路径依赖 `AllowLoadLocalInfile=true`（LOAD DATA LOCAL INFILE 协议）。
MySqlConnector 默认 `false` 出于安全考虑：恶意/被攻陷的 MySQL 服务端可借 LOAD DATA LOCAL
请求读取**客户端**本地文件（已知攻击向量）。初版 `MySqlProvider.CreateConnection` 对
`false`（无论用户显式设置还是驱动默认）无条件提升为 `true`，原因是 builder 层无法区分两种
false（ITM-612 实证），用户的安全加固意图被静默覆盖。PROV-001（2026-09-23）后
`HasExplicitKey` 可区分两者，2026-11-24 起显式关闭不再改写。

## 决策

默认性能优先（用户未表态时自动提升为 true），但显式关闭时尊重用户意图，另有三层约束兜底：

1. **文档登记攻击面**（本 ADR + MySqlProvider 注释已做）——使用者知情。
2. **连接串显式意图优先**：用户显式写 `AllowLoadLocalInfile=false` 时 PalORM 不再改写，
   该标志即生效；此时 BulkCopy 走 local_infile 探测失败的 fallback，改为多值 INSERT，
   安全加固意图生效（代价是该连接失去 LOAD DATA 加速）。原实现无法区分"未设置"与
   "显式 false"，只能无条件提升；PROV-001 的 `HasExplicitKey` 使区分成立，本条于
   2026-11-24 落地（此前长期处于"前提已破、待裁决"状态）。
3. **不做启发式检测**（连接串文本扫描 "AllowLoadLocalInfile" 等）——脆弱且易误判。

## 后果与 revisit 条件

- 多租户/不可信服务端场景：客户端显式 `AllowLoadLocalInfile=false`（2026-11-24 起生效）
  或服务端 `local_infile=OFF` 均可关闭本地文件读取；两者都不做时由默认提升承担性能优先。
- **Revisit 触发**：若 DbOptions 增加 `MySqlBulkCopyMode { Auto, Always, Never }`
  类显式开关的需求出现（用户反馈/issue），升级为显式 opt-in 默认 Never——届时本 ADR
  作废并重裁决。攻击面利用在依赖方被实际报告时立即 revisit。

## 被否方案

- 检测连接串文本关键字：无法覆盖等价写法，误报风险。
- 默认 Never + BulkCopy 需显式 opt-in：性能路径（4.84× 基准）对多数内部服务端场景
  是安全收益为零的损失——留待真实需求驱动。

## 补充（2026-08-15 r4）：Enlist 族同型参数

PG `Enlist` / MySQL `AutoEnlist` / `ConnectionReset` 与 AllowLoadLocalInfile 同属
"显式设置与驱动默认不可区分，无条件覆盖"形态（ITM-643）。其中 Enlist 族的后果最重：
依赖 TransactionScope 的用户被静默脱离环境事务（SQL 自动提交，数据一致性风险）。
维持覆盖现状（与 G 主决策同理由：TransactionScope 不在支持路径——G18 门禁禁用），
缓解 = 显式事务主路径 + Provider 注释登记；revisit 条件并入 G 主体。

## 修订记录

- 2026-08-15：初裁（无条件提升为 true + 三层兜底）。
- 2026-09-23：PROV-001 实测 `HasExplicitKey` 可区分"未设置"与"显式 false"，本 ADR 的
  "builder 层无法区分"前提被证伪；策略改动挂起待裁决。
- 2026-11-24：裁决落地，决策 2 由"通道保留"改为"显式意图优先"（显式 false 不再改写，
  BulkCopy 回退多值 INSERT）；同步 `MySqlProvider.CreateConnection` 实现、注释与
  `ProviderTests` 锁定用例。
