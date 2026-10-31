# ADR-U：IQueryInterceptor 覆盖边界与 ICommandInterceptor 窗口

- 状态：已采纳（2026-10-31）
- 适用范围：`src/PalORM.Core/IQueryInterceptor.cs`、README 中英文审计边界说明

## 背景

架构评审发现 `IQueryInterceptor` 的名称容易被理解为覆盖全部数据库命令。实际实现只覆盖
实体 SELECT 执行管线、`QueryBuilder` UPDATE 与 `ExecuteAsync`；实体 Insert/Update/Delete/Save、
Bulk 家族、存储过程与多结果集不经过拦截器。使用者若把它当作写审计入口，会得到不完整的审计记录。

## 决策

按评审 D1 推荐 C 处理：

1. 保持 `IQueryInterceptor` 名称不变，避免破坏现有实现者；
2. 在 XML 文档与 README 中英文写明覆盖矩阵与不覆盖入口；
3. 新增 `ICommandInterceptor` 列为 7.0 候选，本版本不实现。

`ICommandInterceptor` 的候选形态是在 ADO.NET 命令层统一拦截，覆盖全部命令。它需要新的
抽象、性能预算与语义设计（尤其是批量路径与事务内命令），不适合在 6.x 维护版本内引入。

## 覆盖矩阵

| 入口 | 覆盖 | 说明 |
|------|:--:|------|
| 实体 SELECT（ToListAsync/First/Single 族） | 是 | 查询执行管线统一通知 |
| `QueryBuilder` UPDATE | 是 | 更新语句执行前通知 |
| `ExecuteAsync` | 是 | 原始命令执行前通知 |
| 实体 Insert/Update/Delete/Save | 否 | 走 CRUD 专用路径 |
| Bulk 家族 | 否 | 批量路径按吞吐优化，不逐条通知 |
| 存储过程、多结果集 | 否 | 由 `StoredProc` / `QueryMultipleAsync` 处理 |

## 后果

- 现有实现者不受影响；使用者能从文档判断拦截器是否满足审计需求。
- 写审计场景需要数据库层审计或 OpenTelemetry，README 已给出指引。
- 7.0 若引入 `ICommandInterceptor`，应同时评估批量路径的逐命令开销与拦截顺序。