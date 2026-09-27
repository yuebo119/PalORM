# ADR-M：弃用清单与移除窗口

- 状态：**已修订**（2026-09-28，v6.0 需求定稿裁决）
- 历史：已采纳（2026-09-20，独立审计 M2-6 裁决）；原文移除窗口写"3.0"系版本号笔误
  （裁决时项目已是 5.5.x，3.0 为已过去的历史版本），实际移除窗口即 **v6.0**。
- 关联：`docs/v6.0-requirements.md`（R5/R6）· `docs/v6.0-tasks.md`（Phase 3/5）

## v6.0 处置定案（2026-09-28 用户逐项裁决）

| 成员 | 处置 | 依据（2026-09-28 复核证据） |
|------|------|------|
| `DbOptions.NamingConvention`（属性 + `ApplyNaming` + `ToSnakeCase` + 枚举） | **v6.0 直接删除** | 设置后静默无效：`ApplyNaming` 有完整实现但全仓零消费方（生成器编译期按注解映射，运行时选项从不参与）。未预警破坏（从未标 Obsolete）由 README 迁移指南补偿说明：删除该设置即可，行为从未生效过 |
| `DbOptions.PoolExplicitlyConfigured` | **v6.0 直接删除** | 零读方死字段：仅 `WithPool` 两处置位，无任何读取方（`SqliteProvider` 历史消费方"见标记即抛"已移除，仅存注释）。连带删除置位行与历史注释段 |
| `QueryBuilder.WithMetrics(string name)` 的 name 参数 | **转正不移除** | 原文"若补 label 支持则保留"条款命中：v6.0 将 name 透传为 metrics tag（`palorm.query.name`），参数从"校验后丢弃"变为消费；XML doc 载明低基数警示（name 应为静态业务名，禁止拼接动态值） |

## 保留（有 ADR 依据）

| 成员 | 依据 |
|------|------|
| `RegistryFragment.CommandSqls` / `CreateTableSql` | ADR-I / ADR-J（legacy 生成段已提前移除，属性放宽为可选的既成事实保留） |
| `IRowFactory<T>` | 按 ADR-H 在 v6.0 移除（PALORM900 预告已满一纪元），执行见 v6.0 任务 T5.1 |

## 与 CHANGELOG 的关系

v6.0 发布时本清单处置转入 CHANGELOG 的 breaking changes 段与 Features 段；
本文件与 `docs/v6.0-requirements.md` §R6 共同构成删除项真源。
