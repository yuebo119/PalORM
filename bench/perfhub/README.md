# PerfHub — PalORM 统一性能测试系统（v2）

> 定位：一个命令跑完**三方言 × 三实现 × 统一数据集 × 22 个专业测试项**的全套性能指标，
> 输出单一 HTML 报告（含 SVG 增长曲线、版本对比、A/B 配对比值），原始数据按次留存为 JSON。
> v2 完整设计（含三臂契约与任务清单）见 [docs/v5.8-perfhub-v2-plan.md](../../docs/v5.8-perfhub-v2-plan.md)——**已按任务清单 1-5 阶段全部实施**。

## 快速开始

```bash
# 全量（三方言 × 2000/20000 × 22 项 + 并发）
dotnet run --project bench/PalORM.PerfHub -- run --concurrency --threads 1,4,8 --version HEAD

# 只 SQLite 冒烟
dotnet run --project bench/PalORM.PerfHub -- run --dialects sqlite --tiers 2000

# 交替 A/B（跨版本对比的唯一可信执行方式，基线 worktree 需按脚本头注释就绪）
bash scripts/perfhub-ab.sh /c/v551 3 --dialects pg,mysql --tiers 2000,20000

# 从历史 JSON 重新生成报告（不重跑）
dotnet run --project bench/PalORM.PerfHub -- report
```

`--version` 记入 JSON，是版本对比与增长曲线的横轴；语义化版本号升序在前当基线，
非语义化标识（`HEAD`）最后当最新——与两轮执行顺序无关。

环境变量（与集成测试同一口径）：`PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION`，
未设置时从仓库根 `.env.test` 补入缺失项。**MySQL 连接串会统一追加
`AllowLoadLocalInfile=true`**（MySqlBulkCopy 协议前提，三臂同等生效）。

## 口径与结果登记（规范 v2 §4.1 / §6）

| 项 | 本夹具的值 | 为什么记在这 |
|---|---|---|
| 连接配置口径 | SQLite：三臂共用同一条连接，建连后统一执行 7 项 PRAGMA（WAL + 64MB cache + mmap 等，与产品 `SqliteProvider` 逐条一致）；PG/MySQL：驱动默认 | 只给 ORM 臂配会让比较变成"连接配置差异"：同一修复在 I/O 主导与 CPU 主导两种配置下分别是 0% 与 −30%（2026-09-22 实测） |
| 会话生命周期口径 | `per-operation`（每操作新建 `DataSession`，与 Dapper 无状态扩展方法对等） | 与 DapperSuite 的 `per-scope` 不同，故两套的分配量不可互比（规范 §4.1） |
| 维度 8 计数 | 三臂共用 `CountingConnection` 装饰器，实测**往返次数/op** 与 **prepared 复用率** | 抓 N+1：实测 ADO 臂 `BulkUpdate` = 2000 次往返/op、`BulkInsert` = 11 次/op；基础 67 项中 60 项有值（其余 7 项是 `GenerateRows` 与 6 个纯构建项，本就没有往返），并发模式 3 项也已接计数（三臂 1.46–1.51 往返/op，80/20 混合） |
| 健康度 | 地板行散布中位数，阈值 0.35（本夹具自适应短跑实测 0.21/0.26/0.31） | `--quick` 批次在 label 里带 `quick` 标记，只作冒烟、不进基线 |
| 结果登记 | 除自身 `results/history-*.json` 外，另写结果库信封 `bench/results/perfhub-*.json` | 统一登记处见 `bench/results/README.md`；跨夹具索引 `bash scripts/perf.sh index`（同一内容已含在 `perf.sh full` 的唯一报告里） |

## 22 个测试项（v2 矩阵）

| 组 | 测试项 | 行业最优实现（三臂契约摘要） |
|---|---|---|
| **Build** | `BuildGetByKeySql` / `BuildComplexQuerySql` | 纯 SQL 构建（ORM 构建税，**非同类对比**：ADO/Dapper 返回预写字面量） |
| **CRUD** | `GetByKey` / `QueryAll` / `StreamAll` / `Insert` / `Update` | 显式列 + 参数化 + 键集 seek；流式不物化再枚举 |
| | `BulkInsert` | PG Binary COPY · MySQL MySqlBulkCopy（`local_infile=ON` 分流）· SQLite 多值 VALUES；Dapper 多值 VALUES |
| | `BulkUpdate` | PG `UPDATE FROM VALUES` · MySQL `CASE WHEN` · SQLite 逐条裹单事务 |
| | `BulkDelete` | `IN` 分批裹事务 |
| | `UpsertBatch` | PG/SQLite `ON CONFLICT excluded` · MySQL `ON DUPLICATE KEY VALUES(c)`；PalORM 走 `BulkMergeAsync` |
| | `InsertReturningId` | 三臂各 1 RTT：PG/SQLite `RETURNING`；MySQL `INSERT;SELECT LAST_INSERT_ID()` 合并 |
| **Query** | `KeysetPage` / `WhereIn` / `Count` | seek 分页（OFFSET 是反模式不测）；IN 显式占位符分批 |
| | `WideQueryAll` | 19 列宽表全物化——物化器按列数伸缩（ADO/PalORM 按序号，Dapper 按列名） |
| | `IncludeJoin` | 1:N 装配三策略对照（标注不同构）：ADO JOIN+手工 / Dapper multi-mapping / PalORM Include JOIN；带结果集等价断言 |
| **Transaction** | `TxSingleInsert` / `TxTenInserts` / `TxHundredInserts` / `TxBulkInsert` / `TxRollback` | 命令复用重绑参数；批内走 loader/VALUES；回滚撤销量上限 500 |
| **Baseline** | `GenerateRows` | 不碰库，数据生成内存基线 |
| **Concurrency** | `Concurrent_Mixed80_20` | 预热 1 s + 计时 2 s，池化连接每线程一条 |

**规模**：22 × 3 库 × 2 档 × 3 臂 = 396 单操作项 + 并发 + 基线。

## 测量口径（v2）

| 维度 | 口径 |
|---|---|
| 迭代 | **自适应**：预热后探针测单次耗时，`clamp(预算 ÷ 单次, 3, 上限)`——单测量耗时结构性有界（Build 1 s / 普通 1.5 s / 批量 4 s）。`--quick` 按 0.3 收缩该预算与预热预算 |
| 预热 | **按时间收敛**：至少 3 次，累计耗时达 1.5 s 即停，上限 `maxIterations/5`（Build 400 次，其余 40 次）。快操作行为与固定次数一致；慢操作不再承担无界成本——固定 40 次时 20K 档 Dapper BulkInsert 的预热是 54 s（单次 1.36 s），为计时段 4 s 的 13 倍 |
| 迭代上限 | 通用 200；`BulkDelete` 随档位变化——播种行数 = 轮数 × 档位行数，故由**总行数预算 20 万行**反推：档位 2000 得 50 轮（不变），档位 20000 得 10 轮。实测（2026-09-23，远程 MySQL 8.4，探针 `.ai/perf-probe/MySqlSeedDiag.cs`）：档位 20000 每轮删 20000 键 = 20 条 DELETE、单轮 2.02 s → 4 s 预算只跑 **3 轮**，实际只需 6 万行，而按上限 50 轮播种要 100 万行（**超配 16.7 倍**）；100 万行的播种三处单命令都超过驱动默认 30 s 超时（快照拷贝 94.6 s、`DELETE` 107.3 s、重置 114.0 s），整个方言会失败 |
| 时延 | 全路径（SQL 构建→执行→物化）中位数；预热、探针、prepare 播种不计时 |
| 内存 | `GetTotalAllocatedBytes` 精确计数 + `GC.GetGCMemoryInfo` 采样堆峰 + Gen0 |
| 播种 | (连接, 行数) 级快照缓存——首次全量播进 `perf_s1_seed`，后续 prepare 走 `TRUNCATE`（MySQL；PG/SQLite 用 `DELETE`，见下）+ `INSERT..SELECT` 两条 SQL。播种类语句显式设 **600 s** 命令超时（驱动默认 30 s 会被 100 万行的单命令打断） |
| 写操作主键 | 每轮互不重叠的确定性主键段；探针占 i=0、计时轮从 i=1 起。`BulkDelete` 的播种轮数**必须等于迭代上限**（曾按 `--quick` 的 scale 缩放而上限不缩，导致尾部轮次删到不存在的键——空删最快，中位数不受影响，但分配量被低估，而它是门禁卡的确定性指标） |
| 会话生命周期 | PalORM 每操作新建 `DataSession`（与 Dapper 无状态对等）；不释放（`DisposeAsync` 会关闭共用连接） |

## 公平性（三臂契约 + 机械门禁）

1. **三臂契约前置**：每项先定义三臂各自的行业最优写法（上表），违反即 bug
2. **地板健全性门禁**：报告自动检查"任一 ORM 比地板快 >10%"即标 `⚠地板?` 并汇总命中组数——
   真地板不可能被 ORM 超过，出现即地板实现有缺陷。该门禁在 v2 实施过程中抓出并修正了
   六处真地板缺陷（逐行逐批新建参数对象、批量插入未裹事务、SQLite 批宽未对齐产品口径、
   local_infile 探测未缓存、UpsertBatch 巨型语句、PalORM 事务内批量误用逐行）
3. Build 组与 IncludeJoin 的"策略不同构"显式标注

## 科学性（三层）

| 口径 | 抵消什么 | 怎么算 |
|---|---|---|
| 同轮地板比值 | 跨环境机器差 | ORM ÷ 同轮 ADO.NET |
| 地板归一化 | 跨版本机器漂移 | `(新_PalORM/新_地板) ÷ (旧_PalORM/旧_地板)`；漂移 >18% 标 ⚠ |
| **交替 A/B（黄金）** | 机器漂移 + 起跑顺序 | 块 = 库 × 档，两版背靠背轮流，奇偶轮换起跑序；逐轮配对比值取中位 + 轮间散布，散布跨 1.0 判"不可分辨" |

## 输出

```
bench/perfhub/
  results/history-<yyyyMMdd-HHmmss>.json   每次运行完整原始数据（schema 3，含 Version 与 label）
  results/latest.json                      最近一次
  report.html                              ①按组总览 ②ORM vs 地板（含门禁）③并发
                                           ④版本对比+地板归一化 ⑤b A/B 配对 ⑤增长曲线 ⑥口径声明
```

## 未覆盖维度（如实声明）

方言特有类型（JSON/数组/全文）、连接池参数、断连重试、10 万行以上规模、隔离级别与锁行为、
并发态分配（B74）、`ForEachAsync` 流式终结器（v5.8 新增，v5.5.1 基线无，StreamAll 用两版
都有的 `QueryAsyncEnumerable` 共同口径）。

## 与 BDN 基准的分工

BDN（`bench/PalORM.Benchmarks`）测单线程微基准的精细统计；PerfHub 测跨方言跨实现的一致口径。
规范见 `docs/性能基准规范.md`。
