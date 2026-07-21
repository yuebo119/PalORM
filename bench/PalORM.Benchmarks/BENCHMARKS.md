# PalORM 性能基准报告 v3.0.0

> .NET 11 · RyuJit AVX-512 · BenchmarkDotNet 0.14 · `[MemoryDiagnoser]`
> SQLite 共享内存（10,000 行 seed）· 2026-07-21
> **统一基线：ADO.NET · 共享 DataSession（连接复用对等）· 每个 ORM 最优路径**

---

## 📖 查询

### 全表查询（10,000 行）

```
ADO.NET  ████████████████████████████████████████████████████  5.5 ms   100%
Dapper   ███████████████████████████████████████████████████   5.4 ms    98%
PalORM   ██████████████████████████████████████████████████████████  6.5 ms   118%
```

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_QueryAll** | 5.5 ms | **100%** | 1,329 KB | **100%** |
| Dapper_QueryAll | 5.4 ms | **98%** 🟢 | 1,352 KB | **102%** |
| PalORM_QueryAll | 6.5 ms | **118%** | 1,511 KB | **114%** |

### 主键查询（WHERE id = 5000）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_GetByKey** | 26 μs | **100%** | 1.4 KB | **100%** |
| Dapper_GetByKey | 26 μs | **100%** | 2.3 KB | **167%** |
| **PalORM_GetByKey** | **5 μs** | **19%** 🏆 | 2.2 KB | **154%** |

> 🏆 PalORM 主键查询比 ADO.NET **快 5 倍**——源生成 RowFactory 零反射 + 直接 Dictionary 查找

---

## ✏️ 插入（INSERT + 取回自增 ID）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Insert** | 31 μs | **100%** | 1.4 KB | **100%** |
| Dapper_Insert | 35 μs | **113%** | 3.7 KB | **261%** |
| **PalORM_Insert** | **10 μs** | **32%** 🏆 | 2.7 KB | **186%** |

> 🏆 PalORM 插入比 ADO.NET **快 3 倍**——RETURNING 单次往返 + 源生成 BindInsert

---

## 🔄 更新（Set().Where().ExecuteNonQueryAsync 单步）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Update** | 28 μs | **100%** | 0.9 KB | **100%** |
| Dapper_Update | 30 μs | **107%** | 2.3 KB | **249%** |
| **PalORM_Update** | **6 μs** | **21%** 🏆 | 5.7 KB | **621%** |
| **PalORM_Update_OptimisticLock** | **5 μs** | **18%** 🏆 | 4.1 KB | **453%** |

> 🏆 PalORM 更新比 ADO.NET **快 4.7 倍**——源生成 SQL 构建 + 参数绑定
> ⭐ 乐观锁 `[ConcurrencyCheck]` 甚至比普通 Update 更快（5μs vs 6μs）

---

## 🗑️ 删除

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Delete** | 27 μs | **100%** | 1.8 KB | **100%** |
| Dapper_Delete | 33 μs | **122%** | 4.7 KB | **263%** |
| **PalORM_Delete_Physical** | **13 μs** | **48%** 🏆 | 3.6 KB | **200%** |
| **PalORM_Delete_SoftDelete** | **14 μs** | **52%** 🏆 | 3.4 KB | **190%** |

> 🏆 PalORM 删除比 ADO.NET **快 2 倍**
> ⭐ 软删除与物理删除性能等价（14μs vs 13μs）

---

## 🔀 UPSERT (ON CONFLICT)

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **ADO.NET_Upsert** | 29 μs | **100%** | 1.0 KB | **100%** |
| Dapper_Upsert | 29 μs | **100%** | 2.8 KB | **287%** |
| **PalORM_Save_Upsert** | **12 μs** | **41%** 🏆 | 5.2 KB | **529%** |

> 🏆 PalORM UPSERT 比 ADO.NET **快 2.4 倍**

---

## 📦 批量

| 方法 | 耗时 | 分配 |
|:-----|-----:|-----:|
| Dapper_MultiRowInsert_10000 | 39.9 ms | 13,282 KB |
| PalORM_BulkInsert_10000 | 120.9 ms | 16,623 KB |
| PalORM_BulkUpdate_1000 | 3.2 ms | 1,621 KB |
| PalORM_BulkDelete_500 | 6.3 ms | 1,032 KB |

---

## 🔄 事务

| 方法 | 耗时 | 分配 |
|:-----|-----:|-----:|
| Transaction_Commit（3 条 Insert） | 32 μs | 10.0 KB |
| Transaction_Rollback | 41 μs | 10.3 KB |
| Transaction_Savepoint | 30 μs | 8.0 KB |

---

## ⭐ PalORM 独有特性

| 方法 | 耗时 | 分配 | 说明 |
|:-----|-----:|-----:|:-----|
| **Query_CacheHit** | **8 μs** | 157 KB | 🏆 缓存命中比全表查询快 **687x** |
| **Query_SoftDelete_Filter** | **5 μs** | 2.9 KB | [SoftDelete] 自动 WHERE 零代码 |
| Query_WhereIn_500 | 1.4 ms | 291 KB | 自动分批 IN |
| Query_WithTracing | 6.5 ms | 1,509 KB | ActivitySource **零额外分配** |

---

## 🔨 SQL 构建（零 I/O）

| 方法 | 耗时 | 耗时% | 分配 | 分配% |
|:-----|-----:|:-----:|-----:|:-----:|
| **StringBuilder** | 56 ns | **100%** | 1.46 KB | **100%** |
| PalORM_Simple | 220 ns | 393% | 1.05 KB | **72%** 🟢 |
| PalORM_Complex | 267 ns | 477% | 1.46 KB | 100% |

---

## 🏆 性能总结

| 操作 | ADO.NET | Dapper | **PalORM** | PalORM 优势 |
|------|:---:|:---:|:---:|:---|
| 全表查询 10K 行 | 100% | 98% | 118% | ⚠️ +18%（物化 10K 行） |
| **主键查询** | 100% | 100% | **19%** | 🏆 **快 5x** |
| **插入** | 100% | 113% | **32%** | 🏆 **快 3x** |
| **更新** | 100% | 107% | **21%** | 🏆 **快 4.7x** |
| **乐观锁更新** | — | — | **18%** | 🏆 比普通 Update 更快 |
| **删除** | 100% | 122% | **48%** | 🏆 **快 2x** |
| **UPSERT** | 100% | 100% | **41%** | 🏆 **快 2.4x** |
| 缓存命中 | — | — | **8 μs** | 🏆 快 **687x** |
| SQL 构建 | 100% | — | **72% 分配** | 🟢 少 28% 分配 |
| 事务（3 Insert+Commit） | — | — | **32 μs** | ✅ 轻量 |

> **为什么 PalORM 写操作比 ADO.NET 快？** 共享 DataSession 后，PalORM 的源生成 SQL 构建 + 参数绑定直接走预编译委托（`Action<DbCommand, object>`），跳过 ADO.NET 的 `CreateCommand()` + 手动 `Parameters.Add()` 开销。
>
> **全表查询为什么慢 18%？** PalORM 物化 10K 行时，每行调用 `IRowFactory<T>.Read(reader)` 委托——比手写 `r.GetInt64(0)` 多一次间接调用。这是源生成模式的固有代价。

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
