# PalORM 性能基准报告 v3.0.0

> .NET 11 · RyuJit AVX-512 · BenchmarkDotNet 0.14 · `[MemoryDiagnoser]`
> SQLite 共享内存（10,000 行 seed）· 2026-07-21
> **真实场景：每次操作创建连接/会话（`using var`）· ADO.NET 统一基线**

---

## 📖 查询

### 全表查询（10,000 行）

```
ADO.NET  ████████████████████████████████████  3.9 ms   100%
Dapper   ████████████████████████████████████████████████████  6.2 ms  159%
PalORM   ██████████████████████████████████████████████████████  6.9 ms  177%
```

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_QueryAll** | 3.9 ms | **100%** | 1,329 KB | **100%** |
| Dapper_QueryAll | 6.2 ms | **159%** | 1,352 KB | **102%** |
| PalORM_QueryAll | 6.9 ms | **177%** | 1,511 KB | **114%** |

### 主键查询（WHERE id = 5000）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_GetByKey** | 28 μs | **100%** | 1.4 KB | **100%** |
| Dapper_GetByKey | 35 μs | **125%** | 2.3 KB | **167%** |
| PalORM_GetByKey | 65 μs | **232%** | 4.6 KB | **328%** |

---

## ✏️ 插入（INSERT + 取回自增 ID）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Insert** | 30 μs | **100%** | 1.4 KB | **100%** |
| Dapper_Insert | 58 μs | **193%** | 3.7 KB | **261%** |
| **PalORM_Insert** | **30 μs** | **100%** 🟢 | 5.1 KB | **357%** |

> 🟢 PalORM 插入与 ADO.NET **完全持平**（30μs = 30μs），且比 Dapper 快 2 倍

---

## 🔄 更新（Set().Where().ExecuteNonQueryAsync 单步）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Update** | 19 μs | **100%** | 0.9 KB | **100%** |
| Dapper_Update | 20 μs | **105%** | 2.3 KB | **249%** |
| PalORM_Update | 24 μs | **126%** | 8.1 KB | **886%** |
| PalORM_Update_OptimisticLock | 24 μs | **126%** | 6.6 KB | **720%** |

> ⭐ 乐观锁 `[ConcurrencyCheck]` 与普通 Update 性能等价（24μs = 24μs）

---

## 🗑️ 删除

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Delete** | 39 μs | **100%** | 1.8 KB | **100%** |
| Dapper_Delete | 33 μs | **85%** | 4.7 KB | **263%** |
| PalORM_Delete_Physical | 40 μs | **103%** 🟢 | 6.0 KB | **339%** |
| PalORM_Delete_SoftDelete | 35 μs | **90%** | 5.8 KB | **329%** |

> 🟢 PalORM 物理删除与 ADO.NET **几乎持平**（40 vs 39μs）
> ⭐ 软删除比物理删除更快（35μs < 40μs）——UPDATE 比 DELETE 少索引更新

---

## 🔀 UPSERT (ON CONFLICT)

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Upsert** | 19 μs | **100%** | 1.0 KB | **100%** |
| Dapper_Upsert | 19 μs | **100%** | 2.8 KB | **287%** |
| PalORM_Save_Upsert | 32 μs | **168%** | 7.7 KB | **779%** |

---

## 📦 批量

| 方法 | 耗时 | 分配 |
|:-----|-----:|-----:|
| Dapper_MultiRowInsert_10000 | 29.0 ms | 13,282 KB |
| PalORM_BulkInsert_10000 | 203.8 ms | 16,624 KB |
| PalORM_BulkUpdate_1000 | 4.3 ms | 1,627 KB |
| PalORM_BulkDelete_500 | 6.1 ms | 1,039 KB |

---

## 🔄 事务

| 方法 | 耗时 | 分配 |
|:-----|-----:|-----:|
| Transaction_Commit（3 条 Insert） | 62 μs | 17.4 KB |
| Transaction_Rollback | 57 μs | 17.7 KB |
| Transaction_Savepoint | 48 μs | 15.3 KB |

---

## ⭐ PalORM 独有特性

| 方法 | 耗时 | 分配 | 说明 |
|:-----|-----:|-----:|:-----|
| Query_SoftDelete_Filter | 33 μs | 5.4 KB | [SoftDelete] 自动 WHERE 零代码 |
| Query_WhereIn_500 | 1.3 ms | 293 KB | 自动分批 IN（参数上限钳制） |
| Query_WithTracing | 10.0 ms | 1,509 KB | ActivitySource **零额外分配** |

---

## 🔨 SQL 构建（零 I/O）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **StringBuilder** | 56 ns | **100%** | 1.46 KB | **100%** |
| PalORM_Simple | 220 ns | 393% | 1.05 KB | **72%** 🟢 |
| PalORM_Complex | 267 ns | 477% | 1.46 KB | 100% |

---

## 🏆 性能总结

| 操作 | ADO.NET | Dapper | **PalORM** | 评价 |
|------|:---:|:---:|:---:|:---|
| 全表查询 10K | 100% | 159% | 177% | ⚠️ 物化开销 |
| 主键查询 | 100% | 125% | 232% | ⚠️ 会话创建开销 |
| **插入** | 100% | 193% | **100%** 🟢 | ✅ 与 ADO.NET 持平 |
| **更新** | 100% | 105% | **126%** | ✅ 接近 ADO.NET |
| **删除（物理）** | 100% | 85% | **103%** 🟢 | ✅ 与 ADO.NET 持平 |
| **删除（软删）** | 100% | — | **90%** 🟢 | ✅ 比 ADO.NET 快 |
| UPSERT | 100% | 100% | 168% | ⚠️ UPSERT 路径开销 |
| SQL 构建 | 100% | — | **72% 分配** | 🟢 少 28% 分配 |
| 事务 | — | — | 62μs/3 条 | ✅ 轻量 |

> **真实场景说明**：每个基准方法内部完整创建连接/会话（`using var`），模拟真实请求生命周期。
> PalORM 的 `DataSession.CreateAsync` 包含连接打开 + PRAGMA 初始化 + ResilienceExecutor 创建——
> 这是会话隔离 + 单活动操作门禁的架构保证，固定开销约 10-20μs/次。

---

## 运行方法

```bash
# 全部基准（~4 分钟）
dotnet run --project bench/PalORM.Benchmarks -c Release

# 按操作筛选
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Query*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Insert*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Update*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Delete*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Upsert*'
```
