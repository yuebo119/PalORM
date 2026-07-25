# 3.4 NpgsqlParameter<T> 零装箱 — 基准测试设计方案

> 状态：**设计方案（未实施）** · 来源：v5.0-roadmap 阶段 3.4 推迟后的验证需求
> 决策：用户批准"不做实现，但设计完整方案供未来验证"

## 背景

`CommandFactoryEmitter.GetParameterValueExpression` 生成 `p.Value = (object)entity.Id`——所有值类型列（int/long/decimal/DateTime 等）在赋值给 `DbParameter.Value`（类型 object）时**发生装箱**。

一行 10 列 = 10 次装箱 = 10 个堆分配。BulkInsert 10000 行 = 100,000 次装箱。

Npgsql 提供 `NpgsqlParameter<T>.TypedValue` 避免装箱，但跨方言不一致（SQLite/MySQL 无等价 API）。

## 设计目标

验证以下核心问题（决定 3.4 是否值得实施）：

1. **装箱在总 GC 压力中的占比**——是主要矛盾还是 <5% 可忽略？
2. **BulkInsert vs 单条 Insert 的装箱差异**——多值 INSERT 与逐条 INSERT 哪个装箱更多？
3. **PG/MySQL/SQLite 三方言装箱对比**——是否 PG 独有优化（NpgsqlParameter<T>）就能覆盖？
4. **装箱与总分配的比率**——v4.6 已用 BindInsertValues 把 BulkInsert 10K 分配降到 4.97MB，装箱占多少？

## 测试矩阵

### 维度 1：行数 × 列数

| 行数 | 列数 | 总装箱次数 | 预期场景 |
|:---:|:---:|:---:|------|
| 1 | 5 | 5 | 单条 Insert，装箱占比最小 |
| 100 | 5 | 500 | 小批量 |
| 1000 | 5 | 5000 | 中批量 |
| 10000 | 5 | 50000 | 大批量（v4.6 基准基线） |
| 10000 | 20 | 200000 | 宽表大批量（装箱放大） |

### 维度 2：操作类型

| 操作 | 装箱路径 | 备注 |
|------|------|------|
| InsertAsync（单条） | GetParameterValueExpression emit `(object)` | 逐条装箱 |
| BulkInsertAsync（多值 INSERT） | 同上，但 BindInsertValues 复用 DbParameter[] | v4.6 优化路径 |
| UpdateAsync（单条） | BindUpdate emit `(object)` | 与 Insert 同 |
| QueryAsync（读取） | RowFactoryEmitter GetInt32/GetDateTime 等 | **不装箱**（值类型直接读） |

### 维度 3：方言

| 方言 | 装箱点 | 优化可行性 |
|------|------|:---:|
| PostgreSQL | NpgsqlParameter.Value = (object) | ✓ NpgsqlParameter<T>.TypedValue |
| MySQL | MySqlParameter.Value = (object) | ✗ MySqlConnector 2.6 无泛型参数 |
| SQLite | SqliteParameter.Value = (object) | ✗ MDS 无泛型参数 |

## 基准实现设计

### 文件位置

`bench/PalORM.Benchmarks/BoxingBenchmarks.cs`（新建）

### BenchmarkDotNet 配置

```csharp
[MemoryDiagnoser]  // 关键：报告 GC 分配/堆分配
[HideColumns("Error", "StdDev", "Median")]  // 聚焦分配数据
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class BoxingBenchmarks : IAsyncDisposable
{
    // Params 矩阵——行数 × 列数
    [Params(1, 100, 1000, 10000)]
    public int RowCount { get; set; }

    [Params(5, 20)]
    public int ColumnCount { get; set; }

    // 实体按 ColumnCount 动态生成——用 BenchOrder(5 列) 或宽表实体(20 列)
    private DataSession<SqliteProvider> _db = null!;
    private List<BenchOrder> _entities = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _db = await TestDb.SqliteAsync();
        await _db.MigrateAsync();
        _entities = Enumerable.Range(0, RowCount)
            .Select(i => new BenchOrder { ... }).ToList();
    }

    // 基准 1：单条 InsertAsync（逐条装箱）
    [Benchmark(Baseline = true), BenchmarkCategory("Insert")]
    public async Task Insert_OneByOne()
    {
        foreach (var e in _entities)
            await _db.InsertAsync(e);
    }

    // 基准 2：BulkInsertAsync（BindInsertValues 复用路径，装箱但省 CreateParameter）
    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> BulkInsert()
        => await _db.BulkInsertAsync(_entities);

    // 基准 3：QueryAsync（无装箱对照组——证明读取路径不装箱）
    [Benchmark, BenchmarkCategory("Query")]
    public async Task<List<BenchOrder>> Query()
        => await _db.From<BenchOrder>().ToListAsync();
}
```

### GC Profiler 扩展

除 BenchmarkDotNet 的 `[MemoryDiagnoser]`（报告 Gen0/1/2 + Allocated bytes），加 DotNet-counters 实时监控：

```bash
# 运行基准的同时监控 GC
dotnet-counters monitor --process-id <pid> \
  --counters System.Runtime[gc-heap-size,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,alloc-rate]
```

或用 `dotnet-gcdump` 在基准运行后抓堆快照，分析装箱对象类型分布：

```bash
dotnet-gcdump collect --process-id <pid> -o boxing.gcdump
# 用 PerfView/dotnet-gcdump 分析 Int32/Int64/Decimal 等装箱对象数量
```

### PG 专属对比基准

```csharp
[MemoryDiagnoser]
public class PgBoxingBenchmarks
{
    [Params(10000)]
    public int RowCount { get; set; }

    [Benchmark(Baseline = true)]
    public async Task<long> PalORM_BulkInsert_Current()
        => await _db.BulkInsertAsync(_entities);  // 当前装箱路径

    // 假设性对照——手动用 NpgsqlParameter<T>（不装箱），仅在实施 3.4 后才能对比
    // 当前不实现，留作未来 3.4 实施后的验证基准
    // [Benchmark]
    // public async Task<long> PalORM_BulkInsert_TypedParameter()
    //     => await _db.BulkInsertAsync(_entities);  // 改用 NpgsqlParameter<T>
}
```

## 验证指标

### 决策指标（决定 3.4 是否实施）

| 指标 | 阈值 | 含义 |
|------|:---:|------|
| **装箱占总分配比** | >20% | 装箱是主要矛盾，3.4 值得做 |
| | 5-20% | 次要矛盾，看其他优化是否更优 |
| | <5% | 可忽略，3.4 不值得做 |
| **Gen0 GC 频率** | BulkInsert 10K 触发 >10 次 Gen0 | 装箱造成频繁 GC |
| **宽表装箱放大** | 20 列 vs 5 列分配增加 >4x（线性放大）| 证明装箱是线性增长 |

### 执行命令

```bash
# SQLite 装箱基准（无需外部 DB）
dotnet run --project bench/PalORM.Benchmarks -- -c Release -f '*Boxing*' --filter '*Insert*'

# PG 装箱基准（需 PG 17+）
PALORM_PG_CONNECTION="Host=...;" dotnet run --project bench/PalORM.Benchmarks -- -c Release -f '*PgBoxing*'

# 含 GC 实时监控
dotnet-counters monitor --counters System.Runtime &
dotnet run --project bench/PalORM.Benchmarks -- -c Release -f '*Boxing*'
```

## 决策树（基于基准结果）

```
装箱占总分配比 > 20%？
├─ 是 → 3.4 值得实施
│   ├─ PG 独占优化（NpgsqlParameter<T>）覆盖 >80% 装箱？
│   │   ├─ 是 → 仅 PG 路径优化（接受跨方言不一致）
│   │   └─ 否 → 等待 MySqlConnector/MDS 提供泛型参数 API
│   └─ 快照重生成本可接受？（13 个快照）
│       └─ 是 → 实施
└─ 否（<20%）→ 3.4 不实施，装箱是次要矛盾
    └─ 文档说明装箱占比，用户知晓权衡
```

## 替代方案（如果 3.4 不实施）

1. **BindInsertValues 复用**（v4.6 已实现）：预分配 DbParameter[]，只改 Value（仍装箱但省 CreateParameter）
2. **PG COPY 协议**（已有）：BulkInsert 走 BinaryImporter，参数直接 WriteAsync(value, NpgsqlDbType)——**不经过 DbParameter.Value**，无装箱
3. **MySQL BulkCopy**（v5.0 阶段 4.2 已实现）：DataTable 路径，装箱在 DataTable 行填充阶段

**关键洞察**：PG COPY 和 MySQL BulkCopy 两条最优批量路径**已经绕开了装箱**（不走 DbParameter.Value）。3.4 的 NpgsqlParameter<T> 只优化"多值 INSERT 路径"的装箱——而这已是次优路径。

## 未验证项（实施时需补）

- [ ] NpgsqlParameter<T>.TypedValue 在 Npgsql 10 的 AOT 兼容性
- [ ] MySqlConnector 2.6+ 是否计划提供 MySqlParameter<T>
- [ ] MDS（Microsoft.Data.Sqlite）是否计划提供 SqliteParameter<T>
- [ ] 装箱对象在 LOH（大对象堆）还是 SOH（小对象堆）——影响 GC 代际

## 参考

- v4.6 CHANGELOG：BulkInsert 10K 分配从 10.66MB 降到 4.97MB（BindInsertValues 优化）
- NpgsqlParameter<T> 文档：https://www.npgsql.org/doc/api/Npgsql.NpgsqlParameter-1.html
- BenchmarkDotNet MemoryDiagnoser：https://benchmarkdotnet.org/articles/configs/diagnosers/memory.html
