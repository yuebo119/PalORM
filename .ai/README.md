# .ai — PalORM AI 质量工程系统

> 深检引擎 + 双 profile + gate/refine 双工具 + 误判知识库 + 四指标账本。
> 核心原则：**把纪律转化为基础设施——让正确的事比错误的事更容易做**。
> 演进方向：评审证明会反复出现的发现类别持续下沉为机械防线（快照/对称性/断言强度/架构测试），
> 深检收缩到机器构不着的语义判断。
> 一致性由 `scripts/verify-ai-system.sh` 机械校验（挂 CI）；修改任何口径数字前先看下方「维护规则」。

---

## 系统结构（引擎 + 前端）

| 文件 | 角色 | 回答的问题 | 触发 |
|------|------|-----------|------|
| [deep-check-engine.md](deep-check-engine.md) | **引擎**：七流·误判库·[推断]零容忍·下沉审查·四指标 | 怎么查 | 被两 profile 引用 |
| [review-system-v2.md](review-system-v2.md) | **review profile**：分级触发·diff 范围·8 段报告 | 这次提交行不行？ | 每次实质提交 |
| [audit-system/prompt.md](audit-system/prompt.md) | **audit profile**：全量·视角池·趋势·风险预判 | 整体有什么缺陷？走向如何？ | 里程碑 `/audit` |
| [gate-system/prompt.md](gate-system/prompt.md) | 门禁：G1-G27 机械检查 | 遵守规范了吗？ | 每次提交前 + CI |
| [refine-system/prompt.md](refine-system/prompt.md) | 精炼：24 项操作矩阵 | 如何更优实现？ | `refine-scan.sh` 起步 |

裁决顺序：**gate 阻断 > 深检缺陷（audit/review） > refine 优化**。同一发现只归属一个系统。

## 机械防线清单（深检的下沉产物，挂 CI）

| 防线 | 守护对象 | 来源教训 |
|------|---------|---------|
| test/PalORM.SourceGen.Tests/SnapshotTests | 生成物基线（16 类型 × 全特性 × 三方言）+ 编译探针 | ITM-301/139/328 |
| test/PalORM.SourceGen.Tests/DialectSymmetryTests | 三方言差异登记表（未登记差异即失败） | ITM-303/304/315/326 |
| scripts/assertion-strength-check.sh | 弱断言基线（19 只减不增） | ITM-319/327 |
| scripts/gate-check.sh | G1-G27 | 302 坑 |
| scripts/verify-ai-system.sh | .ai 系统自身一致性 | 模式 P8 |
| scripts/doc-consistency-check.sh | 文档口径 | DOC 系列 |

## 文件地图

```
.ai/
├── README.md                        ← 本文件（总入口）
├── deep-check-engine.md             深检引擎（audit/review 共用检查方法）
├── metrics.md                       四指标账本 + 缺陷逃逸账本（只增不删）
├── audit-system/
│   ├── prompt.md                    audit profile（全量+趋势+预判）
│   ├── known-false-positives.md     误判知识库（每轮深检前强制加载）
│   ├── perspective-stats.md         P3 自适应视角发现率账本（每轮审计后更新）
│   └── reports/                     历史审计报告（不可再生，勿删）
├── gate-system/prompt.md            门禁提示词（与 scripts/gate-check.sh 一一对应）
├── refine-system/prompt.md          精炼提示词（24 项操作矩阵 + 热点基线）
├── review-system-v2.md              review profile（分级触发+产出格式）
├── review-system-v2/
│   ├── template.md                  报告模板（8 段强制；段 8 = 四指标）
│   ├── action-items-template.md     行动项模板（含下沉审查段）
│   └── reports/                     历史评审报告
└── brain-data/                      cortex 运行时记忆（gitignore，不版本化）
```

## 权威依据链

```
docs/踩坑目录.md (302 项陷阱)
    → docs/编码规范.md (167 条 STD 规则 × 17 类)
        → scripts/gate-check.sh (G1-G27 机械门禁)
        → 深检引擎 + 两 profile（审计/评审依据）
docs/API参考.md (113 API = 112 实现 + 1 移除)   ← audit 专项 #2 的对照基准
docs/架构设计.md (18 项设计决策)                 ← 架构流对照基准
```

## 维护规则（违反即 verify-ai-system.sh FAIL）

1. **口径数字单一来源**：STD 条数以 `grep -oE 'STD-[A-Z]+-[0-9]+' docs/编码规范.md | sort -u | wc -l` 为准；踩坑数以目录标题为准；G 编号以 `gate-check.sh` 为准。提示词中引用这些数字的地方改一处必须全改。
2. **门禁三方同步**：改 `gate-check.sh` 必须同步 `gate-system/prompt.md` 的 G 表和 `docs/编码规范.md` 门禁清单章节。
3. **误判知识库只增不删**：发现新误判模式追加为 P{N}；模式被推翻时标注勘误而非删除。
4. **报告不可变**：`reports/` 下已发布报告只能追加「事后勘误」区块，不得改写正文结论。
5. **账实一致**：凡声称"已完成"且指向具体文件的条目，写入前必须 `ls` 验证存在；仓库重建/回滚后所有账本状态视为未验证（误判知识库模式 P8）。
6. **视角账本随审更新**：每轮审计结束后更新 `perspective-stats.md`，否则下一轮 P3 自适应无数据可读。
7. **逃逸账本只增不删**：metrics.md 逃逸账本每条必须附下沉动作；逃逸是深检方法论的改进输入，不是耻辱柱。
8. **快照基线刷新必须评审**：`PALORM_UPDATE_SNAPSHOTS=1` 产生的 snap diff 未经人工评审不得提交。
9. **对称性差异表先登记后实现**：新增方言差异必须先在 DialectSymmetryTests 差异表登记（含依据），再改 Emitter。
