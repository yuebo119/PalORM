# 性能优化方案 step5

> 复核注记（2026-09-22）：本报告部分"已排除项"经复核为假阴性（ShapeCache 键空间、缓存无界增长、PG/MySQL 连接池映射），P2-43 的 `DbBatch` 表述与参考程序集不符；实施前请对照 `docs/review/性能优化任务清单-2026-09-22.md` §六 与 §九。

> 基线：9b22ebf（dev 分支）｜ 审查日期：2026-09-22 ｜ 审查范围：src/ 全部手写源码 16.4K 行 + 三 Provider + SourceGen 生成代码产物
> 审查方法：5 个并行专项（延迟 / 内存 / 并发 / 可靠性 / SourceGen）逐文件精读 + 主审对关键证据逐条复核（含 P0 级发现的跨文件不对称性比对）

## 一、结论

共 52 个可动优化点：P0 级 1 个，P1 级 20 个，P2 级 31 个。

首要结论：**代码库不存在"必须修"的性能缺陷结构**。SELECT 单查询主路径已被形状缓存、参数名缓存、编译期 ordinal 内联、流式终结器多轮收敛，剩余项多为固定开销与批量写入路径的分配热点。唯一 P0 在事务可靠性面：MySQL BulkCopy 失败后不显式回滚，是三 Provider 批量路径中唯一的漏网实现。

按预期收益排序的最先 8 项：

| 优先级 | 项 | 方向 | 结论 |
|---|---|---|---|
| P0 | MySQL BulkCopy 失败路径无显式回滚 | 事务 | 失败后服务端事务悬置，或异常传播路径上无界卡死 |
| P1 | 多值 INSERT 双份大串落 LOH | 内存 | 删掉 string[] 中间层，单批瞬时分配从约 830KB 降到约 276KB |
| P1 | 只读查询每查询 272B 固定开销 | 延迟 | 写路径已修（208B/行），读路径同模式漏修 |
| P1 | From<T>() 过滤子句每查询重建 | 延迟 | 主力查询入口全受害，同文件另两条路径已缓存 |
| P1 | DisposeWaitTimeout 默认 5 分钟 | 可靠性 | 枚举器泄漏时连接、行锁、池额度一起钉住 5 分钟 |
| P1 | SessionBatch 无操作门禁 | 并发 | 会话上唯一裸露的变更面，并发 Append 结构性损坏 |
| P1 | 重试无整体截止时间 | 并发+可靠性 | 最坏约 91s 独占连接，池饥饿放大器 |
| P1 | 引用列物化依赖 NRT 注解 | 可靠性 | #nullable disable 实体遇 DB NULL 抛无列名裸异常 |

依据等级定义：[事实] 代码可见或可立即编译验证；[推断] 逻辑推导未实测，采纳前需实测确认。

## 二、延迟方向（每查询固定开销）

### P1-1 `From<T>()` 每查询重建软删/租户过滤子句文本
- 位置：`src/PalORM.Core/DataSession.Crud.cs:47`
- 问题：软删实体每次 From<T>() 付一次 IdentifierSafety 字符扫描 + 一次 string.Concat（quote）+ 一次插值分配，约 116B；租户会话再加 quote + FormattableStringFactory.Create + 域串，合计约 350-400B/查询。这些文本对 (Type, Dialect) 恒定。同文件 `GetGetByKeySql`（307 行起）已缓存带过滤后缀的完整 SQL，`DataSession.cs:499` 已缓存逐字相同的 `deleted_at IS NULL` 条件；From<T>() 家族（ToList/First/ToPage/ForEach，最高频入口）唯独每查询重建。多租户 + 软删实体是主要业务形态。
- 依据：[事实]
- 建议：软删分支直接取 DataSessionCache.FilterFormsCache 的 Condition；租户分支把引用词前缀按 (Type, Dialect) 缓存为只读串，执行时前缀 + `@pN` 拼接（参数名本就每查询动态）。

### P1-2 COUNT/聚合五兄弟每查询重建基础 SQL
- 位置：`src/PalORM.Core/DataSession.Query.cs:16`
- 问题：`SELECT COUNT(*) FROM "表名"` 是 (Type, Dialect) 常量，与 GetAsync 的全句缓存同构却未缓存；带 where 时 3-4 次 `+=` 再分配，合计 2-5 个字符串约 150-250B/查询。聚合路径（48 行起）连表引形之外的中段也每查询重建。
- 依据：[事实]
- 建议：仿 GetByKeySqlCache 增加 `CountBaseSqlCache`；聚合路径缓存前后两个常量段，中间 expression 段走 FormatCached。

### P1-3 默认配置每查询创建链接 CTS + 定时器（约 272B）
- 位置：`src/PalORM.Core/QueryBuilderExtensions.cs:180`、`src/PalORM.Core/Resilience.cs:83`
- 问题：`IsPassThrough` 需 MaxRetries=0 且熔断禁用，而默认 MaxRetries=3 / 阈值 5，开箱配置下每条只读查询都进重试器：每次尝试一个 `CreateLinkedTokenSource` + `CancelAfter(30s)`（Timer 对象 + 注册/注销）。代码注释自带实测分解：CTS+timer 约 168B + 委托 56B + 执行器机构 48B = 272B/查询。延迟侧约占 SQLite 本地查询（37µs 口径）的 1%。
- 依据：[事实]（分配与默认值代码可见）；延迟占比 [推断]（未实测）
- 建议：属已登记取舍，不建议改默认行为。把选择权显式化：DbOptions 增加"读路径仅用驱动级 CommandTimeout、不包链接 CTS"的开关（该 CTS 覆盖连接获取与读取器迭代，不是驱动超时子集，必须 opt-in），开启后每查询省 168B + 两次 timer 注册；补同会话交替配对 A/B 量化延迟差。

### P1-4 只读入口每查询分配 async lambda 委托 + display class
- 位置：`src/PalORM.Core/DataSession.Query.cs:30、97`、`src/PalORM.Core/DataSession.Crud.cs:334、364`
- 问题：lambda 在调用点求值即分配（捕获 cmd 的 display class + 委托对象），且发生在 IsPassThrough 分支判定之前，直通也要付。写路径同型问题 2026-09 已修（208B/行），读路径漏修。Count/聚合每次调用两点叠加。
- 依据：[事实]（分配发生在分支前可见）；字节数 [推断]（按写路径实测折算）
- 建议：按写路径已落地模式加 `DbCommand` 持有版直 overload：直通分支直接 await 驱动方法，非直通分支才包执行器委托。读四兄弟一致适用。

### P1-5 SQLite 上每次批量靠捕获 NotSupportedException 探测能力
- 位置：`src/PalORM.Core/SessionBatch.cs:130`
- 问题：`DbConnection.CreateBatch()` 基类实现即抛 NotSupportedException，Microsoft.Data.Sqlite 不覆写。SQLite 每次 ExecuteNonQueryAsync（及 MigrateAsync 逐条回退路径）必付一次异常抛出 + 栈捕获，约 10-50µs。SessionBatch 文档自证 SQLite 本地 RTT≈0、回退行为等价，这笔是纯增固定成本。
- 依据：[事实]（异常必然发生）；成本量级 [推断]
- 建议：`SessionBatch<TProvider>` 是泛型类，先做静态判定 `TProvider.Dialect == SqlDialect.Sqlite` 直接走回退；未知 Provider（未来方言）保留 try/catch 兜底。每批 1 次异常降为每连接类型 1 次判定。

### P1-6 原始 SQL 家族绕过 FormattableSqlFormatter 形状缓存
- 位置：`src/PalORM.Core/DataSession.cs:658`（对照 `src/PalORM.Core/FormattableSqlFormatter.cs:30`）
- 问题：FormatSqlWithParameters 调未缓存的 Format，每次一条完整复合格式扫描 + VSB 栈缓冲 + ToString 分配。QueryAsync/ScalarAsync/QueryAsyncEnumerable/ExecuteAsync/StoredProc/聚合 expression 全走此路径。格式串是编译期 ldstr 常量，满足"同调用点每次同一实例"的缓存前提。原始 SQL 是手写 SQL 用户的主路径。
- 依据：[事实]
- 建议：换调 `FormatCached(sql.Format, 0, sql.ArgumentCount)`，键形态与 QueryBuilder 完全一致，调用点零改动。

### P2-7 WithCache 查询每查询拼装缓存键，Set 走三遍字典
- 位置：`src/PalORM.Core/QueryBuilderExtensions.cs:18`、`src/PalORM.Core/CacheStore.cs:146`
- 问题：键两段在 From/WithCache 时即固定，拼接结果可一次定型，现状 TryGet 一次 Set 再一次；Set 单次写付 `Count`（ConcurrentDictionary.Count 是全分段锁遍历）+ ContainsKey + 索引器三遍。
- 依据：[事实]；成本量级 [推断]
- 建议：WithCache 调用时把 EffectiveCacheKey 写入 builder 字段；容量检查改 Interlocked 近似计数（CacheStore.cs:25 注释已预留此方案）。

### P2-8 ToListAsync 每查询两次 Enter 租约（3 次锁）
- 位置：`src/PalORM.Core/QueryBuilderExtensions.cs:216`、锁体 `src/PalORM.Core/SessionOperationState.cs:50`
- 问题：第二次 Enter 因 owner 相同走锁内空转分支，仍付一次 lock 获取释放 + 两组 ReferenceEquals；加外层 Exit 共 3 次锁，约 60-90ns。First/Single 族只付 1 次。
- 依据：[事实]（双 Enter 可见）；耗时 [推断]
- 建议：owner 非 null 即已入门禁，跳过第二次 Enter/Exit；语义由 ITM-613 门禁次序测试锁定，无行为变化。

### P2-9 GetQueryParameters 对零参数查询也分配空 List
- 位置：`src/PalORM.Core/QueryBuilder.cs:666`
- 问题：`_parameterCount == 0` 时仍分配空 List（约 32B）；`Array.IndexOf(kinds, clause.Kind)` 每子句 O(kinds.Length)，5 子句查询约 50 次比较。v4.6 的 `_clauseBitmask` 位掩码基础设施已在，此路径未接入。
- 依据：[事实]
- 建议：首行 `if (_parameterCount == 0) return [];`；IndexOf 改位掩码判定 O(1)。

### P2-10 BuildLimitClause 每执行重分配 List + 数组
- 位置：`src/PalORM.Core/QueryBuilder.cs:1138`
- 问题：每次分页查询（Take/Skip）新分配 `List<DbParameter>(2)` + `[.. parameters]` 集合表达式的第二个 DbParameter[]，单笔约 150-250B，每页一次。BuildSql 有形状缓存兜底，参数装配路径没有。
- 依据：[事实]
- 建议：直接 `new DbParameter[2]` 顺序填充返回，去掉 List 中转与二次拷贝。

### P2-11 CloneForExecution 每分页查询深拷贝全部子句参数
- 位置：`src/PalORM.Core/QueryBuilder.cs:601`
- 问题：每次 ToPageAsync 对每个子句新建 List + 逐参数重建 DbParameter，N 子句查询 = N 个 List 对象 + 全量参数副本，约 200-400B/查询。参数副本隔离是契约需要，List 外壳可省。
- 依据：[事实]
- 建议：构建期数量已知，用 DbParameter[] 作容器，IReadOfList 契约不变。

### P2-12 BuildSql 每查询重算形状哈希
- 位置：`src/PalORM.Core/QueryBuilder.cs:781`
- 问题：即使形状缓存命中也要先算哈希：含 string GetHashCode（O(表名长度)）+ 每子句一次 string 哈希 + 4-8 次 Combine，5 子句查询约 150-300ns。2026-09-19 已因"增量哈希三个写入者不一致"回退为纯函数现算，不能简单恢复增量维护。
- 依据：[事实]（每查询现算可见）；耗时 [推断]
- 建议：保持现算，可把 `(shapeHash, 物化数组版本号)` 存进 builder 的物化缓存行，重复执行同一 builder（ToPageAsync 前 BuildCountSql/BuildSql 两连发）时省一次全量哈希。

### P2-13 租户会话每 `From<T>()` 无条件分配 `_cacheTenantScope`
- 位置：`src/PalORM.Core/DataSession.Crud.cs:60`
- 问题：`__t:{tenantId}` 拼接（约 44B）在每个租户会话 From<T>() 上发生，只被 WithCache 路径消费。不用查询缓存的多租户应用是 100% 纯浪费。
- 依据：[事实]
- 建议：WithTenant() 时算一次存会话字段，From 只做引用拷贝；或与 P2-7 合并成 WithCache 时延迟拼接。

### P2-14 Query 族与 GridReader 未复用 CurrentState 快照
- 位置：`src/PalORM.Core/DataSession.Query.cs:14、48、132、189`、`src/PalORM.Core/GridReader.cs:47、84`
- 问题：每入口 1-2 次独立 Volatile.Read（全屏障），而 Crud 路径 v4.0 已改 CurrentState 单次快照（注释实测省约 2 次内存屏障/查询）。属同类改动内不一致的遗留。
- 依据：[事实]
- 建议：入口处取一次 RuntimeRegistryState 快照后复用，无行为差异。

### P2-15 GridReader.cs 缺 ConfigureAwait(false)（全仓唯一缺失点）
- 位置：`src/PalORM.Core/GridReader.cs:187`
- 问题：Dispose 的有界等待路径未配 ConfigureAwait(false)。Blazor Server/WPF/WinForms 宿主下 Dispose 的续会被 post 回 SynchronizationContext，若上下文线程正在同步等待该 Dispose 即死锁。经剥离注释/字符串的 AST 级扫描确认全仓仅此 1 处。
- 依据：[事实]
- 建议：一处补 `.ConfigureAwait(false)`。

### P2-16 FormattableString API 固有装箱（记录项，不建议改）
- 位置：`src/PalORM.Core/QueryBuilder.cs:940`
- 问题：每个值类型插值参数一次装箱（int 24B / Guid 32B），随后驱动侧拆箱映射。为 API 契约固有成本（Dapper 同），无法在不改公共签名族前提下消除。
- 依据：[事实]（装箱在 API 层可见）
- 建议：docs/ 性能章补一行成本说明；已评估过 WhereEquals<TValue> 泛型重载路线，装箱同点发生、无净收益，记录为已评估项。

## 三、内存方向（分配热点集中在批量写入）

### P1-17 多值 INSERT 批 SQL 重建存在中间 string[] + 双份大串，宽表直达 LOH
- 位置：`src/PalORM.Core/MultiValueBulkInsert.cs:121`、调用点 `:216`
- 问题：批大小变化时（首批 + 末次不满批，每次 BulkInsert 1-2 次）分配四层：string[batchLength]、batchLength 个行占位符小串、string.Join 大串、`+` 插值再一份大串。以 20 列实体、批 1000 行估：行串合计约 276KB，JOIN 结果约 276KB（>85000B 落 LOH），拼接再一份 276KB（LOH），瞬时约 830KB，其中约一半在 LOH。列数≥12 或批≥500 行稳定越线。同仓 `DataSession_Bulk.BuildBulkDeleteSql:140-181` 已验证过消除此形态的写法，INSERT 是漏网的一份。
- 依据：[事实]
- 建议：删掉 string[] 中间层，行循环直接向一个 ValueStringBuilder（stackalloc 起步 + ArrayPool 兜底）顺序写，最终 ToString() 只产一份大串。

### P1-18 BatchUpsert 每批重建设参数池（唯一未接入池范式的批量写路径）
- 位置：`src/PalORM.Core/DataSession_Bulk.cs:699`
- 问题：参数数组与全部 DbParameter 对象每批新建。10,000 行 × 10 列约 11 批 × 900 = 近 1 万个 DbParameter 对象，按每参数保守 100+B 估算约 1MB 量级 churn；命令也是每批新建。对照 `MultiValueBulkInsert.ExecuteBatchesAsync:202-242`（命令跨批复用 + 满批参数池只写 Value）。
- 依据：[事实]
- 建议：池按满批大小（batchSize × columnCount）进循环前建一次，批间只写 Value；末批缩短时用 DataSession_Bulk.AttachParameters 收敛命令参数集合。

### P2-19 BuildUpsertBatchSql 用堆上 StringBuilder 且容量公式低估
- 位置：`src/PalORM.Core/DataSession_Bulk.cs:766`
- 问题：每不同 rowCount（首批 + 末批）一次堆 char[] 分配 + ToString() 第二份。容量公式 `columnCount*8+4` 估不下 `@p65535` 形态（7 字符 + 分隔 = 9/列），超容量触发 StringBuilder 内部倍增复制，又多一次数组分配与拷贝。
- 依据：[事实]（StringBuilder 使用与公式可见；倍增为 BCL 已知行为 [推断]）
- 建议：换 ValueStringBuilder，容量按 columnCount*10+4 留余量。

### P2-20 乐观锁实体批量 UPDATE 每行一个闭包
- 位置：`src/PalORM.Core/DataSession_Bulk.cs:332`
- 问题：仅对带 [ConcurrencyCheck] 的实体：每行一个 DisplayClass + 一个 Action 委托，约 64-80B/行。10,000 行约 640-800KB 瞬时且提交前不可回收（存活内存）。语义上必须延迟到提交后回放（ITM-556 契约）。
- 依据：[事实]（闭包捕获与延迟语义可见）；量级 [推断]
- 建议：换值类型载体，如 `List<(T Entity, Action<T> Increment)>` 存实体数组 + 单一委托，每行只存一个引用。仅 IncrementVersion 非 null 的实体受益。

### P2-21 批量路径三处 per-call 字符串拼接可入既有 per-(Type, Dialect) 缓存
- 位置：`src/PalORM.Core/MultiValueBulkInsert.cs:65`、`src/PalORM.Core/DataSession_Bulk.cs:456、753`
- 问题：INSERT 列清单 / UPDATE SET 列数组 / UPSERT conflict 子句，每次 bulk 调用重建（Select 迭代器 + 委托 + 数组 + N 个 quote 串 + JOIN 结果），每次约 500B-2KB。值随 (Type, Dialect) 恒定，是 DataSessionCache 已覆盖同族键空间的漏网点。
- 依据：[事实]
- 建议：DataSessionCache 增加三个缓存项，查询失败即构建的 GetOrAdd 零闭包形态（同 QualifiedSelectColumnsCache 范式）。

### P2-22 DbCommand 每查询新建未池化（驱动内池兜底，登记备查）
- 位置：`src/PalORM.Core/QueryBuilderExtensions.cs:189`
- 问题：每查询一个 DbCommand + DbParameterCollection + 驱动内部缓冲（三驱动均约 150-250B）。主流驱动内部有命令池，实际堆压力被大幅吸收；未自建池属有意选择（命令带事务/超时/参数集合语义，跨查询复用易踩生命周期坑）。
- 依据：[事实]
- 建议：暂不动。未来做分配画像若发现 CreateCommand 显著，再按"租借-归还"设计池（必须 Reset 全部可变状态）。

### 已排除项（防后续重复审查）
- SELECT 热路径字符串插值/拼接：未发现。BuildSql 全程 ValueStringBuilder + 形状缓存命中即复用同一 SQL 实例；`"@p{N}"` 已被 ParameterNameCache 65536 项静态表消除。
- 装箱字典键 / enum 键：未发现。全部缓存键为元组或 int，默认比较器无装箱。
- COW 残留：未发现。全仓 Immutable* 零命中；子句链已用 cons list + 惰性物化数组替代。
- 缓存无界增长：未发现。BoundedQueryCache 与 SqlShapeCache 满 1024 拒写；DataSessionCache 以注册类型数 × 方言为界；FormattableSqlFormatter.ShapeCache 键空间由编译期常量封闭。ParameterNameCache 约 1.3MB 常驻为有意交换。
- 流式物化缺口：有路径覆盖。ForEachAsync 回调流式（100K 行实测 8.5MB → 0）；预分配有 MaxPreallocatedCapacity=4096 封顶。
- ValueStringBuilder 误用：未发现，唯一不一致处即 P2-19。

## 四、并发方向（连接池、并行执行、锁竞争、async 正确性）

### P1-23 事务收口有界等待（默认 5 分钟）把连接、行锁与池额度一起钉住
- 位置：`src/PalORM.Core/SessionOperationState.cs:212`、触发面 `src/PalORM.Core/DataSession.Transactions.cs:152`
- 问题：事务回调内出现未释放的 QueryAsyncEnumerable 枚举器（或网络黑洞下挂起的 reader）时，DisposeTransactionResourcesAsync 等待 5 分钟才抛。该 DbConnection 被独占、服务端事务/行锁不释放、池额度少一条。故障风暴下每发生一次泄漏，MaxPoolSize 可用连接线性下降，后续请求排队等池造成级联超时。DataSession.DisposeAsync 路径同病。
- 依据：[事实]（代码可见；注释自述触发条件为被放弃的枚举器）
- 建议：两段收口：先按 CommandTimeout 量级（约 30s）探测，超时后主动取消底层 reader/命令或直接 Close 连接归还池，而非无操作等满 5 分钟。与 P1-32 合并设计。

### P1-24 SessionBatch 是会话上唯一没有操作门禁保护的变更面
- 位置：`src/PalORM.Core/SessionBatch.cs:37`、`:48`
- 问题：ctor 内 CreateCommandForBatch() 无租约（还会在会话已有在飞事务时抓一份事务快照）；Append 直接改裸 List<T>。多线程 Append 或"Append 与 ExecuteNonQueryAsync 并发"会结构性损坏 List 或抛 InvalidOperationException: Collection was modified，且该异常在批量执行中途让整批事务回滚。其他所有变更入口（IgnoreFilters/AddInterceptor/WithTenant/WithIsolationLevel/UpdateResilience）都有门禁拒绝，唯独此处绕过。
- 依据：[事实]
- 建议：Append 纳入（可重入的）操作门禁或加实例级轻量锁，并文档化线程契约；_scratch 改为延迟到 ExecuteNonQueryAsync 内创建，与事务绑定时机对齐。

### P1-25 每会话单连接 + 单活动操作门禁，会话内无法 fan-out 并发读
- 位置：`src/PalORM.Core/SessionOperationState.cs:75`、读路由 `src/PalORM.Core/QueryBuilder.cs:560`
- 问题：并发查询只能靠多个 DataSession × 多条池连接。最常见的"一个请求一个 session、请求内并行发 N 个查询"写法被门禁直接拒绝；而读副本路径（ForRead）与会话主连接本就是两条物理连接，多只读操作并发的物理前提已具备，是纯策略没开放。直接压制"增加并发"使用形态的上限。
- 依据：[事实]（门禁与异常文案可见）；读取路由可并行的结论 [推断]
- 建议：提供 ForParallelReads()（或计数型只读租约 EnterReadOnlyParallel）：只读操作允许 N 个并发租约、各自绑定读连接；写操作与事务维持单租约语义。或在文档给出官方推荐组合（N session 共享 DbOptions），把"明确失败"升级为可组合能力。属公共 API 决策，需评审。

### P1-26 PreWarmAsync 串行建连，冷启动预热 100 条约 850ms
- 位置：`src/PalORM.Core/DataSession.cs:166`
- 问题：注释自述远程建连实测约 8.5ms/条，串行 for 把 100 条预热拉成约 850ms；OpenAsync 是纯 IO 等待，串行无任何收益。服务冷启动窗口内首波请求会撞上未暖好的池。
- 依据：[事实]
- 建议：有界并发预热（并行度取 Min(ProcessorCount, count) 或与 MinPoolSize 对齐）Task.WhenAll 扇出，失败语义（建连异常原样抛出）不变。

### P1-27 重试放大最坏延迟（与 P1-35 同一根因，合并处理）
- 位置：`src/PalORM.Core/Resilience.cs:81`
- 问题：CommandTimeout=30s + MaxRetries=3 单查询最坏约 90-122s 才抛，整段独占连接与操作租约。无 jitter 的固定退避（100→200→400ms）在故障风暴下惊群，把池连接慢性占满，正常请求排队等池。重试放大与池饥饿的经典组合。
- 依据：[事实]（时间乘法关系）
- 建议：见 P1-35 的总预算 CTS；退避加 jitter；XML doc 写出最坏延迟公式，让延迟敏感路径可配 MaxRetries=0。

### P2-28 GetActiveTransaction 每命令创建都过锁 + 驱动探针
- 位置：`src/PalORM.Core/DataSession.cs:603`、`src/PalORM.Core/SessionOperationState.cs:323`
- 问题：无事务（最常见）路径也付一次 lock + 一次 IsTransactionAlive（Npgsql 上取 transaction.Connection 是驱动调用，已释放事务走 ODE catch 路径）。bulk 家族每批仍 CreateCommand。
- 依据：[事实]
- 建议：`_transaction` 改 volatile 字段或 (bool hasValue, DbTransaction?) 快照，无事务路径锁外直读返回 null；复杂判定保留在锁内。

### P2-29 ToPageAsync 两个顺序 RTT，分页延迟翻倍
- 位置：`src/PalORM.Core/QueryBuilderExtensions.cs:371`
- 问题：COUNT 与页查询在同一事务内串行两次往返，跨网段分页延迟约 2×RTT。三方言（PG / MySQL 8+ / SQLite 3.25+）均支持 COUNT(*) OVER () 可与页行一次往返取回 Total；不需要 Total 的场景也无跳过 COUNT 的入口。
- 依据：[事实]（两次顺序往返可见）；窗口函数收益 [推断]（未实测）
- 建议：加 includeTotal 开关 + 窗口函数单往返形态（方言不支持回退两趟）；实测确认后再定默认。

### P2-30 无多连接并行写入入口
- 位置：`src/PalORM.Core/MultiValueBulkInsert.cs:210`、`src/PalORM.PostgreSql/PostgreSqlProvider.cs:236`
- 问题：单语句单连接协议下批内确实无法并行（PG COPY / MySQL LOAD DATA 已是协议最快形态）；但"可分区的一大批数据"（多实体、分片键范围）场景无多连接并行入口，用户自行并行又被单活动门禁挡住。
- 依据：[事实]（串行结构与门禁可见）；并行收益 [推断]
- 建议：显式 BulkInsertParallel：实体按分区交给 N 个独立 DataSession 并行执行，文档注明适用条件（无单一大事务要求、调用方保证顺序语义）。

### P2-31 PalORM_Runtime 读路径多处单属性 Volatile.Read
- 位置：`src/PalORM.Core/PalORM_Runtime.cs:91、94、97、132`，未复用快照调用点见 P2-14。
- 问题：各属性各自 Volatile.Read 全屏障；GridReader 每次 ReadAsync<T> 还各来一次。
- 依据：[事实]
- 建议：同 P2-14，入口单次快照后复用。

### 已排除项
- async void、未 await 的 fire-and-forget、阻塞调用（.Wait/.Result/GetAwaiter().GetResult 全 src 零命中）、SemaphoreSlim 静态共享、每查询新建 DbConnection、连接池配置映射（PG/MySQL 正确、SQLite 明确忽略）、Npgsql prepared 配置（MaxAutoPrepare=100 + NoResetOnClose 已到位）、PalORM_Runtime 全局锁粒度（读路径 Volatile.Read + FrozenDictionary 已无锁）、AsyncLocal 残留误判、CircuitBreaker/重试阻塞线程池（Task.Delay + generation 校验已防竞态）：均未发现。
- 大结果集未用 CommandBehavior.SequentialAccess：对峰值内存有影响，列可选。

## 五、可靠性与事务方向

### P0-32 MySQL BulkCopy(LOAD DATA) 失败路径无显式回滚
- 位置：`src/PalORM.MySql/MySqlProvider.cs:298`（对照 `src/PalORM.PostgreSql/PostgreSqlProvider.cs:340`）
- 问题：PG 侧 340 行有 `RollbackPreservingAsync(bulkTransaction, thrown, commandTimeoutSeconds)`，MySQL 侧 307 行只有 DisposePreservingAsync，是三 Provider 批量路径中唯一没调回滚的一处。失败（含 CommitWithTimeoutAsync 抛出的 InfrastructureTimeout 包装异常）后只走 DisposeAsync 的驱动隐式回滚，两种后果都是缺陷：若驱动在 Dispose 内发起 ROLLBACK，该网络往返无任何超时约束，发生在异常传播路径上，网络黑洞下把一次快速失败拖成永久卡死；若驱动不发起，服务端事务悬置到连接归还或回收，继续持锁与 undo 日志。且失败语义记到 PalORM.TransactionCleanupException，PalORM.RollbackException / RollbackTimeoutException 永不出现，回滚失败与未尝试回滚在诊断上不可区分。文件级注释明确要求两侧清理助手保持同步，此处已分叉。
- 依据：[事实]（代码可见，三路径实现不对称可直接比对）
- 建议：catch 内按 PG 侧口径补一次 RollbackPreservingAsync（有界 CTS + 超时挂 Data），并用 TrySkipRollbackAfterCommitFailureForException 裁决"确已提交成功"情形避免徒劳往返；清理助手注释锚点对齐 PG 侧。

### P1-33 QueryBuilder 裸取 .Connection，Npgsql 已释放事务抛 ObjectDisposedException
- 位置：`src/PalORM.Core/QueryBuilder.cs:513、516`、`src/PalORM.Core/QueryBuilder.cs:581`
- 问题：SessionOperationState.cs:305 注释是本仓库自证的驱动事实：Npgsql 对已释放事务取 Connection 抛 ObjectDisposedException，Microsoft.Data.Sqlite 返回 null。DataSession 侧同型判定已全部改用 IsTransactionAlive，QueryBuilder 这两处仍是裸取值。用户链 From<T>().WithTransaction(tran)，dispose tran 后再 ToListAsync()：PG 上得到驱动内部 ODE（堆栈指向 Npgsql 内部，消息与事务无关），SQLite 上才是设计好的 InvalidOperationException。同一种误用错误形态跨方言发散，与 ITM-637 修复目标相反；SQLite 驱动测试覆盖不到 PG 专有路径。
- 依据：[事实]（驱动行为由仓库内注释记录；代码不对称可直接比对）
- 建议：三处 .Connection 读取统一改为先 IsTransactionAlive 判定，存活时才 ReferenceEquals(tran.Connection, _conn)，与 SavepointAsync 处理顺序一致；补 PG 方言回归测试。

### P1-34 MySQL"失败 COMMIT 即服务端终止事务"裁决前提无来源
- 位置：`src/PalORM.Core/TransactionCleanup.cs:13`、判定实现 `:48`
- 问题：PG 侧有官方文档支撑（COMMIT 出错即回滚）；MySQL 侧注释仅写"错误处理同义"。查 MySQL 8.0 手册 15.3.1 未找到该论断的明确出处，只能找到"语句失败不自动回滚整个事务"这类相反倾向表述。若 MySQL 在 COMMIT 失败后仍保持事务活动，跳过回滚的代价是服务端锁与 undo 悬置到连接回收；而多回滚一次的代价只是一次失败往返挂 Data。当前实现把赌注押在未验证且仅存在于注释中的前提上。
- 依据：[事实]（代码与注释原文）；MySQL 语义部分 [推断]（手册未证实也未否证）
- 建议：补 MySQL 官方文档具体出处，或把裁决从"方言非 SQLite"收紧为"方言为 PG"，MySQL 照常回滚。推荐后者：一次徒劳往返的代价远小于服务端事务悬置，且与注释自己"判定保守偏向回滚"的声明一致。

### P1-35 重试无整体截止时间（可靠性+性能双收益）
- 位置：`src/PalORM.Core/Resilience.cs:81`
- 问题：`_timeout` 是每次尝试上界而非整次操作上界。默认 MaxRetries=3、CommandTimeout=30s、退避 0.7s 时最坏墙钟约 91s；调用方只有 ct，无手段表达"本请求最多 10 秒"。慢库雪崩时每请求占连接与线程近 91 秒，熔断阈值 5 需约 7.5 分钟才触发，期间池已压垮，重试反过来放大故障。
- 依据：[事实]（代码可见无 deadline 构造点）
- 建议：ExecuteAsync 入口用 CreateLinkedTokenSource(ct) 配可选 OverallDeadline（DbOptions 增加，Zero = 不设），与每 attempt 的 CTS 串联；总截止触发不再重试，包装为带 PalORM.InfrastructureTimeout 标记的 TimeoutException。至少文档写明最坏公式。

### P1-36 DisposeWaitTimeout 默认 5 分钟不可配不可观测
- 位置：`src/PalORM.Core/SessionOperationState.cs:20`、`src/PalORM.Core/DataSession.cs:25`
- 问题：internal 转发、注释自述测试专用。被放弃的枚举器让 WaitForActiveOperationPreservingAsync 与 GridReader.DisposeAsync 先无诊断挂 5 分钟才抛；生产遇到时既不能配置缩短止损，也无指标或日志提前告警。同类问题里 BulkOperationFramework.CapabilityProbeFailures 是 public 计数器，此处口径不一致。
- 依据：[事实]（可见性为 internal；默认值 5 分钟）
- 建议：提为 public 可配；进入等待前打 Warning 日志（含 WaitTimeout 与原因分类：active operation / active transaction / active read），让"即将挂 5 分钟"在 t=0 可见。

### P2-37 probe 命令裸 Dispose，清理异常可覆盖主异常
- 位置：`src/PalORM.Core/DataSession_Bulk.cs:289`、`:447`
- 问题：BulkOperationFramework.DisposePreservingAsync 与 TransactionCleanup.DisposeTransactionPreservingAsync 建立了统一约定（清理异常挂主异常 Data），这两处 probe 漏网：BindUpdate 抛参数数校验失败且 DisposeAsync 同时抛时，调用方看到的是释放异常，真正的生成器参数数漂移信号被吃掉。
- 依据：[事实]（模式与本仓库自身约定不一致）
- 建议：两处改 DisposePreservingAsync，同 ProbeBinderAsync 写法对齐。

### P2-38 RunInTransactionScopeAsync 的 finally 未按 ITM-704 嵌套
- 位置：`src/PalORM.Core/DataSession.Transactions.cs:284`（对照 `:177` WithTransaction）
- 问题：WithTransaction 用嵌套 try/finally 保证 Restore 抛异常也仍释放事务，Bulk 家族共用的这个内核没有。今天 RestoreTransaction 实际抛不出（只做锁 + IsTransactionAlive），属潜伏缺陷而非活缺陷；未来给 RestoreTransaction 加一行会读状态或打日志的代码就退化成"连接持开事务不释放"。
- 依据：[事实]（代码不对称）；当前可达性 [推断]
- 建议：RestoreTransaction 包进内层 try，finally 里调 DisposeTransactionPreservingAsync，与 WithTransaction 对齐；或抽 RestoreAndDisposeAsync 私有助手共用。

### P2-39 原始 DML 入口 OnBefore 异常路径无 finally，Stopwatch 不收尾
- 位置：`src/PalORM.Core/DataSession.Query.cs:284`（对照 `src/PalORM.Core/QueryBuilderExtensions.cs:126`）
- 问题：拦截器 OnBefore 抛异常时方法直接上抛，stopwatch 永不停表，OnAfter 与 OnError 都不触发。影响观测：WithMetrics/WithTracing 下产生永不收尾的 Stopwatch（换 Activity 即泄漏），begin/end 配对型拦截器只收到 begin。写路径独有，SELECT 侧有 finally。
- 依据：[事实]
- 建议：OnBefore 移入 try，finally 做 sw?.Stop() 与观测收尾，或整体改成与 ExecuteQueryAsync 相同的三段式。

### P2-40 拦截器 OnError 吞异常计数器是 internal
- 位置：`src/PalORM.Core/PalORMMetrics.cs:37`、契约 `src/PalORM.Core/IQueryInterceptor.cs:24`
- 问题：注释说"本计数让吞可观测"，但属性 internal，生产进程内无消费者。自身抛异常的审计拦截器会让某类查询的审计记录永久缺失且线上无人知晓。与 CapabilityProbeFailures 的 public 口径不一致。
- 依据：[事实]（可见性差异可直接比对）
- 建议：提为 public 静态属性，或吞掉时写 LogLevel.Warning（不含参数值）。

### P2-41 GridReader 取消后不进入终止态
- 位置：`src/PalORM.Core/GridReader.cs:65`、`:137`
- 问题：ReadAsync 因调用方 ct 取消抛后，_state 仍为 0，reader 与 command 未释放，GridReader 仍可再次 ReadAsync。第二次读取得到驱动内部异常（Npgsql 与 MySqlConnector 形态不同），跨方言发散，与 ITM-748"明确失败优先于驱动噪音"口径不一致；若调用方忘记 await using，reader 泄漏只靠 GC 终结器兜底。
- 依据：[事实]（状态机转换可见）
- 建议：catch 中识别取消时置 faulted 标志（与 _state=2 同一出口，消息指明"读取已取消"），后续 ReadAsync 得到确定性 PalORM 错误。

### P2-42 WithTimeout 拒绝 Zero，与 DbOptions 契约不一致
- 位置：`src/PalORM.Core/DataSession.cs:346`
- 问题：DbOptions.Validate 明确 CommandTimeout=Zero 合法无限等待，ToCommandTimeoutSeconds 与 Provider/Bulk 路径全链实现该契约；会话级链式 API 却拒绝 Zero。长跑报表场景要么改走 DbOptions 构造，要么被迫用很大的有限值，而后者在 Bulk 与 Resilience 的多个超时判据上与 Zero 不同分支。
- 依据：[事实]（两种入口同一参数两种接受域）
- 建议：拒绝条件改为 ThrowIfNegative，Zero 透传；XML doc 说明风险。

### P2-43 SessionBatch 的 DbBatch 路径无 CommandTimeout
- 位置：`src/PalORM.Core/SessionBatch.cs:91`（回退路径 `:119` 反而有）
- 问题：PG 与 MySQL 走 DbBatch 且无超时挂点，会话级 WithTimeout 静默失效；慢 DDL 或大批量语句按驱动默认（Npgsql 30s）或无界等待。违反 IDbProvider.cs:68"批量命令必须应用超时"纪律。MigrateAsync 的索引 DDL"保持逐条"只是文件内临时规避。
- 依据：[事实]（DbBatch 无 CommandTimeout 可由 API 面确认）
- 建议：批路径外 CTS CancelAfter 传进 ExecuteNonQueryAsync，与 Resilience.ExecuteWithTimeoutAsync 同构；并提升为用户可见文档的已知限制。

### P2-44 手工构造的 FormattableString 复用下标产生重名参数
- 位置：`src/PalORM.Core/FormattableSqlFormatter.cs:88`、`src/PalORM.Core/DataSession.cs:663`
- 问题：编译期插值串每洞独立下标不会撞名，但 FormattableStringFactory.Create("{0} and {0}", value) 手工构造形态会让格式串引用同一索引两次，BindFormattableParameters 仍按 ArgumentCount 绑定，SQL 出现两个 @p0。MySQL/SQLite 在 Add 或执行时报重名，PG 按位置绑定可能绑出预期之外组合，异常形态不可预期。
- 依据：[推断]（代码路径可见，未实跑验证驱动具体报错）
- 建议：Format 内维护 stackalloc bool 位集合检测重复下标，前移为库内 FormatException；单测：Create("{0}={0}", 1) 断言抛 FormatException。

### 已核查未发现（历史风险点专项核对）
- WithTransaction commit 失败半提交状态：未发现缺陷。commitAttempted 标志区分提交失败与 action 失败；回滚裁决集中 TrySkipRollbackAfterCommitFailure，v5.6.0 已收窄为连接类失败一律回滚、服务端错误才跳过，判定保守偏向回滚。
- 嵌套事务/savepoint：未发现。嵌套被 BeginTransactionCoreAsync 明确拒绝；savepoint 名经 ValidateSavepointName + QuoteIdentifier 双重防护；存活探测走 IsTransactionAlive 避开 ODE 陷阱。
- 事务内命令取消后一致性：未发现。回滚路径用 CancellationToken.None + 新 CTS，调用方 ct 取消不中断回滚。唯一例外即 P0-32。
- GridReader 未释放即提交：未发现。GridReader 登记为事务资源，commit 前统一释放；"先提交后读"被 _state != 0 拒绝。
- 批量部分失败补偿：未发现（自开事务路径）。三条自开事务路径失败即整批回滚；外部事务不回滚由调用方负责，XML doc 已明确。
- 资源泄漏：主路径均为 await using + DisposePreservingAsync 三件套；CancellationTokenRegistration 全仓仅 PG COPY 一处且 using 正确注销。
- 取消语义：ct 已真正传到驱动 Execute*Async；无 CommandTimeout 挂点的路径（COPY/LOAD DATA）改用 CTS CancelAfter 兑现；缺口即 P2-43。
- 熔断/重试竞态：generation 防陈旧探针、半开槽位超时回收、确定性失败不熔断（ITM-772）均审慎；写路径不重试、事务内不重试，非幂等重放已被契约阻断。
- SQL 安全：IdentifierSafety.ThrowIfUnsafe 覆盖 NUL/C0/DEL/C1 + 空名，三方言 QuoteIdentifier 翻倍转义内嵌引号，表名列名编译期常量，FormattableString 值恒进 @pN；注入面未见缺口，唯一残留即 P2-44。
- AsyncLocal 事务流判定（历史风险点）：设计正确。双条件判定 + ExitTransactionFlow 不清值的不变量在所有入口均未绕过。

## 六、SourceGen 生成质量方向

### P1-45 char 列物化每行一次完整 string 分配
- 位置：`src/PalORM.SourceGen/RowFactoryEmitter.cs:162`，生成产物 RowFactory_..._AllTypesEntity.g.cs
- 问题：`ReadChar` 走 `GetString(o)[0]`，每行每 char 列一次字符串对象分配（约 24-32B 对齐）+ UTF-16 解码，10 万行 2.4-3.2MB Gen0，串取 [0] 后即刻死亡，纯垃圾流量。GetChar 在 Microsoft.Data.Sqlite/Npgsql 抛 NotSupportedException 是注释自述的改用原因。
- 依据：[事实]（GetString 每次返回新 string）；消除手段收益 [推断]
- 建议：发射 `Span<char> one = stackalloc char[1]; r.GetChars(ordinal, 0, one, 0, 1);`。注意各驱动 GetChars 实现不一（Npgsql 内部仍先取整串再拷贝），采纳前三方言实测对比分配与耗时，无收益方言保留现状并注释登记。

### P1-46 可空引用列读路径依赖 NRT 注解，DB NULL 抛裸 SqlNullValueException
- 位置：`src/PalORM.SourceGen/RowFactoryEmitter.cs:92`，真源 `src/PalORM.SourceGen/TableModel.cs:171`
- 问题：引用类型无 NullableAnnotation.Annotated 注解时不生成 IsDBNull 守卫，DB NULL 的失败点是驱动层 SqlNullValueException，无列名无实体名。测试语料（软删实体）恰是启用 NRT 的正确形态，覆盖不到这条；老存量实体全部命中。这是生成代码唯一运行时崩溃面。
- 依据：[事实]（代码与注释可见，触发条件确定）
- 建议：两选一，需用户裁决（行为变更，非纯优化）。① 对引用类型列无条件发射 IsDBNull 守卫（值类型维持现状，非空由 DDL NOT NULL 同步保证），把"无信息异常"降级为"收到 null"；② 维持现状但生成失败时带列名的包装异常（成本高）。推荐 ①。

### P1-47 enum/未映射类型 fallback 读路径是暗雷且测试零覆盖
- 位置：`src/PalORM.SourceGen/RowFactoryEmitter.cs:165`
- 问题：`(T)r.GetValue(o)` 命中时每行每列付驱动装箱 + unbox 双重转换；更实质的是 TEXT 列 GetValue 返回 boxed string，`(MyEnum)` unbox 必抛 InvalidCastException，写路径（CommandFactoryEmitter.cs:477 绑 boxed enum 到 TEXT）同样不通。全仓生成代码 grep GetValue 命中 0，即从未被真实发射，上线前无人会发现。
- 依据：[事实]（fallback 形态与零覆盖）；运行期必抛 [推断]（string→enum unbox 语义确定）
- 建议：最低成本是 Analyzer 把"enum 且无 [Converter] 列"从 PALORM017 的 StoreAs 专项告警升级为点名读路径不可用；中期按 ITM-553 接入 StoreAs 三策略（int32/int64/string），发射 GetInt32(o)/Enum.ToObject 直读分支。

### P2-48 满批参数池路径每行每值列装箱
- 位置：`src/PalORM.SourceGen/CommandFactoryEmitter.cs:470`
- 问题：写入热路径剩余单一最大分配源。40,000 行 × 4 值列约 16 万次装箱约 3.8MB Gen0（boxed int 24B/个）。DbParameter.Value 是 ADO.NET object 契约，三驱动均无公开泛型 TypedValue 入口。
- 依据：[事实]（装箱可见）；不可规避 [推断]（三驱动公开 API 面）
- 建议：若要吃这块，只能在 IDbProvider 引入类型化参数写入扩展点（Provider 各自实现，其余回落 (object)value 行为不变），属抽象层变更，需 ADR + 三方言基准。不建议单独做。

### P2-49 HasDefaultKey 走 EqualityComparer<T>.Default.Equals 虚分派
- 位置：`src/PalORM.SourceGen/CommandFactoryEmitter.cs:146`
- 问题：每次 Save 一次泛型比较器虚调用（约 2-5ns）。col.ClrTypeName 编译期已知，SetId/IncrementVersion 已是直写形态，仅此处不对称。
- 依据：[事实]
- 建议：已知主键类型直发 `entity.Id == default`，其余保持现状。零风险。

### P2-50 AutoTagging 拦截器每查询一次插值 + 对编译期常量重复运行 ValidateSqlComment
- 位置：`src/PalORM.SourceGen/AutoTaggingEmitter.cs:144`，运行期 `src/PalORM.Core/QueryBuilder.cs:459`
- 问题：tag 字符串在生成代码里是字面量常量且发射期已完整转义，运行期 Tag 又做一次插值分配 + 逐字符扫描校验同一内容。opt-in 开启后每个终态查询一次固定税。
- 依据：[事实]
- 建议：新增 internal TagLiteral(string)（消费已包装常量、跳过校验、保留长度上限），AutoTagging 改为发射 TagLiteral。

### P2-51 Registry 包装 lambda 每 CRUD 一跳委托分派 + castclass
- 位置：`src/PalORM.SourceGen/RegistryEmitter.cs:116`
- 问题：lambda 不捕获已缓存为静态单例（无每调用分配），但每操作多一跳 Action invoke + castclass（约 2-3ns），占单次 CRUD 总开销 <1%。
- 依据：[事实]
- 建议：消掉需把 CrudMetadata.BindInsert 改泛型委托或泛型注册表，属架构级重构，登记待未来顺带收敛。

### P2-52 整行 RETURNING 回填每次 Insert 一次 GetOrdinal 字符串查找
- 位置：`src/PalORM.Core/DataSession.Crud.cs:141`
- 问题：非 key-only 路径每 Insert 一次按列名哈希查找（约 20-50ns），pkColumn 是注册表常量，编译期可知。key-only 窄路径（v5.6.0）已走 ExecuteScalarAsync 不碰 reader。
- 依据：[事实]
- 建议：生成器对主键非首列实体多发射 InsertReturningPkOrdinal（或 CrudMetadata 携带 PK 序号）。

### 已核对未发现
每调用新容器、每调用拼 SQL（参数名走 ParameterNameCache 65536 项静态表，SQL 全为编译期 const）、物化路径 GetOrdinal（生成代码内联编译期 ordinal，本仓最扎实设计之一）、is/as 判断链、DBNull 低效写法、参数名重做、元数据每实例构造（全部 ModuleInitializer 一次性）、DDL 每次拼接（三方言 DDL 全 const）、生成代码并发缺陷面（无可变静态状态）、乐观锁谓词生成（UPDATE version 谓词 + IncrementVersion 延迟回放，失败关闭）：均未发现。v3.1 的 IRowFactory<T> 委托化已消接口虚分派（Obsolete 待 v6.0 移除）。

## 七、执行顺序

按"修复成本 ÷ 预期收益"与依赖关系分四批：

**第一批：立即动（半天内，低风险，均有仓内范式可抄）**
- P0-32 MySQL 回滚（照抄 PG 侧 RollbackPreservingAsync 调用形态）
- P2-15 ConfigureAwait 一处
- P1-6 FormatCached 一处换调
- P2-14 / P2-31 CurrentState 快照对齐
- P2-49 HasDefaultKey 直发
- P2-9 / P2-10 零参数空 List 与 BuildLimitClause 数组化

**第二批：本周动（批量写入与缓存收敛）**
- P1-17 INSERT 大串（抄 BuildBulkDeleteSql 形态）
- P1-18 Upsert 参数池（抄 MultiValueBulkInsert 满批池）
- P1-1 / P1-2 过滤串与 COUNT 缓存（抄 GetByKeySqlCache）
- P1-3 / P1-4 读路径 272B（抄写路径已落地模式）
- P1-5 SQLite 批量静态判定
- P2-19 / P2-20 / P2-21 批量侧 P2 三件套

**第三批：需设计评审（公共 API 语义或行为变更）**
- P1-25 并行读租约（新公共 API）
- P1-46 NRT 无条件守卫（行为变更，见该项两个选项）
- P1-35 / P1-27 重试总预算（契约扩展）
- P1-24 SessionBatch 门禁（线程契约文档化）
- P2-30 并行 bulk 入口
- P2-48 类型化参数扩展点（ADR + 三方言基准）

**第四批：观测优先（先可观测后改默认，收益需实测）**
- P1-23 / P1-36 DisposeWaitTimeout：先提 public + Waring 日志，再论证默认值
- P2-29 ToPageAsync 单往返：BenchmarkDotNet 实测确认后动
- P1-45 GetChars 收益：三方言实测对比后逐方言决定
- P2-28 / P2-29 之外全部 [推断] 标注项：入库前实测

## 八、验收要求

所有性能项入库前按项目 PERF_MANAGED_DISCIPLINE 纪律执行：同会话交替配对 A/B（同配置自检报约 0 才信量具）→ 同负载复跑 ≥3 轮 → 分配与耗时双口径记录 → 优化后重录基线守住新水位。标 [推断] 的收益数字未实测，采纳前以实测为准；实测证伪的项从本文档划除并记录原因，不保留"理论收益"条目。

事务可靠性项（P0-32、P1-33、P1-34、P2-37 至 P2-44）每项配回归测试，其中 P0-32 需覆盖 MySQL LOAD DATA 失败（网络中断与 COMMIT 超时两种注入）断言语义，P1-33 需补 PG 方言用例（SQLite 驱动测试覆盖不到该路径）。

## 九、SQLite 极致优化批次执行记录（2026-09-25）

> 触发：引擎探针（SQLite3MC 3.53.4 实跑）+ 驱动源码逐行 + 社区经验三方深挖后的 17 项清单。
> 提交链：3551bd8（批次A/B/C）→ 批次D → 33c784d（批次E）→ ecc98d1（批次G）。测试基线：Core.Tests 421/421、SourceGen 202/202（快照更新 1 行，目检确认）、Integration SQLite 侧零失败（30 失败均为外部库连接超时的环境性失败）、SQLite AOT publish + 实跑通过。

### 已落地

| 项 | 内容 | 提交 |
|----|------|------|
| 1 | PRAGMA busy_timeout=5000（宽/窄分支共有）——并发 BUSY 引擎内等待，消上层 CTS+退避重试 | 3551bd8 |
| 2 | PRAGMA journal_size_limit=67108864（文件库）——防 WAL 无界膨胀拖慢检查点 | 3551bd8 |
| 4a | PRAGMA analysis_limit=400（宽/窄分支共有）——约束 optimize/ANALYZE 采样成本 | 3551bd8 |
| 4b | MigrateAsync 收尾跑一次 `PRAGMA optimize`（2026-09-26 增量深挖新增）——SQLite 官方对 schema 变更的建议，且引擎探针实测**编译选项无 STAT4**，ANALYZE 基础统计是计划器唯一统计来源，对 keyset/大 IN 查询计划有直接影响；memory 库空表场景不写 stat1 属 SQLite 语义，测试以"有数据+索引"形态锁定 | 增量批 |
| 3 | ~~参数上限 999→32766~~ **实测证伪回滚**（2026-09-26 同轮 A/B：32766 臂 BulkInsert 6.08× / UpsertBatch 15.80× / BulkDelete 1.84×，999 臂全部回 0.99~1.03×，ADO 臂逐位稳定无环境漂移——单语句参数绑定成本随参数数超线性，"减往返"收益远不抵绑定开销；维持 999） | 3551bd8 + 证伪回滚 |
| 8 | PL-2 惰性晋升扩展到 GetByKey——复用分支清参重绑走同一生成键绑定器（键类型转换语义逐位一致）；租户实体排除同 Update | 批次D |
| 9 | SessionBatch 顺序回退单命令复用（L37）——循环外建一条，同文本语句经驱动语句缓存免重编译 | 33c784d |
| 10 | SessionBatch 全无参语句合并单次多语句往返（L38）——驱动 RecordsAffected 跨语句累计（源码核实），返回契约保持；带参/混合/未知方言保持逐条 | 33c784d |
| 13 | OwnedJson 读路径 GetFieldValue<byte[]> + Deserialize(ReadOnlySpan<byte>)（L45）——消每行 UTF-16 JSON string 分配与双重转码；TEXT 列字节语义探针实测 | ecc98d1 |
| 5/6/7 | page_size / mmap_size / secure_delete 调优入口——经既有 SessionSetupSql 通道（Provider 初始化后执行可覆盖默认），XML doc 载明配方与安全取舍，不新增配置面 | 3551bd8 |

### 探针证伪划除（不保留理论收益条目）

- **L44 可空列单读（项12）**：`GetFieldValue<T?>` 读 NULL 抛 SqliteNullValueException（"The data is NULL at ordinal N"，探针实测 Microsoft.Data.Sqlite 11-rc）——IsDBNull 双读是驱动语义下的必需形态，不可单读。
- **L35 批量 Prepare（项15）**：驱动 `SqliteCommand.CommandText` setter 同值短路（`if (value != _commandText)`，源码核实）+ 语句缓存按命令实例生效——同文本批次本就免重编译，显式 Prepare 无增益；文本变化批次（行数不同的尾批）重编译不可避免。
- **命令复用写路径耗时收益**：探针实测同命令复用 vs 每次新建 1 万次 INSERT 仅差 0.5%（WAL 提交主导每操作成本）——复用的真实价值在分配/GC 与读路径（PL-2 原 A/B 的分配口径）。

### 缓议登记（非静默跳过）

- **P2-29 ToPageAsync 单往返（项14）**：SQLite 上 PerfHub KeysetPage P/ADO 已 1.03（平价），单往返无实测差距可收；改共享 SELECT 构建器为跨方言风险面。按本档"第四批观测优先"门槛，需专用 A/B 夹具实测后再裁。
- **L33 PRAGMA 池复用跳过（项11）**：初始化 PRAGMA 已是单次往返批（v5.0 形态），探测读取本身即 1 次往返，成本 ≥ 收益，负优化；维持无条件批执行。
- **大 BLOB 流式（项16）**：驱动 SqliteBlob/GetStream API 面已核实存在；暴露流式列读取是公共 API 语义变更，需 ADR + 三方言基准，归第三批设计评审。
- **sqlite-vec 向量搜索（项17）**：v6.0 设计文档 Phase 3（P2），依赖 Phase 1 核心与 Phase 2 PG 前置；sqlite-vec 已发 0.1.9（2026-03，新增 DELETE 空间回收与 KNN 距离列约束分页），动工前须刷新设计文档的版本假设。
