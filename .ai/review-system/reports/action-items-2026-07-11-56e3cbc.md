# PalORM 评审行动项：56e3cbc

> 来源：`.ai/review-system-v2/reports/review-2026-07-11-56e3cbc.md`  
> 范围：提交注释准确性 + 评审基础设施  
> 原则：先纠正事实，再增加说明；不借注释提交顺手修改生产行为

## 提交内行动项

| ID | 严重度 | 时效 | 工作量 | 位置 | 完成标准 |
|---|:---:|:---:|:---:|---|---|
| ITM-001 | P2 | near | <1h | `src/PalORM.PostgreSql/PgNotificationListener.cs`（⚠ 2026-07-17 注：原指向的 ListenLoopAsync 方法已在后续重构中移除，监听循环现为 `StartAsync`/`ListenAsync` 结构，本项按已过时处理） | 注释明确当前只在同一连接上有限重试，不宣称断线重连；若要宣称恢复，必须实现 reopen + re-LISTEN 并有故障测试 |
| ITM-002 | P2 | near | <1h | `src/PalORM.PostgreSql/PostgreSqlProvider.cs` 的 `BulkInsertAsync` | `CompleteAsync` 注释限定为当前 COPY 批次，并明确跨批次原子性边界 |
| ITM-003 | P2 | near | <1h | `src/PalORM.Core/DataSession.cs` 的 `InsertAsync` | 将“完整行”改为准确的 RETURNING 列范围；不得暗示 timestamp/computed 值已刷新 |
| ITM-004 | P3 | near | <1h | `src/PalORM.Core/QueryBuilder.cs` 的 `BuildSql` | 注释恢复 SplitQuery 条件：普通模式保留 JOIN，split 模式仅跳过已标记 JOIN |
| ITM-005 | P3 | near | <1h | `src/PalORM.PostgreSql/PostgreSqlProvider.cs` | 注释说明复用的是命令、参数每行重建；移除重复尾注释和无基准的固定性能倍数 |
| ITM-006 | P3 | near | <1h | `src/PalORM.PostgreSql/PgNotificationListener.cs` | 非瞬态异常示例与 `WaitAsync` 阶段一致，不再列举发生于 Open/LISTEN 阶段的认证和语法错误 |
| ITM-007 | P3 | future | <1h | Git 提交记录 | 后续更正记录说明实际只评审 5 个方法，NOTIFY 参数化不属于 56e3cbc 的 diff |

## 评审基础设施行动项

| ID | 严重度 | 时效 | 工作量 | 位置 | 完成标准 |
|---|:---:|:---:|:---:|---|---|
| ITM-008 | P2 | near | <1h | `scripts/review-snapshot.sh` | 测试文件统计排除 bin、obj、Generated 和生成代码；输出可复现的实际测试源码数 |
| ITM-009 | P2 | near | 1-2h | `scripts/verify-action-items.sh` | 循环不在丢失计数的管道子 shell 中运行；故意加入不存在标识符时脚本必须非零退出，恢复后再通过 |
| ITM-010 | P2 | future | 1-2h | `.ai/review-system-v2.md` | ✅ 已完成（2026-07-17）：四提示词失效路径全部修正为 docs/编码规范.md、docs/踩坑目录.md、docs/架构设计.md 与 .ai 内模板；新增 `scripts/verify-ai-system.sh` 机械校验并挂入 CI（⚠ 2026-07-17 晚注：该脚本在当日 Git 仓库重建事故中丢失，已按 7 项检查口径重建并重新挂入 CI，V1-V7 全部 PASS） |
| ITM-011 | P2 | future | <1h | `scripts/review-snapshot.sh` | 构建快照保留 error/warning 数量和退出码，不只截取最后三行 |

## 残余风险，不归因于本提交

- 2026-07-11 全量审计中的凭据暴露为 P0，必须独立止血。
- `SaveAsync`、ConcurrencyCheck、ForRead、SoftDelete、OwnedJson AOT 等生产缺陷仍未修复。
- 本清单不将上述旧缺陷伪装成注释提交的修复范围。
