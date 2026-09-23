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
版本标识可用 `DAPPER_SUITE_VERSION` 覆盖（默认 `HEAD`），写入结果库信封的 `version` 字段。

**必须串行跑**：BDN 计时对 CPU 争用敏感，三库并行会同时污染三份数字。

**结果登记**：除 BDN 工件与本文档的结果表外，每次跑测写一份结果库信封
（`bench/results/dappersuite-*.json`，含提交号、口径登记、健康度），统一登记处见
`bench/results/README.md`。跨夹具索引：`bash scripts/perf.sh index`（同一内容已含在唯一报告里）。

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
| D9 | 官方基类在 `Setup` 打开一条连接、全过程复用 | PalORM 臂用公开工厂 `DataSession<TProvider>.CreateAsync` 在**范围入口建一次**、范围内服务全部操作、`CloseConnection` 释放 | README「创建会话」的最佳实践形态，也是编码规范 STD-ARCH-003（using-scoped 无状态、用完即弃）。"范围"就是一个基准类的 Setup→Cleanup，与官方的单连接复用同口径——若改成每操作新建，建连/建会话成本会混进 Ratio（`.ai/lessons.md` B76 记录的正是这一形态） |
| D10 | —（官方只有 SQL Server，无此问题） | SQLite 的 `foreign_keys`/`journal_mode=WAL`/`synchronous`/`cache_size`/`mmap_size` **三臂同一组 PRAGMA** | PalORM 的 `SqliteProvider` 初始化会自动配这一组；只给 ORM 臂配、不给另两臂配，比较就从"ORM 层差异"变成"连接配置差异"。PRAGMA 清单与产品 `SqliteProvider.InitializeConnectionAsync` 逐条一致 |

## 4. 结果（2026-09-22，单机 AMD Ryzen 9 8945HX / .NET 11.0.0-rc.1 / BDN 0.16.0-develop）

**读法**：`Ratio` 是**同一方言同一次运行内**相对 `HandCoded / SqlCommand`（ADO.NET 地板）的比值。
跨方言的绝对值不可比（不同服务器与网络位置），跨**运行**的绝对值也不可比
（实测同一台机器两次运行整体漂移 2–6 µs，见 `docs/性能基准规范.md` 的交替 A/B 规则）。

### SQLite（进程内，无网络；三臂同一组 SQLite PRAGMA，见 D10）

| ORM | Method | Mean | Ratio | Allocated |
|---|---|---:|---:|---:|
| HandCoded | SqlCommand | 5.046 us | 1.00 | 778 B |
| HandCoded | DataTable | 5.744 us | 1.14 | 2892 B |
| Dapper | Query&lt;dynamic&gt; (buffered) | 9.538 us | 1.89 | 3339 B |
| Dapper | Query&lt;T&gt; (unbuffered) | 9.876 us | 1.96 | 3259 B |
| Dapper | QueryFirstOrDefault&lt;T&gt; | 10.013 us | 1.98 | 3067 B |
| Dapper | Query&lt;T&gt; (buffered) | 10.015 us | 1.99 | 3347 B |
| PalORM | Query&lt;T&gt; (buffered) | 10.394 us | 2.06 | 3018 B |
| PalORM | QueryFirst&lt;T&gt; | 10.582 us | 2.10 | 2946 B |
| PalORM | FirstOrDefault&lt;T&gt; | 10.998 us | 2.18 | 2946 B |

### MySQL（网络往返 ~525 µs/op）

| ORM | Method | Mean | Ratio | Allocated |
|---|---|---:|---:|---:|
| HandCoded | DataTable | 515.8 us | 0.98 | 2788 B |
| HandCoded | SqlCommand | 525.3 us | 1.00 | 674 B |
| Dapper | Query&lt;T&gt; (buffered) | 540.3 us | 1.03 | 4948 B |
| Dapper | QueryFirstOrDefault&lt;T&gt; | 551.1 us | 1.05 | 4668 B |
| PalORM | QueryFirst&lt;T&gt; | 555.0 us | 1.06 | 5997 B |
| Dapper | Query&lt;dynamic&gt; (buffered) | 560.0 us | 1.07 | 4988 B |
| PalORM | Query&lt;T&gt; (buffered) | 569.3 us | 1.09 | 5461 B |
| PalORM | FirstOrDefault&lt;T&gt; | 570.4 us | 1.09 | 5942 B |
| Dapper | Query&lt;T&gt; (unbuffered) | 6,324.2 us | 12.06 | 69412 B |

### PostgreSQL（网络往返 ~523 µs/op）

| ORM | Method | Mean | Ratio | Allocated |
|---|---|---:|---:|---:|
| HandCoded | SqlCommand | 522.9 us | 1.00 | 372 B |
| HandCoded | DataTable | 523.4 us | 1.00 | 2340 B |
| PalORM | Query&lt;T&gt; (buffered) | 556.6 us | 1.06 | 3901 B |
| PalORM | FirstOrDefault&lt;T&gt; | 564.4 us | 1.08 | 4573 B |
| PalORM | QueryFirst&lt;T&gt; | 565.5 us | 1.08 | 4629 B |
| Dapper | QueryFirstOrDefault&lt;T&gt; | 583.5 us | 1.12 | 1380 B |
| Dapper | Query&lt;T&gt; (buffered) | 583.7 us | 1.12 | 1660 B |
| Dapper | Query&lt;dynamic&gt; (buffered) | 585.5 us | 1.12 | 1700 B |
| Dapper | Query&lt;T&gt; (unbuffered) | 14,615.0 us | 27.96 | 70676 B |

**联网两库的判别力上限**：本批次 PG 上两个地板行只差 0.1%（522.9 对 523.4 µs，同连接同 SQL
只差 13 列物化方式），此前批次这一差值到过 9%——即联网库自身的行间散布与"ORM 之间的差"
同量级。故 MySQL/PG 上 1.1 以内的差异不具判别力，只有同方言同运行内的比值才有意义。

**PalORM 分配量**：会话在范围入口创建一次（D9）→ 3.0–6.0 KB/op
（SQLite 臂 2946–3018 B，同臂 Dapper 3067–3347 B，ADO.NET 地板 778 B）。

## 5. 已知仪器伪影与探针归因

### 5.1 `Query<T> (unbuffered)` 在联网驱动上退化（MySQL 12.1×、PG 28.0×，SQLite 无量级变化）

官方基准写的是 `Connection.Query<Post>(sql, param, buffered: false).First()`——**提前取一行就放弃
未读完的流式读取器**。这个形状在 SQLite（进程内）与 buffered 同档（1.96 对 1.99），
在 MySQL/PG 上代价达 12.1×/28.0×，且分配量从 ~5 KB/op 涨到 ~69 KB/op。

**这不是 Dapper 或 PalORM 的问题**——该项只存在于 Dapper 臂，PalORM 臂没有对应形状。
它是**官方基准形状与联网驱动的交互**。探针（§6.2）证明代价来自"提前放弃读取器"这一步本身。
MySQL 档该项轮间不稳定（四轮实测 6.22 / 6.66 / 8.66 / 6.32 ms，StdDev 最高 31%），
本身就说明它是驱动级清理而非稳定工作负载。

> 定性为"仪器伪影"而非"被测系统的性能"，是为了防止把这一行读成"Dapper 比 PalORM 慢 12 倍"。
> 该行保留在报告里（它是官方项），但不参与任何三臂结论。

### 5.2 PalORM 单行族的 SQL 形状代价（已修，证据链保留）

修复前 SQLite 上 `FirstOrDefault<T>`（4.71）与 `QueryFirst<T>`（4.21）明显慢于同臂的
`Query<T> (buffered)`（2.31）。两项独立探针把它完整归因到 **SQL 形状**：探针批次内
`PalORM 单行族 − buffered` 为 +7.99 µs，ADO.NET 层隔离出的参数化 LIMIT 成本为 +7.90 µs（§6.1），
两者吻合；终端操作符与 ORM 机械都不额外收费（§6.3）。

修复已落地：First/Single 族（take 由 API 固定为 1/2 且不带 Skip）在 SQLite 上内联 `LIMIT 1`/`LIMIT 2`
字面量且不发 OFFSET；用户 `Take`/`Skip` 与 PG/MySQL 维持参数化（SHAPE-010 的有限形状集不破）。
配对复测（同机 3 分钟内，因子开/关）：`FirstOrDefault` 20.538 → 14.823 µs，`QueryFirst` 23.099 → 14.939 µs，
分配量 3506 → 2946 B；对照项 `Query<T> (buffered)` 在噪声内不动。§4 的三方言表即修复后的正式批次。

**该收益只在 CPU 主导的 SQLite 配置下可见**：换到 I/O 主导的配置（默认 journal 模式 + 2MB 缓存，
地板与三臂同在 129–144 µs/op）后，因子开/关无差别（PerfHub SQLite 档交叉验证 138.3 对 134.8 µs）。
本套件的 SQLite 档启用 WAL + 64MB 缓存 + mmap（D10），数据全量驻留，故能分辨这 8 µs。

## 6. 探针记录（临时基准，测完已删除）

### 6.1 探针一：ADO.NET 层隔离 SQL 形状（SQLite · 同连接 · 同物化）

> 本项已落地为产品改动（见 §5.2），探针数据保留为证据链。

| SQL | Mean | Allocated |
|---|---:|---:|
| `select * from "Posts" where "Id" = @Id` | 5.534 µs | 778 B |
| 同上 + ` limit 1`（**字面量**） | 5.658 µs | 778 B |
| 同上 + ` limit @L offset @O`（**PalORM 实际形状**） | 13.557 µs | 970 B |
| 同上 + ` limit @L`（只参数化 LIMIT，不带 OFFSET） | 14.289 µs | 874 B |

- 字面量 `limit 1` 与无 LIMIT **无差别**（5.658 对 5.534，+0.12 µs 落在噪声内）。
- **参数化 LIMIT 是全部代价**：+8.0 µs，等于整个无 LIMIT 查询的 1.45 倍
  （两条参数化行 StdDev 0.33–0.59 µs，误差远小于信号）。
- 去掉 `OFFSET @O` **没有差别**（14.289 对 13.557，±1σ 内重叠），故成本不在 OFFSET。
- PalORM 的 Take 路径发的正是这一形状：`QueryBuilder.BuildLimitClause` 对 SQLite/PG 走
  `default:` 分支发 `LIMIT <param> OFFSET <param>`（`_skip` 为空时仍绑 0 发出 `OFFSET`）。
- 参数化 LIMIT 为何在 SQLite 上更贵，未进一步隔离 [推断]：参数化值在 prepare 期无法常量折叠，
  SQLite 走不到"行数已知"的执行路径。

### 6.2 探针二：MySQL / Dapper 臂——unbuffered 15.4× 的机制

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
- 该探针只测 Dapper 臂，不受 D9（会话生命周期）与 D10（SQLite PRAGMA）影响。

> **探针设计缺陷（B79）**：初版把各条命令建在各自临时连接上，连接被 GC 回收后语句失效，
> 实测 StdDev 达 149 µs，噪声吞掉全部信号；改到与被测命令同一连接后 StdDev 降到 0.1–0.6 µs。
> 对比 SQL 形状时命令必须挂在同一连接上，否则测的是连接生命周期而非形状。

### 6.3 探针三：固定开销分解（SQLite · 各自批次内全项同跑）

**表 A：手写臂"每调用新建命令与参数"**（与地板同 SQL、同物化、同连接，只差命令与参数是否复用）

| 形态 | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| 复用一个 `Prepare()` 过的命令 + 复用参数（地板） | 4.761 µs | 1.00 | 778 B |
| 每次都新建命令与参数 | 11.159 µs | 2.34 | 1402 B |

手写地板相对 ORM 的优势主要来自**命令与参数复用**这一形态，而不是手写映射。把同样的手写代码
改成每调用新建命令后，它落到 11.159 µs，**比两个 ORM 都慢**（Dapper buffered 10.025、
PalORM buffered 10.754）。ORM 的 2.0–2.3× 因此是"每调用新建 ADO.NET 对象"的结构性成本，
PalORM 与 Dapper 同档，不是 PalORM 独有缺陷。

**表 B：PalORM 默认弹性策略 vs 直通**（同臂、同查询，只差会话弹性配置）

| 形态 | Mean | Allocated |
|---|---:|---:|
| `Query<T> (buffered)` 默认（`MaxRetries=3` + 熔断） | 11.207 µs | 3018 B |
| `Query<T> (buffered)` 直通（`WithRetry(0)` + `WithCircuitBreaker(0)`） | 11.177 µs | 2746 B |
| `FirstOrDefault<T>` 默认 | 20.513 µs | 3506 B |
| `FirstOrDefault<T>` 直通 | 19.774 µs | 3234 B |

弹性包装的成本是恒定 **272 B/查询**（两对差值都是 272，与 `QueryBuilderExtensions.cs` 注释里
记录的三项之和 168+56+48 逐位吻合）；时间上 buffered 档无差别，单行档 0.74 µs（约 3.6%，边缘量级）。
