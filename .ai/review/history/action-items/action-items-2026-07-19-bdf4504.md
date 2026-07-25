# 行动项：2026-07-19 全量地毯 review（基线 bdf4504 · 整改回归验证轮）

> 报告: [../reports/review-2026-07-19-bdf4504-full.md](../reports/review-2026-07-19-bdf4504-full.md)
> 状态: **整改完成**（当日收口） · 方法: review v3.1 并行地毯（4 片 8607 行 100% 逐行 + 45 项整改热点复查）

## 优先级说明（单维度 P0-P3）

## P1 — 本迭代（整改连带回归）
| ID | 描述 | 文件 | 状态 |
|:--:|------|------|:--:|
| ITM-546 | ITM-509 过度拦截：字面 @p<n> 检测误拒含 @p 的合法 SQL 字面量（邮箱/LIKE）。推荐移除该检测或仅引号外生效 | src/PalORM.Core/FormattableSqlFormatter.cs:38-45 | ✅ |

## P2 — 下迭代
| ID | 描述 | 状态 |
|:--:|------|:--:|
| ITM-547 | ITM-513 拦截器接口文档未同步（仍称 UPDATE/QueryMultiple 不经拦截器） | ✅ |
| ITM-548 | ITM-513 QueryMultipleAsync OnBefore 无配对 OnAfter/OnError（begin/end 拦截器泄漏） | ✅ |
| ITM-549 | [Column] Length/Precision/Scale/DefaultValue 注解静默失效（既存） | ✅ |

## P3 — 评估
| ID | 描述 | 状态 |
|:--:|------|:--:|
| ITM-550 | RowFactoryEmitter HasCharColumn 与 switch char 形态不对称（当前不可达，补 System.Char） | ✅ |
| ITM-551 | ThenInclude 未做 _cteName 重映射（ITM-515 只修 Include） | ✅ |
| ITM-552 | UPDATE setColumns 谓词两处内联复制（参数序漂移温床） | ✅ |
| ITM-553 | StoreAs 枚举存储策略在 FromContext 未读取（enum 恒 TEXT） | ✅ |
| ITM-554 | ComputedAttribute 裸串比对未走 IsPalORMAttribute（一致性） | ✅ |

## 下沉审查（P1/P2）
| ID | 下沉判定 | 防线 | 状态 |
|:--:|---------|------|:--:|
| ITM-546 | 可下沉 | FormattableSqlFormatter 单测（含 @p 的邮箱/LIKE 不抛） | ✅ |
| ITM-547/548 | 需行为测试 | 拦截器 begin/end 配对测试 | ✅ |
| ITM-550 | 可下沉 | HasCharColumn 补 System.Char + 生成测试 | ✅ |
| ITM-512(前轮) | 可下沉 | AnalyzerDiagnosticsTests 补 DataAnnotations 混挂快照 | ✅ |

## 进度追踪
| 优先级 | 总数 | 已完成 |
|:------:|:--:|:--:|
| P1 | 1 | 1 |
| P2 | 3 | 3 |
| P3 | 5 | 5 |
| **合计** | **9** | **9** |
