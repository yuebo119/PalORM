# CrossBench — 四臂交叉基准（ADO.NET / Dapper / FreeSql / PalORM）

> 定位：**事件驱动的 ORM 横向对比工具**（三层性能系统的第二层）。三方言 × 两档 × 17 操作
> × 四臂，测量引擎、数据集、三臂实现与 [PalORM.PerfHub](../perfhub/README.md) 单一事实源
>（链接编译），本工程只新增 FreeSql 臂与 Runner。2026-10-25 起入库并纳入 CI（workflow_dispatch）。
>
> **职责边界（2026-10-25 时间优化轮明文化；同日用户指令起纳入默认全量）**：PalORM 自身回归检测
> **不用本工具**——PerfHub 与其共码三臂已覆盖；触发面：①四臂横评主张（发版/对比声明）②形态
> 断点（ADR 级变更后基线重开）；③2026-10-25 用户指令起随 `PerfCli full` 默认执行（新步骤
> "CrossBench 四臂交叉批"，统一报告自动追加 FreeSql 四臂节——数据源按完整批自动判别，
> 四臂 × 三方言 × 两档 = 24 三元组；`PerfCli crossbench` 可独立触发），事件触发语义不变。

## 运行

```bash
# 全量基线批（全臂全预算，~14 min，2026-10-25 实测；需 .env.test 的 PG/MySQL 凭据）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects sqlite,mysql,pg 2000 20000

# 臂子集（断点后快速复核，~7 min：两臂=一半测量点）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --arms ado,palorm --dialects sqlite,mysql,pg 2000 20000

# 冒烟（--fast 全局预算 ×0.4 + --ops 子集，分钟级；冒烟批不进基线）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --fast --dialects sqlite 2000 --ops BulkInsert,UpsertBatch

# 子集（--dialects / --ops / 档位数字均可缩窄；臂名：ado、dapper、freesql、palorm）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects sqlite 2000 --ops BulkInsert,UpsertBatch

# 报告 / 基线（脚本白名单制：文件式 C#）
dotnet run --file scripts/crossbench-report.cs -- <batch.json> --out report.md
dotnet run --file scripts/crossbench-report.cs -- --baseline <b1.json,b2.json,b3.json> --out baseline.md
```

结果 JSON 落 `bench/crossbench/results/crossbench-*.json`（入库）；环境变量与 PerfHub
同口径（`PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION`，`.env.test` 兜底）。

## 测量协议（与 PerfHub 对齐部分不再重复，此处只列交叉特有）

| 项 | 值 | 说明 |
|---|---|---|
| 臂序 | 每操作两遍反序（p0：A,D,F,P；p1 反序），聚合取两遍中位 | 消除臂顺序相位；**不可减为单遍**（MySQL 读族臂序相位 1.55× 事件在案） |
| 慢操作预算表 | Transaction 族与 Bulk 三项（Insert/Update/Upsert）计时窗 1.5→0.6s、预热 ×0.4（2026-10-25 A/A 验证与基线 v3 逐格噪声带一致） | 快操作不受影响（迭代数在 maxIterations 钳制处收敛，预算从不生效）；中位稳定性由 fsync/服务端主导（批内 err 普遍 <2%） |
| 批量形态 | SQLite 行预编译 / multi-exec / json_each（ADR-Q/R 断点 2026-10-25 起） | MySQL/PG 维持多值/加载器/IN |
| FreeSql 臂 | Fluent `ConfigEntity` 原生实体、`UseNoneCommandParameter(true)`、provider 批量 API；读走 `Select<T>` 表达式管线（ORM 层口径） | 详见臂内注释与 ADR-Q/R |
| 读正确性守卫 | 每方言一次非计时校验（值逐字段 + 计数）；`--arms` 缺 FreeSql 时自动跳过 | 防 Fluent 映射错位 |

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
| `PalormProtocol/` | Runner + FreeSql 臂（链接 PerfHub 引擎与三臂） |
| `UpsertProbe/` / `DeleteProbe/` / `EnumProbe/` | 形态每行成本与会话机械探针（独立小工程） |
| `results/` | 批 JSON 与基线报告（入库） |
| `scripts/crossbench-report.cs`（仓根） | 单批报告 / 多批基线生成 |
