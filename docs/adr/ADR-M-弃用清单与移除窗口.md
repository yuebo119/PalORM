# ADR-M：弃用清单与移除窗口

- 状态：已采纳（2026-09-20，独立审计 M2-6 裁决）
- 背景：公共 API 上存在"自陈不生效/无消费者"的成员（独立审计 A4），须登记移除窗口而非无限保留

## 弃用候选（3.0 移除，2.x 期间保持可用不标注 Obsolete——避免破坏现有编译）

| 成员 | 自陈位置 | 弃用理由 | 移除版本 |
|------|---------|---------|---------|
| `DbOptions.NamingConvention` | DbOptions.cs:78（ApplyNaming 有实现但生成侧不消费该映射） | 设置后静默无效——生成器在编译期按注解映射，运行时选项不参与 | 3.0 |
| `DbOptions.PoolExplicitlyConfigured` | DbOptions.cs:66（v5.6 起无消费者，仅 WithPool 内部置位） | 公共面暴露内部状态标记 | 3.0（内部化为 private set） |
| `QueryBuilder.WithMetrics(string name)` 的 name 参数 | 参数校验后丢弃（指标恒为 operation 级） | 见 M2-6/QW-5——若 3.0 前补 label 支持则保留 | 3.0 决定 |

## 保留（有 ADR 依据，不弃用）

| 成员 | 依据 |
|------|------|
| `RegistryFragment.CommandSqls`/`CreateTableSql` | ADR-I/ADR-J 登记的移除窗口（legacy 双轨收敛计划内） |
| `IRowFactory<T>` | ADR-H：PALORM900 已标注，按既有计划走 |

## 与 CHANGELOG 的关系

3.0 发布时本清单转为 CHANGELOG 的 breaking changes 段；2.x 期间本文件是唯一真源。
