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

### D2 连接：入口先解包 `CountingConnection`

RepoDB 的 DbSetting / DbHelper / StatementBuilder **按连接运行时类型查表**。Measure 传入的是
`CountingConnection` 装饰器，直接使用会让 provider 注册全部落空（实测 PG BulkUpdate 因取不到
主键字段抛 NRE）。故每个方法入口 `CountingConnection.Unwrap(conn)` 到驱动连接再用；BulkOperations
本来也要求 `MySqlConnection` / `NpgsqlConnection` 具体类型，与 ADO 地板的 COPY/BulkCopy 同法。

### D3 批量：按操作取优，不统一押注扩展包

RepoDB 的批量有两条互斥路径：Core 的多值语句（`InsertAll`/`UpdateAll`/`DeleteAll`/`MergeAll`）与
官方 `*.BulkOperations` 扩展包（MySqlBulkCopy=LOAD DATA / NpgsqlBinaryImporter=COPY，均经 pseudo
暂存表）。同批 A/B 实测（2000 行，MySQL，两遍）：

| 操作 | Core | BulkOperations | 取 |
|---|---:|---:|---|
| BulkInsert | 17.3~18.3 ms | **11.3~11.7 ms** | BulkOperations |
| BulkUpdate | 368.9~370.1 ms | **160.9~168.1 ms** | BulkOperations |
| BulkDelete | **26.2~33.1 ms** | 150.7~156.8 ms | Core |
| UpsertBatch | 765.3~788.4 ms | **152.5~158.9 ms** | BulkOperations |

结论：删除用 IN 列不占暂存表便宜（pseudo 表三次往返 + DDL 是纯开销），其余三项扩展包明显更快。
SQLite 无 BulkOperations 包，全部走 Core；批宽按 `SqLiteDbSetting.MaxParameterCount=999 ÷ 5 列 = 199`，
与 ADO 地板 `BulkSql.EffectiveBatchRows` 同口径。MySQL/PG 的 Core 批宽用 `BulkSql.BatchRows=1000`。

### D4 PG 的 BulkUpdate/BulkMerge 停用（上游缺陷，非度量选择）

RepoDB 1.17.0 的 PG BulkOperations 在**目标表的键列不是 identity** 时，
`GetUpdateCommandText` / `GetMergeCommandText` 内 `identityField.GetHashCode()` 未判空 →
`NullReferenceException`（源码 `NpgsqlText.cs:938` / 对应 Merge 分支）。perf_s1 的 `Id` 是显式
主键（`[Key(AutoIncrement=false)]`），映射层无法规避（标 Identity 会让 INSERT 跳过 Id 列）。
故 PG 的 BulkUpdate / UpsertBatch 退回 Core（`UpdateAll` / `MergeAll`），并在臂内注释记录原因。
这是被测库的真实能力边界，不构成对 RepoDB 的"手写 SQL 替代"。

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
