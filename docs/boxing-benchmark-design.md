# 3.4 NpgsqlParameter<T> 零装箱 — 基准测试结果与决策

> 状态：**已实施基准 + 已决策不做** · 来源：v5.0-roadmap 阶段 3.4
> 实施证据：`bench/PalORM.Benchmarks/BoxingMicroBenchmark.cs`（手写微基准，绕过 BDN .NET 11 不兼容）

## 实测结果（2026-07-25）

运行环境：.NET 11.0.0-preview.6.26359.118 | SQLite :memory: | 4 列实体（long/int/decimal/bool）

| 操作 × 行数 | 总分配 | B/行 | 装箱估算 B/行 | 装箱占比 |
|------|:---:|:---:|:---:|:---:|
| InsertAsync × 1 | 2.66 KB | 2720 | ~108 | 4.0% |
| BulkInsertAsync × 1 | 3.68 KB | 3768 | ~108 | 2.9% |
| InsertAsync × 100 | 262.46 KB | 2688 | ~108 | 4.0% |
| BulkInsertAsync × 100 | 106.93 KB | 1095 | ~108 | 9.9% |
| InsertAsync × 1000 | 2630.16 KB | 2693 | ~108 | 4.0% |
| BulkInsertAsync × 1000 | 636.45 KB | 652 | ~108 | 16.6% |
| InsertAsync × 10000 | 26395.79 KB | 2703 | ~108 | 4.0% |
| **BulkInsertAsync × 10000** | **5092.56 KB** | **521** | **~108** | **20.7%** |
| QueryAsync × 10000 | 1581.06 KB | 162 | 0 | 0% |

## 决策结论：**3.4 不做**

### 装箱占比看似接近阈值，但实际收益极小

BulkInsertAsync 10K 装箱占比 ~20.7%（接近值得做阈值 20%），但关键事实：

1. **PG/MySQL 大批量已无装箱**——PG 走 COPY 协议（NpgsqlBinaryImporter.WriteAsync(value, NpgsqlDbType)），MySQL 走 BulkCopy（DataTable 路径）。两条最优路径**不走 DbParameter.Value**，无装箱
2. **SQLite 无泛型参数 API**——MDS 不提供 `SqliteParameter<T>`，3.4 即使做也只能优化 PG 多值 INSERT 路径
3. **PG 多值 INSERT 是次优路径**——PG 大批量应走 COPY（已优化）。多值 INSERT 仅在小批量或无 COPY 场景使用
4. **InsertAsync 逐条装箱仅 4%**——装箱在逐条路径中占比很小（每次 Insert 的非装箱分配更大）

### 3.4 的实际优化目标（如果做）

| 路径 | 是否有装箱 | 3.4 能否优化 | 实际价值 |
|------|:---:|:---:|:---:|
| PG COPY（BulkInsert） | ❌ 无 | N/A | 已无装箱 |
| MySQL BulkCopy（BulkInsert） | ❌ 无 | N/A | 已无装箱 |
| PG 多值 INSERT | ✓ 有 | ✓ NpgsqlParameter<T> | **低**（次优路径） |
| MySQL 多值 INSERT | ✓ 有 | ❌ 无 API | 不可优化 |
| SQLite 多值 INSERT | ✓ 有 | ❌ 无 API | 不可优化 |
| 逐条 Insert/Update | ✓ 有 | ✓ PG 可优化 | **低**（4% 占比） |

**3.4 的唯一实际收益**：PG 多值 INSERT 路径（次优）+ PG 逐条 Insert/Update——都是边缘场景。

### 替代方案（已覆盖）

- **PG COPY**（已有）：BulkInsert 走 BinaryImporter，无装箱
- **MySQL BulkCopy**（v5.0 阶段 4.2）：BulkInsert 走 LOAD DATA LOCAL INFILE，无装箱
- **BindInsertValues**（v4.6）：预分配 DbParameter[] 复用，省 CreateParameter/Add（仍装箱但省更大分配）

## 触发重新评估的条件

- MySqlConnector 或 MDS 提供泛型参数 API（`MySqlParameter<T>` / `SqliteParameter<T>`）
- 用户反馈 PG **逐条高频** Insert/Update 有 GC 压力（非 BulkInsert 场景）
- NpgsqlParameter<T> 在 Npgsql 10 的 AOT 兼容性已验证

## 基准实施说明

BenchmarkDotNet 0.15.8 与 .NET 11 preview SDK 不兼容（`GetRuntimeVersion not implemented for NotRecognized`）。
本基准用**手写微基准**（`GC.GetAllocatedBytesForCurrentThread()`）替代，纯 BCL API 任何 .NET 版本可用。

```bash
# 运行装箱基准
dotnet run --project bench/PalORM.Benchmarks -c Release -- --boxing
```

## 原始设计方案（已被实测结果取代）

旧的设计方案（测试矩阵/BDN 配置/决策树）已被上方实测结果取代，不再保留。原始内容见 git 历史 commit 0c6c8ba。
