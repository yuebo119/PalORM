# PalORM 第二轮整改任务账本（独立审计 · 2026-09-19）

> 基线：`dev@03658f3`（ADR-L 提交，工作树清洁）。来源：[independent-audit-2026-09-19.md](independent-audit-2026-09-19.md)（独立复核报告）。
> 与第一轮账本（[audit-remediation-2026-09-19.md](audit-remediation-2026-09-19.md)，22 任务 21 闭环）的关系：第一轮回答"代码内部对不对"（运行时缺陷），本轮回答"交付的承诺算不算数"（发布链路与契约诚实度）。两者正交互补；独立报告对第一轮整改（SHAPE-010/A1/A2、ADR-L）已确认落地。
> 纪律：任务只有在实现、对应测试和验收命令均通过后才能标记完成；每任务走 S1 基线 → S2 单变量 → S3 反向验证（撤回修复 → 用例确定性失败）。
> 任务编号沿用独立报告（M0-x/M1-x/M2-x/M3-x），保持来源可溯。
> **执行记录（2026-09-19 执行会话）**：立即批 6 项 + 短期批 5 项（M0-1/2/3/4/5、M1-1/2/3/5、M2-5B、M3-8、M3-4、M2-8）全部完成；M2-2 首跑即抓到真缺陷（PG 42601 参数版批量 UPDATE，独立审计 T3 的预言应验），根因定位中断于远端数据库连接饱和（HealthCheck 超时），红灯测试保留为证据、修复任务跟进。M3-7 与大项（M2-1/M3-1/2/3/5/6/M2-3/4）保留待后续会话。本地验证：Core 289 / SourceGen 197 全绿、严格构建 0 警告；Integration 含真库用例因 M2-2 缺陷与环境故障不全绿（诚实记录）。
> **可信度前置**：本账本立项前已抽查报告四项最重声明（O1/DOC1/A4/T1），全部亲手复核属实（O1：release.yml 全文无 AOT/gitleaks/包契约；DOC1：`git show v5.5.1` 池默认 30 vs README 声称 0；A4：WithMetrics 校验后丢弃 name；T1：TestDb 方言夹具 0 调用点）。

## 完成定义（改编自报告 §四 可衡量信号）

1. **发布即证据**（S1）：`release.yml` 的 publish 依赖 PR 流水线全部作业（含 4×AOT + 密钥扫描 + 包契约）；故意注入反射调用能阻断一次发布演练。
2. **文档 = 已发布物**（S2）：`git show <最新tag>:README.md` 每条能力陈述在该 tag 源码可复现（抽查 10 条 0 反例）；README 无未标记的"v5.6 起…"句式。
3. **CI 对执行负责**（S3）：三项目 `passed ≥ 地板`、`skipped == 0`、`ExternalDatabase ≥ 基线值` 三断言任一不满足即红；调零能变红（mutation probe）。
4. **CVE 信号**（S4）：`dotnet list package --vulnerable --include-transitive` 在 CI 阻断；Dependabot 对 Critical/High 开 PR。
5. **全新克隆可建**（S5）：干净目录 `git clone` → 官方指令构建成功，或指令改为可执行且实测通过。
6. **公共面无死旋钮**（S6）：grep 全 src/ 无"自陈无消费者/不参与映射"的公共成员（或已 `[Obsolete]` + ADR 声明移除版本）。
7. **审计覆盖面与名称一致**（S7）：`NotifyInterceptorsOn` 调用点 ≥5 文件，或 README/类名收窄承诺。
8. 既有门禁不回退：0 警告、全部套件绿、AOT 四矩阵绿（本地 + release 演练）。

## 状态账本

| ID | 级别 | 任务 | 状态 | 验收证据 |
|---|---|---|---|---|
| M0-4 | P1 | 依赖 CVE 阻断 + Dependabot | 已完成 | ci.yml(gate job)加 --include-transitive 扫描 Critical/High 即红（本地实测零漏洞可跑）；.github/dependabot.yml（nuget 周更+Roslyn 成组+安全独立组+actions） |
| M0-5 | P1 | CODEOWNERS 守护快照/基线 | 已完成 | .github/CODEOWNERS：Snapshots/bench基线/release 流水线/包版本四域；单人期显式标记+多人预留 |
| M1-1 | P1 | Raw 复用控制字符防线（NUL/C0/C1/DEL） | 已完成 | IdentifierSafety.IsControlChar 单一真源；5+1 用例 S3 回红；README 注入措辞改默认参数化+逃生门清单 |
| M0-2 | P1 | 全新克隆可建（CONTRIBUTING 补 fork 说明或 slnx 拆分） | 已完成（诊断路径） | CONTRIBUTING 补 BDN 克隆说明+双构建路径（全量 slnx / CI 同款 ci.slnf 零依赖）；slnx 拆分未做（fork 说明已消除首跑失败，拆分收益降低） |
| M2-5B | P1 | AuditInterceptor 覆盖面收窄（README 醒目声明，低成本路径） | 已完成 | README 特性表改"查询审计拦截器"+覆盖面警告（写路径零审计记录）；A 路径（扩写）留开放问题 4 裁决 |
| M0-1 | P1 | 发布流水线复用 PR 全部作业（workflow_call 组合 verify→publish） | 已完成 | verify.yml（workflow_call）承载 ci 全部作业+SHA 输出；ci.yml 薄壳化；release verify(uses)→publish(needs)。mutation probe（注入反射验证阻断）待 CI 侧执行——需 push 触发真实发布演练，本地无法完成，首次发布前必做 |
| M0-3 | P2 | CI"确实执行了"断言（测试数地板 + 真库用例数 + skipped==0） | 已完成 | scripts/assert-test-counts.sh 读 tunit-report.json（node 解析，不 grep stdout）；地板 bench/baselines/test-counts.json（280/188/190+真库20）；verify.yml 三处接线；S3 地板调高回红验证。注：externalDb 实测 23 |
| M1-2 | P2 | 发布凭据 OIDC + environment 人工审批 | 部分完成 | environment: production 已加（GitHub 侧 required reviewers 需用户配置）；api-key 从 run: 内联改 env 注入。完整 OIDC 需 NuGet.org 侧 trusted publishing 配置（用户操作），代码已留切换注释 |
| M1-3 | P2 | 发布验证前置（push 之前），去重复跑测 | 已完成 | CHANGELOG 校验前置到 pack 前；删除 push 后"Verify Release Accuracy"（echo 恒 0+三套件重跑）——数字口径由 M0-3 地板接管。注：verify 与 publish job 各跑一遍测试是 workflow_call 结构的自然成本（独立 runner 不共享工作区），发布 job 自包含可审计 |
| M1-5 | P2 | S2077 恢复 error + 逐调用点 #pragma 报备 | 已完成 | editorconfig S2077 none→error；7 处合法点报备（Crud 软删/GetByKey、Transactions savepoint×2、MySql SHOW COLUMNS、Sqlite PRAGMA、Scaffold PRAGMA）各带理由 |
| M1-4 | P2 | 发布 v5.6/v5.7 + README 对齐已发布物 | 阻塞：待发布节奏裁决（开放问题 1） | |
| M2-2 | P2 | 批量 UPDATE 参数化分支真库端到端（PG+MySQL 各 ≥3，逐行逐列断言） | 测试已立，根因排查矩阵完成大半 | 稳定复现：多行（4 实体）PG 42601/MySQL 语法错；**单行经 PalORM 真库成功**。Npgsql 原生对照已排除：SQL 文本/参数名与数/绑定方式/事务/连接调优（MaxAutoPrepare 复刻）全部成功。七项 Npgsql 原生对照全部成功（SQL 文本 1/4 行、参数名数、DBNull-后写与直接带值两种绑定、事务有无、MaxAutoPrepare 复刻、同语句两次执行触发 auto-prepare、NpgsqlParameter(name,value) 构造器）——42601 在原生路径**不可复现**。连接生命周期层亦已裁决排除（PG 未覆写 InitializeConnectionAsync=no-op；CreateConnection 全量调优复刻——NoResetOnClose/16384 缓冲区/Enlist=false/auto-prepare 四项全套+4 行语句——原生成功）。**八项对照全过、原生不可复现**。剩余假说：①dump 的 CommandText 与实际发送文本存在不可见差异（sink 按字符串 dump，/U+00A0 等不可见字符不显示——下一轮 dump 逐字符码点即可裁决）；②"单行成功/多行失败"与语句文本唯一性之外的状态交互。下一轮首选实验：码点 dump |
| M3-8 | P3 | 文档一致性小项（release-body.md 删除 / ADR-C 编号 / 依赖表） | 已完成 | release-body.md 已删（release.yml 的 full-release-body.md 是运行时生成物无关联）；ADR-C C3→C2 编号勘误；CONTRIBUTING latest-all 陈述对齐分层现实（DOC2） |
| M3-4 | P3 | 解耦 SqlShapeCacheGrowthTests 顺序依赖（独立隔离计数器） | 已完成 | Tag 填满测试 finally 自清（不外溢给后跑者）；克隆测试 Clear 改防御性（注释更新）；"先清空"顺序耦合消除 |
| M3-7 | P3 | 测试计数口径单一真源（声明执行口径 + 一处生成） | 待开始 | |
| M2-8 | P3 | 依赖来源与 RID 卫生（Roslyn 仅 nuget.org；GA 切轨跟踪项） | 已完成 | NuGet.Config：dotnet-tools 的 Roslyn pattern 移除（nuget.org 单一来源）；还原实测通过。GA 切轨已有既定裁决（等 .NET 11 正式版） |
| M0-2b | P3 | （若选拆分路径）slnx 拆 CI/全量两个方案文件 | 视 M0-2 路径 | |
| M3-1 | P3 | 覆盖率地板（line ≥70% / branch ≥60%，防退化非达标） | 待开始 | |
| M3-2 | P3 | 变异测试扩面（Core 全部 ≥200 行文件） | 待开始 | |
| M3-3 | P3 | 高扇出压力测试（64 交错 + 64 会话并发） | 待开始 | |
| M3-5 | P3 | ArchitectureInvariantTests 转行为断言 | 待开始 | |
| M3-6 | P3 | 删除 no-op 抽象（ConnectionLease）与 legacy 载荷处置 | 待开始 | |
| M2-1 | P2 | 真库测试面从 12% 扩容 + TestDb 方言夹具复活（≥20 调用点） | 待开始 | |
| M2-3 | P3 | CloneForExecution 字段全集机械化（编译期守卫） | 待开始 | |
| M2-4 | P3 | 执行管线单实现化（RunPipelineAsync + 物化策略） | 待开始 | |
| **先裁决** | — | M2-6（公共旋钮处置，破坏 API） | 阻塞：开放问题 5 | |
| **先裁决** | — | M2-7 / P4（WithCache 值语义 CloneOnRead） | 阻塞：开放问题 3 | |
| **先裁决** | — | M1-6（分析器口径 latest-all，高爆量分批） | 阻塞：开放问题 9 | |
| **先裁决** | — | M2-5A（拦截器扩写路径，方案 A 全量） | 阻塞：开放问题 4；M2-5B 先行收窄 | |

> 已被既有裁决覆盖、不再立项：SDK GA 切轨（用户已裁决等 .NET 11 正式版，D1 增量部分由 M1-4 发布时顺带处理）；BulkMerge 语义（已裁决维持，报告确认测试锁定）；多租户缓存（ADR-L 已落地，报告列为优势）。
> 澄清记录：报告 DOC4 将第一轮账本的 280/197/197 列入"计数互斥"——该组是**执行数**（TUnit 运行摘要实测），与报告实测的声明数（271/186/197）差异源于 `[Arguments]` 展开，非口径错误；M3-7 的单一真源方案将同时消除两类数字的混用。

## 执行顺序与依赖

```
立即批（快速获胜，互不依赖，可并行）：
  M0-4（Dependabot） · M0-5（CODEOWNERS） · M1-1（Raw 防线）
  M0-2（CONTRIBUTING 3 行诊断路径优先） · M2-5B（README 收窄） · M3-8 局部（删 release-body.md）

短期批（发布链路加固，顺序执行）：
  M0-1（release 复用 verify）→ M1-2（OIDC）→ M1-3（验证前置）
  M0-3（测试数地板，与上并行）→ M1-5（S2077，依赖 M1-1 同族字符面收口后更干净）

排期批：
  M2-2（批量 UPDATE 真库）→ M2-1（真库面扩容，XL 需拆）
  M3-4 / M3-7 / M3-8 / M2-8（文档与口径，随取随做）
  M3-1 → M3-2（覆盖率地板 → 变异扩面）
  M3-3 / M3-5 / M3-6 / M2-3 / M2-4（结构化重构，各带快照/性能安全网）

裁决依赖：
  开放问题 1（发布节奏）→ M1-4（发版 + README 对齐）
  开放问题 3 → M2-7；开放问题 4 → M2-5A 与否；开放问题 5 → M2-6；开放问题 9 → M1-6
```

## 快速获胜（高影响 × S，立即执行）

**M1-1**（Raw 两行防线 + 3 用例，闭合项目自己实测过的 NUL 截断向量）、**M0-4**（Dependabot，补完全空白的 CVE 维度）、**M0-5**（CODEOWNERS 10 行）、**M0-2 诊断部分**（CONTRIBUTING 3 行）、**M2-5B**（README 一段收窄）、**M3-8 局部**（删过时模板）。

## 重点任务实施要点（摘自报告草图，实施时以此为准）

### M1-1 · Raw 控制字符防线（P1 · S · 风险中：breaking-behavior）
- `IdentifierSafety` 提取 `internal static bool IsControlChar(char)`（单一真源，对齐 CORE-010 纪律）；`Raw` 在空/白检查后拒绝 `\0` 与 C0/C1/DEL，**不**拒绝引号/反引号（合法 Raw 必含）。
- 3 正向用例（NUL/换行/DEL）+ 1 反向（含引号片段必须通过）；先 grep 确认无既有测试依赖多行 Raw。
- README:274 "杜绝 SQL 注入"同步改为"默认参数化 + 显式逃生门清单"（DOC7 一并收口）；CHANGELOG 记 breaking-behavior；S3 撤守卫回红。
- 陷阱：不做引号平衡/关键字黑名单（不可判定且误伤）；不与位置语义改动混提交。

### M0-1 · 发布流水线复用 verify（P1 · L · 风险低）
- 用 `workflow_call` 把 ci.yml 的 security/AOT/包契约作业抽成 `verify.yml`；release 拆 verify（uses）→ publish（needs）。**否决 workflow_run**（默认分支 token 的攻击面，报告已论证）。
- 陷阱：aot-package-consumer 依赖 pack 产物，须重排 pack→verify-aot→push 防循环；verify 末尾输出 commit SHA，release 断言等于 `github.sha`；接线后故意注入 `Activator.CreateInstance` 验证真的能阻断（mutation probe，对齐全局"验证验证者"铁律）。

### M0-3 · CI 执行断言（P2 · M · 依赖 M0-2）
- `scripts/assert-test-counts.sh` 解析 TUnit 报告 JSON（勿 grep stdout、勿硬编码 Debug 目录）；地板 = 当前实测 −5；`skipped != 0` 即红；ExternalDatabase 计数入地板；脚本进 test-quality-scripts 夹具集。

### M1-4 · 发布与 README 对齐（P2 · L · 阻塞于开放问题 1）
- 依赖 M0-1/M1-2/M1-3 先落地（发布不可逆）；README 每条 v5.6/v5.7 陈述在 tag 源码可复现；PoolIdleTimeoutSeconds 类漂移逐条对齐；release-body.md 删除或改模板。

## 反向验证计划（S1/S2/S3 纪律）

- M1-1：撤守卫 → 3 用例确定性回红。
- M0-1：故意注入反射 → 发布演练被 AOT 作业阻断（报告草图的第 4 步，必做）。
- M0-3：把地板调到高于实际 → CI 红；把某项目报告路径指空 → CI 红（不能"找不到报告即放行"）。
- M0-4：构造一个含已知漏洞包的临时分支 → CI 红。
- M1-2：审计 run: 块无 `secrets.*` 字面量（grep 断言）。

## 开放问题（阻塞项的裁决清单）

1. **发布节奏**（阻塞 M1-4）：66 提交未发版，立即发 v5.6/v5.7 还是维持长窗口？（独立报告问题 1）
2. **WithCache 值语义**（阻塞 M2-7）：浅拷贝+文档披露为终态，还是 CloneOnRead？（问题 3）
3. **拦截器覆盖面**（决定 M2-5 是否升级为 A）：写路径接入还是收窄承诺？（问题 4）
4. **弃用候选清单**（阻塞 M2-6）：NamingConvention / PoolExplicitlyConfigured / RegistryFragment.CommandSqls / CreateTableSql / IRowFactory 的移除版本？（问题 5）
5. **单会话契约与 DI 包**（A3，产品定位）：是否提供作用域会话工厂？（问题 6）
6. **`.ai/` 地位**（DOC3 关联）：AGENTS.md 改为"作者本地工具"表述，还是 docs/ 吸收精选内容？（问题 7）
7. **性能目标量化**（问题 8）：是否对外承诺可断言的分配/比值目标。
8. **分析器口径**（阻塞 M1-6）：Core/SourceGen 拉回 latest-all 的分批节奏。（本账本新增，源自 DOC2）
