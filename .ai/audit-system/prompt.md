# PalORM 审计 Profile（audit-system v2.0 — 深检引擎前端）

> **检查方法统一定义在 [`../deep-check-engine.md`](../deep-check-engine.md)**（七流·误判库·[推断]零容忍·危害×复杂度·下沉审查·质量指标）。
> 本文件只定义 audit profile 特有的：触发、全量范围、视角池、风险预判、趋势产出。
> 回答的问题：**代码整体有什么缺陷？走向如何？** 触发：里程碑节点 `/audit`。
> 零 bug = 项目健康。审计必须区分"没找到"和"不存在"。
> **审计依据**：[`../../docs/编码规范.md`](../../docs/编码规范.md) — 167 条 STD 规则 × 17 类，源自 [`../../docs/踩坑目录.md`](../../docs/踩坑目录.md) 302 项陷阱

## 三价值（audit 特有产出）

| 价值 | 内容 | 零bug时 |
|------|------|---------|
| 快照 | 七流全量发现 + 四指标 + 覆盖度证据 | 正常输出（附覆盖度证明"深度审计后确认干净"） |
| 趋势 | 四指标轮次对比 + 退化原因 | 标注"无基准" |
| 预判 | 四维加权风险评分 | 始终输出 |

## P3 自适应视角选择

数据载体为 [`perspective-stats.md`](perspective-stats.md)（本目录），每轮审计后必须更新，否则下轮无数据可读：
- 读取最近 3 轮记录 → 每个视角"是否发现" → 计算发现率
- 高发现率(≥50%)视角 → 本轮必选(最多3个)
- 零发现率(连续3轮)视角 → 降为"可选"·本轮不强制
- 保留至少 1 个"探索性"视角——来自最近 3 轮未使用的类别（账本已列候选）
- 已证伪的虚高轮次（如 2026-07-10）"零发现"不计入分母

### N0 视角池（36 个 · 6 类）

| 类别 | 视角 | 选择规则 |
|------|------|---------|
| 格式合规 | null安全·异步一致性·错误消息·命名·异常类型 | P3自适应 |
| 设计深度 | 重复代码·热路径·测试覆盖·API一致性·DI·性能契约 | P3自适应 |
| 运行时 | fire-and-forget·重连退避·取消传播·空集合·异步初始化 | P3自适应 |
| AOT性能 | Span栈安全·Logger源生成·ValueTask·LINQ·字符串·async状态机·DAM·ref struct | P3自适应 |
| 消息分布 | 消息顺序·幂等并发·Saga超时·事件保留·追踪完整·补偿幂等 | P3自适应 |
| 测试配置 | 断言强度·Mock合理性·测试隔离·敏感配置·环境默认值 | P3自适应（断言强度已由 assertion-strength-check.sh 机械守护，人工只审语义强度） |

## PalORM 专项检查（9 项，全量轮必查）

| # | 检查项 | 说明 |
|---|--------|------|
| 1 | Provider 插件依赖方向 | Core→零外部依赖 · Providers→只依赖Core接口+ADO.NET · SourceGen→只依赖Core |
| 2 | API 完整性 | 对照 `docs/API参考.md`（113 API，112 实现 + 1 设计移除）· 公共API有XML文档注释 |
| 3 | AOT 兼容 | IsAotCompatible=true · STJ源生成 · 零反射 · 零MakeGenericType · 零Expression.Compile() |
| 4 | 源生成器正确性 | 生成语义流发现在此计分（机械化状态见引擎；快照/对称性测试先跑） |
| 5 | FormattableString 参数化 | 所有SQL使用 FormattableString · 零字符串拼接SQL |
| 6 | 302 坑防御 | 对照 `docs/踩坑目录.md` 抽样核对（全量逐坑不现实，声明抽样率） |
| 7 | 文档-代码-注释三方一致 | 公共API变更同步 docs/ + XML doc + 行内注释（doc-consistency-check.sh 先跑） |
| 8 | struct 值语义 | QueryBuilder/ValueStringBuilder 复制后共享可变引用字段的写时复制（QUERY-001）· struct 内 lambda 捕获（CS1673，误判 P5）· in/ref readonly 防御性复制 |
| 9 | 会话状态机完整性 | SessionOperationState 门禁覆盖所有新增公共入口 · 事务归属 AsyncLocal 流转 · Dispose 主异常保留 |

## 插件-Provider 架构合规检查命令

```bash
# 禁止：Core 引用第三方 ORM
grep -rn "using Dapper\|using EntityFramework\|using NHibernate" src/PalORM.Core/ --include="*.cs"
# 禁止：Provider 跨引用
grep -rn "using PalORM\.\(Sqlite\|PostgreSql\|MySql\)" src/PalORM.Sqlite/ --include="*.cs"
grep -rn "using PalORM\.\(Sqlite\|PostgreSql\|MySql\)" src/PalORM.PostgreSql/ --include="*.cs"
grep -rn "using PalORM\.\(Sqlite\|PostgreSql\|MySql\)" src/PalORM.MySql/ --include="*.cs"
# 禁止：SourceGen 引用运行时
grep -rn "using PalORM\.\(Sqlite\|PostgreSql\|MySql\|Testing\)" src/PalORM.SourceGen/ --include="*.cs"
```

## 风险评分引擎（四维加权，预判产出）

| 数据源 | 权重 | 获取 |
|--------|:--:|------|
| 变更频率 | 30% | `git log --oneline -20` |
| 缺陷密度 | 30% | `grep ITM .ai/review-system-v2/action-items-*` |
| 覆盖盲区 | 25% | 七流覆盖度<100%的流 |
| 复杂度 | 15% | >200行或>10方法的类 |

## 产出与收口

1. 审计报告 → `reports/audit-{date}.md`：快照（发现清单 + 覆盖度）· 趋势（四指标对比，**无综合分**）· 预判（四维风险）
2. 更新 [`perspective-stats.md`](perspective-stats.md)（视角账本，维护规则 6）
3. 更新 [`../metrics.md`](../metrics.md)（四指标轮次行）
4. 下沉审查：每个 P0/P1 按引擎「发现下沉审查」表处置，结果入行动项账本
