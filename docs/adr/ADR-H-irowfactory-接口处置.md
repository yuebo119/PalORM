# ADR-H：IRowFactory 接口处置

> 状态：已裁决（2026-08-15，评审 ITM-640/EVAL-2）
> 关联：src/PalORM.Core/IRowFactory.cs（17 行）

## 背景

`IRowFactory` 是 v1 预留的行物化抽象接口，全仓库零实际实现与消费（grep 反证：仅注释
与自身定义；物化实际由 SourceGen 生成的 `RowFactory_*` 静态类承担，不经过该接口）。
评审架构流卡命中"死接口成员有 Obsolete+期限吗"。文档曾声明"保留以兼容潜在的外部实现"。

## 决策

**在 v6.0 移除，v5.x 标 Obsolete 预告**——不采用"永久保留"：

1. 零消费 + 零实现 = 不存在"兼容既有外部实现"的负担窗口；任何外部潜在实现同理零负担。
2. 保留成本真实存在：公共 API 面积（NuGet 消费者可见）、维护时的死代码噪声、
   下轮评审会再次命中同一疑点。
3. 语义上 SourceGen 物化路径已定型（编译期生成 + 零反射，AOT 契约），运行时接口
   注入物化与该架构方向相悖——"潜在用途"没有可预见的落点。

执行：v5.2 起 `[Obsolete("IRowFactory is unused and will be removed in v6.0", DiagnosticId="PALORM900")]`
（非 error 期），v6.0 移除并同步 API 参考计数。

## 后果

- 下一个 minor 版本实施 Obsolete 标注（本 ADR 只裁决方向，标注随 v5.2 发布做）。
- 若 v6.0 前出现真实用例（issue/用户反馈），重开裁决。
