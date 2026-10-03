# 性能优化方案 step16：TxRollback 写路径两级降分配（2026-10-04）

> 提交链：482218b（闭包拆分）+ b3bdec3（AsyncLocal 预热）。
> 起点提问：GetByKey / Update / InsertReturningId / TxRollback 四操作能否极致优化。
> 结论：Update 与 TxRollback 有实质空间，两项已落地（合计 -327B/行）；GetByKey / InsertReturningId 剩余差距为会话构造（F1 证伪）与器材形态差，不动。

## 一、现状基线（00:25 批 + step15 探针）

PerfHub 最新批（history-20261004-002505.json，371 项）四操作读数：

| 操作 | 时延 | 分配 vs ADO | 判读 |
|------|------|------------|------|
| GetByKey | SQLite 6.4µs 三臂最优 | +0.9KB ≈ 会话构造 968B | 无可抢空间 |
| Update | SQLite 7.5µs / MySQL 288µs / PG 511µs 持平偏优 | +1.0KB | 闭包 250B（本轮修）|
| InsertReturningId | MySQL 优 9.7%，其余持平 | +0.8~1.4KB | 会话构造+实体构造形态差 |
| TxRollback | MySQL/PG 280ms 服务器回滚主导；SQLite 877.8µs（1.41×） | SQLite +356B/行 | 本轮主战场 |

网络方言时延被往返主导（ErrorRatio 6~10%，f3e1d99 判别力弱标注），库侧优化只对 SQLite 本地档可测；分配是跨方言稳定指标。

## 二、step16：ApplyUpdateOutcome 闭包拆分（-256B/行）

**根因（分配采样 + IL 双实证）**：乐观锁暂存 lambda `() => metadata.IncrementVersion(entity)` 内联在 ApplyUpdateOutcome 时按值捕获 ~230B 的 CrudMetadata struct，Roslyn 对参数捕获闭包在方法入口无条件分配 display class（IL_0000 newobj 先于 `IncrementVersion is null` 早退）。无并发令牌实体每次 UpdateAsync 白付 ~250B。

**修法**：暂存转发到新私有方法 DeferVersionIncrement——无并发令牌/单条路径零闭包；批量乐观锁路径闭包缩到 ~32B（降 87%）。

**验证**：
- AllocationTick 采样：该 display class（192 ticks 榜首）从分布中消失
- TxRollbackDiag 三方言 ② 单会话循环：1009→753（SQLite）/ 1907→1651（PG）/ 3011→2755（MySQL）B/行，-256B/行跨方言一致（固定开销项）

## 三、step16-P1：操作归属 AsyncLocal 写预热（同会话循环再 -70B/行）

**根因（探针 4 格二分矩阵）**：async 方法体内写 AsyncLocal 的值不跨该调用持久——每次 async 方法（UpdateCoreAsync 等）内的 Enter 写都 COW 一次新 ExecutionContext（EC 48B + OneElementAsyncLocalValueMap 24B ≈ 72B/行），同值守卫无效（读到回滚后的无槽 EC）。矩阵读数：

| 格 | 形态 | 增量 vs 地板 |
|----|------|-------------|
| ①d | 外层循环体直写（同值） | 0 |
| ①e | 内层 async 方法写（await 前，同值守卫） | +72B/行 |
| ①f | 同步方法（直包 ValueTask）写 | 0 |
| ①g | 内层 async 方法写（await 后） | +72B/行 |
| ①h | 外层持久写一次 + 内层 async 只读守卫 | 0 |

**修法**：Enter/EnterTransactionOperation 加同值守卫；Insert/Update/Save 三个非 async 公共入口条件预热（OwnerPrewarmed 门禁）。

**预热门禁（防自引入回归）**：无条件预热在"每操作新会话"形态下让 N 个会话的 AsyncLocal 实例在同一调用方 EC 的不可变 map 链上累积，COW 线性放大（探针 ③ 形态实测 2459→10309 B/行，PerfHub 器材正是此形态）。仅"本会话已完成过至少一次操作"后预热，新会话首调永不预热——③ 回到 2483 B/行基线。

**语义核查**：IsCurrentOperationScope = `_isActive && owner匹配` 的与逻辑，预热只影响槽值（恒 this）不改判定窗口；Exit 本就不清槽（v4.5）；槽 per 会话实例，跨会话嵌套无污染。唯一读消费 = DisposeAsync 自释放防护，行为不变。

## 四、step16-P2：PG/MySQL 剩余差额定性（登记不改）

TxRbAllocDiag 方言采样（PG/MySQL 各 10 轮 × 2000 行，NUMERIC/DECIMAL 口径对齐 perf_s1）：

- PG（NUMERIC 口径）净差 106B/行；MySQL 净差 411B/行
- MySQL 差额 ≈ 100% 来自一个周期性 **106KB 大对象**（75 次 × 106KB ≈ 8MB = 411B/行 × 20000，闭环）：分配栈为 `AsyncHelpers.AllocContinuationMethod ← UpdateCoreAsync ← UpdateAsync`，即 **.NET 运行时为 async 续体分配的基础设施对象**，由 UpdateCoreAsync 的 async 层在真异步驱动（网络 IO 挂起-恢复）上触发
- PG 同栈同样存在（75 ticks），但被产品臂常量 SQL 带来的 String/状态机优势抵消（产品 String 3 ticks vs ADO 59）
- SQLite 无此成本（全同步无续体分发）

**定性**：驱动真异步的本质成本，UpdateCoreAsync 的 await 无法去除（内含真异步等待），库侧不可消除，不改。注意 step15 探针的 TEXT 口径（447B/行）与 PerfHub NUMERIC 口径（106B/行）不可比——列类型改变驱动绑定路径。

## 五、汇总口径（step16 两级修复后，TxRollbackDiag ② 单会话 vs ① 地板）

| 方言 | 基线 | step16 后 | 累计降幅 | vs ADO 差 |
|------|------|----------|---------|----------|
| SQLite | 1009 B/行 | 682 | -327（-32%） | 356→29 |
| PG（TEXT 口径） | 1907 | 1579 | -328 | 775→447 |
| MySQL（TEXT 口径） | 3011 | 2683 | -328 | 759→431 |
| PG（NUMERIC 口径，PerfHub 对齐） | — | — | — | 106（运行时续体，不改） |
| MySQL（DECIMAL 口径，PerfHub 对齐） | — | — | — | 411（同上，不改） |

Update 单行夹具分配差 +1.0KB 中 250B 来自闭包（已修）；GetByKey / InsertReturningId 不动（会话构造 F1 证伪：可减上界 72B < 200B T5d 线）。

## 六、验证与工具

- Core 497/497、Integration 268/268 全绿；SourceGen 225/227（2 失败为基线既有：快照偏 UNNEST 注释 + ConverterPrimaryKey 双括号形态，stash 对照确认，与本轮无关，待快照刷新）
- 门禁 G1-G33 通过；ci.slnf --no-incremental 0 警告 0 错误
- 探针：`dotnet run --project .ai/perf-probe/PerfProbe.csproj -c Release -- txrollback`（①~①h 矩阵 + 三方言）；`-- txrballoc [pg|mysql] [ado]`（采样源，配 dotnet-trace gc-verbose）；分析器 `.ai/perf-tools/TxRbAllocReport`（AllocationTick 类型分布 + 大对象栈）
- 建议验收：跑一轮 PerfCli full 批入库，看 TxRollback/SQLite 与 Update 组分配列收窄幅度（预期 SQLite 档 P/ADO 分配比从 +40% 降到 +7% 级）

## 七、遗留

1. SourceGen 2 失败的快照刷新（独立事项，与本轮无关）
2. PerfHub 全量批验收（等服务器窗口）
