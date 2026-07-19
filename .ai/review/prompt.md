# PalORM Review 系统（v3.1 — 统一入口 · 质量为先）

> **审计系统与评审系统已彻底合并为本系统**（2026-07-19 裁决）。单入口 `/review`，按触发档位决定**范围**。
> **宗旨：质量为先**——档位只划定范围边界，范围内一律地毯式逐行、逐文件读完全部手写代码；
> 不因档位低而抽样、跳读（见引擎核心原则 0/3）。
> 检查方法统一定义在 [`engine.md`](engine.md)（七流·地毯式逐行×探针并用·误判库·[推断]零容忍·危害×复杂度·下沉审查·五指标）。
> 本文件定义：触发档位、范围策略、产出格式、验证回路。
> 回答的问题：**diff 档 = 这次提交行不行？ 里程碑档 = 整体有什么缺陷、走向如何？**

---

## 触发档位（单入口 `/review`，档位决定范围，范围内深度恒为地毯式逐行）

| 档位 | 触发 | 逐行范围 | 产出增量 |
|------|------|------|---------|
| **轻量** | diff 仅 docs/·*.md·注释 | 触及文档全文 + doc-consistency-check.sh + 口径复核 | 不跑七流 |
| **轻量** | diff 仅 test/（不含测试基建） | 触及测试文件全文 + assertion-strength-check.sh + 断言语义逐个读 | 弱断言不得新增 |
| **标准** | diff 触及 src/（Core/Provider/SourceGen） | 触及文件**全文逐行**（非仅 diff 行——上下文缺陷藏在未改动行）+ 七流交叉验证；SourceGen 附专项路径（见下）；DataSession/SessionOperationState/Resilience 改动方法所在类全量 | 报告 8 段 |
| **全量** | 显式 `/review --full` 或重大重构后 | src/ **全部手写代码逐文件逐行** + 覆盖度证据（文件清单+行数；零发现必须证明"逐行后确认干净"） | 报告 8 段 + 覆盖度声明 |
| **里程碑** | 里程碑节点 `/review --milestone` | = 全量档范围 + 趋势 + 热点 | + metrics 指标轮次对比行 + 热点表（见下） |

- 逃逸率驱动升档：某流上一轮出现逃逸（metrics.md 账本），该流本轮升一级深度。
- 防线代码自身按业务标准检查（执行门禁第 3 项，ITM-405 下沉）。

## 执行门禁（不可跳过）

1. `bash scripts/gate-check.sh` 全绿是 review 前置——门禁失败先修门禁，review 不重复门禁已覆盖的检查（G 项清单见 [`../gate/prompt.md`](../gate/prompt.md)）。
2. `bash scripts/review-snapshot.sh` → 输出粘贴到报告段 2。违反 = 视为草稿。
3. `bash scripts/review-scope.sh [--diff]` → 应读清单+覆盖度账本进报告段 1；账本逐文件勾销，
   未勾销文件出现 = 报告视为草稿（"没找到"与"不存在"的区分由账本承载）。
4. 范围声明三项（必须检查 / 明确不检查 / 抽样策略）——见引擎「执行前强制项」。
5. 防线代码 diff（快照/对称性/架构/性质测试、gate/assertion/doc 脚本）按 src 同深度逐行。

## 执行编排（全量/里程碑档——速度靠并行，质量靠问题卡）

```
gate + 机械防线全跑（分钟级，给出重点区域）
    → review-scope.sh 分片（按行数均衡，默认 4 片）
    → 每片一个子代理地毯逐行（带七流问题卡+误判库速版，只产疑点表不定级）
    → 主线程并行做跨片全局不变式（过滤覆盖/参数编号空间/注册表键集/跨文件契约）
    → 收敛：主线程统一定级 → 探针实证（probe-template.sh）→ 定稿门三问 → 报告
```
标准档（diff）通常单线程即可完成——触及文件全文逐行 + 问题卡，无需分片。

## 里程碑档增量产出

### 趋势
[`metrics.md`](metrics.md) 四指标（逃逸/复发/密度/时延 + 证伪数）轮次对比一行 + 退化原因一句。不输出综合分（已废止，误判模式 6）。

### 热点表（替代已废止的四维加权风险评分）
```bash
git log --oneline -30 --name-only --pretty=format: | grep '\.cs$' | sort | uniq -c | sort -rn | head   # 变更频率
grep -oh "ITM-[0-9]*" .ai/review/history/action-items/action-items-*.md | sort | uniq -c | sort -rn | head          # 缺陷密度
```
两列交集即热点。只列事实，不加权评分——评分无预测力（未预判到 OrWhere 复发）。

### 探索性视角（替代已废止的 36 视角池）
每轮里程碑档自选 **1 个**七流之外的探索性视角（如回归面核对、消息面、防线自查），命中史记入 [`perspective-stats.md`](perspective-stats.md)。七流恒为主体。

## 源生成器变更专项路径（diff 触及 SourceGen 时强制）

> 机械化状态见引擎「生成语义流机械化状态」表——检查职责 = 审阅机械防线的 diff，不重做机械检查。

1. **快照先行**：SourceGen.Tests 快照失败 = 生成物变化未刷新基线 = REQUEST_CHANGES；刷新后的 `Snapshots/*.snap` diff 逐文件审阅。
2. **三序一致**：列集合改动时快照 diff 中 CREATE/INSERT 列序、RowFactory 序号、Bind 参数序三处必须同步变化——不同步即 P0。
3. **三方言核对**：DialectSymmetryTests 通过 = 无未登记差异；只审差异表变更是否有文档化依据。
4. **特性组合**：新增特性先扩快照实体再改 Emitter。诊断改动必有负向测试。

## 302 坑清单的消费方式

[`../../docs/踩坑目录.md`](../../docs/踩坑目录.md) 是**新坑登记入口**，不是每轮检查清单——坑的价值已蒸馏进 167 条 STD → 门禁/PALORM 诊断/测试。每轮职责：新发现的缺陷若是新坑形态，登记进目录并评估下沉；不再逐轮"抽样核对"。

## 产出（模板强制）

- 报告 → [`templates/report.md`](templates/report.md)（8 段不可省略；段 6 引用 metrics 不重述；段 8 为四指标记录）
- 行动项 → [`templates/action-items.md`](templates/action-items.md)（单维度 P0-P3 + 下沉审查段）

## 事后验证回路

```
报告完成 → 以下全部通过 → review 完成
```

1. `bash scripts/verify-action-items.sh <行动项文件>` — 标识符存在性
2. 涉及分析器规则 → `dotnet build` 验证触发条件
3. `git diff HEAD~1 --stat` — 影响范围回溯
4. [`metrics.md`](metrics.md) 追加本轮指标行（发现数按流、探针数、**证伪数**、下沉数）
5. 里程碑档 → 更新 [`perspective-stats.md`](perspective-stats.md) 探索性视角命中史

## PalORM 阶段适配

| 阶段 | 检查重点 |
|:----:|---------|
| 0 (筑基) | Core 接口完整性·AOT 配置·源生成器基础·113 API 覆盖（对照 docs/API参考.md） |
| 1 (凝脉) | Provider 实现正确性·FormattableString 参数化·类型映射 |
| 2 (结丹) | 关系映射·缓存·批量操作·AOT 全链路 |
| 3 (元婴) | 多数据库适配·事务·分布式追踪·性能基准 |
| 4 (化神) | NuGet 发布·文档·社区·CI/CD |

## 迁移备注（2026-07-19）

- 原 `/audit` 与 `/review` 统一为 `/review`（档位见上）；`audit-system/` 目录已并入本目录。
- 已废止并有据：四维加权风险评分（无预测力）、36 视角池 P3 自适应（产出恒来自七流+探针）、三层综合分/十维小数分（误判模式 6）、双维度行动项优先级（37 项实测与 P0-P3 完全同向）、逐轮 302 坑抽样（零产出）。
- 历史 audit 报告在 `history/reports/audit-*.md` 原地保留。
