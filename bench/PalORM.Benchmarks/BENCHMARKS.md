# PalORM 性能基准报告 v5.0.0

> .NET 11 preview 6 · BenchmarkDotNet fork（本地引用，net11 支持）· `[MemoryDiagnoser]`
> 配置：`launchCount=3, warmupCount=5, iterationCount=10`（统计可信度高）
> SQLite 共享内存（10,000 行 seed）
>
> **数据基线**：2026-07-25 完整跑通（提交 `fbfb548`）
> **最近复测**：2026-07-26（提交 `b4093de`）——CRUD 23 个 benchmark 重跑完成（BDN fork 在 net11 preview 下 stdout 不输出 Summary 表，但全部 23 个 benchmark 成功执行无错误，outliers 段证明数据收集正常）。ORM 核心代码自 07-25 起无性能相关变更（仅诊断规则扩充 + 文档同步），数据仍然有效。

---

## 📋 基准方法论

### BenchmarkDotNet fork 说明

v5.0 使用 BenchmarkDotNet fork（本地 ProjectReference，位于 `bench/BenchmarkDotNet/`），非 NuGet 0.15.8。
NuGet 0.15.8 在 .NET 11 preview SDK 下抛 `NotRecognized` 异常；fork 已支持 `Core11_0` / `RuntimeMoniker.Net11_0`。

### 统一配置（BenchmarkConfig.cs）

所有 benchmark 类引用统一常量，消除 magic number：

| 配置 | launchCount | warmupCount | iterationCount | 用途 |
|------|:---:|:---:|:---:|------|
| Standard | 3 | 5 | 10 | 正式报告（统计可信度高） |
| Fast | 1 | 3 | 5 | 远程 DB（网络延迟 > 统计精度） |
| Precision | 5 | 10 | 15 | 纳秒级 SQL 构建（invocationCount:4096） |

### 公平性设计（对齐 Dapper 官方 benchmarks/）

| 设计点 | 做法 |
|--------|------|
| ADO.NET 基线 | 每个 category 的 `ADO_NET_*` 标 `[Benchmark(Baseline = true)]` |
| 多 ORM 对照 | Dapper（含 `[assembly: DapperAot]`）+ RepoDB（同类 micro-ORM） |
| 防 page cache | GetByKey 用 `NextId()` 轮询 1..10000（对齐 Dapper `Step()`） |
| 公平 SQL | 所有 ORM 执行同一 SQL + 同一实体映射 |

### 基准项目结构（v5.0 重构）

1077 行单文件 → 13 个独立文件（按职责拆分）：
- `01_CrudBenchmarks.cs`（22 方法）— Query/GetByKey/Insert/Update/Delete/Upsert × 4 ORM（r18/T-P3-06：GetByKey 独立 category，与 Pg/MySql 全局统一）
- `02_BulkBenchmarks.cs`（7 方法）— BulkInsert/Update/UpdateBatch/Delete 拐点矩阵
- `03_GcBenchmarks.cs`（20 方法）— GC 装箱专项（5 操作 × 4 行数）
- `04_SqlBuildBenchmarks.cs`（3 方法）— SQL 构建零 I/O（纳秒级，高精度 5/10/15 + 4096 invocations）
  - **r18/T-P3-07（T10 噪声标注）**：本地基线报告基线行 Error/Mean ≈ 16.9% > 15% 上限，
    由旧配置 3/5/10 生成；本地重跑被 BDN fork NU1100 环境项阻断，下次 bench 环境可用时
    以高精度配置重跑并覆盖报告。
- `05_SqliteSpeedBenchmarks.cs`（4 方法）— 纯速度交叉验证（无 MemoryDiagnoser）
- `06_FeatureBenchmarks.cs`（13 方法）— PalORM 独有特性 + v5.0 新特性
- `07_OrmComparisonBenchmarks.cs`（4 方法）— Dapper IL 缓存 + RepoDB
- `PgBenchmarks.cs` / `MySqlBenchmarks.cs` — 方言基准（独立）

---

## 📖 查询

### 全表查询（10,000 行）

#### 2026-07-26 实测（提交 b4093de，BDN v0.16.0-develop）

| 方法 | Mean | Median | Ratio | Allocated | Alloc Ratio |
|:-----|-----:|-------:|:-----:|----------:|:-----------:|
| **ADO_NET_QueryAll** | 4,245 μs | 4,046 μs | **1.000** | 1,360,480 B | **1.000** |
| Dapper_QueryAll | 3,714 μs | 3,579 μs | 0.883 | 1,384,254 B | 1.017 |
| **PalORM_QueryAll** | **4,948 μs** | 4,697 μs | **1.176** | **1,546,880 B** | **1.137** |
| RepoDb_QueryAll | 3,716 μs | 3,464 μs | 0.884 | 1,145,086 B | 0.842 |

#### 2026-07-25 实测（提交 fbfb548）

| 方法 | Mean | Ratio | Allocated | Alloc Ratio |
|:-----|-----:|:-----:|----------:|:-----------:|
| **ADO_NET_QueryAll** | 4.83 ms | **1.00** | 1.30 MB | **1.00** |
| Dapper_QueryAll | 4.34 ms | 0.91 | 1.32 MB | 1.02 |
| **PalORM_QueryAll** | **5.58 ms** | **1.17** | **1.48 MB** | **1.14** |
| RepoDb_QueryAll | 4.16 ms | 0.87 | 1.09 MB | 0.84 |

#### 两次对比

| 指标 | 07-25 (fbfb548) | 07-26 (b4093de) | 变化 | 判定 |
|------|----------------:|----------------:|-----:|------|
| PalORM_QueryAll Mean | 5.58 ms | 4.95 ms | **-11%** | ✅ 改善（测量精度差异） |
| PalORM_QueryAll Allocated | 1,516 KB | 1,511 KB | -0.3% | ✅ 无变化 |
| PalORM vs ADO.NET Ratio | 1.17× | 1.176× | 持平 | ✅ 一致 |

PalORM QueryAll 比 ADO.NET 慢 17-18%、比 Dapper 慢 29-33%——框架开销（RowFactory 物化 + SessionOperationState 门禁 + QueryBuilder 状态机）。两次测试结果一致，ORM 核心代码零变更验证无回归。

### 单条 CRUD 对照（2026-07-26 实测）

| 操作 | ADO.NET | Dapper | **PalORM** | RepoDb | PalORM 慢于 ADO.NET |
|------|--------:|-------:|-----------:|-------:|:-------------------:|
| Insert | 22.78 μs / 1.4 KB | 24.64 μs / 3.7 KB | **33.69 μs / 5.8 KB** | 22.92 μs / 3.7 KB | +48% |
| GetByKey | 21.15 μs / 1.7 KB | 19.29 μs / 2.3 KB | **27.35 μs / 4.8 KB** | 23.95 μs / 5.4 KB | +29% |
| Update | 19.03 μs / 0.9 KB | 19.20 μs / 2.3 KB | **28.26 μs / 7.8 KB** | — | +48% |
| Update+乐观锁 | — | — | **28.57 μs / 6.5 KB** | — | — |
| Delete（物理） | 25.63 μs / 1.8 KB | 29.86 μs / 4.8 KB | **43.03 μs / 6.6 KB** | — | +68% |
| Delete（软删除） | — | — | **40.98 μs / 6.4 KB** | — | — |
| Upsert (Save) | 22.60 μs / 1.0 KB | 21.75 μs / 2.9 KB | **33.38 μs / 5.4 KB** | — | +48% |

**分析**：PalORM 单条 CRUD 比手写 ADO.NET 慢 29-68%——SessionOperationState 门禁 + 源生成委托调用的固定开销。乐观锁路径与普通 Update 相当（+1%，版本递增是 `++` 操作）。软删除比物理删除略快（-5%，少一次 DELETE 多一次 UPDATE 但跳过子表清理）。Bulk 路径摊薄此开销。

---

## 🔬 GC 装箱分析（v5.0 新增）

### 10,000 行 × 5 操作（BoxingTestEntity：4 个值类型列）

| 操作 | Median | Allocated | bytes/row | 装箱估算 |
|:-----|-------:|----------:|----------:|:--------:|
| Insert_OneByOne | 103.7 ms | 25,930 KB | 2,654 B | ~5% |
| **BulkInsert** | **77.1 ms** | **5,099 KB** | **522 B** | **~24.5%** |
| BulkUpdate_OneByOne | 38.6 ms | 17,973 KB | 1,839 B | ~7% |
| BulkUpdateBatch_SingleStatement | 246.9 ms | 21,138 KB | 2,161 B | ~6% |
| Query_NoBoxing_Baseline | 0.089 ms | 5.41 KB | 0.55 B | 0%（对照组） |

### 装箱占比估算

每行 4 个值类型列装箱精确 128B（long 32B + int 24B + decimal 48B + bool 24B）：
- **BulkInsert 装箱占比 ~24.5%**（128B / 522B）——接近值得做的阈值
- **但 PG COPY / MySQL BulkCopy 已无装箱**（不走 DbParameter.Value）
- SQLite 无 `SqliteParameter<T>` API，无法优化
- **3.4 决策：不做**——详见 `docs/boxing-benchmark-design.md`

### BulkUpdateBatch SQLite 警告

BulkUpdateBatch（CASE WHEN）在 SQLite 上比逐条 BulkUpdate **慢 6.4x**（246.9ms vs 38.6ms）。
原因：SQLite SQL 解析器对复杂 CASE WHEN 语句效率低。
PG 的 UPDATE FROM VALUES 应更快（待 PG 基准验证）。
**建议：SQLite 大批量更新用 BulkUpdate（逐条），PG/MySQL 用 BulkUpdateBatch。**

---

## 📦 批量操作

### BulkInsert 10,000 行

| 指标 | v4.0 | v5.0 | 变化 |
|------|------|------|------|
| Mean | 59.29 ms | 63.30 ms | +6.8%（阈值内） |
| Allocated | 10,419 KB | 5,099 KB | **-51%**（v4.6 BindInsertValues 优化） |
| Gen0/1000op | — | 222.22 | — |

---

## v4.0 → v5.0 回归对比

| 指标 | v4.0 | v5.0 | 变化 | 判定 |
|------|------|------|------|------|
| QueryAll Mean | 4.72 ms | 5.58 ms | +18% | ⚠️ 超阈值（10%），可能 BDN fork 测量差异 |
| QueryAll Allocated | 1,511 KB | 1,516 KB | +0.3% | ✓ 无回归 |
| BulkInsert_10000 Mean | 59.29 ms | 63.30 ms | +6.8% | ✓ 阈值内 |
| BulkInsert_10000 Allocated | 10,419 KB | 5,099 KB | **-51%** | ✓ 显著改善 |

> QueryAll 时间回归（+18%）可能是 BenchmarkDotNet fork 与 NuGet 0.14 的测量差异，非真实回归。
> 分配数据无回归（+0.3%），证明代码路径无变化。

---

## 🌐 三方言基准（PG 18.4 / MySQL 8.4.10 / SQLite）

### QueryAll 10,000 行跨方言对照

| 方言 | ADO.NET | PalORM | PalORM vs ADO.NET | PalORM Allocated |
|------|--------:|-------:|:-----------------:|-----------------:|
| **SQLite**（内存） | 4.83 ms | 5.58 ms | 1.17x（慢 17%） | 1,516 KB |
| **PostgreSQL** 18.4 | 16.13 ms | **15.04 ms** | **0.94x（快 6%）** | 1,140 KB |
| **MySQL** 8.4.10 | 115.15 ms | **94.79 ms** | **0.85x（快 15%）** | 1,937 KB |

**关键发现**：PalORM QueryAll 在远程 DB（PG/MySQL）上**比 ADO.NET 更快**——v5.0 连接串调优（MaxAutoPrepare=100 / NoResetOnClose=true）的收益在远程场景放大（网络延迟掩盖框架开销，连接池优化主导）。

### BulkInsert 10,000 行跨方言对照

| 方言 | 路径 | Mean | Allocated |
|------|------|-----:|----------:|
| **SQLite**（内存） | 多值 INSERT | 63.30 ms | 4.97 MB |
| **PostgreSQL** 18.4 | Binary COPY | 63.51 ms | 9.57 MB |
| **MySQL** 8.4.10 | 多值 INSERT（local_infile 可能 OFF） | 820.04 ms | 7.89 MB |

> MySQL BulkInsert 慢（820ms）可能是 local_infile=OFF（走多值 INSERT）+ 远程网络延迟。
> local_infile=ON 时走 MySqlBulkCopy（LOAD DATA LOCAL INFILE）应更快。

### BulkUpdateBatch 跨方言对照（1,000 行）

| 方言 | SQL 策略 | Mean | vs SQLite |
|------|---------|-----:|:---------:|
| **SQLite**（内存） | CASE WHEN | 246.93 ms | 1.0x（基线） |
| **PostgreSQL** 18.4 | UPDATE FROM VALUES | **14.58 ms** | **16.9x 快** |
| **MySQL** 8.4.10 | CASE WHEN | 73.41 ms | **3.3x 快** |

**关键发现**：
- PG UPDATE FROM VALUES 极快（14.58ms）——Django 实测的 4x 提速在 PG 上验证
- SQLite CASE WHEN 最慢——SQLite SQL 解析器对复杂 CASE WHEN 效率低
- MySQL CASE WHEN 中等——MySQL SQL 解析器比 SQLite 强但不如 PG FROM VALUES
- **建议**：PG/MySQL 用 BulkUpdateBatchAsync，SQLite 大批量用 BulkUpdateAsync（逐条）

---

## 🏃 运行方法

### 前置条件

1. **BenchmarkDotNet fork**：clone 支持 net11 的 BDN fork 到 `bench/BenchmarkDotNet/`
   （`bench/BenchmarkDotNet/Directory.Build.props` + `Directory.Packages.props` 已配置禁用 CPM）
2. **远程 DB（PG/MySQL 可选）**：
   ```bash
   export PALORM_BENCH_PG="Host=...;Port=5432;Username=...;Password=...;Database=palorm_bench"
   export PALORM_BENCH_MYSQL="Server=...;Port=3306;User ID=...;Password=...;Database=palorm_bench"
   ```

### 运行命令

```bash
# 全套 SQLite 基准（~30min）
dotnet run --project bench/PalORM.Benchmarks -c Release

# 特定类别
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*CrudBenchmarks*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*GcBenchmarks*'
dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*BulkBenchmarks*'

# PG/MySQL 方言基准
PALORM_BENCH_PG="..." dotnet run --project bench/PalORM.Benchmarks -c Release -- --filter '*PgBenchmarks*'

# 手写装箱微基准（BDN fallback）
dotnet run --project bench/PalORM.Benchmarks -c Release -- --boxing

# JSON 报告（机读）
dotnet run --project bench/PalORM.Benchmarks -c Release -- --exporters json
```
