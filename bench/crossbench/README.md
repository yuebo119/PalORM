# CrossBench — 四臂交叉基准（ADO.NET / Dapper / FreeSql / PalORM）

> 定位：三方言 × 两档 × 17 操作 × 四臂的 ORM 层交叉对比。测量引擎、数据集、三臂实现
> 与 [PalORM.PerfHub](../perfhub/README.md) 单一事实源（链接编译），本工程只新增
> FreeSql 臂与 Runner。2026-10-25 起入库并纳入 CI（workflow_dispatch）。

## 运行

```bash
# 全量（~55 min，需 .env.test 的 PG/MySQL 凭据）
dotnet run --project bench/crossbench/PalormProtocol -c Release -- --dialects sqlite,mysql,pg 2000 20000

# 子集（--dialects / --ops / 档位数字均可缩窄）
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
| 臂序 | 每操作两遍反序（p0：A,D,F,P；p1 反序），聚合取两遍中位 | 消除臂顺序相位 |
| 批量形态 | SQLite 行预编译 / multi-exec（ADR-Q 断点 2026-10-25 起） | MySQL/PG 维持多值/加载器 |
| FreeSql 臂 | Fluent `ConfigEntity` 原生实体、`UseNoneCommandParameter(true)`、provider 批量 API；读走 `Select<T>` 表达式管线（ORM 层口径） | 详见臂内注释与 ADR-Q |
| 读正确性守卫 | 每方言一次非计时校验（值逐字段 + 计数） | 防 Fluent 映射错位 |

## 基线纪律

- **B100**：升格基线需 ≥3 个完整批（三批窗口中位数）；地板批间散布 >20% 的格标 ⚠。
- 已知形态断点：2026-10-25（SQLite 批插/批 upsert 四臂同批切换 + Dapper TxBulkInsert
  单格 d759346），断点前批的对应格不并入窗口（报告器 n 列如实标注）。
- 探针工具：`bench/crossbench/UpsertProbe`（A+B 形态每行成本，ADR-Q 证据）。

## 目录

| 路径 | 内容 |
|---|---|
| `PalormProtocol/` | Runner + FreeSql 臂（链接 PerfHub 引擎与三臂） |
| `UpsertProbe/` | 形态每行成本探针（独立小工程） |
| `results/` | 批 JSON 与基线报告（入库） |
| `scripts/crossbench-report.cs`（仓根） | 单批报告 / 多批基线生成 |
