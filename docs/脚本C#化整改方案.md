# 脚本 C# 化整改方案（2026-09-29 定稿，v3 论证修订）

> 用户决策一（2026-09-29）：当前项目尽量替换成 C#，以后做脚本能用 C#/.NET 就使用 C#，尽量避免引入其他语言；基于 .NET 10 起的 file-based app（`dotnet run app.cs`）脚本化。
>
> 用户决策二（2026-09-29 v2）：非 C# 脚本若确有必要必须征得用户同意方可存在；C# 脚本做 file-based app 还是传统项目，按真实场景逐个裁定，不一刀切。
>
> 用户决策三（2026-09-29 v3）：方案再次充分论证，给出最佳实践与最优方案。本轮以三个可执行探针把 v2 的推断项全部转为实测（第 2 节），逐决策点自我反驳（第 11 节），产出 4 项修订与 1 组最佳实践（第 8 节）。

## 1. 事实基线（2026-09-29 盘点，git ls-files 与逐文件核读）

**范围**：15 个 `.sh`（约 1740 行）+ 1 个 Node `.mjs` + 1 个 git hook 薄包装。仓内无 Python/Ruby/Perl。`tools/` 下 PerfGate 与 Scaffold 已是 C# 项目，均收入 PalORM.ci.slnf（享受 0 警告 0 错误门禁）。

| 脚本 | 行数 | 职责 | 现有调用方 |
|------|------|------|-----------|
| scripts/secret-guard.sh | 295 | 敏感信息拦截 v3，40 类检测 | .githooks/pre-commit、verify.yml（2 处） |
| scripts/stub-check.sh | 36 | 占位实现扫描 | .githooks/pre-commit、verify.yml |
| scripts/assert-test-counts.sh | 51 | TUnit 报告计数地板断言 | verify.yml（3 个测试 job） |
| scripts/assert-coverage.sh | 20 | 覆盖率 XML 地板断言 | verify.yml |
| scripts/lib/parse-coverage.mjs | 15 | 覆盖率 XML 解析（Node） | 仅 assert-coverage.sh |
| scripts/test-package-contract.sh | 157 | NuGet 包依赖契约验证 | verify.yml |
| scripts/release-version-scan.sh | 80 | 升版本旧号残留扫描 | 发布 SOP §1.3（人肉触发） |
| scripts/pre-release-check.sh | 61 | 发布预检，模拟 CI 消费者路径 | 发布 SOP §4.1 第 0 步（人肉触发） |
| scripts/set-test-env.sh | 50 | .env.test 加载（source 形态导出变量） | perf.sh 内部 source、CONTRIBUTING 提及 |
| scripts/perf.sh | 246 | 性能测评统一编排入口 | AGENTS.md 性能汇报规范、docs/性能基准规范.md |
| scripts/perfhub-ab.sh | 92 | 交替 A/B 编排器 | perf.sh compare 转发 |
| scripts/run-full-perf.sh | 108 | 微基准编排步骤 | perf.sh full 第 1 步 |
| scripts/dappersuite-run.sh | 65 | Dapper 官方套件逐方言跑测 | perf.sh full 第 3 步 |
| scripts/run-benchmarks.sh | 155 | BDN 完整矩阵运行 | perf-gate.yml 注释提及（人肉触发） |
| scripts/run-mutation-tests.sh | 46 | Stryker.NET 变异测试 | mutation-tests.yml 未调用（人肉触发） |
| scripts/test-quality-scripts.sh | 239 | 质量脚本自身回归夹具 | verify.yml |
| .githooks/pre-commit | 26 | pre-commit 薄包装，转发到 scripts/ | git config core.hooksPath |

**引用半径**：CI 9 处调用（全在 verify.yml）；docs 约 20 处；AGENTS.md 4 处；CONTRIBUTING 1 处；CHANGELOG 为历史记录不改。**外部工具依赖**：grep/sed 文本处理为主，Node 仅 parse-coverage.mjs 一处，无 jq/curl/psql/mysql CLI。CI 13 个 job 全 ubuntu-latest，除首个 secret-scan job 外均已装 setup-dotnet。**运行时**：global.json 钉 SDK 11.0.100-preview.6（本机实跑 rc.1），file-based app 需 10.0.100+，无需版本动作。

## 2. 探针实测记录（v3 新增，2026-09-29 本机 SDK 11.0.100-rc.1，探针文件已删、隔离配置已入库）

| # | 实验 | 结论 |
|---|------|------|
| P1 | 零配置脚本在仓内直跑 | 根 props 全量穿透 file-based app：CS1591（根开文档生成且 NoWarn 只豁免 .Tests/Benchmarks 条件组）+ CA1050/CA1515/CA1707（AnalysisLevel latest-all）+ S3903（根 SonarAnalyzer 注入）全部升格 error；冷跑 15.2 秒 |
| P2 | 隔离配置生效后 | scripts/Directory.Build.props + Directory.Build.targets 双文件生效：0 警告 0 错误；冷跑 1.3 秒（关分析器带来约 10 倍提速）；热跑 0.14 秒 |
| P3 | 退出码穿透 | 脚本 `return 42` → 进程 exit=42，干净传播 |
| P4 | CPM 继承 | `#:package Dapper@2.1.89` 显式版本触发 NU1008（"使用中央包管理的项目必须在 PackageVersion 项上定义版本值"） |
| P5 | CPM 逃生通道 | 脚本头部 `#:property ManagePackageVersionsCentrally=false`（虚拟项目体最后求值）必赢：NU1008 解除、包正常还原、exit=0 |
| P6 | 评估顺序陷阱 | 根 props 的 Sonar 注入 ItemGroup 在 props 解析期求值；隔离 props 内 CPM=false 无论放 Import 前/后都未阻止注入（机制未完全定论，候选 .ai/lessons 登记）；targets 恒晚于全部 props，`PackageReference Remove` 是实证有效的确定性手段 |
| P7 | BaseDirectory 陷阱 | `AppContext.BaseDirectory` 指向 `Temp\dotnet\runfile\<内容哈希>\bin\debug\`（构建缓存）而非脚本目录；CWD 保持调用方目录。仓库根定位必须从 CWD 向上找哨兵文件 |

## 3. 技术定案（官方文档逐条核实 + 探针实证）

| 定案 | 内容 | 依据 |
|------|------|------|
| D1 调用约定 | 形态 A `dotnet run --file scripts/&lt;名&gt;.cs -- &lt;参数&gt;`；形态 B `dotnet run --project tools/&lt;项目&gt; -- &lt;子命令&gt;`，本地与 CI 同形 | 官方 CLI 节；--file 消除 CWD 含 csproj 时的退化传参歧义 |
| D2 不用 shebang | 不依赖 `#!/usr/bin/env dotnet` 直执行 | 仓内无 .gitattributes，Windows 检出 CRLF，shebang 直执行在 Linux 会破；dotnet run 形态对行尾免疫 |
| D3 scripts/ 双文件隔离 | props（Import 前置 CPM=false + Import 后覆写 GenerateDocumentationFile/EnableNETAnalyzers/IsAotCompatible/IsTrimmable/PublishAot=false）+ targets（Remove SonarAnalyzer）；严格编译/Nullable/LangVersion/IDE 风格全继承保留 | P1/P2/P6 实测；两文件已入库即 Phase 0 T0-1 交付物 |
| D4 CPM 与包 | 纯 BCL 零 `#:package` 为目标（16 项全部可达）；确需包时脚本头加 `#:property ManagePackageVersionsCentrally=false` 后自钉版本 | P4/P5 实测 |
| D5 缓存与并发 | 默认单文件形态（缓存开）；禁 glob 形态 `#:include`（官方明示禁缓存）；并发调用先 build 再 --no-build；缓存位于用户 Temp，CI 每 job 冷态 | 官方 Build caching 节 + P2 |
| D6 退出码契约 | 0 过 / 非 0 分档失败，逐脚本在夹具显式断言；编排用 Process 透传，禁管道吞码（B104） | P3 实测 + B104 |
| D7 JSON | 脚本内 JSON 用 STJ source-gen（JsonSerializerContext） | 官方明示默认 AOT publish；与本仓 AOT 文化同构 |
| D8 输出契约不变 | stdout/stderr 格式、退出码、产物路径对调用方逐字节兼容；迁移轮不顺手改格式 | 外科手术原则 |
| D9 语言政策载体 | 非 C# 白名单制：唯一初始条目 .githooks/pre-commit（git hook 机制要求可执行脚本入口）；新增白名单必须在 docs/编码规范.md 登记理由并经用户同意；机械门禁 T6-3 | 用户决策二 |
| D10 仓库根定位 | 禁用 AppContext.BaseDirectory（P7 实测指向缓存）；从 Environment.CurrentDirectory 向上探测哨兵（PalORM.slnx/global.json） | P7 实测 |

## 4. 形态判定标准（用户决策二落地）

**形态 A：file-based app（scripts/*.cs）**，同时满足：单一职责单文件可容纳、无成体系共享基础设施、一条命令跑完、黑盒夹具可验证、不进 ci.slnf。
**形态 B：console 项目（tools/PalORM.*）**，任一命中：多入口共享成体系基础设施、活跃演化期且逻辑量超单文件舒适区、需进 ci.slnf 严格门禁、需被引用或白盒测试。
**形态 C：保留 bash 薄壳**：仅限技术约束，属 D9 白名单。

## 5. 迁移裁决表（17 项）

| # | 对象 | 形态 | 去向 | 场景判据 |
|---|------|------|------|---------|
| 1 | stub-check.sh | A | scripts/stub-check.cs | 36 行文本扫描，五判据全中 |
| 2 | assert-test-counts.sh | A | scripts/assert-test-counts.cs | 单命令 JSON 断言 |
| 3 | assert-coverage.sh + parse-coverage.mjs | A | scripts/assert-coverage.cs（合并） | XML 解析一体，Node 依赖消除 |
| 4 | secret-guard.sh | A | scripts/secret-guard.cs | 40 类 Regex 单命令；黑盒自测夹具守卫；迁移配全历史对拍（T2-4） |
| 5 | test-package-contract.sh | A | scripts/test-package-contract.cs | zip 读 nuspec 单命令 |
| 6 | release-version-scan.sh | A | scripts/release-version-scan.cs | 属性扫描 + git tag 推导 |
| 7 | pre-release-check.sh | A | scripts/pre-release-check.cs | 编排 pack/restore，链式调用 #5 |
| 8 | perf.sh | B | tools/PalORM.PerfCli（smoke/full/compare/gate/report/index） | 六子命令与 #9-12 共享成体系基础设施（判据 B1）、活跃演化（B2）、入 ci.slnf 得 0W0E 门禁（B3）。论证注记：CI 冷启动论据经反驳剔除（perf 家族 CI 零调用），结论由 B1-B3 独立支撑 |
| 9 | perfhub-ab.sh | B | tools/PalORM.PerfCli（compare） | 同上家族共享 |
| 10 | run-full-perf.sh | B | tools/PalORM.PerfCli（full-perf） | 同上家族共享 |
| 11 | dappersuite-run.sh | B | tools/PalORM.PerfCli（dappersuite） | 同上家族共享 |
| 12 | run-benchmarks.sh | B | tools/PalORM.PerfCli（bench-matrix） | 同上家族共享 |
| 13 | run-mutation-tests.sh | A | scripts/run-mutation-tests.cs | 独立 Stryker 触发，无家族共享 |
| 14 | set-test-env.sh | 淘汰 | env loader 并入 PerfCli | 仓内唯一 source 方 perf.sh 属 PerfCli；C# 进程无法向父 shell 导出变量（裁决点 1） |
| 15 | test-quality-scripts.sh | A | scripts/test-quality-scripts.cs | 多夹具单命令编排，排最后迁移 |
| 16 | .githooks/pre-commit | C | 保留 bash 薄壳 | git hook 机制要求（技术约束）；转发改形态 A 命令；D9 白名单唯一条目 |
| 17 | CI verify.yml 9 处 | 改造 | 按形态切换；secret-scan job 补 setup-dotnet | 唯一无 dotnet 的调用方 |

**范围外备案**：`.ai/scripts/`（本地工具不入仓库）与用户级 hook 不在本方案范围。此后新增脚本一律 C#（形态按第 4 节判据），非 C# 走 D9 白名单流程。

## 6. 迁移纪律

1. **S1 基线快照**：迁移前留该脚本真实运行输出或夹具断言作对拍基准。
2. **对拍验证**：新形态与旧 .sh 同输入双跑，比对退出码与 stdout 关键行；夹具双覆盖后先绿再删旧 .sh。
3. **单变量**：迁移轮不改检测规则、阈值、输出格式（D8）；疑似 bug 单开任务。
4. **三方一致**：删 .sh 的同一提交内 CI/docs/AGENTS/CONTRIBUTING/hook 全切换，grep 归零（CHANGELOG 除外）。
5. **锁定证据**：完成判据 = 成功夹具过 + 故障夹具（坏输入 → 非零退出）过。
6. **防线分级**：secret-guard 级（P0 载体）适用全历史对拍（见 T2-4）；普通脚本级适用单命令对拍。

## 7. 分阶段任务清单（估时合计约 9.5 个工作日，区间 7 至 12）

### Phase 0：基建与探针（已由本轮论证提前完成）

| 任务 | 状态 | 证据 |
|------|------|------|
| T0-1 scripts/ 隔离双文件 | done | props + targets 已入库，P2 全绿 |
| T0-2 CPM 探针 | done | P4 NU1008 实证 + P5 逃生通道实证 |
| T0-3 冷/热启动探针 | done | 隔离后冷 1.3s、热 0.14s（P2） |
| T0-4 样式告警探针 | done | P1 四类 error 实证，隔离后归零 |
| T0-5 调用约定定稿 | done | D1/D6 即定稿文本 |

### Phase 1：夹具先行（约 0.5 天）

| 任务 | 内容 | 验收 |
|------|------|------|
| T1-1 | test-quality-scripts.sh 增目标形态夹具骨架（形态 A 入口 + 形态 B 子命令的正反夹具位，red 状态） | skip 有明确标注不误报 |
| T1-2 | 夹具通道 mutation probe：改错一个预期值证明夹具能红 | 记录留痕 |

### Phase 2：提交防线四项（约 2 天，形态 A）

顺序：stub-check → assert-test-counts → assert-coverage（吞 mjs）→ secret-guard 压轴。

| 任务 | 内容 | 验收 |
|------|------|------|
| T2-1 | stub-check.cs + 对拍 + 删 .sh | 夹具双绿；verify.yml 223 行切换 |
| T2-2 | assert-test-counts.cs + 对拍 + 删 .sh | verify.yml 112/123/173 切换并实跑 |
| T2-3 | assert-coverage.cs（删 lib/parse-coverage.mjs） | Node 仓内调用归零；verify.yml 116 行切换 |
| T2-4 | secret-guard.cs + 对拍 + 删 .sh | **全历史双跑对拍**：新旧两版对 git log 全量 range 各扫一遍，输出逐字节 diff 为空为过；verify.yml 45/57 切换 + secret-scan job 补 setup-dotnet；本机 3 次真实 commit 实测 hook 延迟 ≤ 热跑基线 0.14s × 2 + 2s |
| T2-5 | 收口 grep：`.github/ scripts/ .githooks/` 仅剩计划内 | 0 意外残留 |

### Phase 3：发布链三项（约 1.5 天，形态 A）

| 任务 | 内容 | 验收 |
|------|------|------|
| T3-1 | release-version-scan.cs + 对拍（v6.1.0 tag 重放） | 结果一致；发布规范 §1.3 同步 |
| T3-2 | pre-release-check.cs + 对拍（6.1.0 正向全绿） | 发布规范 §4.1 同步；链式调用 #5 打通 |
| T3-3 | test-package-contract.cs + 对拍 | verify.yml 283 行切换实跑绿 |

### Phase 4：性能链 → PalORM.PerfCli（约 3.5 天；受裁决点 3 时序约束）

| 任务 | 内容 | 验收 |
|------|------|------|
| T4-1 | 建 tools/PalORM.PerfCli：纯 BCL、入 slnx + ci.slnf、走根 props | ci.slnf 全量 0 警告 0 错误 |
| T4-2 | 子命令 bench-matrix + 对拍 | BDN 矩阵触发与日志路径一致 |
| T4-3 | 子命令 full-perf + 对拍 | SKIP_REPORT 语义不变 |
| T4-4 | 子命令 dappersuite + 对拍 | 三方言串行一致 |
| T4-5 | 子命令 compare + 对拍 | worktree 切臂 + label 契约不变；并发按 D5 |
| T4-6 | 六子命令入口 + env loader 内置（吸收 set-test-env）+ 对拍 | 逐一冒烟；.env.test 缺失时 sqlite 照跑语义不变 |
| T4-7 | docs/性能基准规范 约 18 处 + AGENTS.md + CONTRIBUTING 同步；删六个 .sh | 性能链 .sh 引用归零 |
| T4-8 | run-mutation-tests.cs + 对拍 + 删 .sh | Stryker 触发一致 |

### Phase 5：夹具本体与 hook（约 1 天）

| 任务 | 内容 | 验收 |
|------|------|------|
| T5-1 | test-quality-scripts.cs（含形态 B 子命令夹具）+ 对拍 + 删 .sh | verify.yml 243 行切换实跑绿 |
| T5-2 | .githooks/pre-commit 终态全为形态 A 调用 | 本机 commit 全链绿，延迟记录 |

### Phase 6：政策固化（约 0.5 天）

| 任务 | 内容 | 验收 |
|------|------|------|
| T6-1 | 全仓终扫：非 C# 脚本仅剩白名单 1 项 | 与 D9 一致 |
| T6-2 | docs/编码规范.md 脚本章节（D9 政策 + 第 4 节判据 + 第 8 节最佳实践 + 形态 A 示例）；AGENTS.md 政策指针 | 含最小示例与判据表 |
| T6-3 | 语言门禁：白名单外新 .sh/.mjs/.py 即 FAIL | 植入假文件双向验证 |
| T6-4 | CHANGELOG 登记整改轮 | 含 17 项裁决摘要与白名单政策 |

## 8. C# 脚本最佳实践（v3 新增，探针实证支撑）

| # | 实践 | 依据 |
|---|------|------|
| B1 | 仓库根定位禁用 AppContext.BaseDirectory（指向 Temp 构建缓存）；从 CWD 向上探测 PalORM.slnx/global.json 哨兵；约定调用 CWD = 仓库根 | P7 实测 |
| B2 | 退出码三档：0 过 / 1 失败 / 2 用法错；夹具逐档断言；禁管道转手 | P3 + B104 |
| B3 | 结果走 stdout、诊断走 stderr；失败带一行上下文，禁静默 catch | D8 契约稳定前提 |
| B4 | Process 编排：显式 WorkingDirectory、不开 shell、双流重定向、超时 + Kill、退出码透传；长跑子进程场景禁并发复用同一 file-based app | D5 + 官方并发警告 |
| B5 | JSON 一律 STJ source-gen（JsonSerializerContext） | D7 |
| B6 | 类型面收敛：top-level statements + internal 辅助类型，不声明 public 类型 | P1 四类 error 全由 public 类型触发 |
| B7 | 脚本命名用连字符不用下划线（对齐既有 scripts 命名） | P1 中 CA1707 由程序集名（文件名）触发，虽分析器已关仍保持卫生 |
| B8 | .env.test 读取禁回显值；缺文件按既有语义降级（sqlite 档照跑） | P0 #1 + perf.sh 既有行为 |
| B9 | usage 文本走 stderr + exit 2；禁 Console.Readline 交互等待（脚本必须可无人值守） | CI 无人值守前提 |
| B10 | 单文件单职责；确需共享代码先忍重复，两处以上再评估形态 B（本轮 PerfCli 即按此判据立项） | 三次必抽原则的脚本域适配 |

## 9. 风险与对策（v3 更新）

| 风险 | 等级 | 对策 |
|------|------|------|
| secret-guard 迁移静默语义漂移 | 高 | T2-4 全历史双跑对拍是硬闸，不得裁剪（第 11 节红队结论） |
| PerfCli 六合一重构面大于 1:1 换语言 | 中 | 子命令一一对应现入口，先步骤后入口，逐命令独立对拍 |
| CI 冷启动增量 | 低（v3 降级） | P2 实测隔离后冷跑 1.3s，CI 预估 3 至 8 秒每脚本首建；job 内并行稀释 |
| pre-commit 延迟 | 低（v3 降级） | P2 热跑 0.14s，双脚本约 0.3s，对既有 7 秒链路无感 |
| MSBuild 评估顺序类暗坑复发 | 中 | P6 已示范"探针先行 + targets 确定性手段"的处置范式；B 系列候选登记 |
| bash 语义翻译遗漏（pipefail/set -e/B104） | 中 | 对拍 + 正反夹具 + 退出码逐档断言 |
| SDK 预览版变动影响缓存指纹 | 低 | 缓存键含 SDK 版本，行为是重建而非错构建 |

## 10. 待用户裁决的三个点

1. **set-test-env.sh 残余价值**：推荐淘汰。若仍需交互 shell 手工导出连接串，留 10 行 shim 入 D9 白名单。
2. **PerfCli 子命令命名**：语义名（bench-matrix/full-perf/dappersuite/compare）或沿用旧名（bench/fullperf/dappersuite/ab，文档改动最小）。
3. **Phase 4 与 v6.0 的时序**（v3 新增）：性能链是 v6.0 回归测量依赖的 harness，迁移中途换链会污染 A/B 可比性。推荐 Phase 0-3 与 5-6 先行（约 4 天，与 v6.0 无冲突），Phase 4 整体放在 v6.0 启动前或收尾后，不与 v6.0 穿插。

## 11. 论证记录（辩证流程留痕）

**自我反驳三点**：
1. 反驳"不迁移更省"：bash 现状可用，迁移约 9.5 天纯投入。裁决：语言统一政策属用户主权且已两次确认，迁移换回的是单一技术栈 + ci.slnf 级 0W0E 覆盖（PerfCli）+ 语言门禁的确定性；同时承认 run-mutation-tests/release-version-scan 等低频稳定脚本收益最小，故排期靠后不阻塞。
2. 反驳"PerfCli 是过度工程，1:1 file-based 足够"：反驳过程发现 v2 的 CI 冷启动论据不成立（perf 家族 CI 零调用），已剔除；结论改由共享基础设施、活跃演化、门禁三论据独立支撑，结论不变但论据更诚实。
3. 反驳"探针已全绿可径直开工"：三个裁决点中两个影响调用形态与文档写法，开工后返工文档的成本高于先裁决；Phase 4 时序涉及 v6.0 主线资源分配，属用户主权，不可自主决定。

**评分**：8.5/10。阻碍 9 至 10 的因素：三个裁决点未闭环（用户主权）；估时仍是区间估算无逐项校准；形态 B 子命令的夹具覆盖粒度未细化（T1-1 落地时补）。

**红队一句**：本方案最大的残余风险不是迁不迁，而是 secret-guard 在迁移中出现静默语义漂移；T2-4 的全历史双跑对拍是唯一硬闸，任何压缩工期的情形下不得裁剪这一步。

## 12. 进度追踪

| 项 | 状态 |
|----|------|
| 方案 v3 定稿（探针实证 + 论证修订 + 最佳实践） | done |
| Phase 0 基建与探针 | done（本轮提前完成，T0-1 至 T0-5） |
| Phase 1 夹具先行 | pending |
| Phase 2 提交防线四项 | pending |
| Phase 3 发布链三项 | pending |
| Phase 4 性能链 → PerfCli | pending（受裁决点 3 时序约束） |
| Phase 5 夹具本体与 hook | pending |
| Phase 6 政策固化 | pending |

总进度约 10%（Phase 0 已实测完成并入库：scripts/Directory.Build.props + Directory.Build.targets；未动任何现有脚本；三个裁决点表态后从 T1-1 开工）。
