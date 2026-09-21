# PerfHub — PalORM 统一性能测试系统

> 定位：一个命令跑完**三方言 × 三实现 × 统一数据集**的全套性能指标，
> 输出单一 HTML 报告（含 SVG 增长曲线），原始数据按次留存为 JSON。

## 快速开始

```bash
# 完整跑（三方言 × 100/1000 行 × 7 单操作 + 3 线程档并发）
dotnet run --project bench/PalORM.PerfHub -- run --tiers 100,1000 --concurrency --threads 1,4,8

# 只跑 SQLite（快速验证）
dotnet run --project bench/PalORM.PerfHub -- run --dialects sqlite --tiers 1000

# 从历史 JSON 重新生成报告（不重跑）
dotnet run --project bench/PalORM.PerfHub -- report
```

环境变量（PG/MySQL 档必需，见 `.env.test`，凭据不入 git）：
`PALORM_BENCH_PG` / `PALORM_BENCH_MYSQL`。

## 统一口径（为什么可信）

| 维度 | 口径 |
|---|---|
| 数据集 | S1 Narrow（主键 + 4 数据列），种子由行号**确定性派生**——三次测量库内容逐位相同 |
| 三方言 | 同一实体、同构 DDL（列名/类型对齐），仅引用符与参数占位符按方言分叉 |
| 三实现 | **ADO_NET**（地板基线，手写参数化 SQL + 手工物化）· **Dapper**（主流 micro-ORM）· **PalORM**（被测对象） |
| 连接 | 三实现复用**同一条已打开连接**（建连口径一致，否则 ORM/地板比值被污染，见 lessons B76） |
| 时延 | 预热后测 N 轮取**中位数**（同轮对照）；Error/Mean > 5% 在报告里标黄 |
| 内存 | `GC.GetTotalAllocatedBytes` 精确计数（确定性，复现性优于 1%）+ `GC.GetGCMemoryInfo` 采样堆峰 + Gen0 次数 |
| 并发 | 每线程独立连接，80/20 读写混合，预热 1.5s 不计数，近邻秩 p50/p95/p99 |

## 输出

```
bench/perfhub/
  results/history-<yyyyMMdd-HHmmss>.json   每次运行的完整原始数据（schema 2）
  results/latest.json                      最近一次（供回归对比）
  report.html                              统一报告（①总览 ②ORM vs 地板 ③并发 ④增长曲线 ⑤说明）
```

## 报告区块

1. **单操作指标总览**——时延中位数/均值、Error/Mean、分配/op、采样堆峰、Gen0，一张表并列全部 方言 × 实现 × 操作
2. **ORM vs ADO.NET 地板**——以同轮 ADO.NET 为分母的时延比/分配比（跨环境只比比值，规范 §3）
3. **并发吞吐**——ops/s + p50/p95/p99，按 方言 × 实现 × 线程档
4. **增长曲线**——跨历史运行的 SVG 折线（对数轴），每项测量一条线，向下 = 优化生效
5. **说明与未覆盖维度**——如实标注未测项

## 未覆盖维度（如实声明）

- 方言特有类型（JSON/数组/枚举）——三方言表结构同构，未测方言扩展类型
- 连接池行为——PerfHub 用单连接，池参数（MaxPoolSize/MinPoolSize/空闲超时）不在范围
- 长时衰减——单次运行无衰减数据，需连续多轮对比（增长曲线区可观测）
- 并发态分配——多线程下 `GetTotalAllocatedBytes` 被干扰，不测（见 lessons B74）

## 与 BDN 基准的分工

BDN（`bench/PalORM.Benchmarks`）测**单线程微基准**的精细统计；PerfHub 测**跨方言跨实现的一致口径**。
两者互补：BDN 的绝对精度高，PerfHub 的可比性强（同一数据集、同一连接、同一指标定义）。
规范见 `docs/性能基准规范.md`。
