# PalORM Action Items — 2026-07-18 整改验证轮评审

> 生成日期: 2026-07-18
> 关联评审报告: [reports/review-2026-07-18-70fc6ec.md](reports/review-2026-07-18-70fc6ec.md)
> 状态: 进行中

---

## P1 — 近期（本迭代内修）

| ID | 描述 | 文件 | 影响 | 状态 |
|:--:|------|------|:--:|:--:|
| ITM-201 | MySQL 方言对 TEXT 列生成的索引 DDL 报 1170 不可执行（非 1061 不被幂等兜底吞）；被索引 string 列建列改 VARCHAR(255) 或前缀索引，补 MySQL 生成物断言 | src/PalORM.SourceGen/MigrationEmitter.cs:131-174 | 🔴 | ⬜ |
| ITM-202 | ADR-A 列序校验未覆盖 GridReader.ReadAsync/ReadFirstAsync 与 StoredProcBuilder.QueryAsync——同型列错位仍静默换数据；校验下沉共享并接入两路径，补测试 | src/PalORM.Core/GridReader.cs、src/PalORM.Core/StoredProcBuilder.cs、src/PalORM.Core/DataSession.cs | 🔴 | ⬜ |

## P2 — 计划（下个迭代）

| ID | 描述 | 文件 | 影响 | 状态 |
|:--:|------|------|:--:|:--:|
| ITM-203 | 索引名冲突/无效声明静默化三联：ux_ 命名无歧义边界 + IF NOT EXISTS/1061 掩蔽 + 空列/重名 [Index] 静默丢弃；新增 PALORM018 诊断 + 吞 1061 处日志 | src/PalORM.SourceGen/TableModel.cs:55,127-134、src/PalORM.Core/DataSession.cs:580-583 | 🟡 | ⬜ |
| ITM-204 | CreateIndexSqlSet 持有裸数组引用，Register 缺防御性包装（对照 ColumnNames 纪律） | src/PalORM.Core/PalORM_Runtime.cs:156-157 | 🟢 | ⬜ |
| ITM-205 | DbOptions 脱敏不覆盖序列化器解构/调试器路径；文档标注勿整体序列化，可选 Redacted() 视图 | src/PalORM.Core/DbOptions.cs | 🟡 | ⬜ |
| ITM-206 | CreateAsync 连接超时耗尽抛裸 OCE，与命令路径 TimeoutException 包装不对称 | src/PalORM.Core/DataSession.cs:53-66 | 🟢 | ⬜ |
| ITM-207 | InitializeConnectionAsync 未覆盖读路由连接（ConnectionLease.OpenOwnedAsync 不调钩子）；补调或文档明示仅主连接 | src/PalORM.Core/ConnectionLease.cs:22-39、src/PalORM.Core/IDbProvider.cs:54-57 | 🟢 | ⬜ |

## P3 — 低危小项

| ID | 描述 | 文件 | 影响 | 状态 |
|:--:|------|------|:--:|:--:|
| ITM-208 | ScalarAsync 文档补类型支持范围（Guid/枚举抛 InvalidCastException，与 Max/Min 对齐声明） | src/PalORM.Core/DataSession.cs:817 | 🟢 | ⬜ |
| ITM-209 | 熔断活性：陈旧失败作废在飞探针成功致恢复延迟一周期；注释声明保守取舍 | src/PalORM.Core/Resilience.cs:137-157 | 🟢 | ⬜ |
| ITM-210 | BoundedQueryCache 文档声明软上限（Set TOCTOU 有界超容）与 Count 全分段锁成本 | src/PalORM.Core/CacheStore.cs:54-59 | 🟢 | ⬜ |
| ITM-211 | ValidateColumnOrder 列数不足时截断校验，超出部分未核对；补分支或文档 | src/PalORM.Core/DataSession.cs:767-785 | 🟢 | ⬜ |
| ITM-212 | WhereJson 拒 NUL（防御对称）+ value 非字符串与 ->> text 比较的归一或文档 | src/PalORM.PostgreSql/PostgreSqlExtensions.cs:14-22 | 🟢 | ⬜ |
| ITM-213 | MigrateAsync 三次独立 Volatile 读注册表快照的理论撕裂窗口；不动，记录在案 | src/PalORM.Core/DataSession.cs:555-567 | 🟢 | ⬜（仅记录） |

## 在案项

| ID | 描述 | 状态 |
|:--:|------|:--:|
| ITM-108 | 凭据历史重写 + 数据库轮换（8 个本地提交含真实凭据） | ⬜（用户决策）|

## 进度追踪

| 优先级 | 总数 | 已完成 | 进行中 | 未开始 |
|:------:|:--:|:--:|:--:|:--:|
| P1 | 2 | 0 | 0 | 2 |
| P2 | 5 | 0 | 0 | 5 |
| P3 | 6 | 0 | 0 | 6 |
| **合计** | **13** | **0** | **0** | **13** |

---

## 验证锚点（verify-action-items.sh 机械校验用）

涉及文件：
`src/PalORM.SourceGen/MigrationEmitter.cs` `src/PalORM.SourceGen/TableModel.cs` `src/PalORM.Core/GridReader.cs` `src/PalORM.Core/StoredProcBuilder.cs` `src/PalORM.Core/DataSession.cs` `src/PalORM.Core/PalORM_Runtime.cs` `src/PalORM.Core/DbOptions.cs` `src/PalORM.Core/ConnectionLease.cs` `src/PalORM.Core/IDbProvider.cs` `src/PalORM.Core/Resilience.cs` `src/PalORM.Core/CacheStore.cs` `src/PalORM.PostgreSql/PostgreSqlExtensions.cs`

涉及标识符：
`BuildCreateIndex` `GetDbType` `IsDuplicateSchemaObject` `ValidateColumnOrder` `ReadFirstAsync` `CreateIndexSqlSet` `CreateIndexSqlByDialect` `InitializeConnectionAsync` `OpenOwnedAsync` `BoundedQueryCache` `EvictExpired` `WhereJson` `RecordFinalFailure` `RecordSuccess` `ReleaseCancelledProbe` `NormalizeGeneratedId` `RecordCleanupException` `GetRegisteredTableName`
