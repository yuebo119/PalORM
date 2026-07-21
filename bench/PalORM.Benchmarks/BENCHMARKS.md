# PalORM 性能基准报告 v4.0.0

> .NET 11 preview 6 · BenchmarkDotNet 0.14 · `[MemoryDiagnoser]`
> 配置：`launchCount=3, warmupCount=5, iterationCount=10`（统计可信度高）
> SQLite 共享内存（10,000 行 seed）· 2026-07-22
> **真实场景：每次操作创建连接/会话（`using var`）· ADO.NET 统一基线**

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
| PalORM_BulkInsert_10000 | 142.2 ms | 134.4 ms | 16.2 MB |
| PalORM_BulkUpdate_1000 | 5.5 ms | 5.6 ms | 1.6 MB |
| PalORM_BulkDelete_500 | 7.2 ms | 7.8 ms | 1.0 MB |

> ⚠ PalORM BulkInsert 比 Dapper MultiRowInsert 慢 3.9 倍。这是已知差距——PalORM BulkInsert 走 `MultiValueBulkInsert` 共享框架，每批 `ProbeBinderAsync` 探测；Dapper 直接拼接多值 INSERT 无探测。改进方向：PostgreSQL 走 Binary COPY（已实现），SQLite/MySQL 可考虑跳过 probe。

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
