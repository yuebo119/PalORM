# 脚本 C# 化整改方案（2026-09-29 定稿，v2 修订）

> 用户决策一（2026-09-29）：当前项目尽量替换成 C#，以后做脚本能用 C#/.NET 就使用 C#，尽量避免引入其他语言；基于 .NET 10 起的 file-based app（`dotnet run app.cs`）脚本化。
>
> 用户决策二（2026-09-29 v2 修订）：非 C# 脚本若确有必要必须征得用户同意方可存在；C# 脚本做成 file-based app 还是传统项目，按真实场景逐个裁定，不一刀切。
>
> 本文回答三件事：仓内还有哪些非 C# 脚本、每一个的形态与去向、按什么顺序与纪律迁移。方案本身不改任何脚本，开工前用户对第 8 节两个裁决点表态。

## 1. 事实基线（2026-09-29 盘点，全部来自 git ls-files 与逐文件核读）

**范围**：15 个 `.sh`（约 1740 行）+ 1 个 Node `.mjs` + 1 个 git hook 薄包装。仓内无 Python/Ruby/Perl。`tools/` 下 PerfGate 与 Scaffold 已是 C# 项目，且均已收入 PalORM.ci.slnf（享受 0 警告 0 错误严格门禁）。

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

**引用半径**：CI 9 处调用（全部在 verify.yml）；docs 约 20 处（性能基准规范 18、发布规范 6、测试规范/编码规范/路线图零散）；AGENTS.md 4 处；CONTRIBUTING 1 处；CHANGELOG 为历史记录不改。全部为 bash 调用形态。

**外部工具依赖实况**：grep/sed/head/tail 为主（C# 原生可替）；Node 仅 parse-coverage.mjs 一处；无 jq、无 curl、无 psql、无 mysql CLI（此前疑似 mysql 客户端调用实为方言名字符串，已逐条排除）。CI 13 个 job 全部 ubuntu-latest，除首个 secret-scan job 外均已装 setup-dotnet。

**运行时环境**：global.json 钉 SDK 11.0.100-preview.6（rollForward latestMinor），本机实跑 rc.1。file-based app 需 SDK 10.0.100+，本仓全部特性可用，无需任何版本动作。

## 2. 技术定案（依据 learn.microsoft.com file-based-apps 官方文档逐条核实）

| 定案 | 内容 | 依据 |
|------|------|------|
| D1 调用约定 | 形态 A 统一 `dotnet run --file scripts/&lt;名&gt;.cs -- &lt;参数&gt;`；形态 B 统一 `dotnet run --project tools/&lt;项目&gt; -- &lt;子命令&gt;`，本地与 CI 同形 | 官方 CLI 节；CWD 有 csproj 时无 --file 会退化传参，--file 消除歧义 |
| D2 不用 shebang | 不依赖 `#!/usr/bin/env dotnet` 直执行 | 仓内无 .gitattributes，Windows checkout 为 CRLF，shebang 文件在 Linux 直执行会破；`dotnet run` 调用形态对行尾免疫 |
| D3 scripts/ 目录隔离 | 新增 scripts/Directory.Build.props，只作用于形态 A：Import 父级 props 后覆写 ManagePackageVersionsCentrally=false、PublishAot=false、GenerateDocumentationFile=false、IsAotCompatible=false、IsTrimmable=false | 官方 Folder layout 节明确推荐脚本目录配隔离 props；保持 net11.0/Nullable/TreatWarningsAsErrors/LangVersion 继承 |
| D4 CPM 规避 | 隔离 props 关闭 CPM，形态 A 脚本自钉版本；形态 B 是正式项目，走根 CPM 正常通道 | 根 Directory.Packages.props 开启 CPM + 传递钉扎；NU1008 风险由 Phase 0 探针实证后定稿 |
| D5 纯 BCL 为默认 | 全部迁移目标零第三方 NuGet 依赖（JSON 用 System.Text.Json，XML 用 System.Xml.Linq，zip 用 System.IO.Compression） | 懒惰阶梯：标准库优先；现有脚本能力全部 BCL 可覆盖 |
| D6 构建缓存 | 形态 A 默认单文件形态（缓存开启）；禁用 glob 形态 `#:include`（官方明示 glob 会禁用缓存）；显式单文件 include 可用 | 官方 Build caching 节 |
| D7 并发防争抢 | 同一脚本可能并发调用的场景（CI 矩阵、A/B 轮次），先 `dotnet build` 再 `--no-build` 启动 | 官方明示并发实例会争抢构建输出 |
| D8 退出码契约 | 每个 .cs 保持与 .sh 相同的 exit 语义（0 过 / 非 0 分档失败）；编排类用 Process 传退出码，禁管道吞码形态（B104） | 全局 AGENTS 退出码族 + B104 实测教训 |
| D9 JSON 序列化 | 形态 A 内需要 JSON 时用 STJ source-gen（JsonSerializerContext） | 官方明示 file-based app 默认 AOT publish；与本仓 AOT 文化同构 |
| D10 输出契约不变 | stdout/stderr 文本格式、退出码、产物路径对调用方（CI 断言、SOP、PerfHub 结果库）逐字节兼容，迁移轮不做格式顺手改 | 外科手术原则；格式变更是独立任务，不夹带 |
| D11 语言政策载体 | 非 C# 脚本白名单制：白名单唯一初始条目为 .githooks/pre-commit（git hook 机制要求可执行脚本入口，技术约束）；新增白名单条目必须在 docs/编码规范.md 登记理由并经用户明确同意；机械门禁见 T6-3 | 用户决策二；元规则"确定性 > 概率性"，门禁是承载物 |

## 3. 形态判定标准（用户决策二的落地判据）

**形态 A：file-based app（scripts/*.cs）**，同时满足：
1. 单一职责，单文件可容纳，无持续膨胀预期；
2. 与仓内其他脚本无成体系共享基础设施（零共享，或至多一个显式 `#:include`）；
3. 调用形态是一条命令跑完的脚本式使用，无人机接口演化需求；
4. 行为验证走黑盒夹具（test-quality-scripts），不需要白盒单测；
5. 不需要纳入 ci.slnf 严格编译图。

**形态 B：console 项目（tools/PalORM.*）**，任一命中：
1. 多入口共享成体系的基础设施（路径常量、Process 包装、env loader、结果库契约）；
2. 逻辑量超出单文件舒适区且处于活跃演化期；
3. 需要纳入 ci.slnf 严格编译与门禁图（0 警告 0 错误 + SonarAnalyzer）；
4. 需要被其他项目引用或白盒测试。

**形态 C：保留 bash 薄壳**：仅限技术约束（git hook 必须是可执行脚本），属 D11 白名单条目，逻辑全部外移。

## 4. 迁移裁决表（17 项逐一去向）

| # | 对象 | 形态 | 去向 | 场景判据 |
|---|------|------|------|---------|
| 1 | stub-check.sh | A | scripts/stub-check.cs | 36 行文本扫描，五判据全中 |
| 2 | assert-test-counts.sh | A | scripts/assert-test-counts.cs | 单命令 JSON 断言，STJ 直覆盖 |
| 3 | assert-coverage.sh + parse-coverage.mjs | A | scripts/assert-coverage.cs（合并） | XML 解析 + 地板断言一体，Node 依赖消除 |
| 4 | secret-guard.sh | A | scripts/secret-guard.cs | 40 类 Regex 单命令；行为有 test-quality-scripts 自测夹具黑盒守卫；不与任何脚本共享基础设施 |
| 5 | test-package-contract.sh | A | scripts/test-package-contract.cs | zip 读 nuspec + XML 断言单命令 |
| 6 | release-version-scan.sh | A | scripts/release-version-scan.cs | 属性模式扫描 + git tag 推导单命令 |
| 7 | pre-release-check.sh | A | scripts/pre-release-check.cs | pack/restore/断言编排，链式调用 #5 的 .cs 形态 |
| 8 | perf.sh | B | tools/PalORM.PerfCli（子命令 smoke/full/compare/gate/report/index） | 六子命令与 perfhub-ab/run-full-perf/dappersuite/run-benchmarks 共享路径常量、env loader、Process 包装、结果库契约（判据 B1）；v5.8 起活跃演化（B2）；入 ci.slnf 得 0W0E 门禁（B3） |
| 9 | perfhub-ab.sh | B | tools/PalORM.PerfCli（子命令 compare，内部步骤可独立触发） | 同上家族共享；worktree 切臂语义原样保留 |
| 10 | run-full-perf.sh | B | tools/PalORM.PerfCli（子命令 full-perf） | 同上家族共享 |
| 11 | dappersuite-run.sh | B | tools/PalORM.PerfCli（子命令 dappersuite） | 同上家族共享 |
| 12 | run-benchmarks.sh | B | tools/PalORM.PerfCli（子命令 bench-matrix） | 同上家族共享 |
| 13 | run-mutation-tests.sh | A | scripts/run-mutation-tests.cs | 独立 Stryker 触发，46 行，与性能家族无共享 |
| 14 | set-test-env.sh | 淘汰 | env loader 并入 PerfCli 内部 | 仓内唯一 source 调用方 perf.sh 属 PerfCli；C# 进程无法向父 shell 导出变量，进程内读 .env.test 后传子进程已覆盖全部仓内场景（裁决点 1） |
| 15 | test-quality-scripts.sh | A | scripts/test-quality-scripts.cs | 多夹具单命令测试编排；被测对象全部黑盒调用；排最后迁移 |
| 16 | .githooks/pre-commit | C | 保留 26 行 bash 薄壳 | git hook 机制要求可执行脚本入口（技术约束）；内部转发改调形态 A/B 命令；D11 白名单唯一条目 |
| 17 | CI verify.yml 9 处调用 | 改造 | 调用形按形态 A/B 切换；secret-scan job 补 setup-dotnet 步 | 该 job 是唯一无 dotnet 的调用方 |

**范围外备案**：`.ai/scripts/`（tech-debt-scan.sh、verify-ai-system.sh 等本地工具，明确不入仓库）与用户级 hook（~/.zcode）不在本方案范围，如需同步迁移另立任务。此后任何新增脚本一律 C#（形态按第 3 节判据），非 C# 需求走 D11 白名单流程。

## 5. 迁移纪律（每个脚本通用，对应全局诊断三步骤）

1. **S1 基线快照**：迁移前记录该脚本现有行为证据（触发一次真实运行留输出，或引用 test-quality-scripts 既有夹具断言），作为对拍基准。
2. **对拍验证**：新形态与旧 .sh 并存一轮，同一输入双跑，比对退出码与 stdout 关键行；夹具同时覆盖两形态后先绿再删旧 .sh。这是"修复前红"纪律在迁移场景的等价物。
3. **单变量**：迁移轮不顺手改检测规则、阈值、输出格式（D10）。发现疑似 bug 单独开任务。
4. **三方一致**：删除 .sh 的同一提交内，CI、docs、AGENTS、CONTRIBUTING、hook 转发全部切换；`grep -rn 'scripts/xxx\.sh'` 全仓归零（CHANGELOG 除外）。
5. **锁定证据**：每项迁移的完成判据 = 成功夹具通过 + 故障夹具（坏输入 → 非零退出）通过；只过成功路径不算完成。

## 6. 分阶段任务清单

估时为单侧工作量，含夹具扩展、CI 切换与文档同步。基准日期 2026-09-29。

### Phase 0：基建与探针（约 0.5 天）

| 任务 | 内容 | 验收 |
|------|------|------|
| T0-1 | 建 scripts/Directory.Build.props（D3 清单，只管形态 A） | scripts 下任意 .cs 构建继承 net11.0/严格编译，且不触发 CPM/文档生成/AOT 分析器报错 |
| T0-2 | 探针 A：CPM 冲突实证。临时 .cs 带 `#:package X@v` 构建一次，验证 NU1008 是否发生与关闭 CPM 的解除效果 | 探针结论回写 D4，删除临时文件 |
| T0-3 | 探针 B：冷/热启动耗时。空脚本 `dotnet run --file` 首跑与缓存命中各测 3 次取中位；含 root .editorconfig + AnalysisLevel 干扰实测 | 数字回写第 7 节风险表；CI 预算据此复核 |
| T0-4 | 探针 C：.editorconfig + EnforceCodeStyleInBuild 对 top-level 脚本的样式告警实测（CS1591/IDE 规则） | 隔离 props 终稿落盘，样例脚本 0 警告构建 |
| T0-5 | 定稿调用约定文档片段（D1/D8），供 Phase 2 起 CI 与 docs 引用 | 片段进入 docs/编码规范.md 脚本节占位 |

### Phase 1：夹具先行（约 0.5 天）

| 任务 | 内容 | 验收 |
|------|------|------|
| T1-1 | test-quality-scripts.sh 增"目标形态"夹具骨架：为每个待迁对象预留形态 A 入口与形态 B 子命令的成功/故障夹具位（red 状态） | 夹具对未迁移目标报 skip 有明确标注，不误报 |
| T1-2 | 夹具基础设施验证：故障输入触发非零退出的断言通道自身先被证明能失败（mutation probe：把一个夹具的预期值改错，确认夹具报红） | 验证记录留痕 |

### Phase 2：提交防线四项（约 2 天，全部形态 A）

顺序：stub-check → assert-test-counts → assert-coverage（含吞并 parse-coverage.mjs）→ secret-guard（最复杂压轴）。

| 任务 | 内容 | 验收 |
|------|------|------|
| T2-1 | stub-check.cs + 对拍 + 删 .sh | 夹具正反用例双绿；verify.yml 223 行调用切换 |
| T2-2 | assert-test-counts.cs + 对拍 + 删 .sh | verify.yml 112/123/173 三处切换；三 job 各触发一次实跑 |
| T2-3 | assert-coverage.cs（含 XML 解析，删 lib/parse-coverage.mjs） | Node 在仓内调用归零；verify.yml 116 行切换 |
| T2-4 | secret-guard.cs + 对拍 + 删 .sh；secret-guard 自测夹具同步迁 | .githooks/pre-commit 转发更新；verify.yml 45/57 两处切换；secret-scan job 补 setup-dotnet；本机连续 3 次真实 commit 实测 hook 延迟在探针 B 数字 + 2s 内 |
| T2-5 | Phase 2 收口：`grep -rn '\.sh' .github/ scripts/ .githooks/` 仅剩计划内项 | 0 意外残留 |

### Phase 3：发布链三项（约 1.5 天，全部形态 A）

| 任务 | 内容 | 验收 |
|------|------|------|
| T3-1 | release-version-scan.cs + 对拍 + 删 .sh；用 v6.1.0 tag 重放一次 | 扫描结果与 .sh 版一致；docs/发布规范.md §1.3 同步 |
| T3-2 | pre-release-check.cs + 对拍 + 删 .sh；正向（当前 6.1.0）全绿 | docs/发布规范.md §4.1 第 0 步命令同步；对 test-package-contract.cs 的链式调用打通 |
| T3-3 | test-package-contract.cs + 对拍 + 删 .sh | verify.yml 283 行切换；CI 实跑绿 |

### Phase 4：性能链 → PalORM.PerfCli 项目（约 3.5 天）

| 任务 | 内容 | 验收 |
|------|------|------|
| T4-1 | 建 tools/PalORM.PerfCli console 项目：纯 BCL、入 PalORM.slnx tools 目录 + PalORM.ci.slnf，走根 props 严格编译 | ci.slnf 构建 0 警告 0 错误（新增一项目后的全量口径） |
| T4-2 | 子命令 bench-matrix（对应 run-benchmarks.sh）+ 对拍 | BDN 矩阵触发与日志落盘路径一致 |
| T4-3 | 子命令 full-perf（对应 run-full-perf.sh）+ 对拍 | perf full 第 1 步语义一致（SKIP_REPORT 通道不变） |
| T4-4 | 子命令 dappersuite（对应 dappersuite-run.sh）+ 对拍 | 三方言串行行为一致 |
| T4-5 | 子命令 compare（对应 perfhub-ab.sh）+ 对拍 | worktree 切臂 + label 契约不变；并发防护按 D7 |
| T4-6 | 六子命令入口（对应 perf.sh：smoke/full/compare/gate/report/index）+ env loader 内置（吸收 set-test-env.sh）+ 对拍 | 六子命令逐一实跑冒烟；.env.test 缺失时 sqlite 档照跑、pg/mysql 档报缺凭证的原有语义不变 |
| T4-7 | docs/性能基准规范.md 约 18 处、AGENTS.md 性能节、CONTRIBUTING set-test-env 段同步；删 perf.sh/perfhub-ab.sh/run-full-perf.sh/dappersuite-run.sh/run-benchmarks.sh/set-test-env.sh 六个 .sh | 性能链 .sh 引用 grep 归零（CHANGELOG 除外） |
| T4-8 | run-mutation-tests.cs（形态 A）+ 对拍 + 删 .sh | Stryker 触发一致 |

### Phase 5：夹具本体与 hook 收口（约 1 天）

| 任务 | 内容 | 验收 |
|------|------|------|
| T5-1 | test-quality-scripts.cs（全部夹具随迁，含形态 B 子命令夹具）+ 对拍 + 删 .sh | verify.yml 243 行切换；CI 实跑绿；本机全量夹具绿 |
| T5-2 | .githooks/pre-commit 终态：三段转发全为形态 A 命令 | 本机 commit 实测全链绿，延迟记录在案 |

### Phase 6：政策固化与收尾（约 0.5 天）

| 任务 | 内容 | 验收 |
|------|------|------|
| T6-1 | 全仓三方一致终扫：`git ls-files` 中非 C# 脚本仅剩 .githooks/pre-commit | 白名单计数 1，与 D11 一致 |
| T6-2 | docs/编码规范.md 增脚本语言章节：D11 白名单政策 + 第 3 节形态判据 + D1/D3/D5/D8/D9 约定 + 形态 A 最小示例；AGENTS.md 同步一行政策指针 | 章节含最小可运行示例与判据表 |
| T6-3 | 语言门禁：test-quality-scripts.cs 增"仓内出现新 .sh/.mjs/.py/.rb/.pl（白名单外）即 FAIL"扫描 | 植入一个假 .sh 验证 FAIL，删除后恢复绿（V17 式双向验证） |
| T6-4 | CHANGELOG 未发布段登记本整改轮 | 条目含 17 项裁决摘要与白名单政策 |

**合计约 9.5 个工作日**（Phase 0 与 1 可合并半天内完成；Phase 2-4 之间无依赖可穿插）。

## 7. 风险与对策

| 风险 | 等级 | 对策 |
|------|------|------|
| PerfCli 六合一重构面大于 1:1 换语言 | 中 | 子命令一一对应现入口（T4-2 至 T4-6 各自独立对拍）；内部步骤子命令保留独立触发能力；先移植步骤子命令、入口压轴 |
| CI 冷启动增量：runner 每次冷构建脚本，形态 A 9 处调用预计各增 5 至 15 秒 | 中 | 探针 B 实测后回填；纯 BCL 无 restore，增量可控；不达标则 CI 侧预热后 `--no-build`；PerfCli 在 ci.slnf 内随既有构建图编译，无额外冷启动 |
| 本机 pre-commit 延迟上升（secret-guard/stub-check 各一次 dotnet 启动） | 中 | 热缓存后单次约 0.3 至 0.5 秒；超预算则合并两脚本为单一入口一次启动 |
| file-based app 继承根配置产生未预期告警（.editorconfig 样式、NU 系） | 中 | Phase 0 探针 C 在动工前暴露全部告警，隔离 props 一次定稿 |
| 同脚本并发争抢构建输出 | 低 | D7 约定；现网仅 A/B 轮次与 CI 矩阵存在并发面，均可控 |
| bash 独有语义翻译遗漏（pipefail、set -e 短路、B104 管道吞码） | 中 | 迁移纪律第 2 条对拍 + 第 5 条正反夹具；退出码契约逐脚本在夹具中显式断言 |
| global.json SDK 预览版变动影响脚本构建缓存指纹 | 低 | 缓存键含 SDK 版本，行为是重建而非错构建；CI setup-dotnet 版本与 global.json 对齐已有既有约束 |

## 8. 待用户裁决的两个点

1. **set-test-env.sh 的残余价值**：推荐淘汰（仓内唯一 source 调用方 perf.sh 已由 PerfCli 内置 loader 覆盖）。若你仍要在交互 shell 里手工导出连接串跑 psql 等客户端工具，保留一个 10 行 bash shim 并列入 D11 白名单，其余照迁。
2. **PerfCli 的子命令命名**：T4-2 至 T4-6 采用与现入口一一对应的语义名（bench-matrix/full-perf/dappersuite/compare + 六入口原名）。若你更在意文档改动最小，可把内部步骤子命令直接沿用旧名（bench/fullperf/dappersuite/ab），命名风格定稿后 docs 一次性切换。

## 9. 进度追踪

| 项 | 状态 |
|----|------|
| 方案定稿与任务清单（v2 含形态逐项裁定） | done（本文档） |
| Phase 0 基建与探针 | pending |
| Phase 1 夹具先行 | pending |
| Phase 2 提交防线四项 | pending |
| Phase 3 发布链三项 | pending |
| Phase 4 性能链 → PerfCli | pending |
| Phase 5 夹具本体与 hook | pending |
| Phase 6 政策固化 | pending |

总进度 0%（方案 v2 已定稿，未动任何脚本；两个裁决点表态后从 T0-1 开工）。
