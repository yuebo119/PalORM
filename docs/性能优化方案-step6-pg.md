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

## 五、A/B 结果（顺序交替 4 轮 · PG 真库 · PerfHub @ 20000 档 · 2026-09-26）

**协议（复测轮，c1~c4）**：base 臂 = `0d15da5` 独立 git worktree（detach，`.ai/ab-base2`），opt 臂 = 主树 S1+S2+S3+PG-4；奇数轮 base→opt、偶数轮 opt→base（每臂各得 2 个批内前段 + 2 个后段槽位）；`--no-build`。口径 = `MedianNs` 与 `AllocatedBytesPerOp`。

**这一协议修正是本轮的枢纽**：首批 5 轮用固定顺序（每轮 base→opt），opt 臂固定占批内后段，远端共享库的时段劣化全部记到 opt 账上——未改代码的 Count +27.5%、KeysetPage +22.8%、GetByKey +8.6% 同步"劣化"（分配零变化），BulkUpdate 假劣化 +34%。同批 ADO 臂归一不能替代顺序交替（ADO 臂也在批内前段，梯度照记）。顺序交替后对照组回到 +0.1~+5.8% 混合噪声，两个"劣化"项 BulkUpdate 回平（+1.4%）、原"退化"的 BulkDelete 反现真赢。

| 操作 | base | opt | Δt | Δalloc | P/ADO 归一 | 逐轮一致性 |
|---|---|---|---|---|---|---|
| BulkInsert | — | — | **−13.8%** | **−37.9%**（8.48→5.27MB） | −13.7% | 4/4 负 |
| TxBulkInsert | — | — | **−9.9%** | **−37.9%** | −12.3% | 4/4 负 |
| BulkDelete | — | — | **−11.0%** | **−34.2%**（11.25→7.40MB） | −8.8% | 4/4 负 |
| BulkUpdate | — | — | +2.8% | +0.1% | +1.4% | 混合（持平） |
| UpsertBatch | — | — | +2.2% | +0.1% | +0.8% | 混合（持平） |
| GetByKey / KeysetPage / Count / Insert / WhereIn | — | — | +0.1~+3.0% | ~0 | −2.1~+5.8% | 混合噪声 |
| StreamAll / QueryAll | — | — | −27.8% | ~−1% | −40/−29% | 4/4、3/4 负（见下） |

**判读**：分配（确定性口径）BulkInsert/TxBulkInsert −38%、BulkDelete −34% 为硬赢；时延 BulkInsert/TxBulkInsert/BulkDelete 三赢且逐轮一致；BulkUpdate/UpsertBatch 持平（PG-4 见 §八）；StreamAll/QueryAll 的 −27.8% 为只读路径（代码未改）观测到的稳定负值，**机制未确立**（候选：更快更 lean 的 COPY 写入改变后续扫描的表物理状态/可见性映射），按 B97 纪律标"观测到、未归因"，不计入收益。绝对时间值跨协议不可比（服务端负载不同），只可比同协议内比值。

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

1. `NpgsqlParameter.NpgsqlDbType` 每读重推（探针四：设 DbType 后 31ns/读、未设 146~330ns/读）——未设 DbType 的路径仍在付（SourceGen 未覆盖的 char/TimeSpan/enum/对象型 OwnedJson 列；`PostgreSqlProvider.CreateParameter` 的 16 组已知基元已显式设 DbType）。
2. PG 每操作一会话固定开销（探针口径 SQLite 侧 16~29µs/op）未在 PG 侧重测。
3. 本地 PG 为远端共享库（192.168.200.120），本机无 Docker/WSL/本地 PG，时间读数必须按 §五顺序交替协议采集。
4. StreamAll/QueryAll 的 −27.8% 观测未归因（§五）。

## 八、探针四与 PG-4（2026-09-26 第二轮）

**探针四事实**（`.ai/scratch-pgprobe/run4.log`）：`NpgsqlParameter.DbType` setter = **1.8ns/次**（推翻"每行赋值昂贵"假设）；设 DbType 后 `NpgsqlDbType` getter = 31ns/读（未设 146~330ns）；`DBNull + DbType.Int32 → Integer` 且真库 `SELECT @p1::int` 返回 DBNull 正确（ITM-527 修复真库双证）。

**PG-4 两项**：
1. **WriteRow 类型缓存**：每列 NpgsqlDbType 是列属性不是单元格值属性，原每单元格读 getter（31ns × 20000 行 × 13 列 ≈ 8ms）改为每次 `BulkInsertAsync` 采样一次（`SampleColumnTypes`，首行绑定后）。S3 的显式 DbType 保证首行全 DBNull 列也映射真实类型，采样对任何列组合成立。
2. **emit 对称性规则**：显式 DbType 只保留在「有 `NpgsqlDbType` 消费方」或「每操作一次」的路径——INSERT 池（COPY WriteRow + ITM-527）、单行 binder（R5 推断确定性）；Update/Upsert 池（每行每列重绑、无消费方、执行期由驱动从 Value 推断）恢复 S3 前形态（仅 byte[]→Binary）。效果：BulkUpdate/Upsert 分配与 base 逐位相同（+0.1%），时延回到持平（+1.4%/+0.8%），首批协议下的 +34%/+17% 劣化读数消失。
