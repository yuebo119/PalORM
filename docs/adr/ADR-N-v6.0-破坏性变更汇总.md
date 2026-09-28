# ADR-N：v6.0 破坏性变更汇总

- 状态：已采纳（2026-09-28，v6.0 需求定稿 R6 裁决）
- 关联：ADR-H（IRowFactory 处置）· ADR-M（弃用清单与移除窗口）· `docs/v6.0-requirements.md` §R6
- 本 ADR 是 v6.0 全部破坏性变更的汇总索引；各项的裁决依据在对应 ADR/需求定稿中。

## 变更清单

| # | 变更 | 类型 | 依据 | 影响面 |
|---|------|------|------|--------|
| 1 | 删除 `IRowFactory<T>` 接口 | 已预告删除（PALORM900，v5.2 起） | ADR-H | 零——全仓零实现零消费，预告已满一纪元 |
| 2 | 删除 `DataSession.DiffAsync<T>` | 已预告删除（PALORM901，v4.0 起） | v4.0 裁决 | 零——ValidateSchemaAsync 的薄包装；替代：`ValidateSchemaAsync<T>()` 后自行加前缀 |
| 3 | 删除 `DbOptions.NamingConvention`（属性 + ApplyNaming + ToSnakeCase + 枚举） | 未预告删除 | ADR-M 修订 + 用户裁决（2026-09-28） | 设置过该项的用户升级后编译错——**删除该设置即可，行为从未生效过**（生成器编译期按注解映射，运行时选项零消费方）；要改列名用 `[Column("...")]` |
| 4 | 删除 `DbOptions.PoolExplicitlyConfigured` | 未预告删除 | ADR-M 修订 + 用户裁决 | 趋零——零读方死字段（读一个无语义标记的代码不存在）；`WithPool`/`PALORM_MAX_POOL_SIZE` 行为不变 |
| 5 | `ColumnAttribute.Length/Precision/Scale`：`int?` → `int`（0 = 未设置） | 签名修正 | R3 实施（CS0655 实证） | 趋零——`[Column(Length = …)]` 命名参数语法**此前从未可编译**（`int?` 不是合法特性参数类型）；唯一影响面是反射读取这三属性类型的代码（返回 `int` 而非 `int?`） |

## 决策要点

1. **未预告删除的补偿**（变更 3/4）：v6.0 是 major 窗口，但 NamingConvention 从未标过
   Obsolete——README 迁移指南显式列出（"删除该设置即可，行为从未生效过"），
   把未预警破坏的迁移成本压到一行删除。
2. **签名修正优于签名保持**（变更 5）：`int?` 形状是 ADR-B 时代的设计缺陷（语法层
   不可用），保持它等于保持一个永远无法工作的 API 形状；major 窗口修正。
3. **历史叙事注释保留**：5 处 v3.1/v5.6 注释提及已删成员（"从 IRowFactory 迁移到
   Func"等）保留——史实记录非活性引用；活性引用（代码/测试/API 面/文档表）零残留。

## 验证

- 全仓 grep `IRowFactory|DiffAsync|NamingConvention|PoolExplicitlyConfigured|PALORM900|PALORM901`
  仅剩上述历史叙事注释（口径：src/test/docs/README）。
- `TreatWarningsAsErrors` 下任何 Obsolete 残留引用必然编译错——build 0 警告即天然防线绿。
- 测试对账：Core 433→431、SourceGen 221→219（删除项的用例同步移除）。
