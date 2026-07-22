# .ai — PalORM AI 质量工程系统

> **四系统一个入口**：review（找缺陷）· gate（查规范）· refine（求更优）· test（验测试）。各系统目录自包含。
> **宗旨：质量为先**——默认地毯式逐行、逐文件阅读全部范围内代码；探针/防线/档位是
> 质量之上的证据强化与流程编排，不以牺牲覆盖换速度。
> 核心原则：**把纪律转化为基础设施——让正确的事比错误的事更容易做**。
> 演进方向：review 证明会反复出现的发现类别持续下沉为机械防线（快照/对称性/断言强度/架构测试），
> 深检收缩到机器构不着的语义判断。
> 一致性由 `scripts/verify-ai-system.sh` 机械校验（挂 CI）；修改任何口径数字前先看下方「维护规则」。

---

## 四系统（每个系统一个目录、一个 prompt 入口）

| 入口 | 系统 | 回答的问题 | 触发 |
|------|------|-----------|------|
| [review/prompt.md](review/prompt.md) | **review**（审计+评审已合并）：单入口五档位，引擎/账本/误判库/模板/历史全内聚于 `review/` | diff 档=这次提交行不行？里程碑档=整体健康+走向？ | `/review` 按档位 |
| [gate/prompt.md](gate/prompt.md) | **gate**：G1-G30 机械门禁（全阻断，零警告级死项） | 遵守规范了吗？ | 每次提交前 + CI |
| [refine/prompt.md](refine/prompt.md) | **refine**：24 项操作矩阵 | 如何更优实现？ | `refine-scan.sh` 起步 |
| [test/prompt.md](test/prompt.md) | **test**：T1-T10 测试铁律 + 覆盖矩阵 + 基准配置规范 | 测试是否充分且规范？ | `test-gate.sh` + `/test` |

裁决顺序：**gate 阻断 > test 违规 > review 缺陷 > refine 优化**。同一发现只归属一个系统。

## 文件地图

```
.ai/
├── README.md                    ← 本文件（总入口，仅此一份跨系统导航）
├── review/                      ← review 系统（自包含：入口+引擎+账本+历史）
│   ├── prompt.md                系统入口（单入口五档位 + 并行执行编排，v3.1）
│   ├── engine.md                检查引擎（地毯×探针并用 + 并行地毯协议 + 七流问题卡 + 定稿门三问，v2.2）
│   ├── metrics.md               五指标账本（逃逸/复发/证伪/密度/时延，只增不删）
│   ├── known-false-positives.md 误判知识库（速版内嵌子代理 + 完整版定稿门对照）
│   ├── perspective-stats.md     探索性视角命中史（里程碑档更新）
│   ├── templates/
│   │   ├── report.md            报告模板（8 段强制；段 3 含误判对照列；段 8 = 指标）
│   │   └── action-items.md      行动项模板（单维度 P0-P3 + 下沉审查段）
│   └── history/                 历史产出（不可再生，勿删）
│       ├── action-items/        历轮行动项账本
│       └── reports/             历轮报告（含原 audit-*.md）
├── gate/prompt.md               门禁提示词（与 scripts/gate-check.sh 一一对应）
├── refine/prompt.md             精炼提示词（24 项操作矩阵 + 热点基线 v1.2）
├── test/prompt.md               测试规范提示词（T1-T10 铁律 + 覆盖矩阵 + 基准规范）
└── brain-data/                  cortex 运行时记忆（gitignore，不版本化）
```

## 机械防线清单（review 的下沉产物，挂 CI）

| 防线 | 守护对象 | 来源教训 |
|------|---------|---------|
| test/PalORM.SourceGen.Tests/SnapshotTests | 生成物基线（含继承实体 × 全特性 × 三方言）+ 编译探针 | ITM-301/139/328/502 |
| test/PalORM.SourceGen.Tests/DialectSymmetryTests | 三方言差异登记表（未登记差异即失败） | ITM-303/304/315/326 |
| test/PalORM.Core.Tests/ArchitectureInvariantTests | 触表入口必经默认过滤（含 partial 扫描） | ITM-302/404/405 |
| test/PalORM.Core.Tests/QueryBuilderPropertyTests | SQL 结构性质（500 种子 + xorshift） | ITM-306/307/401/414 |
| scripts/assertion-strength-check.sh | 弱断言基线（19 只减不增） | ITM-319/327 |
| scripts/gate-check.sh | G1-G28（G26 struct 守卫 / G27 CS1591 防回退 / G28 超时截断） | 302 坑 |
| scripts/verify-ai-system.sh | .ai 系统自身一致性 | 模式 P8 |
| scripts/doc-consistency-check.sh | 文档口径（含 D9 加和校验） | DOC 系列 + ITM-418 |
| scripts/probe-template.sh | 探针骨架生成（~30 秒出最小工程，定稿实证基建） | ITM-401/403 探针实践 |
| scripts/review-scope.sh | 应读清单+分片+覆盖度账本（地毯完整性机械可查） | 质量为先宗旨配套 |
| scripts/test-gate.sh | T1/T4/T6/T8/T9 测试规范门禁 + T-DEF-1/4 下沉 | T-DEF-1/3/4 审计 |

## 权威依据链

```
docs/踩坑目录.md (302 项陷阱 · 新坑登记入口，非逐轮检查清单)
    → docs/编码规范.md (167 条 STD 规则 × 17 类)
        → scripts/gate-check.sh (G1-G28 机械门禁)
        → review 引擎 + prompt（检查依据）
docs/API参考.md (113 API = 112 实现 + 1 移除)   ← review 阶段适配的对照基准
docs/架构设计.md (18 项设计决策)                 ← 架构流对照基准
```

## 维护规则（违反即 verify-ai-system.sh FAIL）

1. **口径数字单一来源**：STD 条数以 `grep -oE 'STD-[A-Z]+-[0-9]+' docs/编码规范.md | sort -u | wc -l` 为准；踩坑数以目录标题为准；G 编号以 `gate-check.sh` 为准。提示词中引用这些数字的地方改一处必须全改。
2. **门禁三方同步**：改 `gate-check.sh` 必须同步 `gate/prompt.md` 的 G 表和 `docs/编码规范.md` 门禁清单章节。
3. **误判知识库只增不删**：发现新误判模式追加为 P{N}；模式被推翻时标注勘误而非删除。
4. **报告不可变**：`review/history/reports/` 下已发布报告只能追加「事后勘误」区块，不得改写正文结论。
5. **账实一致**：凡声称"已完成"且指向具体文件的条目，写入前必须 `ls` 验证存在；仓库重建/回滚后所有账本状态视为未验证（误判知识库模式 P8）。
6. **视角账本随里程碑更新**：里程碑档结束后更新 `review/perspective-stats.md` 探索性视角命中史。
7. **逃逸账本只增不删**：`review/metrics.md` 逃逸账本每条必须附下沉动作；逃逸是深检方法论的改进输入，不是耻辱柱。
8. **快照基线刷新必须评审**：`PALORM_UPDATE_SNAPSHOTS=1` 产生的 snap diff 未经人工评审不得提交。
9. **对称性差异表先登记后实现**：新增方言差异必须先在 DialectSymmetryTests 差异表登记（含依据），再改 Emitter。
10. **review 不重复门禁**：门禁已覆盖的检查（G1-G28）review 只消费结果不重新定义命令；改门禁按规则 2 三方同步。
11. **系统目录自包含**：每个系统的规则/账本/模板/历史留在自己目录内；跨系统导航只在本 README。新增系统建 `.ai/<name>/prompt.md`。
