# ADR-R：SQLite 批量删除的 json_each 键集合形态（E 方案）与 C 方案否决

> 状态：**已采纳（2026-10-25 用户裁决「现在做，按最优方案实施」）** · 来源：C 方案调查（主流 ORM 批删参数化行为核验）+ DeleteProbe 探针
> 关联：`BulkDeleteAsync` json_each 分支 · `UseJsonEachDeleteForm` · ADR-Q（A+B 行预编译）

## 背景

四臂交叉基准（B100 三批窗口，2026-10-25）中 SQLite BulkDelete 是 PalORM 唯一显著
落后 FreeSql 的格子（P/FreeSql 2.13×：35.72ms 对 16.77ms，20000 键）。归因：FreeSql
`UseNoneCommandParameter` 把键值内联进 SQL 文本（字面量形态），PalORM 全参数化 IN
999 键/批。此前该差距登记为「C 方案（字面量内联）待裁决」。

## 证据链（三源互证）

**DeleteProbe（bench/crossbench/DeleteProbe，2000 键/轮 × 10 轮中位，三连跑复现）**：

| 形态 | µs/键 | 分配 B/键 |
|---|---:|---:|
| 参数化 IN 999/批（原形态） | 3.64~3.86 | 292 |
| 字面量内联（FreeSql 形态） | 0.34~0.40 | 98 |
| **单参数 json_each** | **0.21~0.25** | 89 |

json_each 不但全参数化，还**快于字面量约 40%**：服务端虚拟表展开优于巨型 IN 文本
解析，C 方案唯一的卖点（性能）被消除。

**业界核验**：EF Core 8 对集合参数化在 SQLite 正是用 `json_each(@param)`（官方
breaking changes）；EF9 加控制开关、EF10 通用默认回摆到每值参数的理由（服务端
优化器基数信息）不适用于本场景：SQLite 进程内无计划缓存，且批量删除是固定形状
操作。Dapper 默认列表展开为参数化占位符；linq2db 的 `InlineParameters` 是窄场景
opt-in；EF ≤7 的内联常量因计划缓存膨胀被废弃（#13617，当时最高票 issue）。

**自家先例**：PG 的 `= ANY(@ids)` 数组形态（UNNEST-1，DELETE 时延 −52%、分配 −99%）
是同一思想的现成实现；E 是它的 SQLite 分支。

## 决策

1. **E（采纳）**：SQLite `BulkDeleteAsync` 增加 json_each 分支——
   `DELETE/软删 UPDATE ... WHERE pk IN (SELECT value FROM json_each(@ids))`，
   `@ids` 为手写 StringBuilder 构建的 JSON 数组文本（不变文化，AOT 零反射）。
   语句文本跨批恒定、单参数批间只换 Value（R-UNNESTB 纪律同数组形态）。
2. **C（否决）**：字面量内联不再保留为候选——慢于 E、破全参数化立场（注入面/
   语句文本膨胀/联网方言计划缓存污染）。账本表 2 关账。
3. **三层能力门控**：方言 = SQLite + 首键（经 ConvertDeleteKey 转换后）整型 +
   引擎探测（每 (TProvider, T) 缓存一次；旧 e_sqlite3 bundle 无 JSON1 时回退 IN）。
   字符串/GUID 主键与复合主键回退 IN 形态（首版不覆盖，JSON 转义后续按需）。
4. **方言纪律（EF10 教训的适用面）**：PG 维持原生数组 ANY（优化器有数组统计）、
   MySQL 维持参数化 IN 分批（InnoDB 服务端计划交互）——进程内库选绑定成本最优，
   联网方言尊重服务端优化器信息，不全局跟风单一形态。
5. **已知边界**：ulong 主键排除（超 int64 域 JSON 数值与引擎 INTEGER 语义分叉）；
   混合类型键列表在 JSON 构建期 fail-fast（ConvertDeleteKey 先抛 FormatException），
   与 IN 形态批内转换异常同语义（都在事务提交前，全有或全无）。

## 后果

- 基准地板断点：PerfHub ADO 地板 SQLite BulkDelete 同步切 json_each（地板保持
  最紧形态），基线该格分段（v2 窗口另起，先例 ADR-Q）。
- 预期：SQLite BulkDelete 20000 档从 35.72ms 降到约 5ms 量级，P/FreeSql 2.13×
  反转为约 0.3×；四臂格局 PalORM 领先 FreeSql 86/87 格（仅剩 MySQL BulkUpdate
  1.05× 噪声格）。
- 测试：`BulkDeleteJsonEachTests` 8 项锁定（单语句往返/部分重叠/软删/租户零影响/
  字符串键回退正确性/坏键 fail-fast 原子性/IN 形态中途失败整批回滚（顺带关掉删除
  侧原子性测试缺口）/空与不存在键）；IN 形态池化夹具经 `ForceInFormDeleteForTests`
  （IVT 钩子，先例 ResetLocalInfileProbeCache）锁定回退路径；AotTest 含 json_each
  探针行（发布 0 IL 警告 + 原生 PASSED）。
- 门禁交互一例（登记）：R-UNNESTB 的 carrier 反启发式按文件级 grep 命令变量的
  Execute 调用（不辨作用域）——新增引擎探测命令命名避开 `probe`（用 `engineProbe`），
  否则会误伤同文件既有 probe 载体的断言。
