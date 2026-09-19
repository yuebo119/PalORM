# PalORM 独立审计报告（2026-09-19）

> 基线：`dev@03658f3`（工作树清洁，`git status` 无未提交改动）。
> 来源：外部视角四阶段审计（发现与映射 → 证据审计 → 改进策略 → 任务计划）。覆盖范围：运行时 Core 45 文件 / SourceGen 16 文件 / 三 Provider 13 文件 / 测试 812 例（`[Test]` 声明实测计数）/ CI 四份 workflow / 构建与配置文件 / docs 抽样。
> 方法：三条并行深读路线（测试质量 / 文档准确性 / 核心代码）+ 主线程直读关键文件 + 交叉验证。**全程只读，未执行任何 `dotnet build/restore`，未修改任何代码。**
> 与 `audit-remediation-2026-09-19.md` 的关系：那是同日的**自审账本**（作者视角，任务已闭环 22 项）。本文是**独立复核**，两者结论互补且不冲突——自审已修复的 SHAPE-010/A1/A2 本文确认为已落地；本文的新增主张集中在交付链路与契约诚实度，而非运行时缺陷。

---

## 一、执行摘要

**整体健康度：B（核心工程质量 A−，交付与验证纪律 C+）**。运行时与源生成器代码是近年代所见最高水准之一：362 处 `await` 全部 `ConfigureAwait`、src/ 零 sync-over-async（grep 0 命中）、仅 2 处裸 `catch` 且均带指标计数、645 个测试方法中 0 个无断言、0 个"仅 `IsNotNull`"。拉低评级的是**交付链路**：一个以"Native AOT 零反射"为核心卖点的库，其发布流水线（`release.yml`）从不运行 AOT 验证——4 个 AOT 作业与包契约校验只存在于 PR 流水线（`ci.yml:192-314` 对比 `release.yml:89-135`）。更严重的是 README 把未发布的行为写成现状：徽章/安装指令是 `5.5.1`（`README.md:9,74-78`），而 `README.md:181` 声称 `PoolIdleTimeoutSeconds` 默认为 **0**，但 `git show v5.5.1:src/PalORM.Core/DbOptions.cs:50` 实测为 `= 30`，`dev` 领先最新 tag **66 个提交**。

**前 3 风险**

1. 发布路径缺 AOT / 密钥扫描 / 包契约验证（严重，O1）。
2. README 描述 NuGet 用户拿不到的行为（严重，DOC1）。
3. "Integration.Tests" 197 用例中仅 23 个（12%）触真库，且 `TestDb.PostgreSqlAsync/MySqlAsync`（`TestDb.cs:24,31`）零调用点，真库路径无 CI 断言确实执行（高，T1+T2）。

**前 3 机会**

1. 把 `ci.yml` 全部作业接入 `release.yml` 的 `needs` 链——半天工作量消除最严重发现。
2. `dotnet list package --vulnerable` + Dependabot + OIDC 发布凭据，补齐完全空白的依赖 CVE 信号。
3. 执行一次 `v5.6.0`/`v5.7.0` 发布并把 README 的"未发布"内容分栏，一次性消除 5 处文档漂移中的 3 处。

---

## 二、仓库地图

**项目类型**：已发布到 NuGet.org 的 .NET 类库（AGPL-3.0，`Directory.Build.props:35`），5 个包（Core / SourceGen / Sqlite / PostgreSql / MySql，见 `release.yml:113`）。
**成熟度**：生产级库（v5.x 迭代中，CHANGELOG 113 KB，12 份 ADR，登记缺陷编号 B8-B37 / ITM-xxx 共 62+ 条）。

**技术栈**：.NET 11（`global.json:3` = `11.0.100-preview.6`，`allowPrerelease: true`）· C# `latest` · `Features=runtime-async=on`（`Directory.Build.props:19`）· Roslyn `IIncrementalGenerator`（`Microsoft.CodeAnalysis.CSharp` 5.9.0）· Npgsql 10.0.3 / MySqlConnector 2.6.2 / Microsoft.Data.Sqlite.Core 11.0.0-rc.1 · TUnit 1.66.27（MTP 模式，`dotnet run` 执行，无 VSTest）· SonarAnalyzer.CSharp 10.34 · BenchmarkDotNet（本地 fork ProjectReference）。

**架构草图（主要数据流）**

```
用户实体 [Table]/[Column] ──编译期──► PalORMGenerator.cs + 7 个 Emitter
   (TableModel → RowFactoryEmitter / CommandFactoryEmitter / RegistryEmitter /
    SqlFileEmitter / SqlTemplateEmitter / AutoTaggingEmitter / MigrationEmitter)
        │ 生成 .g.cs：ModuleInitializer + RegistryFragment + 方言 SQL 常量 + Func<DbDataReader,T>
        ▼
PalORM_Runtime.Register(fragment) ──锁内全量重建──► FrozenDictionary×17 快照（不可变，Volatile 发布）
        ▼
DataSession<TProvider>（6 个 partial 文件）──EnterOperation()──► SessionOperationState 单活动操作门禁
        ├── QueryBuilder<T>（public struct，33 字段）──► AddClause / 持久化链表 ──► BuildSql ──► SqlShapeCache
        │        └── QueryBuilderExtensions（执行管线：拦截器/弹性/观测性三段式）──► GridReader
        ├── Crud / Transactions / Bulk / Schema / Query（直连命令路径，不经拦截器）
        └── IDbProvider（三实现）──► 方言引用符 / IsTransient / InitializeConnectionAsync
```

**关键目录**

| 路径 | 一行描述 |
|---|---|
| `src/PalORM.Core/`（45 文件） | 运行时核心。最大文件 `QueryBuilder.cs` 1208 行、`QueryBuilderExtensions.cs` 593 行执行管线 |
| `src/PalORM.SourceGen/`（16） | 生成器 + 分析器。`PalORMAnalyzer.cs` 1450 行 / 37 个 `DiagnosticDescriptor`（PALORM001-046） |
| `src/PalORM.{PostgreSql,MySql,Sqlite}/` | 方言 Provider；PG 侧含 LISTEN/NOTIFY（`PgNotificationListener.cs` 382 行）与 Advisory Lock |
| `test/` | 812 个 `[Test]` 声明：Core 271 / Integration 197 / SourceGen 186 + 5 个 AOT/包消费冒烟程序 |
| `bench/` + `tools/PalORM.PerfGate` | BenchmarkDotNet 基准 + C# 实现的回归门禁（比值判定，非绝对耗时） |
| `docs/`（39 文件） | 12 份 ADR + 中文规范文档（编码/测试/发布/性能/架构）+ `docs/review/` 自审账本 |
| `scripts/`（9） | `secret-guard.sh`（290 行 / 40 规则）、`stub-check.sh`、`test-package-contract.sh`、`run-mutation-tests.sh` |
| `.ai/ .zcode/ .serena/ .cortex/ .depwire/ .trellis/` | **全部 gitignored（tracked=0）**，但 AGENTS.md 称 `.ai/lessons.md` 为"高"优先级规范真源 |

**令我惊讶的内容**

1. 全仓库注释密度极高，几乎每条规则带缺陷 ID（ITM-xxx / B-xx / r11 轮次）与"为什么这样写"，包括实测字节数（`QueryBuilderExtensions.cs:160-179` 用 20 行注释拆解 272 B/查询的三项构成，并与 A/B 测量逐字节对齐）。
2. 项目已有一轮完整的自我审计（`docs/review/audit-remediation-2026-09-19.md`），本轮多个发现（`SqlShapeCache` 无界增长、克隆哈希旁路）是自己发现并整改的——这提高了对其余未整改项的可信度判断。
3. 反向发现：**没有**传统意义上的 bug 堆积，问题集中在流程与契约诚实度上。

---

## 三、审计报告

标注约定：**[事实]** = 有 file:line 可直接验证；**[判断]** = 我的解读，需你独立确认。

### 3.1 架构与设计

| # | 发现 | 位置 | 后果 | 严重性 |
|---|---|---|---|---|
| A2 | **[事实]** `QueryBuilder<T>` 是 `public struct`，33 个字段（估 ~220 B），且 `CloneForExecution` 用对象初始化器**手工列举 17 个字段**做复制 | `QueryBuilder.cs:11`（struct 声明）、`:23-77`（字段）、`:583-628`（克隆） | 字段注释自陈该复制点已出过事故：`:588` "r6-N1：克隆透传——r5-S2 曾在此断裂致条件分支死代码"。新增字段若忘记加入克隆列表，编译器**不报错**（其余字段取默认值），静默产生行为分叉。可维护性定时炸弹 | **高** |
| A1 | **[事实]** 查询执行管线两份近重复：`ExecuteForEachAsync` 与 `ExecuteQueryAsync` 各 ~75 行，结构（BuildSql→参数→QueryContext→Activity→Stopwatch→拦截器→弹性→try/catch/finally）逐行同构 | `QueryBuilderExtensions.cs:63-133` vs `:139-240`。项目在 `:59-62` 用 `SuppressMessage S3776` 承认："与 ExecuteQueryAsync 同构的管线形态" | 观测性/拦截器语义靠人肉双写维持。若给 `ExecuteQueryAsync` 补一个新指标标签，`ForEachAsync` 会静默缺失——而文档 `:44-47` 明确承诺"②③ 拦截器语义与 ToListAsync 一致 / 弹性管线覆盖同口径"，即契约已声明但结构未保证 | 中 |
| A4 | **[事实]** 公共 API 存在"参数校验后被丢弃"的旋钮 | `QueryBuilder.cs:513-520`（`WithMetrics(string name)` 校验 name 但从不存储，指标 operation 恒为 `:75/:150` 的 `const "select"`）；`DbOptions.cs:74-78`（`NamingConvention` 自陈"本运行时选项不参与该映射"）；`DbOptions.cs:61-66`（`PoolExplicitlyConfigured` 自陈"v5.6 起无消费者"） | 调用方写 `.WithMetrics("orders")` / `.WithMetrics("users")` 得到**完全相同**的指标面，无法按业务维度切分；设 `NamingConvention=SnakeCase` 静默无效。三个都是已发布 NuGet 包上的公共成员 | **高** |
| A3 | **[事实]** `DataSession<TProvider>` 单活动操作契约 + 每次 `CreateAsync` 开连接并跑 `SessionSetupSql`；无 DI 集成包（`Directory.Packages.props` 无 `Microsoft.Extensions.DependencyInjection.Abstractions`） | `SessionOperationState.cs:75-83`（重叠操作抛）、`DataSession.cs:88-111`（每次建连+初始化+setup SQL） | Web API 场景下每请求一会话 → 每请求一次连接打开 + N 条 PRAGMA/SET。`DataSession.cs:33-35` 自己实测 SQLite"建连 26.3µs / 2209B 每次"。缺一个官方作用域会话/池化工厂，用户会各自造轮子 | 中 |
| A5 | **[事实]** `ConnectionLease` 自 v5.6 起是纯 no-op 包装（`DisposeAsync` 返回 `ValueTask.CompletedTask`），但仍在每次命令执行时分配，并保留一段永不可达的异常处理 | `ConnectionLease.cs:22-23`；分配点 `QueryBuilder.cs:545,556`；不可达 catch `GridReader.cs:206-211` | 每次查询多 1 次堆分配 + 1 次虚调用换零行为。对一个把 40 B/查询写进注释的项目，这是自身纪律的例外 | 低 |
| A6 | **[判断]** 规范真源部分落在 gitignore 的 `.ai/` 目录 | `.ai/` tracked=0（实测 `git ls-files .ai` 为空）；AGENTS.md 把 `.ai/lessons.md` 列为"高"优先级、`.ai/test/prompt.md` 同理 | 任何非作者环境无法获得项目声称的最高规范；仓库内可审计的只有 `.editorconfig` + `docs/` | 中 |

### 3.2 代码质量

| # | 发现 | 位置 | 后果 | 严重性 |
|---|---|---|---|---|
| Q2 | **[事实]** `RegisterTransactionResource` 在前提不成立时静默 no-op：无 `else` 分支、不抛、不记录 | `SessionOperationState.cs:149-166` | 注册时机错误的事务资源（`_transactionOwner` 为空或非当前异步流）永不被 `DisposeTransactionResourcesAsync` 释放，且无任何信号。与该项目"响亮失败"哲学（`ITM-640`，见 `:311-327`）自相矛盾 | 中 |
| Q1 | **[事实]** 复杂度热点：`PalORMAnalyzer.cs` 1450 行单文件（37 个描述符 + 全部检查逻辑），`QueryBuilder.cs` 1208 行，`DataSession` 拆 6 个 partial | 见 §二 | 有 `S3776=error`（`.editorconfig:72`）与 33 处 `SuppressMessage`（全 src/）兜住单方法复杂度，因此是可管理的而非失控；但分析器单文件已成 PR review 瓶颈 | 低 |
| Q4 | **[事实]** 诊断 ID 空间有空洞：PALORM007/028/029/030/038/039 无定义 | grep 全 `src/PalORM.SourceGen/*.cs` | 用户看到文档/issue 引用 PALORM028 时无法归因。提交 `b0abe31` 提"PALORM001-040 完整化"，空洞来历未记录 | 低 |
| Q3 | **[事实]** 错误处理健康：src/ 仅 2 处裸 `catch`（`DataSession.cs:163`、`QueryBuilderExtensions.cs:254`），后者同时计入 `RecordInterceptorOnErrorFailure()`；清理异常统一"主异常保留 + `Exception.Data` 链"模式，跨 6 个文件一致（`DataSession.cs:410-419`、`GridReader.cs:195-227`、`TransactionCleanup.cs:16,32`、`Resilience.cs`） | — | 无需整改 | 健康 |

### 3.3 安全

| # | 发现 | 位置 | 后果 | 严重性 |
|---|---|---|---|---|
| O1 | **[事实]** 发布流水线不验证 AOT / 不扫密钥 / 不验包契约 | `release.yml:89-135`（仅 restore→build→unit→integration→pack→push）；`ci.yml:192-314` 有 4 个 AOT 作业 + `:229` `test-package-contract.sh` + `:16-51` gitleaks/secret-guard，release 全部不依赖 | NuGet 用户拿到的是**从未在 CI 里证明过能 Native AOT 编译并运行**的包；一旦某个改动破坏 trimmability/AOT（例如引入反射），照样发布。**全仓库最高优先级缺陷**（自审账本 `:执行记录` 亦证实 AOT 四矩阵是"本地验证"） | **严重** |
| S3 | **[事实]** `${{ secrets.NUGET_API_KEY }}` 内联进 `run:` 命令行；发布 job 无 `environment:` 人工审批 | `release.yml:130-135`（`--api-key ${{ ... }}`），`:24-29` permissions 仅 `contents: write`，无 environment | 密钥进入 runner shell 命令行（可被进程枚举 / 崩溃回显读到）；任何有仓库写权限的人推一个 `v*` tag 即向 NuGet.org 发布，无第二人确认。若 key 为 owner 级则可发布该账号下全部包 | **高** |
| S6 | **[事实]** 依赖 CVE 信号完全缺失：无 `.github/dependabot.yml`、无 renovate、无 `dotnet list package --vulnerable`、无 `NuGetAudit` 配置（`.github/` 仅 `BRANCH_PROTECTION.md` + `PULL_REQUEST_TEMPLATE.md` + `workflows/`；全仓 grep `NuGetAudit` 0 命中） | `.github/`、`Directory.Packages.props` | 4 个运行时第三方包（Npgsql / MySqlConnector / Microsoft.Data.Sqlite.Core / SQLite3MC）+ Roslyn 的新 CVE 只能靠人工"依赖升级轮"（`Directory.Packages.props:27-31` 记录 r21 轮）发现。项目已在 `:17-18` 手动跟过 2 个安全修复（GHSA-473q-m89c-ghf8），说明此类事件真实发生 | **高** |
| S1 | **[事实]** `Raw(string)` 不做 NUL/控制字符校验，而同文件的 `Tag` 拒绝 NUL，三 Provider 的标识符路径经 `IdentifierSafety.ThrowIfUnsafe` 拒绝 C0/C1/DEL | `QueryBuilder.cs:431-436`（仅 `ThrowIfNullOrWhiteSpace`）vs `:441`+`:1158-1170`（`Tag` 拒 `/*`、`*/`、`\0`）vs `SqliteProvider.cs:51` / `MySqlProvider.cs:79` / `PostgreSqlProvider.cs:79` | 项目自己在 `IdentifierSafety.cs:14-20` 记载"NUL 截断**已证**（ITM-584）；C0 换行/制表符（ITM-593）"。`Raw` 是**唯一**接受运行时来源任意字符串、又完全不设控制字符防线的 SQL 通道。若 `Raw` 参数含 `\0`，按项目自身实测会被驱动截断语句——`UPDATE t SET x=1 ` + NUL + `Raw("AND id=5")` → 全表更新 | **高** |
| S5 | **[事实]** `AuditInterceptor` 只覆盖实体 SELECT 管线与 QueryBuilder UPDATE；拦截器通知调用点 100% 位于 `QueryBuilderExtensions.cs`（grep `NotifyInterceptorsOn` → 13 处全在此文件） | `AuditInterceptor.cs:8-11`（自陈限制）；`IQueryInterceptor.cs` | `InsertAsync` / `DeleteAsync` / `SaveAsync` / `ExecuteNonQueryAsync` / Bulk 家族 / StoredProc / Migrate **全部不产生审计记录**。类名与 NuGet 包对外承诺的"审计"是部分覆盖：按它做合规留痕的团队会漏掉全部写入 | **高** |
| S4 | **[事实]** 凭据卫生**合格**：`.env.test`（含真实 LAN 凭据 + MySQL root）不在 git 索引、不在全历史（`git log --all -- .env.test` 为空，`.gitignore:64` 生效），CI 有 gitleaks 全历史兜底（`ci.yml:21-28`）。残留风险：明文 LAN 凭据长期驻留工作树、使用 root 账号（`.env.test:2`）、本地 pre-commit 需手动启用（`CONTRIBUTING.md:17`，无自动接线） | `.env.test:1-2`、`.gitignore:52,64` | 非泄露事件；属加固项。若某次 `git add -f` 或打包脚本改动手误即外泄，而 gitleaks 只在 push 后跑 | 低 |
| S2 | **[事实]** Sonar `S2077`（SQL 字面量拼接注入检测）**全仓库范围关闭**，理由仅覆盖 FormattableString 路径 | `.editorconfig:66`：`# FormattableString + 源生成器自动参数化，非字符串拼接` | 该规则恰好是 `Raw` / `ExecuteNonQuery(string)` / `SessionSetupSql` / `WithCte` 这类**真实字符串面**的唯一编译期绊线。关掉它使 S1 类不一致无法被自动发现。建议改为按调用点 `#pragma` 抑制（保留规则 error 级），使新增裸拼 SQL 必须显式报备 | 中 |

### 3.4 测试

| # | 发现 | 位置 | 后果 | 严重性 |
|---|---|---|---|---|
| T1 | **[事实]** "Integration.Tests" 实际以 SQLite 为主：`TestDb.SqliteAsync` 147 处调用、`TestDb.PostgreSqlAsync`/`MySqlAsync` **0 处**（尽管 `TestDb.cs:24,31` 定义了它们）；真库用例仅 23 个（手工 `TestEnvironment.Resolve*ConnectionString()`，分布在 7 个文件），占该项目 197 个 `[Test]` 的 **12%** | 实测 grep 汇总 | 三方言语义差异（锁、JSON、RETURNING、大小写、时区）主要在 SQLite 上被"跨方言"外推。同时 Provider 测试夹具是死代码 | **高** |
| T2 | **[事实]** CI 无"真库用例确实执行了"的断言，也无任何覆盖率工具（全仓 grep `coverlet` / `CollectCoverage` / `--collect` = 0 命中） | `ci.yml:136`（裸 `dotnet run`）、`release.yml:107` | 删掉 23 个真库用例、或某个测试程序加载 0 个用例，流水线仍报绿（历史报告 `skipped:0` 说明当前无跳过机制，因此失败会红，但"消失"不会）。无覆盖率地板意味着无法回答"核心业务逻辑周围有多少测试" | **高** |
| T3 | **[事实]** PG/MySQL 的 `BulkUpdateBatchAsync` 参数化批量 UPDATE 分支**零端到端执行**（测试文件自陈），仅字符串层钉住参数编号 | `test/PalORM.Core.Tests/BatchUpdateSqlBuilderTests.cs:4-5`；`BulkUpdateBatchAsync` 集成调用点仅 `BulkDataTests.cs:76,95,106`（SQLite，按设计回退逐行，见 `BatchUpdateParameterContractTests.cs:9`） | 参数编号/占位符形状是批量 UPDATE 最易出错处（错位即静默写错行），而它只被字符串比较守护，从未被真库执行验证 | **高** |
| T4 | **[事实]** 快照基线无人工审核门：`.github/CODEOWNERS` 不存在；13 份 `test/PalORM.SourceGen.Tests/Snapshots/*.snap` 已在 30 个提交中被重写 | 实测 `ls .github/CODEOWNERS` → 不存在 | 开发者可提交一份"重新生成"的基线，把语义回归正当化；CI 只与已提交基线比对，恒绿。多层防线（`SnapshotTests.cs:128,136-138,156-159,169-174`）挡的是 CI 自动改写与产物消失，挡不住人肉改基线 | 中 |
| T5 | **[事实]** 变异测试不阻塞任何合并：仅 `schedule: cron 0 3 * * 6` + `workflow_dispatch`（`mutation-tests.yml:5-8`），且 Core 只突变 5 个具名文件（`test/PalORM.Core.Tests/stryker-config.json:6-10`），不含 `CacheStore` / `GridReader` / `SqlShapeCache` / `CircuitBreaker` / `Resilience.cs` / `StoredProcBuilder` | — | 阈值确实会让排程作业失败（`threshold-break: 40`），但没人被阻断合并。覆盖面使"测试质量高"的结论只在 5 个文件上被机器证明过 | 中 |
| T8 | **[事实]** 无高扇出压力测试：全测试树仅 2 处 `Task.WhenAll`（`SessionConcurrencyTests.cs:35,41`），各 ≤3 任务 | — | `SessionConcurrencyTests` 用 TCS 握手编排交错（`:168-191,197-222,251-278,332-348`，设计优秀），但锁/AsyncLocal/ExecutionContext 交互在高并发下的非确定性行为无覆盖 | 中 |
| T6 | **[事实]** 测试顺序耦合被写进源码：`SqlShapeCacheGrowthTests.cs:62-63`"组内先跑的容量测试会把缓存填满拒写——先清空"，因 `:83-98` 填满 1024 上限（`SqlShapeCache.cs:24,78`）后不清空 | 同上 | `[NotInParallel]` 只排除并发不排除顺序；未来调整测试筛选/并行度会静默失效 | 低 |
| T7 | **[事实]** `ArchitectureInvariantTests.cs:69-80` 是源文件文本 `Contains("GetDefaultFilter")` grep（经 `:152-160` 的 `[CallerFilePath]` 相对路径定位），非行为测试 | 同上 | 一条注释或死局部变量即可满足；重命名即误红。但同文件 `:89-118` 的"新增公共注册项必须登记"反向守卫是有价值的 | 低 |
| — | **[事实] 优势**：断言质量经机械度量——90 个测试文件 / 645 个 `[Test]`，0 个方法无断言、0 个方法全部断言仅为 `IsNotNull`；`IsEqualTo` 673 次；~180 处 `Throws`；异常断言穿透到 `Data` 载荷同一性（`BulkCleanupTests.cs:30-31` `IsSameReferenceAs`）；仅 2 处真实 wall-clock sleep（`CacheStoreTests.cs:11`、`QueryCacheInjectionTests.cs:76`），零 `Random`，`Stopwatch` 只用于日志不用于断言 | — | 显著优于多数生产 OSS .NET 库 | 优势 |

### 3.5 性能

| # | 发现 | 位置 | 后果 | 严重性 |
|---|---|---|---|---|
| P4 | **[事实]** `WithCache` 命中返回**浅拷贝**：新 `List<T>` 但元素是与缓存共享的实体实例 | `QueryBuilderExtensions.cs:30-34`（命中路径 `new List<T>(cached)`）、`:207-210`（写入路径）、契约声明 `QueryBuilder.cs:467-468`（ITM-308） | 调用方 `foreach (var u in users) u.Status = X;` 会污染缓存并泄漏到其他请求。文档已诚实披露，但它是**默认行为**且 `IQueryCache` 无任何克隆/失效钩子。本报告中最需要在产品层决策的一条 | **高** |
| P2 | **[事实]** `PalORM_Runtime.Register` 对每个片段重建全部 17 个 `FrozenDictionary`（`Merge` 每次整体复制 current） | `PalORM_Runtime.cs:220-255`，`Merge` `:247-255` | K 个模型程序集 → O(K²·实体数) 启动工作，`FrozenDictionary` 构建本身昂贵。实际 K 通常 1-3，故低；但 `test/…/RegistryIncrementalCostTests.cs` 已存在说明项目关心此点 | 低 |
| P3 | **[事实]** 缓存容量纪律不一致：本轮整改给 `SqlShapeCache`（`:24,78` 上限 1024 拒写）和 `BoundedQueryCache`（`CacheStore.cs:63`）加了上界，但 4 个 `DataSessionCache` 字典与 `FormattableSqlFormatter.ShapeCache` 仍无上限 | `DataSessionCache.cs:14,18,24,28`；`FormattableSqlFormatter.cs:26-27` | **[判断]** 实际风险低：前者键为 `(Type, SqlDialect, bool, bool)`（受程序集实体类型数约束），后者键为编译期 `ldstr` 常量 Format 文本（受调用点数约束），均非用户输入驱动。但"同批整改、同一缺陷类、只改了两个"是不一致的，且 `bench/…/RegistryScaleTests.cs` 存在大量类型场景 | 低 |
| P1 | **[事实]** 只读弹性管线每查询 +272 B，且"默认配置不是直通"（`MaxRetries=3` / 阈值 5 就走执行器） | `QueryBuilderExtensions.cs:160-179`（三项构成逐字节实测）、`README.md:330` | 已测量、已在 README 披露、已给出直通逃生门（并诚实说明直通会失去 `InfrastructureTimeout` 包装）。**这不是缺陷，是 exemplary 的性能透明度** | 低（已披露） |

### 3.6 依赖与构建

| # | 发现 | 位置 | 后果 | 严重性 |
|---|---|---|---|---|
| D3 | **[事实]** 全新克隆无法按官方指令构建：`PalORM.slnx:20` 包含 `bench/PalORM.Benchmarks`，其 `csproj:26-27` ProjectReference `..\BenchmarkDotNet\src\...`，而该目录被 `.gitignore:31` 排除，`csproj:22-24` 注释自陈"其他开发者需自行 clone BDN fork 到此目录"——**`CONTRIBUTING.md` 全文未提此事**，而 `:20` 的官方指令正是 `dotnet build PalORM.slnx -c Debug` | 见左 | 新贡献者第一条命令即 restore 失败，且失败信息（NETSDK1009 找不到项目）不指向真因。CI 之所以绿是因为它用 `PalORM.ci.slnf`（排除 bench）——典型"作者机器可建"。**注：本条为文件证据推断，审计只读未执行构建；复现步骤见 M0-2** | **高** |
| D1 | **[事实]** 已发布的稳定版本号建立在预发布基线上：`Version=5.5.1`（`Directory.Build.props:40`）却依赖 `Microsoft.Data.Sqlite.Core 11.0.0-rc.1`（`Directory.Packages.props:11`）、SDK `11.0.100-preview.6`（`global.json:3`）、`Features=runtime-async=on`（`:19`，需运行时支持） | 见左 | .NET 11 尚未 GA（今日 2026-09-19），NuGet 上的"5.5.1 stable"对消费者意味着：拉一个 RC 传递依赖、且 `runtime-async` 语义在 GA 前后可能变化。这是产品决策而非疏漏，但需要对外声明 | **高** |
| D2 | **[事实]** `NuGet.Config:8-14` 让 dnceng 公共 `dotnet-tools` feed 与 nuget.org 同时满足 `Microsoft.CodeAnalysis.*` | 同左 | 版本已显式钉住（5.9.0），故无版本漂移；但分析器 DLL 的来源不唯一，`dotnet-tools` 是可变公共 feed。建议对 Roslyn 只留 nuget.org 一个 pattern | 低 |

### 3.7 开发与运维

| # | 发现 | 位置 | 后果 | 严重性 |
|---|---|---|---|---|
| O2 | **[事实]** `release.yml:137-144` 的 "Verify Release Accuracy" 在**已经 push 到 NuGet 之后**运行，且断言形如 `echo "Core 测试: $(dotnet run ... \| grep -oP '成功: \K\d+')"`——grep 空匹配时 `echo` 仍 exit 0；该步还为了抓计数把三套测试各**再跑一次** | `release.yml:130-144` | "验证"步骤结构上不可能让发布变红（唯一真断言是 `:148-153` 的 CHANGELOG 版本存在性）；同时浪费约 2× CI 分钟。若此处真发现问题，包已在 NuGet.org 上不可撤回 | **高** |
| O3 | **[事实]** 分支保护是**手工清单**而非可验证配置：`.github/BRANCH_PROTECTION.md:19` 明确"需在仓库 Settings 中配置"，仓库内无任何 `gh api` 校验脚本 | 同左 | 发布由任意 `v*` tag 触发（`release.yml:4-6`）。若 GitHub 设置漂移/未配置，仓库内无人能发现 | 中 |
| O4 | **[事实]** `perf-gate.yml:11-12` 无条件 `pull_request` 触发 + `:26-29` job 级 `if` 标签门控 → 非 `[perf]` PR 产生一个**零 job 且恒绿**的 "Performance Gate" 检查 | 同左 | 若它被设为必需状态检查，则性能门禁对普通 PR 是空壳。注释 `:9-12` 显示上一版是"每个 PR 白跑并必败"——修反了方向但没修到根 | 中 |
| O5 | **[事实]** 无 NuGet 包签名、无 SBOM、无 provenance attestation（`release.yml:109-135`） | 同左 | 供应链留痕不足；对 AGPL 库的消费者合规审查不利 | 中 |

### 3.8 文档

| # | 发现 | 位置 | 后果 | 严重性 |
|---|---|---|---|---|
| DOC1 | **[事实]** README 把未发布行为写成现状。徽章与安装指令 `5.5.1`（`README.md:9,74,76,78`），但 `:181` 声称 `PoolIdleTimeoutSeconds` 默认 **0** 并注"v5.6 起默认由 30 改为 0"；实测 `git show v5.5.1:src/PalORM.Core/DbOptions.cs:50` = `= 30`（HEAD 为 `:56` 无默认值）。`dev` 领先 tag **66 提交**；`CHANGELOG.md:5` 挂着 ~658 行 `[未发布·性能轮]`，其中已含 `v5.7.0` 基线重录标题（`:117`） | 见左 | 用户按 README 装 5.5.1，得到**相反**的池行为默认值，且现象表现为"偶发首查询慢 13 ms"（README `:181` 自己给的实测数）。同类：`:191` 会话级读连接复用、`:205` BulkMerge 8–10×、ADR-L 缓存隔离均只存在于未发布代码 | **严重** |
| DOC2 | **[事实]** `CONTRIBUTING.md:44` 声称"`AnalysisLevel=latest-all`——所有分析器规则启用"，实际：`src/PalORM.Core/PalORM.Core.csproj:12` = `latest-minimum`（并留 `:10` "Phase 1 放宽 CA 规则至建议，Phase 4 收紧"的历史 TODO），`src/PalORM.SourceGen/PalORM.SourceGen.csproj:17` = `none` + `:16` `EnforceCodeStyleInBuild=false` | 见左 | 被发布的**核心包**分析器口径最松，Provider 包反而保持 `latest-all`（未覆盖）；而**发射 SQL 的源生成器**（全项目风险最高处）分析全关。文档与代码相反 | **高** |
| DOC3 | **[事实]** AGENTS.md"启动清单（每次会话开始确认）"强制步骤含 `bash .ai/scripts/tech-debt-scan.sh` 与读 `.ai/lessons.md`，而 `.ai/` tracked=0 | `AGENTS.md` 启动清单 + `.gitignore` | 新克隆/新会话执行清单第 3、4 步必然失败（文件不存在）。README 亦引用这些文档 | **高** |
| DOC4 | **[事实]** 测试计数六处互斥。实测 `[Test]`：Core 271 / SourceGen 186 / Integration 197。声称：`docs/测试体系规范.md:33` "205/157/181"、`CHANGELOG.md:9` "260/190/180"、`docs/review/audit-remediation-2026-09-19.md:7` "280/197/197"、`docs/测试体系路线图.md:12` "156/104/156"、commit `e6cfbca` "279/196" | 见左 | 项目自己的 T11 规则（计数口径统一）与 B14 缺陷（AGENTS.md 登记"全仓库统一为 CI 通过数"）被违反。注：`[Arguments]`/`[MethodDataSource]` 展开使"声明数 ≠ 执行数"，这是数字必然分歧的根因，应在文档中声明口径而非各写各的 | 中 |
| DOC7 | **[判断]** `README.md:274`"FormattableString 参数化**杜绝** SQL 注入"。措辞强于 API 实际保证——`Raw()`（`QueryBuilder.cs:431`）、`ExecuteNonQuery(string)`、`SessionSetupSql`（`DbOptions.cs:117-118` 自陈"PalORM 不解析、不验证内容"）都是显式逃生门 | 见左 | 用户按"杜绝"理解而把运行时字符串传给 `Raw`。配合 S1（`Raw` 无控制字符防线）风险叠加 | 中 |
| DOC5 | **[事实]** `release-body.md:1` = "PalORM v5.2.0"，落后 4 个发布 | 同左 | 若被复用为模板会错标 | 低 |
| DOC6 | **[事实]** ADR-C 内部矛盾：`:32` 把已采纳方案称 C2，`:42` 同一方案称 C3，而选项表 `:27` 的 C3 是另一设计（"维持静态 + 上限"） | `docs/adr/ADR-C-*.md` | ADR 是"决策不可变"记录，自指不一致会削弱其权威 | 低 |
| — | **[事实] 优势**：README 全部 API 样例经逐一 grep 核实存在（注解 18 个、`DataSession`/`QueryBuilder`/`DbOptions` 成员、`Bulk*` 家族、`GridReader.ReadAsync/ReadFirstAsync`、`AcquireXactLockAsync`），**未发现虚构 API**；`README.md:374,383,399` 诚实展示 PalORM 延迟**劣于** Dapper/ADO.NET，无"X 倍快于 Dapper"或"零分配"式虚假宣称；`:638` "零运行时依赖"经 `PalORM.Core.csproj:18-20` 核实（仅 SourceLink `PrivateAssets=all`） | — | API 文档准确性罕见地高 | 优势 |

### 3.9 未覆盖 / 浅审区域（诚实声明）

以下文件仅做了结构级浏览，未逐行审计：`PgNotificationListener.cs`(382)、`DataSession_Bulk.cs`(540)、`MultiValueBulkInsert.cs`(278)、`CommandFactoryEmitter.cs`(488)、`RegistryEmitter.cs`(326)、`SqlFileEmitter.cs`(288)、`MigrationEmitter.cs`(272)、`tools/PalORM.PerfGate`(408+261)、`bench/` 全部 harness、`scripts/secret-guard.sh` 的 40 条正则、以及 `docs/` 中未被本文引用的约 30 个文件。

**审计全程只读，未执行 `dotnet build/restore`**——因此 D3（全新克隆无法构建）是基于 `PalORM.slnx:20` + `PalORM.Benchmarks.csproj:26-27` + `.gitignore:31` + `CONTRIBUTING.md:20` 四条文件证据的**推断**，而非亲自观察到的失败输出。复现方式列在 M0-2。

**优势总览（决定"保留什么"）**

- 错误处理与资源清理纪律跨全库一致（主异常保留 + `Exception.Data` 链）。
- 异步卫生 100%：零 sync-over-async、零 `async void`（`CA1849=error`）。
- 断言质量经机械度量近乎无浅测试；并发用例用握手编排而非 sleep。
- 快照防线多层且 CI 环境双守卫（`ci.yml:13` 清空变量 + `SnapshotTests.cs:136-138` 主动拒绝）。
- `release.yml:78-87` 的 tag↔props↔CHANGELOG 三点版本校验。
- 凭据卫生经全历史验证（`.env.test` 从未入库）。
- OTel 指标面用弱引用注册表规避 instrument 泄漏（`CacheStore.cs:48-110`，并记录了曾被 review 出泄漏的历史）。
- 12 份 ADR 真实运转，含 supersede 头部（ADR-C 正确指向 ADR-L）。
- 性能门禁用同轮比值而非绝对耗时、且"不可判定即 exit 1"（`perf-gate.yml:92-104`）。

**这些是本项目最不该被动的部分。**

---

## 四、改进策略

### 主题 1：验证只发生在作者机器上，不在发布流水线上

**覆盖发现**：O1, O2, D3, T1, T2, T5, T8, S6, A6, DOC3

这是解释力最强的主题。`ci.yml` 拥有 4 个 AOT 作业、包契约校验、密钥扫描——而 `release.yml` 一个都不依赖（O1）。CI 用 `PalORM.ci.slnf` 绕开 bench，所以"CI 绿"与"仓库可建"是两件不同的事（D3）。集成测试在 CI 里跑的其实是 SQLite 为主的那 88%（T1），且无人断言真库那 12% 确实执行了（T2）。

**目标状态**：*发布即证据*。任何一次 `v*` tag 发布，必须由一条显式依赖 PR 流水线全部作业的链驱动，且流水线对"作业是否真的执行了工作"负责（最低测试数断言、真库用例数断言、覆盖率地板）。

**原则**：可重复的验证 > 聪明的验证。宁可多花 20 CI 分钟，不可让一个未验证 AOT 的包进入 NuGet.org。

### 主题 2：公共契约的诚实度（文档 = 已发布物，而非当前 HEAD）

**覆盖发现**：DOC1, DOC2, DOC4, DOC5, DOC6, DOC7, A4, S5, P4

一个 66 提交领先于最新 tag 的 `dev`，其 README 用"v5.6 起…"句式陈述未发布行为（DOC1），API 里三个公共成员静默无效（A4），一个类名承诺了实现不提供的覆盖面（S5），一个默认语义会跨请求污染数据（P4）。文档不是"写少了"，而是**写在了错误的版本上**。

**目标状态**：README 的每一条能力陈述，要么在已发布版本中可复现，要么显式标 `[未发布 → v5.7.0]`。公共 API 上不存在"参数被丢弃"或"设置项不参与映射"的成员——要么实现，要么删除/`[Obsolete]`。

**原则**：库的可信度等于它最保守的那条宣称。

### 主题 3：安全纵深应依赖编译期绊线，而非人工纪律

**覆盖发现**：S1, S2, S3, S6, O3, O5, T4

`Raw` 缺控制字符防线（S1）之所以能长期存在，正是因为仓库唯一能自动发现它的规则被整体关闭（S2）。分支保护、快照基线审核、依赖 CVE 都是"人来保证"（O3, T4, S6）。

**目标状态**：安全属性由工具在 CI 里判定。`S2077` 恢复为 error + 逐调用点 `#pragma` 报备；`Raw` 复用 `IdentifierSafety`；发布凭据走 OIDC + `environment` 人工审批；Dependabot 开 CVE PR；`CODEOWNERS` 覆盖 `Snapshots/` 与 `bench/baselines/`。

**原则**：抑制应该指出**位置**，而不是取消**规则**。

### 主题 4：热路径正确性不应靠"手写镜像"维持

**覆盖发现**：A1, A2, Q2, A5

`CloneForExecution` 手工列举字段并有已发生的断裂史（A2），执行管线双写（A1）。这类问题的共同形态：契约写在注释/文档里，结构不强制。

**目标状态**：`QueryBuilder<T>` 的复制/克隆由单一构造点或字段全集机制保证；执行管线只有一个实现体 + 一个物化策略（`List<T>` 收集 vs 逐行回调）。

**原则**：让正确的那条路径是唯一可写出的那条路径。

### 主题 5：预发布运行时基线上的稳定版本承诺

**覆盖发现**：D1, DOC1（部分）

`Version=5.5.1` 无 `-preview` 后缀，但 SDK / Sqlite 驱动 / `runtime-async` 全是预发布。要么对外明示，要么改版本号语义。**这是产品决策，列入开放问题 Q2。**

### 明确不修的东西（权衡声明）

| 不修 | 理由 |
|---|---|
| 覆盖率追到 80%+ | 645 个测试已 0 无断言、错误路径实测充分（~180 处 `Throws`）。设一个**地板**（line ≥70% / branch ≥60%）防退化即可，为提高数字补测试在该项目是负价值 |
| `struct QueryBuilder<T>` 改回 class | 这是项目最核心的性能决策（`QueryBuilder.cs:7-10` 有据），代价是复制语义。本审计只主张**机械化克隆**（A2），不主张推翻 struct |
| 引入 LRU / 分层缓存 | `BoundedQueryCache` 的"满则拒写 + TTL 回落"是显式取舍（`CacheStore.cs:20-31`，含并发软上限的诚实披露）。缓存未命中正确性中性，加锁 LRU 是净损失 |
| 消除 `DateTime.UtcNow` 的 NTP 回拨风险 | 项目已在 `ITM-538`/`ITM-586`（`CacheStore.cs:174-176`）分析过：换 `TickCount64` 需处理 49.7 天回绕。判断正确，不动 |
| 给 bench / PerfGate 补测试 | `IsPackable=false`、仅本地/排程使用的内部工具（`PalORM.Benchmarks.csproj:6`），且 `perf-gate.yml` 已把"不可判定"设为 `exit 1` |
| 把 `.ai/` 入库 | 它是本地 AI 工具链（`.ai/.zcode/.serena/.cortex/.trellis` 全部有意 gitignore）。正确解法是把 AGENTS.md 里对它的**强制依赖**改成可选（DOC3），而非公开私有工具 |
| 迁移到 .NET 10 LTS | 项目定位就是"吃 .NET 11 前沿能力"（`runtime-async`、`Lock`、`FrozenDictionary`）。改为要求：对外声明预发布基线（D1） |

### "完成"的可衡量信号

| 信号 | 判据 |
|---|---|
| S1 | `release.yml` 的 publish job 对 `ci.yml` 全部 8 个作业有 `needs` 依赖；连续 3 次发布零人工补救 |
| S2 | `git show <最新tag>:README.md` 的每一条能力陈述能在该 tag 源码中复现（抽查 10 条，0 反例） |
| S3 | CI 存在 3 条硬断言：`ExternalDatabase` 用例数 ≥ 基线值、各测试项目 `passed ≥ 地板`、`skipped == 0`；任一不满足即红 |
| S4 | `dotnet list package --vulnerable --include-transitive` 在 CI 阻断，且 Dependabot 对 Critical/High 有 PR |
| S5 | 全新 `git clone` 后 `dotnet build PalORM.slnx -c Debug` 成功，或 `CONTRIBUTING.md:20` 改为一条可执行指令 |
| S6 | 公共 API 上不存在丢弃入参的成员；`grep` 全 `src/` 无"自陈无消费者/不参与映射"的公共成员（0 命中） |
| S7 | `AuditInterceptor` 覆盖面文档与实际调用点集合一致（grep 覆盖 ≥5 文件，或类名/README 收窄承诺） |
| S8 | 变异测试覆盖 `src/PalORM.Core` 全部 ≥200 行文件（当前 5 个具名文件） |
| S9 | 全仓 `SuppressMessage` 数不增加（当前 33），且 `#pragma warning disable` 不含任何 `.editorconfig` 已设 `= none` 的规则 |

---

## 五、任务计划

### 里程碑 0 · 安全网（先于任何重构）

| ID | 标题 | 受影响文件 | 验收标准 | 工作量 | 变更风险 | 依赖 |
|---|---|---|---|---|---|---|
| **M0-1** | 发布流水线复用 PR 全部作业 | `.github/workflows/release.yml:23-28` | publish job 依赖 `security/build/unit/integration/gate/aot-sqlite/aot-package-consumer/aot-postgresql/aot-mysql`（reusable workflow 或 `workflow_call`）；故意破坏一次 AOT 能阻断发布 | L | 低（只增门禁） | — |
| **M0-2** | 复现并修复"全新克隆不可建" | `PalORM.slnx:20`、`bench/PalORM.Benchmarks/PalORM.Benchmarks.csproj:26-27`、`CONTRIBUTING.md:20` | 干净临时目录 `git clone` → `dotnet build PalORM.slnx` 成功；**或**官方指令改为可执行命令且实测通过（两条都算达标） | M | 低 | — |
| **M0-3** | CI 加"确实执行了"断言 | `.github/workflows/ci.yml:87-136`、新增 `scripts/assert-test-counts.sh`、新增 `bench/baselines/test-counts.json` | 解析 TUnit 报告：三项目 `passed ≥ 地板`、`skipped == 0`、`ExternalDatabase` 用例数 ≥ 23；把任一数字调零能变红 | M | 低 | M0-2 |
| **M0-4** | 依赖 CVE 与更新信号 | 新增 `.github/dependabot.yml`、`ci.yml` 新步 | `dotnet list package --vulnerable --include-transitive` 有 Critical/High 即失败；Dependabot 对 `Directory.Packages.props` 开 PR | S | 低 | — |
| **M0-5** | CODEOWNERS 守护可被"正当化"的基线 | 新增 `.github/CODEOWNERS` | `Snapshots/*.snap`、`bench/baselines/*.json` 改动需第二人批准；试改一份 .snap 观察是否要求 review | S | 低 | — |

### 里程碑 1 · 关键修复

| ID | 标题 | 受影响文件 | 验收标准 | 工作量 | 风险 | 依赖 |
|---|---|---|---|---|---|---|
| **M1-1** | `Raw` 复用标识符安全防线 | `src/PalORM.Core/QueryBuilder.cs:431-436`、`IdentifierSafety.cs:28`；参照 `QueryBuilder.cs:1158-1170` | `Raw("a\0b")` / 含 C0 的输入抛 `ArgumentException`；新增 3 用例（NUL/换行/DEL）+ 1 反向用例（含引号合法片段必须通过）；ITM-584/593 口径写入文档；全量测试不回退 | S | 中（可能打断依赖宽松 `Raw` 的用户——CHANGELOG 标 breaking-behavior） | — |
| **M1-2** | 发布凭据改 OIDC + 人工审批 | `release.yml:13-35,130-135` | `run:` 中无 `secrets.*` 内联；job 有 `environment: production`（required reviewers）；NuGet key 为 `PalORM.*` 前缀作用域 | M | 中（需 NuGet.org 侧配置） | M0-1 |
| **M1-3** | 把"验证"移到推送之前，去掉重复跑测 | `release.yml:97-153` | 版本/计数/CHANGELOG 校验全部前置于 `pack`；测试只跑一次；计数复用 M0-3 脚本解析报告文件而非 grep stdout；grep 空结果能失败 | M | 低 | M0-1, M0-3 |
| **M1-4** | 发布 v5.6.0/v5.7.0 并把 README 对齐到已发布物 | `Directory.Build.props:40`、`README.md:9,74-78,181,191,205,274,330`、`CHANGELOG.md:5`、`release-body.md:1` | README 每条 v5.6/v5.7 陈述在该 tag 源码可复现；"杜绝 SQL 注入"改为"默认参数化 + 显式逃生门清单"；`release-body.md` 删除或改为模板 | L | 中（发布不可逆，须 M0-1/M1-2/M1-3 先落地） | M0-1, M1-2, M1-3 |
| **M1-5** | 恢复 `S2077` 为 error + 逐点报备 | `.editorconfig:66`、`QueryBuilder.cs:431`、`DataSession.Query.cs`/`DataSession.Schema.cs`/`StoredProcBuilder.cs`/`QueryBuilderExtensions.cs` 拼串点 | `.editorconfig` 无 SQL 注入类规则的 `= none`；剩余违规点带 `#pragma warning disable S2077` + 一行理由；构建仍 0 警告 | M | 低 | M1-1 |
| **M1-6** | Core / SourceGen 分析器口径对齐 | `src/PalORM.Core/PalORM.Core.csproj:10-12`、`src/PalORM.SourceGen/PalORM.SourceGen.csproj:16-17`、`CONTRIBUTING.md:44` | Core 回到 `latest-all`（或 `.editorconfig` 显式列出保留的豁免）；SourceGen 至少 `latest-recommended` + `EnforceCodeStyleInBuild=true`；文档与 props 实测一致 | L | **高**（会一次性爆出一批 CA 诊断；需分批修 + 必要时逐点 SuppressMessage） | M0-5 |

### 里程碑 2 · 高杠杆改进

| ID | 标题 | 受影响文件 | 验收标准 | 工作量 | 风险 | 依赖 |
|---|---|---|---|---|---|---|
| **M2-1** | 真库测试面从 12% 提升到有地板 | `test/PalORM.Integration.Tests/*`（147 处 `TestDb.SqliteAsync`）、`src/PalORM.Testing/TestDb.cs:24,31` | 每条方言差异路径（锁/JSON/RETURNING/列序/时区/大小写）有 ≥1 PG + ≥1 MySQL 用例；`TestDb.PostgreSqlAsync/MySqlAsync` 有 ≥20 调用点（不再是死代码）；`ExternalDatabase` 计数写入 M0-3 地板 | XL | 中（真库用例是新 flake 来源） | M0-3 |
| **M2-2** | 批量 UPDATE 参数化分支真库端到端 | `test/PalORM.Core.Tests/BatchUpdateSqlBuilderTests.cs:4-5`、新增 `test/PalORM.Integration.Tests/BulkUpdateBatchDialectTests.cs` | PG + MySQL 各 ≥3 用例真执行并断言**每行每列最终值**（非仅 affected rows）；删除 `:4-5` 的"无端到端"自陈 | L | 低 | M2-1 |
| **M2-3** | `CloneForExecution` 字段全集机械化 | `src/PalORM.Core/QueryBuilder.cs:23-77,583-628` | 反射-free 的生成侧克隆（或把可变字段收进单个可复制状态 record）；新增 PALORM 诊断或源级守卫，使"新增字段未进克隆"变编译期错误；用 `r6-N1` 场景（`_isolationLevel` 漏传）写回红测试 | L | **高**（热路径结构改动，须靠 bench 守护） | M0-5, M2-7 |
| **M2-4** | 执行管线单实现化 | `src/PalORM.Core/QueryBuilderExtensions.cs:63-133,139-240` | 抽取单一 `RunPipelineAsync`（观测性+拦截器+弹性+资源管理），物化差异由 `IMaterializer<T>` 承载；删除 `:59-62` 的 S3776 抑制；`:44-47` 承诺的 4 条语义由一份契约测试同时对两条路径验证 | L | 中 | — |
| **M2-5** | 拦截器覆盖面扩到写路径（或改名字） | `DataSession.Crud.cs`、`DataSession_Bulk.cs`、`StoredProcBuilder.cs`、`AuditInterceptor.cs:8-11`、`README.md` | **A**：接入写路径，`grep NotifyInterceptorsOn` 覆盖 ≥5 文件，文档限制段删除；**B**（低成本）：改名 `SelectAuditInterceptor` + README 醒目声明 | L / S | 中（A）/ 低（B） | — |
| **M2-6** | 清理"参数被丢弃"的公共旋钮 | `QueryBuilder.cs:513-520`、`DbOptions.cs:61-66,74-78` | `WithMetrics(name)` 的 name 成为指标标签或参数删除；`NamingConvention`/`PoolExplicitlyConfigured` 移除或 `[Obsolete]`（附 ADR + 移除版本）；全仓 grep 无"自陈不生效"的公共成员 | M | 中（破坏公共 API，需 CHANGELOG breaking 条目） | M1-4 |
| **M2-7** | 缓存值语义安全化 | `QueryBuilder.cs:467-481`、`QueryBuilderExtensions.cs:30-34,207-210`、`CacheStore.cs` | 提供 `WithCache(key, ttl, Clone)` 或 `IQueryCache.CloneOnRead`；新增 PALORM 诊断：`WithCache(...)` 之后对结果实体属性赋值报 warning；默认行为变更需 ADR | L | 中（性能与语义双向影响，需 bench） | M0-5 |
| **M2-8** | 依赖来源与 RID 卫生 | `NuGet.Config:8-14`、`Directory.Packages.props:11` | `Microsoft.CodeAnalysis.*` 仅 nuget.org；`Sqlite.Core` 在 .NET 11 GA 后切 stable 的跟踪项登记进 `docs/` | S | 低 | — |

### 里程碑 3 · 质量与润色

| ID | 标题 | 受影响文件 | 验收标准 | 工作量 | 风险 | 依赖 |
|---|---|---|---|---|---|---|
| **M3-1** | 覆盖率地板（不设高目标） | 三测试 csproj + `ci.yml` | coverlet 接入，`line ≥70% / branch ≥60%` 低于即红；基线写进 `bench/baselines/` 同级；文档记"地板为防退化非达标" | M | 低 | M0-3 |
| **M3-2** | 变异测试变守护 | `.github/workflows/mutation-tests.yml:5-8`、`test/*/stryker-config.json:6-10` | 突变集覆盖 src/PalORM.Core 全部 ≥200 行文件；每周结果作为可查询趋势；阈值跌破在 `docs/review/` 立项 | L | 低（不阻塞合并，仅扩面） | M3-1 |
| **M3-3** | 高扇出压力测试 | 新增 `test/PalORM.Core.Tests/SessionFanOutStressTests.cs` | 单会话 64 交错操作（受门禁必红）+ 64 会话并发各 16 操作 × 10k 迭代，确定性种子、无 sleep、断言最终态 | M | 中（易超时，需调 job budget） | — |
| **M3-4** | 解耦快照增长测试的顺序依赖 | `test/…/SqlShapeCacheGrowthTests.cs:62-63,83-98` | 用独立隔离计数器替代"先填满再手工 Clear"；`grep "先清空" test/` 0 命中 | S | 低 | — |
| **M3-5** | `ArchitectureInvariantTests` 转行为断言 | `test/PalORM.Core.Tests/ArchitectureInvariantTests.cs:69-80` | 过滤谓词的行为（同一 tenant/softDelete 组合下三条 Get* 路径 + Bind 生成的 SQL 一致）用 DryRun 断言；文本 grep 仅保留反向登记守卫 `:89-118` | M | 低 | — |
| **M3-6** | 删除 no-op 抽象与 legacy 载荷 | `ConnectionLease.cs`、`GridReader.cs:14,28,206-211`、`QueryBuilder.cs:540-557`、`PalORM_Runtime.cs:16-20,44-48,99-100,124-125` | `ConnectionLease` 消失（直传 `DbConnection`），每查询分配 −1；legacy 字典有 ADR 声明的移除版本或保留理由 | M | 中（触及公共 `RegistryFragment`） | M1-4 |
| **M3-7** | 测试计数口径单一真源 | `docs/测试体系规范.md:33`、`CHANGELOG.md:9`、`docs/review/audit-remediation-2026-09-19.md:7`、`docs/测试体系路线图.md:12,153` | 声明"CI 通过数（含 `[Arguments]` 展开）"为唯一口径 + 一处生成、其余引用；grep 全 docs 无第二个手填数字 | M | 低 | M0-3 |
| **M3-8** | 文档一致性小项 | `ADR-C:32,42`、`docs/架构设计.md:312,315,377`、`release-body.md`、`CONTRIBUTING.md:44` | ADR-C 编号自洽；依赖表与 `Directory.Packages.props` 一致；stale 模板删除 | S | 低 | M1-4 |

### 快速获胜（高影响 / S 工作量，可立即执行）

| # | 任务 | 一句话 |
|---|---|---|
| QW-1 | **M0-4** | 依赖 CVE 阻断 + Dependabot——补一个完全空白的安全维度 |
| QW-2 | **M0-5** | `.github/CODEOWNERS`——10 行文件，堵住唯一"可被正当化的回归通道"（30 次 .snap 重写无审核） |
| QW-3 | **M1-1** | `Raw` 加 `IdentifierSafety.ThrowIfUnsafe` + NUL 检查——2 行代码，闭合与 `Tag`/Provider 路径的加固不一致，防护的是项目**自己实测证明过**的截断向量 |
| QW-4 | **M2-5-B** | `AuditInterceptor` 改名或在 README 把"仅 SELECT"提到特性列表——消除名称过度承诺 |
| QW-5 | **M2-6 局部** | `WithMetrics(name)` 丢弃入参——若不想加标签，删参数只需 1 行 |
| QW-6 | **M3-8 局部** | 删 `release-body.md`、改 `CONTRIBUTING.md:44` 的 `latest-all` 陈述——各 1 行 |
| QW-7 | **M0-2 诊断部分** | 在 `CONTRIBUTING.md:19` 后加一段"clone BenchmarkDotNet fork 到 bench/"——若决定不重构解决方案，这 3 行文字消除新贡献者 100% 的首次失败 |

### 前 3 个任务的实现草图

#### M0-1 · 发布流水线复用 PR 全部作业

- **方法**：GitHub Actions 无法用 `needs` 跨 workflow，考虑过 `workflow_run` 但**否决**（见陷阱）。改用把 `ci.yml` 的 AOT/契约/安全作业抽成 `.github/workflows/verify.yml`，同时可被 `push` / `pull_request` / `workflow_call` 触发；`release.yml` 拆成两个 job：`verify`（`uses: ./.github/workflows/verify.yml`）→ `publish`（`needs: verify`）。
- **关键步骤**
  1. 拆分时保持 `PalORM.ci.slnf` 的还原口径不变，避免 AOT 作业在 reusable workflow 里丢 `global-json-file` 上下文（`ci.yml:63,82,96` 每处 setup 都要带）。
  2. AOT 作业中 `aot-package-consumer` 依赖 `artifacts/packages`（`ci.yml:223-229`）——若 `release.yml` 的 `pack` 步在它之后会形成循环，须重排为 `pack → verify-aot → push`。
  3. 在 `verify.yml` 末尾加一个 `summary` job 输出被验证的 commit SHA，`release.yml` 断言它等于 `github.sha`，防止"验的是别的提交"。
  4. 首次接线后**故意**引入一个反射调用（`Activator.CreateInstance`）跑一次完整发布，确认 AOT 作业真的能阻断，再撤销。
- **陷阱**：`workflow_run` 在**默认分支**上运行并携带默认分支的 `GITHUB_TOKEN` 权限——若 `publish` 依赖它，攻击者可通过 PR 影响发布路径。所以**必须**用 `workflow_call` 在同一 run 内组合。另注意 `verify.yml` 里 PG/MySQL service 容器的健康检查超时（`ci.yml:98-122`）在合并后会被多个 job 并行拉起，需保留各自的 `ports` 映射避免冲突。

#### M0-3 · CI"确实执行了"断言

- **方法**：TUnit/MTP 会输出 `TestResults/**.tunit-report.json`（本机已有历史文件可作 schema 参照，见 `test/PalORM.Integration.Tests/bin/Debug/net11.0/TestResults/*.tunit-report.json`）。写 `scripts/assert-test-counts.sh`：读三项目报告，用 `node -e` 或 `jq` 解析 `total/passed/failed/skipped`，与新增的 `bench/baselines/test-counts.json` 地板比对；额外统计 `properties.Category == "ExternalDatabase"` 的用例数。
- **关键步骤**
  1. 三处 `dotnet run --project test/...`（`ci.yml:88,91,136`）后各追加一步 `bash scripts/assert-test-counts.sh <project> <report-dir>`。
  2. 地板值取"当前实测 − 5"，避免并行展开抖动。
  3. `skipped != 0` 直接失败——当前无跳过机制（实测 `skipped:0`），任何跳过都是新引入的静默降级。
  4. 把该脚本加进 `scripts/test-quality-scripts.sh` 的夹具集（`ci.yml:190` 已消费它），让防线本身有回归测试（对齐项目 B13/B14 纪律）。
- **陷阱**：`bin/` 在 `.gitignore` 内且 CI 全新 checkout 后不存在历史报告——路径必须从 `dotnet run` 的实际输出目录解析，**不要硬编码 `Debug`**（CI 用 `-c Release`）。`[Arguments]` 展开会让"声明数"与"执行数"不同，断言必须基于**报告文件**而非 `[Test]` grep。不要在 `release.yml` 里重复实现——M1-3 要求它复用同一脚本。

#### M1-1 · `Raw` 复用标识符安全防线

- **方法**：在 `QueryBuilder.Raw`（`QueryBuilder.cs:431-436`）的空/白检查之后，加一段与 `ValidateSqlComment`（`:1158-1170`）同族的 `ValidateRawSqlFragment(string)`：拒绝 `\0` 与 C0/C1/DEL（复用 `IdentifierSafety` 的字符区间判定，但**不**拒绝引号/反引号——`Raw` 的合法用法必然含它们）。
- **关键步骤**
  1. 把 `IdentifierSafety.cs:28` 的字符区间判定提为 `internal static bool IsControlChar(char)`，让 `IdentifierSafety.ThrowIfUnsafe` 与新 `Raw` 守卫共享同一谓词（单一真源，符合本项目 `GEN-012`/`CORE-010` 一贯口径）。
  2. 新增 3 用例（NUL、`\n`、`DEL`）+ 1 反向用例（含引号的合法 Raw 片段必须通过）。
  3. 先跑 `grep -rn '\.Raw(' test/ | grep '\\n'` 确认无既有测试依赖多行/NUL 片段，再跑全量测试。
  4. 更新 `README.md:274` 的安全段与 `docs/API参考.md`，把"Raw 拒绝控制字符"写成契约。
  5. `CHANGELOG.md` 记为 **breaking-behavior**（不是 breaking-API）。
  6. S3 反向验证（项目纪律）：撤回守卫，确认 3 条新用例确定性回红。
- **陷阱**：**不要**在 `Raw` 里做引号平衡或关键字黑名单——那是不可判定问题，且会误伤合法片段。另注：`Raw` 的**位置语义**已在 `:426-430` 文档化为"尾部追加"，本改动不触碰位置、只收紧字符面——两者不要混在同一次提交（B9 纪律：方案文字与实施严格对齐）。

---

## 六、开放问题（需人类决策）

1. **发布节奏**：`dev` 领先 `v5.5.1` 已 66 提交，`CHANGELOG.md:5` 挂着 ~658 行 `[未发布·性能轮]`。是否立即发布 v5.6.0/v5.7.0？还是有意保持"dev 长期领先"的发布窗口策略？这决定 M1-4 是"发版"还是"给 README 加未发布标记"。
2. **.NET 11 GA 前的对外承诺**：`Version=5.5.1`（无 `-preview` 后缀）依赖 `Microsoft.Data.Sqlite.Core 11.0.0-rc.1` 与 `11.0.100-preview.6` SDK（`global.json:3` `allowPrerelease: true`）。NuGet.org 上的这个版本号是否对消费者构成"stable"误承诺？应在 README 安装段落明示预发布基线，还是改产 `-preview` 后缀版本？（M1-4 / D1）
3. **`WithCache` 默认语义**：接受"浅拷贝 + 文档披露"作为最终产品决策，还是引入 `CloneOnRead`？后者对已发布包是行为变更，需要一次带迁移指引的版本。**这条决定 P4 是"已披露的取舍"还是"待修的缺陷"。**
4. **`IQueryInterceptor` 是否应覆盖写路径**：修（M2-5-A，触及 CRUD/Bulk/StoredProc 四条路径）还是收窄承诺（M2-5-B，改名 + 文档）？若面向合规客户，前者几乎是必需的。
5. **弃用候选清单**：`DbOptions.NamingConvention`（自陈不参与映射）、`DbOptions.PoolExplicitlyConfigured`（自陈 v5.6 起无消费者）、`RegistryFragment.CommandSqls` 与 `CreateTableSql`（自陈运行时从不消费，ADR-I/ADR-J 未声明移除版本）、`IRowFactory<T>`（ADR-H，已是 PALORM900）。各自的保留窗口与移除版本是什么？需要一次性写进 CHANGELOG 的 deprecation 表。
6. **单活动操作契约**：`DataSession<TProvider>` 是否长期保持"一会话一活动操作"（`SessionOperationState.cs:75-83`）？若目标场景是 ASP.NET Core 高并发，是否提供官方 DI 包 + 作用域会话工厂（A3），以及是否重新考虑允许并发读？这是产品定位问题，不是工程问题。
7. **`.ai/` 的地位**：AGENTS.md 把 `.ai/lessons.md`（62 条缺陷）列为"高"优先级规范真源。它是有意不公开（则 AGENTS.md 措辞应改为"作者本地工具，新克隆不可执行"），还是应当精选公开（则 `docs/` 应吸收其内容）？当前状态对任何非作者贡献者是不可发现的。
8. **性能目标量化**：`perf-gate.yml:92-98` 明确"不设绝对耗时阈值"，用同轮比值 + 分配字节数。这是否是长期承诺？若要在 README 对外承诺"每查询 ≤ X B 分配 / 相对 ADO.NET ≤ Y×"，需要一个可断言的目标值——目前 `bench/baselines/perf-baseline.json` 只有观测值，没有目标值。

---

## 附录 A · 审计方法与可复现命令

本文所有 **[事实]** 类断言可由下列只读命令复核（无一条修改工作树）：

```bash
# 版本漂移（DOC1）
git tag --sort=-v:refname | head -1          # → v5.5.1
git rev-list --count v5.5.1..HEAD            # → 66
grep -n '<Version>' Directory.Build.props    # → 5.5.1
git show v5.5.1:src/PalORM.Core/DbOptions.cs | grep -n PoolIdleTimeoutSeconds
grep -n PoolIdleTimeoutSeconds src/PalORM.Core/DbOptions.cs

# 凭据卫生（S4）
git ls-files --error-unmatch .env.test       # → 不在索引
git log --all --oneline -- .env.test         # → 空
git check-ignore -v .env.test                # → .gitignore:64

# 拦截器覆盖面（S5）
grep -rn "NotifyInterceptorsOn" src/*/*.cs | grep -v "/obj/\|/bin/"
#   → 13 处，全在 QueryBuilderExtensions.cs

# 集成测试真库占比（T1）
grep -rho "TestDb\.[A-Za-z]*" test/ --include=*.cs | sort | uniq -c
grep -rc '\[Property("Category", "ExternalDatabase")\]' test/PalORM.Integration.Tests/*.cs

# 测试计数（DOC4）
for d in test/*/; do printf "%s %s\n" "$d" "$(grep -rho "\[Test" "$d" --include=*.cs | wc -l)"; done

# 覆盖率 / CVE 工具缺失（T2 / S6）
grep -rn "coverlet\|CollectCoverage" --include=*.yml --include=*.csproj --include=*.props . | grep -v "/obj/\|/bin/"
ls .github/CODEOWNERS .github/dependabot.yml

# bench 克隆缺口（D3）
grep -n "BenchmarkDotNet" PalORM.slnx bench/PalORM.Benchmarks/PalORM.Benchmarks.csproj
git check-ignore -v bench/BenchmarkDotNet

# 分析器口径（DOC2）
grep -n "AnalysisLevel\|EnforceCodeStyleInBuild" src/PalORM.Core/PalORM.Core.csproj src/PalORM.SourceGen/PalORM.SourceGen.csproj

# 异步卫生（优势）
grep -rn "\.Result\b\|\.Wait()\|GetAwaiter().GetResult()" src/*/*.cs   # → 0 命中
```

**grep 纪律提示**：本仓库 `bin/`、`obj/` 内含构建产物副本，任何 src/ 检索都必须 `--include=*.cs` 并排除 `/obj/`、`/bin/`，否则计数会被 `.xml` 文档文件放大 6–8 倍。

## 附录 B · 发现索引

| 严重性 | 编号 |
|---|---|
| 严重 | O1（发布不验 AOT）、DOC1（README 陈述未发布行为） |
| 高 | S1、S3、S5、S6、A2、A4、T1、T2、T3、D1、D3、O2、DOC2、DOC3 |
| 中 | A1、A3、A6、Q2、S2、T4、T5、T8、O3、O4、O5、DOC4、DOC7、P4 |
| 低 | A5、Q1、Q4、T6、T7、P1、P2、P3、D2、DOC5、DOC6、S4 |
