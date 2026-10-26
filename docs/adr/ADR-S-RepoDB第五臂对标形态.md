# ADR-S：CrossBench 第五臂（RepoDB 1.17.0）的对标形态与路径选择

> 状态：**已采纳（2026-10-26 用户指令「增加 RepoDB 最新版的对比测试，符合 RepoDB 最佳实践，测试 ORM 不是测试 SQL」）** · 来源：RepoDB 1.17.0 源码审读（tag v1.17.0）+ 同批 A/B 实测
> 关联：`bench/crossbench/PalormProtocol/RepoDbArm.cs` · `scripts/crossbench-report.cs`（五臂模板） · `tools/PalORM.PerfGate/TableGen.cs`（17 列四组表） · ADR-Q / ADR-R（批量形态断点）

## 背景

CrossBench 自 2026-10-25 起为四臂（ADO.NET 地板 / Dapper / FreeSql / PalORM）。用户要求加入
RepoDB 最新版作第五臂，并明确两条判据：**生成的代码要符合 RepoDB 最佳实践**、**要测试 ORM
不是测试 SQL**。RepoDB 与 FreeSql 的集成教训（Fluent 映射、驱动级批量、原生实体物化、
读正确性守卫）在本次直接复用。

## 决策

### D1 映射：FluentMapper 零特性（与 FreeSql `ConfigEntity` 同法）

S1Row/EnumRow 携带 PalORM 特性（`[Key]`/`[Column]`），RepoDB 不可见。用
`FluentMapper.Entity<S1Row>().Table("perf_s1").Primary(e => e.Id)` 与
`FluentMapper.Entity<EnumRow>().Table("perf_enum").Primary(...).PropertyHandler<OrderStatusStringHandler>(e => e.Status)`
接管映射。枚举列显式挂 `IPropertyHandler<string, OrderStatus>`：`perf_enum.Status` 三方言均为
TEXT，而 RepoDB 的 `EnumDefaultDatabaseType`（默认 String）只设 DbType、不做值转换。

### D2 连接：用共享连接，`Unwrap` 只作防御

RepoDB 的 DbSetting/DbHelper/StatementBuilder 按连接运行时类型查表，因此本臂入口调用
`CountingConnection.Unwrap`。**审计更正（2026-10-26 复核）**：CrossBench 的 `Measure.SingleAsync`
传入的是裸驱动连接（包装只发生在 PerfHub 的并行测量路径），所以该调用在交叉批上是直通，不是
任何问题的解。保留它的唯一理由是 BulkOperations 的 CAST（`MySqlConnection` / `NpgsqlConnection`）
要求具体类型。**MySQL 方括号 SQL 与 `MySqlParameter` 强转的真实解因是 D6 的缓存清理，不是解包。**

### D3 批量：按操作取优，不统一押注扩展包

RepoDB 的批量有两条互斥路径：Core 的多值语句（`InsertAll`/`UpdateAll`/`DeleteAll`/`MergeAll`）与
官方 `*.BulkOperations` 扩展包（MySqlBulkCopy=LOAD DATA / NpgsqlBinaryImporter=COPY，均经 pseudo
暂存表）。同批 A/B 实测（2000 行，MySQL）：

| 操作 | Core | BulkOperations | 取 |
|---|---:|---:|---|
| BulkInsert | 17.3~18.3 ms | **11.3~11.7 ms** | BulkOperations |
| BulkUpdate | 368.9~387.3 ms | **153~160 ms** | BulkOperations |
| BulkDelete | **26.7~42.6 ms** | 150.7~157.1 ms | Core |
| UpsertBatch | 765.3~1087 ms | **152.5~159.1 ms** | BulkOperations |

（异步 API 与同步探针两套口径并列，结论一致。）删除用 IN 列不占暂存表便宜（pseudo 表三次往返 +
DDL 是纯开销），其余三项扩展包明显更快。MySQL 的 pseudo 表类型再实测：`Auto`（2000 行落到 Memory
临时表）与 `Physical` 在 BulkUpdate 上是 158.0 对 152.9 ms（3% 差在噪声带内），BulkMerge 是
199.4 对 159.1 ms（Auto 样本散布 151~252 更大）；保持官方默认 `Auto`。

SQLite 无 BulkOperations 包，全部走 Core；批宽按 `SqLiteDbSetting.MaxParameterCount=999 ÷ 5 列 = 199`，
与 ADO 地板 `BulkSql.EffectiveBatchRows` 同口径。MySQL/PG 的 Core 批宽 1000 / 419（PG 的
`MaxParameterCount=2098` ÷ 5，避免内部再拆）。

### D4 PG 的 BulkUpdate/BulkMerge 停用（上游缺陷，非度量选择）

RepoDB 1.17.0 的 PG BulkOperations 在**目标表的键列不是 identity** 时，
`GetUpdateCommandText` / `GetMergeCommandText` 内 `identityField.GetHashCode()` 未判空 →
`NullReferenceException`（源码 `NpgsqlText.cs:938` / 对应 Merge 分支）。perf_s1 的 `Id` 是显式
主键（`[Key(AutoIncrement=false)]`），映射层无法规避（标 Identity 会让 INSERT 跳过 Id 列）。

**已排除的替代解释（干净进程探针复核）**：新建 `perf_s1_probe` 表、裸 `NpgsqlConnection`、
`DbFieldCache.GetPrimary()=Id` / `GetIdentity()=null`、先 `InsertAll` 2000 行成功，随后
`BulkUpdate` 与 `BulkMerge` 仍各自 NRE；`BinaryBulkUpdate` / `BinaryBulkMerge` 在源码里已标
`[Obsolete]` 且内部委托给同一 `BulkUpdate`/`BulkMerge`，无绕行路径。故 PG 这两项退回 Core
（`UpdateAll` / `MergeAll`），并在臂内注释记录原因。

### D7 读路径分配与形态对齐（审计修正，2026-10-26）

初版读路径有三处与本臂最优形态有差，已修：

| 位置 | 初版 | 修正 | 依据（同表同连接实测中位） |
|---|---|---|---|
| GetByKey | `QueryAsync<T,TWhat>` + `FirstOrDefault` | → `QueryFirstAsync<T,TWhat>`（去 List 聚合） | 前者内部先聚成 List 再取首元素；Dapper 臂用 `QueryFirstOrDefaultAsync` 直接返回 T |
| GetByKey（进一步） | `QueryFirstAsync<T,TWhat>(id)` key 直传 | → `QueryFirstAsync<T>(new QueryField("Id", id))` | key 直传要先解析主键字段名（PrimaryCache + PropertyCache 查找 + 主键守卫）；显式 QueryField 跳过。**同批交替 A/B（1200 次、A/B 交替取中位，消除顺序效应）**：SQLite −7.7%，MySQL −0.6%，PG +0.8%——SQLite 稳定更优，联网方言等价 |
| QueryAll / GetAllAsync | `[.. rows]` | `rows as List<S1Row> ?? [.. rows]` | RepoDB 的 `QueryAllAsync` 内部已是 List，再复制一遍纯浪费；Dapper 臂用 `AsList()` 零复制 |

基准批实测（SQLite 2000）：QueryAll 分配 388KB → 381KB；GetByKey 5.15KB → 4.68KB、时延
27.5µs → 25.8µs。`BuildGetByKeySql` / `BuildComplexQuerySql` 原返回空串（静默假值），一并改为
显式 `NotSupportedException`——CrossBench 操作集不含 Build 组，与 FreeSql 臂的未覆盖项同口径。

结果之外，基准确立一条判读纪律并据此复核：MySQL 的 `GetByKey` 在修正前后两批里从 201.3µs 抬到
230.7µs（R/ADO 1.14），但同批其它四臂漂移只有 ±2.6%，复测（独立批）RepoDb 稳定 230µs 而 ADO
地板本身摆 203→221µs——**属分母漂移，不是形态回归**；交替 A/B 进一步确认两形态在联网方言等价。
判读顺序仍为「分母漂移 × 噪声带 × 真 FAIL」。

已核对无需改的项：映射（FluentMapper + Primary，不设 Identity 正确——Id 是显式值）、
`identityBehavior` 默认 `KeepIdentity`（显式 Id 得以保留）、异步 API 为真 async（非 Task.Run 包装）、
`DeleteAll` 内部按 `MaxParameterCount` 分批且超限自动开事务（与 ADO 地板同形态）、
MySQL 的 pseudo 表类型保持官方默认 `Auto`（Physical 在 BulkUpdate 上只快 3%，落在噪声带内）。

### 已知边界

- **单批尖峰须复测**：审计轮的第三个完整批（`crossbench-20261026-112047.json`）里 `PG Insert 2000`
  出现 26.7ms（R/ADO 19.87），而同一操作 20000 档只有 1.42ms（大档反而快 19 倍即异常），该格
  ErrorRatio 8.9% 也显著高于同批其它格（1~2%）。独立复测（`--ops Insert` 两档）得 1436~1500µs，
  与地板 1416~1421 持平。判为单批尖峰，按 B100 以三批窗口中位吸收。
- **写后映射校验只覆盖 RepoDb 臂**（BulkUpdate + UpsertBatch 两条写路径，改值 → 读回逐字段比对
  → 快照复原）；其余四臂只有读校验，写后校验是历史批次遗留缺口。
- **枚举 PropertyHandler** 每行读写各一次转换调用；RepoDB 无类型级 enum→string 的等价配置
  （`EnumDefaultDatabaseType` 只给 DbType 不转值），当前形态与 FreeSql 的 `MapType(typeof(string))`
  同为"逐行转换"，口径一致。
- **跨方言缓存清理**（D6）是基准夹具的隔离措施，不是产品用法；真实应用一进程一方言，不会触发。
- MySQL 的 `BulkInsert` 走 `MySqlBulkCopy`（LOAD DATA），连接串必须带 `AllowLoadLocalInfile`，
  由 Runner 的 `MySqlConnString.WithLocalInfile` 统一追加，四臂同口径。

### D5 事务与读路径

事务直接传 `IDbTransaction` 给扩展方法（BulkOperations 要求 provider 具体事务类型），无需 FreeSql
那样的 `WithTransaction` 绑定。读路径用 `QueryAsync<T>` / `QueryAllAsync<T>` 走表达式解释器
（编译期构建行物化委托），物化原生实体而非 DTO。每方言一次读映射校验（值逐字段 + 计数），
与 FreeSql 守卫同构。

### D6 每方言清空 RepoDB 的七个全局缓存（跨方言缓存键缺陷的规避）

RepoDB 的全局缓存键里**不含 connection / provider**，假定"一进程一方言"。单进程混用多方言时：

| 缓存 | 键构成 | 实测症状 |
|---|---|---|
| `CommandTextCache` | QueryRequest 类型 + 表名 + 字段 + 条件 + 排序 + Top + Hints | SQLite 先跑后，MySQL 的 `GetByKey` 拿到 `SELECT [Id], ... FROM [perf_s1] WHERE ([Id] = 1)`（SQLite 方括号转义）→ MySQL 语法错 |
| 六个 `ExecutionContextCache`（Insert / InsertAll / Update / UpdateAll / Merge / MergeAll） | 实体类型 + 字段集（不含 provider） | 编译好的参数绑定委托里嵌了 SQLite 的 `DbHelper.DynamicHandler`，MySQL 复用时 `MySqlParameter` 被强转 `SqliteParameter` → InvalidCastException（单行 Update 处炸） |

规避：`RepoDbArm` 构造时（Runner 每方言只创建一个实例，正是方言边界）清空
`CommandTextCache` + 全部六个 `ExecutionContextCache`。漏清任一缓存就漏一条执行路径——
这是第二轮全量批失败（清前四个后仍炸在 Update）的直接教训。

## 后果

- RepoDB 的数据进入三处产物：CrossBench 五臂报告（`scripts/crossbench-report.cs`）、PerfGate 四组表
  17 列（`TableGen.cs`，含 P/RepoDb、分配 R 段、分配相对 ADO（R））、统一报告 CrossBench 节
  （`PerfCli`）。完整批判据升为 30 三元组（五臂 × 三方言 × 两档）。
- 性能画像（PG 2000 档同批）：单行与读 0.93~1.17×（与 ADO/Dapper 同量级）；批量 DML 显著落后——
  BulkUpdate 7.65×、BulkDelete 5.44×、UpsertBatch 1.52×。这是 RepoDB 1.17.0 在"每 2000 行一批"
  形状下的真实表现，报告按实测呈现，不做形态美化。
- 该臂不进 PalORM 回归门禁（CrossBench 仍是事件驱动横评工具），只参与横评主张与断点复核。

## 复现

```bash
# 五臂全量批（三方言 × 两档，约 20 min）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects sqlite,mysql,pg 2000 20000
# 单臂冒烟
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --fast --arms repodb --dialects sqlite 2000
```
