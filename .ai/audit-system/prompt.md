# PalORM 全局深度审计（v1.1 — PalORM 适配版）

> `/audit` → 全量审计。三价值：快照+趋势+预判。自适应：视角权重随历史发现率动态调整。
> 零 bug = 项目健康。审计必须区分"没找到"和"不存在"。
> 基于 audit-system 框架，适配 PalORM 插件-Provider 架构。
> **审计依据**：[`docs/编码规范.md`](../../docs/编码规范.md) — 167 条 STD 规则 × 17 类，源自 [`docs/踩坑目录.md`](../../docs/踩坑目录.md) 302 项陷阱

## P0：三价值

| 价值 | 内容 | 零bug时 |
|------|------|---------|
| 快照 | 三层评分+证据 | 正常输出 |
| 趋势 | 与上次对比+退化原因 | 标注"无基准" |
| 预判 | 四维加权风险评分 | 始终输出 |

## P1：评分可复现 · P2：零bug≠满分(无论证最高8) · P3：自适应视角选择

**P3 自适应规则**——数据载体为 [`perspective-stats.md`](perspective-stats.md)（本目录），每轮审计后必须更新，否则下轮无数据可读：
- 读取 `perspective-stats.md` 最近 3 轮记录 → 每个视角"是否发现" → 计算发现率
- 高发现率(≥50%)视角 → 本轮必选(最多3个)
- 零发现率(连续3轮)视角 → 降为"可选"·本轮不强制
- 保留至少 1 个"探索性"视角——来自最近 3 轮未使用的类别（账本已列候选）
- 已证伪的虚高轮次（如 2026-07-10）"零发现"不计入分母

## 三层评估

### 层1(30%): 7流 + N0视角池(36个·6类)

> 第七流「生成语义流」为 PalORM 专属：本项目最大的独特风险面是**编译期生成物与运行时假设的一致性**——普通六流都以"手写代码"为对象，覆盖不到这条缝。

| 流 | 检查焦点 | 方法 |
|------|---------|------|
| 架构流·安全流·资源流·并发流·错误流·AOT流 | 同通用定义（见下表） | 逐方法读取 |
| **生成语义流（第七流）** | ① 生成 SQL 列序 = RowFactory 读取序号 = BindInsert 参数序（三序一致）② 三方言 CommandSqlsByDialect 语义等价（LIMIT/引用符/RETURNING 差异是否被 DataSession 正确消费）③ Emitter 变更后 Verify 快照是否同步 ④ RegistryFragment 键集 = PalORM_Runtime.ValidateRequiredKeys 要求集 ⑤ 生成代码对 SoftDelete/TenantAware/Converter/OwnedJson 特性组合的笛卡尔覆盖 | 对每个 Emitter：Read 模板 → 找一个真实生成的 `*.g.cs`（obj/Generated）比对 → 与消费点（DataSession/Provider）交叉验证 |

| 类别 | 视角 | 选择规则 |
|------|------|---------|
| 格式合规 | null安全·异步一致性·错误消息·命名·异常类型 | P3自适应 |
| 设计深度 | 重复代码·热路径·测试覆盖·API一致性·DI·性能契约 | P3自适应 |
| 运行时 | fire-and-forget·重连退避·取消传播·空集合·异步初始化 | P3自适应 |
| AOT性能 | Span栈安全·Logger源生成·ValueTask·LINQ·字符串·async状态机·DAM·ref struct | P3自适应 |
| 消息分布 | 消息顺序·幂等并发·Saga超时·事件保留·追踪完整·补偿幂等 | P3自适应 |
| 测试配置 | 断言强度·Mock合理性·测试隔离·敏感配置·环境默认值 | P3自适应 |

### 层2(40%): 10维度×可计数证据
每维度含具体数字证据+取舍论证。评分封顶：层1发现→层2降分。

### 层3(30%): PalORM 专项检查（9项）

| # | 检查项 | 说明 |
|---|--------|------|
| 1 | Provider 插件依赖方向 | Core→零外部依赖 · Providers→只依赖Core接口+ADO.NET · SourceGen→只依赖Core |
| 2 | API 完整性 | 对照 `docs/API参考.md`（113 API，112 实现 + 1 设计移除）· 公共API是否有XML文档注释 |
| 3 | AOT 兼容 | IsAotCompatible=true · STJ源生成 · 零反射 · 零MakeGenericType · 零Expression.Compile() |
| 4 | 源生成器正确性 | RowFactory生成完整 · TypeMapper生成完整 · Migration生成完整 · **生成语义流（第七流）的发现在此计分** |
| 5 | FormattableString 参数化 | 所有SQL使用 FormattableString · 零字符串拼接SQL · 零SQL注入风险 |
| 6 | 302 坑防御 | 对照 `docs/踩坑目录.md` 逐坑验证 |
| 7 | 文档-代码-注释三方一致 | 公共API变更是否同步更新 docs/ + XML doc + 行内注释 |
| 8 | **struct 值语义** | QueryBuilder/ValueStringBuilder 等 struct 类型：复制后共享可变引用字段是否有写时复制或防御（QUERY-001 教训）· struct 内 lambda 捕获模式（CS1673，见误判 P5）· `in`/`ref readonly` 参数传递避免防御性复制 |
| 9 | **会话状态机完整性** | SessionOperationState 门禁覆盖所有新增公共入口（新 API 是否接入 operation state）· 事务归属 AsyncLocal 流转 · Dispose 路径主异常保留 |

## 风险评分引擎(四维加权)

| 数据源 | 权重 | 获取 |
|--------|:--:|------|
| 变更频率 | 30% | `git log --oneline -20` |
| 缺陷密度 | 30% | `grep ITM docs/review/action-items-*` |
| 覆盖盲区 | 25% | 层1覆盖度<100%的流 |
| 复杂度 | 15% | >200行或>10方法的类 |

## 趋势(必出)·产出5项(必全)·S1-S4·自举

S1:证据→论证→权衡·不可修复必有实测 S2:三层架构6项 S3:量规化 S4:反模式禁止

综合=层1×30%+层2×40%+层3×30%

---

## PalORM 专项：插件-Provider 架构合规检查

| 原则 | 检查方式 | 违规示例 |
|------|---------|---------|
| Core 零外部依赖（仅 BCL + ADO.NET） | `grep -rn "using Dapper\|using Newtonsoft\|using AutoMapper" src/PalORM.Core/` | Core 引用了第三方库 |
| Providers 只依赖 Core 接口 | 检查 Provider 项目是否引用了非 Core 项目 | Sqlite Provider 引用了 MySql Provider |
| SourceGen 零运行时依赖 | 检查 SourceGen 是否只有分析器/生成器引用 | SourceGen 引用了运行时库 |
| FormattableString 参数化 SQL | `grep -rn "string\.Format\|\\$\"" src/ --include="*.cs"` 检查SQL构建 | 字符串拼接SQL参数 |
| 公共 API 有 XML 文档注释 | `grep -rn "public.*class\|public.*interface\|public.*record" src/PalORM.Core/` 对照注释 | 公共API缺少 `<summary>` |
| 302 坑防御完整性 | 对照 `docs/踩坑目录.md` 逐项核对 | 未覆盖已记录的陷阱 |
