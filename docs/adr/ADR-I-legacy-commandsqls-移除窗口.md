# ADR-I：legacy CommandSqls 双份生成的移除窗口

> 状态：**已修订**（2026-09-02 架构评审批次提前实施移除，原 v6.0 窗口裁决见文末存档）
> 关联：src/PalORM.SourceGen/RegistryEmitter.cs · DataSession.Crud.cs GetCommandSqls · CrudMetadata

## 背景

RegistryEmitter 同时生成 legacy `CommandSqls`（无方言、标识符未转义）与
`CommandSqlsByDialect`（三方言）双份 SQL 常量。运行时 `GetCommandSqls` 对 legacy 路径
**显式拒绝**（ITM-580：未转义标识符对保留字/特殊字符产生错误语句，抛
"recompile against the current version"）。legacy 仅服务于"旧版本生成器编译的模型程序集"
过渡期，其存在使生成物体积双倍、消费约束仅靠注释。

## 决策（2026-09-02 修订）

**legacy 生成段随本评审批次移除**（原裁决绑定 v6.0，提前理由见下）。移除内容：

1. 生成器不再发射 legacy `CommandSqls` 字典与 7 个 legacy const（Quote 恒等函数删除，
   方言参数必填）——方言 SQL 成为唯一真源。
2. `RegistryFragment.CommandSqls` 由 required 放宽为可选（默认空集）——旧版本生成器
   片段仍可注册（载荷保留、运行时继续不消费）；键校验由 ValidateRequiredKeys 放宽为
   ValidateOptionalKeys。
3. `CrudMetadata` 新增不含 SQL 载荷的推荐 ctor；含 `CommandSqlSet` 的旧 ctor 保留
   （旧生成片段的二进制兼容），字段标注为"仅兼容载荷、从不消费"。
4. `DeleteAsync` 的存在性检查由 `_commandSqls` 改走 `_crudMetadatas`（与 Insert/Update/
   Save 对齐）；`GetCommandSqls` 删除 fallback 形参。

**提前于 v6.0 的理由**（相对原裁决的输入变化）：

- 原裁决评估的收益仅"体积"；评审 2026-09-02 指出 legacy 路径还是**错误形态的活体样本**——
  关键字列名（如 `order`）在 legacy SQL 中恒为非法语句，与方言路径行为不一致，且
  DeleteAsync 曾以它作存在性代理（语义误导面），消灭比封存更符合项目的 fail-closed 纪律。
- 本项目已在同一 [未发布] 批次消化多项破坏性行为变更并逐条 CHANGELOG 注明（v5.x minor
  内消化先例：IDbProvider 签名扩展 r7-O1），原"v6.0 语义化窗口"的前提已与既定实践不一致。
- 混合场景影响收敛："旧运行时 + 重编译模型程序集"从"注册成功、CRUD 时抛 recompile
  错误"变为"注册时抛键集校验错误"——两者均为响亮失败，后者触发时点更早、用户正处于
  主动升级动作中，无静默数据风险。

## 后果

- 生成物体积下降（每实体一组 7 常量 + 一个字典项）。
- 旧运行时 + 新生成片段的组合在注册期明确失败——CHANGELOG 注明重编译模型程序集时
  需同步升级 PalORM.Core/Provider 包。
- 本批次移除 PR 已含快照 diff 审阅 + Core/SourceGen 测试全绿（快照即防回退断言）。

---

## 存档：原裁决（2026-08-15，评审 ITM-640/EVAL-3）

**v6.0 移除 legacy 生成段**，v5.x 保持现状（不加 Obsolete——它是 internal 生成物，
非公共 API，没有消费者能"被通知"，提前移除的收益仅在体积）：

1. v5.0 起已有运行时硬拒绝（GetCommandSqls throw），真实混合场景（新运行时 + 旧模型
   程序集）在 v5.0 发布时即已失败——legacy 常量至今只服务"旧运行时 + 新模型程序集"
   反向混合，该场景随 v5.x 用户全部升级自然消失。
2. 移除时机绑定 v6.0（下一个 major，破坏性变更的语义化窗口）。
3. 移除时同步：RegistryFragment 键校验（ValidateRequiredKeys 对 CommandSqls 的必选
   键集合）、API 参考文档、快照基线一次性刷新。

原后果：v5.x 期间生成物体积冗余维持（每实体一组常量字符串，实测可接受）；
v6.0 移除 PR 必须含快照 diff 审阅 + 三套测试全绿（快照即防回退断言）。
