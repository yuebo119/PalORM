# PalORM.DapperSuite — Dapper 官方基准套件的三臂移植版

> 定位：用 **Dapper 官方的数据集与测试方法学**，在 SQLite / MySQL / PostgreSQL 三方言上
> 对照 **ADO.NET 手写（性能地板）/ Dapper / PalORM** 三方。
> 与 [PerfHub](../perfhub/README.md) 的分工：PerfHub 测 22 项端到端能力矩阵（增删改查/批量/事务/并发），
> 本套件只测**官方那一个形状**（单行主键查询），但换来与官方数字可直接对照的资格。

## 1. 真源（官方仓库 `DapperLib/Dapper` → `benchmarks/Dapper.Tests.Performance/`）

| 官方文件 | 本套件对应 | 说明 |
|---|---|---|
| `Post.cs` | `Post.cs` | 13 列 POCO（`Id` + `Text` + 2×`DateTime` + `Counter1..9`），`Seed(i)` 按行号确定性派生 |
| `Benchmarks.HandCoded.cs` | `Benchmarks.HandCoded.cs` | 预建命令 + `Prepare()` + 参数复用只写 `Value` + `CommandBehavior.SingleResult\|SingleRow` + 13 列按序号手工物化 + `DataTable` 变体 |
| `Benchmarks.Dapper.cs` | `Benchmarks.Dapper.cs` | `Query<T>`（buffered/unbuffered）、`Query<dynamic>`、`QueryFirstOrDefault<T>` |
| `Config.cs` | `Config.cs` | `Job.ShortRun` + `LaunchCount(1)` + `WarmupCount(2)` + `UnrollFactor(500)` + `IterationCount(10)` + `MemoryDiagnoser` + `BaselineRatioColumn.RatioMean` + `DefaultOrderer(FastestToSlowest)` + `JoinSummary` |
| `Helpers/ORMColum.cs` | `Columns.cs` | ORM 列（`[Description]` 优先，否则类名去 `Benchmarks` 后缀） |
| `BenchmarkBase.cs` | `BenchmarkBase.cs` | `Step()` 轮转 `i` 于 `1..5000`（`i++; if (i > RowCount) i = 1;`） |
| — | `Benchmarks.PalOrm.cs` | 第三臂（PalORM），与 Dapper 臂同语义的对照项 |

官方 SQL：`select * from Posts where Id = @Id`（HandCoded 官方版为 `select Top 1 ...`）。

## 2. 运行

```bash
# 三库串行全跑（SQLite → MySQL → PostgreSQL），日志落 bench/PalORM.DapperSuite/results/
bash scripts/dappersuite-run.sh

# 只跑一库
bash scripts/dappersuite-run.sh pg
```

凭证经 `scripts/set-test-env.sh` 从仓库根 `.env.test` 读入（不回显值）；`sqlite` 档不需要连接串
（库文件落在系统临时目录）。方言由 `DAPPER_SUITE_DIALECT`（`sqlite` / `mysql` / `pg`）选择。

**必须串行跑**：BDN 计时对 CPU 争用敏感，三库并行会同时污染三份数字。

## 3. 口径差登记（与官方的全部偏离，逐条给理由）

| # | 官方做法 | 本套件做法 | 理由 |
|---|---|---|---|
| D1 | 只支持 SQL Server（连接串来自 `app.config`） | 三方言经环境变量选择 | 本套件的目的就是跨库对照；官方无多方言版本可照搬 |
| D2 | HandCoded 用 `select Top 1 * ...` | `select * ... limit 1` | `Top` 是 SQL Server 专有语法，`limit 1` 在三目标方言语义等价 |
| D3 | 未加引号的标识符 `Posts` / `Id` | 带引号：`"Posts"` / `` `Posts` `` | **实测失败驱动**：PG 把未加引号的混合大小写标识符折叠为小写，Dapper/HandCoded 两臂全项报 `42P01: relation "posts" does not exist`。三目标方言统一带引号，引用与 PalORM 生成 SQL 完全相同的对象（同表同列），语句语义不变 |
| D4 | 依赖已存在的 `Posts` 库（官方未提供建表脚本） | 自建 13 列同构 DDL + 5000 行种子（幂等） | 官方假定 SQL Server 侧已备好库；本套件必须自建才能三库可复现。种子按官方 `Post` 形状由行号派生，逐位确定 |
| D5 | `Dapper.Contrib` 的 `Get<T>` 项 | 未移植 | Dapper.Contrib 不在本仓库中央包管理内；该项测的是 Contrib 而非 Dapper |
| D6 | `ReturnColum`（工作负载方法返回类型名） | 未移植 | 本套件 PalORM 臂是 async-only，该列对三臂会一律塌成 `Task`，无区分度且与方法名列重复 |
| D7 | 全部基准为同步 API | PalORM 臂为 `async Task<...>` | PalORM 公共 API 是 async-only（这本身是对照的一部分）；BDN 原生支持 `Task` 返回值，异步开销计入 PalORM 侧 |
| D8 | 官方 `Post` 无 ORM 标注（Dapper 靠约定映射） | `Post` 加 PalORM 标注（`[Table]`/`[Key]`/`[Column]`） | PalORM 是源生成器 ORM，需要编译期实体元数据；列形状与官方逐位一致 |

## 4. 结果（2026-09-22，单机 AMD Ryzen 9 8945HX / .NET 11.0.0-rc.1 / BDN 0.16.0-develop）

**读法**：`Ratio` 是**同一方言同一次运行内**相对 `HandCoded / SqlCommand`（ADO.NET 地板）的比值。
跨方言的绝对值不可比（不同服务器与网络位置），跨**运行**的绝对值也不可比
（实测同一台机器两次运行整体漂移 2–6 µs，见 `docs/性能基准规范.md` 的交替 A/B 规则）。

### SQLite（进程内，无网络）

| ORM | Method | Mean | Ratio | Allocated |
|---|---|---:|---:|---:|
| HandCoded | SqlCommand | 37.77 us | 1.00 | 778 B |
| HandCoded | DataTable | 38.53 us | 1.02 | 2892 B |
| Dapper | Query&lt;dynamic&gt; (buffered) | 45.96 us | 1.22 | 3339 B |
| Dapper | QueryFirstOrDefault&lt;T&gt; | 47.43 us | 1.26 | 3067 B |
| Dapper | Query&lt;T&gt; (unbuffered) | 48.97 us | 1.30 | 3259 B |
| PalORM | Query&lt;T&gt; (buffered) | 49.31 us | 1.31 | 3922 B |
| Dapper | Query&lt;T&gt; (buffered) | 51.94 us | 1.38 | 3347 B |
| PalORM | QueryFirst&lt;T&gt; | 61.63 us | 1.63 | 4410 B |
| PalORM | FirstOrDefault&lt;T&gt; | 62.17 us | 1.65 | 4410 B |

### MySQL（网络往返 ~570 µs/op）

| ORM | Method | Mean | Ratio | Allocated |
|---|---|---:|---:|---:|
| Dapper | Query&lt;T&gt; (buffered) | 570.6 us | 0.99 | 4948 B |
| Dapper | QueryFirstOrDefault&lt;T&gt; | 571.5 us | 0.99 | 4668 B |
| PalORM | FirstOrDefault&lt;T&gt; | 572.6 us | 1.00 | 6844 B |
| PalORM | QueryFirst&lt;T&gt; | 573.2 us | 1.00 | 6902 B |
| HandCoded | SqlCommand | 575.3 us | 1.00 | 674 B |
| PalORM | Query&lt;T&gt; (buffered) | 577.2 us | 1.00 | 6339 B |
| Dapper | Query&lt;dynamic&gt; (buffered) | 578.0 us | 1.01 | 4988 B |
| HandCoded | DataTable | 578.1 us | 1.01 | 2788 B |
| Dapper | Query&lt;T&gt; (unbuffered) | 6,655.7 us | 11.58 | 69412 B |

### PostgreSQL（网络往返 ~540 µs/op）

| ORM | Method | Mean | Ratio | Allocated |
|---|---|---:|---:|---:|
| HandCoded | SqlCommand | 542.4 us | 1.00 | 372 B |
| PalORM | Query&lt;T&gt; (buffered) | 562.3 us | 1.04 | 5341 B |
| Dapper | Query&lt;dynamic&gt; (buffered) | 567.2 us | 1.05 | 1700 B |
| PalORM | FirstOrDefault&lt;T&gt; | 569.7 us | 1.05 | 6011 B |
| Dapper | Query&lt;T&gt; (buffered) | 575.1 us | 1.06 | 1660 B |
| HandCoded | DataTable | 581.8 us | 1.07 | 2340 B |
| Dapper | QueryFirstOrDefault&lt;T&gt; | 583.6 us | 1.08 | 1380 B |
| PalORM | QueryFirst&lt;T&gt; | 585.3 us | 1.08 | 6067 B |
| Dapper | Query&lt;T&gt; (unbuffered) | 15,020.2 us | 27.72 | 70676 B |

## 5. 已知仪器伪影与探针归因

### 5.1 `Query<T> (unbuffered)` 在联网驱动上退化（MySQL 11.6×、PG 27.7×，SQLite 干净）

官方基准写的是 `Connection.Query<Post>(sql, param, buffered: false).First()`——**提前取一行就放弃
未读完的流式读取器**。这个形状在 SQLite（进程内）无害（1.30×），在 MySQL/PG 上代价达 11.6×/27.7×，
且分配量从 ~5 KB/op 涨到 ~69 KB/op。

**这不是 Dapper 或 PalORM 的问题**——该项只存在于 Dapper 臂，PalORM 臂没有对应形状。
它是**官方基准形状与联网驱动的交互**。探针（§6.3）证明代价来自"提前放弃读取器"这一步本身。

> 定性为"仪器伪影"而非"被测系统的性能"，是为了防止把这一行读成"Dapper 比 PalORM 慢 11 倍"。
> 该行保留在报告里（它是官方项），但不参与任何三臂结论。

### 5.2 PalORM 单行操作符比 `ToListAsync()[0]` 慢约 25%（仅 SQLite 可见）

SQLite 上 `FirstOrDefault<T>`（1.65）与 `QueryFirst<T>`（1.63）都明显慢于同臂的
`Query<T> (buffered)`（1.31），而 MySQL/PG 上该差异被网络往返淹没（1.00 vs 1.04）。
探针（§6.1、§6.2）把成因定位到 **PalORM 单行查询发出的 SQL 形状**，不是 ORM 层开销、
也不是终端操作符。

## 6. 探针记录（临时基准，测完已删除）

### 6.1 探针一：SQLite / PalORM 臂——形状还是操作符？

| 形态 | Mean | Allocated |
|---|---:|---:|
| `Query<T> (buffered)`（无行数限制） | 49.60 µs | 3.83 KB |
| `FirstOrDefault<T>`（Take(1) + FirstOrDefault） | 56.69 µs | 4.31 KB |
| **PROBE** `Take(1)+ToListAsync` | 59.98 µs | 4.31 KB |
| `QueryFirst<T>`（Take(1) + First） | 63.51 µs | 4.31 KB |

`Take(1)+ToListAsync` 与 `Query<T> (buffered)` 只差 `_take=1`、与 `FirstOrDefault<T>` 只差终端操作符。
它落在 `FirstOrDefault` 同一档（59.98 ≈ 56.69），**不在** `buffered` 那一档（49.60）——
说明开销来自行数限制这条 SQL 形状，`FirstOrDefaultAsync` 的代码路径本身不额外收费。

### 6.2 探针二：ADO.NET 层（SQLite，同连接、同物化路径）——形状里哪一段贵？

| SQL | Mean | Allocated |
|---|---:|---:|
| `select * from "Posts" where "Id" = @Id` | 37.82 µs | 778 B |
| 同上 + ` limit 1`（**字面量**） | 36.92 µs | 778 B |
| 同上 + ` limit @L`（**参数化** LIMIT） | 50.89 µs | 906 B |
| 同上 + ` limit @L offset @O`（**PalORM 实际形状**） | 55.81 µs | 970 B |

- 字面量 `limit 1` 与无 LIMIT **无差别**（36.92 vs 37.82，落在噪声内）。
- **参数化 LIMIT 是代价来源**（+13 µs），再叠加参数化 OFFSET（再 +5 µs）。
- PalORM 的 Take 路径恰好发出最贵的形状：`QueryBuilder.BuildLimitClause` 对 SQLite/PG 走
  `default:` 分支发 `LIMIT <param> OFFSET <param>`，且 `_skip` 为空时仍绑 0 发出 `OFFSET`。
  在 MySQL/PG 上这一项被网络往返淹没，故只在 SQLite 可见。

**跨运行稳定性登记**：参数化形状实测 45.05 µs（第一轮）/ 55.81 µs（第二轮），方向稳定、
幅度受运行间漂移影响（+7 至 +18 µs）。故本套件只把该探针作为**形状归因**的证据，
不把幅度当结论。参数化 LIMIT 为何在 SQLite 上更贵，未进一步隔离 [推断]：参数化值在
prepare 期无法常量折叠，SQLite 走不到"行数已知"的执行路径。

### 6.3 探针三：MySQL / Dapper 臂——unbuffered 11.6× 的机制

| 形态 | Mean | Allocated |
|---|---:|---:|
| `Query<T> (buffered)` | 577.0 µs | 4.83 KB |
| **PROBE** unbuffered + 完整枚举（`ToList`） | 572.7 µs | 4.83 KB |
| **PROBE** 每操作新建连接 + buffered | 1,473.0 µs | 8.38 KB |
| `Query<T> (unbuffered)` + `First()`（官方形状） | 6,219.5 µs | 67.79 KB |

- 代价来自**提前放弃未读完的流式读取器**，不是 unbuffered 本身：完整枚举后回落到 572.7 µs，
  与 buffered（577.0 µs）无差别。
- 也**不是**"每操作一次连接建立"：那是 1,473.0 µs，不到 6,219.5 µs 的四分之一。
- 剩余约 4.7 ms 属驱动内部对未完成结果集的清理（分配量 67.79 KB/op 与之吻合），
  机制未进一步隔离。

> 探针一的初版把三条命令建在各自临时连接上（连接被 GC 回收后语句失效），实测 StdDev 达
> 149 µs，噪声吞掉全部信号；改为同一连接后 StdDev 降到 0.5–1.6 µs。**登记为探针设计缺陷**：
> 对比 SQL 形状时，命令必须挂在同一连接上，否则测的是连接生命周期而非形状。
