1	# CrossBench — 五臂交叉基准（ADO.NET / Dapper / RepoDB / FreeSql / PalORM）
2	
3	> **定位**：**事件驱动的 ORM 横向对比工具**（三层性能系统的第二层）。三方言 × 两档 × 17 操作
4	> × 五臂，测量引擎、数据集、三臂实现与 [PalORM.PerfHub](../perfhub/README.md) 单一事实源
5	>（链接编译），本工程只新增 RepoDB 臂与 Runner。2026-10-26 起入库并纳入 CI（workflow_dispatch）。
6	>
7	> **职责边界**：PalORM 自身回归检测**不用本工具**——PerfHub 与其共码三臂已覆盖；触发面：①五臂横评主张（发版/对比声明）②形态断点（ADR 级变更后基线重开）；③2026-10-26 用户指令起随 `PerfCli full` 默认执行（新步骤"CrossBench 五臂交叉批"，统一报告自动追加 RepoDB 数据臂——数据源按完整批自动判别，五臂×三方言×两档 = 30 三元组；`PerfCli crossbench`可独立触发），事件触发语义不变。
8	>
9	> **RepoDB v1.17+ 集成教训（来自 FreeSql 经验）**：
	> **跨库横评扩展（2026-10-30 立项；2026-11-01 按用户指令移除 Insight.Database 与 SmartSql——两者长期构造失败只产出 N/A 行）**：清单 = PalORM/Dapper.AOT/RepoDb/FreeSql/EF Core/linq2db/SqlSugar（7 库）+ Dapper + ADO 地板。各臂见 *Arm.cs（每臂头部注释含形态决策与方言边界）；--arms 令牌惰性构造（不测的臂不付静态初始化）；--dialects litedb 伪方言（快照机制跳过、臂自管状态、Prepare 重定向为重播种）。
	> **N/A 账本**：2026-11-01 起清零——SqlSugar PG 曾因「小写引号标识符 vs CamelCase 引号 DDL」N/A，改走独立小写表 perf_s1_lc（列名不加引号，PG 折叠后命中），映射固定共享名防静态缓存跨方言泄漏（SqlSugar 实体映射按类型进程级缓存，探针实证），每操作经 .AS()/As() 覆写指向组目标表（五条写路径 + 查询逐路径探针实证）。报告走 scripts/crossbench-12arm-report.cs（七节结构，数字全部当批推导）。
	>
10	> - 实体映射走 DynamicMapping（零特性，FieldMapper 静态缓存预热）——避免运行时反射
11	> - 批量操作自动路由驱动级 API（MySQL LOAD DATA/PG COPY/SQLite 行预编译）——与 ADO 地板同路径
12	> - 事务传 DbTransaction 参数给 Repository 方法（无需 WithTransaction 包装）
13	> - AOT 兼容需 TrimmerRootAssembly（用户需配置），本夹具显式预热映射表防首次访问锁竞争
14	> - SQLite PRAGMA 独立连接（池连接无法注入 PRAGMA，与 FreeSql 同坑）

## 运行

```bash
# 全量基线批（全臂全预算，~20 min，2026-10-26 实测；需 .env.test 的 PG/MySQL 凭据）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects sqlite,mysql,pg 2000 20000

# 臂子集（断点后快速复核，~12 min：两臂=一半测量点）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --arms ado,palorm --dialects sqlite,mysql,pg 2000 20000

# RepoDB 单独测试（冒烟模式）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --fast --arms repodb --dialects sqlite,mysql,pg 2000 --ops GetByKey,GetAllAsync,BulkInsert,UpsertBatch

# 冒烟（--fast 全局预算 ×0.4 + --ops 子集，分钟级；冒烟批不进基线）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --fast --dialects sqlite 2000 --ops BulkInsert,UpsertBatch

# 子集（--dialects / --ops / 档位数字均可缩窄；臂名：ado、dapper、repodb、freesql、palorm）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects sqlite 2000 --ops BulkInsert,UpsertBatch

# 报告 / 基线（脚本白名单制：文件式 C#）
dotnet run --file scripts/crossbench-report.cs -- <batch.json> --out report.md
dotnet run --file scripts/crossbench-report.cs -- --baseline <b1.json,b2.json,b3.json> --out baseline.md
```

结果 JSON 落 `bench/crossbench/results/crossbench-*.json`（入库）；环境变量与 PerfHub
同口径（`PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION`，`.env.test` 兜底）。

## SQL 文本审计（`--sql-audit`，不参与计时）

审计对象是「每组是否用自身 ORM API 生成 SQL」：逐 (方言 × 操作 × 组) 只执行一次，记录**实际执行过的
SQL 文本**，同方言内做归一化比较（标识符引号剥离、字符串/数字字面量与参数占位统一为 `?`、空白折叠；
标识符整体保留，`perf_s1`/`INT8` 不被数字规则拆开）。归一化后文本相同，要么是共用同一 SQL 真源
（手写模板或同一驱动路径），要么是各自生成了同形 SQL——报告把同文本簇并列展示供判读；单组独有 +
形态符合该库惯用写法，则是「确实在用自身 API」的直接证据。审计不改写 SQL、不动参数与事务；但连接包装层
会与「按 provider 具体类型强转」的路径冲突，这类路径登记为通道限制（见下表），不当作操作失败。
审计批不写 `crossbench-*.json`，不进基线。

```bash
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --sql-audit --dialects sqlite,mysql,pg 2000
# 产物（均入库）：results/sql-audit-<stamp>.json（原始语句）+ results/sql-audit-<stamp>.md（方法/矩阵+变体/未捕获）
```

捕获通道与已知边界（2026-11-02 审计批实测，报告第一节逐条列明）：

| 组 | 通道 | 边界 |
|---|---|---|
| ADO / Dapper / Dapper.AOT / PalORM | 宿主连接包装层（`DbCommand.CommandText` 赋值即记录） | Dapper.AOT 的三条事务内操作（upstream `Command<T>` 校验 `transaction.Connection == connection`）不可包装；ADO 的 PG/MySQL 批量插入与 PalORM 的 PG 批量插入按具体驱动连接强转，也不可包装（PalORM 的 MySQL 批量在 `local_infile` 关闭时回退多值 INSERT，可捕获） |
| FreeSql | `Aop.CommandAfter` | 批量插入走 provider 私有通道（`ExecuteSqliteBulkInsert` / `ExecuteMySqlBulkCopy` / `ExecutePgCopy`），无语句文本 |
| SqlSugar | `Aop.OnLogExecuting`（**每次操作前重装**——Aop 挂在 provider 实例上，长批次里会被内部重建） | `Fastest`/BulkCopy 路径只留库自有日志（`Begin BulkCopy…` / `Begin BulkUpdate…`，矩阵标「非 SQL 日志」） |
| EF Core | `LogTo` 过滤 `CommandExecuted` 事件 | 回调消息含 `info:` 日志头与参数行，SQL 取 `Executed DbCommand` 行之后 |
| linq2db | `LastQuery` | 只保留最后一条语句（分块批删/批改只呈现末块形态） |
| RepoDB | 无可用通道 | 连接包装层被 provider 类型解析拒绝（MissingMappingException）；全局 `ITrace` 对异步 API 在 1.17.0 上对 null Task 执行 await，必抛 NRE |

## 测量协议（与 PerfHub 对齐部分不再重复，此处只列交叉特有）

| 项 | 值 | 说明 |
|---|---|---|
| 臂序 | 每操作两遍反序（p0：A,D,R,F,P；p1 反序），聚合取两遍中位 | 消除臂顺序相位；**不可减为单遍**（MySQL 读族臂序相位 1.55× 事件在案） |
| 慢操作预算表 | Transaction 族与 Bulk 三项（Insert/Update/Upsert）计时窗 1.5→0.6s、预热 ×0.4（2026-10-25 A/A 验证与基线 v3 逐格噪声带一致） | 快操作不受影响（迭代数在 maxIterations 钳制处收敛，预算从不生效）；中位稳定性由 fsync/服务端主导（批内 err 普遍 <2%） |
| RepoDB 臂 | FluentMapper 零特性映射 + 枚举 PropertyHandler；批量按操作取优：Insert/Update/Merge 走官方 `*.BulkOperations`（MySQL LOAD DATA / PG COPY，经 pseudo 暂存表），Delete 走 Core `DeleteAll`（IN），SQLite 全部走 Core | 详见 RepoDbArm.cs 注释与 ADR-S |
| RepoDB 跨方言隔离 | 建臂时清空七个全局缓存（`CommandTextCache` + 六个 `ExecutionContextCache`）——RepoDB 缓存键不含 provider，同进程混用多方言会让后跑的方言命中先到方言的 SQL 与参数绑定委托（实测 MySQL 拿到 SQLite 的方括号 SQL 报语法错、`MySqlParameter` 强转 `SqliteParameter` 抛 InvalidCastException） | ADR-S D6；单方言冒烟不复现，**全量批必查** |
| FreeSql 臂 | Fluent `ConfigEntity` 原生实体、`UseNoneCommandParameter(true)`、provider 批量 API；读走 `Select<T>` 表达式管线（ORM 层口径） | 详见 arm 内注释与 ADR-Q/R |
| 读正确性守卫 | 每方言一次非计时校验（值逐字段 + 计数）；`--arms` 缺 FreeSql/RepoDb 时自动跳过 | 防映射错位 |

## 基线纪律

- **B100**：升格基线需 ≥3 个完整批（全臂全预算，三批窗口中位数）；地板批间散布 >20% 的格标 ⚠。
- **例行 = 单批对冻结基线**（不重跑三批窗口）：断点或横评主张时跑一个全批，与冻结基线
  逐格噪声带（±8pp）比对；臂子集批与 `--fast` 批**不进基线**。
- **报告模板已冻结**（2026-10-25 用户指令）：所有性能数据一律经 `scripts/crossbench-report.cs` 输出；
  模板内每个倍数、计数与"最快/最差"标签由当批 JSON 推导，总览与说明禁止硬编码数字（说明列只写定性机制）。
  调整展示先改模板脚本再重生成，不手改生成物。
- 已知形态断点：2026-10-25（SQLite 批插/批删/批 upsert 四臂同批切换 + Dapper TxBulkInsert
  单格 d759346），断点前批的对应格不并入窗口（选批时剔除对应 JSON）。
- 探针工具：`UpsertProbe`（A+B 每行成本）、`DeleteProbe`（json_each 每键成本）、
  `EnumProbe`（事务内逐行写会话机械分解）——均为对应 ADR 的证据件。

## 目录

| 路径 | 内容 |
|---|---|
| `PalormProtocol/` | Runner + RepoDB/FreeSql 臂（链接 PerfHub 引擎与三臂） |
| `UpsertProbe/` / `DeleteProbe/` / `EnumProbe/` | 形态每行成本与会话机械探针（独立小工程） |
| `results/` | 批 JSON 与基线报告（入库） |
| `scripts/crossbench-report.cs`（仓根） | 单批报告 / 多批基线生成 |
