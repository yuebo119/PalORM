# perf-budgets.md（PalORM 性能预算契约 · EPSL 接入件）

> 生成：2026-10-11（EPSL T2.1）｜ 定位：把既有 PerfHub/PerfGate 门禁口径收拢为单一事实源；本文件不改变任何现有阈值，只做成文声明。
> 真源关系：测量明细在 `bench/perfhub/results/history-*.json`；门禁执行在 `tools/PalORM.PerfGate`；汇报规范在根 AGENTS.md。本文件与三者冲突时以本文件发起修订、同批次更新。

## 1. 量级声明

| 维度 | 当前档位 | 说明 |
| --- | --- | --- |
| 行档 | 2,000 / 20,000 | 测试矩阵固定双档 |
| 方言 | SQLite（进程内）/ MySQL / PostgreSQL（远程） | 三方言全量为一轮 |
| 并发 | 混合读写 80/20，t1/t4/t8 | 并发项单独组 |

## 2. SLO 预算（比值口径优先，绝对值仅量级参考）

跨机绝对值不可比（B100），预算以批内三臂比值与确定性分配为口径：

| 指标 | 预算 | 口径 | 执行 |
| --- | --- | --- | --- |
| 批内 P/ADO 时延比 | ≤ 1.30（批量族 + GetAllAsync 为主门禁面） | 同批三臂，per-operation，中位数 | PerfGate tables 自动标色（🟥 ≥+30%） |
| 偏离基准待看线 | P/ADO ≥ 1.50 或 ≤ 0.67 → 📌 待人工判读（两个方向都过目：劣化或地板缺陷） | 同上 | 同上 |
| 地板健全性 | 任一 ORM 臂快过 ADO 地板 >10% → 判地板实现缺陷，该轮相关项作废 | 同批 | PerfHub ⚠地板? 门禁 |
| 分配相对 ADO | 记录带内波动（P/D 两列）；单行族带 ~+26~45%（已知分配税），批量族收敛至 ±10% 内 | AllocatedBytesPerOp | 报告四组表 |
| 地板稳定度 | 地板自身同轮漂移 >18% → 该轮 P/F 作废，判环境态 | 三层判据：分配 > 绝对耗时 > 同轮比值 > 跨轮比值 | B100 纪律 |
| BDN 微基准 | 27 项阈值内绿（MemoryDiagnoser 受控进程） | 单独套件 | bench 阈值文件 |

## 3. 复杂度契约（库形态）

PalORM 的"热路径" = 物化器（QueryAll/StreamAll 按序号取列）与 SQL 构建器；契约形态为 O(列数 + 行数) 线性物化，无逐行 GetOrdinal / 全表缓存。既有锁定测试承载（Core/Integration 套件），新增公共 API 时随行为测试补契约断言。

## 4. GC 与环境口径（测量有效性前提）

工作站 GC（Environment 记录于每批 history JSON）；远程方言的 MySQL/PG 慢态既有族（filtered 复测回落先例）判环境态不判回归。

## 5. 明确不做（引用既有裁决，防重复提案）

| 项 | 出处 |
| --- | --- |
| 单条 Insert/Update 池化 | B75（单次调用语义无池化前提，驱动固有下限 2024B） |
| MySQL Insert_1by1 池化 | B77（分解数据在案：可省仅约 5%） |
| 每请求复用 DataSession 的默认化 | 报告口径声明项（复用可快 35~40%，属调用方决策） |
| N6 以下量级逐项立项 | step23 收敛结论（低于批间摆动带，机制可证 + 行为测试锁定即可） |
| MySQL BulkCopy 值载体 object[] 化（弃参数池中转） | EPSL 2026-10-11 审计 B 批 #4 否决：库内 PG-4 探针实测未定类型参数 Value setter 13ns/格（DbType+Value 31ns 对照），20K 行 × 5 列 ≈ 1.5ms ≈ 操作的 1.7%——低于 5% 不做线与 ±8pp 批间摆动带，基准不可见；而改动面 = SourceGen 新 binder + 注册链 + 快照重录 + 旧模型回退（7caa357 同形否决过的成本面） |

## 6. 门禁接线表

| 预算项 | 执行载体 | 触发 |
| --- | --- | --- |
| 比值/色标/📌 | PerfGate tables（PerfCli report 自动附带） | 每次跑测汇报 |
| 地板健全性 | PerfHub 报告检查 | 每批 |
| 回归自动归因 | perf-bisect（EPSL perf-template/audit/bisect-design.md，设计稿） | 门禁红且复测确认非环境态 |
| 趋势曲线 | `python curve.py bench/perfhub/results`（EPSL T0.6，已对本仓 268 批跑通） | 节点审计 |

## 7. 修订触发

量级新增档位（如 10 万行曲线）、新方言、SLO 重谈时，本文件与 PerfGate 阈值同批次更新。
