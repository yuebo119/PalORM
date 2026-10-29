# PalORM 变更日志

本项目遵循 [Semantic Versioning 2.0.0](https://semver.org/lang/zh-CN/) 规范。

## [Unreleased]

### 💔 破坏性/行为变更

- PostgreSQL 上 `DateTime` 列的类型由 `timestamp`（无时区）改为 `timestamptz`。参数侧 `DbType.DateTime` 被 Npgsql 映射为 timestamptz，列与参数不匹配会让"读回再写回"抛 `ArgumentException`；统一后 DateTime 在 PG 上表示 UTC 时刻，往返可用。已有库需手工 `ALTER TABLE ... ALTER COLUMN ... TYPE timestamptz`（`MigrateAsync` 只建不改）。
- `ToPageAsync` 对 builder 上已设的 `Skip()` 由「静默忽略」改为抛 `InvalidOperationException`。键集分页以 `lastValue` 锚点续页，OFFSET 语义不适用；静默丢弃用户显式设置的子句会让分页结果与预期不符。
- SQLite 上 `ForUpdate()` / `ForShare()` 改为构建期抛 `NotSupportedException`。原来构建期放过、执行期由数据库报 `near "UPDATE": syntax error`；SQLite 没有行级锁，这两个 API 在该方言上从来不可用，本次把失败提前到调用点（ITM-639 终态）。

### 变更要点

- 弹性执行器出口解包驱动包装的命令超时：`NpgsqlException` 内层为 `TimeoutException` 时，向外抛 `TimeoutException`（保留原异常链）。熔断计数仍按原异常计算。
- 新增 `BulkInsertReturningAsync`：PG 与 SQLite 走多行 `INSERT ... RETURNING` 并按行序回填自增主键；MySQL 因 `LAST_INSERT_ID` 只保证首值而明确抛 `NotSupportedException`。原 `BulkInsertAsync` 保持最快的写入形态（不回填），两者分工写进 API 参考。
- PG 的 DateTime 往返回归测试 `PostgreSql_DateTimeRoundTrip_ReadThenWriteBackSucceeds` 锁定列型修复；方言对称性差异表登记 PG 侧 `TIMESTAMPTZ`。
- 行锁子句位于 `LIMIT` 之后的形态断言移到 PostgreSQL（`PostgreSql_LockClause_AppearsAfterLimit`），SQLite 侧只断言构建期拒绝。

## [6.8.0] - 2026-10-26

### 💔 破坏性/行为变更

- SQLite 上 `batchSize` 不再决定语句形状（≤0 仍拒绝），批内重复主键改为后行覆盖前行。

### 变更要点

- 项目协议自本版起由 AGPL-3.0-only 改为 Apache-2.0（v6.7.0 及以前发布物仍为 AGPL-3.0-only）。
- SQLite `BulkInsertAsync`/`BulkMergeAsync` 改为行预编译形态，批插 3.5~3.9×、批 upsert 2.4~3.1×。
- SQLite `BulkDeleteAsync` 改为 json_each 单参数形态，比参数化 IN 快 15 倍以上；Guid、ulong 主键与旧引擎回退 IN。
- 批删混合类型键在 JSON 构建期抛 `FormatException`，事务提交前全有或全无。

## [6.7.0] - 2026-10-23

### 💔 破坏性/行为变更

- `[TenantAware]` 租户列不再进入更新 SET；查询缓存租户键加长度前缀，`IgnoreFilters` 走独立命名空间；超时与退避上限 49.7 天。

### 变更要点

- 新增请求级查询管道 `CreatePipeline()`，PG 走 `NpgsqlBatch` 单往返，本机 4 条单键查询 3.2~3.4×。
- 新增 `UpdateColumnsAsync` 列子集更新，以及 `[Projection] readonly struct` 与 `QueryStructsAsync` 结构体投影。
- MySQL 复用命令晋升时自动预编译，重复单键读约 −10~18%；BulkCopy 部分写入失败改为透传原异常。
- 修复 OR/AND 链式括号不配对生成的非法 SQL、MySQL 非默认键 UPSERT 回填陈旧 `LAST_INSERT_ID`、CTE 后列引用未重限定等缺陷。

## [6.6.0] - 2026-10-20

### 💔 破坏性/行为变更

- `BulkInsertAsync(entities)` 在 PG 上默认单次 COPY（原 1000 行/批），需要限制单次行数时改用带 `batchSize` 的重载。

### 变更要点

- 版本化实体（`[ConcurrencyCheck]`）的 `BulkUpdateAsync` 在 PG UNNEST 与 MySQL 8.0.19+ 自动走单语句，SQLite 与低版本 MySQL 回退逐条。
- PG 版本化批量更新 18.6ms → 7.2ms，MySQL 52.4ms → 20.9ms（1000 行）。
- 新增 `DataSessionFactory<TProvider>`，以及 MySQL 事务内 DbBatch 分页（COUNT 与页 SELECT 同批）。
- 热路径收敛（SQLite 2000 档）：GetByKey 分配 −15.2%、Count −21.1%。

## [6.5.0] - 2026-10-14

### 💔 破坏性/行为变更

- 修复 `Where(A).OrWhere(B).Where(C)` 的运算符优先级（现生成 `((A) OR (B)) AND (C)`，查询结果可能变化）；引号内花括号改为原样透传。

### 变更要点

- 新增会话池 `DataSessionPool<TProvider>`，归还时复位瞬态状态；GetByKey 每操作会话 45.5µs → 3.3µs（13.8×）。
- 修复 `BeginTransaction` 预热门槛旁路造成的 AsyncLocal 泄漏，TxSingleInsert 每事务泄漏量 96924 → 4157 B。
- 修复非 async 入口让 AsyncLocal 写逃逸，SQLite 并发饱和循环 20× 塌方，相关入口改回 async。
- PG UNNEST 批宽上限 5000 → 20000，BulkUpdate/20000 批内耗时 −58%。

## [6.4.1] - 2026-10-11

- GetByKey 复用槽不再持有探针命令，晋升后每次读省一次参数创建与两次集合操作。
- IN 形态 BulkDelete 的后续满批不再为每个键建参数（20K 键约省 1.9 万个）。
- local_infile 与 MySQL 版本探测缓存改为连接串键控，per-op 会话每会话约省 0.5~0.65ms。
- AutoTagging 的语义工作改为输出段门控，关闭态下 300 个同名调用编译从 11.5ms 降至 1.9ms。
- 依赖升级 TUnit 1.73.5 → 1.73.19。

## [6.4.0] - 2026-10-11

### 💔 破坏性/行为变更

- `DbOptions.Production` 预设的熔断作用域由 Session 改为 Process，短会话下熔断真正生效；租户隔离可用独立连接串或显式回退 Session。

### 变更要点

- 修复 GridReader 释放路径缺少 `ConfigureAwait(false)` 的同步上下文死锁风险。

## [6.3.1] - 2026-10-07

- 修复多值 INSERT 分多批时 stackalloc 缓冲跨 await，Release 下第二批起可能抛 `NullReferenceException`。
- IN 形态 BulkDelete 恢复满批池化，SQLite 20000 键 66.8ms → 38.6ms。
- 修复并行读作用域内 GetAllAsync、CountAsync、聚合、ScalarAsync 与原始 SQL 未走读路由导致的 already open DataReader。
- 修复 PG 并发 `MigrateAsync` 竞态，42P07 纳入幂等兜底。
- PALORM046/041 诊断携带声明位置，PALORM017 死规则删除（诊断总数 46→45）。

## [6.3.0] - 2026-10-06

### 💔 破坏性/行为变更

- PG 批量 UPDATE 与 UPSERT 单批上限降为 1000 行，BulkUpdate/PG/2000 约快 2.8×。

### 变更要点

- 修复 PG auto-prepare 与命令复用槽组合导致的静默错数（同会话第 4 次起变键 GetAsync 返回旧行），5.7.0~6.2.0 受影响，临时可用 `MaxAutoPrepare=0` 规避。
- PG `BulkDeleteAsync` 改 `pk = ANY(@ids)` 单参数数组形态，时延 −52%、客户端分配 −99%。
- PG `BulkUpdate`/`BulkMerge` 同步切 UNNEST 数组形态。
- SQLite 池化连接初始化改为每个物理句柄只完整执行一次，`CreateAsync+DisposeAsync` 9.19µs → 1.80µs。

## [6.2.0] - 2026-09-30

- 新增 `DataSession.EvictQueryCache()`，写后可显式失效本会话查询缓存。
- FK DDL 随 `CREATE TABLE` 内联，OnDelete 支持 NO ACTION、CASCADE、SET NULL、RESTRICT；建表按 FK 依赖拓扑排序。
- 直查族 7 个只读入口接入读租约，`ForParallelReads` 作用域内可并发只读；GetByKey 复用槽在作用域内禁用。
- 新增 `PalORM.PostgreSql.VectorText`（pgvector 文本字面量）与 pgvector 探针。
- 15 个 `.sh` 与 1 个 `.mjs` 脚本迁移为 C#，并加脚本语言白名单门禁。

## [6.1.0] - 2026-09-29

- 枚举属性开箱支持，`[Column(StoreAs)]` 提供成员名 TEXT、AsInt32、AsInt64 三形态，缺省与旧映射一致。
- 新增 PALORM053，诊断总数 45→46。
- 参数池 DbType 发射统一为可空列与 byte[] 列，修 DBNull 落 Unknown 的慢路径。
- 通知监听器把空闲期断线的 IOException 与 ObjectDisposedException 包为 transient，避免永久终止。

## [6.0.1] - 2026-09-28

- 修复并行读作用域连接池无锁访问，避免同一连接发给两个 reader；池集合操作全部持锁。
- 修复 ForEachAsync 不归还池连接、OpenAsync 失败泄漏句柄、作用域重复 Dispose 提前释放外层池。
- 修复 `WithTransaction`/`UseTransaction` 与在飞只读租约并存，击穿读写互斥。
- 修复 SQL 形状缓存跨实体串列、查询缓存计数漂移、租户缓存作用域交错、提交成功后回滚已提交事务。
- 新增 PALORM052（索引名超方言上限告警），诊断总数 44→45。

## [6.0.0] - 2026-09-28

### 💔 破坏性/行为变更

- 删除 `IRowFactory<T>`、`DiffAsync<T>`、`DbOptions.NamingConvention`、`DbOptions.PoolExplicitlyConfigured`；`ColumnAttribute` 的 Length/Precision/Scale 由 `int?` 改为 `int`。

### 变更要点

- 新增 `[Projection]` DTO 投影，QueryAsync、QueryFirstAsync、QuerySingleAsync、GridReader.Read 可直接映射非表类型。
- 新增 `[DefaultValue]` DDL 与 `[Column]` 的 Length/Precision/Scale/TypeName 类型细化。
- 新增 `UniqueConstraintViolationException` 统一包装插入撞唯一约束；`WithMetrics(name)` 的名称转为 tag `palorm.query.name`。
- 诊断总数 39→44，注解 22→23。

## [5.9.0] - 2026-09-27

- MySQL 批量 UPDATE 由 CASE WHEN 改为 UPDATE JOIN VALUES ROW（8.0.19+），20000 行 8.75×，低版本自动回退。
- MySQL 乐观锁逐条 UPDATE 改 DbBatch 打包（100 命令一次往返，3.55×）。
- PG 同机制平移，逐条 118.7ms → 15.1ms（7.85×）；SQLite 实测无收益不移植。
- `MigrateAsync` 收尾在 PG 执行 ANALYZE，与 SQLite 的 `PRAGMA optimize` 同构。

## [5.8.0] - 2026-09-27

- 修复 PG 可空列读慢路径：显式 DbType 避免每次读重新推断，null 列 261.5ms → 6.6ms。
- PG 20000 档批量分配 BulkInsert −38%、BulkDelete −34%，时延 BulkInsert −13.8%、BulkDelete −11.0%。
- 参数占位符改预建表取用、BulkDelete 参数按批转移复用，消除每键新字符串与参数对象。
- `CreateConnection` 改写结果缓存，12µs → 0.53µs；每操作 `CreateAsync` 25.1µs → 2.67µs。
- PerfHub 新增租户、OwnedJson、SessionBatch 夹具族。

## [5.7.0] - 2026-09-26

- SQLite 新增 busy_timeout=5000、journal_size_limit、analysis_limit 三项 PRAGMA；`MigrateAsync` 收尾执行 `PRAGMA optimize`。
- GetByKey 单行读接入命令复用（第 3 次起）。
- SessionBatch 的 SQLite 回退把全无参语句合并为单次多语句往返。
- OwnedJson 读路径回退 GetString，UTF-8 Span 解析在 MySqlConnector 的 TEXT 列抛 `InvalidCastException`。
- 实测证伪把 SQLite 参数上限提到 32766（批量劣化最高 15.8×），维持 999。

## [5.6.0] - 2026-09-25

### 💔 破坏性/行为变更

- 池空闲超时默认值 30 秒改为 0（不覆盖驱动默认），显式需要回收时用 `WithPool` 指定。

### 变更要点

- 单行插入在非自增主键且全列可插入时走纯 INSERT 不回读，单条写 5.79µs → 2.72µs，返回传入实体同一引用。
- BulkMerge 改参数池直写，分配 982 → 789 B/行；UpsertBatch 分配相对 ADO 由 +32%/+41% 转为 −4%/0%。
- 单行 CRUD 命令与参数跨调用复用（第 3 次起），逐条 UPDATE 参数池化 −41~62%。
- 新增 SessionBatch（事务内 N 语句一次往返，PG 3.4×）、`BulkMergeAsync` 集合化（远程 8~10×）、`BulkUpdateAsync` 自动路由批量。

## [5.5.1] - 2026-09-11

- 依赖升级：Roslyn 5.9.0、Microsoft.Data.Sqlite.Core 11.0.0-rc.1、SonarAnalyzer 10.34、TUnit 1.66.27、SourceLink 10.0.401、RepoDb 1.16.0。
- bench 的 SQLitePCLRaw 高危漏洞 GHSA-2m69-gcr7-jv3q 随升级消除。

## [5.5.0] - 2026-09-11

### 💔 破坏性/行为变更

- `[SensitiveData]` 脱敏改为运行时注册表传递掩码，端到端覆盖真实执行路径；空白 Mask 归一为默认掩码。
- 外部事务被外部释放后改为持续失败，不再静默自动提交。

### 变更要点

- 回退上一轮对 MySQL 1061 的过度修复，恢复正常二次迁移的警告并跳过。
- 修复只读 SQLite 连接设置 WAL 抛 Error 8、WhereJson 方言守卫、ConnectionTimeout 缺上限等。
- 括号平衡检查补齐 MySQL 反斜杠转义、方括号标识符、PG dollar-quoting 三类词法；PALORM031 降为 Warning，诊断总数 37→39。

## [5.4.0] - 2026-09-02

### 💔 破坏性/行为变更

- 弹性策略接入只读查询内置管线，默认配置下只读瞬时故障自动重试并计入熔断。
- 外部事务被外部释放后改为响亮失败，不再静默降级为自动提交；`QuerySingleAsync` 改为流式精确单行。
- 移除 legacy 无方言 CommandSqls 与 CreateTableSql 生成段，重编译模型程序集须同步升级 Core 与 Provider 包。

### 变更要点

- 新增 PALORM042-045 编译期诊断，生成器 transform 失败不再崩溃或静默跳过。
- `[SqlFile]` 的 `.sql` 内容进入增量缓存键，仅编辑 `.sql` 也触发重新生成。

## [5.3.0] - 2026-08-22

- `byte[]` 一维数组列原生支持，DDL 映射 PG BYTEA、SQLite BLOB、MySQL LONGBLOB，主键与索引列用 VARBINARY(255)。
- 绑定显式发射 `DbType.Binary`，等值过滤可参数化直通（含 0x00 字节）。
- Scaffold 反向工程支持 BLOB、bytea、varbinary，可空引用列补 `?` 与 IsDBNull 守卫。
- 64KB 二进制列基准：原生 BLOB 相比 Base64 TEXT 延迟 2.3 倍、分配 6.3 倍优势。

## [5.2.0] - 2026-08-17

### 💔 破坏性/行为变更

- `CrudColumns` 构造由 2 参改为 3 参（新增 update 列集）；`IDbProvider.BulkInsertAsync` 新增 `IsolationLevel` 参数。

### 变更要点

- `[Table]` 支持 record 类型，位置参数 record 与 init-only `[Key]` 给出定位诊断。
- 新增 `CrudColumns.Update` 列集，批量 UPDATE 直接消费编译期元数据；`WithIsolationLevel` 在 ToPageAsync 与全部批量、Seed 路径生效。
- SqlTemplate 生成物补 `global using System`，无 ImplicitUsings 的项目不再 CS0246。
- 修复 MySQL Upsert 自增主键不回填、Raw 分页 Total 虚高、PG `WhereJson<bool>` 恒空、审计关闭参数记录仍泄露 PII 等。

## [5.1.0] - 2026-07-30

- 新增 opt-in 的 Auto Tagging Interceptor，csproj 设 `PalORMAutoTagging` 为 true 后为 6 个查询终态调用自动注入源码位置注释。
- 路径归一为相对工作目录，Native AOT 下可用。

## [5.0.0] - 2026-07-27

- 依赖升级：Npgsql 10.0.3、MySqlConnector 2.6.1（含安全修复）、TUnit 1.61.15、BenchmarkDotNet 0.15.8，移除 Microsoft.NET.Test.Sdk。
- 连接串与 PRAGMA 默认调优：PG 开 MaxAutoPrepare=100、NoResetOnClose=true，SQLite 设 synchronous=NORMAL、cache_size 64MB、mmap_size 256MB（仅文件库）。
- MySQL BulkInsert 由行数阈值改为 local_infile 能力检测，ON 走 MySqlBulkCopy，OFF 走多值 INSERT。
- 新增 SessionSetupSql、AuditInterceptor、PG 事务级 advisory lock、`BulkUpdateBatchAsync` 单语句批量。
- 诊断补至 PALORM001-040，Scaffold 扩到 SQLite、PostgreSQL、MySQL 三 Provider。

## [4.6.0] - 2026-07-24

- 25 项以上分配优化，GetByKey 分配 −14%、Update −10%、BulkInsert 10K −53%。
- BulkInsert 10K 分配 4.97MB，优于 Dapper 62%。
- 参数名缓存、软删与租户片段缓存、CRUD SQL 预构建、BulkInsert 参数池跨批复用、HasClause 位掩码等。

## [4.0.0] - 2026-07-22

- CommandFactory 的 Converter 改为类级 static readonly 单例，与 RowFactory 对齐。
- CRUD 路径的三次 Volatile.Read 合并为单次 CurrentState 快照；结果 List 起步容量 16。
- `DiffAsync<T>` 标记 `[Obsolete]`。
- AOT 文档补充聚合与标量查询注意事项。

## [3.1.0] - 2026-07-21

- `IRowFactory<T>` 改为 `Func<DbDataReader,T>` 委托，接口保留兼容；QueryAll 10K 时延 −22%、GetByKey −60%。
- Converter 改为 static readonly 单例，消除每行每列一次 Gen0 分配。
- Runtime 状态读取合并为单次快照，Stopwatch 仅在开启 Tracing、Metrics、拦截器时创建，空拦截器列表跳过。

## [3.0.0] - 2026-07-21

### 💔 破坏性/行为变更

- 移除 `DataSession.ForRead()`/`ForWrite()`（改用 `From<T>()`）、`CrudMetadata` 旧 9 参构造、`QueryBuilder.ThenInclude` 单参重载。
- 删除 8 个 Obsolete 公共 API。

### 变更要点

- DataSession 拆为 Crud、Query、Transactions、Schema 四个 partial，熔断状态机独立，抽取 BulkOperationFramework。
- 集成 SonarAnalyzer.CSharp 10.29.0，P0 安全与 P1 设计规则为 error。

## [2.0.1] - 2026-07-15

- 初始发布：Core、SQLite/PostgreSQL/MySQL 三 Provider、SourceGen、Testing。
- 面向 Native AOT（IsAotCompatible + IsTrimmable），源生成器 6 条管线。
- 编译期诊断 PALORM001-040（33 条）、弹性执行器、批量操作、查询 DSL、软删与多租户与乐观锁、PG NOTIFY/LISTEN。
