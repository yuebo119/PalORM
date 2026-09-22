# 结果库（bench/results/）

> **唯一登记处**（`docs/性能基准规范.md` v2 §6）。三套夹具每次跑测各写一份
> `<夹具>-<时间戳>.json` 并刷新 `latest-<夹具>.json`；跨夹具索引报告由
> `dotnet run --project tools/PalORM.PerfGate -- index` 生成到 `bench/reports/perf-index.md`。

## 为什么是"信封"而不是完整明细

各夹具的原生产物格式各异且各有消费方（PerfHub 有 HTML 报告与自己的 history JSON、
微基准有 BDN JSON 与 BENCHMARKS.md、DapperSuite 有 BDN 工件与 README 表）。
强行统一明细格式会伤到既有报告，所以信封只承载**跨夹具可查询的最小集 + 口径登记 + 健康度**，
明细路径写进 `detailPath`。

**单一真源**：信封模型定义在 `bench/shared/PerfResultEnvelope.cs`，被三个 bench 项目与
`tools/PalORM.PerfGate` 以 Compile Link 共用。复制成多份会让 schema 各自漂移，
那正是"结果四处散落"的成因。

## 字段

| 字段 | 说明 |
|---|---|
| `schema` | 信封版本，当前 2 |
| `harness` | `benchmarks` / `perfhub` / `dappersuite` |
| `timestamp` | 落盘时间（含时区偏移） |
| `commit` | git 短哈希（直接读 `.git/HEAD`，支持 worktree；取不到为 `unknown`） |
| `version` / `label` | 被测版本标识与批次标签（PerfHub 的 A/B 标签、`quick` 冒烟标记走 label） |
| `elapsedSeconds` | 本批耗时 |
| `detailPath` | 明细产物路径（相对仓库根） |
| `environment` | OS / CPU 逻辑核 / 运行时 / 机器名 / GC 模式 / 工具版本 |
| `regime.connectionConfig` | **连接配置口径**（规范 §4.1）：journal/cache/mmap/pooling/会话级 SET，全臂同值 |
| `regime.sessionLifecycle` | **会话与连接生命周期口径**（规范 §4.1）：`per-scope` 或 `per-operation` |
| `regime.health` / `healthRatio` / `healthThreshold` | **环境健康度**（规范 §4.2）：地板行散布中位数与该夹具自报阈值 |
| `items[]` | `name` / `dialect` / `arm` / `tier` / `meanUs` / `allocBytes` / `ratio`（对同批地板）/ `roundTripsPerOp` / `preparedReuse` / `note` |

`roundTripsPerOp` 与 `preparedReuse` 为 0 表示**该夹具未测这一维度**（维度 8 由 PerfHub 覆盖，见规范 §1.1），
不是"零往返"。

## 使用纪律

- **跨夹具比绝对值没有意义**：形状、档位、连接口径都不同，只有同一批次内的比值可比（规范 §3/§4.1）。
- **缺口径或缺健康度标注的条目不得引用**（规范 §7）。
- `label` 带 `quick` 的是冒烟批次，只用于验证夹具可用，不进基线、不作为结论依据。
- 生成索引：`dotnet run --project tools/PalORM.PerfGate -- index`
  （默认读本目录，写 `bench/reports/perf-index.md`）。
