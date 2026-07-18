# PalORM 评审 Profile（review-system v2.1 — 深检引擎前端）

> **检查方法统一定义在 [`deep-check-engine.md`](deep-check-engine.md)**（七流·误判库·[推断]零容忍·危害×复杂度·下沉审查·质量指标）。
> 本文件只定义 review profile 特有的：触发、范围策略、产出格式、验证回路。
> 回答的问题：**这次提交行不行？**

---

## 触发与分级（按 diff 触及面决定深度）

| diff 触及面 | 评审深度 | 说明 |
|------------|---------|------|
| 仅 docs/·*.md·注释 | **轻量**：doc-consistency-check.sh + 口径数字复核 | 不跑七流 |
| 仅 test/（不含测试基建） | **轻量**：assertion-strength-check.sh + 断言强度抽读 | 弱断言不得新增 |
| src/PalORM.Core 或 Provider | **标准**：七流 diff 范围逐行 + 交叉验证 | 引擎全规则 |
| src/PalORM.SourceGen | **标准 + 专项**：快照/对称性测试先行（见下） | 生成物 diff 是评审对象 |
| DataSession/SessionOperationState/Resilience | **标准 + 全量**：改动方法所在类全量逐行 | 状态机/并发面爆炸半径大 |
| scripts/·.github/·门禁 | **标准**：test-quality-scripts.sh 故障夹具 + 三方同步核对 | G 表/脚本/规范文档 |

逃逸率驱动修正：某流上一轮出现逃逸（metrics.md 账本），该流本轮升一级深度。

## 执行门禁（不可跳过）

1. `bash scripts/review-snapshot.sh` → 输出粘贴到报告段 2。违反 = 评审视为草稿。
2. 范围声明三项（必须评审 / 明确不评审 / 抽样策略）——见引擎「执行前强制项」。
3. **防线代码自身按业务标准评审**（ITM-405 下沉）：diff 触及机械防线（快照/对称性/架构/性质测试、gate/assertion/doc 脚本）时，防线的检测逻辑与绕过路径按 src 同深度逐行——防线失明比业务缺陷更贵（45c476b 轮 5 项 P2 均为防线自身弱点）。

## 源生成器变更专项路径（diff 触及 SourceGen 时强制）

> 机械化状态见引擎「生成语义流机械化状态」表。评审职责 = 审阅机械防线的 diff，不是重做机械检查。

1. **快照先行**：`dotnet run`（test/PalORM.SourceGen.Tests）——快照测试失败 = 生成物变化未刷新基线 = REQUEST_CHANGES；`PALORM_UPDATE_SNAPSHOTS=1` 刷新后的 `Snapshots/*.snap` diff 逐文件评审。
2. **三序一致**：列集合逻辑改动时，快照 diff 中 CREATE/INSERT SQL 列序、RowFactory `GetXxx(n)` 序号、BindInsert 参数序三处必须同步变化——不同步即 P0（静默错列数据）。
3. **三方言核对**：DialectSymmetryTests 通过 = 无未登记方言差异；评审只核对差异表变更是否有文档化依据。
4. **特性组合**：快照基线已含全特性同体实体；新增特性先扩快照实体再改 Emitter。
5. **诊断不退化**：PALORM0xx 触发条件改动必须有负向测试。

## 产出（模板强制）

- 评审报告 → [`review-system-v2/template.md`](review-system-v2/template.md)（8 段不可省略；段 8 为四指标记录，不是评分）
- 行动项 → [`review-system-v2/action-items-template.md`](review-system-v2/action-items-template.md)（含下沉审查段）

## 事后验证回路

```
报告完成 → 以下全部通过 → 评审完成
```

1. `bash scripts/verify-action-items.sh <行动项文件>` — 标识符存在性
2. 涉及分析器规则 → `dotnet build` 验证触发条件
3. `git diff HEAD~1 --stat` — 影响范围回溯
4. [`metrics.md`](metrics.md) 追加本轮指标行（发现数按流、探针数、下沉数）

## PalORM 阶段适配

| 阶段 | 评审重点 |
|:----:|---------|
| 0 (筑基) | Core 接口完整性·AOT 配置·源生成器基础·113 API 覆盖（对照 docs/API参考.md） |
| 1 (凝脉) | Provider 实现正确性·FormattableString 参数化·类型映射 |
| 2 (结丹) | 关系映射·缓存·批量操作·AOT 全链路·302 坑防御 |
| 3 (元婴) | 多数据库适配·事务·分布式追踪·性能基准 |
| 4 (化神) | NuGet 发布·文档·社区·CI/CD |
