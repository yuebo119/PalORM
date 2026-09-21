# PerfHub — PalORM 统一性能测试系统

> 定位：一个命令跑完**三方言 × 三实现 × 统一数据集 × 18 个专业测试项**的全套性能指标，
> 输出单一 HTML 报告（含 SVG 增长曲线与版本对比），原始数据按次留存为 JSON。

## 快速开始

```bash
# 完整跑（三方言 × 2000/20000 行 × 18 项 + 3 线程档并发），约 30~35 分钟
dotnet run --project bench/PalORM.PerfHub -- run --concurrency --threads 1,4,8 --version HEAD

# 只跑 SQLite 冒烟（迭代次数降到 30%）
dotnet run --project bench/PalORM.PerfHub -- run --dialects sqlite --tiers 2000 --quick

# 从历史 JSON 重新生成报告（不重跑）
dotnet run --project bench/PalORM.PerfHub -- report
```

`--version` 记入 JSON，是版本对比与增长曲线的横轴。语义化版本号（`v5.5.1`）按版本号升序
排在前面当基线，非语义化标识（`HEAD`/`main`）排最后当最新——与两轮实测谁先跑无关。

环境变量（PG/MySQL 档必需，与集成测试同一口径，见 `CONTRIBUTING.md`）：
`PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION`；未设置时从仓库根 `.env.test`
补入缺失项。凭据不入 git、不回显。

## 18 个测试项（按分组）

| 分组 | 测试项 | 测什么 |
|---|---|---|
| **Build** | `BuildGetByKeySql` / `BuildComplexQuerySql` | 纯 SQL 构建开销（不执行）——ORM 构建税 |
| **CRUD** | `GetByKey` / `QueryAll` / `StreamAll` / `Insert` / `Update` / `BulkInsert` / `BulkUpdate` / `BulkDelete` | 点查、全查、流式、单条写、批量写删 |
| **Query** | `KeysetPage` / `WhereIn` / `Count` | 键集分页、IN 查询（100 键）、计数 |
| **Transaction** | `TxSingleInsert` / `TxTenInserts` / `TxHundredInserts` / `TxBulkInsert` / `TxRollback` | 真实事务形态：1/10/100/批量条提交，以及更新后回滚 |
| **Baseline** | `GenerateRows` | 数据生成自身内存（不含 ORM 与数据库），每档位一条 |
| **Concurrency** | `Concurrent_Mixed80_20` | 80/20 读写混合吞吐，`--concurrency` 时启用 |

**Build 组不是同类对比**：ADO.NET / Dapper 两臂只是返回预先写好的字面量 SQL，测的是
「不构建」的下限；PalORM 臂测的是 `From<T>()/Where/OrderBy/Take` 链式构建 + 方言引用符 +
DryRun 出参的完整开销。该组差值读作「ORM 构建税」，不是「PalORM 查询慢 N 倍」。

**写操作的主键空间**：每轮用互不相同的主键段（`SeedRows(count, offset)` 的 offset 按轮次递增），
第二轮不会撞主键；`prepare` 回调在预热前与计时前各重置一次库状态，不计入时延与分配。

## 统一口径（为什么可信）

| 维度 | 口径 |
|---|---|
| 数据集 | S1 Narrow（主键 + 4 数据列），种子由行号**确定性派生**——任何一次生成的库内容逐位相同 |
| 行数档位 | 2,000 / 20,000（`--tiers` 可覆盖），覆盖中小表与中大表两个量级 |
| 三方言 | 同一实体、同构 DDL（列名/类型对齐），仅引用符与参数占位符按方言分叉 |
| 三实现 | **ADO_NET**（地板基线，手写参数化 SQL + 手工物化）· **Dapper**（主流 micro-ORM）· **PalORM**（被测对象） |
| 连接 | 三实现复用**同一条已打开连接**（建连口径一致，否则 ORM/地板比值被污染，见 lessons B76） |
| 时延 | **全路径**——被测动作内部含 SQL 构建 → 执行 → 物化，测量引擎不剥离任何段；种子数据生成在测量前，不计入。预热后测 N 轮取**中位数**，Error/Mean > 5% 标黄 |
| 内存 | `GC.GetTotalAllocatedBytes` 精确计数（确定性，复现性优于 1%）+ `GC.GetGCMemoryInfo` **采样堆峰** + Gen0 次数 |
| 并发 | 每线程独立连接，80/20 读写混合，预热 1.5s 不计数，近邻秩 p50/p95/p99 |
| 会话生命周期 | PalORM 臂**按操作新建** `DataSession`——与 Dapper 的无状态扩展方法对等；会话不释放，因为 `DisposeAsync` 会关闭三实现共用的那条连接 |

## 输出

```
bench/perfhub/
  results/history-<yyyyMMdd-HHmmss>.json   每次运行的完整原始数据（schema 3，含 Version）
  results/latest.json                      最近一次（供回归对比）
  report.html                              统一报告（①总览 ②ORM vs 地板 ③并发 ④版本对比 ⑤增长曲线 ⑥说明）
```

## 报告区块

1. **单操作指标总览**——按 Build/CRUD/Query/Transaction/Baseline 分组，时延中位数/均值、Error/Mean、分配/op、采样堆峰、Gen0
2. **ORM vs ADO.NET 地板**——以同轮 ADO.NET 为分母的时延比/分配比（跨环境只比比值，规范 §3）
3. **并发吞吐**——ops/s + p50/p95/p99，按 方言 × 实现 × 线程档
4. **版本对比**——同项时延/分配变化率，外加**地板归一化对比**（见下）
5. **增长曲线**——跨历史运行的 SVG 折线（对数轴），每项测量一条线，向下 = 优化生效
6. **说明与未覆盖维度**——如实标注未测项

## 跨版本对比为什么必须「地板归一化」

两轮实测相隔数十分钟，**同机 ADO.NET 地板本身会漂移**（实测单项漂移可达 50%）。
直接比绝对时延会把「机器那轮更快」误读成「ORM 那轮更快」。

报告④的地板归一化算式：

```
(新_PalORM / 新_地板) ÷ (旧_PalORM / 旧_地板)
```

对机器漂移取一阶抵消，只有 ORM 相对地板的变化被保留下来——这是跨版本唯一可信的口径。
归一化后仍会残留噪声：地板自身波动大的项在表里标 **⚠ 地板漂移 >18%**，这些行只能当方向性参考。
要拿硬结论，需对关注项做**交替 A/B 重测**（两版轮流跑、多轮取中位）。

## 未覆盖维度（如实声明）

- 方言特有类型（JSON/数组/枚举）——三方言表结构同构，未测方言扩展类型
- 连接池行为——PerfHub 用单连接，池参数（MaxPoolSize/MinPoolSize/空闲超时）不在范围
- 长时衰减——单次运行无衰减数据，需连续多轮对比（增长曲线区可观测）
- 并发态分配——多线程下 `GetTotalAllocatedBytes` 被干扰，不测（见 lessons B74）
- `ForEachAsync` 流式终结器——v5.8 才加，v5.5.1 基线没有，故 `StreamAll` 用两版都有的
  `QueryAsyncEnumerable` 做共同口径

## 与 BDN 基准的分工

BDN（`bench/PalORM.Benchmarks`）测**单线程微基准**的精细统计；PerfHub 测**跨方言跨实现的一致口径**。
两者互补：BDN 的绝对精度高，PerfHub 的可比性强（同一数据集、同一连接、同一指标定义）。
规范见 `docs/性能基准规范.md`。
