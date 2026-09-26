# 性能优化方案 step6：PostgreSQL 极致化

> 基线 `0d15da5`（v5.7.0-dev），2026-09-26。目标：把 PalORM 对 PG 的支持压榨到极致。
> 方法：三路深挖（Npgsql 10.0.3 驱动 API 全量反射盘查 · PalORM PG 路径逐行热账 · 社区/官方 2024-2026 优化经验调研）
> → 真库探针三轮（分离变量）→ 交替 A/B（PerfHub，PG 18.4 真库）→ 落地与划除。

## 一、事实基线

### 1.1 PG 支持现状（事实）

Npgsql **10.0.3**（Directory.Packages.props:16）。Provider 能力：Binary COPY 批量插入、`UPDATE … FROM (VALUES …)` 批量更新、ON CONFLICT UPSERT、JSONB `WhereJson`、LISTEN/NOTIFY、咨询锁、RETURNING。连接串十旋钮（MaxAutoPrepare=100 / AutoPrepareMinUsages=2 / NoResetOnClose / Read-WriteBufferSize=16384 / Enlist=false / 池化四项）。最新全量批（2026-09-26 11:38，PG 120 项）**无一项劣于 Dapper ≥30%**：BulkInsert 0.167×、TxBulkInsert 0.313×、StreamAll 0.379×、BulkUpdate 0.682×；落后项集中在每查询固定开销型（GetByKey 1.068 / Count 1.086 / IncludeJoin 1.126）。

### 1.2 Npgsql 驱动面（反射实测，非记忆）

`NpgsqlConnectionStringBuilder` 68 个公开实例属性（60 Npgsql 命名 + 索引器 + 7 继承），性能相关键 PalORM 已覆盖 10 个；未触碰键中含 Multiplexing、Socket{Receive,Send}BufferSize、KeepAlive、Options（-=c GUC 注入）、GssEncryptionMode（10.0 新增，默认 Prefer）。

`NpgsqlBinaryImporter` 20 个公开成员：无 Flush、无公开 NumColumns、无传统 typed Write——typed 形态只有 `Write<T>(T, NpgsqlDbType)` 与 `Write<T>(T, string)`。`NpgsqlDataReader` 无 Span getter；写入侧零分配唯一路径 `NpgsqlParameter<T>.TypedValue`（破坏三 Provider 对称，不做）。Npgsql 无 libpq pipeline 独立 API（#5231 closed as not_planned；等价能力是 NpgsqlBatch/TechEmpower 实测移除 multiplexing）。

### 1.3 社区/官方要点（来源均已抓取验证）

- prepared：PG 前 5 次 custom plan 后转 generic；官方 BDN 数据 5~10 表 JOIN 收益 10~100×，但那是同机场景。
- JIT：默认 jit_above_cost=100000 短查询不触发；isman 确认被调低会伤害短查询（30.2：cost≈16 查询 JIT 后 0.365ms→7.416ms）。
- synchronous_commit=off：官方认证的短事务杠杆（28.4），风险窗口 ≤3×wal_writer_delay ≈600ms；属数据安全取舍，须 opt-in。
- Unix domain socket：本机 PG 免费提速（S22）——本PerfHub 环境为远端库（192.168.200.120），不适用，仅文档化配方。
- multiplexing：TechEmpower 基准实测"slower on GOLD machines"已移除；与显式 Prepare 互斥；本机/容器场景不做。
- `No Reset On Close`：官方 "Use only if benchmarking shows a performance improvement"——PalORM 已开并登记 ITM-652（SET/临时表跨池租客泄漏取舍）。

## 二、真库探针（三轮，分离变量 · PG 18.4 @ 192.168.200.120）

证据：`.ai/scratch-pgprobe/Program.cs`（run1/2/3.log，连接串经环境变量不明文）。

| 探针 | 结论 | 数值 |
|---|---|---|
| P1b GSS 冷建连交替 | baseline 与 gss-off 中位相同（6ms），baseline 有 149ms 长尾、gss-off max 21ms | 削长尾不削均值；安全取舍，不改默认 |
| P2 NpgsqlDbType getter | **每次读取重新推断，无缓存**：330ns/读 vs 预置 5ns；DBNull/clr null → Unknown | 每列每行一次读取 = 纯浪费 + ITM-527 根因 |
| P3 参数集合转移 | Clear/RemoveAt 后改名 Add 到另一命令：执行正确、复用正确 | S2 可行性实证 |
| P4 prepared（远端 RTT ~556µs） | SELECT1 ratio 0.96、JOIN ratio 0.90：**prepared 无收益** | S4 划除依据 |
| P6/P6c COPY Write 形态 | A2（null 列 Write(null, Unknown)）= 261.5ms vs D2（WriteAsync(null)）2.9ms：**慢 40~90×**；A1（非 null+getter）10.6ms vs F1（显式 type）6.6ms | S3 根因与形态依据 |

## 三、决策表

| # | 项 | 决策 | 依据 |
|---|---|---|---|
| S1 | GetParameterPlaceholder 默认实现改走 ParameterNameCache | ✅ 落地 | 原插值 BulkDelete 满批一次生成 5000 个字符串；输出逐字节相同（Concat("@p",i)），零分配 |
| S2 | BulkDelete 中转参数改名后转移（不再每 key CreateParameter） | ✅ 落地 | P3 实证；每 key 省 1 个 NpgsqlParameter + 1 次装箱 + DbType switch。重名规避（MySqlConnector Add 时校验）不变 |
| S3 | SourceGen 全标量 DbTypeHint（建参与池 binder） | ✅ 落地 | P2/P6c 实证：NpgsqlDbType getter 每读重推 + DBNull→Unknown 慢路径 40~90×；显式 DbType 后 getter 退化为映射字段读；同时修 ITM-527（整列全 null 无值可推断） |
| S4 | 命令复用槽显式 PrepareAsync | ❌ 划除 | P4 远端实测 ratio 0.90~0.96；自动预编译旋钮已覆盖；官方 BDN 大收益是同机场景（本环境 RTT 500µs 级淹没 parse 收益） |
| — | GssEncryptionMode=Disable 默认化 | ❌ 不改默认 | P1b：只削 149ms 长尾；属安全默认变更，改文档化配方 |
| — | synchronous_commit=off 批量路径 | ❌ 不做 | 数据安全取舍须用户 opt-in；且 PerfHub 夹具每批 1 COMMIT，收益上限小；登记为 opt-in 配方 |
| — | NpgsqlDataSource / multiplexing / 多连接并行 COPY / SequentialAccess / NpgsqlParameter<T>.TypedValue | ❌ 不做 | 架构成本或收益不成立（multiplexing TechEmpower 实测更慢+禁显式 Prepare；多连接 COPY 已 D-PL-1 缓议且 BulkInsert 0.167；TypedValue 破坏 Provider 对称；SequentialAccess 乱序读风险未测） |
| — | InitializeConnectionAsync PG 覆写（SET jit=off 等） | ❌ 不做 | 官方：默认 jit_above_cost=100000 短查询不触发；need EXPLAIN 实证再议，盲目关闭伤分析查询 |

## 四、变更说明（无附带修复）

本轮只含 S1/S2/S3 三项。Update 池 binder 的计数器写法在改写过程中一度落入 `{pi}++`（增量在插值括号外）形态被 `--no-incremental` 构建抓住（CS1059），终态改回与提交版 `{pi++}` 逐位等价的 `{pi}` + 循环末自增；生成输出与改动前一致（快照比对仅余 DbType 新增行），非缺陷修复。

## 五、A/B 结果（5 轮交替 · PG 真库 · PerfHub @ 20000 档 · 2026-09-26）

方法：base 臂 = `0d15da5` 独立 git worktree（detach），opt 臂 = 主树 S1+S2+S3；`--no-build` 防构建竞态；每轮 base→opt 顺序执行；口径 = `MedianNs` 与 `AllocatedBytesPerOp`。**溯源说明**：opt 臂 5 轮 + 首轮单跑两批的结果 JSON 已随本提交入库（`bench/perfhub/results/history-20260926-173700..180228.json` 等 7 份）；base 臂 5 轮的同批 JSON 随 A/B worktree 清理一并删除（疏漏），其逐行测量留存于本地 gitignored 日志 `.ai/scratch-pgprobe/round{1..5}-base.log`，本表数字均从中汇总；复现只需按 §五方法重跑。

**判定按 B100 稳定度：分配 > 绝对耗时 > 同轮比值。**

| 操作 | base | opt | Δt | Δalloc | 判定 |
|---|---|---|---|---|---|
| BulkInsert | 39.13ms | 33.36ms | **−15%** | **−38%**（8.50→5.28MB，5 轮逐位一致） | ✅ 赢（5 轮 4 负，第 1 轮 −15%） |
| TxBulkInsert | 44.42ms | 27.28ms | **−39%** | −38% | ✅ 赢 |
| BulkDelete | 41.48ms | 45.29ms | +9%（污染） | **−34%**（11.27→7.44MB） | ✅ 分配赢；时间第 1 轮 −19% |
| BulkUpdate | 178.09ms | 238.49ms | +34%（污染） | +2%（第 1 轮 +0%） | ⚪ 无归因退化 |
| UpsertBatch | 473.31ms | 506.59ms | +7%（污染） | +0% | ⚪ 第 1 轮 +4.6% |
| GetByKey / WhereIn / KeysetPage / Count | — | — | +14~20%（污染） | ~0 | 对照组：**代码未改，同样劣化** |
| StreamAll / QueryAll / IncludeJoin | — | — | −3~−9% | 0 | 对照（读路径）反降 |

**污染证据链**（为什么正值读数不算退化）：① 四项未改动代码的只读操作（GetByKey/WhereIn/KeysetPage/Count）同步 +14~20% 且分配零变化——不可能由本次改动引起；② 劣化随时段梯度恶化（第 1 轮 opt 反而 −6~−12%，第 3 轮起 ~1ms 级）；③ base 臂自身也随时段膨胀（BulkInsert 39.1→44.3ms）。第 1 轮（全场最干净）口径：BulkInsert −15%、BulkDelete −19%、BulkUpdate −2.5%、Upsert +4.6%、读操作 −6~−12%。

**结论**：S1/S2/S3 净效果 = 批量路径分配 −34~−38%（确定性）+ BulkInsert 时延 −15%（机制：消除 COPY 每单元格 `NpgsqlDbType.Unknown` 慢路径）。远端共享库的会话级负载漂移使时间读数第 2 轮起失真，**未发现可归因于本次改动的退化**；精确复测需本地容器 PG（B94/B100 留档）。

## 六、文档化配方（连接串层，不入代码）

```text
# 本机/同机 PG：Unix domain socket（S22）
Host=/var/run/postgresql

# GSS 协商长尾削峰（P1b：149ms → ≤21ms，安全策略允许时）
GssEncryptionMode=Disable

# 非关键表批量写入（官方 28.4，风险窗口 ≤600ms，须业务确认可丢）
# 在事务内首条语句：SET LOCAL synchronous_commit TO off
```

## 七、遗留登记

1. `NpgsqlParameter.NpgsqlDbType` 每读重推（330ns）——未设 DbType 的路径仍在付（SourceGen 未覆盖的 char/TimeSpan/enum/对象型 OwnedJson 列；`PostgreSqlProvider.CreateParameter` 的 16 组已知基元已显式设 DbType）。
2. PG 每操作一会话固定开销（探针口径 SQLite 侧 16~29µs/op）未在 PG 侧重测。
3. 本地 PG 为远端共享库（192.168.200.120），跨批时间读数受时段负载影响，A/B 必须交替（B71）。
