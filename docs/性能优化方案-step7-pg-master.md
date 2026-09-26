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

## T1【P0·仪器】A/B 顺序交替协议固化

**Files:**
- Create: `.ai/scripts/ab-alt.sh`（本地工具，gitignored）
- Modify: `docs/性能基准规范.md`（新增“顺序交替”条款）

**Interfaces:**
- Produces: `bash .ai/scripts/ab-alt.sh <轮数> <标签前缀>`；内部按奇偶轮交换 base/opt 执行顺序，退出时打印两臂分配对照。

- [ ] **Step 1: 抄录已验证脚本为模板**（`.ai/scratch-pgprobe/ab-creverse.sh` 的奇偶轮逻辑，base=worktree、opt=主树、`--no-build`）
- [ ] **Step 2: 脚本尾部追加分配对照输出**（从同批 JSON 提取 `AllocatedBytesPerOp` 中位数对比，漂移轮次标 `⚠污染`）
- [ ] **Step 3: 文档条款**：`docs/性能基准规范.md` 增补——固定顺序批次不得作为回归判据；同批 ADO 归一仅作辅助
- [ ] **Step 4: 自检**：用最近两批（固定顺序 vs 交替）跑一次脚本，确认能标出旧批次的污染轮
- [ ] **Step 5: Commit** `工具链(PERF)：A/B 顺序交替协议固化——分配对照 + 污染轮标注`

## T2【P0·探针】PG 每操作一会话固定开销量级

**背景：** GetByKey/Count/IncludeJoin 对 Dapper 的 1.07~1.13 差距主因疑为“每操作一会话 vs 裸连接”形态差；SQLite 侧探针口径 16~29µs/op，PG 未测。

**Files:**
- Modify: `.ai/scratch-pgprobe/Program.cs`（新增探针六）
- Test: `test/PalORM.Integration.Tests/`（若结论触发优化，补会话预热用例）

**Interfaces:**
- Consumes: `DataSession.CreateAsync`（`src/PalORM.Core/DataSession.cs:100-176`）
- Produces: 每操作一会话 CreateAsync vs 裸连接 OpenAsync 的 µs/op 差值（PG 口径）

- [ ] **Step 1: 写探针**：PG 连接串暖池后，各 2000 次（a）`DataSession.CreateAsync` + DisposeAsync（b）裸 `new NpgsqlConnection` + OpenAsync；交替 3 轮取中位
- [ ] **Step 2: 分解开销**：把 (a)−(b) 的差额按 DataSession 构造清单（NpgsqlConnectionStringBuilder 10 次 `Keys.Contains` + linked CTS + ResilienceExecutor + 3 集合）逐项注释归因
- [ ] **Step 3: 判定门槛**：差值 >10µs/op → 立 T2b（连接串旋钮扫描缓存：per-connectionString 判定结果缓存）；≤10µs/op → 登记结案
- [ ] **Step 4: Commit** `探针(PERF)：PG 每操作一会话开销量级 + 归因`

## T3【P1】From&lt;T&gt; 每查询分配实测与收尾

**背景（本总纲编制时实地核实，P1-1 已大部闭环）**：`DataSession.Crud.cs:13-20` 的软删/租户格式串已按封闭泛型静态缓存（PERF-002）；`GetDefaultFilterForms`（`DataSession.cs:590-612`，S2743）已按 `(Type,Dialect,hasSoftDelete,hasTenant)` 缓存三种拼接形态。剩余疑点只有租户会话的 `FormattableStringFactory.Create`（`DataSession.Crud.cs:66-67`，API 形状固有成本，P2-16 同族）与 `_cacheTenantScope` 拼接（`:75-77`，每 `From<T>()` 一次、会话内恒定）。本任务以实测结案，不预置结论。

**Files:**
- Modify: `src/PalORM.Core/DataSession.Crud.cs:66-77`（仅当实测显示租户形态 >50B/查询时修 scope 拼接）
- Test: `test/PalORM.Core.Tests/`

**Interfaces:**
- Produces: 实测报告（非租户/租户两形态每查询分配 B 数）；修或不修的结论落入本文档 §结果

- [ ] **Step 1: 写分配探针测试**：`[Test] From_Allocation_Measured()`——非租户与租户两种会话，各 1000 次 `From<Posts>()`，`GC.GetAllocatedBytes` 差 ÷ 1000 得 B/次
- [ ] **Step 2: 运行并记录两形态读数**（当前无基线，本步骤即建立基线）
- [ ] **Step 3: 判定**：非租户 >30B 或租户 >80B → 修（scope 拼接改惰性字段，见 T6 Step 3）；否则登记结案
- [ ] **Step 4: 若修**：补 `[Test] TenantScope_NotRebuilt()` 引用相等断言；若否：在文档记“实测 X B/查询，低于门槛，结案”
- [ ] **Step 5: Core 421 + Commit** `性能(Core)：From<T> 每查询分配实测收尾（P1-1 残余）`

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

## T12【P3】char 列物化 GetChars 化

**背景：** C1/P1-45——char 列读路径每行一次完整 string 分配（`ReadChar` 走 `GetString(o)[0]`，`RowFactoryEmitter.cs:180`）。

**Files:**
- Modify: `src/PalORM.SourceGen/RowFactoryEmitter.cs:180`、辅助方法 `:63-70`
- Test: `test/PalORM.SourceGen.Tests/` 快照 + `test/PalORM.Integration.Tests/` 三方言 round trip

**Interfaces:**
- Produces: `stackalloc char[1]` + `GetChars` 形态；ITM-520（两驱动 GetChar 抛 NotSupported）的替代方案必须三方言矩阵实测

- [ ] **Step 1: 写失败测试**：char 列读分配归零断言（Integration，AllTypesEntity 的 VChar 列）
- [ ] **Step 2: 实现 emit**：`ReadChar` 辅助改 `reader.GetChars(ordinal, 0, buf, 0, 1)`
- [ ] **Step 3: 三方言矩阵**：SQLite/MySQL/PG 真库 round trip（ITM-520 记录 MySQL 抛 NotSupportedException 的驱动面）
- [ ] **Step 4: 快照更新 + 人工评审 diff**
- [ ] **Step 5: Commit** `性能(SourceGen)：char 列 GetChars 零分配读（P1-45，三方言矩阵实测）`

## T13【裁决项】可空引用列读路径行为

**背景：** C2/P1-46——DB NULL + 非可空注解属性 → 裸 `SqlNullValueException`。改为三态响亮失败属**行为变更**，需用户裁决。

- [ ] **Step 1: 呈现选项**：(a) 维持现状 + XML doc 警告；(b) 可空性未知时抛带列名的 PalORM 异常；(c) 读路径加 `IsDBNull` 守卫（每列每行一次 IsDBNull 成本）
- [ ] **Step 2: 等裁决再排实现**（未经确认不动）

## T14【ADR 项】OwnedJson 方言条件 Span emit

**背景：** C3——v5.7.0 三方言统一 emit 的 Span 化被 MySQL TEXT 列 `InvalidCastException` 证伪回滚（0d15da5）；PG 的 jsonb 理论可行，但 RowFactory 是方言无关生成物。

- [ ] **Step 1: 先写 ADR**（`docs/adr/`）：方言条件 emit 的 SourceGen 改造面 + PG-only 收益评估（无基准夹具覆盖 OwnedJson 列，收益不可测是主要障碍）
- [ ] **Step 2: 若 ADR 通过再排实现**（含新增 OwnedJson 夹具进 PerfHub 的前置任务）

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
