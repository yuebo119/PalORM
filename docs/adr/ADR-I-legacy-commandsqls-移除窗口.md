# ADR-I：legacy CommandSqls 双份生成的移除窗口

> 状态：已裁决（2026-08-15，评审 ITM-640/EVAL-3）
> 关联：src/PalORM.SourceGen/RegistryEmitter.cs:42-68 · DataSession.GetCommandSqls

## 背景

RegistryEmitter 同时生成 legacy `CommandSqls`（无方言、标识符未转义）与
`CommandSqlsByDialect`（三方言）双份 SQL 常量。运行时 `GetCommandSqls` 对 legacy 路径
**显式拒绝**（ITM-580：未转义标识符对保留字/特殊字符产生错误语句，抛
"recompile against the current version"）。legacy 仅服务于"旧版本生成器编译的模型程序集"
过渡期，其存在使生成物体积双倍、消费约束仅靠注释。

## 决策

**v6.0 移除 legacy 生成段**，v5.x 保持现状（不加 Obsolete——它是 internal 生成物，
非公共 API，没有消费者能"被通知"，提前移除的收益仅在体积）：

1. v5.0 起已有运行时硬拒绝（GetCommandSqls throw），真实混合场景（新运行时 + 旧模型
   程序集）在 v5.0 发布时即已失败——legacy 常量至今只服务"旧运行时 + 新模型程序集"
   反向混合，该场景随 v5.x 用户全部升级自然消失。
2. 移除时机绑定 v6.0（下一个 major，破坏性变更的语义化窗口）。
3. 移除时同步：RegistryFragment 键校验（ValidateRequiredKeys 对 CommandSqls 的必选
   键集合）、API 参考文档、快照基线一次性刷新。

## 后果

- v5.x 期间生成物体积冗余维持（每实体一组常量字符串，实测可接受）。
- v6.0 移除 PR 必须含快照 diff 审阅 + 三套测试全绿（快照即防回退断言）。
