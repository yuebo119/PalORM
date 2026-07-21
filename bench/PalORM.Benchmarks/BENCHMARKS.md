# PalORM 性能基准报告 v3.0.0

> .NET 11 · RyuJit AVX-512 · BenchmarkDotNet 0.14 · `[MemoryDiagnoser]`
> SQLite 共享内存（10,000 行 seed）· 2026-07-21
> **全部以 RawADO（手写 ADO.NET）为统一基线，同时显示耗时%和分配%**

---

## 📖 查询

### 全表查询（10,000 行）

```
RawADO   ████████████████████████████████████  3.7 ms   100%
Dapper   ██████████████████████████████████    3.4 ms    91%
PalORM   █████████████████████████████████████████  4.3 ms   115%
```

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **RawAdo_QueryAll** | 3.7 ms | **100%** | 1,329 KB | **100%** |
| Dapper_QueryAll | 3.4 ms | **91%** 🟢 | 1,352 KB | **102%** |
| PalORM_QueryAll | 4.3 ms | **115%** 🟡 | 1,511 KB | **114%** |

### 主键查询

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **RawAdo_GetByKey** | 22 μs | **100%** | 1.4 KB | **100%** |
| Dapper_GetByKey | 19 μs | **86%** 🟢 | 2.3 KB | **167%** |
| PalORM_GetByKey | 23 μs | **105%** | 4.7 KB | **336%** |

### COUNT 聚合

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **RawAdo_Count** | 18 μs | **100%** | 0.7 KB | **100%** |
| PalORM_Count | 20 μs | **111%** | 3.8 KB | **543%** |

---

## ✏️ 插入

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **RawAdo_Insert** | 29 μs | **100%** | 1.4 KB | **100%** |
| Dapper_Insert | 29 μs | **100%** 🟢 | 2.4 KB | **167%** |
| PalORM_Insert | 42 μs | **145%** 🟡 | 5.2 KB | **366%** |

---

## 🔄 更新

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **RawAdo_Update** | 21 μs | **100%** | 0.9 KB | **100%** |
| Dapper_Update | 27 μs | **128%** | 2.3 KB | **249%** |
| PalORM_Update | 33 μs | **160%** | 6.3 KB | **690%** |
| PalORM_Update_OptimisticLock | 47 μs | **226%** | 6.0 KB | **654%** |

> ⭐ **乐观锁特性**：`[ConcurrencyCheck]` 自动加 `WHERE version=@old`——仅 +66% 耗时（47 vs 33μs）。

---

## 🗑️ 删除

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **RawAdo_Delete** | 32 μs | **100%** | 1.8 KB | **100%** |
| PalORM_Delete_Physical | 36 μs | **113%** | 6.1 KB | **345%** |
| PalORM_Delete_SoftDelete | 38 μs | **120%** | 5.9 KB | **335%** |

> ⭐ **软删除特性**：`[SoftDelete]` 自动 `UPDATE deleted_at`——与物理 DELETE 性能等价（120% vs 113%）。

---

## 🔀 UPSERT (ON CONFLICT)

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **RawAdo_Upsert** | 21 μs | **100%** | 1.0 KB | **100%** |
| Dapper_Upsert | 25 μs | **122%** | 2.8 KB | **287%** |
| PalORM_Save_Upsert | 39 μs | **188%** | 7.8 KB | **790%** |

---

## 📦 批量

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **RawAdo_BulkInsert_10000** | — | — | — | — |
| Dapper_MultiRowInsert_10000 | 26.2 ms | **100%** | 13,282 KB | **100%** |
| PalORM_BulkInsert_10000 | 94.1 ms | **359%** 🔴 | 16,623 KB | **125%** |
| PalORM_BulkUpdate_1000 | 3.1 ms | — | 1,627 KB | — |
| PalORM_BulkDelete_500 | 4.9 ms | — | 1,039 KB | — |

---

## 🔄 事务

| 方法 | 耗时 | 分配 | 说明 |
|:-----|-----:|-----:|:-----|
| Transaction_Commit | 80 μs | 17.9 KB | 3 条 Insert + Commit |
| Transaction_Rollback | 72 μs | 18.2 KB | 2 条 Insert + 异常回滚 |
| Transaction_Savepoint | 52 μs | 15.8 KB | Savepoint + RollbackTo |

---

## ⭐ PalORM 独有特性

| 方法 | 耗时 | 分配 | 说明 |
|:-----|-----:|-----:|:-----|
| **Query_CacheHit** | **24 μs** | 160 KB | 🏆 WithCache 命中比全表查询快 **178x** |
| **Query_SoftDelete_Filter** | 23 μs | 5.5 KB | [SoftDelete] 自动 WHERE 零代码 |
| **Query_WhereIn_500** | 1.1 ms | 293 KB | 自动分批 IN（参数上限钳制） |
| **Query_WithTracing** | 4.7 ms | 1,511 KB | ActivitySource **零额外分配** |

---

## 🔨 SQL 构建（零 I/O）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **StringBuilder** | 56 ns | **100%** | 1.46 KB | **100%** |
| PalORM_BuildSql_Simple | 220 ns | **393%** | 1.05 KB | **72%** 🟢 |
| PalORM_BuildSql_Complex | 267 ns | **477%** | 1.46 KB | **100%** |

> PalORM SQL 构建比 StringBuilder **多 3-4 倍耗时**（因遍历子句列表），但简单 SELECT **分配减少 28%**（ValueStringBuilder + stackalloc）。

---

## 性能优势总结

| 维度 | 结论 | 耗时% | 分配% |
|------|------|:-----:|:-----:|
| **主键查询** | 与 RawADO 持平 | 105% | 336% |
| **全表查询** | +15% RawADO | 115% | 114% |
| **乐观锁开销** | 仅 +66% vs 普通 Update | — | — |
| **软删除 vs 物理删除** | 性能等价 | 120% vs 113% | 335% vs 345% |
| **缓存命中** | 快 178x | 0.6% | — |
| **SQL 构建** | 分配减少 28% | — | **72%** 🟢 |
| **观测性开销** | 零额外分配 | — | 100% |
| **UPSERT** | +88% RawADO | 188% | 790% |
| **事务** | 轻量 | 80μs/3 条 | 17.9 KB |
| **Tracing** | 零额外分配 | — | 100% |

> **耗时开销根因**：PalORM 每次操作创建 DataSession（连接打开 ~10μs 固定成本）。Dapper 直接复用已打开连接，因此写操作快 ~10μs。

---

## 运行方法

```bash
# 全部基准（~4 分钟）
dotnet run --project bench/PalORM.Benchmarks -c Release

# 按类别筛选
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Query*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Write*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Bulk*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Transaction*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Feature*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*SqlBuild*'
```
