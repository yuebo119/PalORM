# PalORM 项目级 AI 规范

> 本文件被 ZCode 自动加载（项目根目录 AGENTS.md 约定）。
> 与全局 `~/.zcode/AGENTS.md` 的分工：全局管行为准则（Karpathy 准则），项目级管项目规范。

## 启动清单（每次会话开始确认）

```
1. git status——工作树清洁？
2. dotnet build PalORM.ci.slnf -c Debug——0 警告 0 错误？（口径=src+test；bench/BenchmarkDotNet vendored 子树有登记豁免，不在 ci.slnf）
3. 技术债扫描——dotnet run --file scripts/tech-debt-scan.cs（判定层已 C# 化迁主仓；.ai 知识层仍为本地工具）
4. .ai/lessons.md——已读最新版？（本地工具，不入仓库）
```

## 规范真源（按优先级）

| 优先级 | 文件 | 用途 |
|-------|------|------|
| **最高** | `.editorconfig` | SonarAnalyzer 39 条规则（P0+P1 error，编译期阻断；口径=全部 dotnet_diagnostic 严重性条目含 *.g.cs 段） |
| **高** | `.ai/lessons.md` | 规范系统手册 v7.43（214 缺陷：A1-A7 + B1-B207 + XI 性能测量纪律 SOP，本地工具，独立嵌套 git 仓库） |
| **高** | `.ai/perf-ledger.md` | 性能决策账本（EPSL §4.4 三表：已用/不做/勿重开；立项前必查勿重开表，B182） |
| **高** | `.ai/test/prompt.md` | 测试规范系统 v1.3（24 铁律 + 19 缺陷，本地工具，不入仓库） |
| **高** | `docs/发布规范.md` | NuGet 发布流程 SOP（v5.0.0 实测，含 8 条实践教训） |
| **中** | `docs/编码规范.md` §18-19 | SonarAnalyzer 守护层规则文档化 + 脚本语言规范（脚本一律 C#，白名单制与形态判据，2026-09-29 用户决策） |
| **参考** | `.github/PULL_REQUEST_TEMPLATE.md` | PR 检查清单 |

**版本与发布**：本项目遵循 [Semantic Versioning 2.0.0](https://semver.org/lang/zh-CN/)。MAJOR=破坏性变更，MINOR=向后兼容新增，PATCH=向后兼容修复，预发布用 `-preview.N`；版本真源 `Directory.Build.props`，发版前按 `docs/发布规范.md` §1.2 判定升级类型，并按 §4.1 完成预检与 §1.3 三方一致扫描。不按 SemVer 的版本号不得打 tag 或发布。

## 四系统（按需触发）

| 命令 | 系统 | 用途 |
|------|------|------|
| `/review` | 审计+评审 | 地毯式逐行代码审查（5 档位） |
| `/gate` | 门禁 | G1-G33 规范合规检查（编译前阻断） |
| `/refine` | 精炼 | 24+3 项操作矩阵（更优实现） |
| `/test` | 测试规范 | T1-T24 测试铁律 + 覆盖矩阵 + 基准配置规范 |
| （自动） | pre-commit | 提交前七段：`secret-guard` → `stub-check` → 受影响项目构建（`precommit-build-check.cs`：src/test/tools 任一编译面变更即全量 `PalORM.ci.slnf` Debug 增量，根级基础设施文件同，docs/scripts/vendored 子树跳过；2026-10-18 起取消单项目正向映射——反向依赖漏检，实测全量增量仅约 9 秒）→ 参数集合复用门禁（R-UNNESTB）→ `dotnet run --file scripts/verify-ai-system.cs -- --fast`（27 项里的 25 项）→ `dotnet run --file scripts/gate-check.cs -- --allow-dirty`（G1-G33，G10/G15/G16 为警告级）→ `dotnet run --file scripts/doc-consistency-check.cs`（D4/D10-D14 文档口径；`.ai/` 不存在则 verify/gate 两段跳过）。**改了 `.ai/*.md` 的计数/标题声明却不改对应校验的配对源，会在这里被拦** |

### 项目专属经验（v4.0 + v5.0 实测，编号与四系统脚本对齐）

> 通用教训已在全局 `~/.zcode/AGENTS.md` 的"大型重构实践教训"章节。以下仅保留 PalORM 专属的规则编号和缺陷编号。

**门禁 G31-G33**：
- G31 方言感知验证（跨方言 API 需 SqlDialect 分支或回退）
- G32 NuGet.Config packageSourceMapping CI 兼容（nuget.org 也能提供约束包）
- G33 工作树脏检查（跨任务切换前 git status 清洁）— 已提升到全局

**精炼 O25-O27**：
- O25 阈值改能力检测（local_infile 替代行数阈值）
- O26 方案 Y 双方法（BulkUpdate vs BulkUpdateBatchAsync）
- O27 SourceGen WithComparer（sealed record 增量缓存）

**测试 T11-T14**（编号冲突注意）：
- T11 计数口径统一（badge / 文档 / tech-debt #9 一致）
- T12 断言基线提升需标注理由
- T13 DryRun/SQL 断言必须先读生成代码
- T14 优先行为断言，避免裸 IsNotNull

**v5.0 期望但未入 test/prompt.md 的规则**（编号冲突）：
- 原 AGENTS.md 声称的"T11 环境变量命名分离 / T12 三方一致扩展 / T13 被测代码完整性"与 test/prompt.md 的 T11-T14 完全冲突
- 环境变量命名分离 → 全局 `~/.zcode/AGENTS.md` 的 v5.0 大型重构教训章节已覆盖
- 三方一致扩展 → 全局"三方一致"铁律已覆盖（包版本号+连接串参数）
- 被测代码完整性 → 已提升到全局

**过程纪律 B8-B37**（r11 审计补 B28/29 空洞、r5/r10 沉淀 B30-B37——真源在 .ai/lessons.md）（PalORM 专属缺陷编号，见 .ai/lessons.md）：
- B8 emit 变更必须清 obj/bin（增量构建复用旧 emit 导致 NRE）
- B9 方案文字与实施代码偏差（核心路径严格对齐）
- B10 方案调研可能误判瓶颈（benchmark 验证声称的瓶颈）
- B11 review 子代理推理不验证代码（P0/P1 定级前写复现测试）
- B12 Edit 替换误删相邻行（old_string 只含目标行 ±1 行）
- B13 门禁正则匹配注释内容（统计前剥离 /// 和 //）
- B14 测试计数口径统一（全仓库统一为 CI 通过数）
- B15 SQL 断言必须先读生成代码（不看代码的断言 = 猜测）
- B16-B20 v5.0 会话缺陷（TUnit 零改动误判/阈值伪精确/CASE WHEN 方言慢/NuGet.Config CI/SourceGen WithComparer）
- B21-B23 过程纪律缺陷（核实 summary / 核实 diff / 核实 API）— R0 的底层缺陷登记
- B24-B27 诊断规则工程化缺陷（RS1032 消息格式/XML doc 转义/防静默错误价值分层/SyntaxNodeAction 变量流局限）— PALORM001-040 完整化会话
- B28 特性推荐偏置缺陷（参照系偏置：用 EF Core 全功能面衡量 micro-ORM）— 「特性推荐四问」SOP：推荐前必过(1) ORM 职责吗？(2) AOT 可行吗？(3) 客户自己做更好吗？(4) 真必需吗？且必查 docs/adr/ 既有 ADR + .ai/lessons.md B 系列 + grep 目标 API 已存在性
- B29 Interceptor 实施工程化缺陷（API 名称/位置推断错误）— PoC 驱动开发 SOP：实施前先写最小 PoC 验证关键 API 可用性（1 天止损），API 以编译错误信息为准不以记忆为准

**PL 落地会话纪律（B92-B104，2026-09-25 实测，真源 `.ai/lessons.md` AJ 节）**：
- B92 A/B 脚本运行期间禁止构建任何链接同一批源文件的项目——否则探针拿到另一臂的二进制（本会话据此误判"有退化"，多做一轮返工）
- B93 提交前 grep 一个**唯一标识符**校验 HEAD 内容确实含本次声称的改动；`git status` 干净 ≠ 内容对（后台切文件的作业未结束时禁止提交）
- B94 A/B 批次只靠 `label` 区分，**绝不信信封 `Commit` 字段**（就地 `git checkout <提交> -- <文件>` 切臂时 HEAD 不变）；label 必须带 `IsSubsetLabel` 认的前缀，否则单方言/单测项批次会顶掉 `latest-<夹具>.json`
- B95 比率型指标**先读分母的实现**——分母不干同一件事则该比值无判别力（`Build*` 地板 `return` 插值字面量而产品真生成 SQL → 6.5~7.1× 长期挂榜首）。处置=记 `Ratio=0` + `Note` 注明、绝对值与分配照登；**不删项**（等于假装它不存在）也不照比（误导）
- B96 禁用/置零一个值时**枚举所有读侧的合并顺序**：`KeyItems` 过滤掉的键等于"最新批次没发言"，旧批次的高比值会把它复活（实测 152 项基线里带回 12 个已禁用的 `Build*`、比值 2.5~7.1）
- B97 写"已排除/已确认"清单**每项标来源**：实测排除 / 未采集（未知）/ 推断——"没测过"不得进"已排除"（维度 8 计数器只在 SQLite 生效，PG 往返数是未知而非相等）
- B98 给会话/租约类加**只随其释放的资源**（命令/语句句柄/定时器）前，先列它的两种用法：复用到 `Dispose` / 用完即弃；后者拿不到 `Dispose` 必然泄漏。修法=**惰性晋升**（第 3 次同形态操作才建池）——实测无条件缓存让并发混合慢 **4.84×**（40380 vs 8335 µs）
- B99 多因子优化**先跑半方案对照**（只改 A / 改 A+B），否则收益归因是猜的（只池化参数实测 960 vs 969 µs、分配只回收 12%——全在命令侧）
- B100 地板不是恒定标尺：三层判据稳定度**分配 > 绝对耗时 > 同轮比值 > 跨轮比值**；地板自身慢 2~5.4× 时该轮 P/F 全作废，判定改用绝对耗时与分配
- B101 新增测试的 `CREATE TABLE` 表名从实体 `[Table]` 注解抄并跑前 grep 核对；`Cache=Shared` 内存库跨会话持一条 keeper 连接
- B102 探针需要 internal 实现时**复刻而非加 `InternalsVisibleTo`**，但复刻必须**前置逐字节比对钉住**（校验失败即差源已定位）
- B104 要看脚本退出码就**别把它管进 `tail`/`grep`**（`bash s.sh | tail` 恒返回 0，`set -e` 的失败被外层屏蔽）——实测 A/A 脚本第一轮就中止、harness 却报 "completed (exit code 0)"；兜底是**分析前先核对预期批次数与项数**（本例靠"说好 2 轮只出 1 轮、且仅 2 项 health=unknown"发现）。性能批次调用统一写 `bash s.sh > log 2>&1; echo exit=$?`

**R0 审查前置三核实**（已提升到全局）：核实 summary / 核实 diff / 核实 API

详见 `.ai/README.md`。

## 构建验证时机

| 时机 | 命令 |
|------|------|
| 同类改动内 | 不构建 |
| 跨类别切换 | `dotnet build` |
| 快照类改动 | 先 `PALORM_UPDATE_SNAPSHOTS=1 dotnet run` 确认基线 |
| 最终提交前 | `dotnet build --no-incremental` |
| 技术债扫描 | `dotnet run --file scripts/tech-debt-scan.cs`（判定层 C# 化迁主仓） |

## 性能测试结果输出规范（2026-09-24 用户指定 · 2026-10-17 七节结构改版 · 每次跑测汇报强制）

> **EPSL v1.7 接入**：预算契约 `perf-budgets.md`（门禁阈值单一事实源）、决策账本 `.ai/perf-ledger.md`（已用/不做/勿重开三表）、测量与汇报口径即本节；对账 `node ~/.zcode/scripts/eps-perf-check.mjs`（版本/模板引用/基准类数/基准入口接入/结果登记 latest 新鲜度）。
> **全量运行后置检查**：任何复测后执行 `PerfCli report` 重建统一报告；离群先分单臂尖峰与三臂同向，未复测不归因；容器重启后的首批为 transition，至少 3 个完整批再 `record-index`。
> **形态选择（P1）**：一请求一会话用 `DataSessionPool`，高吞吐逐行写用批量 API，大结果集用 `QueryAsyncEnumerable`；P95/P99 只展示不门禁（B100 纪律）。

性能跑测/对比结果一律以下列固定结构与表格呈现，数据源为 `bench/perfhub/results/history-*.json` 明细（非报告转述）。**触发场景**：任何性能测试后汇报——`PerfCli full`、PerfHub 批次、A/B 复测、单夹具运行。四组表由 `PerfGate tables`（`PerfCli report` 自动附带）生成。

**结构设计原则**（2026-10-17 用户定标，替代原五段数据陈列式结构）：结论先行、决策相关内容先行，全量表降级为附表；专业书面语体，禁口语化语气词；通俗优先，术语首次出现给白话定义、关键机制配类比；图标色标做视觉分层（🩺症状 / 🔍证据 / ⚖️判定 / 👉处置，🟢🟡🔴 置信度 chip）；目标读者为中级工程师，结论层 2 分钟可读完。

**汇报结构固定七节**：
1. **总体结论与解读前提**：一句话总体结论（🟢 标记）+ 四阶段运行状态面板（✅/⚠️/❌）+ 解读前提（环境变更、代码基线等读全文前必须知道的事实）
2. **关键发现速览**：9 行以内的要点表，每行带优先级（🔴P0/🟡P1/🟢P2）、数字、一句话结论、证据强度（🟢强=复测或确定性指标实证 / 🟡中=同批对照或锚定推断 / 🔴待确认=仅单批观察）
3. **异常判读（门禁 N 项 FAIL）**：核心原则（比值失真机制，配类比）+ 术语定义（地板臂/锚定/共移）+ N 项总览表（表观/复测锚定/判定/置信度）+ 逐项判读（症状→证据链→判定→处置四段式；新异常点按"单方言/单批跳变 + 上批对照值"如实呈现，**未复测不归因**）
4. **环境变更与服务现状**：变更内容与原因（端口/链路归属小表）+ 当前服务状态表（地址/状态/备注；涉及服务/端口/分支等易变状态时必附）
5. **全量四组表（附表）**：读表说明一句 + 四组表 + 每组两行以内要点
6. **指标定义与判读纪律（附录）**：指标定义（口径、公式、色标、📌 阈值）+ 判读纪律（判据层级、共移核对、锚定、复测方法、负向结论口径）
7. **产物与后续行动（附录）**：数据产物路径表 + 按优先级行动清单（🔴必做/🟡待确认/🟢已闭环）

**表格与格式规范**：
1. **表格组**（按组分表，逐组呈现，缺组注明"本批未跑"；明细表一/二/三每行展开方言列覆盖三方言）：
   - CRUD 单行与读（GetByKey/GetAllAsync/QueryAll/Insert/Update，三方言，两档）
   - 批量处理（BulkInsert/BulkUpdate/BulkDelete/UpsertBatch，三方言，两档）
   - 事务（TxSingleInsert/TxHundredInserts/TxBulkInsert/TxRollback/EnumInserts，三方言，仅最小档）
   - 跨方言 PalORM/ADO 比值表（**仅批量族 + GetAllAsync**；列序 SQLite/PostgreSQL/MySQL，两档）
   - CrossBench FreeSql / RepoDb 对比数据（2026-10-25 用户指令起默认并入四组表，2026-10-26 增 RepoDb）：表一/二/三各加 FreeSql、RepoDb 时延列、P/FreeSql 与 P/RepoDb 比值列、分配 F/R 段与分配相对 ADO（F/R）列（数据源 = CrossBench 最近完整批（五臂 × 三方言 × 两档 = 30 三元组），两遍反序中位，独立夹具口径脚注必须附；分配相对 ADO 的 P/D/F/R 四列同分母 = PerfHub ADO 地板）；表四追加 4B（PalORM/FreeSql，交叉批口径）；CrossBench 专节仍由冻结模板生成嵌入统一报告（五臂全量 + 绝对时间附表），节内不与四组表重复展开。**对话速记（2026-10-25 用户约定）：「表A」= 全量四组表（PerfGate TableGen），「表B」= CrossBench 五臂横评节（冻结模板原文）；两份模板各自独立，交付时不得混淆**
2. **列固定**（明细表一/二/三）：`操作 | 方言 | 档 | ADO.NET | Dapper | PalORM | FreeSql | RepoDb | P/ADO（倍数±%） | P/Dapper（倍数±%） | P/FreeSql（倍数±%） | P/RepoDb（倍数±%） | 分配（A/D/P/F/R） | 分配相对 ADO（P） | 分配相对 ADO（D） | 分配相对 ADO（F） | 分配相对 ADO（R）`（末四列右对齐）。**模板冻结（2026-10-25 用户指令，2026-10-26 增 RepoDb 列）**：展示调整先改 `tools/PalORM.PerfGate/TableGen.cs` 再重生成，禁手改生成物；倍数/标签全部由当批数据推导，禁硬编码
3. **数值口径**（2026-10-04 step23 定稿；2026-10-05 时延恢复带单位）：时延 = `MedianNs`（全路径中位数），**单元格数值自带单位**（`9.4 µs` / `16.76 ms`，µs ≥1 一位小数、<1 三位小数、≥1000 换算 ms 两位小数）；分配 = `AllocatedBytesPerOp`，**紧凑无空格**（`2.1KB/2.8KB/3.0KB`，1024 进制，KB 一位小数、≥100 KB 取整、MB 一位小数）；比值列 = **裸倍数两位小数 + 空格 + 色标紧贴百分比**（`0.72 🟩-28%`，无括号，零值记 `+0%`）；分配相对 ADO **拆 P/D/F 三列右对齐**（2026-10-05 用户定标 P/D，2026-10-25 增 F）：P 列 = PalORM 对 ADO 的分配差，D 列 = Dapper 对 ADO 的分配差，F 列 = FreeSql 对 ADO 的分配差（数据源 CrossBench 批，三列同分母 = 行内 PerfHub ADO 地板），缺数据一侧为 —；P/ADO% = PalORM ÷ ADO − 1；P/Dapper% = PalORM ÷ Dapper − 1（基准 Dapper，正 = 慢 = 劣）；百分比四舍五入整数，|p|<0.5% 记 0%；倍数 ≥1.3 或 ≤0.7 加粗（所有比值列）；跨方言表格值 = 裸比值两位小数（无 ×、无色标），越线格前缀 📌
4. **色标分档**（基准 = ADO.NET；性能方向：时间越短越好、内存占用越少越好——百分比 >0 即比基准更慢/更费 = 劣，取暖色；<0 即更快/更省 = 优，取冷绿。时延 P/ADO%、P/Dapper%（基准 Dapper）与分配相对 ADO% 共用，各三级，+30% 对齐门禁 1.3× 线）：
   - 🟢 强优 ≤ −30% · 🟩 中优 −29% ~ −10% · 🔹 微优 −9% ~ −1%
   - ⚪ 持平 0%（|p|<0.5% 四舍五入为 0，显示 `+0%`）
   - 🔸 微劣 +1% ~ +9% · 🟧 中劣 +10% ~ +29% · 🟥 强劣 ≥ +30%
   - **行级 📌**（2026-10-05 用户定标，历经 🚨 → 🔍 → 📌）：该行 P/ADO ≥ 1.50 或 ≤ 0.67（跨方言表任一格如此时操作名与越线格均前置 📌），语义 = **标记待看：远离基准需人工判读，非警告**（劣化方向 = 回归风险；优于地板 >33% = 地板健全性待核，对齐 PerfHub ⚠地板? 门禁方向——两个方向都要过目，不预设方向）
5. **口径注记必须附**（报告 §6）：同批三臂 · per-operation 会话 · 三臂契约各自行业最优写法 · 连接配置三臂同口径 · 跨机绝对值不可比、同批比值可比 · 百分比公式 · 色标分档与 📌 阈值
6. 只列用户点名的组；Query 组（WhereIn/Count/KeysetPage/WideQueryAll/IncludeJoin）与 Build 组默认不放，被点名才加
7. **参照样例**：`docs/性能汇报样例-2026-10-17.md`（2026-10-17 09:22 全量批交付原样存档，数据源 `bench/perfhub/results/history-20261017-092214.json`）——结构与格式以后续每次汇报向该样例对齐

## 详见

- **完整规范手册**：`.ai/lessons.md`
- **四系统导航**：`.ai/README.md`
- **发布流程**：`docs/发布规范.md`
- **贡献指南**：`CONTRIBUTING.md`
- **变更日志**：`CHANGELOG.md`
