# PostgreSQL 极致优化 · 终极方案与任务清单（step7 总纲）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 subagent-driven-development（推荐）或 executing-plans 逐任务实施。步骤用 `- [ ]` 复选框跟踪。
>
> **Goal:** 把 PalORM 对 PostgreSQL 的支持压榨到实测可证的天花板——分配与时延双口径、每项带 A/B 证据、无理论收益占位。
>
> **Architecture:** 全部改动落在既有三处（`PalORM.Core` 查询/会话路径、`PalORM.SourceGen` emitter、`PalORM.PostgreSql` provider），不引入新文件新依赖；每任务独立可测、独立可审；性能宣称只认顺序交替 A/B（本总纲 §0 的协议）。
>
> **Tech Stack:** .NET 11 / C# 15 · TUnit（Microsoft.Testing.Platform）· Npgsql 10.0.3 · PG 18.4 真库（`PALORM_PG_CONNECTION`）· PerfHub。
>
> **Spec（证据基座）:** `docs/性能优化方案-step6-pg.md`（S1/S2/S3/PG-4 已落地 + 探针一~五原始数据）· `docs/性能优化方案-step5.md`（52 项 P 系列清单）· `docs/性能优化审计-mimo26flash.md`（208 点）· `.ai/lessons.md`（B 系列缺陷编号）。

## 0. 已定盘的事实（所有任务的前提，复述而非重新论证）

| 事实 | 数值 | 来源 |
|---|---|---|
| NpgsqlParameter.DbType setter | 1.8ns/次（免费） | 探针四 run4.log |
| NpgsqlDbType getter：设 DbType 后 / 未设 | 31ns / 146~330ns，DBNull→Unknown | 探针二/四 |
| COPY 写 null 列传 `Write(null, Unknown)` vs 显式类型 | 261.5ms vs 6.6ms（5000 列） | 探针三 P6c |
| A/B 必须顺序交替（奇轮 base 先/偶轮 opt 先）；同批 ADO 归一不能替代 | 固定顺序伪造 +27%/+23%/+34% 假劣化 | §五-B 复测 |
| 分配是最稳定口径（>时延>同轮比值>跨轮比值） | BulkInsert 分配 5 轮逐位一致 | step6 §五 |
| jit_above_cost=100000，夹具查询 cost 29~300 | 无一触发 JIT，jit 项划除 | 探针五 run5.log |
| 远端共享库（192.168.200.120）时段漂移 | 需顺序交替协议 | step6 §五-B |

## 已闭环清单（不再占任务，防重复劳动）

- **S1/S2/S3/PG-4**：提交 `32f0110` + `955cc39`；分配 −34~−38%、时延 BulkInsert −13.8%/TxBulkInsert −9.9%/BulkDelete −11.0%（4/4 轮一致）。
- **P1-1 From&lt;T&gt; 过滤子句重建**：大部已闭环（PERF-002 静态格式串 + v5.6 S2743 `GetDefaultFilterForms` 缓存）；残余转 T3 实测收尾。
- **P2-9 零参数 List**：已由 PERF-005 修复（`QueryBuilder.cs:1042` `Array.Empty`）。
- **P2-43 SessionBatch DbBatch 超时**：已由 BATCH-001 修复（`SessionBatch.cs:114` 已设 `batch.Timeout`）。
- **jit 连接初始化**：探针五证伪（cost ≤300 vs 阈值 100000），不做。
- **S4 显式 PrepareAsync / GSS 默认化 / synchronous_commit 批量 / multiplexing / 多连接并行 COPY（D-PL-1）/ NpgsqlDataSource / NpgsqlParameter\<T\>.TypedValue / SequentialAccess**：均实测证伪或缓议，理由与触发条件见 step6 §三 与 §七。

## Global Constraints

- AOT 全链路：零反射、无 `MakeGenericType`/`Expression.Compile`；`IsAotCompatible=true` + 0 警告（`TreatWarningsAsErrors=true`），`NoWarn` 豁免需 Justification。
- 每任务先写失败测试再实现（TUnit，`dotnet run --project test/...` 入口，不用 `dotnet test`）。
- emitter 变更必须 `PALORM_UPDATE_SNAPSHOTS=1` 更新快照并人工评审 `git diff`（判据：除预期行外零漂移）。
- 任何 per-(Type,…) 缓存键**必须带 `SqlDialect`**（B59：漏 Dialect 曾让 PG 收到 MySQL SQL）；PessimisticLockTests 是既有对抗夹具。
- 每项性能改动按 §0 协议做顺序交替 A/B；**单项收益 <5% 不单独立项**，只在批量任务里顺手做。
- 提交前三套测试全绿：Core 421 / SourceGen 202 / Integration 208（PG 在线）。
- 不改公共 API 签名；改动波及文档/注释同一次提交同步（准则 8 三方一致）。
- 本地无容器 PG，时间读数一律走顺序交替协议；分配读数不受影响。

## Review Focus（最可能咬人的五类）

1. **PG 标识符折叠**（B78：未加引号的混合大小写在 PG 折叠为小写）——每个涉及 SQL 文本的任务必须带 PG 真库或引号断言用例。
2. **缓存键漏 Dialect**（B59）——T4 聚合缓存键必须带 `SqlDialect` 且含跨方言互不复用的对抗测试（T3 经核实不再新增缓存，其 B59 风险已随 P1-1 闭环消失）。
3. **A/B 读数可信**——T1 验收标准即“已知坏协议能被脚本本身检出”。
4. **nullable 行为变更破坏调用方**——T15 是裁决项，未经用户确认不改行为。
5. **生成物快照漂移**——emitter 任务以 `git diff` 快照零意外漂移为验收行。

---

# 任务清单

## T1【P0·仪器】A/B 顺序交替协议固化（编制后核实：协议已存在于工具链，缺口只在契约认知）

**编制期修正**：本项目已有完整实现——`scripts/perfhub-ab.sh`（由 `scripts/perf.sh compare` 转发）
按“轮内交替起跑顺序”执行（奇数轮 HEAD 先、偶数轮基线先）并把基线 JSON 拷回主仓，报告 ⑤b
按 label `ab/轮/方言/档位` 配对、逐轮中位、四档判定，**且 A/B 表已含分配对比列**。
step6 会话早期手跑批次（label `ab/pgcN-base/opt`）不符合该契约，⑤b 不聚合，只能手工算——
这是认知缺口不是工具缺口。

**Files:**
- Modify: `docs/性能基准规范.md`（A/B 执行契约三条款：入口唯一 + label 契约 + ADO 归一不能替代交替 + 分配优先）

**Interfaces:**
- Produces: 规范条款；后续所有 A/B 批次走 `bash scripts/perf.sh compare <基线worktree> <轮数>`

- [x] **Step 1: 核实既有工具链**（2026-09-26 实施时完成：`scripts/perfhub-ab.sh` 已含顺序交替 + 拷回 + label 契约；`Report.cs:596-597` ⑤b 段已含分配对比）
- [x] **Step 2: 规范补三条契约**（入口唯一 / label 契约决定 ⑤b 配对 / 同批 ADO 归一不能替代顺序交替 + 分配优先判读）
- [x] **Step 3: Commit** `文档(PERF)：A/B 执行契约——入口唯一 + label 契约 + ADO 归一不能替代交替`

## T2【P0·探针】PG 每操作一会话固定开销量级 ✅ 已完成（2026-09-26 探针六）

**背景：** GetByKey/Count/IncludeJoin 对 Dapper 的 1.07~1.13 差距主因疑为“每操作一会话 vs 裸连接”形态差；SQLite 侧探针口径 16~29µs/op，PG 未测。

**Files:**
- Modify: `.ai/scratch-pgprobe/Program.cs`（探针六，本地留档 gitignored）

**Interfaces:**
- Produces: 每操作一会话 CreateAsync vs 裸连接 OpenAsync 的 µs/op 差值（PG 口径）

- [x] **Step 1: 三形态交替 3 轮探针**（构造式 = PerfHub PalORM 臂实际形态；CreateAsync 自含式；裸连接基线；探针项目 AssemblyName=PerfProbe 走 InternalsVisibleTo）
- [x] **Step 2: 记录与归因**：见下
- [x] **Step 3: Commit** `探针(PERF)：PG 每操作一会话开销量级 + 归因`

**结果（3 轮交替中位）**：

| 形态 | 中位 | 分配 |
|---|---|---|
| (a) 构造式 `new DataSession<PostgreSqlProvider>(共享连接)`（PerfHub 臂实际形态） | **0.44µs/op** | 1041 B/op |
| (b) CreateAsync 自含式 + DisposeAsync | 25.14µs/op | — |
| (c) 裸连接 `new NpgsqlConnection + OpenAsync` | 1.25µs/op | — |

**结论（证伪一个假设，闭环一项）**：GetByKey/Count/IncludeJoin 对 Dapper 的 1.07~1.13 差距（22~89µs/op）
**不可能**由“每操作一会话 vs 裸连接”形态差解释——PerfHub PalORM 臂的构造式只付 0.44µs/op，
占差距的 0.5~2%。剩余差距候选收敛到每查询管线本身（CTS+timer 168B、`EnterOperation` 锁、
`GetActiveTransaction`、每查询新建命令/reader），即 T3~T6 的标的。
每操作自建连接的用法（形态 b）付 +23.9µs/op 会话税，登记给用户参考。
按判定门槛（>10µs/op 才立 T2b 旋钮扫描缓存）：**T2b 不立**。

## T3【P1】From&lt;T&gt; 每查询分配实测与收尾 ✅ 已完成（2026-09-26，隔离单测口径）

**背景（编制期实地核实，P1-1 已大部闭环）**：`DataSession.Crud.cs:13-20` 的软删/租户格式串已按封闭泛型静态缓存（PERF-002）；`GetDefaultFilterForms`（`DataSession.cs:590-612`，S2743）已按 `(Type,Dialect,hasSoftDelete,hasTenant)` 缓存三种拼接形态。剩余疑点只有租户会话的 `FormattableStringFactory.Create`（`:66-67`，API 形状固有成本，P2-16 同族）与 `_cacheTenantScope` 拼接（`:75-77`，每 `From<T>()` 一次、会话内恒定）。本任务以实测结案，不预置结论。

**Files:**
- Create: `test/PalORM.Core.Tests/FromAllocationTests.cs`（隔离单测口径分配基线 + 宽松 tripwire）
- Modify: `src/PalORM.Core/DataSession.Crud.cs:66-77`（租户形态超门槛，scope 拼接修复移 T6 Step 3）

- [x] **Step 1: 分配探针测试**（`--treenode-filter "/*/*/FromAllocationTests/*"` 单跑；全量套件禁配——TUnit 并行污染全局分配计数，同 BulkUpdatePoolingTests 既有结论）
- [x] **Step 2: 实测记录**：见下
- [x] **Step 3: 判定**：非租户 <1B/查询（零分配，不修）；租户 **334.92 B/查询**（两轮逐位稳定，超 80B 门槛）→ scope 拼接修复移 T6 Step 3；参数绑定 deferral 待 T6 分解后定
- [x] **Step 4: tripwire 入库**（100B / 1000B，宽于实测）
- [x] **Step 5: Commit** `测试(PERF)：From<T> 分配基线——非租户零分配 / 租户 334.92B`

**结果**：

| 形态 | B/查询 | 判定 |
|---|---|---|
| 非租户 `From<PoolBasic>()` | **<1**（零分配） | 缓存全热后无堆分配，不修 |
| 租户（软删+租户）`From<FilteredEntity>()` | **334.92** | 超门槛；构成待分解（候选：scope 拼接 ~32B / FormattableString 传值 ~140B / 过滤参数 `List+NpgsqlParameter` ~150B / 2×ClauseNode ~64B），T6 落地时同测分解

## T4【P1】Count 组合 SQL 缓存

**背景：** A2——`CountBaseSqlCache` 已缓存基底（PERF-002），未缓存的是 where+defaultFilter 组合（`DataSession.Query.cs:25-28` 每次 2~4 个 concat 中间串）。

**Files:**
- Modify: `src/PalORM.Core/DataSession.Query.cs:19-33`
- Test: `test/PalORM.Core.Tests/`

**Interfaces:**
- Produces: 组合 SQL 拼装从“每次全量 concat”变为“头尾缓存 + 中间仅拼 where 文本”；零 where 走 `defaultFilter` 缓存键 `(Type, Dialect)`

- [ ] **Step 1: 写失败测试**：`[Test] CountSql_Composed_Cached()`——无 where 有默认过滤时，连续两次 `CountAsync` 的 CommandText 引用相等
- [ ] **Step 2: 实现**：拆三段缓存——`CountPrefixCache`（`SELECT COUNT(*) FROM t`）、`CountFilterOnlyCache`（`(Type,Dialect)` → 带 defaultFilter 的整句）；有 where 时拼 `prefix + " WHERE " + filter + " AND (" + where + ")"`（值仍走参数，缓存键不含值）
- [ ] **Step 3: 写聚合族同构测试**：Sum/Avg/Min/Max 共享 `ExecuteAggregateScalarAsync`（`DataSession.Query.cs:51-59`，每次全量插值建 SQL），同形态缓存，键 `(Type, Dialect, function, filterShape)`
- [ ] **Step 4: Core 421 + A/B 分配列**
- [ ] **Step 5: Commit** `性能(Core)：Count/聚合组合 SQL 缓存（P1-2）`

## T5【P1】读路径入口 display class 消除

**背景：** A3/P1-4——只读入口每查询 async lambda + display class；写路径已由 `ExecuteWriteRowsAsync` 直调重载修复（208B/行，`DataSession.cs:789-798` 范式）。

**Files:**
- Modify: `src/PalORM.Core/QueryBuilderExtensions.cs:193-230`（ExecuteCoreAsync 异步局部函数）
- Test: `test/PalORM.Core.Tests/`（分配回归）

**Interfaces:**
- Produces: 读管道复用与写路径同构的直调重载；拦截器/韧性路径保持行为

- [ ] **Step 1: 写失败测试**：`[Test] QueryAll_Allocation_Dropped()`——`From<T>().ToListAsync()` 分配不高于基线 −168B（CTS+timer 之外至少消 display class 56B）
- [ ] **Step 2: 实现**：把 `ExecuteCoreAsync` 局部函数提为私有静态方法 + `QueryContext`  Struct 传递（对齐 `ExecuteWriteRowsAsync` 直调形态）
- [ ] **Step 3: 保留韧性路径**：直调仅用于“无事务且 IsPassThrough”分支，其余不变（对照写路径条件）
- [ ] **Step 4: Core 421 + SQLite/PG 集成冒烟**
- [ ] **Step 5: Commit** `性能(Core)：读路径入口 display class 消除（P1-4，对齐写路径已修范式）`

## T6【P1】BuildLimitClause / 租户 scope 分配收敛

**背景：** A5/P2-10（`QueryBuilder.cs:1150-1235` 每执行 List+数组 150~250B）+ A6/P2-13（`DataSession.Crud.cs:66-77` 每 `From<T>()` 分配 `_cacheTenantScope` 44B）。两项均 <5% 单独收益，合并一个任务。

**Files:**
- Modify: `src/PalORM.Core/QueryBuilder.cs:1150-1235`、`src/PalORM.Core/DataSession.Crud.cs:66-77`
- Test: `test/PalORM.Core.Tests/`

**Interfaces:**
- Produces: Take/Skip 的 limit 子句零参数路径不分配（literal take 已有先例 `:1161`）；租户 scope 串 per-（会话，方言）惰性缓存

- [ ] **Step 1: 写失败测试**：`[Test] LimitClause_NoTakeSkip_ZeroAlloc()` 与 `[Test] TenantScope_LazyOnce()`
- [ ] **Step 2: 实现 limit 侧**：无参数路径归并到既有 literal 分支（参照 `:1152`）
- [ ] **Step 3: 实现租户侧**：`_cacheTenantScope` 改惰性（首次访问才建，`WithTenant` 变更时失效）
- [ ] **Step 4: Core 421 + 租户集成用例**
- [ ] **Step 5: Commit** `性能(Core)：limit 子句与租户 scope 分配收敛（P2-10/P2-13）`

## T7【P2·先探针】每查询锁与 ODE 探测

**背景：** A8/L17——每查询固定 3 次 `_sync` 锁 + 2 次 `GetActiveTransaction`（`SessionOperationState.cs:430-457`）；`IsTransactionAlive` 在 Npgsql 已释放事务上抛 ODE 被 try/catch 吞（`:376-387`）；B3/P1-33——`QueryBuilder.cs:513/516/581` 裸取 `.Connection` 在 PG 得 ODE（SQLite 得 null，跨方言发散）。

**Files:**
- Modify: `src/PalORM.Core/SessionOperationState.cs`、`src/PalORM.Core/QueryBuilder.cs`
- Test: `test/PalORM.Core.Tests/`（PG 方言 ODE 用例，验收要求来自 step5 P1-33）

**Interfaces:**
- Produces: 探针输出锁成本量级；若 <2µs/查询则只做 B3 的 ODE 形态统一，不动锁

- [ ] **Step 1: 写失败测试（B3）**：PG 方言下 `QueryBuilder` 对已释放事务调用 → 期望 `ObjectDisposedException`（或统一后的 PalORM 异常类型），现在实测得裸 ODE 属“未定义形态”
- [ ] **Step 2: 运行确认红**
- [ ] **Step 3: 探针**：量 `GetActiveTransaction` 在暖路径的 ns 级成本（1e6 次循环，复用探针四方法）
- [ ] **Step 4: 按量级分流**：≥2µs/查询 → 提优化子任务（缓存活跃事务引用 per 操作）；<2µs → 仅统一 ODE 形态后结案
- [ ] **Step 5: Commit** `修复(Core)：PG 已释放事务 ODE 形态统一（P1-33）+ 锁成本探针登记`

## T8【P2】缓存并发占位

**背景：** B5/R49+R50——`QuotedInsertTargetCache.TryAdd` 无占位（首次并发双构建）、`InsertBinderValidated` 无锁双检（首次并发多付一次 ProbeBinder 往返）。

**Files:**
- Modify: `src/PalORM.PostgreSql/PostgreSqlProvider.cs:384-408`、`src/PalORM.Core/BulkOperationFramework.cs`
- Test: `test/PalORM.Integration.Tests/`（并发播种夹具）

**Interfaces:**
- Produces: 首次并发构建去重（`Lazy<T>` 或 GetOrAdd 委托工厂天然原子）

- [ ] **Step 1: 写失败测试**：8 线程同时首触 COPY 目标缓存，断言 `QuoteIdentifier` 调用次数 ≤1（计数器探针）
- [ ] **Step 2: 运行确认红**（现在会 >1）
- [ ] **Step 3: 实现**：改 `GetOrAdd(key, static factory)` 形式（值参数透传），`InsertBinderValidated` 同理
- [ ] **Step 4: PG 并发 BulkInsert 集成用例 + 全量三套**
- [ ] **Step 5: Commit** `修复(PG)：COPY 目标缓存与 binder 校验首次并发双构建（R49/R50）`

## T9【P2】手工 FormattableString 重名参数

**背景：** B4/P2-44——用户手工构造的 FormattableString 复用下标产生重名参数，PG 按位置绑定可能绑出预期外组合（[推断]，未标状态）。

**Files:**
- Modify: `src/PalORM.Core/FormattableSqlFormatter.cs`（或 DataSession 参数绑定入口）
- Test: `test/PalORM.Core.Tests/`

**Interfaces:**
- Produces: 重名参数要么确定性报错（推荐）要么按声明序重命名——二选一在 Step 1 的测试里钉死语义

- [ ] **Step 1: 写复现测试**：`From<Posts>().Where($"\"Id\" = @p0 OR \"Text\" = @p0", ...)` 形态（复用下标），断言当前行为（记录基线：现在静默通过）
- [ ] **Step 2: 定语义**：重名输入抛 `InvalidOperationException`（消息含参数名）——在测试中断言
- [ ] **Step 3: 实现**：绑定期扫描参数名集合，重名即抛（成本：一次 HashSet 试探，仅参数数 >1 时）
- [ ] **Step 4: Core 421 + PG 集成 Where 冒烟**
- [ ] **Step 5: Commit** `修复(Core)：FormattableString 重名参数 PG 位置绑定歧义（P2-44）`

## T10【P2】连接串覆盖判据

**背景：** B6/R47——连接串旋钮用“当前值==驱动默认值”判定用户是否显式设置，用户显式设成默认值（`MaxAutoPrepare=0`、`ConnectionLifetime=3600`）的意图被静默改写。

**Files:**
- Modify: `src/PalORM.PostgreSql/PostgreSqlProvider.cs:52-90`
- Test: `test/PalORM.Integration.Tests/`（PG 连接串断言夹具）

**Interfaces:**
- Produces: 语义三选一在测试钉死：(a) `Keys.Contains` 已覆盖（Npgsql 10 对未设置已知键返 true 的坑，`:95-99` 注释记载，先核实是否仍需绕）；(b) 保留行为但 XML doc 明示；(c) 显式退出键

- [ ] **Step 1: 写测试**：`ConnectionString_ExplicitDefault_NotOverwritten()`——连接串显式 `Max Auto Prepare=0`，断言建连后仍是 0
- [ ] **Step 2: 运行确认当前行为**（预期红或记录跳过原因）
- [ ] **Step 3: 实现**：按 Step 1 断言最小修复（若 `Keys.Contains` 在 10.0.3 已可靠则直接简化 10 次枚举为一次解析缓存）
- [ ] **Step 4: 全量三套**
- [ ] **Step 5: Commit** `修复(PG)：连接串旋钮覆盖判据——显式默认值不再静默改写（R47）`

## T11【P3】连接串配方文档化

**背景：** step6 §六已有三配方文案（Unix socket / GSS Disable / synchronous_commit off），未进 README 与 XML doc。

**Files:**
- Modify: `README.md`（PG 调优配方节）、`src/PalORM.PostgreSql/PostgreSqlProvider.cs`（类头 XML doc）

**Interfaces:**
- Produces: 与 SQLite 项5/6/7 同口径（XML doc 载明配方、不新增配置面）

- [ ] **Step 1: README 增节**：三配方 + 各自触发条件与风险（Unix socket 仅本机；GSS 只削长尾属安全取舍；synchronous_commit 风险窗 ≤600ms 须业务确认）
- [ ] **Step 2: XML doc**：`PostgreSqlProvider` 类头补配方指针
- [ ] **Step 3: Commit** `文档(PG)：连接串调优三配方进 README + XML doc`

## T12【P3】char 列物化 GetChars ⛔ PoC 证伪结案（2026-09-26）

**Files:**
- Modify: 无（维持 `ReadChar` 现状）

- [x] **Step 1: PoC**：探针九对 SQLite/PG 实测 `r.GetChars(0, 0, stackalloc char[1], 0, 1)`——编译器报错（CS1503）：`DbDataReader.GetChars(int, long, char[]?, int, int)` 的缓冲参数**只接受 `char[]`，无 Span 重载**（不同于读路径其他 API）。stackalloc 零分配形态不可行。
- [x] **Step 2: 备选评估**：`ArrayPool<char>.Shared.Rent(1)` 每行 rent/return——收益是消 `GetString(o)[0]` 的每行 ~28B gen0 分配，开销是 rent+return（~40ns）+ 缓冲生命周期管理（RowFactory 的 `static readonly Func<DbDataReader,T>` 无 per-query 状态载体，只能每行 rent/return）。收益与开销相抵，且 char 列不是基准夹具覆盖的形态（PerfHub `Post` 无 char 列）。
- [x] **Step 3: 结论**：维持 `GetString(o)[0]` + 空串守卫现状；ITM-520 记录的 GetChar NotSupported 背景不变。若未来 .NET 增 Span 版 GetChars 重估。
- [x] **Step 4: Commit** `文档(PERF)：T12 PoC 证伪——GetChars 无 Span 重载，维持 GetString[0]`

## T13【裁决项】可空引用列读路径行为 ⏸ 挂起等用户裁决（2026-09-26 已呈递）

**Files:**
- Modify: 无（未经确认不改行为——步骤 2 是硬门）

- [x] **Step 1: 呈现选项**（已在对话中呈递，此处留档）：
  - (a) 维持现状 + XML doc 警告：DB NULL 撞非可空注解属性时抛裸 `SqlNullValueException`（当前行为）
  - (b) 可空性未知时抛带列名的 PalORM 异常：诊断性更好，仍是行为变更（异常类型变）
  - (c) 读路径加 `IsDBNull` 守卫：每列每行一次 IsDBNull 探测成本，但可返回 null（需要属性可空）
- [ ] **Step 2: 等裁决再排实现**——裁决记录到本行后开工

## T14【ADR 项】OwnedJson 方言条件 Span emit ✅ ADR 已产（实现保持挂起）

**Files:**
- Create: `docs/adr/ADR-G-ownedjson-方言条件span化.md`（2026-09-26）

- [x] **Step 1: ADR**：G1 维持 / G2 SourceGen 方言感知化（推荐，但需先补 OwnedJson PerfHub 夹具）/ G3 PG-only 运行时旁路 / G4 驱动能力探测 四选项 + 收益成本分析
- [ ] **Step 2: 若 ADR 通过再排实现**（G2 前置：OwnedJson 夹具子任务 + 三方言矩阵实测）

## T15【缓议登记】触发条件表（不排期）

| 项 | 触发条件（满足其一即重评） |
|---|---|
| 多连接并行 COPY（D1/D-PL-1） | A/A 显示比值稳定 >1.5×，或 BulkInsert 分配劣化被第二台机器复现 |
| synchronous_commit off 配置面（D4） | 用户明确要 opt-in 且接受崩溃丢 ~600ms 窗口 |
| PL-3 COPY 打字写入（D5） | 同 D1 |
| NpgsqlDataSource 迁移（D2） | multiplexing 被官方或社区实证转正 |
| T2b 连接串旋钮扫描缓存 | T2 探针显示建连开销 >10µs/op |

---

## Self-Review 记录

1. **Spec 覆盖**：step6 §七遗留 4 项 → T2（项2/3）、T11（项1 剩余部分即 T12/T14）、T15（项4 观测）；step5 P1-2/P1-4/P2-10/P2-13/P2-14/P1-33/P1-45/P1-46/P2-44/R47/R49/R50 → T4~T10、T12~T14；P2-9/P2-43/P1-1 大部经核实已闭环（残余转 T3）。无遗漏无占位。
2. **占位符扫描**：无 TBD；每任务 Files/Steps/测试断言具体；T13/T14 为显式裁决/ADR 门（非占位，是流程门）。
3. **类型/命名一致**：`GetDefaultFilterCondition`/`GetDefaultFilterWhereClause`（T4）与 `DataSession.Query.cs:618-624` 既有方法同名；`CountPrefixCache`/`CountFilterOnlyCache`（T4）为新名仅本任务使用；`ab-alt.sh`（T1）与 `.ai/scripts/` 既有脚本目录一致。
4. **Review Focus 对应**：PG 引号 → T4/T9 测试均带引号 SQL；缓存键 Dialect → T4 Step 3 聚合缓存键带 `SqlDialect` + 对抗测试；A/B 可信 → T1；nullable 行为 → T13 裁决门；快照漂移 → T12 Step 4 + Global Constraints。
5. **编制期实地核实修正（2026-09-26）**：T3 原按 step5 文档写“FilterClause 每查询重建”，实地核实 `DataSession.cs:590-612`（S2743）已缓存三种拼接形态、`DataSession.Crud.cs:13-20`（PERF-002）已静态化格式串——P1-1 为过期项，T3 改写为实测收尾（Step 1 建分配基线，Step 3 设判定门槛）。**教训：step5 的 P 项状态列从未逐项维护，任务化前必须逐项读码核实。**
6. **实施期二次修正（T1）**：T1 原计划“从零造顺序交替脚本”，实施时核实 `scripts/perfhub-ab.sh`（阶段 4.1）与报告 ⑤b（阶段 4.2）早已实现顺序交替、基线拷回、逐轮中位四档判定且含分配列——本会话 step6 早期的手跑批次是认知缺口（没用既有入口 + label 不合 `ab/轮/方言/档位` 契约）而非工具缺口，T1 降级为规范契约条款。**教训：动手造工具前先 `ls scripts/` + 读规范原文；AGENTS.md「代码库已有此能力？”是懒惰阶梯第一问。**
