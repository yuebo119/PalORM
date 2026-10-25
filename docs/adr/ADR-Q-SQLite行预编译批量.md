# ADR-Q：SQLite 批量插入/UPSERT 的行预编译路由（A+B 方案）

> 状态：**已采纳（2026-10-25 用户裁决：A+B 打包立项，C 单独裁决）** · 来源：FreeSql 交叉基准胜出项归因 + UpsertProbe 探针
> 关联：`RowPreparedBulkInsert` · `SqliteProvider.BulkInsertAsync` · `BatchUpsertAsync` SQLite 分支

## 背景

FreeSql 四臂交叉基准（2026-10-25，审计后批次）中 FreeSql 在 SQLite 批量族 7 格胜出
（BulkInsert 1.39~1.63×、BulkDelete 2.59~3.25×、UpsertBatch 3.73~4.60×、TxBulkInsert
2.13×）。归因定位两台发动机：字面量内联（值拼进 SQL 文本，省参数绑定）与预编译单行
语句复用（命令文本恒定命中 Microsoft.Data.Sqlite 连接级语句缓存，每行只付绑定+步进）。
探针（`C:\dev\github\CrossBench\UpsertProbe`，2000 行/轮 × 10 轮中位，三连跑复现）：

| 形态 | µs/行 | 分配 B/行 |
|---|---:|---:|
| 参数化多值 INSERT（原形态，199 行/批） | 8.6~9.4 | 704 |
| **预编译单行 INSERT（A）** | **2.2~2.6** | 772 |
| 预编译单行 upsert 插入分支（B） | 2.4~4.3 | 773 |
| 预编译单行 upsert 更新分支（B） | 2.9~4.0 | 773 |
| 字面量多值 INSERT（FreeSql 形态，C 候选） | 0.88~0.98 | 370 |

## 决策

1. **A（采纳）**：`SqliteProvider.BulkInsertAsync` 改路由 `RowPreparedBulkInsert`
   （Core 新骨架，多值骨架的姊妹件）：单行 INSERT 语句 + 列数个参数一次建立挂命令，
   事务内逐行只写 Value。复用 v4.6 池契约（`InitInsertParameters` 一次设 DbType、
   `BindInsertValues` 每行写值）；旧生成器程序集回退每行 Clear+整建。
2. **B（采纳）**：`BatchUpsertAsync` 增加 SQLite 分支 `ExecuteRowPreparedUpsertAsync`：
   SQL 与多值形态同源（`BuildUpsertBatchSql(1, …)`，租户守卫同 shape 机器），池布局
   [列数行参数][租户参数?]，`BindUpsertValues` 逐行写值，旧生成器走 scratch 回退。
3. **联网方言不进此路径**（逐行 = N 次 RTT，PerfHub 备案 20K 行 MySQL multi-exec 31s）：
   MySQL 维持多值/LOAD DATA，PG 维持 COPY/UNNEST。
4. **batchSize 语义变更（登记）**：SQLite 批插的 `batchSize` 不再决定语句形状
   （兼容保留，≤0 仍拒绝）；整批一个事务全有或全无。
5. **已知边界（宽容语义，登记）**：多值批对"同批内同一行更新两次"报错（SQLite ON
   CONFLICT 同语句限制）；行预编译逐行执行与 SaveCore 逐条语义一致，后行覆盖前行。
6. **已知取舍**：分配 +9%（772 对 704 B/行，每行一次 ExecuteNonQueryAsync 的驱动层
   分配），纯时延项（3.5~3.9×）不做分配优化。
7. **C（字面量内联）不随本 ADR 采纳**：与全参数化立场冲突，仅 SQLite/数值键安全窗口
   可行，需显式 opt-in 开关，独立裁决。探针已钉死其天花板（0.9µs/行）供裁决。

## 备选与不采纳理由

- **维持多值形态**：探针证伪"多值即 SQLite 最快"的地板假设——语句复用形态每行成本
  低 3.5~3.9×，多值巨型语句的解析成本高于复用省下的绑定。
- **跟进 FreeSql 的 INSERT OR REPLACE upsert 语义**：REPLACE 是删除+重插（触发删除
   触发器、重置未覆盖列），语义粗于 ON CONFLICT DO UPDATE，不采纳。
- **字面量内联（C）**：见决策 7。

## 后果

- 基准地板断点：PerfHub ADO 地板 SQLite BulkInsert/UpsertBatch 形态同步改行预编译
  （地板必须保持最紧形态），该项基线分段重录，历史比值注明形态断点（先例：GetByKey
  2026-10-01 形态断点）。
- AOT：新路径零反射零运行时代码生成，AotTest 发布 0 IL 警告 + 原生运行 PASSED
  （2026-10-25 实测）。
- 测试：`RowPreparedBulkTests` 6 项锁定（往返/整批原子性/外部事务回滚/batchSize 兼容/
  upsert 双分支/批内重复主键宽容语义）；既有批量族（BulkBatchLimitTests、
  UpsertTenantGuardTests、BulkMergeDefaultKeyBatchTests、BulkCleanupTests sqlite 变体）
  全部经新路径执行，675/675 绿。
