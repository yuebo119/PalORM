# PalORM 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 规范。

## [未发布] — SQLite 极致优化批次：PRAGMA 三补 + 参数上限 32766 + 读路径命令复用 + 批量回退合并 + OwnedJson Span 解析

> 变更范围：`SqliteProvider` / `SqlLimits` / `DataSession.Crud` / `SessionBatch` / `RowFactoryEmitter` + 测试与文档。执行账本与探针证据见 `docs/性能优化方案-step5.md` §九。

- **PRAGMA 调优**：新增 `busy_timeout=5000`（并发 BUSY 引擎内等待，消上层 CTS+退避重试）、`journal_size_limit=67108864`（防 WAL 无界膨胀）、`analysis_limit=400`（约束 optimize/ANALYZE 采样成本）；进阶调优（page_size/mmap_size/secure_delete）经既有 SessionSetupSql 通道，XML doc 载明配方。
- **参数上限 999→32766**：引擎编译选项 `MAX_VARIABLE_NUMBER=32766` 探针实测（SQLite3MC 3.53.4）；同数据量批量语句往返最多降 33 倍。多批测试用例行数同步提高保持跨批验证面。
- **GetByKey 命令复用（PL-2 扩展）**：单行读惰性晋升（阈值 3），复用分支清参重绑走同一生成键绑定器，键类型转换语义与新建路径逐位一致。
- **SessionBatch SQLite 回退**：全无参语句合并单次多语句往返（`RecordsAffected` 跨语句累计，返回契约保持）；顺序路径循环外单命令复用，同文本语句经驱动语句缓存免重编译。
- **OwnedJson 读路径 Span 化**：`GetFieldValue<byte[]>` + `Deserialize(ReadOnlySpan<byte>)` 替代 GetString 重载——消每行整段 UTF-16 JSON string 分配与双重转码。
- **证伪划除**（探针实测，不保留理论收益）：L44 可空列单读（驱动 `GetFieldValue<T?>` 读 NULL 抛异常，双读必需）；L35 批量 Prepare（驱动 CommandText 同值 setter 短路，语句缓存已生效）。
- **缓议**：P2-29 ToPageAsync 单往返（SQLite 上已平价无差距可收，跨方言 SELECT 形态风险，按观测优先门槛待专用夹具）；大 BLOB 流式与 sqlite-vec 归设计评审/多阶段路线。
- **验证**：Core.Tests 421/421、SourceGen 202/202（快照更新 1 行目检确认）、Integration SQLite 侧零失败（30 失败均为外部库未启动的连接超时）、SQLite AOT publish + 实跑通过。

## [未发布·工具链] — PerfHub `latest.json` 补两条守卫：子集批次与零真测量批次

> 变更范围：`bench/PalORM.PerfHub/Program.cs`（写 latest 的判据）+ 还原被污染的指针 + 三份失败批次留档。

规范 §6 写的是"带子集标记的批次不顶 `latest-<夹具>.json`"，但两侧实现只有一侧在执行：
信封侧（`PerfResultWriter`）按 `IsSubsetLabel(label) && hasData` 判，原始侧（PerfHub 写
`bench/perfhub/results/latest.json`）**只排 `quick`**——于是 `ab/…` 批次会把"当前数字"
换成单方言单档的读数，与规范文字直接冲突（三方一致缺口）。

第二个缺口是**零真测量**：2026-09-25 PG+MySQL 连接握手双双超时，一次跑只剩 `DataGen`
两项却仍写了 `latest.json`——读到它的人以为"当前数字 = 0 项"。这正是 B86「空批次顶
latest」当时**只修了信封侧**的那半个。

现在两侧同一真源：`!IsSubsetLabel(label) && results.Exists(m => m.Implementation != "DataGen")`。
不满足只打一行说明；历史文件照旧落盘（跑过什么包括失败都是事实，只是不该被读成当前状态）。

**实测验收**：带子集 label 的失败重跑 `exit=1`、`latest.json` md5 前后一致、打印
`跳过 latest（零真测量 label=…）`；被污染的指针已还原到最后一个可引用批次（362 项、
2026-09-24 23:12:14），与 `bench/results/latest-perfhub.json` 一致。

## [5.6.0] — 性能大轮（性能轮一~九 · PL-1~4）+ 统一性能测试系统：写路径 −53%、UPSERT 分配 −28%、PG COPY 比值归位

> 变更规模：v5.5.1 后 204 个提交 · 19 轮分节明细见下（各轮子节保留原标题内容）
> 背景：五轴性能优化系列（延迟/内存/并发/可靠性/事务）落地产品侧，配套 PerfHub 统一性能
> 测试系统（三方言 × 三臂 × 21 项 + 并发）、五段汇报标准与 PerfGate 基线门禁。
> 工具链轮次只动 bench/docs/scripts，不进产品包。

### 💔 破坏性变更

- 无（SemVer Minor 判据：全系列向后兼容）。两处行为微变，值恒等、语义等价：
  - **`InsertAsync` 不读返回形态（PL-3）**：显式主键且全部列可插入、无转换器/OwnedJson 的实体
    走纯 INSERT，返回**传入实体同一引用**（原为物化新实例）；保守判定不满足任一条件即保持原读返回路径
  - **`CommandSqlSet.Insert` 字段恢复发射（PL-3）**：由恒空串变为纯 INSERT SQL（外部读者
    语义增强，旧生成器模型程序集仍为空串）

### ✨ 产品性能亮点（实测数字，明细见各子节）

- **PL-3 InsertNoReturning**：探针写路径单条 **5.79 → 2.72 µs（−53%）**；PerfHub 实测
  `TxHundredInserts` P/ADO **2.76 → 1.30**、`TxSingleInsert` 1.38 → 1.06、`Insert` 20000 档 1.28 → 0.98
- **PL-3.2 BindUpsertValues**：BulkMerge 分配 **982 → 789 B/行（−20%）**；`UpsertBatch` 分配
  相对 ADO **+32%/+41% → −4%/0%**
- **PL-2 命令/参数复用**：单行写路径池化（先测再改，惰性晋升防并发退化）
- **性能轮二~七**：批量 INSERT 范式收敛、逐条 UPDATE 参数池化（−41~62%）、写路径直调重载
  （−3.4~5.5%）、SQLite First/Single 族 LIMIT 内联、读路由会话级复用等（各子节含 A/B 数据）

### 🛠 工具链（bench/docs，不入产品包）

- PerfHub 统一性能测试系统（21 项 + 并发，项数精简 112→91、全量 42min→约 33min）、
  五段汇报标准（看点/四组表/🚨读法/口径/尾注）、PerfGate 索引基线门禁（152 项）、
  PL-1~4 诊断与夹具修正（PG COPY 批宽比值 3.25 → 1.08 等）、依赖升级轮
  （TUnit 1.69.0 / Dapper 2.1.89 / Dapper.AOT 1.1.0，产品包依赖面零变化）

### ✅ 验证（2026-09-24 实跑）

- 构建 `PalORM.ci.slnf` Release `-warnaserror` **0 警告 0 错误**；测试 **Core 415 / SourceGen 202 /
  Integration 205**；AOT 三 Provider publish + SQLite 运行 **PASSED**；包契约 PASS；五包 pack 与
  nuspec 元数据全过；全量性能跑测五步完成，BDN 门禁 **27/27**

### 分轮明细（19 轮，原标题保留）

### [5.6.0 ·依赖] — 2026-09-24 依赖升级轮：TUnit 1.69.0 / Dapper 2.1.89 / Dapper.AOT 1.1.0

> 变更范围：`Directory.Packages.props`（4 个 PackageVersion）。产品包（Core/SourceGen/三 Provider）
> 依赖面零变化——包契约脚本 PASS，升级项全部是测试与基准侧依赖。

| 包 | 旧 → 新 | 类型 | 影响面 |
|---|---|---|---|
| `TUnit` / `TUnit.Assertions` | 1.66.27 → **1.69.0** | minor | 三个测试套（升后实跑 **415 / 202 / 205** 全绿） |
| `Dapper` | 2.1.79 → **2.1.89** | patch | Benchmarks / DapperSuite / PerfHub 三臂对照臂 |
| `Dapper.AOT` | 1.0.52 → **1.1.0** | minor | Benchmarks / PerfHub |

**口径注记**：Dapper 是三臂对照的外部锚点，版本变化会让 PerfHub/DapperSuite 的 Dapper 臂数字
与历史批次漂移——下次全量跑测起 `P/Dapper` 列跨批不直接可比（批内三臂同批仍可比）。

**验证**：`PalORM.ci.slnf` Release `-warnaserror` 构建 0 警告 0 错误 · 三测试套 415/202/205 ·
bench 三项目 0 错误 · 包契约 PASS · `dotnet list package --outdated` 归零
（Npgsql 10.0.3 / MySqlConnector 2.6.2 / Sqlite.Core rc.1 / CodeAnalysis 5.9.0 /
SonarAnalyzer 10.34 / SourceLink 10.0.401 / RepoDb 1.16 等经查已为源内最新）。

### [5.6.0 ·性能轮九] — 单行插入省读返回与批量 UPSERT 参数池直写（PL-3 / PL-3.2）

> 变更范围：`src/PalORM.Core/DataSession.Crud.cs`（InsertNoReturning 分派 + InsertPlainAsync）、
> `src/PalORM.Core/CrudMetadata.cs`（CrudBindings 两字段）、`src/PalORM.Core/SqlSets.cs`（Insert 字段恢复发射）、
> `src/PalORM.SourceGen/CommandFactoryEmitter.cs`（SupportsInsertWithoutReturning 判定 + BindUpsertValues 生成）、
> `src/PalORM.SourceGen/RegistryEmitter.cs`（发射）、`src/PalORM.Core/DataSession_Bulk.cs`（BulkMerge 池直写）、
> `test/PalORM.Core.Tests/InsertNoReturningTests.cs`（新增 6 例）＋ 快照基线刷新。

### 为什么是这两项

拆账（`.ai/perf-probe/TxPathDiag.cs`，本地探针）显示：`InsertAsync` 比同为命令复用路径的
`UpdateAsync` 慢 3.1 µs/条，大头是 RETURNING 整行读+物化+回填——而显式主键（`AutoIncrement=false`）
且全部列可插入的实体，插入值即行值，读回是纯浪费。`BulkMergeAsync` 的批 UPSERT 经 scratch 命令
逐行 `BindUpsert` 建参数再值拷贝，每行白建 N 个 `DbParameter`（探针 c1 982 B/行 vs 地板 344 B/行）。

### ✨ 新增

- **PL-3 `InsertNoReturning`**：生成器保守判定（恰一个非自增主键 + 全部列 `IsInsertable` 且无
  转换器/OwnedJson → 走纯 INSERT 不读返回；任一不满足保持读返回契约）
  - 探针实测：w1 全路径 **5.79 → 2.72 µs/条（−53%）**，分配 1068 → 636 B/条
  - PerfHub 实测（SQLite 同批前后对照）：`TxHundredInserts` P/ADO **2.76 → 1.30**、
    `TxSingleInsert` **1.38 → 1.06**、`Insert` 20000 档 **1.28 → 0.98**
  - 锁定测试：`InsertNoReturningTests` 6 例（判定值正反例 / 生成 SQL 形态 / 返回同一引用 /
    事务回滚 / `[IgnoreOnInsert]` 判否回退读回 DB 真源）
- **PL-3.2 `BindUpsertValues`**：与 `BindUpsert` 同谓词同列序的 Value 直写生成器，
  `BatchUpsertAsync` 直写预建池；旧模型程序集回退 scratch 路径（`BindUpsertRowViaScratch`）
  - 探针实测：BulkMerge **982 → 789 B/行（−20%）**
  - PerfHub 实测：`UpsertBatch` 分配 P 相对 ADO **+32%/+41% → −4%/0%**

### ✅ 验证

- Core 415/415 · SourceGen 202/202 · Integration 205/205（2026-09-24 实跑）
- 生成代码快照按 `PALORM_UPDATE_SNAPSHOTS` 流程刷新并逐行评审
- `CommandSqlSet.Insert` 字段恢复发射（`Insert` 全方言无消费的旧断言已随语义更新）

### [5.6.0 ·工具链·七] — PerfHub 臂 PG Binary COPY 批宽修正（比值 3.25 → 1.08）

> 变更范围：`bench/PalORM.PerfHub/Implementations.cs`（`BulkBatchRows`：PG 传整段行数）、
> `src/PalORM.PostgreSql/PostgreSqlProvider.cs`（`batchSize` 参数文档化）＋
> `src/PalORM.Core/PalORM.Core.csproj`（InternalsVisibleTo 加 PerfProbe 拆账探针）。

### 🐛 修复

| 问题 | 影响 | 修复方式 |
|---|---|---|
| PerfHub PalORM 臂 PG `BulkInsert` 传 `batchSize=1000`（多值 VALUES 变量上限思维），对 Binary COPY（无参数上限）20000 行 = 20 次 `BeginBinaryImport/Complete` 协议往返 | A/A 四批读数稳定在 3.10–3.79×，档位指纹吻合（2000 档 2 次 vs 1 次 = 1.6×；20000 档 20 次 vs 1 次 = 3.3×），违反三臂契约"行业最优调用" | 臂对 PG 传 `rows.Count`（整段单批）；反向验证：单因子改动后 **3.25 → 1.08**（20000 档 111.5 → 40.9 ms，−63%），`TxBulkInsert` 1.53–1.71 → 0.94–1.06 |

产品侧同步：`PostgreSqlProvider.BulkInsertAsync` 的 `batchSize` 文档注明 Binary COPY 无参数上限、
远程库建议传整段行数——真实用户传 1000 同样付 20 次往返。

### [5.6.0 ·诊断] — PG/MySQL BulkUpdate 1.3-1.9× 差源定位：不在产品侧（PL-1.1）

> 变更范围：`.ai/perf-probe/PgBatchUpdateDiag.cs`（本地探针，不入库）＋
> `docs/性能基准规范.md` §6 结论登记。**未改任何产品代码。**

PerfHub 全量跑里 PG `BulkUpdate` 2000 档 1.81×、MySQL 20000 档 1.91×，长期登记为"差源未隔离"。
三项此前已排除（参数池化两边都只写 Value；乐观锁/软删/租户 S1Row 三者皆无；
往返——维度 8 计数装饰器仅 SQLite 生效，**PG 的往返数此前是未知而非相等**）。
本轮探针把剩下两个候选各打一枪：

| 假设 | 探针做法 | 结果 |
|---|---|---|
| SQL 文本不同 | 逐字复刻产品 `BatchUpdateSqlBuilder` 与夹具 `BulkSql` 两个生成器，1~5000 行逐字节比对 | **8 个宽度全部逐字节相同**（1 行 197 字符 … 5000 行 224060 字符）。不是差源 |
| 批宽不利 | 固定产品自己的 SQL 生成器，扫 500/1000/2000/5000 四档（5000 行总量，5 轮 round-robin） | **5000 宽每行 0.85×、2000 宽 0.89×，都比 1000 宽快**——产品用 5000/批、地板用 1000/批，这个不对称**方向相反**，反而利于产品 |
| 产品路径本身慢 | 同数据、同连接、同 SQL（已证逐字节相同）下，产品 `BulkUpdateAsync` vs 手写等价交替 5 轮 | 2000 行 **0.96×**、5000 行 **0.80×**——产品持平或更快，**不存在产品侧架构性落后** |

**结论**：PerfHub 里那 1.3-1.9× 来自**夹具侧读数差异**，不是产品缺陷。
候选（未逐一排除）：`CountingConnection` 装饰器在两臂上的摊销方式不同、表状态、
以及本机已实测的 ±2~5× 环境噪声。
**下一步最小实验**：对**同一提交**跑两轮看比值稳定性（`scripts/perfhub-ab.sh` 同参 A/A）——
比值自身抖动若与 1.3-1.9× 同量级，这一项就该按噪声带处理，不必再追。

### [5.6.0 ·工具链·六] — `Build*` 比值不计比（PL-4）：地板不构造 SQL，对比无判别力

> 变更范围：`bench/PalORM.PerfHub/Program.cs`（`NonComparableOperations` + 信封映射）
> ＋ `docs/性能基准规范.md` §1.1。

PerfHub 的 `BuildGetByKeySql`/`BuildComplexQuerySql` 长期以 6.5~7.1× 挂在落后项榜首，
看着像产品在大面积落后。实际是地板这两项直接 `return` 插值字面量，不做任何 SQL 构造——
拿它当分母量的是"生成一条 SQL 文本"这件事本身，而那正是产品相对裸 ADO.NET 的价值。
三方言实测比值 PG 7.10×/6.59×、MySQL 7.10×/3.22×、SQLite 6.51×/3.66×，
分配比还到 5.42×，全是同一个无判别力的机制。

现在 `Program.NonComparableOperations` 把这两项的 `Ratio` 记 0 并在 `Note` 注明原因，
绝对值与分配仍照登（构造 SQL 的真实成本仍然可见，只是不当地板分母）。
记 0 即被 `PerfGate` 既有的 `Ratio > 0` 过滤排除出索引基线、关键项速览与最差比值表——
不需要在各消费点各写一遍排除名单。报告里的落后项数从 7 降为 5。

**没删项**：SQL 构建成本本身值得登记；删了就等于假装它不存在。
想真比这项只有一条路——把地板也接到生成器上，而那会让差异归零，测不出东西。

### [5.6.0 ·性能轮八] — 单行 CRUD 命令与参数跨调用复用（PL-2）

> 变更范围：`src/PalORM.Core/DataSession.Crud.cs`（Insert/Update 复用路径 + 惰性晋升）、
> `src/PalORM.Core/DataSession.cs`（晋升命令随会话释放）、
> `test/PalORM.Core.Tests/SingleRowCommandReuseTests.cs`（新增 13 例回归）。

### 为什么是这一项
PerfHub 三臂全量对比里 PalORM 最大的两个落后项都在单行写路径：
`TxHundredInserts` 4.40×、`TxRollback` 3.83×（往返数与地板持平，落后全在客户端每行固定开销）。
同产品内部对照给出了归因：池化路径 `BulkUpdateAsync` **1.41 µs/行**，
单行 API `UpdateAsync` **9.9 µs/行**——同一个 SQLite、同一张表，只因为"复用与否"差 7 倍。
根因是 `InsertCoreAsync`/`UpdateCoreAsync` 每次调用 `CreateCommand()` 新建命令，
`BindInsert`/`BindUpdate` 每列 `CreateParameter()` + `ParameterName` + `Add`。

### 收益在哪一半：先测再改，两次改才改对

三方交替 A/B（SQLite t2000）先把"收益来自命令还是参数"这个问题问清楚：

| 方案 | TxHundredInserts | 分配 | 并发混合 t1 |
|---|---|---|---|
| 改前（每次新建命令+参数） | 969 µs | 254628 B | 8335 µs |
| 只缓存参数、命令仍新建 | 960 µs（**无收益**） | 223055 B（只回收 12%） | — |
| 命令 + 参数都缓存（无条件） | 549 µs | 154666 B（回收 39%） | **40380 µs（慢 4.84×）** |

即**命令的新建与释放才是大头**，只池化参数几乎白做；但无条件缓存命令会引入一个真实的退化
（见下）。终版取两者之长：**惰性晋升**。

### 改法

同一 (实体类型, 操作) **前 2 次**走原新建路径（命令随操作释放、不泄漏），**第 3 次**建池并晋升，
之后复用命令 + 参数池只写 Value——用 v4.6 就已发射、批量路径一直在用的
`BindInsertValues`/`BindUpdateValues`（offset=0）。语句文本、超时与参数绑定的职责从
`InsertWithReturningAsync`/`InsertWithLastInsertIdAsync` 上移到
`TryAcquireInsertCommand`/`TryAcquireUpdateCommand`，子方法只负责执行与回填
（否则复用命令的参数集合会逐次增长——这一条由新增测试抓出来过）。

**为什么必须惰性晋升**：缓存命令意味着"命令只随会话释放"，而 `DataSession` 构造时接管所接收
连接的所有权——"一条外部连接配多个一次性会话"的用法（PerfHub 的每操作一会话正是如此）
无法调用 `DisposeAsync`，那会把共享连接一起关掉。无条件缓存时该用法每次操作泄漏一个未释放的
命令（原生语句句柄 + 终结器），实测并发混合负载慢 **4.84×**（40380 vs 8335 µs，三轮交替每臂 6 跑）。
阈值 3 让"每操作一个抛弃式会话"永远不进缓存，而真正复用会话的用法从第 3 次起拿到全部收益。

晋升生效由分配探针逐次证实（同一会话连续写，B/op）：

| 路径 | 第 1-2 次 | 第 3 次（建池） | 第 4-8 次 |
|---|---|---|---|
| `InsertAsync` | 1464 | 1544 | **872**（−40%） |
| `UpdateAsync` | 1752 | 1840 | **1104**（−37%） |

三条边界：旧模型程序集未发射 `BindInsertValues`/`BindUpdateValues` 时恒走新建路径；
`Update` 排除租户实体（租户参数每次调用新加 + `_tenantId` 可经 `WithTenant` 中途变更）；
晋升命令归会话所有，在 `DisposeCoreAsync` 里先于主连接关闭释放。

### A/B 结果（SQLite 2000 档，三轮交替配对）

第一轮的**绝对耗时作废**：那一轮的地板臂自身慢了 2~5.4 倍（`TxHundredInserts` 地板
215.8 → 1165.5 µs、`Count` 6.3 → 11.1 µs，后者是纯 SQLite 微操作、不碰被测产品代码）。
改用**同轮交替配对**（B→A→B→A 三组，`label=ab/pl2-{a,b}/<轮>/sqlite/2000`）：
同轮内取 PalORM ÷ 地板，分母与被测量同环境，能把漂移约掉。

终版（惰性晋升）三轮交替的实测（中位）：

| 项 | 改前 | 改后 | 判定 |
|---|---|---|---|
| `TxHundredInserts` 耗时 | 969.3 µs | 573.5 µs | **−41%** |
| `TxRollback` 耗时 | 1978.7 µs | 975.8 µs | **−51%** |
| `TxHundredInserts` 分配 | 254620 B/op | 156681 B/op | **−38%** |
| `TxRollback` 分配 | 1069618 B/op | 598716 B/op | **−44%** |
| 并发混合 t1 | 8491.4 µs | 8174.9 µs | 0.96×（无退化） |
| 并发混合 t4 / t8 | 34567 / 74725 µs | 35180 / 76908 µs | 1.02× / 1.03×（噪声内） |
| `Insert` / `Update` / `TxSingleInsert` | 24.4 / 8.6 / 46.8 µs | 24.8 / 8.4 / 44.7 µs | 持平，不声称收益 |

分配是 `GC.GetTotalAllocatedBytes` 精确计数。**并发项是本条最关键的一行**：
无条件缓存命令的版本在这里是 40380 µs（慢 4.84×），惰性晋升把它拉回 8175 µs——
与改前的 8491 µs 同量级，t1 的 P/F 1.18× 甚至优于改前的 1.24×。

**对照原定 [推断] 目标**（按 P/F）：`TxRollback` ≤2.0× **达标**（1.86×）；
`TxHundredInserts` ≤1.5× **未达标**（2.60×）。P/F 依赖同轮地板的绝对耗时，
而地板自身在本机的波动就有数倍，故判定以**绝对耗时与分配**为准（两者三轮一致、方向无例外）。
剩余差距是语义差——产品要 RETURNING 取自增 ID 并回填（`ExecuteScalar` + backfill +
会话租约 + 元数据查找），地板的 100 条插入是裸 `ExecuteNonQueryAsync`，不回 ID。

读这批数字须知：`ab/pl2-*` 批次是用 `git checkout <提交> -- <文件>` 就地切出另一臂代码跑的，
故信封里的 `Commit` 字段读到的是当前 HEAD 提交号——区分两臂只能看 `label`。

### [5.6.0 ·工具链·五] — 性能测试系统重构：项数 112→91、全量 42min→约 33min、进度与结果表格化

> 变更范围：`bench/PalORM.PerfHub/`（Program / Dataset）+ `bench/PalORM.Benchmarks/`
> （02 / 03 / 06 / MySql / Pg / Program，删 BoxingMicroBenchmark）+ `tools/PalORM.PerfGate/`
> （ReportGenerator / IndexGenerator）+ `scripts/perf.sh` + 四份文档。

### 先摆事实：时间不在"项数"上

PerfHub 逐项耗时（SQLite，两档，三臂合计 189 秒）：

| 项 | 耗时 | 占比 |
|---|---:|---:|
| TxBulkInsert | 35.0s | 19% |
| BulkInsert | 34.9s | 18% |
| UpsertBatch | 34.4s | 18% |
| BulkDelete | 26.0s | 14% |
| BulkUpdate | 20.6s | 11% |
| **前 5 项** | **150.9s** | **80%** |
| 其余 17 项 | 38.2s | 20% |

**5 个批量项吃掉 80% 的时间**，所以"删小项"省不下时间——删掉一半的小项只省 10%，
却砍掉点查/流式/键集分页/IN/计数/Join/自增回填/SQL 构建一整片覆盖。真正的杠杆是
**档位**与**重复项**。

### PerfHub（22 项 → 21 项，档位组合 44 → 30）

- **删 `TxTenInserts`**：1/10/100 三点里的中间点；有 1 与 100 就能分离"事务固定开销"
  与"每条约边际成本"，10 的信息量最小。
- **8 项只在最小档跑**（`Build*` / `InsertReturningId` / `IncludeJoin` / `TxSingleInsert` /
  `TxHundredInserts` / `TxRollback` / `TxBulkInsert`）：判据是**行数是否真的进入该项的测量**。
  `Build*` 不碰库、签名里没有行数参数（`BuildComplexQuerySql(conn)`），两档是**同一个测量的
  复制品**；`InsertReturningId` 用每次清空的独立自增表；`IncludeJoin` 固定 50 父×3 子；
  `Tx*` 条数固定；`TxBulkInsert` 与 `BulkInsert` 近重复（地板臂的 BulkInsert 本身就自开事务
  包整批），保留它是为覆盖"批量装载器在显式事务内"这条产品路径，属语义检查而非规模问题。
  省下每方言 21 个重复测量（按实测只省 1.6% 时间，价值在项数与测量数）。

### BDN（85 → 61 项 / 10 类）

| 类 | 改动 | 理由 |
|---|---|---|
| `01_Crud`（23）/ `07_OrmComparison`（4） | **不动** | 唯一进 CI、唯一有录制基线、唯一能挡回归的 27 项 |
| `02_Bulk`（7→5） | 删 `Dapper_MultiRowInsert_10000`、`PalORM_BulkInsert_10000` | 与 `[Params]` 矩阵在 10000 档完全重复；Params 是维度 2「耗时-行数曲线」的唯一曲线源 |
| `03_Gc`（5×Params 4→2 档） | `[Params(1,100,1000,10000)]` → `(100,10000)` | 1 与 100 对"装箱占总分配比"没有额外信息，减半省一半时间 |
| `04_SqlBuild`（3） | **不动** | Precision job（5/10/15+4096）是为把 Error/Mean 压到 5% 以下，改档会让该指标失真 |
| `06_Feature`（13→11） | 删 `PalORM_QueryAll_Small_10`、`PalORM_Concurrent_GetByKey_8x` | 前者与 01 的 `PalORM_QueryAll` 同表同 API；后者并发口径已由 PerfHub 与 `--workload` 覆盖，且 BDN 单线程套件放并发项违反规范 §7 |
| `08_Binary`（6） | **保留，形状登记进规范 §2** | 二进制列选型依据。原为未登记的自定义形状 `BenchBinary`（256B/64KB）——按规范补登记，而非改成 S4 尺寸（改形状会作废已文档化的实测数字） |
| `MySql`（9→1）/ `Pg`（9→1） | 各留 `PalORM_BulkUpdateBatch_*` | 其余项的跨方言覆盖由 PerfHub 权威承担（规范 §1.1）；这两类需真库、不在自动路径、无报告引用其结果。保留的是 PerfHub 无同名项的方言专有路径（`CASE WHEN` / `UPDATE FROM VALUES`） |
| `MySqlBulkColumnWidth`（2） | **不动** | 五项价值核查命中"文档化能力"（`BENCHMARKS.md` 有专章 + 2026-09-21 实测结论） |
| `--boxing` + `BoxingMicroBenchmark.cs` | **删** | v5.0 阶段 3.4 的一次性决策量具（判据写在代码里），决策已落地；同一问题由 `03_Gc` 覆盖 |

### DapperSuite（9 项 × 3 方言 → × 1 方言）

只跑 SQLite。它的定位是"与 Dapper 官方数字可对照的外部锚点"，而官方数字本身是单机 SQLite 的
——跑 PG/MySQL 得不到可对照的外部锚点，只是白花 2/3 时间。哨兵目的一个方言足够。
（另注：9 项里只有 3 项能算出跨臂比值，其余 6 项是孤立数字——`SqlCommand` 是声明的 Baseline
却没有任何臂与它对照。）

### 进度与结果表格化

- **PerfHub**：每项测量前打印 `[k/n] 已用 Xm Ys 余约 Zs`；启动时打印计划数与档位策略；
  结束时**计划数与实际数对账**，不符即警告（进度条说谎比没有进度条更糟）。
- **`perf.sh full`**：每步结束打印「本步耗时 + 累计」并刷新进度表；末尾汇总表给出各步耗时与占比。
- **`PerfGate report`**：写文件之外，在终端打印「各夹具最近一批 + 门禁判定」表格。
- 表格只让 ASCII 列参与宽度填充且不设表头——bash 在 C locale 下按**字节**计宽
  （一个中文 3 字节、`✓` 也 3 字节），对含中文或 `✓` 的列做 `%-Ns` 填充必然错位（实测过）；
  中文步骤名放最后不填充，状态用 ASCII 的 `OK`/`FAIL`，列自带"累计"字样故不需要表头。

### 实测

| 指标 | 改前 | 改后 |
|---|---:|---:|
| 项数（三套夹具合计） | 112 | **91** |
| `perf.sh full` | 42m28s | **约 33min**（PerfHub −13%、DapperSuite −7min、其余不变） |
| PerfHub 计划测量数 | 452 | 366（同覆盖，少 86 个重复测量） |
| `perf.sh` 冒烟（PerfHub sqlite t2000 quick） | 82s（`--quick` 未生效） | **35s**（`--quick` 已生效） |

### 未隔离与适用边界

- PerfHub 的档位精简只影响"行数不进测量"的 8 项，其余 13 项两档不变，故**规模曲线覆盖不减**。
- `08_BinaryBenchmarks` 的 256B 与 S4 的 32B/1KB 不同尺寸，跨夹具比较时须按派生形状看待
  （已在规范 §2 登记）。
- 删掉 24 个 BDN 项后，`run-benchmarks.sh` 的 `all` 目标耗时随之下降，但其**基线录制**用途不变。

### [5.6.0 ·工具链·四] — 修复 MySQL 方言整列失败：BulkDelete 播种超配 16.7 倍 + 默认超时

> 变更范围：`bench/PalORM.PerfHub/Implementations.cs` + `Program.cs` +
> `bench/perfhub/README.md` + `docs/性能基准规范.md`。
> 诊断工具：`.ai/perf-probe/MySqlSeedDiag.cs`（gitignored 本地探针）。

### 问题（逐段实测，非推测）

2026-09-23 全量跑测里 MySQL 在 tier 20000 档失败，内因 `SocketException`（995 = 操作被中止，
MySqlConnector 命令超时后中止 socket 的签名），随后连接 Broken、重连也超时。
PerfHub 全仓库未设 `CommandTimeout` → 驱动默认 **30 秒**。

探针逐段计时（远程 MySQL 8.4，CommandTimeout 放宽到 300 s 以取真实耗时）：

| 阶段 | 10 万行 | 100 万行 | 越 30 s？ |
|---|---:|---:|---|
| DDL | 0.2 s | 0.1 s | 否 |
| 客户端批量插入 | 7.8 s | 93.6 s | 每批 47 ms，不触发单命令超时 |
| 快照拷贝（单命令） | 8.9 s | **94.6 s** | **是** ← 首次失败点 |
| `DELETE FROM perf_s1`（单命令） | 9.5 s | **107.3 s** | **是** |
| 快照重置 INSERT（单命令） | 9.7 s | **114.0 s** | **是** |

tier 2000 的播种是 10 万行、全部在 9 s 内，故那档能跑通。

**为什么是 100 万行**：播种行数 = 迭代上限 × 档位行数（50 × 20000）。
而实测每轮删 20000 键 = 20 条 DELETE（`BulkSql.BatchRows` = 1000）、单轮 **2.02 s**
（每条语句 101 ms，远程 RTT 主导）→ 4 s 预算只跑 **3 轮**，实际只需 **6 万行**——
**超配 16.7 倍**。播种是纯开销：一次 100 万行播种 188 s，每次测量重置两遍、三臂共 6 遍
（约 1328 s），合计约 25 分钟只为一个测量项，且必然撞超时。

### 改动

1. **播种类语句显式设 600 s 命令超时**（`ExecSetupAsync`）：播种不是被测操作、不计入任何指标，
   不该因驱动默认值把整个方言打断；**被测命令仍用默认**，保持"挂住就快速失败"。
   `perf_s1` 的 DDL 也走该路径。
2. **MySQL 用 `TRUNCATE` 替代 `DELETE`**（`TruncateAsync`）：删 100 万行 107.3 s → 近瞬时
   （InnoDB 逐行删 + 每行 undo/redo 日志 vs 元数据操作）。PG/SQLite 保持 `DELETE`。
3. **播种改由总行数预算反推轮数**（`BulkDeleteSeedRowBudget` = 20 万行）：
   档位 2000 得 50 轮（**不变**，那里每轮只删 2000 键、单轮 0.20 s，4 s 预算能跑 19 轮，
   直接压上限会白丢样本）；档位 20000 得 10 轮。播种行数随之从 100 万降到 20 万。

### 实测（MySQL 单方言，两档）

| 指标 | 修复前 | 修复后 |
|---|---|---|
| 结果 | t20000 档 11 项后**整列失败** | **134 项全通过、0 失败登记** |
| 耗时 | — | 894 s（14.9 min），健康度 clean（0.085 / 0.35） |
| `BulkDelete` 三臂合计（两档） | ~1516 s（按探针推算） | **213 s**（约 7 倍） |

### 未隔离与适用边界

- **SQLite 档位 20000 的样本数由 49 降到 10**（其删除快，预算不再是瓶颈）——10 个样本对
  中位数仍够（BDN 默认 15 轮），分配量复现性从 0.07% 量级降到约 0.3%，仍远优于规范 §4 的 1% 门槛。
  该变化**随基线重录一并生效**。
- 远程 MySQL 的单条 DELETE 语句平均 **101 ms**，其中远程 RTT 占主导——同一修复在低延迟网络上
  收益会小得多；行数预算按"最坏情况（慢库）"设定，对快库是保守取值。
- MySQL 仍是三方言里最慢的（14.9 min 对 SQLite 约 4 min），瓶颈是 RTT 而非本修复能触及的部分。

### [5.6.0 ·工具链·三] — 精简后首次完整跑测（41 分钟）与由此暴露的三处缺陷

> 变更范围：`tools/PalORM.PerfGate/IndexGenerator.cs` + `scripts/run-full-perf.sh` +
> `docs/性能基准规范.md`。

### 跑测结果（`bash scripts/perf.sh full`，41m03s，5 步中 3 步通过）

| 步骤 | 结果 | 说明 |
|---|---|---|
| 1 微基准 + 负载 + 内存 + 启动 + BDN 门禁 | ✓ | BDN 27/27 阈值内 |
| 2 PerfHub 三方言全量 | ✗ | MySQL 在 t20000 档命令超时（见下），PG/SQLite 完整 |
| 3 DapperSuite 三方言 | ✓ | sqlite/mysql/pg 全通 |
| 4 门禁 | ✗ | 结果库门禁 20/102 失败，全部来自失效基线（见下） |
| 5 统一报告 | ✓ | 单一产物 |

**精简的净效果**：PerfHub 本次 **386 项 / 1448 s**，对比旧协议最好的一次
**190 项 / 1500 s**——**项数 2.03 倍、耗时 0.97 倍**（吞吐 2.1 倍）。且本次是首次
把 PG 两档完整跑通（历史 26 个批次没有一次跑完三方言）。

### 由此暴露并修复的三处缺陷

**① 报告会藏起最新最全的数据。** 上轮加的"优先选无失败登记的批次"规则在本次反向伤人：
386 项只缺 MySQL 的批次被跳过，表里显示的是前一天 152 项的旧批次。改为**始终取最近一批**，
完整性靠 `方言范围` 与 `失败登记` 两列披露，不靠隐藏批次；"全部批次"表新增 `失败登记` 列，
让跨批次反复出现的失败可见（MySQL `BulkDelete` 在 5 个批次里 ⚠️ 2/4/4/5/6）。

**② `run-full-perf.sh` 收尾横幅打印未生成的报告路径。** `SKIP_REPORT=1` 时
`$REPORT_MD` 只是预留路径，横幅照旧打印它，让人去找一个不存在的文件。

**③ 索引基线失效（阻断项）。** 它的元数据自己写着"录制批次 2026-09-22 20:43:04
（健康度 **noisy 27.0%**）"——从一批被判 noisy 的数据录了门禁基线，而 §4.2 要求 noisy
批次重跑。后果：7/102 项 PalORM 比值 < 0.6（`Update/SQLite/2000` = 0.220，即 PalORM 比
手写 ADO 快 4.5 倍），隐含地板普遍比干净批次高 4~5 倍。本次 20 项 FAIL 中 16 项是
**地板暴跌 22~77% 而 PalORM 平/降**；另 4 项 PalORM 上升（PG `BulkUpdate` +94.5%、
SQLite `GetByKey` +46.4% 等）。**不重录部分基线**：`check-index` 的"缺项不判失败"是为
方言缺席设计的，重录 SQLite-only 基线会让 PG/MySQL 静默失去检查。

### 未隔离（已登记规范 §5）

- **流程位置会系统性抬高同一夹具的绝对值约 2 倍**：同一份代码，`GetByKey/SQLite/2000`
  独立跑 11.1 / 10.9 µs（连跑两次），全量流程内 21.67 µs；`StreamAll` 1.26 / 1.27 ms 对 1.94 ms。
  **健康度指标抓不到它**（两批都自报 clean，该指标只测批内散布，对批间水平漂移无分辨力）。
  机制未查明 [推断：前序 6 分钟步骤改变机器状态，SQLite 走文件库 + WAL 落在 I/O 敏感区间]。
  纪律：同一夹具的数字只在同一流程位置内可比。
- **MySQL 失败的根因定位到 `BulkDelete` 的播种路径**：tier 20000 要客户端播 100 万行
  （500 行/批 = 2000 次往返），远程 MySQL 上命令超时 → 连接 Broken。本轮改动已让它从
  历史的"t2000 档死在第 9 项"变成"t2000 档完整跑完 + t20000 档 11 项后失败"。
  剩余修法（把播种改成服务端一条语句 / 单独放宽播种命令超时）未做。
- PG/MySQL 本轮可达（网络恢复），但 MySQL 的 t20000 档仍未跑通。

### [5.6.0 ·工具链·二] — 性能测试系统精简：逐项耗时归因、BulkDelete 播种、`--quick` 生效

> 变更范围：`bench/PalORM.PerfHub/`（Program / Measure）+ `bench/PalORM.Benchmarks/`
> （删 05_SqliteSpeedBenchmarks、BenchmarkConfig）+ `scripts/`（perf.sh / run-benchmarks.sh /
> run-full-perf.sh / dappersuite-run.sh / perfhub-ab.sh）+ `docs/`（性能基准规范 / 测试体系路线图）。

### 问题（逐项耗时归因实测，非推测）

先给 PerfHub 的每项测量加上墙钟耗时输出（只进控制台，不进信封 schema），归因结果：

| 位置 | 耗时 | 占该档 |
|---|---:|---:|
| tier 20000 档合计 | 390.1 s | — |
| 其中 `BulkDelete`（三臂） | **269.5 s** | **69%** |

`BulkDelete` 的播种行数 = 迭代上限 × 档位行数。上限 200 时 tier 20000 档要播 **400 万行**，
且每次测量重置两遍——而该档的计时轮数由 4 s 预算决定（`clamp(4 ÷ 81ms, 3, 200)` = **49 轮**），
**播种是实际消费的 4 倍**。这是纯开销，不计入任何指标。

另两处缺陷：
- **`--quick` 文档说了但没实现**：usage 写"迭代次数降到 30%"，但 `scale` 只流到
  `BulkDeleteSeedRounds`，`MeasAsync` 里被 `_ = (rows, scale);` 丢弃——冒烟跑其实没变快。
  而它缩放播种、却不缩上限，还让尾部轮次删到不存在的键（空删最快，中位数不受影响，
  但分配量按"整轮总分配 ÷ 轮数"计算会被低估）。
- **表状态漂移**：`BulkDelete` 播种过大 → 删完仍残留约 300 万行 → 其后的
  `KeysetPage`/`WhereIn`/`Count` 在**机器速度决定的表规模**上测量（消费轮数由单次耗时决定）。
  证据就在基线里：SQLite 档 `Count` t20000 记的是 **114 ms**，而 t2000 是 17 µs（6700 倍）。

### 改动

- 新增逐项墙钟耗时输出（`PrintRow` 带 elapsed，不进信封）。
- `BulkDelete` 迭代上限 200 → **50**，播种随之一致（`BulkDeleteSeedRounds` 不再随 scale 缩放）。
  该档计时轮数由预算决定（49 轮），**上限降低不减少样本数**，只把播种降到 1/4。
- `--quick` 真正按 0.3 收缩计时与预热两条预算（`Measure.SingleAsync` 新增 `scale` 参数）。
- `KeysetPage`/`WhereIn`/`Count` 显式带 `reset` prepare，保证"从表内恰好 rows 行开始"。
- 删除 `05_SqliteSpeedBenchmarks.cs`（4 项）：它是 01 的真子集（同操作/同 SQL/同 job），
  且因无 MemoryDiagnoser 被 `ResultReader` 结构性排除在门禁之外，数字从未被引用；
  路线图 P2-C 据此关闭，复现方式留在 `run-benchmarks.sh speed` 的提示里。
- 五个脚本标注角色：`perf.sh` 与 `run-benchmarks.sh` 是仅有的两个用户入口，
  其余三个是被调用的编排步骤。

### 实测（同机串行 SQLite 档，134 项不变）

| 批次 | 耗时 | 说明 |
|---|---:|---|
| 改前（仅预热修复） | 444 s | — |
| + BulkDelete 上限 50 | 270 s | `BulkDelete` 三臂 269.5 → 74.4 s |
| + Query 组 reset（最终） | **250 s** | 修表状态漂移，另付 ~20 s 重置成本 |

累计：**640 s → 250 s（−61%）**。归因验证其余项逐项未动
（`BulkInsert` 5.9→6.0 s、`QueryAll` 1.7→1.7 s），单变量隔离成立。

### 未隔离与适用边界

- **`Update`/`BulkUpdate` 的 reset 尝试已回退**：加上后其后 ADO 臂的 `BulkInsert` 单次从
  165 ms 跳到 1571 ms（9.5×，Dapper/PalORM 臂不变），机制未查明。按"不引入无法解释的行为"
  撤掉，两项的残余漂移（`Update` +1%、`BulkUpdate` 约 25 倍表）登记在规范 §5 待办。
- Query 组 reset 使 PalORM 臂其后 8 项分配量系统性下移 3~21%，**可复现**
  （两个改后批次互差 0.1~1.0%），机制未查明。
- **旧基线必须重录**：上述改动改变了 3 项自身与 PalORM 臂 8 项的数字，
  且 `BulkDelete` 的样本数由 200 降到 50（tier 2000 档）。
  `bench/baselines/perfhub-index-baseline.json` 与 `perf-baseline.json` 需按规范 §5
  「基线重录」流程在新口径下重录后再卡阈值。
- PG/MySQL 本次仍不可达，三方言矩阵的完整性未验证。
- **索引基线失效（阻断项，已登记规范 §5）**：`perfhub-index-baseline.json` 的元数据自己写着
  "录制批次 2026-09-22 20:43:04（健康度 **noisy 27.0%**）"——从一批被判 noisy 的数据录了门禁基线。
  后果：该基线 7/102 项 PalORM 比值 < 0.6（`Update/SQLite/2000` = 0.220，即 PalORM 比手写 ADO
  快 4.5 倍），隐含地板普遍比干净批次高 4~5 倍。2026-09-23 用干净批次打它 → 14/102 FAIL，
  且每一条都是地板暴跌 44~79%、PalORM 自身只动 −0.5%~−45%，失败来自坏基线而非真回归。
  **不重录部分基线**：`check-index` 的"缺项不判失败"是为方言缺席设计的，重录 SQLite-only
  基线会让 PG/MySQL 静默失去检查。动作：PG/MySQL 可达后跑干净全量 → `record-index` → 人工过 diff。

### [5.6.0 ·工具链] — 性能测试系统审计：预热口径、唯一报告、时间预算

> 变更范围：`bench/PalORM.PerfHub/Measure.cs`（预热收敛）+ `tools/PalORM.PerfGate/`
> （IndexGenerator / ReportGenerator / Program）+ `scripts/perf.sh` + `scripts/run-full-perf.sh` +
> `docs/性能基准规范.md` + 三处 README / BENCHMARKS.md。

### 问题（审计实测，非推测）

**① 预热是无界的时间成本。** PerfHub 单操作预热固定 `maxIterations/5` 次（Build 400，其余 40），
与单次耗时无关。20K 档 Dapper BulkInsert 单次 1.36 s → 40 次预热 **54 s**，是计时段
（4 s 预算 → 3 次 ≈ 4.1 s）的 13 倍。预热的目的（JIT、驱动缓冲、语句缓存）在前几次即达成，
规范 §4 也只要求「≥1.5 s 预热不计数」。

**② 报告碎片化，且维度表与实现矛盾。** BDN 明细在 `perf-report-*.md`、跨夹具登记在
`perf-index.md`，读者要自己拼；「12 维总览」把已实现的维度 8（PerfHub 往返计数）与
维度 10（长稳量具）硬编码标成「未实现」。

**③ 残缺批次被当成可引用。** 方言级/单项失败写在信封 `sections`，而报告只渲染 `items`：
PG/MySQL 连接超时时批次只剩 1 项、健康度仍报 clean，读者会读成「该夹具已覆盖」。
实测 2026-09-22 的 26 个历史批次**没有一次跑完三方言全矩阵**（MySQL 恒停在 9 项）。

**④ `gate-set` 是子集标签却未登记。** `run-full-perf.sh` 的门禁同参集只跑 11 个基准类里的
Crud + OrmComparison 两个类，标签 `gate-set` 不含 `filtered`，故 `IsSubsetLabel` 判它非子集
→ 这个残缺矩阵会顶掉 `latest-benchmarks.json` 并出现在「最近一批（可引用）」表里。

### 改动

- `Measure.SingleAsync` 预热改为**按时间收敛**：至少 3 次，累计达 1.5 s 即停，上限仍是
  `maxIterations/5`。快操作行为与固定次数一致，慢操作不再承担无界成本。
- `perf.sh full` 的第 5 步在**三套夹具都写完结果库信封之后**生成唯一报告
  `bench/reports/perf-report-<时间戳>.md`（`run-full-perf.sh` 内部报告由 `SKIP_REPORT=1` 让位）；
  新增 `perf.sh index` 保留单独查看索引的入口。
- `PerfGate report` 新增 `--envelopes` / `--index-baseline`：同一份报告追加跨夹具批次登记
  （口径登记 + 健康度 + **方言范围** + 失败登记）与关键项速览。
- 12 维总览改为**按本轮实测批次推导**；新增「批次失败登记」节；「最近一批（可引用）」
  表带 `方言范围` 与 `失败登记` 两列披露完整性（选取规则见「工具链·三」——该轮曾短暂改成
  "优先选无失败登记的批次"，实测证明会藏起最新最全的数据，已改回取最近一批）。
- `PerfResultWriter.IsSubsetLabel` 补登记 `gate-set`（规范 §6 的子集清单同步补齐）。

### 实测（同机串行，SQLite 档，S2/S3 双向验证）

| 步骤 | 修复前 | 修复后 |
|---|---:|---:|
| PerfHub sqlite 两档（134 项） | 640 s | **444 s（−31%）** |
| DapperSuite sqlite（9 项） | 142 s | 142 s（未涉及） |
| BDN 门禁集 27 项（1/3/5） | 294 s | 294 s（未涉及） |
| 负载 1/2/4/8 线程 · 内存曲线 | 15 s · 3 s | 15 s · 3 s |

项数与逐项分配量不变。S3 反向验证：还原旧 `Measure.cs` 重跑，分配量给出的是**新值**
（`InsertReturningId` 1941 对 1939/1936/1933），证明 4 项 2.5–8.8% 的分配差异来自
**「重建后首次运行」效应**而非本改动——该效应已写入规范 §4 的分配纪律。

### 时间预算归属（三方言全量估算，约 45 min）

PerfHub 三方言 ≈ 32 min（其中 tier 20000 占 80%：单档 90 s 对两档 444 s）、
DapperSuite 三方言 ≈ 7 min、BDN 门禁集 + 负载 + 内存 ≈ 5 min。
**剩余的进一步压缩都是覆盖取舍**（如砍 tier 20000 或按方言分档），不是纯工程优化。

### 未隔离与适用边界

- 预热收敛对**慢操作**改变了实际预热次数（40 → 3），故 PerfHub 的绝对耗时在慢项上
  与旧批次不可直接比；比值口径（同轮 ORM/地板）不受影响。旧基线仍需按规范 §5
  「基线重录」流程在新口径下重录后再卡阈值。
- 「重建后首次运行」效应的机制未查明 [推断：JIT 分层编译与代码布局在首次运行未收敛]。
  纪律：录基线前先跑一轮热身批次丢弃。
- PG/MySQL 本次不可达（连接超时），三方言矩阵的完整性**未验证**；报告已能如实登记该缺口。

### [5.6.0 ·性能轮七] — SQLite 单行查询 LIMIT 字面量内联（First/Single 族）

> 变更范围：`src/PalORM.Core/QueryBuilder.cs`（BuildLimitClause / 形状键 / 新字段）+
> `QueryBuilderExtensions.cs`（First/Single 族置标志）+ `SqlShapeCache.cs`（ShapeFields 加字面量维度）+
> 测试两处 + `bench/PalORM.DapperSuite/README.md`。

### 问题

SQLite 上 PalORM 的单行查询明显慢于自己的无 LIMIT 查询：`FirstOrDefault<T>` 4.71× 地板、
`QueryFirst<T>` 4.21×，而同臂 `Query<T> (buffered)` 只有 2.31×（同一批次内比值）。

### 归因（两步独立测量，非推测）

第一步在 ADO.NET 层隔离 SQL 形状（同一条连接、同一物化路径，只差 SQL 文本）：

| SQL | Mean | Allocated |
|---|---:|---:|
| `where "Id" = @Id` | 5.534 µs | 778 B |
| 同上 + ` limit 1`（字面量） | 5.658 µs | 778 B |
| 同上 + ` limit @L offset @O`（PalORM 实际形状） | 13.557 µs | 970 B |
| 同上 + ` limit @L`（不带 OFFSET） | 14.289 µs | 874 B |

字面量与无 LIMIT 同价，**参数化 LIMIT 多付 7.90 µs**，OFFSET 无贡献。
第二步走 PalORM 原始 SQL 通道端到端复核：字面量 12.793 µs 对参数化 22.571 µs，
且字面量落回无 LIMIT 档（12.902 µs），即该差值不掺 ORM 机械。
两步与产品侧观测吻合：探针批次内 `单行族 − buffered` 为 +7.99 µs。

### 改动

`FirstAsync` / `FirstOrDefaultAsync` / `SingleAsync` / `SingleOrDefaultAsync` 的 take 由 API 固定为
1 或 2 且不带 `Skip`，故在 **SQLite** 上把该值内联为字面量（`LIMIT 1` / `LIMIT 2`）并不再发 OFFSET。
边界刻意收窄，以保住 SHAPE-010 的有限形状集：

- 用户 `Take(n)`（值域无界）与 `ToPageAsync`（pageSize 无界）维持参数化；
- 带 `Skip` 的 First/Single 族（动态分页）维持参数化；
- PG/MySQL 维持参数化：该形状差异在联网库上被网络往返淹没到不可测量（PG 地板自身行间差曾达 9%），
  无证据支持改其文本；
- 形状缓存键新增字面量维度（`ShapeFields.TakeLiteral`），字面量值进键与哈希，
  避免 `LIMIT 1` 与 `LIMIT 2` 互相复用条目；`CloneForExecution` 同步复制该字段。

### 实测（配对复测，同机 3 分钟内，因子开/关）

| 项 | 因子关 | 因子开 | 差值 |
|---|---:|---:|---:|
| `FirstOrDefault<T>` | 20.538 µs | 14.823 µs | −5.72 µs（−28%） |
| `QueryFirst<T>` | 23.099 µs | 14.939 µs | −8.16 µs（−35%） |
| 每操作分配（单行族） | 3506 B | 2946 B | −560 B（两个 limit 参数消失） |
| 对照项 `Query<T> (buffered)`（无 take，不受改动影响） | 11.897 µs | 12.701 µs | 噪声内 |

三方言正式批次（修复后）的比值：SQLite PalORM 2.06 / 2.10 / 2.18 对 Dapper 1.89–1.99；
MySQL 1.06–1.09 对 Dapper 1.03–1.07；PG 1.06–1.08 对 Dapper 1.12。

### 测试

- Core 新增 5 例：字面量形态与参数快照（无 limit 参数）、`Single` 用 `LIMIT 2`、带 `Skip` 退回参数化、
  用户 `Take` 维持参数化、`LIMIT 1` 与 `LIMIT 2` 占两条形状缓存条目。
- Integration 新增 2 例（真库）：PG/MySQL 的 `FirstOrDefaultAsync` 保持参数化 LIMIT 且取值正确。
- 三套实测：Core 365 / SourceGen 197 / Integration 205（合计 767），全绿。

### 未隔离与适用边界

SQLite 为何对参数化 LIMIT 多收约 8 µs，机制未查明 [推断：参数化值在 prepare 期无法常量折叠]。
故本项收益在换驱动版本后需复测；`bench/PalORM.DapperSuite` 的 SQLite 档即是该复测口。

**收益随环境而变，报告时必须带环境**：本项只在 CPU 主导的 SQLite 配置下可见。
用 PerfHub 的 SQLite 档做交叉验证（2000 行档，默认 journal 模式 + 2MB 缓存，
地板与三臂同在 129–144 µs/op 的 I/O 主导区间）时，因子开/关无差别（138.3 对 134.8 µs），
页 I/O 把 8 µs 的 CPU 节省淹没了。DapperSuite 的 SQLite 档启用 WAL + 64MB 缓存 + mmap
（三臂同一组 PRAGMA，见其 README 口径差 D10），属 CPU 主导，故能分辨。

### [5.6.0 ·性能轮六] — MySQL QueryAll 归因修正 · SQLite 并发边界文档化

> 变更范围：docs/架构设计.md + README.md + CHANGELOG.md（纯文档，无代码改动）

### MySQL QueryAll 慢 44% 的归因结论（修正上一轮判断）

BDN 基准显示 MySQL `QueryAll` Ratio 1.44（慢 44%），而 PG 是 0.82（快 18%）。
上一轮我说「分配差异只解释一半，剩余需要 profile」。profile 完成后结论不同：

| 形态（10000 行 × 4 列，8 次平均） | 耗时 |
|---|---|
| PalORM QueryAll（每次建会话） | 16.94 ms |
| 裸 ADO.NET 同 SQL（每次建连接） | 16.22 ms |
| 裸 ADO.NET 只读（不物化实体） | 14.70 ms |
| **PalORM 会话复用** | **10.49 ms** |

**查询路径本身只慢 4.4%**（16.94 vs 16.22 ms），不是 44%。BDN 那 44% 的主要来源是
**建连口径不可比**：BDN 的 `ADO_NET_QueryAll` 臂在迭代间复用同一连接（池已预热、
建连成本被 warmup 吸收），而 PalORM 臂每次 `CreateAsync` 都要建会话。这也解释了为何
BDN 里 ADO.NET 臂只要 6.26 ms 而这里的裸 ADO.NET 要 16.22 ms——前者把建连排除在外了。

真正的成本在**建会话/建连接**：会话复用后 PalORM 只要 10.49 ms，比每次建连接还快 35%。

因此不把「MySQL QueryAll 慢 44%」列为优化项——**它是基准口径差异，不是产品缺陷**。
上一轮方案里的 P1-1 随之撤销。

### SQLite 并发边界文档化（P2-2）

`docs/架构设计.md` 的「会话并发生命周期」段后新增方言并发扩展性实测表（三方言 × 4 档），
README 的配置表处补选型提示。数据来自 `--workload` 80/20 读写混合、10K 行种子：

- PostgreSQL：1→8 线程 1,745 → 12,617 ops/s（**7.2×**）
- MySQL：1,734 → 8,034 ops/s（**4.6×**）
- SQLite：75,720 → 23,304 ops/s（**−69%**）

SQLite 负扩展已用裸 ADO.NET 同负载对照证实（裸 ADO.NET 同样 −69%），根因是 WAL 单写者
在 20% 写占比下的写串行化，不是 PalORM 引入的锁。文档明确选型建议：高并发写用 PG/MySQL，
SQLite 适合读密集或低并发写的嵌入式场景。

### [5.6.0 ·性能轮五] — 写路径直调重载（单条写 −3.4%~−5.5%）

> 变更范围：src/PalORM.Core 三个文件 + 一个测试项目
> 验证：`PalORM.ci.slnf --no-incremental` 0 警告 0 错误 · Core 360/360 ·
> Integration 203/203 · 三个 AOT 程序 `publish` 全通过 · tech-debt-scan 13/13 ·
> stub-check 零发现

### 背景

P0-2 原计划「MySQL Insert_1by1 4184 B/行池化」。分解实测后否决了该方案：

| 形态 | 每行分配 |
|---|---|
| PalORM InsertAsync | 4249 B |
| 裸 ADO.NET 同形（新建 command + 3 参数） | 2936 B |
| 裸 ADO.NET + 命令/参数复用（理论下限） | 2024 B |
| **PalORM BulkInsert（已池化）** | **1222 B** |

**单条 `InsertAsync` 的语义决定了无法跨调用复用命令**——每次都是独立调用，
跨调用持有命令对象会改变生命周期契约。而 4249 B 里驱动固有 2024 B（动不了），
可优化空间只有约 5%。投入产出比不成立，故不做。

### 改动（P2-1）

改为消除写路径的委托分配——这是**所有单条写路径共有**的固定成本。

新增 `ExecuteWriteRowsAsync(DbCommand, CancellationToken)`：调用方已持有命令时经此入口，
直通分支（策略直通或事务内）直接 `ExecuteNonQueryAsync`，不经
`async token => (long)await cmd.ExecuteNonQueryAsync(token)` 的委托与 display class；
非直通分支才包 `ExecuteWithTimeoutAsync`（该形态少见——写入路径默认不重试，
只有显式配置非零 CommandTimeout 才会走到，语义不变）。

原 `ExecuteWritePipelineAsync<T>(Func<CancellationToken, Task<T>>, …)` 已无调用方，删除。
四个调用点（Update / Delete 软删 / Delete 物理删 / ExecuteAsync）改走直调重载。

### 实测收益（A/B 交替）

| 路径 | base | HEAD | 变化 |
|---|---|---|---|
| BulkUpdate 池化（P0-1，本轮复测） | 1273 B/行 | **426 B/行** | **−67%** |
| UpdateAsync 单条 | 1632 B/行 | **1576 B/行** | **−3.4%** |
| DeleteAsync 单条 | 1304 B/行 | **1232 B/行** | **−5.5%** |

与隔离实验预测的 208 B/行（含两层委托包装）方向一致，实测 56~72 B/行——
差额来自 JIT 对部分委托的内联。

### 测试修正

`PooledPath_Allocation_IsBelowLegacyRowByRow` 原断言全局分配低于 1000 B/行。
**并行测试的后台分配会污染 `GC.GetTotalAllocatedBytes` 样本**（单独跑三次均过、
全量跑偶发失败），`[NotInParallel]` 也无法阻断不同组之间的并行。改为
`PooledPath_ReusesCommandAndParameters_NoGrowthPerRow`——断言写值正确性与重复执行的
幂等性（参数错位会立刻暴露），不再依赖全局计数。每行成本的量由 bench 的 Gc 基准与
真库 A/B 覆盖，单测里重复断言全局分配既不可靠也无必要。

### [5.6.0 ·性能轮四] — 逐条 UPDATE 参数池化（跨方言 −41%~−62%）

> 变更范围：src/PalORM.Core 一个文件 + 一个测试项目（新增 8 个测试）
> 验证：`PalORM.ci.slnf --no-incremental` 0 警告 0 错误 · Core 360/360 ·
> Integration 203/203 · 三个 AOT 程序 `publish` 全通过 · tech-debt-scan 13/13 ·
> stub-check 零发现 · 变异探针通过

### 背景：全面性能测试定位到的系统性缺陷

三方言每行分配成本实测（2000 行，`GC.GetTotalAllocatedBytes` 精确计数）：

| 路径 | PG | MySQL | SQLite |
|---|---|---|---|
| BulkInsert（多值/COPY） | 148 B | 736 B | 415 B |
| **BulkUpdate 逐条** | **1618 B** | **1577 B** | **1457 B** |
| **乐观锁实体逐条** | **2488 B** | **3830 B** | **1568 B** |
| QueryAll | 90 B | 139 B | 90 B |

逐条 UPDATE 是 BulkInsert 的 2.2~11 倍。根因：`UpdateCoreAsync` 每行新建
`DbCommand`、`BindUpdate` 重建全部参数、`ExecuteWritePipelineAsync` 新建 async 委托。
同一文件的 `MultiValueBulkInsert` 自 v4.6 就有「命令跨批复用 + 参数池 + 只写 Value」
三件套，逐条 UPDATE 是漏项。

### 改动（P0-1）

`ExecuteBulkUpdateRowByRowAsync` 按 `CrudMetadata.BindUpdateValues` 是否为 null 分派：

- **有**（当前生成器）：走 `ExecuteBulkUpdatePooledAsync`——命令与参数池建一次，
  逐行只写 Value（零 `CreateParameter`），复用 `BatchUpdateSqlBuilder.CreateParameterArray`
  的命名契约与 `AttachParameters` 的池↔集合收敛。
- **无**（旧版模型程序集）：回退 `ExecuteBulkUpdateLegacyAsync`，逐行 `UpdateCoreAsync`，
  语义与改前逐位一致。

**参数数由 probe 提取而非硬算**：`BindUpdateValues` 的参数序是
`[SET 列…, 主键…, 并发令牌?]`（见 `CommandFactoryEmitter.GenerateBindUpdateValuesBody`），
带 `[ConcurrencyCheck]` 的实体比 `setColumnCount+1` 多一个 version 参数。硬算会让乐观锁
实体的 version 参数拿不到值，**静默写错数据**。probe 同时是生成器三处
（SQL/Bind/元数据）漂移的运行时哨兵，与 `PrepareBatchUpdateContext` 同范式。

**乐观锁语义保持不变**：affectedRows 的 0 行/多行检查、`ConcurrencyConflictException`、
以及 version 内存回填的时机（ITM-556：提交成功后统一执行）都按原契约保留。
池化只改「参数怎么来」，不改判定。

### 实测收益（真库，A/B 交替）

| 路径 | base | HEAD | 变化 |
|---|---|---|---|
| SQLite BulkUpdate | 1457 B/行 | **522 B/行** | **−64%** |
| SQLite 乐观锁实体 | 1568 B/行 | **602 B/行** | **−62%** |
| PG 乐观锁实体 | 2488 B/行 | **952 B/行** | **−62%** |
| MySQL 乐观锁实体 | 3830 B/行 | **2267 B/行** | **−41%** |
| PG BulkUpdate（普通实体） | 1618 B/行 | 1592 B/行 | −1.6%（噪声内） |
| MySQL BulkUpdate（普通实体） | 1577 B/行 | 1496 B/行 | −5.1%（噪声内） |

普通实体在 PG/MySQL 上走**单语句批量路径**（`BulkUpdateAsync` 的自动路由），
本改动不作用于该路径，故无差异——这与设计一致：池化只惠及逐条路径。

### 踩坑：闭包捕获循环变量

首版 `increments.Add(() => metadata.IncrementVersion(entities[i]))` 抛
`IndexOutOfRangeException`——lambda 在提交成功后统一执行，届时 `i` 已越界
（ITM-556 的延迟回放语义）。修为每次迭代捕获局部实体变量。测试
`OptimisticLock_Success_IncrementsVersionInMemory` 立即暴露。

### 测试

新增 `BulkUpdatePoolingTests`（8 个）：池化路径写值正确性、分配低于 1000 B/行的回归
护栏、乐观锁冲突仍抛、乐观锁成功 version 回填、租户隔离、软删不碰 `deleted_at`、
空列表零副作用、批次中途失败整批回滚。

变异探针：把 `valuesBinder(pool, entities[i], 0)` 的偏移改为 1（模拟参数错位），
8 个用例中 6 个失败——确认测试不是空转。

### [5.6.0 ·性能轮三] — 事务前置校验 · 只读内核零 display class · 通知监听器保活

> 变更范围：src/PalORM.Core 五个文件 + 三 Provider + 两个测试项目（新增 15 个测试）
> 验证：`PalORM.ci.slnf --no-incremental` 0 警告 0 错误 · Core 352/352 · SourceGen 197/197 ·
> 三个 AOT 程序 `publish` 全通过 · tech-debt-scan 13/13 · stub-check 零发现

### 🔒 可靠性

- **PG 咨询锁新增事务前置校验（A1，行为变更）**：`pg_advisory_xact_lock` 是事务级锁，
  事务外调用会获得锁但立即释放，而方法原先正常返回——调用方以为临界区已持锁，
  跨进程互斥形同虚设且无任何错误信号。四个入口（单/双键的获取与尝试）统一显式失败，
  错误消息指向 `BeginTransactionAsync`/`WithTransaction`。类文档同步改写，
  并补「会话级 `pg_advisory_lock` 在连接断开时不释放」的警告。
- **`DataSession.IsInTransaction` 公开（新增 API）**：事务级语义的 API 需要调用方自查
  前置条件。返回 true 覆盖自开与 `UseTransaction` 外部设入两种来源；外部事务失效时
  （ITM-640）返回 false。
- **LISTEN 连接新增心跳保活（A2）**：原等待循环单次无限期 `WaitAsync`，连接被中间设备
  静默掐断（NAT 超时、LB 空闲切断、无 FIN/RST）时不返回也不抛错，重连逻辑完全不触发，
  低频通道上 NOTIFY 丢失可持续数小时无痕。改为周期性 `SELECT 1` 探测，失败即走既有
  transient 重连路径。间隔默认 30 秒（远小于常见 NAT/LB 空闲超时 60~350s），
  可经内部构造的 `keepaliveInterval` 注入以便测试与按部署调整。
- **重连退避加全量抖动（A5）**：原 `attempt => TimeSpan.FromSeconds(attempt)` 无抖动，
  PG 恢复瞬间同进程/同机的大量监听器以完全同步的 1s/2s/3s 节奏重连，对刚恢复的
  服务端形成二次冲击。改为线性 + 0~100% 随机乘子，上限 30 秒。
- **`NpgsqlNotificationConnection.OpenAsync` 清理失败静默（A4）**：原 `DisposeAsync`
  在连接半开状态下抛异常时会 (a) 顶掉原始 `NpgsqlException` 使根因湮灭，
  (b) 更严重的是它不带 `PalORM.IsTransient` 标记，被 `IsTransient` 判为非瞬时，
  把本可重连的瞬时开锁失败升级为监听器永久死亡。
- **`LastError` 入口清零（A6）**：原只在 `started.Task` 成功后清零，首次启动就失败的
  路径会把上一次会话的异常留给调用方（与本次失败无关的陈旧故障态）。
- **`NotifyAsync` 显式设 CommandTimeout（A6）**：原走驱动默认 30s，与
  `DataSession.CreateCommand` 声明的「超时权威源」脱钩。
  配套 `DbOptions.ToCommandTimeoutSeconds` 与新增的 `DbOptions.DefaultCommandTimeout`
  改为公开（Provider 是独立程序集，需要同一套 TimeSpan→秒口径）。

### ⚡ 性能

- **参数名缓存扩容到 65535（B1）**：`ParameterNameCache` 原上界 1024，而 MySQL 多值
  INSERT 的满批参数池上限是 `SqlLimits.MaxBindParameters`(65535)——4 列实体默认
  `batchSize=1000` 时 `poolSize=4000`，索引 1024..3999 共 2976 个全部落入
  `$"@p{index}"` 插值分支。SQLite 上限 999 不越界、PG 走 COPY 不建该池，
  **仅 MySQL 受益**。代价是常驻约 1.3 MB 字符串表。
  <br>**实测（2026-09-21）**：隔离微基准（确定性，`GC.GetTotalAllocatedBytes`）建池一次
  base 151,064 B → HEAD 32,024 B，净省约 **95 KB/次 BulkInsertAsync**（4 列实体、
  1000 行/批）。真库 MySQL 交替 A/B 四轮：4.39 MB → 4.31 MB（**−80 KB，−1.8%**），
  与微基准吻合；但真库分配的轮次间波动本身有 60~70 KB，信号仅为噪声的 1.2 倍，
  故以微基准为准。**耗时无结论**——同配置连跑方差 ±30%。
- ~~**只读查询内核零 display class（B2）**~~ **已回退（2026-09-21 实测结论）**：
  原计划把 `ExecuteQueryAsync` 的单次尝试内核从「捕获七项的 async 局部函数」改为
  `static` 局部函数 + `QueryExecutionState<T>`，期望消除 display class（~250B）与
  委托转换（56B）。**实测未获任何收益，已回退。**
  实测（SQLite，`From<T>().FirstOrDefaultAsync()` 会话复用，`GC.GetTotalAllocatedBytes`
  精确计数，2000 次）：改造前后均为 **2,552.2 B/op**，逐字节相同。
  隔离实验（同量具，10 万次）揭示原因：三种写法分别为
  「局部函数捕获 7 变量」483.78 B/op、「static + struct 参数但经 lambda 传给
  `ResilienceExecutor.ExecuteAsync(Func<…>)`」531.15 B/op、「static + struct 直接调用」
  301.36 B/op。**display class 并没有被消除**——`token => ExecuteCoreAsync(token, state)`
  仍捕获局部变量 `state`，只是把七次捕获换成一次（且 `QueryExecutionState<T>` 内嵌
  `QueryBuilder<T>` 大 struct，display class 反而更大）。真正能省 182 B/op 的第三条路
  要求不经委托，而函数指针方案需要 C# 预览版内存安全规则（CS8652），对生产 AOT 库
  不可接受。
  结论：经 `ResilienceExecutor` 的委托式 API 无法消除该分配；代码注释里「未做」的
  登记仍然成立，保留原实现。
- **PG COPY 目标引用形态按 (Type, Dialect) 缓存（B3）**：原每次调用重算
  `string.Join(", ", InsertColumns.Select(QuoteIdentifier))`（方法组转委托 + LINQ
  迭代器 + Join 中间数组 + 每列一次引用）。键空间 = 实体数 × 3，天然有限。
  <br>**实测（2026-09-21）**：分配 2.94 MB → 2.93 MB（**−10 KB**，三轮均确定性一致）。
  这 10 KB 主要来自同批落地的 **M2**（COPY 的 `rowCommand` 从每批新建改为整批一次，
  10 批省 9 个 `DbCommand`），B3 本身省的 `string.Join` 只有数百字节。
  **耗时无差异**——交替 A/B 四轮 BASE 33.52 ms vs HEAD 33.76 ms，互相交错。
  （首轮非交替测量曾显示 BASE 38 ms / HEAD 33.6 ms 的 10% 优势，那是 BASE 撞上慢时段
  的假象，交替设计控制住后消失。）
- **通知监听器分发零分配（B6）**：`OnNotification` 改自定义 add/remove 缓存调用列表
  快照（Interlocked 换数组），分发路径不再每次 `GetInvocationList()`——原每收到一条
  NOTIFY 即一次 `Delegate[]` 分配。
- **LISTEN 多 channel 合并单次往返（B7）**：原逐 channel 各一次
  `ExecuteNonQueryAsync`（各建一个 `NpgsqlCommand`），N 个 channel 的启动/重连延迟
  = N × RTT。PG 的 LISTEN 可在一条命令里用 `;` 拼接多条，压成 1 次；
  引用名同时在构造期预计算（重连不重跑 `IdentifierSafety` 校验与拼接）。

### 🧪 测试

- 新增 `TransactionGuardTests`（5 个）：`IsInTransaction` 在无事务/事务内/外部
  `UseTransaction`/回滚后/`WithTransaction` 内的五种形态。
- 新增 `PgNotificationListenerHotPathTests`（5 个）：B7 合并 LISTEN 后全部 channel
  仍被监听、B6 多订阅者都收到且取消订阅后不再收到、A2 心跳失败触发重连、
  A2 心跳成功不重连。
- A2 的用例暴露了实现缺陷：心跳间隔原为硬编码 30 秒，测试无法在合理时间内验证，
  也无法按部署调整——改为可注入参数后两用例通过。

### 未实施（记录在案）

- **A3 Channel 分发**：慢订阅者会阻塞整个通知泵（到达率 > 1/T 时积压无界）。
  本次未做：它改变「回调在后台监听任务线程上执行」这一既有契约，需要先与调用方
  确认线程语义变更的影响面，不适合与上述改动混在一批。

### [5.6.0 ·性能轮二] — 批量路径收敛 INSERT 范式 · 事务收口三分支裁决 · 熔断无锁快路径

> 变更范围：src/PalORM.Core 六个文件 + 三 Provider + 两个测试项目（新增 39 个测试）
> 验证：`PalORM.ci.slnf` 0 警告 0 错误 · Core 342/342 · SourceGen 197/197 ·
> Integration 203 项（28 项需外部库，与基线一致）· 三个 AOT 程序 `publish` 全通过 ·
> tech-debt-scan 13/13 · stub-check 零发现

### ⚡ 性能

- **批量 UPDATE 收敛 INSERT 路径的三件套（M1/L2）**：原实现是「调用方循环 + 每批一个
  `ExecuteBatchUpdateAsync`」，每批新建 `DbCommand`、每批重建全部参数、每批重建一份逐位
  相同的 SQL 文本。现收敛为 `ExecuteBulkUpdateBatchesAsync`：命令与参数池在进入循环前建
  一次（池按本调用实际最大批预留），逐批只写 `Value`；SQL 文本仅在批大小变化时重建
  （满批恒等，仅末批可能不同）。命令参数集合随批大小收敛——末批缩短时清空后按池内前缀
  重挂，参数对象全部来自池，无新分配。
  新增 `BatchUpdateSqlBuilder.CreateParameterArray`（仅建数组不挂集合）与
  `AttachParameters`（池 ↔ 命令集合收敛），`CreateParameterPool` 委托同一实现保证命名
  契约不漂移。
- **PG Binary COPY 的 `rowCommand` 与参数池外提到批循环外（M2）**：COPY 路径下
  `rowCommand` 只是参数容器（从不执行），原每批 `CreateCommand` + 异步释放，千批即千次
  `DbCommand` 与 `NpgsqlParameterCollection` 分配。`WriteRowAsync` 改收 `pool` 参数，
  满批直传避开 `DbParameterCollection` 索引器的跨接口虚调用与硬转型（P1）。
- **MySQL BulkCopy 的列布局与每批计算项外提（B1/L4）**：`pksToAdd`/`allColumns` 只由 ctx
  决定，原每批重算一次 LINQ + 两个集合分配（`Contains` 还是 O(pk×cols) 线性扫描）；
  现提为 `ColumnLayout` 一次算好逐批复用。批循环首行加 `ct.ThrowIfCancellationRequested()`
  检查点（C4）。
- **`local_infile` 探测按连接缓存（L1）**：原每次 `BulkInsertAsync` 都付一次
  `SHOW VARIABLES` RTT。改为 `ConditionalWeakTable<MySqlConnection, …>` 按连接实例缓存
  （60s TTL，探测异常不写入缓存）。每请求只插 5 行的短事务场景下这 1 次 RTT 与业务写入
  同量级。探测失败经 `BulkOperationFramework.CapabilityProbeFailures` 计数留痕（R14）——
  此前「探测故障」与「能力关闭」在慢路径上行为一致且都静默无痕，生产上突然变慢无从归因。
- **标识符引用免一次纯拷贝（L3）**：三方言 `QuoteIdentifier` 在无内嵌引号时
  `string.Replace` 仍返回新实例，改走 `string.Concat`。
- **BulkDelete 批语句单 VSB 构建（L5）**：替代「每批一个 `string[]` + `string.Join` +
  多重插值」的中间串；满批占位符名与语句文本预建，仅末批另建。
- **`BatchUpdateSqlBuilder` 改 `ValueStringBuilder` + `@pN` 数字直写（M7）**：原
  `new StringBuilder()` 默认容量 16，大块 SQL 需 ~16 次倍增并产生 chunk 链。
- **`ValueStringBuilder.Append(string)` 扩容封顶（M8）**：目标只需 520 字符时不再翻倍到
  1024，「至少 2 倍」封顶在 4096，超过的增量按需增长。
- **熔断拒绝消息预构造（M6）**：Open 态下每次请求都格式化 `DateTime` 是纯垃圾，改为开闸时
  生成一次缓存到字段，并暴露 `OpenUntil` 供诊断查询。
- **熔断 Closed 态无锁快路径（C1）**：`Enter`/`RecordSuccess`/`RecordFinalFailure` 原先每次
  DB 操作都抢同一把锁；现加 volatile 镜像，Closed 态（绝大多数时间）只读镜像 + generation。

### 🔒 可靠性

- **失败提交的回滚裁决从「方言非 SQLite」收紧为「失败看起来是服务端错误」（T1/R1）**：
  原依据只覆盖 PG/MySQL 文档的「COMMIT 出错即服务端回滚」。若 COMMIT 因连接断开/取消/超时
  失败，事务在服务端的最终状态未知，跳过回滚会让它悬置到连接归还，继续占锁与 undo 日志。
  裁决改为沿 InnerException 链查找 `OperationCanceledException`/`TimeoutException`/
  `IOException`/`SocketException`，命中即照常尝试回滚。判据保守偏向回滚：多回滚一次的代价
  是一次失败往返挂 Data，少回滚的代价是服务端事务悬置。
  配套修正 `MultiValueBulkInsert`：原先把「批执行失败」与「提交失败」混在一起判定，会把
  执行失败误判为「服务端已终止事务」而跳过回滚——新增 `commitAttempted` 标志区分。
- **自管事务的 COMMIT 纳入 commandTimeout 超时窗口（T2/R2）**：PG COPY / MySQL LOAD DATA
  每批已有 per-batch 超时，但整批收尾的 COMMIT 此前只受调用方 ct 约束，`ct == default` 时
  网络黑洞可让提交永久挂起。新增两 Provider 的 `CommitWithTimeoutAsync`，超时包装为带
  `PalORM.InfrastructureTimeout` 标记的 `TimeoutException`（与 COPY 路径同口径）。
- **回滚改为有界取消（T3/R3）**：原用 `CancellationToken.None` 无界等待。ITM-747 的论证
  （「释放必须尽力完成」）对本地句柄 Dispose 成立，但 Rollback 是网络往返且发生在异常传播
  路径的 finally 里。改为按会话 CommandTimeout 设有界取消，超时后把 `TimeoutException` 挂
  主异常 Data。`DataSession` 内两处绕过门禁的直连回滚一并收敛。
- **熔断半开探针槽位兜底回收（R5）**：`_halfOpenProbeActive` 原先只由
  `RecordSuccess`/`RecordFinalFailure` 释放，调用方在两次记录之间崩溃/取消且未调
  `ReleaseCancelledProbe` 时槽位永久泄漏，熔断器永久 Open。现按 5 分钟下限回收（**不用
  `resetAfter`**——`resetAfter=Zero` 是合法配置，以它为界会让探针一获取就算过期，破坏半开
  单探针不变式）。
- **`ReleaseCancelledProbe` 带 generation（C2）**：原实现不带 generation，序列「探针 P 进入
  (gen N) → 探针失败重开(gen N+1) → P 的调用方取消」会把 `_openUntil` 改写为「现在」，
  新窗口瞬间到期，实际冷却期被旧探针的取消单方面抹掉。
- **批量家族单次注册表快照（R8）**：`BulkUpdateAsync`/`BulkUpdateBatchAsync`/`BulkMergeAsync`
  入口原先多次独立读 `CurrentState`（每次一次 `Volatile.Read`），Register/热重载窗口内可能
  跨版本混用元数据。与 Insert/Update/Delete 的 r19/ITM-703 单快照纪律对齐。
- **`BatchUpdateSqlBuilder` 入口守卫补齐（R9/R10）**：空 SET 集会生成
  `UPDATE t SET  FROM …`，`rowCount=0` 会生成 `FROM (VALUES )` / `IN ()`；租户参数名未经
  校验即拼进 SQL。三者现都在入口显式拒绝。
- **`BulkInsertAsync` 元数据守卫复用单一实现点（R13）**：会话层自写的两键检查改为调用
  `BulkOperationFramework.EnsureInsertMetadata`，与三 Provider 同源。
- **SQLite Provider 原生 bundle 初始化改 `[ModuleInitializer]`（C5）**：显式静态构造器使
  类型失去 `beforefieldinit`，CLR 在每次静态成员访问前插初始化检查——这些静态方法在批量
  路径上作为方法组反复传递，每次都白付一次。ModuleInitializer 在程序集加载后、任何类型被
  触达前由运行时保证恰好执行一次，语义与原静态构造器等价。
- **`EntityDataReader.IsDBNull` 直读参数值（M9）**：原经 `GetValue` 取值，驱动对每列可能
  先 `IsDBNull` 再 `GetValue`，同一 ordinal 两次解引用 + 两次 null 合并分支。

### 🧪 测试

- 新增 `BulkUpdateBatchReuseTests`（14 个）：池 ↔ 命令集合收敛契约（满批全挂/末批保留前缀/
  再增长/租户参数末位/尺寸不变 no-op）、`CreateParameterArray` 与 `CreateParameterPool` 命名
  一致性、`Build` 入口守卫、批量路径端到端（方言夹具跑真 SQL，逐行断言最终值）、租户过滤
  只改本租户行。
  方言夹具 `BatchedDialectProvider` 报 MySql 方言而底层连接仍是 SQLite——批量 UPDATE 的
  CASE WHEN 形态是 SQLite 原生支持的语法子集，使该路径首次在本地获得真实执行覆盖
  （此前只有字符串层锁定）。
- 新增 `TransactionCleanupTests`（14 个）：裁决的异常形态分类（服务端错误跳过 /
  传输与取消回滚 / 驱动异常内层含 IO 回滚 / SQLite 恒回滚）、有界回滚（超时转 Data 而非
  挂起 / 正常完成无记录 / 失败保留 / Zero 透传无界）。
- 新增 `ProviderHotPathTests`（11 个）：三方言 `QuoteIdentifier` 的正确性与控制字符守卫、
  SQLite Provider 无显式静态构造器（C5 的反射锁定）、`EntityDataReader.IsDBNull` 与
  `GetValue` 判空结论一致（含主键补位列恒空）。
- 变异探针：把 `AttachParameters` 临时改为恒挂满批，`ShrunkBatch` 与 `WithTenant` 两个用例
  如期失败，确认新测试不是空转。

### [5.6.0 ·性能轮] — 读路由会话级复用 · 查询构建分配减半 · SQL 零漂移 · 测试凭据自动加载

> 变更范围：v5.5.1 后 8 个提交，src/PalORM.Core 七个文件 + src/PalORM.Sqlite +
> src/PalORM.SourceGen + src/PalORM.Testing + 四个测试项目
> 验证：`PalORM.ci.slnf` 0 警告 0 错误 · Core 260/260 · SourceGen 190/190（快照语义等价已机器校验）·
> Integration 180/180（含 PG/MySQL 真库，无需手动 source 凭据）· 三个 AOT 程序
> `publish -p:PublishAot=true` 后原生运行 PASSED · SQL 转储 22 场景与基线提交 b2e5741 逐字节一致

### ⚡ 性能（实测数据来自 .ai/perf-probe，分配字节数复现性优于 1%）

- **读路由连接改为会话级复用**（行为变更）：原每次读查询都 `CreateConnection` +
  `Open` + Provider 初始化（SQLite 为 7 条 PRAGMA），实测单查询 **33.75µs → 8.98µs
  （−73%）**、分配 5815B → 2944B。读连接由 `DataSession` 持有并在会话释放时关闭；
  连接失效时丢弃重建，Provider 初始化随新物理句柄补设。
  `ConnectionLease` 随之简化为纯借用语义（移除 `OpenOwnedAsync`）。
- **查询构建分配**：`From<T>()` 构建器基线 **440B → 64B**；`ToSql()` **984B → 392B**；
  `WhereIn(500)` **89800B → 71456B**、`WhereIn(2000)` **432393B → 325537B**。
  手段：`QueryBuilderServices`/`QueryBuilderContext` 改 `readonly record struct`；
  `AddClause` 写时复制按 `Count+4` 预留容量；SELECT 列清单按 (Type, Dialect) 缓存；
  IN 列表改单 `ValueStringBuilder` 拼接；会话侧三处 `ConcurrentDictionary.GetOrAdd`
  捕获闭包改 `TryGetValue`，默认过滤三形态一次缓存。
- **PG Binary COPY 参数复用（T19，真库实测）**：COPY 行循环原先每行 `Parameters.Clear()` +
  `binder` 重建 `columnCount` 个参数（1 万行 × 4 列 = 4 万个 `NpgsqlParameter`）。现改为每批
  建一次、逐行只写 Value，取值走生成器已产出的 `BindInsertValues`（与 `MultiValueBulkInsert`
  的 v4.6 池同一机制，<b>无需改生成器</b>；旧模型程序集该绑定器为 null 时自动回退逐行路径）。
  实测（10K 行，4 列实体）：**882.3 → 211.1 B/行（−76%）**，时间 77.7 → 70.0 ms；PG 由
  「比 MySQL 多值 INSERT 贵 2.4 倍」（882 vs 361）变为「便宜 1.7 倍」。
  收益随列宽放大——19 列实体（18 个插入列）：**4844.7 → 816.9 B/行（−83%）**，
  即原始分析所引「1 万行 × 20 列 = 20 万参数对象」的规模上，单次批量插入少分配约 40 MB。
  参数对象仍留在命令集合内——`WriteRowAsync` 读 `NpgsqlParameter.NpgsqlDbType`，
  脱离集合会丢类型推断。
  <br>新增 `PG_BinaryCopy_NullFirstThenValue_KeepsTypeInference`：参数类型由首个绑定行推断，
  现有用例的行序是「先全非空、后全 null」，覆盖不到反向顺序，该用例把顺序倒过来钉住该风险。
  AOT 全链路复验：`dotnet publish -r win-x64 -p:PublishAot=true` 后原生运行
  `PalORM AOT PG verification PASSED`（该程序含 BulkInsert COPY 路径）。
- **批量 UPDATE 取值走零分配绑定器（T15-v2，生成器新增 `BindUpdateValues`）**：
  单语句批量 UPDATE 的目标参数池本身没问题，真正的削减点在 probe 命令**逐行 `BindUpdate`
  建参数**（40000 行 × 4 列 = 16 万次创建）。生成器现按 `BindInsertValues` 同一形态发射
  `BindUpdateValues(DbParameter[], entity, int paramOffset)`——只写 Value、零 `CreateParameter`，
  列序与 `BindUpdate` 逐位一致（SET 列 → 主键 → 并发令牌）。
  `CrudBindings`/`CrudMetadata` 新增可选字段（追加参数，不破既有调用点；旧模型程序集为 null 时
  `ExecuteBatchUpdateAsync` 回退 probe 路径）。
  <br>真库实测（数据准备置于计时区外，两侧同规模）：
  PG 单批 2671.6 → **1512.7 B/行**（−43%），多批 2750.0 → **1609.2 B/行**（−41%）；
  MySQL 单批 2291.7 → **1810.0 B/行**（−21%），多批 2457.0 → **2006.4 B/行**（−18%）；
  正确性两方言 `BulkUpdateBatchAsync` 均返回全行数且值校验通过。S3 反向验证一致。
  <br>注：上一轮（T15）只把参数创建挪进池、取值仍逐行建参数，创建总量不变，真库 A/B 判定为
  净负收益并已回退——零分配绑定器到位后池才有意义，两条合并才是完整的优化。
- **MySQL BulkCopy 参数复用（T20，真库实测）**：`MySqlBulkCopyInserter` 行循环原先每行
  `Parameters.Clear()` + `Binder` 重建 `columnCount` 个 `MySqlParameter`。现改为每批建一次、
  逐行只写 Value，取值走生成器已产出的 `BindInsertValues`（零 `CreateParameter`，
  与 `MultiValueBulkInsert` 的 v4.6 池同机制；旧模型程序集回退逐行 `Binder`）。
  <br>真库实测（10K 行）：4 列实体 **671.7 → 294.1 B/行（−56%）**；
  19 列实体 **3100.3 → 940.3 B/行（−70%）**，时间 161 → 141 ms。
  <br><b>该改动翻转了两条路径的优劣</b>：改前 BulkCopy 分配比多值 INSERT 回退路径更差
  （4 列 671.7 vs 359.1 = 贵 87%；19 列 3100.3 vs 2307.0 = 贵 34%），改后反超
  （4 列 294.1 vs 359.1 = 便宜 18%；19 列 940.3 vs 2307.0 = 便宜 59%）。O25 的
  「能力检测替代行数阈值」判据因此成立——原先差的是实现，不是判据。
  正确性：`MySql_BulkInsert_LocalInfileOn_InsertsAllRows` 通过（其 `name='row-9'` → 9
  的跨列断言可捕获池下标错位导致的静默串列）。S3 反向验证一致。AOT 复验 PASSED。
- **MySQL `local_infile` 配置补记**：BulkCopy 路径以 `local_infile=ON` 为唯一前置，而
  MySQL 8 默认 OFF——`bench/PalORM.Benchmarks/docker-compose.yml` 的 mysql 服务原先未开，
  任何人按该文件起库都只会走多值 INSERT 回退，BulkCopy 与 PG COPY 都测不到。已补
  `command: ["--local-infile=1"]`。<b>注意</b>：本次在真库上是用 `SET GLOBAL local_infile=ON`
  于运行时开启，<b>不随 MySQL 重启保留</b>；要持久化需在服务端 `my.cnf` 写 `local-infile=1`
  并重启（该实例不在本仓库管辖范围）。
- **AOT 三 Provider 首次全部本地验证通过**：生成器改动影响全部实体的生成代码，据此重跑
  `dotnet publish -r win-x64 -p:PublishAot=true` + 原生运行——SQLite / PostgreSQL / MySQL
  三个 AotTest 应用均 `PASSED`（此前 PG/MySQL 一直标注「CI 待验证」，本次借真库可用补齐）。
- **10K 行物化保持在地板**：相对 ADO.NET 地板 +0.05% 分配（未改动该路径）。

### 📝 行为变更

- 读连接生命周期：由「每查询创建并释放」改为「每会话懒创建并复用」。影响面为
  配置了 `ReadConnectionString` 的会话——读副本连接在会话存续期内保持打开。
- `ReadSessionSetupSql` 的执行时机从"每次读连接建立"收敛为"每会话首次建立读连接"。
- GridReader 释放不再级联释放被借用的连接（连接归会话所有）。
- **SQLite 上的池参数由「抛 `NotSupportedException`」改为「忽略」**（缺陷修复）：
  原实现 `SqliteProvider.CreateConnection` 见 `DbOptions.PoolExplicitlyConfigured` 即抛，
  而该标记由 `WithPool(...)` 与 `PALORM_MAX_POOL_SIZE` 环境变量置位，`DbOptions.Production(...)`
  预设内部就调用 `WithPool`——于是**任何走生产预设或环境变量的 SQLite 部署都在会话构造期
  必失败**，等于装不起来。（ITM-315 把「与默认值比对」改成显式标记位是对的判断，
  错在把"无意义的配置"升级成了"不可用的部署"。）
  忽略的依据：SQLite 是进程内嵌入式库，没有服务端连接池可承接这三个旋钮——驱动自带的池
  只有 `Pooling=on/off` 一个开关，无法表达最大连接数/空闲寿命/存活期。
  `MaxPoolSize` / `PoolIdleTimeoutSeconds` / `PoolLifetimeMinutes` 对 PG / MySQL 照常生效。
  非静默：Provider 方法注释与 README 配置表均标注"SQLite 忽略"。

### 📝 文档

- **表达式树可提到静态字段**（README + `docs/API参考.md`）：表达式树在调用点构造，
  库无法替调用方缓存，内联 lambda 在每查询都会重建树与闭包。in-situ 实测（同一查询，
  唯一差异是 lambda 内联还是来自 `static readonly` 字段）：`Set(expr, value)` −18.0% 分配 /
  −18.3% 耗时，单行查询 `+OrderBy` −12.4% / −7.2%，`WhereIn(500)` −0.72%，
  万行结果可忽略——即"表达式树占比随查询本身变重而摊薄"。给出
  `private static readonly Expression<Func<Order, DateTime>> ByCreatedAt = o => o.CreatedAt;` 模式。
- **弹性管线的每查询开销更正**（源码注释 + README）：`QueryBuilderExtensions` 原注释称
  "默认直通路径零开销"，两处都不成立——默认配置（`MaxRetries=3` / 熔断阈值 5）**不是**直通，
  只读查询默认就走执行器。实测为**常数 ≈272 B/查询，与结果行数无关**（单行查询 +8% 分配，
  千行 +0.2%）。三项独立测量之和与 in-situ A/B 之差逐字节吻合（168 + 56 + 48 = 272，
  实测 272~278）：超时 `CTS` + `CancelAfter` 定时器 ≈168 B（每次尝试一份，这是
  `CommandTimeout` 语义本身，覆盖连接获取与读取器迭代，非驱动侧 `CommandTimeout` 的子集）、
  调用点把单次尝试内核转成委托 56 B、执行器机械（熔断进出 + 异步状态机）≈48 B。
  <br>可剥的只有后两项（104 B），须把只读内核从 async 局部函数改成 struct 内核 + 泛型约束
  （顺带消掉两条分支共有的 ≈250 B display class）；实测耗时无差异，故记录而不实施。
  <br>另一项实测更正：`CancellationTokenSource.CreateLinkedTokenSource(ct)` 换 `new CTS()`
  在 `ct` 不可取消时**零收益**（两者同为 168 B）——联动源对不可取消的父令牌不注册，
  尺寸等价。原以为的优化路径经测量为死路，未改代码。
  <br>同时钉住直通配置的语义代价：`MaxRetries=0` + `CircuitBreakerThreshold=0` 下读路径
  既不建 CTS 也不包装超时，慢命令抛驱动自身异常，而非带 `PalORM.InfrastructureTimeout`
  标记的 `TimeoutException`（驱动的 `CommandTimeout` 仍然生效）。

### 🔁 基线重录（v5.6.0）+ LOAD DATA 方差关闭

- **重录性能基线**（规范 §5 流程：影响基线的改动合入后重录守住新水位）：
  旧基线（5.6.0）已落后于往返优化轮后的水平——Insert 6,000→5,136 B（−14.4%）、
  Update −10%、Stable/VaryingShape −9.7%/−9.3%；回归到旧水平将无法被 +20% 阈值捕获。
  新基线 27 项 + 6 项对照比值，自检 27/27 通过；阈值维持 20/10/30。
  新基线水位：Insert alloc_ratio **3.711**（旧 4.335）、time_ratio **1.188**（旧 1.463）。
- **MySQL LOAD DATA 分配方差关闭**（登记项）：健康服务器连续两测 **70.1 B/行稳定**
  （19 列 501.2 也稳定）；此前 105.9 B/行为降级期采样。结论：该路径分配对服务器
  流控状态敏感（LOAD DATA 缓冲随包大小分配），跨状态对比需注明服务器健康度。
  PG 侧 211.1 B/行与旧基线逐位一致（零回归确认）。

### ⚡ 性能（L4：MigrateAsync 批量化——N 表 N 次往返 → 1 次）+ 测试基建修复（外库 DDL 全量串行）

- **MigrateAsync 两阶段重构**：先全集校验（表/索引方言 DDL 键齐全）再执行——原实现
  边校验边执行，type B 缺键在 type A 的 DDL 已执行后才抛，留半成品 schema；现在缺键时
  零副作用。建表 DDL 经 `SessionBatch` 单次往返（PG 真 DbBatch / MySQL 驱动侧批处理 /
  SQLite 顺序回退）；索引 DDL 保持逐条（MySQL 1061 幂等跳过是逐条 catch 语义）。
  配套：`SessionBatch.AppendRaw`（internal，DDL 运行时字符串不能走 FormattableString
  插值洞）、owner 重入的 `ExecuteNonQueryAsync` 内部重载（迁移持租约内执行批量）。
  边界核实：`System.Data.Common.DbBatch` 无 CommandTimeout 面——批路径超时由驱动默认
  决定；慢 DDL（大表 CREATE INDEX）不在批内。
- **测试基建修复（根因实证）**：外库集成测试存在**同库并发 DDL 竞态**——
  `information_schema.PROCESSLIST` 快照实证多连接同时执行 DROP/CREATE。失败机制：
  测试 A 的 DROP 落地后、CREATE 发出前，并发测试 B 的 MigrateAsync（注册表含全部实体）
  以 `CREATE TABLE IF NOT EXISTS` 重建刚被 DROP 的表 → A 的 CREATE 撞 1050；
  PG 侧并发迁移的建表突发在 pg_type 系统目录互撞 23505。此前仅部分测试归
  `ExtBulkTable` 组（靠时序运气掩盖，基线即有 ~1/9 偶发）。现在**全部 10 个触外库
  测试类类级归组**串行。L4 批量化使迁移 DDL 更密集、撞窗概率放大，是本轮实证定位
  的触发器而非根因。
  验证：Integration 连续 8 轮默认并行度全绿（203/203）· Core 303/303 ·
  SourceGen 197/197 · AOT 原生运行 PASSED。

### 🔭 可观测性（R3：ExecuteAsync 接入拦截器三段式——覆盖面缺口补齐）

- **`ExecuteAsync`（原始 DDL/DML）拦截器接入**：此前仅实体 SELECT 管线与 QueryBuilder
  UPDATE 经过拦截器（ITM-513/547 的文档化边界），经 `ExecuteAsync` 执行的建表/数据变更
  对审计拦截器完全不可见——是审计场景的实际盲点。现在 OnBefore（SQL 全文 + 绑定参数表）/
  OnAfter（受影响行数 + 耗时）/OnError（原始异常透传，拦截器自身异常吞掉计数）与
  ToListAsync 完全同语义。参数表与计时**仅在拦截器非空时物化**——默认会话零开销
  （与 SELECT 管线"空列表跳过"同口径）。`IQueryInterceptor`/README 覆盖面文档同步；
  PipelineParityContractTests 序列断言纳入种子的 DDL 事件。
- 测试：ExecuteAsyncInterceptorTests +3——三段式与参数表可见性、失败路径 OnError
  且原始异常同一性、无拦截器会话行为不变。
  验证：Core 303/303 · Integration 203/203 · AOT 原生运行 PASSED。

### ⚡ 性能（M1：租户过滤写路径 SQL 缓存——四处缓存外重建收口）

- **写路径租户片段缓存**：SELECT 家族三形态 v5.6 已缓存（`FilterFormsCache`），但四个
  写路径调用点仍在缓存外逐次重建——软删 `DeleteAsync` 每次调用 **5 次 QuoteIdentifier +
  全句插值**重建 UPDATE 语句；`UpdateCoreAsync`/物理 `DeleteAsync` 每次拼接
  `sqls.Update/Delete + 租户后缀`；`BulkDeleteAsync` 每次重建后缀。现在：
  软删全句 per-(Type, Dialect, hasTenant) 缓存、Update/Delete 带租户形态 per-(Type, Dialect)
  缓存、租户后缀 per-Dialect 缓存（BulkDelete 语句随批次占位符变化，仅后缀可缓存）。
  稳态命中路径为一次字典查找，构建期成本不变；键含 Dialect（跨方言缓存污染教训在案）。
- 测试：`TenantWritePaths_IsolateAcrossTenants_AfterSqlCaching`——Update/软删/BulkDelete
  三入口的租户隔离（本租户 1 行、跨租户 0 行、幂等 0）+ 二次调用（缓存命中）结果一致。
  验证：Core 300/300 · Integration 203/203 · AOT 原生运行 PASSED。

### 🔒 可靠性（T1 后半：Commit 失败后跳过已终结事务的回滚）

- **提交失败 → 跳过徒劳回滚**（`WithTransaction` / Bulk 内核 `RunInTransactionScopeAsync` /
  `ToPageAsync` 三个提交点）：提交尝试标志（置于 CommitAsync 紧前）区分「提交失败」与
  「回调失败」——PG/MySQL 的失败 COMMIT 由服务端终止事务（PG 文档：COMMIT 出错即回滚），
  此前仍发起 RollbackAsync，得到 "transaction already completed" 驱动噪音挂进
  `PalORM.RollbackException` 并多一次徒劳往返。现在跳过回滚、以 `PalORM.RollbackSkipped`
  标记留痕（跳过的回滚 ≠ 失败的回滚，诊断不再误指向回滚）。**SQLite 例外**：失败的
  COMMIT（如 SQLITE_BUSY）保留活动事务，必须回滚释放写锁——方言守卫排除。
  MultiValueBulkInsert 的提交点无方言管道（上下文只有 QuoteIdentifier 委托），暂不接入
  ——其回滚失败本就以 Data 挂主异常不掩盖，噪音代价可接受。
- 测试：CommitFailureDialectTests +1——PG `DEFERRABLE INITIALLY DEFERRED` 唯一约束
  确定性触发 COMMIT 失败（唯一可靠手段），锁定 RollbackSkipped 在场、RollbackException
  缺席、事务终结后连接可用三契约。
  验证：Core 299/299 · Integration 203/203 · AOT 原生运行 PASSED。

### 🔒 可靠性（R4+T1 前半：保存点名校验 + 已释放事务跨驱动一致处理）

- **保存点名校验（R4）**：`SavepointAsync`/`RollbackToAsync` 统一拒绝空名/空白名/含 NUL
  的名字（`ArgumentException` 指向参数）——这些形态经 QuoteIdentifier 转义后跨方言行为
  发散（PG 接受空引用标识符、MySQL 拒绝；NUL 截断命令文本），库内提前拒绝。
- **`IsTransactionAlive` 探测（T1 家族，6 处）**：Npgsql 在已释放事务上取 `Connection`
  抛 `ObjectDisposedException`（Microsoft.Data.Sqlite 返回 null 不抛）——此前
  `GetActiveTransaction`（内部残留的「静默清理」）/`UseTransaction` 双入口（「已释放」
  精确报错）/`RestoreTransaction`（还原空值）/会话 `DisposeAsync`（跳过「先完成事务」
  警告）/保存点双方法的已释放检查，在 PG 路径全被驱动异常抢先崩溃，设计语义
  （静默清理/响亮失败）从未完整触发。统一探测后已释放事务跨驱动一致视同
  `Connection=null`。**由 PG 保存点集成测试首次暴露**（Sqlite 宽松性掩盖了全族）。
- 测试：SavepointDialectTests 新增 3 例——PG/MySQL 真库锁定方言引号字符嵌入名的
  SAVEPOINT/ROLLBACK TO 转义往返（两侧转义不一致即「保存点不存在」失败）、
  非法名三形态双方法拒绝。
  验证：Core 299/299 · Integration 202/202 · AOT publish + 原生运行 PASSED。

### ⚡ 性能（C4：池下限透传 + PreWarmAsync——消除修剪清池与冷启动的建连尖峰）

- **`DbOptions.MinPoolSize`（新增，默认 0 = 不覆盖驱动默认）**：正数透传
  （PG `MinPoolSize` / MySQL `MinimumPoolSize`，`WithPool(maxSize, ..., minSize:)` 或
  init 直设均可；`Validate` 拒绝负数与 > MaxPoolSize）。语义按驱动官方文档为
  **空闲修剪保留下限**——空闲超期时池内至少保留这么多条，消除「稀疏流量 + 空闲修剪
  清池 → 突发查询重建物理连接」的延迟尖峰（远程建连实测 ~8.5 ms/条；v5.6 默认不再
  覆盖空闲超时后，显式配置空闲超时的部署仍会踩到修剪，本参数是配套的保留下限）。
  SQLite 无池忽略（与既有池参数契约一致）。
- **`DataSession<TProvider>.PreWarmAsync(options, count)`（新增）**：启动期逐条打开
  count 条连接随即归还池——首批突发查询命中暖连接。过 Provider 初始化钩子（与
  `CreateAsync` 同口径）；SQLite 直接返回；建连异常原样抛（预热是优化，容错由调用方
  决定）。与 `MinPoolSize` 正交：前者灌暖、后者防修剪清空，配合使用才保持暖态。
- 测试：Core.Tests +4（WithPool/Validate 双入口校验、两 Provider 透传与
  「仅默认时覆盖」、SQLite 无操作契约）；架构登记 `PreWarmAsync`（不触表豁免）。
  验证：`PalORM.ci.slnf` 0 警告 0 错误 · Core 299/299 · Integration 199/199 ·
  AOT publish + 原生运行 PASSED。

### ⚡ 性能（L1：BulkUpdateAsync 自动路由批量——满足条件时 N 行 N 次 RTT → 分批单语句）

- **`BulkUpdateAsync` 智能路由**：满足全部保守条件时自动走单语句批量路径
  （`PrepareBatchUpdateContext` + 分批 `ExecuteBatchUpdateAsync`，与 `BulkUpdateBatchAsync`
  同核心），**N 行 N 次往返 → 分批单语句**（与 BulkMerge 集合化同构收益）。
  条件（任一不满足即保持逐条）：方言非 SQLite（CASE WHEN 实测慢 6.4×）、实体数 >1、
  无 `[ConcurrencyCheck]`（批量无法表达每行 version 匹配）、无软删、无租户过滤。
  逐条路径的乐观锁/软删/租户语义**完全保留**——不满足条件的用户零感知。
  <br>用户不再需要知道"有 BulkUpdateBatchAsync 这个更快的选项"——默认路径在安全时自动加速。

### 🔒 可靠性（R1+L2+T5：写路径超时包装——异常统一 + 事务上界）

- **新增 `ResilienceExecutor.ExecuteWithTimeoutAsync`**：仅超时包装、不重试不熔断——
  非幂等写路径（ITM-310 契约禁止重试）此前<b>连超时包装都没有</b>，命令超时抛驱动原始异常，
  调用方无法用统一模式匹配。本方法提供与读路径一致的 `TimeoutException` +
  `Data["PalORM.InfrastructureTimeout"]=true` 契约。
- **接入三条写路径**（DataSession 新增 `ExecuteWritePipelineAsync`）：
  `UpdateCoreAsync`（单行 UPDATE）、`DeleteAsync`（软删 + 物理删双路径）、
  `ExecuteAsync`（原始 DDL/DML）。事务内直通（事务有自己的上界语义）；
  直通配置直通（零开销契约保持）。
  <br>事务内语句挂起时，整个事务的持锁时间由本超时限定上界（长事务风险消除）。
- **事务可靠性专项审查结论**（源码级核实，修正初始分析中的两处误判）：
  - T3「BulkInsert 无事务自建」——**伪缺陷**：MySQL Provider 层（MySqlProvider:222-227）
    已有正确的自建事务+commit+rollback，Inserter 层不加事务是正确的单一权责设计；
    PG COPY / SQLite MultiValueBulkInsert 同样正确。三方言 `ownsTransaction` 全在。
  - T2「外部事务不自动 Rollback」——**伪缺陷**：`WithTransaction` 总是自建事务
    （BeginTransactionAsync + 嵌套守卫拒绝外部），`RunInTransactionScopeAsync` 已有
    `ownsTransaction` 守卫——复用外部时不 Commit 不 Rollback。
  - 修正的教训：初始审查只看了 Inserter 层就断定缺陷——没有沿调用链上溯到 Provider 层。
    以后审查事务语义必须从 DataSession → Provider → Inserter 全链走通。

### 🧪 维度 10 落地：`--stability` 长稳仪器（规范最后一个 ❌ 测量维度）

- **新增 `--stability <秒> [dialect]`**：持续负载 + 10s 窗口采样（窗口吞吐/p95/累计分配/
  Gen0/1/2/工作集）+ 衰减判定（前 1/3 vs 后 1/3：吞吐跌 >20% 或工作集增 >30%）。
  与 `--workload` 分工：workload 测扩展曲线（每档 3s），stability 测时间维度稳定性。
- **首跑抓到一个信号并用 S2 单变量隔离归因**：首跑显示"吞吐 −20% 衰减"——逐项排查发现
  两个仪器缺陷（p95 恒 0：采样器先 Clear 后取样，顺序反了；UPDATE qty+1 累加：WAL 无限
  增长）。修为幂等 SET + 修正采样顺序后重跑：衰减消失（前/后 1/3 **+7.9%**），工作集
  +17.2% 在阈值内。**结论：衰减信号来自负载自身状态增长，不是 PalORM**——这正是
  "先怀疑量具"纪律的又一次兑现（本轮第三次：B4 单树假象、比值断言 flaky、本次 WAL 混淆）。
- 仪器修正要点已写入代码注释：稳定性负载必须幂等（平稳态），否则测的是负载自身的
  状态增长而非被测系统。
- 规范 §1 维度 10 状态 ❌→✅；§5 流程表补长稳初筛入口。

### 📄 报告增补（2026-09-19 同日）：往返优化轮复测入正式报告

- `docs/性能测试报告-2026-09-19.md` 增补节：①②B1 三项复测数据 + PG 64T 深测
  （35,878 ops/s 仍扩展，p50 上升显示接近拐点）+ 四方向收官状态。
  **② 在标准门禁基准显形：PalORM_Insert 6,000 → 5,136 B（−14.4%），
  vs ADO 分配比 4.335 → 3.711、耗时比 1.206（基线 1.463）**；
  SQLite 负载 92,031 ops/s @1T（会话最好成绩，p99 0.019 ms）。

### ⚡ 性能（B1：SessionBatch——事务内 N 语句压成一次往返，PG 实测 3.4×）

- **新增 `session.CreateBatch()` 显式批量 API**：`Append(FormattableString)` 链式追加
  非查询语句，`ExecuteNonQueryAsync()` 一次执行。方言分派：PG 走 `DbBatch`（Npgsql 真
  **单往返**）、MySQL 走驱动批处理、SQLite 无批量 API **回退顺序执行**（行为等价、
  本地 RTT≈0 无损失，回退对外不可见）。批量自动加入会话活跃事务（原子性随事务回滚）。
  <br>**PoC 实测（PG 远程，事务内 10 条 INSERT，60 轮中位）**：逐条 4.56 ms → 批量
  1.33 ms（**3.4×**，每语句消除 ~324µs RTT）；语句数越多、RTT 越大收益越大。
  <br>语义契约：只收非查询语句（批量内无每语句结果的可寻址位置）；参数化与单条路径
  同纪律（FormattableString 编译期参数化）；返回累计受影响行数。
  6 项用例锁定（SQLite 回退 4 + PG/MySQL 真批 2）：生效+可读回、失败整批回滚、
  空批 no-op、空白语句 Append 期拒绝（ITM-745 同口径）、事务原子性、行数口径。
- **附带实测：① 的集合化交付了一个未声明的事务并发红利**——BulkMerge 1K 行的事务
  持锁时间从 588ms 降到 57ms（**10× 更短的持锁 = 并发写同表时 10× 更少的锁竞争窗口**）。
- **并发扩展实测（A1，线程档扩至 32）**：PG 24,084 ops/s @32T（**14.5×，仍未到顶**）、
  MySQL 16,514（12.2×，但 p95 随线程单调恶化 2.38→6.73ms——排队形状）；
  SQLite 77K@1T 不变（回归通过）。结论：并发瓶颈不在 PalORM（门禁按会话隔离，
  驱动/服务器在前）。**连接池预热（A2）已接入 harness**（每档预开 N 连接+真实查询），
  但首档 p95 双峰仅 2.47→2.30ms——池生长非全部成因，未解部分如实登记。
- **A3（会话churn 7.7KB）裁决：否掉**——负载与真实并发模式均为长命会话，
  构造不在热点；D2 维持原结论（除非出现会话-per-请求的真实负载画像）。

### ⚡ 性能（②：INSERT RETURNING 收窄——满足保守条件的实体只回主键）

- **生成器静态判定 + SQL 收窄 + 标量读取路径**：当实体满足保守条件（恰一个自增主键；
  其余全部列可直接插入且无转换器/OwnedJson/IgnoreOnInsert/Computed/Timestamp——
  即 RETURNING 的整行与插入值**恒等**）时：① `InsertReturning` SQL 从全部列收窄为
  `RETURNING "Id"`；② `InsertWithReturningAsync` 走 `ExecuteScalarAsync` 标量路径，
  返回**调用方实体**（引用相等）+ 回填 ID，省整行 reader 行缓冲与实体重建；
  远程库还省 RETURNING 的网络字节（宽表显著）。
  <br>实测（SQLite 本机，同批 DELETE 重插，2 万次/点）：收窄路径 3 列实体 **1,920 B/次** vs
  整行物化路径 4 列实体 2,784 B/次（**−31%**；对照非严格同形——宽实体多一列，
  差异主体是 reader 行缓冲 + 物化重建）。快照机器校验：4/12 块收窄（有转换器/
  OwnedJson/非自增键的实体正确保持整行），flag 2 true/2 false 与实体数一致。
  <br>契约用例 2 项：收窄路径返回引用相等实体且值真实落库；IgnoreOnInsert 实体保持
  **物化返回 DB 默认值**（'database' 而非调用方的 'client'——DB 默认值以返回实例为真源，
  收窄条件正确排除了此类实体）。
  <br>**过程缺陷（测试当场抓出）**：`CrudMetadata.Copy()`（注册时逐类型快照）重建
  `CrudBindings` 时漏传新 flag → 运行时恒 false，127 个用例瞬间红（SQL 已收窄但走了整行
  reader → ordinal 越界）。修复后全绿——这正是"新增元数据字段必须核对 Copy/快照路径"的
  教训，已随修复落入代码。

### 🌐 测评（③b：负载测试方言化——PG/MySQL 并发首数据）

- **`--workload [sqlite|pg|mysql]`**：负载 harness 方言化（连接串来自
  `PALORM_PG_CONNECTION`/`PALORM_MYSQL_CONNECTION`，引用符按方言分派——`"Id"` 在
  MySQL 是语法错误；远程档关弹性重试避免退避污染 p99；种子经泛型 `SeedAsync` 走各方言
  最优批量路径）。SQLite 档重构后回归验证（88K ops/s @1 线程，与历史一致）。
- **维度 4 跨方言首数据**（详见 BENCHMARKS.md）：PG 1→8 线程 **6.8× 近线性扩展**、
  MySQL **4.5×**——证明此前登记的"8 线程回落"是 SQLite 单写者方言形状而非 ORM 瓶颈。
- ③a 往返计数器评估结论：**完整计数需要连接装饰器级别设计**（会话级计数覆盖不了
  Provider 内部 COPY/批量命令，部分计数比没有更糟——仪器可信性纪律），登记为设计议题
  待专项，不仓促实现。

### ⚡ 性能（①：BulkMergeAsync 集合化——远程库 8~10×，往返维度首个大项）

- **`BulkMergeAsync` 由「N 行 = N 次往返」改为分区集合化**：源码核实原实现是
  `foreach → SaveCoreAsync → 每行一次 ExecuteNonQueryAsync`（包在一个事务里）。
  现按键状态分区——默认键行走逐条 INSERT（保留 ID 回填契约：多值形态无法按行还原
  InsertCoreAsync 的物化/回填）；非默认键行（bulk-merge 主流场景）改为**多行 UPSERT**
  分批（每语句 ≤900 参数）：PG/SQLite `ON CONFLICT (pk) DO UPDATE SET c=excluded.c`，
  MySQL `ON DUPLICATE KEY UPDATE c=VALUES(c)`（与单行 upsert 生成 SQL 同形态）。
  <br>**真库实测（1,000 行）**：MySQL **588 → 57 ms（10.3×）**、分配 3250 → 1448 KB（−55%）；
  PG **571 → 71 ms（8.0×）**、分配 2362 → 1951 KB（−17%）。
  本地 SQLite（往返免费）：分配 −47%（8753 → 4623 KB/5K 行），耗时持平（72→83 ms，
  集合化的 SQL 构建抵消了省下的往返——本地收益有限是预期内，主战场是远程库）。
  <br>语义契约（6 项用例锁定）：既有键更新+新键插入、混合键分区+自增键 ID 回填、
  400 行跨批（>900 参数）、重复执行幂等、返回值=处理行数（不依赖 MySQL
  ON DUPLICATE KEY 的 affectedRows 口径）、[ConcurrencyCheck] 实体保持逐条路径的
  ITM-503 拒绝。
  <br>实现要点：`BindUpsert` 每行从 @p0 起命名且 **MySQL 参数集合在 Add 时校验重名**
  （实测撞出 ArgumentException）——不能直接往批命令逐行追加；改为 scratch 命令绑定 →
  值拷贝进预建参数池（池参数批内连续下标命名，逐行只写 Value）。
  <br>批内重复主键的语义边界（如实登记）：旧行为后行静默覆盖前行（last-wins 巧合，
  非契约）；集合化后 MySQL 保持 last-wins，PG/SQLite 对同语句影响同一行**明确报错**。
- **顺带根治一类 E1 同源 flaky**：多个 PG 集成用例并行调 `MigrateAsync`（全实体建表），
  PG 并发 DDL 在系统目录（pg_type/pg_class）竞态 → 23505 偶发失败。实测 3 轮 1 次
  （PessimisticLockTests 撞 ExtBulk 组用例）。已把两个锁用例编入 `ExtBulkTable` 编组，
  3 轮连绿。

### 📄 性能测试报告（2026-09-19）

- **`docs/性能测试报告-2026-09-19.md`**：优化前后对比总报告——批量路径 −47~84%、
  查询构建 −58~100%、启动 JIT −71%、稀疏负载省 13.2 ms/查询、并发单线程 +15~40%、
  门禁 27/27 零回归；含被否掉方向的防回潮记录与口径说明。

### ⚡ 性能（T5b：子句持久化链表，彻底删除子句 COW）

- **`QueryBuilder` 子句存储由 List+COW 改为持久化链表（cons list）**：每次
  `AddClause` 只分配一个不可变节点（48 B：前驱引用 + 子句 + 计数），零复制。
  struct 副本共享链引用——链不可变，副本隔离天然成立；这正是原 COW 存在的原因
  （QUERY-001：List 原地修改会泄漏到副本），链结构以更小的常量分配满足同一契约。
  只读遍历经 `MaterializeClauses()` 惰性物化为数组（每执行一次、此后复用；
  子句数为缓存有效期哨兵——链增长后长度必然不符而重建）。
  <br>实测（S1 单行查询，直通配置，5 万次/点，S3 双向确认）：
  `ToListAsync` 单行 **2,528 → 2,456（−72 B）**，`FirstOrDefault` **2,752 → 2,352（−14.5%）**；
  GetAsync/Insert/Update 不变 ✓。
  <br>**构建器路径单行查询本轮累计：2,976 → 2,456 B（−17.5%）**。
  CloneForExecution 语义不变（参数深拷贝保留），链重建仅为挂载拷贝参数。

### ⚡ 性能（T5c：BuildSql 输出缓存 + 据此修复跨方言缓存污染缺陷）

- **BuildSql 输出按形状缓存**：BuildSql 输出仅由「子句 Sql 文本序列 + 方言 + SplitQuery +
  Take/Skip 值」决定（LIMIT/OFFSET 的值直接内联进文本——键必须含值）。同一形状的查询
  复用同一 SQL string 实例，命中时跳过整个分段拼接。
  <br>实测（S1 单行查询，直通配置，5 万次/点）：
  `ToListAsync` 单行 **2,816 → 2,528（−288 B，−10.2%）**；
  `FirstOrDefault` **2,752 → 2,424（−11.9%）**；GetAsync/Insert/Update 不变 ✓。
  本轮起点 2,976 → **2,528，T1+T5a+T5c 合计 −15.0%**。
  <br>正确性设计：哈希只用于选桶，命中后**全量核对**——子句序列逐条值相等 +
  字段包（`SqlShapeCache.ShapeFields` 记录结构体：方言/SplitQuery/Take/Skip/表名/CTE 名）值相等；
  任一不等即未命中重建，哈希碰撞不会产出错误 SQL。容量以应用内"不同形状数"为界。
  S3 反向验证：禁用命中 → 2,816/2,752（退化确认），恢复 → 2,528/2,424。
- **T5c 暴露并修复一个此前不可见的真实缺陷：跨方言缓存污染**。形状键若无方言，
  PG 会话与 MySQL 会话的同形状查询（用户手写的 WHERE 文本无引用符差异）会互相复用
  对方方言的 SQL——`PessimisticLockTests`（上一轮新增的 PG/MySQL 同形状锁用例）
  实测捕获：MySQL 收到 PG 双引号 SQL 报语法错误。根因是 SELECT 列清单的引用符随方言
  不同，而它不在子句序列内。修复：`ShapeFields` 加入 `Dialect` 并参与哈希与核对。
  变异探针：禁用文本核对 + 恒定哈希 → 集成套件 5 例失败（错表 SQL 被当场抓出）；
  同时实证核对层的碰撞防护设计有效（恒定哈希单独注入时 184 全过——只损失性能不出错）。

### ⚡ 性能（T5a：删除冗余平铺参数列表，COW 减半）

- **`QueryBuilder` 删除冗余的平铺参数列表（`_parameters` List）**，代之以 `int _parameterCount`：
  每个子句本就自带参数（`QueryClause.Parameters`），扁平视图唯一被读取处
  （`GetParametersForKinds`）本来就从子句遍历——平铺列表是纯粹的重复状态，
  却让每次 `AddClause` 的写时复制要**复制两个列表**（子句表 + 参数表，各带 +4 槽）。
  删除后 COW 减半（每次 AddClause 少 2 次分配），单行查询实测 **−120 B**：
  `ToListAsync` 单行 2,936 → 2,816、`FirstOrDefault` 2,873 → 2,752；
  `GetAsync`/`Insert`/`Update` 不变（不走 AddClause 路径，确认无副作用）。
  S3 反向验证：摘掉改动 → 2,936/2,873（退化回基线），复用 → 2,816/2,752。
  平铺计数语义保留（参数全局编号 @pN 跨子句递增、WhereIn 65535 守卫）。
- 累计：构建器路径单行查询自本轮起点 2,936 B → **2,816 B**，加上 T1 前 2,976 →
  全程 **−160 B（−5.4%）**。与专用路径（GetAsync 1,656 B）的剩余差距
  在 COW 的另一半（子句表）、BuildSql 拼接与执行器包装——下一轮候选，
  仍需先量化再动手。

### ⚡ 性能（T4 流式终结器 ForEachAsync）+ T2 参数池化的实测裁决

- **新增 `ForEachAsync(action, ct)`**——流式消费查询结果，逐行回调**不物化列表**。
  真库 A/B（S1 × 100K）：物化 12.7MB 分配 / 8.5MB 列表存活 → 流式 10.7MB 分配（−16%）/
  **存活 0（调用方不再持有整表）**，耗时持平（54 vs 50 ms）。
  价值在解锁百万行级导出/ETL 而不 OOM——存活内存不随行数增长。
  语义契约与 ToListAsync 对齐：拦截器 OnBefore 按尝试/OnAfter 携带行数/OnError 通知、
  弹性覆盖同口径、**不写 WithCache**（流式没有可缓存的列表，与 First 族截断防护同理）、
  回调异常原样上抛并释放 reader/连接。API 参考表已补行。
- **T2（会话级参数池化）实测裁决：不做**。量化：3 参数组创建总成本 248 B，
  其中装箱 ~56 B 不可省，池化上限 ~190 B/查询（T1 后约占构建器路径 10%、
  全路径 ~6%）；而参数对象跨查询复用要面对 PG/MySQL 的类型推断残留风险
  （NullFirstThenValue 先例证明推断发生在首次绑定）+ 池归还生命周期横跨全部读写路径
  （漏归还只是无收益，错归还即正确性事故）。6% 分配收益换 Provider 正确性风险
  与生命周期复杂度，不成立。若将来 profiler 显示参数创建成为真实热点再重启，
  届时先在 PG/MySQL 真库上复现类型推断行为。

### ⚡ 性能（T1 形状缓存：立项预估被实测修正）

- **构建器 SQL 形状缓存**：`FormattableSqlFormatter` 的格式化输出是
  （Format 文本, 槽位偏移, 参数个数）的**纯函数**——Format 文本是编译期 ldstr 常量
  （同调用点恒同实例），据此以值相等键缓存输出，QueryBuilder 热路径
  （`FormattableSqlFormatter.FormatCached`）命中时复用同一 SQL 文本实例。
  <br>**实测收益 −40 B/查询（2,976 → 2,936，−1.3%），远小于立项预估的 −36%**——
  立项时把 Where+ToSql 的 1,048 B 归因于"格式扫描与输出串"，实测拆解证明格式扫描
  已走 ValueStringBuilder 栈分配、几乎零分配，可省的只有输出串本身。
  **预估−36% 是错的，按实测修正并如实记录。** 保留原因：正收益、零行为变化、
  命中时还省格式扫描时间。formatter 随之重构为纯字符串签名
  `Format(string format, int baseIndex, int argumentCount)`（纯函数性显式化，
  既有 FormattableString 重载委托保留，契约测试不变）。
- **同场测得的真实结论（比 T1 本身更有价值）**：单行查询的两条路径差距
  （构建器 2,936 B vs GetAsync 1,656 B）**不是** SQL 格式化造成的——格式化已近乎零分配。
  剩余差距在：每子句参数对象创建（~83 B/个）、子句写时复制（COW 两列表 ×4 槽）、
  BuildSql 拼接、执行器包装。这些构成下一轮的真实候选，全部需先量化再动手。
- `FirstOrDefaultAsync` 的容量 1 已存在（`Take(1)` → `Min(1, 16384)`），原计划 T3 撤销。

### ⚙️ 测评流程：一键全量 + 报告生成器

- **`scripts/run-full-perf.sh`**：单命令跑完整测评（构建 → 负载×2 → 内存曲线 → 启动量具 →
  BDN 微基准 → 门禁判定），产出 `bench/reports/perf-report-<时间戳>.md`（bench/reports/ 已
  gitignore——报告按需人工登记进 BENCHMARKS.md，不入库）。
- **门禁工具新增 `report` 子命令**：读取 BDN 结果 + 负载/内存 JSON，生成带表格的 markdown
  报告（环境头 + 微基准判定表 + 并发分位数表 + 内存曲线表）。
- **基准项目新增 `--memory` 模式**：大结果集内存曲线（10K/100K）+ 查询构建分配，
  复用 StandardShapes（S1），产出 memory-sqlite.json。
- 流程加固：BDN 步骤前清空结果目录——混入陈旧/截断报告会让门禁解析失败
  （本日实测：被中断的运行留下半截 JSON，check 直接 FATAL）。

### 📐 性能测评体系（规范 + 标准数据 + 并发负载测试）

- **新增规范真源 `docs/性能基准规范.md`**：12 维度矩阵（含逐项归属与状态）、标准数据形状
  S1–S5 的权威定义、行数档位、环境记录要求、测量口径、流程（日常/门禁/优化轮/基线重录）、
  新增基准准入清单、禁止事项。BENCHMARKS.md 自此只登记结果，"怎么测"以本文件为准。
- **新增标准数据形状**（`bench/PalORM.Benchmarks/StandardShapes.cs`，唯一权威定义）：
  S1 Narrow（4 列基线）/ S2 Wide（19 列全类型）/ S3 Sparse（确定性 70% null）/
  S4 Blob（32B/1KB/64KB）/ S5 LongText（2000 字符）。种子完全确定（值由行号派生、无随机），
  任何一次生成的库内容逐位相同；DDL 一律经 `MigrateAsync`，禁止手写建表。
- **补齐维度 3/4/11：并发负载测试**（`--workload`，此前全部结论都是单线程中位数）：
  N 线程 × 80/20 读写混合，每操作采样 → 近邻秩 p50/p95/p99 + ops/s，输出 schema 2 草案 JSON。
  首跑（S1 × 10K，SQLite WAL，Windows/.NET 11）：吞吐峰值在 2 线程（71,711 ops/s，+31%），
  8 线程回落到单线程一半以下——SQLite 单写者锁竞争的典型形状；
  p50 ≈ 0.01 ms 与读路由优化后的 8.98 µs 独立互证。**首跑数据只登记不设阈值**
  （分位数噪声底待积累，规范 §6）。
- `scripts/run-benchmarks.sh` 新增 `workload` 目标。

### 🧪 覆盖审计：会话级弹性配置器与 TagWithCaller（此前零覆盖）

- **系统审计"文档声明的 API 是否被测试执行过"**（ForUpdate 空白的同类排查）：39 个文档声明
  的调用式 API 里，`SingleAsync`/`SingleOrDefaultAsync` 经核实有覆盖（首轮启发式误报）；
  真缺口是 4 个**从未在任何测试中出现**的 API：会话级 `WithRetry` / `WithCircuitBreaker` /
  `WithTimeout`（v5.4 弹性特性的**运行期入口**——既有弹性测试全部走 `DbOptions` 构造期配置，
  若热替换 `UpdateResilience` 回归，既有测试全绿而生产配置路径失效）与 `TagWithCaller`。
  新增 `SessionResilienceConfiguratorTests`（4 项）补上。
- **实测钉住会话弹性方法的真实语义——是"叠加"而非"清空"**：`UpdateResilience` 把合并后的
  配置写回 `_options`（DataSession.cs:352），所以 `WithRetry(3)` 之后再 `WithTimeout(...)`，
  重试**仍然生效**。三个方法的 XML 文档写的是"重置当前弹性策略状态"——这个措辞有歧义，
  我第一版测试就按"重置=清空"断言、被当场否掉；文档已改写为准确表述
  （"以合并后的当前配置重建执行器；既有 builder 因快照语义不受影响"）。
  <br>另钉住 `resetAfter` 语义：`TimeSpan.Zero` 意味着开闸即半开、探针永远放行，
  看不到 `CircuitBreakerOpenException`（用例注释里留了这条）。
- 假连接（`FlakyConnection`）补了一个 `LastCreatedCommand` 捕获点，
  使 `WithTimeout` 的秒数能被断言"真的到达命令对象"。

### 🧪 AOT：PG/MySQL 原生程序补锁子句的"原生执行"验证

- 两个 AOT 验收程序（`PalORM.AotTest.Pg` / `PalORM.AotTest.MySql`）新增
  `VerifyPessimisticLocksAsync`：事务内**真执行** `FOR UPDATE` / `FOR SHARE` /
  `SKIP LOCKED` 并取回行。此前锁子句的原生验证是空的——SQLite 执行不了锁语句（ITM-639），
  SQLite AOT 程序只能验形态；而 PG/MySQL 这两个真正支持锁的方言，其 AOT 程序从未执行过锁。
  <br>**验证**：两程序 Release 构建与 `publish -p:PublishAot=true` 均 0 警告 0 错误，
  原生运行双双 PASSED（新增锁路径在原生二进制里真执行通过）。
  <br>PG 侧顺带钉住一个真实方言细节：`AotPgEntity.Id` 无 `[Column]` 特性时，PG 的实际列名是
  带引号的大小写敏感 `"Id"`——WHERE 手写小写 `id` 直接撞 42703（首跑即踩）。锁验证因此改用
  显式映射的 `name` 列，并在代码注释里说明了原因；MySQL 列名不区分大小写，同形代码不受影响。

### 🐛 修复（测试）：时间比值断言导致 flaky —— 换成对象同一性判定 + 订正 B4 解读

- **上一轮新增的 `RegistryIncrementalCostTests` 是 flaky 的**（5 轮红 4）：它用同一进程内的
  耗时比值断言（`未变化重跑 < 冷跑 × 0.2`、`改一个实体 < 冷跑 × 0.4`）。首轮调阈值到 0.5/0.6
  **反而更糟**——失败输出给了真相：同一比值在 0.2~0.8 之间跳。
  <br>根因：**TUnit 并行跑套件，墙钟测量全程与被并行执行的其它测试争 CPU**——同 run 内的比值
  也压不住（perf-gate 那套"同 run 比值可消机器差异"的经验在并行测试里不成立）。
  <br>改法：断言换成 **Roslyn 增量缓存的对象同一性**（命中缓存时输出实例被复用），
  完全不受负载影响。5 轮连绿。
- **B4 最终结论：生成器的增量设计正确，无需改动**（此前两版解读都被实测推翻，过程见下）。
  对象同一性实测（120 实体，**一实体一语法树**——真实工程布局）：
  · 输入未变重跑：**361 个输出全部复用同一批实例**（缓存生效的确定证据，11 ms）
  · 改一个实体：**复用 357 / 重发 4**——恰好是该实体的 3 份产物
    （RowFactory/CommandFactory/Migration）+ 1 份聚合注册表
  <br>即值比较器（`TableModel` 全字段 `EquatableArray`，结构等值）与每实体输出节点
  **确实按实体生效**，聚合注册表只重发自己这一份文件。原"改一个实体全量重发"的担忧不成立。
  <br><b>两版错误解读的成因（值得记住的教训）</b>：第一版拿耗时比值（0.2）当粒度证据，
  第二版把量具的**单语法树布局**当成生成器行为——把 120 个实体拼在一个源字符串里，
  改一个实体就是"整棵树变了"，于是复用数掉到 0。测增量必须用真实布局（一实体一文件）；
  改用一树一实体后复用数立刻变成 357/361。用例里留了复现提示以防后人重踩。

### 🧪 门禁新增 D12（文档 API ↔ 源码一致性）+ 补锁子句执行验证

- **新增门禁 D12**（`.ai/scripts/doc-consistency-check.sh`）：`docs/API参考.md` 表格里声明的
  调用式 API（形如 `` `.Name(` ``）必须在源码中存在对应的公开/内部声明。来源是上一轮的真实缺陷——
  `OrderByDescending`/`ThenByDescending` 被文档列为可用 API 而源码从未存在（`git log -S` 确认），
  调用方照文档写会撞上指向 LINQ 扩展的 CS0411。**这类偏差此前没有任何守护**；纯靠"下次有人
  偶然发现"不可接受（本条正是偶然发现的）。当前 39 个声明全部通过。
  变异探针：往表格塞一个虚构方法 → D12 红并点名该方法。
- **补 `ForUpdate`/`ForShare` 的"执行"验证**：此前整个套件里锁子句**只被 DryRun 断言过形态、
  从未真正执行**——"锁子句拼在语句合法位置、目标方言能接受"这件事零覆盖（锁子句错了只在运行时炸，
  而 SQLite 执行不了锁语句，见 ITM-639）。新增 `PessimisticLockTests` 在真 PG/MySQL 上
  **事务内执行** `FOR UPDATE`/`FOR SHARE`/`SKIP LOCKED` 并取回行，另断言锁子句出现在 WHERE 之后
  （位置是拼接顺序的函数，顺序回归会让语句非法而形态断言看不出来）。
  变异探针：把锁子句改成 `FOR UPDATEX` → 两个用例都执行失败（2/2 红），证明它们真在跑真库。
- **B3（两个无内部消费者的平面字典）量化收口**：实测 `BindInsert` + `BindUpdate` 的发射载荷
  占注册文件 **3.8%**（`RowFactories` 1.2%、`BindDelete` 1.5% 作对照，全在活的路径上）。
  3.8% 远小于 B2 的 19.6%，而可选改法要么是破坏性 API 变更、要么给运行时加一条派生路径而
  用户零收益——**关闭该项**，留待下个主版本随其它破坏性项一并处理。

### 🔬 生成器成本收口（B4/B5：两项"待办"以数据关闭）

- **B5（SQL 构建 memoize）实测否掉**：单条 30 列 INSERT 构建 0.44 µs；生成器每模型每方言调
  `BuildInsertSql` 3 次（经由两个 RETURNING 变体）× 3 方言 = 9 次，500 实体合计 **≈2 ms**——
  占实测生成耗时（4.4 s，500 实体 × 30 列）的 **0.05%**。为 0.05% 去把缓存穿进多个 emitter
  的签名不值得，未实施。
- **B4（增量重生成）不再按原假设处理，实测修正了假设**：原判断是"注册表是聚合输出，
  改一个实体必然全量重发"。新增守卫 `RegistryIncrementalCostTests` 用同一 driver 连跑三轮实测
  （120 实体 × 3 列）：
  | 轮次 | 耗时 | 含义 |
  |---|---|---|
  | 冷跑 | 653 ms | 全量 |
  | 输入未变重跑 | **9 ms** | 增量缓存生效（72×） |
  | 改一个实体 | **131 ms** | 冷跑的 **20%** |
  <br>**该解读后来被对象同一性测量否掉**（见下条"订正"）。500 实体规模按此推算单次编辑约数百毫秒——
  可接受，故当时不建议为它拆成按块多文件输出。

### 🧪 AOT 覆盖（表达式构建器此前从未被原生验证）

- **补上 P0 级验证缺口**：三个 AOT 验收程序此前只走 CRUD/Bulk/OwnedJson/软删路径——
  `OrderBy`/`ThenBy`/`OrderByDescending`/`Select`/`GroupBy`/`Having`/`WhereIn`/`WhereNotIn`/
  `Set`/`Include`/`ThenInclude`/`With(CTE)`/`UnsafeWindowOver`/`ForUpdate`/`ForShare`/
  `WithCache`/`AsPrepared`/`Tag` **一次都没被调用过**，即"它们 AOT 兼容"从未被原生运行验证。
  <br>做法：先在 JIT 侧写冒烟（`ExpressionBuilderSmokeTests`，迭代快、失败定位准），
  跑绿后原样搬进 `PalORM.AotTest`，`publish -p:PublishAot=true` 后**原生运行 PASSED**。
  断言口径双轨：可执行的断结果（排序/筛选/CTE 行数、`Set` 影响行数、Include 行数），
  只影响 SQL 的断形态（投影列、GROUP BY/HAVING、锁子句、标签）。
- **修一处文档与 API 不符**：`docs/API参考.md` 一直把 `.OrderByDescending` / `.ThenByDescending`
  列为可用 API，而源码里**从未存在**（`git log -S` 确认）——调用方写出
  `.OrderByDescending(x => x.Id)` 时编译器会去匹配 LINQ 扩展并抛难以归因的 CS0411
  「无法推断类型参数」。已补两个便捷方法（转调 `OrderBy(member, descending: true)`），
  并纳入上述 AOT 冒烟。
- **锁语句按 ITM-639 登记契约验证**：`ForUpdate`/`ForShare` 在 SQLite 上**故意不在构建期拒绝**
  （既定契约允许在 SQLite 上预览面向 PG/MySQL 的锁语句形态），执行才报语法错误。
  冒烟因此只断言 SQL 形态——我第一版直接执行并撞上 `SQLite Error 1: near "FOR"`，
  核实到该登记后按契约改正（这类"看起来是缺陷、实为登记取舍"的行为，改动前必须先查登记）。
- 冒烟用例自身的两处断言错误由用例当场抓出并订正：`Select` 投影被写成"应含未列出的列"、
  CTE 结果行数写成总数而非筛选后行数。

### ⚡ 性能（B2：只发射会被读到的 SQL 载荷）

- **生成器按方言族只发射会被读到的 `CommandSqlSet` 字段**，另一族与本族无关字段置 `""`：
  - `Insert` **全方言无消费者**（运行时一律走 `InsertReturning` 或 `InsertWithLastInsertId`），
    恒为空串。核实方式：原两条 `.Insert` 命中分别是 `_interceptors.Insert(...)` 与
    `columns.Insert`，与 SQL 集无关。
  - PostgreSQL/SQLite 读 `InsertReturning`/`UpsertReturning`；MySQL 读
    `UpsertMySql`/`InsertWithLastInsertId`（分发点是 `TProvider.SupportsReturningClause`）。
    非本族的字段置空——跨族字段本就不可达。
  <br>**实测**：快照（4 实体 × 20 列）注册文件 **48,265 → 38,789 字符（−19.6%）**；
  500 实体 × 30 列规模下约省 1 MB 级元数据字符串（生成物同时更小、编译更快）。
  逐块机器校验：12 个 `CommandSqlSet` 块每族必需字段齐备、恰好清空 3 个无关字段，无过度清空。
- **为什么置空而不删字段**：删除是破坏性 API 变更（旧生成器产物与外部构造点编译失败）。
  置空对既有消费者零破坏，代价是外部读者会读到空串——由 `CommandSqlSet` 的字段文档逐项声明
  "哪族有值"。**下个主版本可连同其它破坏性项（如 B3 的死字典）一并移除，一次迁移。**
- **B2b（SQLite 复用 PG 的 `CommandSqlSet` 实例）实测否掉**：两族七载荷**逐位完全相同**
  （4 实体 × 7 字段全等，已加断言），而 C# 编译器本就把相同字面量在元数据串堆里存一份——
  共享实例只省一次构造调用的 IL（约 50 B/实体），不值得引入局部变量与发射分支。

### 💔 行为变更（池空闲超时默认值）

- **`DbOptions.PoolIdleTimeoutSeconds` 默认值 30 → 0**，`0` 语义为**不覆盖驱动默认值**
  （Npgsql 300 秒 / MySqlConnector 180 秒原样保留）。`WithPool(...)` 的
  `idleTimeoutSeconds` 默认同步改为 0，故 `Production(...)` 预设（只传池大小）也不再改空闲超时。
  <br>**为什么改**：实测被覆盖时的代价是"间隔超过该值之后的首次查询必须重建物理连接"——
  跨网段 `SELECT 1` 池内 **0.300 ms** vs 新建连接 **13.523 ms**，即**多付 13.2 ms（44 倍）**。
  稀疏流量（查询间隔 &gt; 30 秒）会稳定踩到，而现象是"PalORM 慢"或"数据库慢"，
  几乎不可能归因到池参数。省服务端连接的收益（原 30 秒的动机）改为由显式配置获取。
  <br>**迁移**：依赖"30 秒回收空闲连接"的部署显式写
  `WithPool(maxSize, idleTimeoutSeconds: 30, lifetimeMinutes)`；不写即为驱动默认。
  合法值域由"正数"放宽为"非负"（0 合法）。
  <br>**契约测试**：`ProviderConnectionFactories_LeaveDriverIdleTimeoutAtDriverDefault_WhenNotConfigured`
  以"连接串里该键的有无"为观察面（builder 只序列化显式设过的键）——
  默认与"只给池大小"两种形态断言键缺席，显式 17 断言键出现，双向可判。

### ⚡ 性能（MySQL BulkCopy：去掉 DataRow 层）

- **批量插入的取值载体由 DataTable 换成 `EntityDataReader`**（新增 `src/PalORM.MySql/EntityDataReader.cs`）：
  原实现逐行 `table.NewRow()` + 逐列写值，实测每行 372.0/360.0 B（10K 行 × 4 列，两次采样）；
  读取器按序号直读参数池，每行 **57.0 B**（两次采样逐位一致），时间同向更快
  （114.2/112.7 ms → 100.9/100.4 ms）。**分配 −84%、时间 −11%，两条轴都赢**。
  <br>真库经真实 API 复核（同一探针口径）：4 列 **294.1 → 105.9/106.4 B/行（−64%）**；
  19 列 **940.3 → 502.0/526.9 B/行（−46%~−47%）**。
  <br>源码原注释称"MySqlBulkCopy 对 DataTable 路径有专门优化"——**该判断被实测证伪**，
  注释已按实测更正。取值仍走生成器发射的 `BindInsertValues`（零 `CreateParameter`），
  与 v5.6 的参数池是同一条链的两半：池到位后 DataRow 层就是唯一的每行分配主项。
- **保留的既有守卫一条未减**：`ITM-709` Warnings 非空即失败（防静默数据损坏）、`ITM-615`
  显式列名映射、自增 PK 前置补 DBNull、`ITM-710` 分批、`ITM-655` 超时 0=无限、
  `ITM-656` 行数规范化、旧模型程序集的 `Binder` 回退路径（读取器只多一步"把新建参数引用抄进池"）。
- **新增 7 项读取器契约单测**（`EntityDataReaderTests`，不依赖数据库）：逐行绑定与耗尽语义、
  前置主键列恒 DBNull、C# null → DBNull、列名/序号/形状、空范围、未知列名、分块读取不静默返回 0。
  <br>为什么必须有它：服务端 `local_infile=OFF` 时 provider 静默回退多值 INSERT（不经过读取器），
  真库用例照样通过——读取器契约必须有**不依赖服务端能力**的覆盖。为此给 `PalORM.MySql`
  加了 `InternalsVisibleTo("PalORM.Core.Tests")`（与 `PalORM.Core` 同例）。
  <br>**变异探针**：把读取器的列偏移故意改错 → 真库用例 2/5 失败，证明新增覆盖确实经过读取器。
- **新增真库值保真用例**：`MySql_BulkCopy_AllWhitelistedTypes_RoundTripPreservesValues`
  覆盖 `AllTypesEntity` 全部白名单类型（bool/Guid/DateTimeOffset/DateOnly/TimeOnly/float/byte[]/
  可空变体）——既有 BulkCopy 用例只覆盖 string/decimal/DateTime/byte[]。读取器把值统一以
  `object` 交给驱动按运行时类型格式化，格式错误即静默数据损坏，这是本次换路径的主要风险面。
- 顺带修探针自身缺陷：宽表 DDL 对 PG 用了 MySQL 的 `AUTO_INCREMENT`（历史遗留），
  使 PG 侧的宽实体对照此前直接抛 42601——现按方言分支。

### 🧩 生成器（B1：注册表分块）

- **注册表生成由单个巨型方法改为按 IL 预算分块**：原先全部注册代码落在单个
  `[ModuleInitializer] Initialize()` 里，IL 随实体数线性增长——实测 **500 实体（4 列）
  即单方法 611,449 B IL**。现在按**估算 IL**（每实体固定 ≈900 B + 每列 ≈60 B，由 4 列/30 列
  两个实测点反解）切成 `AddChunk{N}` 方法，每块写同一个可变 `RegistryDraft`，最后仍是一次
  `PalORM_Runtime.Register`。
  <br>为什么不是"每块各自调 Register"：`Register` 每次都复制并重建累计状态，逐块调用会把
  17 个字典反复拷贝，N 块累计 O(N²/块) 次插入。分块只切**构建**。
  <br>为什么按估算 IL 而不是固定实体数：单实体 IL 随列数增长，固定"每块 25 个实体"在宽表上
  会重新撑破方法体（30 列实体单块可达 143 KB）。
  <br>实测（`RegistryScaleTests`，3 样本/侧，500 实体 × 4 列）：单方法 IL **611,449 → 21,632 B**；
  注册初始化器**全部方法**的 JIT 编译耗时中位数 **1299.2 → 376.8 ms（−71%）**——
  超大方法的 JIT 代价是超线性的，分块不只是把风险摊开。切块是列宽感知的：4 列实体每块 18 个、
  30 列实体每块 7 个，单方法 IL 稳定在 ≈21.5 KB。
  <br>新增规模守卫 `RegistryScaleTests`：两维（实体数 × 列宽）断言"无生成方法超过 64 KB IL"。
  变异探针实测其有区分力——把块预算调到 10 MB（退回单体）后两个用例都失败（123,842 / 143,522 B）。
- **`docs/API参考.md` 的注册字典计数订正**：原称"16 个注册字典"且列表漏 `SensitiveColumnMasks`
  ——`RegistryFragment` 实为 17 个属性。计数与列表已同步。

### 📝 文档（AOT 验收）

- `docs/AOT部署指南.md` 的 PG / MySQL 两节原先只有 publish 命令、**没有运行命令与凭据要求**
  ——按文档操作会在原生程序启动后拿到一串连接失败的堆栈，无从判断是"环境没配好"还是"AOT 链路坏了"
  （本轮实际发生过）。现已补上带 `PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION` 的完整命令，
  并说明本地可用 `set -a && . ./.env.test && set +a` 载入。
- 明确记录取向：这两个程序**刻意不自己读 `.env.test`**——它们是 AOT 验收程序，凭据应由环境显式注入
  （CI 直接注入 secret）；自动读仓库本地文件会让"从仓库跑"与"从 CI 跑"在凭据环节分叉。
- 状态表更新：PG / MySQL 由"原生 publish 通过，CI 运行待验证"改为"本机原生运行通过
  （2026-09-16，对远程开发库）· CI 容器运行待验证"——本轮三个 AOT 程序 publish 后原生运行均 PASSED，
  但按本文档既有口径，最终状态仍以 CI 服务容器为准。

### 🧪 新增测试

- `ReadRouteConnectionReuseTests`（3）：用 `ReadSessionSetupSql` 作副作用探针证伪
  "每查询新建连接"，含对照组证明计数有区分力。
- `DefaultFilterFormsTests`（3）：软删 + 租户的三种拼接形态在 Count/GetAll/Get 上各钉一条。
- `SqlitePoolParameterTests`（3）：`Production` 预设在 SQLite 上能构造会话、
  `WithPool` 被忽略而非拒绝、配了池参数的会话仍能正常执行查询。
- `RegistryScaleTests`（1 个用例 × 2 组参数）：生成 120 实体 × 4 列 与 60 实体 × 30 列，
  编译后直读 PE 元数据，断言**无生成方法超过 64 KB IL**。变异探针：块预算调到 10 MB
  （退回单体）→ 两个用例都失败（123,842 / 143,522 B）。
- 既有用例改名随契约变更：`SqliteConnectionFactory_RejectsUnsupportedPoolOptions`
  → `..._IgnoresUnsupportedPoolOptions`（原断言 `Throws<NotSupportedException>`）。
- 既有用例定位器随分块变更：`NonNumericPrimaryKeys_AreNotMarkedAsGenerated` 原按
  `IndexOf("SetIdDelegates =")` 切片——分块后条目不再连续，切片会落在片段赋值处，
  两条负向断言将在空集上**静默通过**。改为按行提取并先断言"恰好一条"，定位失效即失败。

### 🧪 性能门禁重做与基线重录

- **门禁原先结构上不可能变红**（三处叠加）：
  ① 过滤器 `*SqliteBenchmarks*` 匹配不到任何类名——仓库里最接近的是
  `SqliteSpeedBenchmarks`，中间隔着 `Speed`；实测 BDN 打印可用列表并以**退出码 0** 结束，
  跑 0 个基准；
  ② 回归判定 grep 控制台表格（`grep 'PalORM_QueryAll' | grep 'ms |'`），列宽/单位随 BDN
  版本变化，抓不到就落 `::warning::` 分支；
  ③ 基线阈值是绝对毫秒，而实测同机两次运行的 ADO.NET 地板从 6.97ms 漂到 10.0ms（43%）。
  另有第四处：artifact 上传路径写成 `bench/PalORM.Benchmarks/BenchmarkDotNet.Artifacts/`，
  而 `dotnet run --project` 的 CWD 是仓库根，真实位置是仓库根的 `BenchmarkDotNet.Artifacts/`。
- **重做**：判定改读 BDN 的 `-report*.json`（稳定契约）；范围收为
  `CrudBenchmarks` + `OrmComparisonBenchmarks`（27 项，约 5 分钟，CI 40 分钟预算内）；
  阈值只对**分配字节数（+20%）**与**相对同轮手写对照的比值（+10%）**设——比值在同轮内计算，
  把机器差异约掉，跨机器可比；缺基准/缺对照/schema 不符/哨兵缺失一律 `exit 1`，
  删掉"不可判定即放行"分支。
- **仪器已验证**（此前从未验证过它能否失败）：阳性对照 27 项 `exit 0`；**四处**变异全部被抓到
  ——分配抬高 50%、**中位耗时 ×2（仅耗时比触发，独立于分配维度）**、删掉 Crud 类（哨兵）、
  结果目录为空，均 `exit 1`。
  过程中阳性对照还抓出脚本自身一个恒红缺陷：哨兵按 `CrudBenchmarks.PalORM_` 做前缀匹配，
  而 `FullName` 是 `PalORM.Benchmarks.CrudBenchmarks.PalORM_QueryAll`——改为按叶子名判定。
- **门禁脚本改为 C#**（`tools/PalORM.PerfGate`，已加入 `PalORM.slnx` 与 `PalORM.ci.slnf`）：
  与 `PalORM.Scaffold` 同例的仓库内部工具，用 STJ 源生成上下文序列化（与仓库 AOT 纪律一致）。
  原先的 `python3` 内联片段读控制台表格、失败落 warning 分支——即"仪器本身从未被验证"；
  抽成工具后可本地对同一份 BDN 产物反复跑阳性对照与变异探针。`scripts/perf-baseline.py` 已删。
- **基线重录**：新增 `bench/baselines/perf-baseline.json`（schema 1，27 项，6 项带比值），
  由新工具 `tools/PalORM.PerfGate`（`record` / `check` 两个子命令，供门禁与
  `run-benchmarks.sh` 共用，避免两套解析漂移）生成。v5.0.json 保留为历史记录。
  <br>**判定维度扩到三个**：分配字节数（+20%）、分配比（+10%）、**耗时比（+30%）**。
  耗时比取同轮 PalORM 与手写对照的中位数之比，阈值放宽是因为门禁用 1/3/5 短 job、
  中位数本身有可观方差——它挡的是量级性 CPU 退化而非几个百分点的波动。
  <br>录得的分配比与耗时比揭示一个结构性事实：**分配差距远大于时间差距**——
  `PalORM_QueryAll` 1.137× / 1.161×，`PalORM_Update` 8.095× / 1.281×，
  `PalORM_Insert` 4.335× / 1.463×，`PalORM_GetByKey` 3.288×。
  单行写路径分配是手写 ADO.NET 的 4~8 倍而耗时只慢 1.3~1.5 倍（短命 Gen0 分配，
  回收成本未等比体现）：看分配判断优化空间，看耗时比判断用户感知退化。
- **阈值标定：用同一份代码连跑 3 轮量出噪声底**（每轮 27 项、与门禁同参 1/3/5）。
  原耗时比阈值 +30% 是推断值，现为实测标定：
  分配维度**逐位相同**（仅 `PalORM_QueryAll` 在 1547164 ± 30 B 浮动 = ±0.002%），
  分配比三轮四位小数全同；耗时比相对基线的偏离为 −9.4% ~ **+11.1%**（3 轮 × 6 项 = 18 个采样），
  逐项最坏波动 15.5%（`PalORM_VaryingShape` 1.245→1.438）。
  结论：+30% 是最坏偏离的 2.7 倍，任何小于 20% 的耗时比阈值都只是在测抖动，维持 +30%；
  同代码重跑门禁自检 27/27 PASS，阳性对照（基线耗时比压到 1.0）`exit 1` 且仅该项失败。
- **新发现：基线录于 Windows，门禁跑在 `ubuntu-latest`——跨平台分配一致性未验证**，
  且本机无 Docker 无法复现，故分配阈值保留 +20% 不收紧（实测该维度噪声仅 ±0.002%，
  一旦跨平台一致即可收紧到 +5%）。工具据此新增平台族提示：基线录制平台与当前不同族时
  先打印警告行，避免把平台差读成真回归。族归一按 `RuntimeInformation.OSDescription`——
  实现时踩到一个坑：Ubuntu 上该字段是 `"Ubuntu 24.04.1 LTS"`，字面不含 `Linux`，
  只认 `Linux` 关键词既会漏判（探针实测输出族名是整串描述），也会把两个 Ubuntu 补丁版本
  判成不同族而每次运行都误报；改为多发行版标记（Linux/Ubuntu/Debian/Alpine/CentOS/Fedora/
  Red Hat/Rocky/SUSE）归一到 `Linux`。

- `scripts/run-benchmarks.sh` 同步：`sqlite` 与 `scale` 的过滤器改为实际类名，
  报告路径改到仓库根，基线保存从「读 CSV 拼 JSON」改为调用同一脚本
  （原实现还有个真 bug：`tail | while` 的子 shell 使 `FIRST` 标志失效，
  条目间不输出逗号，生成的是非法 JSON）。

### 🔧 修复（测试基础设施）

- **集成测试必须先手动 `source scripts/set-test-env.sh` 才会通过**：`TestEnvironment`
  现于解析连接串前自动从仓库根 `.env.test` 补入**缺失**的 `PALORM_*` 变量。
  优先级为「显式环境变量 > `.env.test` > 报错」，已设置的值恒不被覆盖，故 CI 注入
  secret 的路径完全不读该文件；文件缺失时保持原有显式报错。
  <br>此前的失败信息是"环境变量未设置"，容易被误读为"没有可用数据库实例"——本轮实测
  发生过一次该误判，并据此错误地推迟了两项远程 Provider 优化。
- **`FindFileUpwards` 的深度差一错误**：`AppContext.BaseDirectory` 以目录分隔符结尾，
  `Path.GetDirectoryName` 首次调用只剥掉它、返回同一层，白耗一次迭代，深度上限实际
  少一层。该缺陷此前被 `appsettings.test.json` 的"复制到输出目录"（i=0 即命中）掩盖，
  只在查找未被复制的 `.env.test` 时显形。已用 `Path.TrimEndingDirectorySeparator`
  归一化起点，上限 6 → 8（RID 特定输出 / AOT publish 更深一层）。
- 新增 6 项单测锁定上述两点
- **批量 UPDATE 参数池（T15）经真库实测否决并回退**：原方案假定参数池在整个
  `BulkUpdateBatchAsync` 调用内建一次、跨批复用；实际 `ExecuteBatchUpdateAsync` 是
  **每批调用一次**，池随之为每批新建，参数创建总量不变，还多出 `DbParameter[]` 数组。
  真库 A/B（PG 与 MySQL 各 40000 行 = 3 批）：含改动 2779.6 / 2689.9 B/行，回退后
  2750.1 / 2582.9 B/行——**改动是净负收益**。单批 1000 行两侧同为约 2670 B/行（无变化）。
  <br>真正的削减点在 probe 命令逐行 `BindUpdate` 建参数（40000 行 × 4 列 = 16 万次），
  需生成器发射 `BindUpdateValues`（对齐既有 `BindInsertValues`）才能消除，属后续项。
- **secret-guard 文件名黑名单误拦 `.env` 家族模板**：黑名单的 `\.env\.[^e]` 只能豁免
  `.env.example` 这一种形态，`.env.<x>.example`（如仓库跟踪的 `.env.test.example`）被拦，
  挡住了对模板的正常修改。已加 `*.example` 豁免（仅文件名规则；内容规则照常执行），
  并补 11 条文件名自测向量（3 误报豁免 + 8 真阳性拦截，含 `.env.production`/`.env.local`/
  `id_rsa` 必须仍被拦截）。守卫自身变异探针：摘掉豁免后自测退出码 1，抓不到才算失败。
- **架构文档测试计数漂移**：`docs/架构设计.md` 与 `docs/API参考.md` 声明的 `[Test]` 标记数
  落后于实测（579 vs 实际 594），D10 门禁已红——上一批新增 12 项测试时未跑
  `doc-consistency-check.sh` 所致。已用该脚本的 `--fix` 机械同步（项目为此漂移提供的
  正规入口），D10 转绿。
：`FindFileUpwards` 用"恰好需要的层数"作上限以区分两种实现
  （变异探针实测：摘掉归一化后仅该用例失败，另 5 项通过），`ParseDotEnv` 覆盖注释/空行/
  引号剥离/非 `PALORM_` 键拒绝。

## [5.5.1] — 依赖全量升级：Roslyn 5.9 · Sqlite.Core rc.1 对齐 SDK · 漏洞清零

> 变更范围：v5.5.0 后 2 个提交，仅 Directory.Packages.props（无 API/行为变更）
> 验证：CI -warnaserror 0/0 · Core 238 / SourceGen 188（快照字节级零漂移）/
> Integration 172（PG/MySQL 真库）· AOT 三平台原生运行 PASSED · 漏洞/过时双归零

### 📦 依赖升级（7 项）

- **Microsoft.CodeAnalysis.CSharp/Analyzers**：5.6.0 → 5.9.0（源生成器构建基础；
  13 份快照字节级对比确认生成物零漂移）
- **Microsoft.Data.Sqlite.Core**：11.0.0-preview.7 → 11.0.0-rc.1（与 SDK 11.0.100-rc.1
  同轨对齐；.NET 11 GA 后切 stable）
- **SonarAnalyzer.CSharp**：10.32.0.713 → 10.34.0.3385（质量门禁）
- **TUnit / TUnit.Assertions**：1.65.0 → 1.66.27
- **Microsoft.SourceLink.GitHub**：10.0.400 → 10.0.401
- **RepoDb / RepoDb.Sqlite.Microsoft**：1.15.x → 1.16.0（bench 对照）
- **漏洞清零**：bench 的 SQLitePCLRaw NU1903（High，GHSA-2m69-gcr7-jv3q）随升级消除

## [5.5.0] — r20/r21 两轮评审全清偿 · [SensitiveData] 脱敏落地 · 方言词法与可观测性

> 变更规模：v5.4.0 后 14 个提交 · 58 文件 · +1975/−383 行（实测）
> 两轮全量评审（r20 43 项 + r21 复检 46 项）全部收口；Core 238 / SourceGen 188 / Integration 172 全绿；三平台 AOT 原生验证 PASSED

### 🔒 安全（P1 级）
- **[SensitiveData] 脱敏信道重构（ITM-763/764/794/751）**：掩码载体由生成器参数的
  SourceColumn（挪用 ADO.NET DataAdapter 标准语义）迁移为运行时注册表
  `PalORM_Runtime.SensitiveColumnMasks` → `QueryBuilder.Set` 登记参数名→掩码 →
  `QueryContext.SensitiveParameterMasks` 传递 → `AuditInterceptor` 替换真实值。
  端到端测试覆盖真实执行路径（此前 r20 实现信道断路、单测手工构造参数掩盖）；
  空/空白 Mask 归一为默认掩码（防明文回退）
- **BulkCopy Warnings 异常不回显服务端原文（ITM-777）**——MySQL 1366 内嵌被拒原始值
- **TestEnvironment 模板 null 守卫 + 异常声明补齐（ITM-776/746 doc）**

### ⚙️ 可靠性与契约
- **回退 r20 的 ITM-722 过度修复（P0）**——MySQL 1061 幂等迁移恢复"警告+跳过"；
  新增 `LastMigrationSkippedIndexes` 无副作用可观测通道（ITM-765）
- **ITM-640 外部事务失效改持续失败（ITM-767）**——原一次性响亮失败后静默自动提交
- **只读 SQLite（Mode=ReadOnly）修复（ITM-766）**——journal_mode=WAL 在只读连接抛 Error 8
- **WhereJson 方言守卫（ITM-770）+ value 归一拒绝 enum/char/TimeSpan/byte[]（ITM-771）**
- **DDL 尊重显式 Zero 无限等待；探活类保留 30s 兜底（ITM-769/762）**
- **PG COPY 超时包装修正（ITM-759/760）**——cleanup 异常挂实际抛出对象；timeoutCts 确证判据；
  CreateAsync 连接超时补 InfrastructureTimeout 标记
- **ConnectionTimeout 补上限（ITM-749）**——CancelAfter 上限 ≈49.7 天
- **半开探针非计数失败熔断复位（ITM-772）**——原 Open 谎言状态使熔断失效

### 🧹 生成器与诊断（39 条口径）
- **IsBalancedParentheses 补三类方言词法（ITM-753）**——MySQL `'`、方括号、PG dollar-quoting
- **PALORM031 降 Warning（ITM-752，SQLite 回退使其在该方言合法）；PALORM046 补命名冲突
  检查（ITM-754）；PALORM012/037 消息修正（ITM-780/781）；PALORM023 对齐 int/long 真源（ITM-782）**
- **诊断口径三方一致**——37→39 条（36 分析器+3 生成器），明细表补 PALORM041/046

### 📊 可观测性
- `db.system.name` 改 OTel semconv 小写（ITM-768）；instrumentation version 5.4.0（ITM-773）；
  PgNotificationListener.LastError 生命周期修正（ITM-761）；From\<T\> 孤儿 doc 归位（ITM-774）

### 🧹 死码与防御（P3 18 项）
AutoTaggingEmitter 头部残留/死分支/不可达守卫清理；AddJoin 空子句守卫（ITM-757）；
EquatableArray 双向防御拷贝（ITM-737/783）；WithOutputParam 可空解包（ITM-787）等

### 🧪 测试
- 新增：SensitiveDataMaskingE2ETests（脱敏端到端 3）· ReadOnlyAndTransactionGuardTests（4）·
  WhereJsonSqlGenerationTests（13，含方言守卫与归一拒绝）· ParenthesisScanAndTemplateCollisionTests（15）
- 迁移：WhereJson DryRun 测试自 Integration 迁 Core.Tests（方言守卫使旧模式失效）

### [5.6.0 ·r21 复检轮] — 回退上批过度修复 · 方言词法/诊断契约/文档三方一致

> 本轮为 2026-09-10 r20 修复批（7 提交）的复检结果。独立分片评审发现并修复如下问题。

### 🐛 修复（严重）

- **回退 r20 的 ITM-722 过度修复**：该修复把 MySQL 1061（索引名已存在）从"警告日志 + 跳过"
  改为"无可见日志即抛异常"，使**正常二次迁移变成硬失败**——与 ITM-528 既有裁决
  （"1061 无法区分同名同构/异构，故不改判定逻辑"）及 `ExternalDatabaseBulkTests` 的
  "二次迁移经 1061 兜底不抛"断言直接冲突，且会中断同批后续实体的迁移。
  现恢复为警告日志 + 跳过，并在注释中登记"不得因日志不可见而改为抛异常"。
  （本地 SQLite/PG 不触发该分支，CI 的 MySQL 容器用例是唯一防线——已在报告盲区登记）
- **ITM-751：`[SensitiveData(Mask = "")]` 把脱敏静默关成明文**：空掩码写入
  `p.SourceColumn = ""`，而读取侧以 `IsNullOrEmpty` 判"无掩码"→ 回落输出真实值。
  现生成器把空白 Mask 归一为默认 `***MASKED***`。
- **ITM-752：PALORM031 对 SQLite 误报 Error 阻断编译**：描述符断言"必抛 NotSupportedException"，
  但 SQLite 已方言回退逐条路径（方法文档亦明写"SQLite 回退则支持"）。分析器无 Provider 信息，
  降为 Warning 并订正文案。
- **ITM-749：`ConnectionTimeout` 无上限**：`CancellationTokenSource.CancelAfter` 的 delay
  上限是 uint.MaxValue-1 毫秒（约 49.7 天），超出抛与配置项无关的 `ArgumentOutOfRangeException`
  （实测 49 天 OK、50/60 天均抛）。与已修的 ITM-695 同族，`DbOptions.Validate` 补齐上限。

### 🐛 修复（方言与诊断）

- **ITM-753：`IsBalancedParentheses` 漏判三类方言词法**——MySQL 反斜杠转义单引号
  （`'it\'s'`）、方括号标识符（SQLite/T-SQL `[we(ird]`）、PG dollar-quoting（`$$a(b$$`）
  被当作未闭合区间吞掉后续括号，合法 `[Computed]` 表达式被判不平衡 → PALORM044 Error 误拒
- **ITM-754：PALORM046 漏检生成类名冲突**——同命名空间既有非 partial 的 `SqlTemplates`
  类型时，生成的 partial 声明冲突以 CS0260 落在 `.g.cs`（属 ITM-573 家族要消灭的形态）
- **ITM-755**：`SqlTemplateEmitter` 宿主形状判定的冗余合取条件（死条件）已简化

### 📝 文档

- **诊断口径三方一致**：`docs/API参考.md` 与 README 由"37 条 / PALORM001-045"
  订正为"39 条（36 分析器 + 3 生成器）/ PALORM001-046"，明细表补齐 PALORM041/046 行
- **ITM-746 补 XML doc**：`TestEnvironment` 两个公开 `Resolve*` 方法补 `InvalidDataException`
  声明（模板格式非法；消息不回显模板内容以免泄露字面量凭据）
- D10 计数同步（`docs/架构设计.md` / `docs/API参考.md`）

### 🧪 测试

- 新增 `ParenthesisScanAndTemplateCollisionTests`：方言词法参数化用例（11 组，含三类新增方言形态）
  + PALORM044 端到端 + PALORM046 命名冲突三态（非 partial 冲突 / partial 不冲突 / global 不冲突）
- PALORM031 用例补严重级断言（Error → Warning）

## [5.4.0] — 弹性只读管线 · 编译时诊断 PALORM045 · legacy SQL/DDL 收敛至方言单一真源

> 变更规模：29 个提交 · 84 个文件 · +2893/−1112 行（v5.3.0…v5.4.0 实测）
> 评审整改批次：2×P0（CI 门禁失效）+ 3×P1（弹性脱节/事务静默降级/生成器崩溃 UX）+ 工程防线托管
> 架构评审整改批次（2026-09-02）：评审报告 P1×2 + P2×4 + P3×3 全项清偿（详见下文各节）
> 架构评审第二批（2026-09-02）：复审报告 P2×2 + P3×3 + 可选项全项清偿（ADR-J + 包契约测试补强）

### ✨ 新增

- **弹性策略接入只读查询内置管线**：`WithRetry`/`WithCircuitBreaker` 此前对内置管线无效
  （全库唯一消费点是显式 `ExecuteWithResilience`）——现覆盖 `From<T>()` SELECT 家族、
  `GetAsync`/`GetAllAsync` 与聚合五兄弟；写入路径与事务内查询维持直连（幂等性契约 ITM-310）
- **编译时诊断 PALORM042-044**（ITM-640 收口）：`[Timestamp]+[Computed]` 冲突、SQL 标识符
  含控制字符/空串、`[Computed]` 表达式 NUL 或括号不平衡——此前以生成器异常（CS8785）
  或静默跳过呈现，现编译期精确定位；生成器侧改为防御性跳过不崩溃
- **编译时诊断 PALORM045**（架构评审 2026-09-02）：生成器 transform 失败面兜底 Warning——
  分析器规则被 `.editorconfig`/ruleset 抑制时，实体静默跳过的唯一编译期线索
  （此前该场景编译期零反馈，故障延迟到运行期 not registered）
- **[SqlFile] 增量管线接入文件内容**：.sql 文件经 AdditionalFiles（包 targets 自动注入
  `**/*.sql`，仓库内项目经 Directory.Build.props）进入增量缓存键——仅编辑 .sql 也触发
  重新生成（消除 ITM-585 陈旧缓存限制）；项目根改读 `build_property.ProjectDir`（不再
  从源文件路径向上找 *.csproj）；生成器零磁盘 IO
- **legacy CreateTableSql 生成段移除**（ADR-J，复用 ADR-I 修订模板）：三方言 DDL 是唯一
  真源；`RegistryFragment.CreateTableSql` 放宽为可选（旧片段仍可注册，运行时从不执行）；
  `MigrateAsync` 实体枚举源改走 `TableNames.Keys`，缺方言键的 recompile 错误保持不变
- **测试补强**：PG/MySQL 连接串自动调优值断言（此前仅注释承载）、SQLite 文件库/内存库
  PRAGMA 断言、`WithTransaction` 内未释放 GridReader 由事务收口兜底释放的安全网行为锁定、
  SqlFile AdditionalFiles 管线正/负例、独立双跑锁定 .sql 内容→嵌入产物映射（零串扰）、
  包契约测试与 NuGet 包消费者 AOT 冒烟补入 SqlFile 用例（覆盖包分发路径的 targets 注入契约）

### 💔 破坏性变更

- 默认 `DbOptions`（MaxRetries=3/CircuitBreakerThreshold=5）下，只读查询的瞬时故障现在
  自动重试并计入熔断——与连接建立重试同口径；`Testing` 预设（零重试零熔断）行为不变；
  事务内查询不受影响（重试会以次生异常掩盖根因）
- **legacy 无方言 CommandSqls 生成段移除**（ADR-I 修订：原绑定 v6.0，提前理由见 ADR-I
  修订节）：重编译模型程序集时须同步升级 PalORM.Core/Provider 包——"旧运行时 + 新生成
  片段"从注册成功/CRUD 时抛错改为注册期抛键集校验错误（均为响亮失败）；
  `RegistryFragment.CommandSqls` 放宽为可选（旧片段仍可注册，运行时从不消费）；
  `CrudMetadata` 新增不含 SQL 载荷的推荐构造，旧构造保留供旧生成片段二进制兼容
- **legacy CreateTableSql 生成段移除**（ADR-J，同模板）：`RegistryFragment.CreateTableSql`
  放宽为可选（运行时从不执行）；重编译模型程序集须同步升级包——影响面与上一条相同；
  `MigrateAsync` 实体枚举改走 `TableNames`，行为仅"旧片段错误消息时点"变化
- `QuerySingleAsync` 多于 1 行时的异常消息由精确总数改为 "at least 2"（流式精确单行——
  读到第 2 行立即失败并释放 reader，不再物化全表）；PALORM003 默认严重度由 Error 复议为
  Warning（多程序集布局可误报，见 ADR-D）；PALORM020 消息格式模板化、PALORM041 category
  归一为 "PalORM"；PALORM034 判定由初始化器文本白名单改为语义常量值（等价写法自然归一）

### 🐛 修复

| 问题 | 影响 | 修复方式 |
|------|------|---------|
| perf-gate.yml 在 CI 必然失败 | 性能门禁从未生效 | BDN fork clone 步骤 + restore 收窄 + `[perf]` 标签门控 |
| CI 秘密扫描第二道防线空转 | 40 类自定义规则从未消费 CI 差异集 | secret-guard 新增 `--range` 模式并接入 security job；临时仓库探针双向验证 |
| UseTransaction 外部事务被外部 Dispose 后静默降级自动提交 | 写操作丢失事务隔离无反馈（ITM-640） | 失效的外部事务响亮失败；`UseTransaction(null)` 显式清场逃生门 |
| `QuerySingleAsync` 为判"恰好一行"物化全表 | 大表上静默全读 | 流式精确单行：读第 2 行立即失败并释放 reader（对齐 QueryFirstAsync 策略） |
| `SessionOperationState.DisposeWaitTimeout` 可变静态 + 单读人工契约 | 双读事故两次发生（ITM-581/629），纪律已被证伪 | 结构化为实例状态：生产只读、测试经 `DataSession.DisposeWaitTimeout` 按会话设置，无需保存/还原全局值 |
| `BoundedQueryCache` 每实例注册 ObservableGauge 且回调闭包持有字典 | instrument 与缓存字典不可 GC（review R8 登记的泄漏） | 进程级单次注册 + 弱引用实例表（回调顺带剪枝死引用），指标名与标签形状不变 |
| PALORM034 初始化器文本白名单 | `0.0e0`/`1_000` 等等价写法误报、变体拼写漏报 | 语义常量值判定（GetConstantValue），仅保留语义可证的 Guid.Empty 特判与 MinValue 哨兵例外 |
| PALORM005 N+1 检测语义查询先行 | 绝大多数非循环调用白付 GetSymbolInfo | 语法圈检查前移（与 PALORM033 修复同口径） |
| SqlFileEmitter 注释声称"RS1041 强制 netstandard2.0 故不能用 AdditionalTexts" | 错误理由误导后来者（该 API 与 TFM 无关） | 注释更正为真实动机（内容进缓存键），并以此为基础完成 AdditionalTexts 重构 |
| 运行时异常消息中英混杂（15 处） | 公共库消费者无法统一检索 | 全部统一为英文（DataSession/Transactions/QueryBuilder/PostgreSqlExtensions/PgNotificationListener/SqliteProvider） |

### 🔧 工程

- **ADR-D 裁决落档（D3 降级告警）**：唯一开放 46 天的草案正式关闭——2026-09-02 第一批
  整改的 PALORM003 Warning 化即 D3 语义（"无法在本程序集验证，运行时自负"），本裁决为
  追认；D1（跨程序集 FK 扫描）保留为未来选项。至此 11 篇 ADR 全部状态终结，决策台账
  零未决项
- **AI 质量系统 v7.2 升级**（lessons B46-B53 八项实践登记 + AF 节 B54-B55 两项质检沉淀，
  计数修正至 62）：secret-guard 白名单正则变量化并入 --selftest（9 向量）；
  test-quality-scripts.sh skip_ai 真修复并接入 ci.yml gate（"声称在岗"防线收口）；
  .githooks 薄包装托管消除 cp 副本漂移
- **全量质检运行产出修复四则**：G18 门禁词边界化（裸子串误命中 RunInTransactionScopeAsync）、
  G9 豁免链补检测器自测向量文件、test-gate T-DEF-1 引入带理由豁免表（聚合计数器模式豁免）、
  IdentifierConsistencyTests 两处零断言改 Assert 形态
- **源码精炼批次**（评审整改，净 -220 行）：MySQL UPSERT 死代码对删除（UPSERT SQL 收敛
  至生成物单一真源）；WithTransaction 双重载委托泛型核心；聚合 Sum/Max/Min/Avg 共享
  ExecuteAggregateScalarAsync 内核（Count 形态不同保留独立）；Bulk 家族四处事务骨架
  收敛至 RunInTransactionScopeAsync 单内核（ITM-676/556/704 语义逐点保持）
- **legacy CommandSqls 链清除**（ADR-I 修订：由 v6.0 窗口提前至本批次，裁决变更理由
  与混合场景影响分析见 ADR-I 修订节）：生成物删除 7 legacy const + 无方言字典，方言 SQL
  唯一真源；快照基线已刷新并人工评审 diff（净删除，无语义漂移）
- **变异测试接入 CI**（mutation-tests.yml，每周六自动 + 手动）：Core 既有配置生效化，
  新增 SourceGen emitter + 分析器变异面——threshold-break=40 自此首次具备约束力
- `.githooks/pre-commit` 薄包装托管（`core.hooksPath` 方案），消除 cp 拷贝式安装的脚本漂移
- secret-guard 白名单精确豁免 MySQL uint64 LIMIT 常量（20 位连续数字误触身份证号规则）
- perf-gate 基线 v4.0.json→v5.0.json；回归解析失败从静默放行改为显式警告标注不可判定
- README 驱动版本对齐 Directory.Packages.props 实际值（MySqlConnector 2.6.2 / SQLite3MC 2.4.0）

### 🧪 验证

- **单元测试 369 项全绿**：Core 209 + SourceGen 160（Release 配置，`GITHUB_ACTIONS=true` 模拟 CI 环境）
- **Integration 181 项**：本地实跑 171 通过、10 项因本地无 PG/MySQL 服务容器未执行
  （环境变量缺失，非代码失败）；CI release 流水线以 postgres:17 / mysql:8.4 容器执行全量
- **Release 严格构建**：`PalORM.ci.slnf -c Release --no-incremental -warnaserror` 0 警告 0 错误
  （本地离线环境以临时清空 auditSources 规避 NU1900 联网审计；CI 在线环境不受影响）
- **包契约**：`test-package-contract.sh` PASS——本地打 5 个 5.4.0 包 → 仓库外独立消费工程
  → 实体身份契约 + SqlFile AdditionalFiles 注入验证
- **pack 计数 5 个**（Core/SourceGen/Sqlite/PostgreSql/MySql），nuspec 元数据抽查
  id/version/license(AGPL-3.0-only)/projectUrl/releaseNotes 全部正确
- **快照基线 13 份一致**（`PALORM_UPDATE_SNAPSHOTS=1` 刷新后 diff 人工评审为净删除）

## [5.3.0] — byte[] 二进制列原生支持（契约显式化 + AOT 全链 + 基准背书）

> 6 个提交 · 30 个文件 · +743/−92 行 · 四环节编译期契约补齐（读取/绑定/DDL/BulkInsert）
> 设计决策与放弃项见 [ADR-K](docs/adr/ADR-K-byte[]-二进制列支持.md)（发布时编号 ADR-G，2026-09-02 索引重建改号）；使用规范见 [二进制列最佳实践](docs/二进制列最佳实践.md)

### 💔 破坏性变更

- 无。白名单从"拒绝 byte[]"放宽为"收窄放行"，此前被 PALORM016 拒绝的实体现在可生成——纯放宽，Converter 出口 `string` 的既有路径不受影响。

### ✨ 新增

- **`byte[]` 一维数组列原生支持**：不再强制 Base64 TEXT 或 `[Converter]` 中转
  - 白名单收窄放行：仅元素为 `System.Byte` 且 `Rank == 1` 的数组——`int[]`/`string[]`/多维数组仍被 PALORM016 拒绝
  - DDL 映射：`MigrationEmitter.GetBinaryDbType` 三方言 BYTEA（PG）/ BLOB（SQLite）/ LONGBLOB（MySQL 数据列，4GB 上限对齐 Pomelo 惯例）；主键/索引列 VARBINARY(255)（BLOB 索引前缀约束，防错误 1170）
  - 参数绑定显式化：生成 binder 对 byte[] 列发射 `DbType.Binary`——PG Binary COPY 经 `NpgsqlParameter.DbType → NpgsqlDbType.Bytea` 显式分派，不依赖驱动运行时推断
  - 等值过滤：`Where($"col = {bytes}")` 参数化直通（含 0x00 字节），Native AOT 原生二进制下验证
  - Scaffold 反向工程闭环：BLOB/bytea/varbinary 全族 → 可用实体（修复前"生成即不可用"）
  - 锁定测试：`ByteArrayColumns_GenerateCrudAndBlobDdl`、`BinaryColumn_EqualityPredicate_FiltersRows`、`ScaffoldReverseEngineeringTests` ×2、AllTypes/BulkBinary/ExtBulk 往返（PG COPY 与 MySQL 真库）

### 🐛 修复

| 问题 | 影响 | 修复方式 |
|------|------|---------|
| byte[] 列落 TEXT 兜底（ITM-661(r4) 登记） | PG TEXT 拒 0x00 字节、静默变形为 hex 文本；MySQL strict mode 报 1366 | 白名单放行 + GetBinaryDbType 三方言 BLOB 映射 |
| Scaffold 可空引用列不加 `?`（预存缺口） | 反向工程的可空 TEXT/BLOB 列读 NULL 即抛 SqlNullValueException | 生成物带 `#nullable enable`，可空列（含 string/byte[]）加 `?` 后缀驱动 IsDBNull 守卫 |
| bench BDN fork NU1100 环境项 | 本地基准全部无法运行（T10 债根源） | 根 NuGet.Config 为 fork 源键补通配映射 |
| secret-guard v3 三处误报 | 合法提交被拦，诱发 `--no-verify` 习惯化 | 规则 14 占位符连接串/规则 32 大小写敏感/根 NuGet.Config 文件名精确豁免；钩子按文档程序同步 |
| T10 噪声债（r18/T-P3-07） | SqlBuild 基线 Error/Mean 16.9% 超阈 | 高精度重跑 ≤4.9%，基线数字入档 |

### ⚡ 性能

- **二进制列基准首跑**（SQLite 内存，StandardJob 3/5/10，详见 BENCHMARKS.md）：
  - 64KB 档：原生 BLOB 插入 **108μs / 69KB 分配 / 零 Gen2** vs Base64 TEXT 246μs / 432KB / **Gen0+Gen1+Gen2 全触发**——2.3 倍延迟、6.3 倍分配；Base64 编码串 85.3KB 越过 .NET 85000B LOH 阈值（实测坐实）
  - 256B 档两者相当（噪声区间）——二进制优势随载荷尺寸放大

### 📦 依赖升级

- 无

### 🧪 验证

- **523 个测试全绿**：Core 195 + SourceGen 147 + Integration 181（含 PG COPY / MySQL 真库 byte[] 往返）
- **Native AOT**：win-x64 本地 publish + 原生二进制实跑通过（含 0x00 等值参数化/物化往返/NULL 守卫）；linux/osx 由本次 tag 触发的 release workflow 验证
- **快照基线** 13 份一致（重生成并人工评审 diff：四条 DDL 均为 BLOB 语义、绑定器含 DbType.Binary）
- CI slnf 非增量构建零警告；tech-debt 扫描 12/12

### 📚 参考

- [ADR-K：byte[] 二进制列支持](docs/adr/ADR-K-byte[]-二进制列支持.md) · [二进制列最佳实践](docs/二进制列最佳实践.md) · [基准数字](bench/PalORM.Benchmarks/BENCHMARKS.md)

## [5.2.0] — 质量收口（14 轮 AI 评审 + record 支持 + 隔离级别全链 + 真库回归）

> 58 个提交 · 127 个文件 · +5390/−2939 行 · 48 个产品代码文件变更
> 14 轮 AI 自动化评审-修复迭代，每批修复经下一轮独立验证确认

## 💔 破坏性变更

- **`CrudColumns` 构造函数**：2 参数 → 3 参数（新增 `update` 列集）
  ```csharp
  // v5.1.0
  new CrudColumns(insertColumns, upsertColumns)
  // v5.2.0
  new CrudColumns(insertColumns, upsertColumns, updateColumns)
  ```
- **`IDbProvider.BulkInsertAsync`**：新增 `IsolationLevel` 参数（自定义 Provider 需同步签名）
- **`[Table]` 实体类约束**：record 类型现在被支持（不再是 class-only）

## ✨ 新增功能

- **record 实体支持**：`[Table] public record User { [Key] public long Id { get; set; } }` 现在完全可生成。
  - `get; set` 属性 → 真实生成（RowFactory/CommandFactory/Migration/Registry 全输出）
  - 位置参数 `record User(string Name)` → PALORM015 Error（缺无参构造，有定位诊断）
  - `[Key]` init-only → PALORM022 Error（防 CS8852，有定位诊断）
  - 锁定测试 ×3（GeneratorPhase2Tests）
- **隔离级别全链透传**：`WithIsolationLevel()` 现在在所有自开事务路径生效
  - `ToPageAsync`（此前仅单条 CRUD 生效）
  - `BulkInsertAsync`（PG COPY / MySQL BulkCopy / MySQL 多值 fallback）
  - `BulkDeleteAsync` / `BulkUpdateAsync` / `BulkUpdateBatchAsync` / `BulkMergeAsync` / `SeedAsync`
- **`CrudColumns.Update` 列集**：批量 UPDATE 不再反解 SQL 文本，直接消费编译期元数据
- **`SqlTemplate` 生成物 `global using System`**：限定引用（`{Math.PI}`）在无 ImplicitUsings 项目不再 CS0246

## 🐛 修复

| 问题 | 影响 | 修复 |
|------|------|------|
| MySQL Upsert 自增主键不回填 | `entity.Id` 恒为 0 | LAST_INSERT_ID 死分支 + 补 SELECT 后缀 |
| Raw 子句分页 Total 虚高 | 分页统计错误 | BuildCountSql 补 Raw 消费 |
| PG `WhereJson<T>(bool)` 恒空 | 查询静默返回空结果 | bool 特判小写 "true" |
| Audit `logParameters=false` 仍泄露 PII | PG DETAIL 参数值入日志 | else 分支不再传 exception 实例 |
| 连接池参数被静默覆盖 | 用户 `Max Pool Size=500` 被改为默认 100 | 仅默认值时覆盖策略 |
| Bulk 空列表+未注册类型静默 0 | 同一非法输入两种结果 | 六方法统一前置检查 |
| PgListener 首连挂起 | LISTEN 瞬态失败后 `StartAsync` 永久阻塞 | TrySetResult 锚定 startupPhase |
| ToPageAsync 缓存键污染 | 页截断结果写入用户缓存致同键 ToListAsync 丢行 | 克隆后清 `_cacheKey` |
| UPDATE + Take/Skip 扩大范围 | `.Set().Take(5)` 静默丢 LIMIT 更新全表 | 构建期显式拒绝 |
| PALORM024 谓词误报 | 纯 `[IgnoreOnInsert]` 实体被 Error 阻断 | 分析器谓词对齐生成器真源 |
| PALORM025 Nullable 时间误报 | `DateTime?` 属性被 Error 阻断 | UnwrapNullable 判定 |
| PALORM031/032 推断式调用漏报 | `BulkUpdateBatchAsync(list)` 不报 | 语义层 TypeArguments |
| `WithTransaction` 已释放事务误导报错 | 报"不属于主连接"而非"已释放" | 先查 Connection null |

## 📦 依赖升级

| 包 | 旧版本 | 新版本 |
|---|--------|--------|
| Microsoft.Data.Sqlite.Core | 11.0.0-preview.6 | 11.0.0-preview.7 |
| MySqlConnector | 2.6.1 | 2.6.2 |
| SQLite3MC.PCLRaw.bundle | 2.3.6 | 2.4.0 |
| Microsoft.CodeAnalysis.Analyzers | 5.3.0 | 5.6.0 |
| Microsoft.SourceLink.GitHub | 8.0.0 | 10.0.400 |
| TUnit / TUnit.Assertions | 1.61.38 | 1.65.0 |
| SonarAnalyzer.CSharp | 10.30 | 10.32 |

## 🧪 验证

- **518 个测试全绿**：Core 195 + SourceGen 144 + Integration 179（含 PG/MySQL 真库回归）
- **Native AOT** 三平台 publish 零错误 + 原生二进制运行通过
- **快照基线** 13 份一致（源生成器输出锁定）
- **每批修复经下一轮独立地毯评审验证**（连续 4 轮零 P0-P2 终证）

## 🔧 开发基础设施

- **敏感信息三层防护**：.gitignore 20+ 模式 → pre-commit hook 9 类检测 → CI gitleaks 全历史扫描
- **Release 自动化**：从 CHANGELOG 自动提取版本段 + 组装标准 Release Body
- **CHANGELOG 规范化**：全部版本统一 emoji 段头七段结构


## [5.1.0] — Auto Tagging Interceptor（SourceGen 自动 SQL 源码定位）

> 基于 `docs/v5.1-auto-tagging-design.md` + ADR-F。本版本引入 **opt-in 的自动 Query Tagging**，
> 用户零代码改动即可让 SQL 自动带源码定位注释（`/* 相对路径:行号 方法名 */`）。

### ✨ 新增

- **Auto Tagging Interceptor**（opt-in）：消费侧 csproj 设 `<PalORMAutoTagging>true</PalORMAutoTagging>` 启用。
  源生成器在编译期检测 `QueryBuilderExtensions` 的 6 个终态方法调用（`ToListAsync`/`FirstAsync`/
  `FirstOrDefaultAsync`/`SingleAsync`/`SingleOrDefaultAsync`/`ExecuteNonQueryAsync`），
  为每个调用点生成 `[InterceptsLocation]` 拦截方法，自动注入 `builder.Tag(...)` 后调用原方法。
  - SQL 日志自动含 `/* Controllers/UserController.cs:42 GetUserList */` 样式注释
  - 路径规范化：绝对路径 → 相对工作目录（避免泄露编译机目录结构到 DB 日志）
  - AOT 全兼容：net11 NativeAOT publish 0 警告实测通过
- **buildTransitive targets**：NuGet 包消费侧只需一行 `<PalORMAutoTagging>true</PalORMAutoTagging>`，
  targets 自动注入 `Features`/`InterceptorsNamespaces`/`CompilerVisibleProperty`
- **ADR-F**：`docs/adr/ADR-F-auto-tagging-interceptor.md`（决策记录 + 6 项技术约束）
- **B29 教训**：`.ai/lessons.md` AC 章节（Interceptor 实施工程化缺陷 + PoC 驱动 SOP）

### 🔧 技术约束（详见 ADR-F）

| 约束 | 说明 |
|------|------|
| `GetInterceptableLocation` 是扩展方法 | 在 `CSharpExtensions` 类，非 `SemanticModel` 实例方法 |
| `InterceptsLocationAttribute` 命名空间 | 编译器硬编码要求 `System.Runtime.CompilerServices` |
| MSBuild Property 传递 | 需 `<CompilerVisibleProperty>` 显式声明 |
| `TagWithCaller` 不能直接复用 | Caller* 在拦截器中返回拦截器自身位置，需用 `Tag(string)` + 编译期常量 |

### 🧪 测试

- 新增 4 个 AutoTagging 单元测试（开关关闭零生成 + 开关开启生成拦截器 + 签名匹配 + 路径规范化）
- `test/PalORM.AotTest.MySql` 扩展：启用 PalORMAutoTagging + 2 个拦截调用点
- 125 个 SourceGen 测试全部通过（零回归）

### 📚 参考

- 设计文档：`docs/v5.1-auto-tagging-design.md`（含"实施差异说明"章节，B9 教训）
- EF Core 参考实现：[Thirty25 博客](https://thirty25.blog/blog/2025/04/ef-core-source-gen-interceptors)

## [5.0.0] — 驱动现代化 + 调优（包升级 + 连接串/PRAGMA 性能调优）

> 基于 v5.0-roadmap.md 的 5 阶段方案。本版本聚焦**驱动层现代化 + 默认调优**，
> 不引入新公共 API。架构级改造（DbDataSource 单例化 / MySqlBulkCopy / DbBatch）
> 与功能增值（阶段 5）作为独立后续工作，不在 v5.0 主线。

### 💔 破坏性变更（测试源码层，公共 API 零破坏）

仅影响**测试代码**（src/ 公共 API 面零改动）：
- TUnit 0.19.24→1.61.15 引入 4 类测试 API 适配（10 处修复）：
  - `Assert.ThrowsAsync<T>(Task)` 重载移除→改用 `() => task`
  - `Assert.ThrowsAsync<T>` 返回类型可空化（`TException?`）→访问 `.Message` 加 `!`
  - `HasCount()` 已弃用→改用 `Count().IsEqualTo(n)`
  - `WithMessage(string)` 新增 `StringComparison` 重载→加 `Ordinal`

### 📦 依赖升级

| 包 | v4.6 | v5.0 | 备注 |
|---|:---:|:---:|------|
| TUnit | 0.19.24 | **1.61.15** | 测试框架现代化 |
| TUnit.Assertions | 0.7.9 | **1.61.15** | 与 TUnit 元包版本锁定 |
| Microsoft.NET.Test.Sdk | 17.13.0 | **删除** | TUnit 1.x 走 MTP 模式，不需 VSTest 桥接 |
| Npgsql | 9.0.3 | **10.0.3** | RowFactory 兼容（GetDateTime 仍返回 DateTime） |
| MySqlConnector | 2.4.0 | **2.6.1** | 含安全修复 GHSA-473q-m89c-ghf8 |
| BenchmarkDotNet | 0.14.0 | **0.15.8** | 基准工具升级 |
| Dapper | 2.1.66 | **2.1.79** | 基准对照升级 |
| RepoDb | 1.13.1 | **1.15.1** | 基准对照升级 |
| RepoDb.Sqlite.Microsoft | 1.13.1 | **1.15.0** | 基准对照升级 |

**保持不动**（有约束）：Microsoft.CodeAnalysis.Analyzers 5.3.0（5.6.0 不在 NuGet.Config 配置的 dotnet-tools 源，触发 NU1103）；Microsoft.Data.Sqlite.Core 11.0-preview.6（等 net11 GA）。

### ⚡ 性能调优

**PG 连接串调优**（PostgreSqlProvider.CreateConnection，用户显式值优先）：
- MaxAutoPrepare 0→100（自动预编译，查询延迟 -30~50%）
- AutoPrepareMinUsages 5→2（第 2 次起 Prepare）
- NoResetOnClose false→true（归还连接跳过 DISCARD ALL，+30% localhost 吞吐）
- ReadBufferSize/WriteBufferSize 8192→16384（大结果集/大值写入吞吐）
- Enlist true→false（跳过 TransactionScope 检查）

**MySQL 连接串调优**（MySqlProvider.CreateConnection，用户显式值优先）：
- AutoEnlist true→false / ConnectionReset true→false（归还池更快）
- CancellationTimeout 2→5（防连接泄漏）
- AllowLoadLocalInfile false→true（MySqlBulkCopy 前提，为后续阶段铺路）
- ServerRedirectionMode Disabled→Preferred（Azure MySQL 直连）

**SQLite PRAGMA 调优**（SqliteProvider.InitializeConnectionAsync）：
- synchronous=NORMAL（WAL 下安全，减少 fsync）
- cache_size=-65536（64MB 页缓存，默认 2MB）
- temp_store=MEMORY / wal_autocheckpoint=1000
- mmap_size=268435456（256MB，**仅文件数据库**，:memory: 跳过）

**global.json**：rollForward `disable`→`latestMinor`（允许 SDK 补丁版本前滚）。

### ✨ 新增：MySQL BulkInsert 能力检测分流

`MySqlProvider.BulkInsertAsync` 改为 **local_infile 能力检测** 分流（替代原 2000 行阈值）：
- **local_infile=ON**（服务端）→ 走 `MySqlBulkCopy`（LOAD DATA LOCAL INFILE 协议，~4.84x）
- **local_infile=OFF** 或非 MySqlConnection → 走多值 INSERT

**无阈值**：不再用行数阈值（2000 是伪精确），改为环境能力检测——行为可预测。与 PG COPY 永远走最优协议对齐。检测开销：每次 BulkInsert 额外 1 次 SHOW VARIABLES RTT（<1ms，批量场景占比可忽略）。

**部署约束**（已文档化）：客户端连接串默认追加 `AllowLoadLocalInfile=true`（v5.0 阶段 3.2）；服务端需 `local_infile=ON`（MySQL 默认 OFF，需 `SET GLOBAL local_infile=ON` 或 my.cnf）。

**DataTable 设计**：包含目标表全部列（含 AUTO_INCREMENT 主键），主键列填 NULL 让 MySQL 自增。

### 📐 调优判断策略

"用户未显式设置"的判据：属性当前值等于 ADO.NET 默认值。该判据在罕见场景（用户显式
设成默认值）下会把用户意图当作默认覆盖，但调优参数主动设成低性能默认值的实际场景极少，
收益（透明调优）大于风险。用户可通过显式设置非默认值避免被覆盖。

### ❌ 不做项（v5.0 排除 + 理由）

| 项 | 理由 |
|------|------|
| 阶段 3.4 NpgsqlParameter\<T\> 零装箱 | **已实测决策不做**——装箱占比 ~24.5% 但 PG COPY/MySQL BulkCopy 已无装箱；SQLite 无泛型参数 API。详见 `docs/boxing-benchmark-design.md` |
| 阶段 3.6 SQLite Pooling/Cache 解除限制 | 强制 Pooling 有测试隔离风险；用户可在连接串显式配置 Pooling=true / Cache=Shared |
| 阶段 4.1 DbDataSource 单例化 | **已批准 E1（不做）**——5 条 ORM 实践调研 + EF Core #3086 反面证据 + Npgsql 官方 "discouraged 非 deprecated"。详见 `docs/adr/ADR-E-dbdatasource-单例化取舍.md` |
| 阶段 4.3a BulkMerge 多值 UPSERT | 分流有害（语义偏差）+ RETURNING 多行回填复杂 + 用户无需求。BulkUpdateBatchAsync（4.3b）已完成 |
| 阶段 5.1 单操作 timeout/retry override | API 一致性困境（20+ 方法）+ 替代方案充分（多 DataSession / CancellationToken）|
| 阶段 5.6 ASP.NET Core 集成包 | 设计哲学冲突（无 DI）+ 替代方案充分（单例 DbOptions + 工厂模式）|
| 阶段 5.7 MySQL VECTOR 映射 | Innovation 版 + MySqlVector\<T\> API 不完整 + MySQL 8.4 测试环境不支持 |
| Microsoft.CodeAnalysis.Analyzers 5.6.0 | NuGet.Config 约束 Microsoft.CodeAnalysis.* 只从 dotnet-tools 源，该源无 5.6.0 stable |

### ✨ 功能增值（阶段 5 第一梯队）

**5.2 SessionSetupSql**（`DbOptions.SessionSetupSql` + `DbOptions.ReadSessionSetupSql`）：
- 主连接 + 读副本分别配置会话级 SQL（如 `SET TIME ZONE` / `SET search_path` / `SET statement_timeout`）
- 多条 SQL 分号分隔一次 `ExecuteNonQueryAsync` 执行（三方言均支持多语句）
- `IsNullOrWhiteSpace` 判断 null/空白等价未设置（向后兼容）
- 读连接初始化器：无 ReadSessionSetupSql 时用 static 委托（零闭包分配）

**5.4 AuditInterceptor**（`src/PalORM.Core/AuditInterceptor.cs`）：
- 实现 `IQueryInterceptor` 三段式：OnBefore/OnAfter/OnError
- Priority=200（让用户业务拦截器优先）
- `logParameters` 默认 false（脱敏，避免凭据/PII 写入日志）
- `ILogger.IsEnabled` 短路优化（无订阅者零开销）
- 覆盖面继承 IQueryInterceptor：仅实体 SELECT + QueryBuilder UPDATE；INSERT/DELETE/Bulk/存储过程不经过

**5.5b AdvisoryXactLock**（`src/PalORM.PostgreSql/AdvisoryLockExtensions.cs`）：
- 4 个扩展方法：`AcquireXactLockAsync(long)` / `AcquireXactLockAsync(int,int)` / `TryAcquireXactLockAsync(long)` / `TryAcquireXactLockAsync(int,int)`
- 事务级锁（`pg_advisory_xact_lock` / `pg_try_advisory_xact_lock`），事务结束自动释放
- 单 bigint key 和双 int key 是独立锁空间（不冲突）
- 用法：`await db.WithTransaction(async ct => await db.AcquireXactLockAsync(key, ct))`

**5.5 ForUpdate**（`QueryBuilder.ForUpdate(skipLocked)`）：v5.0 前已实现，本次确认存在。

### ✨ Scaffold 三 Provider 支持

`tools/PalORM.Scaffold` 从 SQLite-only 扩展为三 Provider：
- **新增 ISchemaProvider 抽象**（`tools/PalORM.Scaffold/ISchemaProvider.cs`）：屏蔽三方言元数据差异
- **三 Provider 实现**：
  - `SqliteSchemaProvider`：sqlite_master + PRAGMA table_info
  - `PostgreSqlSchemaProvider`：information_schema + JOIN tables/key_column_usage
  - `MySqlSchemaProvider`：information_schema.COLUMNS（DATABASE() 当前库过滤）
- **TypeMapper**（`tools/PalORM.Scaffold/TypeMapper.cs`）：DB 类型 → C# 类型，按方言分支
  - SQLite 按亲和性（INTEGER/REAL/TEXT/BLOB/NUMERIC）
  - PG/MySQL 按精确类型名 + 长度后缀裁剪（如 `varchar(255)` → `varchar`）
  - 覆盖 40+ 类型（含 uuid/jsonb/bytea/tinyint/DateOnly/TimeOnly 等）
- **EntityGenerator**：与 Provider 解耦，从 SchemaTable DTO 生成 C# 实体类
  - snake_case → PascalCase 转换（表名 + 列名）
  - 列名与属性名不同时加 `[Column("原名")]`
  - 自增 PK 加 `[Key(AutoIncrement = true)]`
  - 引用类型属性加 `= default!`（避免 nullable 警告）
  - 值类型可空列加 `?` 后缀
- **CLI 扩展**：`--dialect sqlite|pg|mysql` + `--namespace NS` + `--output DIR`
  - 方言别名：pg/postgres/postgresql、mysql/my、sqlite/sql
  - 兼容旧位置参数（args[1] 当 namespace）

**真实集成验证**（三 Provider 连真实库 scaffold）：
- SQLite：建表 → 生成 `UserOrders` 实体（含可空列 + 自增 PK）
- PostgreSQL：连 PG 18.4，生成 `AllTypesEntities`（uuid/bool/DateOnly/TimeOnly 类型完整映射）
- MySQL：连 MySQL 8.4.10，生成 `AllTypesEntities`（datetime/decimal/char 类型映射）

### ✨ 批量 UPDATE 单语句化

**新增** `DataSession.BulkUpdateBatchAsync<T>`（方案 Y 严格版）：
- 单次 RTT 完成 N 行 UPDATE（PG: UPDATE FROM VALUES 4x 提速；MySQL/SQLite: CASE WHEN）
- 永远走批量，无内部阈值，N=1 也走批量（用户显式选择）
- 带 `[ConcurrencyCheck]` 的实体调用抛 `NotSupportedException`（批量无法表达每行 version 匹配）
- 参数上限自动分批（PG/MySQL 65535，SQLite 999），物理约束非性能阈值
- 租户过滤自动追加 `AND tenant_id = @p`（与 BulkUpdateAsync 对齐）

**新增 BatchUpdateSqlBuilder**（`src/PalORM.Core/BatchUpdateSqlBuilder.cs`）：静态 SQL 构造器，与 DataSession 解耦降低认知复杂度。参数顺序对应 BindUpdate 输出：`[setCol0, setCol1, ..., pk]`（SET 列先，PK 在末尾）。

**与 BulkUpdateAsync 的语义差异**：
- BulkUpdateAsync 逐条执行 + 乐观锁检查（每行 affectedRows==1 否则 ConcurrencyConflictException）
- BulkUpdateBatchAsync 单语句批量 + 不区分"行不存在"与"并发修改"（返回受影响总行数）

### ✨ 诊断规则完整化（PALORM001-040）

**扩充 13 条新规则**（PALORM023-027, 031-033, 034-037, 040）：

- **P0 防运行时崩溃**（8 条）：
  - PALORM023/024：实体无可插入/可更新列（运行期 `throw`）
  - PALORM025：`[Timestamp]` 标在非时间类型（NOT NULL 无 DEFAULT 每次插入失败）
  - PALORM026：`[NotMapped]` 与映射特性互斥（避免 PALORM001 误报）
  - PALORM027：`[Converter]` 与 `[OwnedJson]` 互斥（消息比 PALORM015 更精准）
  - PALORM031：`BulkUpdateBatchAsync<T>` 对 `[ConcurrencyCheck]` 实体调用（必崩）
  - PALORM032：`Include/Join` 引用未注册实体（运行期 throw）
  - PALORM033：`Select(projection).ToListAsync()` 调用链（必崩）

- **P1 防静默错误**（5 条）——防止不 throw 但数据错/丢失/安全绕过：
  - PALORM034：`[Key]` 非默认初值让 SaveAsync 永远走 Update（数据静默丢失）
  - PALORM035：`[ConcurrencyCheck]+[IgnoreOnInsert]` 让乐观锁基线为 0（安全绕过）
  - PALORM036：`#nullable disable` 下引用类型不生成 IsDBNull 守卫（NULL 读取崩溃）
  - PALORM037：`[Required]` + 可空注解矛盾（DDL/读取行为不一致）
  - PALORM040：`[TenantAware]` 租户列可空（跨租户数据可见，多租户安全漏洞）

**修复 7 项现有规则缺陷**：

- F1：PALORM005 N+1 检测遗漏 Bulk/Save/Get 方法（功能遗漏）
- F2：PALORM002 消息"does not match table schema"语义错位（实际是"建议加 [Column]"）
- F3：PALORM017 对每个 `[ForeignKey]` 无条件报 Warning（过度报告，FK 在 Include 中有效）
- F4：PALORM015 消息"writable mapped properties"含义模糊（修订为原因清单）
- F5：PALORM012 类型限制未说明"emitter 用 ++ 自增"理由
- F6：PALORM003 跨程序集局限未在消息中提示可降级
- F7：PALORM010 无正例测试（补 `DoesNotReport`）

**精准化 1 条消息**：

- F8：PALORM009 补"partial sealed class"要求说明（STJ 源生成器约束）

### 🧪 验证

- dotnet build：0 错误
- Core.Tests: 174/174 通过
- SourceGen.Tests: 121/121 通过（原 104 + 新增 17 条诊断规则测试）
- Integration.Tests: 173/173 通过
- 总计：468/468 全部通过
- 环境：PG 18.4 + MySQL 8.4.10（MySQL local_infile=ON 部署约束）

---

## [4.6.0] — 极致性能（25+ 项分配优化）

> 基于 v4.0 实施后的深度性能审计与基准驱动迭代优化。
> 本版本为 **non-breaking**——公共 API 无破坏性变更。

### ⚡ 性能成果（vs v4.0 实测）

| 操作 | v4.0 分配 | v4.6 分配 | 改善 |
|------|:---------:|:---------:|:----:|
| GetByKey | 4.65 KB | **3.98 KB** | **-14%** |
| Insert | 5.16 KB | **4.97 KB** | **-4%** |
| Update | 8.31 KB | **7.44 KB** | **-10%** |
| BulkInsert 10K | 10.66 MB | **4.97 MB** | **-53%** |

BulkInsert 分配已优于 Dapper 62%（4.97MB vs 12.97MB）。

### v4.1：性能优化（IRowFactory 委托化 + Converter 单例 + 快照合并）

详见 v3.1/v4.0 CHANGELOG 条目。核心：IRowFactory → Func 委托、Converter static readonly 单例、CRUD Volatile.Read 合并。

### v4.2：极致降内存第一批（8 项）

- FormattableSqlFormatter 删除丢弃的 CompositeFormat.Parse + 改用 ValueStringBuilder
- QueryAsync 结果 List 起步容量 16
- InsertWithLastInsertId 预构建为 CommandSqlSet const
- QueryBuilder _clauses/_parameters 初始容量预分配 4/8
- 参数名预缓存 ParameterNameCache（@p0..@p1023 零分配索引取用）
- GetParametersForKinds 去 LINQ Contains + AsReadOnly
- 软删/租户过滤 SQL 片段 per-(Type,Dialect) 缓存
- From\<T\> 读连接工厂闭包提取为实例字段

### v4.3：ParameterNameCache public + probe 缓存跳过

- ParameterNameCache 提为 public，SourceGen binder emit 改用 GetName
- ProbeBinderAsync 结果缓存（InsertBinderValidated=true 跳过 probe）
- BulkInsert 10K 省 30K 次字符串插值

### v4.4：极致降内存第二批（8 项）

- BuildSql TrimEnd 先裁剪再 ToString（省 1 次 string 分配）
- CacheStore OTel counter 预构造 hit/miss KVP
- GridReader/StoredProcBuilder list 起步容量 16
- QueryClauseKinds 提为非泛型 static readonly（省临时数组）
- AppendSelectColumns sourceName quote 提循环外
- byte[] 列 GetValue → GetFieldValue<byte[]>
- BuildLimitClause 直接写 ValueStringBuilder

### v4.5：SessionOperationState TCS 延迟创建 + Exit 不写 AsyncLocal

- Enter 不创建 TCS，仅设 _isActive=true（省 88B/操作）
- Exit 不写 _currentOperationOwner.Value=null（省 ~300B EC 拷贝）
- Dispose/WaitForActive 条件创建 TCS

### v4.6：极致降内存第三批（6 项）

- owner=new object()→this + EC 相等短路（省 324B/操作）
- ExitTransactionFlow 不清 AsyncLocal（省 300B/事务收口）
- GetAsync 完整 SELECT SQL 缓存（省 120B/GetByKey）
- FormattableSqlFormatter 用 ParameterNameCache（省 32B/占位符）
- BulkInsert SqliteParameter 复用：新增 BindInsertValuesToBatch 旁路 binder，满批预分配参数池跨批复用（省 ~1.76MB/10K行）
- HasClause 位掩码 O(n)→O(1)（省 Predicate 委托分配/链式调用）

### 🧪 验证
- Core 161/161 + SourceGen 104/104 + Integration 160/160 = **425/425**
- 技术债扫描 12/12 + 门禁 G1-G29 全绿

---

## [4.0.0] — 性能与一致性收口（CommandFactory 单例 + Volatile 合并 + API 治理）

> 基于 v3.1 实施后的代码审查与 4 路并行深度调研产出，详见 `docs/v4.0-improvement-plan.md`。
> 本版本为 **non-breaking**——公共 API 无破坏性变更，仅 `DiffAsync` 标 `[Obsolete]`。

### ⚡ 核心优化

#### 优化 A：CommandFactory Converter 单例对齐（v3.1 最大遗留不对称）
- **问题**：v3.1 RowFactoryEmitter 已用 `static readonly _conv_<prop>` 单例，CommandFactoryEmitter 仍每次 `new Converter()`——同一项目内 emit 模式不对称
- **改动**：`CommandFactoryEmitter` 类级生成 `private static readonly IValueConverter<TClr,TProv> _conv_<prop> = new Converter();`
- **迁移路径**：BindInsert / BindUpdate / BindUpsert / BindDelete 全部从 `((IConverter)new Converter()).ToProvider(...)` 改为 `_conv_<prop>.ToProvider(...)`
- **收益**：百万行 BulkInsert 含 2 Converter 列省 ~200 万次 Gen0 分配
- **代码清理**：删除未使用的 `GetConverterInterfaceType` 方法

#### 优化 B：CRUD 路径 Volatile.Read 合并
- **问题**：v3.1 优化 3a 让 `From<T>()` 用了 `CurrentState` 单次快照，但 CRUD 路径（GetAsync / GetAllAsync / BulkDeleteAsync）未对齐
- **改动**：三处 `PalORM_Runtime.RowFactories/TableNames/ColumnNames` 独立访问 → 单次 `PalORM_Runtime.CurrentState` 快照
- **收益**：每次查询省 ~2 次内存屏障（fence）

#### 优化 D：List Capacity 起步优化
- **问题**：`ExecuteQueryAsync` 和 `GetAllAsync` 默认 `List<T>()` (=0)，10K 行场景扩容 14 次
- **改动**：默认 `new List<T>(16)` 起步
- **收益**：10K 行场景扩容次数从 14 降至 10

### 💔 API 治理（Breaking）

- **`DiffAsync<T>`** 标 `[Obsolete]`——本质是 `ValidateSchemaAsync<T>` 的字符串前缀薄包装
- **`GetRawConnection`** XML doc 强化「⚠️ 危险操作」警示（不重命名，避免破坏性）
- **`[Column].Length/Precision/Scale/TypeName/StoreAs`**：保留——已有 PALORM017 告警 + ITM-549 文档完整说明
- **`NamingConvention`**：保留——有实际 `ApplyNaming` 用途（自定义 SQL 场景归一标识符）
- **`WithMetrics(name)`**：保留——已有文档说明「名称仅保留 API 兼容」
- **`IRowFactory<T>`**：保留——v3.1 已决定作为公共契约

### 📚 AOT 文档补录

- **`docs/AOT部署指南.md`** 新增「聚合/标量查询 AOT 注意事项」小节
- 澄清 `Convert.ChangeType` 经评估确认 AOT 安全（走 IConvertible 接口分发）
- 非 IConvertible 类型（Guid/枚举/DateOnly）在 JIT 与 AOT 下行为一致

### 评估后跳过的方案项（B9 教训应用）

| 方案项 | 跳过原因 |
|--------|---------|
| 优化 C（QueryBuilder O(N²) 拷贝消除） | 构建时间占 QueryAll 4.73ms 的 0.02%，非瓶颈；重构将重新引入 QUERY-001 |
| 预构建 SQL（QuotedColumnList） | emit 三方言版本复杂度高，9-30KB 额外生成代码换取 ~150ns/查询 |
| 源生成器 emit 工程化（S2/S3/S4/S5） | S2 跨版本不稳定；S3 破坏快照基线；S4 实际仍被 MigrateAsync 使用；S5 增加状态参数 |
| AddRange 参数批处理 | ADO.NET Provider 行为不一致，风险高于收益 |
| Tracing/Metrics 短路 | 已是 const string + intern，零分配 |

### 🧪 验证

- 构建：0 警告 0 错误
- Core Tests：156/156 全绿
- SourceGen Tests：104/104 全绿（含快照重生成）
- 技术债扫描：12/12 通过

---

## [3.1.0] — 性能优化（IRowFactory 委托化 + Converter 单例 + 快照合并）

> 基于 v3.0.0 真实场景基准数据的深度优化，详见 `docs/v3.1-performance-plan.md`。

### ⚡ 性能成果（vs v3.0.0）

| 操作 | v3.0.0 | v3.1 | vs ADO.NET | 改善 |
|------|:---:|:---:|:---:|:---:|
| QueryAll 10K | 6.9ms (177%) | **5.35ms (132%)** | 4.05ms (100%) | -22% 时间 |
| GetByKey | 65μs (232%) | **26μs (141%)** | 18.4μs (100%) | **-60% 时间** |

### 优化 1：IRowFactory&lt;T&gt; → Func&lt;DbDataReader, T&gt; 委托（核心）
- **源生成器 emit 重写**：`sealed class : IRowFactory<T>` → `internal static class + static readonly Func<DbDataReader, T> Read` 委托字段
- **注册方式变更**：`RowFactories[type] = RowFactory_X.Instance` → `RowFactories[type] = RowFactory_X.Read`（委托装箱为 object）
- **所有调用点迁移**：QueryBuilder._factory、DataSession.Crud/Query、GridReader、StoredProcBuilder 共 9 处 `(IRowFactory<T>)factory).Read(reader)` → `((Func<DbDataReader, T>)factory)(reader)`
- **原理**：接口虚分发（vtable 查找 + 间接跳转）→ 委托直接 invoke（.NET 8+ JIT 对 static delegate invoke 有更好内联支持）

### 优化 2：Converter 单例缓存
- **RowFactoryEmitter emit**：每个 `[Converter]` 列从"每次 Read `new Converter()`"改为类级 `private static readonly IValueConverter<TClr,TProv> _conv_<prop> = new Converter();`
- **收益**：带 Converter 的实体每行每列省一次 Gen0 分配 + GC 压力
- **NRT 抚慰**：lambda 内 `_conv_X!.FromProvider(...)` 加 `!` 告知分析器字段已完成初始化

### 优化 3：state 快照合并 + Stopwatch 延迟 + 拦截器空跳过
- **3a：合并 Volatile.Read**：`From<T>()` 内 3 次 `PalORM_Runtime.RowFactories/TableNames/ColumnNames` 各自 `Volatile.Read` 合并为单次 `PalORM_Runtime.CurrentState` 快照（属性公开为 `internal static`）
- **3b：Stopwatch 延迟创建**：`ExecuteQueryAsync` / `ExecuteNonQueryAsync` 中 `Stopwatch.StartNew()` 改为仅在 Tracing/Metrics/拦截器任一启用时分配；热路径默认配置省一次 StartNew + Stop
- **3c：拦截器空列表跳过**：`foreach (interceptor) OnBefore/OnAfter/OnError` 加 `if (interceptors.Count == 0) return` 守卫；默认会话无拦截器时省迭代开销
- **抽取辅助方法**：`NotifyInterceptorsOnBefore/OnAfter` 共享于 SELECT/UPDATE 管线，降低认知复杂度

### 📦 文件变更
- `src/PalORM.Core/IRowFactory.cs` — 接口保留（向后兼容），XML 注释更新说明迁移
- `src/PalORM.Core/PalORM_Runtime.cs` — `RuntimeRegistryState` 从 private → internal，新增 `CurrentState` 属性
- `src/PalORM.Core/QueryBuilder.cs` — `_factory` 字段 + `QueryBuilderServices.Factory` 类型 `IRowFactory<T>` → `Func<DbDataReader, T>`
- `src/PalORM.Core/QueryBuilderExtensions.cs` — Stopwatch 延迟 + 拦截器辅助方法 + 调用点迁移
- `src/PalORM.Core/DataSession.Crud.cs` — `From<T>()` 快照合并 + 4 处 cast 迁移
- `src/PalORM.Core/DataSession.Query.cs` — 2 处 cast 迁移
- `src/PalORM.Core/GridReader.cs` — 2 处 cast 迁移
- `src/PalORM.Core/StoredProcBuilder.cs` — 1 处 cast 迁移
- `src/PalORM.SourceGen/RowFactoryEmitter.cs` — emit 重写
- `src/PalORM.SourceGen/RegistryEmitter.cs` — 注册值从 Instance → Read

### 🧪 验证
- Core Tests: 156/156 全绿
- SourceGen Tests: 104/104 全绿（5 个快照基线重生成 + 评审通过）
- SQLite Integration Tests: 149/149 全绿（7 个 PG/MySQL 环境变量失败与本次无关）
- 技术债扫描: 12/12 全通过
- 基准对比: QueryAll 177%→132%、GetByKey 232%→141%

## [3.0.0] — Breaking Changes（架构精炼 + Breaking API 移除 + 质量增值）

### 💔 Breaking Changes
- **移除 DataSession.ForRead() / ForWrite()**：请使用 `From<T>().ForRead()` / `From<T>().ForWrite()`
- **移除 CrudMetadata 旧 9 参 ctor**：请使用聚合 ctor（CrudBindings + CrudColumns）
- **移除 QueryBuilder.ThenInclude&lt;TGrandChild&gt;(单参)**：请使用双参 `ThenInclude<TGrandChild, TParent>(grandChildKey, parentKey)`

### ✨ 架构精炼
- **删除 8 个 Obsolete 公共 API**：MinimumLogLevel / ParameterPrefix / CreateConnection 单参 / GetLimitOffsetClause / LogQuery / RecordQueryStart / RecordQueryDuration / QueryBuilder 14 参 ctor
- **合并 TypeMapperEmitter → RowFactoryEmitter**：DateTimeOffset 读取直接内联 `GetFieldValue<T>`
- **PalORM_Runtime 拆 3 文件**：EntityFeatures.cs / SqlSets.cs / CrudMetadata.cs
- **Resilience 拆 CircuitBreaker + Exceptions**：熔断状态机独立为 `internal sealed class CircuitBreaker`
- **DataSession God Object 拆 4 partial**：Crud.cs / Query.cs / Transactions.cs / Schema.cs（1597→6 文件）
- **PgNotificationListener partial 拆分**：NpgsqlNotificationConnection.cs + PgNotificationEventArgs.cs
- **抽取 BulkOperationFramework**：消除 MultiValueBulkInsert/PostgreSqlProvider 间的 probe+cleanup 重复
- **ColumnModel 瘦身**：删除 4 个恒 null 预留字段（Length/Precision/Scale/DefaultExpression）
- **EquatableArray 独立文件**：从 TableModel.cs 提取

### 🧪 质量增值
- **集成 SonarAnalyzer.CSharp 10.29.0**：CI 守护层——P0 安全 + P1 设计规则全部为 error
- **BulkOperationFramework**：三 Provider 共享的 probe + cleanup 骨架
- **测试配置双层覆盖**：appsettings.test.json + .env.test + TestEnvironment 读取器
- **测试 helper 集中化**：TestInterceptors.cs（CountingTestInterceptor + CallbackTestInterceptor + OrderedInterceptor）
- **测试方法拆行**：AdvancedTests + QueryTests + FinalTests + MultiEntityTests 单行→多行
- **PALORM006/007 占位诊断删除**：零报告描述符移除
- **P1-2 规则升级为 error**：S3776/S107/S927/S2681/S125/S1066/S1994/S2189

### 📚 AI 系统
- **`.ai/lessons.md` v6.0**：14 个缺陷 + SOP + 决策矩阵 + 技术债扫描 SOP（自包含手册）
- **PR 模板**：编译/测试/Sonar/三方一致/精炼守护/反模式预防 6 类清单
- **`docs/编码规范.md` 第 18 节**：SonarAnalyzer 守护层规则配置

## [2.0.1] — 2026-07-15
- 初始发布
- Core + 3 Provider（SQLite/PostgreSQL/MySQL）+ SourceGen + Testing
- 面向严格 Native AOT（IsAotCompatible + IsTrimmable）
- 源生成器：RowFactory / CommandFactory / Migration / Registry / SqlFile / SqlTemplate
- 编译期诊断：PALORM001-040（33 条，含 P0 防崩溃 + P1 防静默错误 + 调用级 API 误用）
- 三方言支持：SQLite / PostgreSQL / MySQL
- 弹性执行器：重试 + 退避 + 超时 + 熔断
- 批量操作：MultiValue INSERT / PG Binary COPY
- 查询 DSL：Where/OrderBy/Take/Skip/Include/Join/CTE/Window/Cache
- 软删除 + 多租户 + 乐观锁
- PG NOTIFY/LISTEN
