# 行动项：2026-07-18 全量深度轮（基线 9e23c90）

> 报告: [review-2026-07-18-9e23c90-full.md](reports/review-2026-07-18-9e23c90-full.md)
> 状态: 整改完成（除 ADR/环境类挂账项）
> 整改提交: 5c2da3c → cd6a38f（6 个提交）

## P0
| ID | 描述 | 文件 | 状态 |
|---|---|---|---|
| ITM-301 | TimeOnly/char 物化生成不可用 → GetFieldValue&lt;TimeSpan&gt; / GetString[0]；补全类型端到端回归（AllTypesEntityTests，16 类型往返） | src/PalORM.SourceGen/RowFactoryEmitter.cs | ✅ 5c2da3c |

## P1
| ID | 描述 | 状态 |
|---|---|---|
| ITM-302 | 租户过滤覆盖全部直连入口（GetDefaultFilterCondition + 参数化绑定 + Update/Delete 追加）；TenantIsolationTests 6 用例 | ✅ 87d7d02 |
| ITM-303 | decimal → DECIMAL(18,6) 三方言显式精度；生成器断言 | ✅ 87d7d02 |
| ITM-201 | MySQL 被索引 string 列 → VARCHAR(255)（IsIndexed 判定）；生成器断言 | ✅ 87d7d02 |
| ITM-304 | 多值 INSERT 骨架上提 Core（MultiValueBulkInsert：参数钳制 SQLite 999/MySQL 65535 + 行命令复用 + 异常保留清理单一实现）；PG COPY 保持独立 | ✅ e6d61de |
| ITM-305 | SqlTemplates 改 partial + SQL 提取去边界引号；补双模板生成测试 | ✅ e6d61de |
| ITM-202 | ColumnOrderValidator 下沉共享，接入 GridReader.ReadAsync/ReadFirstAsync 与 StoredProcBuilder.QueryAsync；3 回归用例 | ✅ e6d61de |

## P2
| ID | 描述 | 状态 |
|---|---|---|
| ITM-306 | AddOrderBy 双前缀守卫（二次调用退化多键续排）；2 回归用例 | ✅ 66de9f4 |
| ITM-307 | Where/OrWhere/CountAsync 用户条件括号包裹（推广 WhereIn 纪律）；OR 穿透回归用例 | ✅ 66de9f4 |
| ITM-308 | 缓存浅拷贝契约：WithCache XML 文档 + 执行管线注释修正（根治需深拷贝/不可变实体，YAGNI 暂缓） | ✅ 66de9f4（文档路线） |
| ITM-311 | CanGenerateEntity 加"恰好一个 [Key]"守卫 + PALORM019 复合主键拒绝诊断 + 测试 | ✅ 66de9f4 |
| ITM-312 | DDL NOT NULL 取 IsRequired ‖ (!IsNullable && !IsPrimaryKey)，与 RowFactory 语义源合一；生成器断言 | ✅ 87d7d02 |
| ITM-314 | IDbProvider.IsUniqueViolation static virtual + 三 Provider 实现（19/1062/23505）+ 断言 | ✅ 49d2ccc |
| ITM-315 | DbOptions.PoolExplicitlyConfigured 标记位替代魔法数字比对 | ✅ 49d2ccc |
| ITM-324 | 单条软删补 AND deleted_at IS NULL（与 Bulk 幂等对齐）+ 回归用例 | ✅ 87d7d02 |
| ITM-325 | PG/SQLite RETURNING 路径回填自增 ID 到传入实体（与 MySQL 对齐）+ 契约文档 | ✅ 49d2ccc |
| ITM-309/310/313/320/326 | 文档类：拦截器覆盖面 / 重试幂等约束 / .sql 陈旧 rebuild / dotnet test 静默（README 运行测试章节）/ CURRENT_TIMESTAMP 时区 | ✅ 49d2ccc |
| ITM-321 | PALORM003/004 描述符语义对齐 + 表名惰性收集（O(N²) 缓解）+ char 修复并入 301 | ✅ 66de9f4 |
| ITM-319 | StoredProc 恒真断言 → 白名单/单次使用契约行为断言；AsPrepared 行为断言 | ✅ cd6a38f |

## P3
| ID | 描述 | 状态 |
|---|---|---|
| ITM-322 | BuildSql 三调用点改 stackalloc 初始缓冲（声明成真）+ 注释修正 | ✅ cd6a38f |
| ITM-323 | DbOptions.LoggerFactory 注入 → CreateAsync 接通；MinimumLogLevel 不再是死配置 | ✅ cd6a38f |
| ITM-327 | TestDb.FromRows 注释改为名实相符（不经 RowFactory） | ✅ cd6a38f |
| ITM-329 | 缓存测试 [NotInParallel("CacheStore")] 分组隔离 | ✅ cd6a38f |
| ITM-330 | WithTransaction 提交前等待补 DisposeWaitTimeout 兜底 | ⬜ 未做（低危，挂下轮） |
| ITM-331 | 死成员降级 static virtual + 接口扩展文档如实收窄 | ✅ 49d2ccc（ADR 部分见挂账） |
| ITM-332 | QueryMultipleAsync 用户子句守卫（区分默认注入）+ 删 mydatabase.db + .gitignore | ✅ cd6a38f |

## 挂账处置（2026-07-18 用户裁决）
| ID | 描述 | 裁决/结果 |
|---|---|---|
| ITM-316 | 目标框架策略 | ✅ 用户裁决：维持 net11.0 单目标，不做多目标 |
| ITM-331b | 方言扩展模型 | ✅ 用户裁决：确有第四 Provider 需求时再议（文档已收窄） |
| ITM-317 | SQLitePCL AOT 初始化 | ✅ 25b9aea：SqliteProvider 静态构造显式 Batteries_V2.Init()；AotTest 改文件库覆盖 WAL 路径；NativeAOT publish + 运行实测 PASSED |
| ITM-318 | PG COPY 可空列/UTC DateTime | ✅ 25b9aea：真库回归 ExternalDatabaseBulkTests（可空列 null 行 COPY 往返、DECIMAL(18,6) 精度、MySQL 索引迁移无 1170、双库 IsUniqueViolation）7/7 通过；PG/MySQL NativeAOT smoke 真库运行 PASSED。**推断证伪**：Npgsql 9 的 COPY 对 DBNull/UTC DateTime 均正确处理，原 [推断] 风险不成立 |
| ITM-108 | 凭据历史重写 | ⬜ 用户裁决：暂不处理 |

## 真库实测意外收获（评审未发现，实测暴露）
| 缺陷 | 修复 |
|---|---|
| BulkDeleteAsync 绑参后改名在 MySqlConnector 下抛"Parameter @p0 already defined"（Add 时即拒绝集合内重名；SQLite 容忍瞬时重名掩盖了此缺陷） | 25b9aea：暂存命令取值 + 按批内序号重建参数 |
| AOT smoke DDL 与生成 SQL 引用大小写不一致（PG 42703）、MySQL TEXT DEFAULT（1101） | 25b9aea：smoke DDL 修正 |

## 验证锚点（cd6a38f 实测）
- build：0 警告 0 错误
- Core.Tests：140/140（+3 GridReader 列序回归）
- SourceGen.Tests：69/69（+5：DECIMAL/VARCHAR/NOT NULL/SqlTemplate/PALORM019）
- Integration.Tests（本地）：137/137（+8：租户隔离 6 + 软删幂等 1 + 全类型 2，恒真断言重写 -1 计入）
- gate-check 25/25 · verify-ai-system 9/9 · doc-consistency 8/8 · stub-check PASS
