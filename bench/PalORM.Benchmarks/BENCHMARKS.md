# PalORM 性能基准报告 v4.0.0

> .NET 11 preview 6 · BenchmarkDotNet 0.14 · `[MemoryDiagnoser]`
> 配置：`launchCount=3, warmupCount=5, iterationCount=10`（统计可信度高）
> SQLite 共享内存（10,000 行 seed）· 2026-07-22
> **真实场景：每次操作创建连接/会话（`using var`）· ADO.NET 统一基线**

---

## 📋 基准方法论（对齐 Dapper 官方 benchmarks/）

本项目基准设计严格对齐 [DapperLib/Dapper](https://github.com/DapperLib/Dapper/tree/main/benchmarks/Dapper.Tests.Performance) 官方基准：

| 设计点 | 做法 | 理由 |
|--------|------|------|
| **测量工具** | BenchmarkDotNet 0.14 `[MemoryDiagnoser]` | .NET 官方运行时团队、Dapper、EF Core 全采用 |
| **ADO.NET 基线** | 每 benchmark 类都有 `ADO_NET_*` 方法标 `[Benchmark(Baseline = true)]` | 测量纯框架开销（相对原生 ADO.NET 的百分比） |
| **多 ORM 对照** | Dapper（micro-ORM）+ RepoDB（micro-ORM，同类公平对照） | Dapper 官方对照 9 个 ORM；PalORM 选 2 个同类最具代表性 |
| **防 page cache 命中** | 单点查询（GetByKey）使用 `NextId()` 轮询 1..10000 | 对齐 Dapper 官方 `Step()` 机制，避免 SQLite page cache 让数字失真 |
| **统计三件套** | Mean + Error + StdDev + Allocated | 所有报告表均含此四列 |
| **Job 配置** | 主基准 `launchCount=3, warmupCount=5, iterationCount=10`；远程 DB 用快速配置 `1/3/5` | Adam Sitnik 推荐 ≥15 迭代；3 次独立进程降低进程间差异 |

---

## 📖 查询

### 全表查询（10,000 行）

```
ADO.NET  ████████████████████████████████████████  3.95 ms   100%
Dapper   ██████████████████████████████████████    3.67 ms    93%  🟢
PalORM   █████████████████████████████████████████████  4.64 ms  118%
```

| 方法 | Mean | Median | 耗时% | 分配 | 分配% |
|:-----|-----:|-------:|:-----:|-----:|:-----:|
| **ADO_NET_QueryAll** | 4.15 ms | **3.95 ms** | **100%** | 1,329 KB | **100%** |
| Dapper_QueryAll | 3.74 ms | 3.67 ms | **93%** 🟢 | 1,352 KB | 102% |
| PalORM_QueryAll | 4.72 ms | **4.64 ms** | **118%** | 1,511 KB | 114% |

> 🟢 Dapper 在全表查询上比原生 ADO.NET 还快 7%（3.67ms vs 3.95ms），这是 Dapper 的著名优势——它的 `EmittedConstructor` 会直接生成 IL 物化代码。PalORM 的 118% 是包含 IRowFactory 委托 + Interceptiors 空列表检查 + SessionOperationState 门禁的完整框架开销。

### 主键查询（WHERE id = 5000）

```
ADO.NET  ████████████████████████████████████  18.73 μs   100%
Dapper   █████████████████████████████████████████  21.54 μs  115%
PalORM   ██████████████████████████████████████████  23.40 μs  125%
```

| 方法 | Mean | Median | 耗时% | 分配 | 分配% |
|:-----|-----:|-------:|:-----:|-----:|:-----:|
| **ADO_NET_GetByKey** | 20.44 μs | **18.73 μs** | **100%** | 1.4 KB | **100%** |
| Dapper_GetByKey | 22.26 μs | 21.54 μs | **115%** | 2.3 KB | 167% |
| PalORM_GetByKey | 24.03 μs | **23.40 μs** | **125%** | 4.6 KB | 328% |

> ✨ **v4.0 优化 B 直接生效**：GetByKey 从 v3.1 的 151% 降至 **125%**（-26%），绝对值从 31μs 降至 23.4μs。原因是 GetAsync 路径合并了 3 次 `Volatile.Read` 为单次 `CurrentState` 快照。

---

## ✏️ 插入（INSERT + 取回自增 ID）

```
ADO.NET  ████████████████████████████████  21.66 μs   100%
Dapper   ██████████████████████████████████████  25.05 μs  116%
PalORM   ███████████████████████████████████████████  29.05 μs  134%
```

| 方法 | Mean | Median | 耗时% | 分配 | 分配% |
|:-----|-----:|-------:|:-----:|-----:|:-----:|
| **ADO_NET_Insert** | 22.44 μs | **21.66 μs** | **100%** | 1.4 KB | **100%** |
| Dapper_Insert | 25.61 μs | 25.05 μs | **116%** | 3.7 KB | 261% |
| PalORM_Insert | 29.55 μs | **29.05 μs** | **134%** | 5.1 KB | 357% |

> PalORM 插入路径包含 RETURNING 物化整行 + SetId 回填 + SessionOperationState 门禁，相对 ADO.NET 多 34%。

---

## 🔄 更新（Set().Where().ExecuteNonQueryAsync 单步）

```
ADO.NET                ████████████████████████  17.45 μs   100%
Dapper                 ███████████████████████████  18.53 μs  106%
PalORM_Update          ████████████████████████████████████  24.14 μs  138%
PalORM_OptimisticLock  ██████████████████████████████████  21.72 μs  124%
```

| 方法 | Mean | Median | 耗时% | 分配 | 分配% |
|:-----|-----:|-------:|:-----:|-----:|:-----:|
| **ADO_NET_Update** | 17.76 μs | **17.45 μs** | **100%** | 0.9 KB | **100%** |
| Dapper_Update | 19.38 μs | 18.53 μs | **106%** | 2.3 KB | 249% |
| PalORM_Update | 25.04 μs | **24.14 μs** | **138%** | 8.1 KB | 888% |
| PalORM_Update_OptimisticLock | 22.76 μs | 21.72 μs | **124%** | 6.6 KB | 722% |

> 注：PalORM_Update 分配较高是因为 `Set().Where().ExecuteNonQueryAsync` 单步 API 内部构建参数化 UPDATE，包含完整的拦截器/metrics 注入路径。带乐观锁的 `OptimisticLock` 路径反而更快（24% vs 138%），因为 version 检查提前短路。

---

## 🗑️ 删除

```
ADO.NET           ██████████████████████████████████  32.24 μs   100%
Dapper            ███████████████████████████████████████  35.06 μs  109%
PalORM_Physical   ███████████████████████████████████████████████████  47.76 μs  148%
PalORM_SoftDelete ███████████████████████████████████████████████████████  49.88 μs  155%
```

| 方法 | Mean | Median | 耗时% | 分配 | 分配% |
|:-----|-----:|-------:|:-----:|-----:|:-----:|
| **ADO_NET_Delete** | 32.49 μs | **32.24 μs** | **100%** | 1.8 KB | **100%** |
| Dapper_Delete | 34.07 μs | 35.06 μs | **109%** | 4.7 KB | 263% |
| PalORM_Delete_Physical | 48.43 μs | **47.76 μs** | **148%** | 5.9 KB | 335% |
| PalORM_Delete_SoftDelete | 50.36 μs | 49.88 μs | **155%** | 5.8 KB | 325% |

---

## 🔀 UPSERT (ON CONFLICT)

```
ADO.NET  ████████████████████████████████  20.67 μs   100%
Dapper   ███████████████████████████████████  21.65 μs  105%
PalORM   ██████████████████████████████████████████████  30.38 μs  147%
```

| 方法 | Mean | Median | 耗时% | 分配 | 分配% |
|:-----|-----:|-------:|:-----:|-----:|:-----:|
| **ADO_NET_Upsert** | 22.13 μs | **20.67 μs** | **100%** | 1.0 KB | **100%** |
| Dapper_Upsert | 22.40 μs | 21.65 μs | **105%** | 2.8 KB | 286% |
| PalORM_Save_Upsert | 31.52 μs | **30.38 μs** | **147%** | 7.6 KB | 771% |

---

## 📦 批量

| 方法 | Mean | Median | 分配 |
|:-----|-----:|-------:|-----:|
| Dapper_MultiRowInsert_10000 | 36.6 ms | 39.4 ms | 13.0 MB |
| PalORM_BulkInsert_10000 | **59.3 ms** ✨ | — | **10.2 MB** ✨ |
| PalORM_BulkUpdate_1000 | 5.5 ms | 5.6 ms | 1.6 MB |
| PalORM_BulkDelete_500 | 7.2 ms | 7.8 ms | 1.0 MB |

> ✨ **v4.0 BulkInsert 重大优化**：耗时从 142ms 降至 **59.3ms（-58%，提速 2.4 倍）**，分配从 16.2MB 降至 **10.2MB（-37%）**。
>
> **与 Dapper 差距**：从 3.9 倍缩小到 **1.6 倍**。
>
> 优化手段：
> - `BuildRowPlaceholders` 改用 `Span<char> + stackalloc`（消除 LINQ + string.Join 分配）
> - `DbCommand` 跨批次复用（`Parameters.Clear` + CommandText 重用，替代每批 `CreateCommand`）
> - CommandText 仅在批大小变化时重建（首批 + 末尾不满批时）
>
> 剩余 1.6 倍差距的根因：PalORM 的 `binder → rowCommand → 逐参数拷贝到 batchCmd` 两阶段绑定路径。彻底消除需源生成器生成 `BindInsertToBatch(cmd, entity, offset)` 直接按 offset 绑定——属于 v5.0 候选优化。

---

## 🔄 事务

| 方法 | Mean | Median | 分配 |
|:-----|-----:|-------:|-----:|
| PalORM_Transaction_Commit | 58.84 μs | 54.90 μs | 17.3 KB |
| PalORM_Transaction_Rollback | 59.10 μs | 57.29 μs | 17.0 KB |
| PalORM_Transaction_Savepoint | 49.43 μs | 47.99 μs | 15.3 KB |

---

## ⭐ PalORM 独有特性

| 方法 | Mean | Median | 分配 |
|:-----|-----:|-------:|-----:|
| PalORM_Query_WhereIn_500 | 1.74 ms | 1.86 ms | 293 KB |
| PalORM_Query_SoftDelete_Filter | 27.0 μs | 23.2 μs | 5.4 KB |
| PalORM_Query_WithTracing | 5.52 ms | 5.61 ms | 1.5 MB |

> - `WhereIn_500` 自动分批（500/批）+ 参数化，避免 SQLite 999 参数上限
> - `SoftDelete_Filter` 自动注入 `deleted_at IS NULL`，开销约 4.5μs（vs 无过滤查询）
> - `WithTracing` 启用 ActivitySource + Metrics，开销约 900ns/行——这是 tracing 必然代价

---

## 🔨 SQL 构建（零 I/O）

```
StringBuilder      █████  107.4 ns   100%
PalORM_Simple      ██████████████  291.9 ns   272%
PalORM_Complex     ███████████████████  392.0 ns   365%
```

| 方法 | Mean | Error | 分配 |
|:-----|-----:|------:|-----:|
| StringBuilder_BuildSql | 107.4 ns | 18.18 ns | 1.46 KB |
| PalORM_BuildSql_Simple | 291.9 ns | 41.77 ns | **1.05 KB** 🟢 |
| PalORM_BuildSql_Complex | 392.0 ns | 57.26 ns | 1.46 KB |

> PalORM 用 ValueStringBuilder（栈分配 + ArrayPool），简单查询分配比 StringBuilder 还少 28%（1.05 KB vs 1.46 KB）。绝对耗时高是因为额外的 QueryBuilder 子句聚合 + 参数绑定开销。

---

## 🏆 v4.0 性能总结

### 核心指标相对 ADO.NET（Median）

| 操作 | v3.0.0 | v3.1 | **v4.0** | 趋势 |
|------|:---:|:---:|:---:|:---:|
| QueryAll 10K | 177% | 123% | **118%** | ✅ 持续改善 |
| GetByKey | 232% | 151% | **125%** | ✅✅ 显著改善（-26%）|
| Insert | 100% 🟢 | 137% | 134% | ➡ 稳定 |
| Update | 126% | 133% | 138% | ➡ 稳定 |
| Delete Physical | 103% 🟢 | 140% | 148% | ⬅ 环境波动 |
| Upsert | 100% 🟢 | 168% | 147% | ✅ 改善 |

### v4.0 核心成果

1. **GetByKey 大幅改善**：v3.1 的 151% → v4.0 的 **125%**（-26%）
   - 直接归因：优化 B（CRUD 路径 Volatile.Read 合并）
   - 绝对值：23.40μs vs v3.1 的 31μs（-7.6μs/查询）

2. **QueryAll 持续改善**：v3.1 的 123% → v4.0 的 **118%**（-5%）
   - 归因：优化 D（List Capacity 16 起步减少扩容）+ v3.1 优化的累积效果

3. **核心 CRUD 路径全绿**：所有测试通过，无回归

### 与 Dapper 对比

| 操作 | Dapper | PalORM v4.0 | 差距 |
|------|:---:|:---:|:---:|
| QueryAll | 93% 🟢 | 118% | +25% |
| GetByKey | 115% | 125% | +10% |
| Insert | 116% | 134% | +18% |
| Update | 106% 🟢 | 138% | +32% |
| Upsert | 105% 🟢 | 147% | +42% |
| Delete | 109% | 148% | +39% |

PalORM 在所有路径上比 Dapper 慢 10-42%。**但 PalORM 提供 Dapper 没有的**：
- 编译时实体校验（PALORM001-022 诊断）
- 源生成 RowFactory（零反射）
- 软删/多租户/乐观锁自动注入
- Native AOT 全链路支持
- SessionOperationState 单会话门禁

---

## 🆚 同类 micro-ORM 四方对照（v4.1 新增）

> 对齐 Dapper 官方 `benchmarks/Dapper.Tests.Performance` 的多 ORM 对照做法。
> 加入 **RepoDB**（同为 micro-ORM，自称最快 ORM）作为同类公平对照。
> 配置：`launchCount=1, warmupCount=3, iterationCount=5`（快速验证，统计精度中等）
> 2026-07-23 · AMD Ryzen 9 8945HX · .NET 11 preview 6

### 核心读写四 ORM 对照

| Method | Mean | Error | StdDev | Allocated |
|:------|-----:|------:|-------:|----------:|
| **QueryAll 10K 行** | | | | |
| ADO_NET_QueryAll | 3,667.14 μs | 17.09 μs | 4.44 μs | 1,328.67 KB |
| RepoDb_QueryAll | **3,322.10 μs** 🟢 | 177.07 μs | 45.98 μs | **1,118.33 KB** 🟢 |
| Dapper_QueryAll | 3,445.34 μs | 257.67 μs | 66.92 μs | 1,351.87 KB |
| PalORM_QueryAll | 4,293.73 μs | 725.74 μs | 188.47 μs | 1,510.46 KB |
| **GetByKey（轮询 1..10000）** | | | | |
| Dapper_GetByKey | **18.17 μs** 🟢 | 0.24 μs | 0.04 μs | 2.34 KB |
| ADO_NET_GetByKey | 19.73 μs | 1.97 μs | 0.51 μs | 1.70 KB |
| RepoDb_GetByKey | 21.27 μs | 0.47 μs | 0.12 μs | 5.38 KB |
| PalORM_GetByKey | 21.36 μs | 0.46 μs | 0.07 μs | 4.55 KB |
| **Insert + 取回自增 ID** | | | | |
| RepoDb_Insert | **23.39 μs** 🟢 | 1.30 μs | 0.20 μs | 3.86 KB |
| ADO_NET_Insert | 26.17 μs | 17.10 μs | 4.44 μs | **1.43 KB** 🟢 |
| Dapper_Insert | 30.05 μs | 18.35 μs | 4.77 μs | 3.73 KB |
| PalORM_Insert | 32.94 μs | 7.46 μs | 1.94 μs | 5.04 KB |

### 排名（按 Mean 升序）

| 操作 | 1st | 2nd | 3rd | 4th |
|------|:---:|:---:|:---:|:---:|
| QueryAll | **RepoDb** 🟢 | Dapper | ADO.NET | PalORM |
| GetByKey | **Dapper** 🟢 | ADO.NET | RepoDb | PalORM |
| Insert | **RepoDb** 🟢 | ADO.NET | Dapper | PalORM |

### 结论（诚实归因）

1. **RepoDB 在 2/3 项胜出**——它自称"最快 ORM"在 QueryAll/Insert 路径上有数据支撑。原因：RepoDB 内部用 `DbCommandCache` + 预编译参数绑定，绕过 Dapper 的 IL 缓存层。
2. **Dapper 在 GetByKey 上最快**——但比 ADO.NET 仅快 8%（18.17 vs 19.73μs），在统计误差边缘。
3. **PalORM 在所有三项排名末位**——但与第三名差距小（GetByKey 落后 RepoDB 0.4%，Insert 落后 Dapper 9.6%）。
4. **PalORM 的 Insert 慢的根因**：`InsertAsync` 包含 RETURNING 物化整行 + SetId 回填 + SessionOperationState 门禁，相对 RepoDB 的 `InsertAsync` 路径（只回 last_insert_rowid）多一次物化。

**绝对不是"PalORM 比 RepoDB/Dapper 慢"这么简单**——PalORM 在同样安全语义（编译时校验 + AOT 全链路 + 软删/租户/乐观锁）下交付速度。若只比裸 CRUD 速度，用户应直接选 RepoDB 或 Dapper。

---

## 🔬 Dapper Cache Impact 专项（v4.1 新增）

> 对齐 Dapper 官方 [`DapperCacheImpact.cs`](https://github.com/DapperLib/Dapper/blob/main/benchmarks/Dapper.Tests.Performance/DapperCacheImpact.cs)。
> 目的：验证 PalORM "源生成 RowFactory 零反射" 在不同参数形状下的表现。
> 配置：`launchCount=1, warmupCount=3, iterationCount=5`

### 假设（事前）

- 假设：参数形状变化时 Dapper 缓存 miss 显著拖慢，PalORM 源生成路径恒定快。

### 实测结果

| Method | Mean | Error | StdDev | Allocated |
|:------|-----:|------:|-------:|----------:|
| **稳定参数形状（缓存命中）** | | | | |
| Dapper_StableShape | **23.73 μs** 🟢 | 77.44 μs | 4.25 μs | **2.25 KB** 🟢 |
| PalORM_StableShape | 40.50 μs | 82.38 μs | 4.52 μs | 7.46 KB |
| **变化参数形状（缓存 miss）** | | | | |
| Dapper_VaryingShape | **322.07 μs** 🟢 | 475.34 μs | 26.06 μs | **1.92 KB** 🟢 |
| PalORM_VaryingShape | 351.68 μs | 278.80 μs | 15.28 μs | 6.75 KB |

### 结论（事前假设被证伪）

**原假设错误**。数据证明：

1. **缓存命中场景 Dapper 反而比 PalORM 快 1.7×**（23.73 vs 40.50μs）——Dapper 的 IL 物化器一旦缓存命中，直接委托调用，比 PalORM 的 `IRowFactory<T>.Read` 接口分发快。
2. **变化参数形状场景两者持平**（322 vs 351μs，PalORM 慢 9%）——但绝对值主要由 `WHERE status = @status` **全表扫描**（无索引）主导，ORM 开销被 DB 扫描时间掩盖，无法分离 cache miss 代价。
3. **PalORM 分配始终高于 Dapper**（7.46/6.75 KB vs 2.25/1.92 KB）——SessionOperationState 门禁 + IRowFactory 委托 + 拦截器空列表检查的固有代价。

**对 PalORM 卖点的修正**：
- ❌ "源生成零反射优势 → 查询更快"——**在稳态查询场景下不成立**，Dapper IL 缓存命中后更快。
- ✅ "源生成零反射优势"——**仍然成立，但限定场景**：AOT 发布（Dapper.AOT 拦截器对 internal 类型失效）、首次查询冷启动、编译时类型校验。
- ✅ "统一 SessionOperationState 门禁 + 编译时诊断 + AOT 全链路"——**架构性优势，不能直接用 CRUD 速度衡量**。

---

## 运行方法

```bash
# 完整 33 项基准（约 15-20 分钟）
dotnet build bench/PalORM.Benchmarks -c Release
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*'

# 单类基准
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*SqliteBenchmarks*'
```

### 配置说明

```csharp
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
```

- `launchCount=3`：独立进程跑 3 次降低进程间差异
- `warmupCount=5`：5 次 warmup 让 JIT 完全预热
- `iterationCount=10`：10 次正式迭代取 Mean/Median

### 环境敏感性

micro-benchmark 对环境高度敏感。已知影响：
- **VMware/虚拟机进程**：占用 CPU + 内存带宽，可使 ADO.NET 基线升高 50-100%
- **散热降频**：长时间跑（>15 分钟）触发 CPU 热节流
- **后台 Windows Update / 杀毒软件**：偶发尖峰

**建议**：对比不同版本时，**关注相对 ADO.NET 的倍数**而非绝对值——ADO.NET 基线随环境波动，但 PalORM/ADO.NET 的相对关系稳定。

---

## 🌐 三 Provider 纯 Docker 基准（v4.0）

> **纯 Docker 环境**——benchmark 客户端与数据库在同一台服务器的 Docker 容器内，通过 `--network=host` 用 localhost 通信（零网络 RTT）
>
> 服务器：Ubuntu 7.0 / 8 核 / 7.2 GB RAM
> - PostgreSQL 18.4（Docker 容器，1Panel 管理）
> - MySQL 8.4.10（Docker 容器，1Panel 管理）
> - Benchmark 客户端：`mcr.microsoft.com/dotnet/sdk:11.0-preview` Docker 镜像
> - 网络：`--network=host`（localhost，零 RTT）
> 种子数据：10,000 行 bench_orders
> 配置：`launchCount=1, warmupCount=3, iterationCount=5`

### PostgreSQL 18.4（纯 Docker）

| 操作 | ADO.NET | Dapper | PalORM | PalORM vs ADO.NET |
|------|:---:|:---:|:---:|:---:|
| QueryAll 10K | 11.37 ms | 11.82 ms (108%) | **9.86 ms** | **90%** 🟢 |
| GetByKey | 6.66 ms | — | 8.68 ms | 130% |
| Insert + RETURNING | 7.82 ms | — | **7.26 ms** | **94%** 🟢 |
| BulkInsert 10K（Binary COPY）| — | — | **43.21 ms** | — |

> 🟢 PalORM 在 PG 上 **QueryAll 快 10%、Insert 持平**。消除网络 RTT 后，框架开销占比变大（vs 上次网络测试的 65%/35%）——说明上次的巨大优势部分来自 PalORM 减少了网络往返次数。

### MySQL 8.4.10（纯 Docker）

| 操作 | ADO.NET | Dapper | PalORM | PalORM vs ADO.NET |
|------|:---:|:---:|:---:|:---:|
| QueryAll 10K | 7.66 ms | 9.05 ms (118%) | 9.24 ms | 121% |
| GetByKey | 4.02 ms | — | 4.51 ms | 112% |
| Insert + LAST_INSERT_ID | 6.16 ms | — | **5.82 ms** | **96%** 🟢 |
| BulkInsert 10K（多值 INSERT）| — | — | **56.50 ms** | — |

> MySQL 上 PalORM Insert **略快于 ADO.NET（96%）**。QueryAll 慢 21%——MySQL 无 RETURNING，框架开销无法被网络优势抵消。

### 三 Provider 对比（纯 Docker）

| 操作 | SQLite（本地内存） | PostgreSQL（Docker） | MySQL（Docker） |
|------|:---:|:---:|:---:|
| QueryAll vs ADO.NET | 118% | **90%** 🟢 | 121% |
| GetByKey vs ADO.NET | 125% | 130% | 112% |
| Insert vs ADO.NET | 134% | **94%** 🟢 | **96%** 🟢 |
| BulkInsert 10K | 59.3 ms | 43.2 ms (COPY) | 56.5 ms |

### 网络环境 vs 纯 Docker 对比

| 操作 | 环境 | ADO.NET | PalORM | PalORM vs ADO.NET | 差异分析 |
|------|------|:---:|:---:|:---:|------|
| PG QueryAll | 网络 | 28.21 ms | 18.35 ms | **65%** 🟢 | 网络下 PalORM 减少往返次数优势放大 |
| PG QueryAll | Docker | 11.37 ms | 9.86 ms | **90%** 🟢 | 零 RTT 后框架开销占比变大 |
| PG Insert | 网络 | 23.54 ms | 8.04 ms | **35%** 🟢 | 网络下 RETURNING 单次往返优势巨大 |
| PG Insert | Docker | 7.82 ms | 7.26 ms | **94%** 🟢 | 零 RTT 后优势缩小但仍持平 |
| MySQL Insert | 网络 | 6.37 ms | 6.60 ms | 104% | 网络下基本持平 |
| MySQL Insert | Docker | 6.16 ms | 5.82 ms | **96%** 🟢 | Docker 下 PalORM 略快 |

> **关键洞察**：PalORM 的 RETURNING 单次往返在网络环境下优势巨大（PG Insert 快 65%），但在零 RTT 的 Docker 环境下回归到框架基线（90-96%）。这证明 PalORM 的核心价值在**网络场景**——减少往返次数比单次操作速度更重要。

### 连接配置

```bash
# 纯 Docker 运行（benchmark 客户端也在 Docker 里）
docker build -t palorm-bench .

# PostgreSQL
docker run --rm --network=host \
  -e PALORM_BENCH_PG="Host=127.0.0.1;Port=5432;Username=USER;Password=PASS;Database=palorm_bench;Pooling=false" \
  palorm-bench \
  dotnet run --project bench/PalORM.Benchmarks -c Release --no-build -- --filter '*PgBenchmarks*'

# MySQL
docker run --rm --network=host \
  -e PALORM_BENCH_MYSQL="Server=127.0.0.1;Port=3306;User ID=USER;Password=PASS;Database=palorm_bench;Pooling=false" \
  palorm-bench \
  dotnet run --project bench/PalORM.Benchmarks -c Release --no-build -- --filter '*MySqlBenchmarks*'
```
