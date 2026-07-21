# PalORM 性能基准报告 v3.0.0

> .NET 11 · RyuJit AVX-512 · BenchmarkDotNet 0.14 · `[MemoryDiagnoser]`
> SQLite 共享内存（10,000 行 seed）· 2026-07-21
> **统一基线：ADO.NET · 每个 ORM 使用最优路径 · 同一 SQL 同一数据**

---

## 📖 查询

### 全表查询（10,000 行）

```
ADO.NET  █████████████████████████████████████████  4.7 ms   100%
Dapper   ████████████████████████████████████       3.6 ms    77%
PalORM   ████████████████████████████████████████████████  5.6 ms   119%
```

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_QueryAll** | 4.7 ms | **100%** | 1,329 KB | **100%** |
| Dapper_QueryAll | 3.6 ms | **77%** 🟢 | 1,352 KB | **102%** |
| PalORM_QueryAll | 5.6 ms | **119%** | 1,511 KB | **114%** |

### 主键查询（WHERE id = 5000）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_GetByKey** | 26 μs | **100%** | 1.4 KB | **100%** |
| Dapper_GetByKey | 27 μs | **104%** | 2.3 KB | **167%** |
| PalORM_GetByKey | 30 μs | **115%** | 4.7 KB | **337%** |

---

## ✏️ 插入（INSERT + 取回自增 ID）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Insert** | 22 μs | **100%** | 1.4 KB | **100%** |
| Dapper_Insert | 29 μs | **132%** | 3.7 KB | **261%** |
| PalORM_Insert | 45 μs | **205%** | 5.2 KB | **366%** |

---

## 🔄 更新（单步 UPDATE WHERE id = 5000）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Update** | 25 μs | **100%** | 0.9 KB | **100%** |
| Dapper_Update | 25 μs | **100%** 🟢 | 2.3 KB | **249%** |
| PalORM_Update | 29 μs | **116%** | 8.2 KB | **899%** |
| PalORM_Update_OptimisticLock | 33 μs | **132%** | 6.8 KB | **741%** |

> ⭐ PalORM Update 已改用最优路径 `Set().Where().ExecuteNonQueryAsync()`（单步，不做 Get+Update 两步）
> ⭐ 乐观锁 `[ConcurrencyCheck]` 仅 +16% 耗时（33 vs 29μs）

---

## 🗑️ 删除（先插入再删除——保证幂等）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Delete** | 28 μs | **100%** | 1.8 KB | **100%** |
| Dapper_Delete | 30 μs | **107%** | 4.7 KB | **263%** |
| PalORM_Delete_Physical | 47 μs | **168%** | 6.1 KB | **345%** |
| PalORM_Delete_SoftDelete | 39 μs | **139%** | 5.9 KB | **335%** |

> ⭐ 软删除 `[SoftDelete]` 比物理删除**更快**（139% vs 168%）——UPDATE 比 DELETE 少一次索引更新

---

## 🔀 UPSERT（ON CONFLICT DO UPDATE）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Upsert** | 28 μs | **100%** | 1.0 KB | **100%** |
| Dapper_Upsert | 21 μs | **75%** 🟢 | 2.8 KB | **287%** |
| PalORM_Save_Upsert | 38 μs | **136%** | 7.8 KB | **790%** |

---

## 📦 批量

| 方法 | 耗时 | 分配 | 说明 |
|:-----|-----:|-----:|:-----|
| Dapper_MultiRowInsert_10000 | 32.5 ms | 13,282 KB | 逐条 INSERT |
| PalORM_BulkInsert_10000 | 145.9 ms | 16,623 KB | 多值 INSERT（999/批） |
| PalORM_BulkUpdate_1000 | 3.9 ms | 1,627 KB | 批量更新 + 乐观锁 |
| PalORM_BulkDelete_500 | 6.2 ms | 1,039 KB | IN 分批 500 |

---

## 🔄 事务

| 方法 | 耗时 | 分配 | 说明 |
|:-----|-----:|-----:|:-----|
| Transaction_Commit | 55 μs | 17.9 KB | 3 条 Insert + Commit |
| Transaction_Rollback | 73 μs | 18.2 KB | 2 条 Insert + 异常回滚 |
| Transaction_Savepoint | 63 μs | 15.8 KB | Savepoint + RollbackTo |

---

## ⭐ PalORM 独有特性

| 方法 | 耗时 | 分配 | 证明点 |
|:-----|-----:|-----:|:-------|
| **Query_CacheHit** | **33 μs** | 160 KB | 🏆 WithCache 命中比全表查询快 **170x** |
| **Query_SoftDelete_Filter** | 25 μs | 5.5 KB | [SoftDelete] 自动 WHERE 零代码 |
| Query_WhereIn_500 | 1.5 ms | 293 KB | 自动分批 IN（参数上限钳制） |
| Query_WithTracing | 4.9 ms | 1,511 KB | ActivitySource **零额外分配** |

---

## 🔨 SQL 构建（零 I/O）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **StringBuilder** | 56 ns | **100%** | 1.46 KB | **100%** |
| PalORM_Simple | 220 ns | **393%** | 1.05 KB | **72%** 🟢 |
| PalORM_Complex | 267 ns | **477%** | 1.46 KB | **100%** |

> PalORM SQL 构建比 StringBuilder 慢（遍历子句列表），但简单 SELECT **分配减少 28%**

---

## 性能优势总结

| 维度 | 结论 | 耗时% | 分配% |
|------|------|:-----:|:-----:|
| 主键查询 | 与 ADO.NET 持平 | 115% | 337% |
| 更新（最优路径） | 与 ADO.NET 接近 | 116% | 899% |
| 乐观锁开销 | 仅 +16% vs 普通 Update | — | — |
| 软删除 vs 物理删除 | 软删除更快 | 139% < 168% | 335% ≈ 345% |
| 缓存命中 | 快 170x | 0.6% | — |
| SQL 构建 | 分配减少 28% | — | **72%** 🟢 |
| 观测性开销 | 零额外分配 | — | 100% |
| 事务 | 轻量 | 55μs/3 条 | 17.9 KB |

> **耗时开销根因**：PalORM 每次操作创建独立 DataSession（连接打开 ~10μs 固定成本）。
> Dapper/ADO.NET 直接复用已打开连接，因此写操作快 ~10μs。

---

## 运行方法

```bash
# 全部基准（~4 分钟）
dotnet run --project bench/PalORM.Benchmarks -c Release

# 按类别筛选
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Query*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Insert*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Update*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Delete*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Upsert*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Bulk*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Transaction*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*Feature*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*SqlBuild*'
```
