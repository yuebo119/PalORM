# ADR-D：多程序集实体布局的官方支持范围（已裁决 D3）

> 状态：**已裁决 D3（降级告警）**（2026-09-02 用户确认，随评审循环闭环落档）
> 来源：ITM-420（2026-07-18 评审轮）
> 关联：PALORM003（[ForeignKey] 引用未知表）、PalORM_Runtime.Register 片段合并
> 实施证据：2026-09-02 评审整改第一批——PALORM003 默认严重度 Error→Warning + 消息
> 文案"this can be a false positive if the entity lives in a referenced assembly"，
> 即本 ADR D3 选项的语义（"无法在本程序集验证，运行时自负"）。

## 背景

运行时明确支持多程序集片段合并：每个模型程序集经 `[ModuleInitializer]` 调用
`PalORM_Runtime.Register(fragment)`，注册表原子合并（AotModels.First/Second 已验证）。

但编译期 PALORM003 只扫描**本程序集**的 [Table] 表名（`GetAssemblyTableNames`）：
程序集 A 的实体 `[ForeignKey("b_table", ...)]` 引用程序集 B 的表时，Error 级诊断直接拦死——
运行时支持的布局在编译期被禁止，两层承诺不一致。

## 备选方案

| 方案 | 内容 | 代价 |
|------|------|------|
| D1：官方支持跨程序集 FK | PALORM003 扩为扫描全部引用程序集的 [Table]（`compilation.References` 元数据符号遍历） | Analyzer 增量性能面；引用程序集元数据属性读取的复杂度 |
| D2：不支持，文档声明 | PALORM003 保持现状；文档明确"FK 引用限本程序集，跨程序集布局请勿使用 [ForeignKey]" | 运行时能力被编译期限制收窄；已有多程序集用户需绕行 |
| D3：降级告警 | 跨片段引用查不到时降 Warning（"无法在本程序集验证，运行时自负"） | 拼写错误的表名从 Error 降级，防护变弱 |

## 裁决

**D3（降级告警），且已随 2026-09-02 评审整改第一批实施完毕**：

1. PALORM003 默认严重度由 Error 复议为 Warning，消息注明"实体位于引用程序集时可为误报"
   ——与本草案 D3 选项的语义逐点一致（草案推荐 D3；实施在裁决落档前先行，本节为追认）。
2. 防护权衡的接受理由：同程序集拼写错误从 Error 降为 Warning 是 D3 的既定代价，
   由消费者侧 TreatWarningsAsErrors 兜底；跨程序集场景不再拦死，与运行时片段合并
   承诺对齐。
3. **D1 保留为未来选项**：确有"跨程序集 FK 编译期验证"需求时按备选表 D1 行评估
   （`compilation.References` 元数据符号遍历），届时需重跑 ITM-612 同款性能面评估。
   零需求信号期间不做。
4. 文档面同步：API 参考 PALORM003 行、本 ADR、分析器注释三处引用一致。

## 原草案存档（2026-07-18，ITM-420）

原文以"草案 · 待裁决"状态存续 46 天，推荐 D3；"需要的决策"一节原文为：
"多程序集实体布局是否官方支持场景？裁决后按上表实施对应方案。"
