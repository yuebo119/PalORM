# .ai — PalORM AI 质量工程系统

> 四子系统 + 误判知识库 + 视角发现率账本。核心原则：**把纪律转化为基础设施——让正确的事比错误的事更容易做**。
> 一致性由 `scripts/verify-ai-system.sh` 机械校验（挂 CI）；修改任何口径数字前先看下方「维护规则」。

---

## 四子系统边界（四问四答，互不越界）

| 系统 | 回答的问题 | 触发 | 产出 | 时长 |
|------|-----------|------|------|:--:|
| [gate-system](gate-system/prompt.md) | 遵守规范了吗？ | `bash scripts/gate-check.sh`（每次提交前 + CI） | G1-G25 通过/失败 | 秒级 |
| [audit-system](audit-system/prompt.md) | 有什么缺陷？ | 里程碑节点全量执行 | 三层评分 + 趋势 + 风险预判 | 小时级 |
| [refine-system](refine-system/prompt.md) | 如何更优实现？ | `bash scripts/refine-scan.sh` 起步 | 24 项矩阵量化方案 | 分钟-小时 |
| [review-system-v2](review-system-v2.md) | 这次提交行不行？ | 每次实质提交 | 8 段模板报告 + 行动项 | 分钟-小时 |

裁决顺序：**gate 阻断 > audit 缺陷 > review 行动项 > refine 优化**。同一发现只归属一个系统（边界表见各 prompt 开头）。

## 文件地图

```
.ai/
├── README.md                        ← 本文件（总入口）
├── audit-system/
│   ├── prompt.md                    审计提示词（三层评估 + 七流）
│   ├── known-false-positives.md     误判知识库（每轮审计前强制加载）
│   ├── perspective-stats.md         P3 自适应视角发现率账本（每轮审计后更新）
│   └── reports/                     历史审计报告（不可再生，勿删）
├── gate-system/prompt.md            门禁提示词（与 scripts/gate-check.sh 一一对应）
├── refine-system/prompt.md          精炼提示词（24 项操作矩阵 + 热点基线）
├── review-system-v2.md              评审提示词（4 层架构）
├── review-system-v2/
│   ├── template.md                  报告模板（8 段强制）
│   ├── action-items-template.md     行动项模板
│   └── reports/                     历史评审报告
└── brain-data/                      cortex 运行时记忆（gitignore，不版本化）
```

## 权威依据链

```
docs/踩坑目录.md (302 项陷阱)
    → docs/编码规范.md (167 条 STD 规则 × 17 类)
        → scripts/gate-check.sh (G1-G25 机械门禁)
        → 四子系统提示词（审计/评审/精炼依据）
docs/API参考.md (113 API = 112 实现 + 1 移除)   ← audit 层 3 专项 #2 的对照基准
docs/架构设计.md (18 项设计决策)                 ← 架构流对照基准
```

## 维护规则（违反即 verify-ai-system.sh FAIL）

1. **口径数字单一来源**：STD 条数以 `grep -oE 'STD-[A-Z]+-[0-9]+' docs/编码规范.md | sort -u | wc -l` 为准；踩坑数以目录标题为准；G 编号以 `gate-check.sh` 为准。提示词中引用这些数字的地方改一处必须全改。
2. **门禁三方同步**：改 `gate-check.sh` 必须同步 `gate-system/prompt.md` 的 G 表和 `docs/编码规范.md` 门禁清单章节。
3. **误判知识库只增不删**：发现新误判模式追加为 P{N}；模式被推翻时标注勘误而非删除。
4. **报告不可变**：`reports/` 下已发布报告只能追加「事后勘误」区块，不得改写正文结论（历史评分曲线是系统自省的证据链）。
5. **账实一致**：凡声称"已完成"且指向具体文件的条目，写入前必须 `ls` 验证存在；仓库重建/回滚后所有账本状态视为未验证（误判知识库模式 P8）。
6. **视角账本随审更新**：每轮审计结束后更新 `perspective-stats.md`，否则下一轮 P3 自适应无数据可读。
