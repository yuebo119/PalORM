# PalORM 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 规范。

## [未发布] — byte[] 二进制列原生支持

> 1 个提交 · 12 个文件 · 四环节编译期契约补齐（读取/绑定/DDL/BulkInsert）

### ✨ 新增

- **`byte[]` 一维数组列原生支持**：不再强制 Base64 TEXT 或 `[Converter]` 中转
  - 白名单收窄放行：`SourceGenerationValidation.IsSupportedProviderType` 仅放行元素为 `System.Byte` 且 `Rank == 1` 的数组，`int[]`/`string[]`/多维数组仍被 PALORM016 拒绝
  - DDL 映射：`MigrationEmitter.GetBinaryDbType` 三方言 BYTEA（PG）/ BLOB（SQLite）/ LONGBLOB（MySQL，4GB 上限对齐 Pomelo 惯例）；MySQL 主键/索引列走 VARBINARY(255)（BLOB 索引前缀约束，错误 1170）
  - legacy 单方言 DDL（`TableModel.MapToDbType`）同步映射 BLOB
  - 写入绑定 `p.Value` 直通、PG Binary COPY 经 NpgsqlDbType 推断、MySQL BulkCopy 序列化——三路径经驱动层 PoC 实测逐字节往返一致（含 0x00 字节与 1MB 载荷）
  - 锁定测试：`ByteArrayColumns_GenerateCrudAndBlobDdl`（SourceGen）、`AllWhitelistedTypes_InsertAndMaterialize_RoundTrip`、`BulkInsert_ByteArrayColumn_RoundTripThroughMultiValueSkeleton`、`PG_MigrateAndBinaryCopy_NullableAndUtcDateTime_RoundTrip` / `MySql_MigrateWithUniqueIndexOnString_AndDecimalPrecision_RoundTrip`（真库）

### 🐛 修复

| 问题 | 影响 | 修复方式 |
|------|------|---------|
| byte[] 列落 TEXT 兜底（ITM-661(r4) 登记） | PG TEXT 拒 0x00 字节；MySQL strict mode 报 1366 Incorrect string value——二进制数据无法安全存储 | 白名单放行 + GetBinaryDbType 三方言 BLOB 映射，TEXT 兜底仅保留给真正未支持类型 |

### 🧪 验证

- **520 个测试全绿**：Core 195 + SourceGen 145 + Integration 180（含 PG/MySQL 真库 byte[] 往返）
- **快照基线** 145 份一致（重生成并人工评审 diff：四条 DDL 均为 BLOB 语义）

## [5.2.0] — 质量收口（14 轮 AI 评审 + record 支持 + 隔离级别全链 + 真库回归）

> 58 个提交 · 127 个文件 · +5390/−2939 行 · 48 个产品代码文件变更
> 14 轮 AI 自动化评审-修复迭代，每批修复经下一轮独立验证确认

## 💔 破坏性变更

- **`CrudColumns` 构造函数**：2 参数 → 3 参数（新增 `update` 列集）
  ```csharp
  // v5.1.0
  new CrudColumns(insertColumns, upsertColumns)
  // v5.2.0
  new CrudColumns(insertColumns, upsertColumns, updateColumns)
  ```
- **`IDbProvider.BulkInsertAsync`**：新增 `IsolationLevel` 参数（自定义 Provider 需同步签名）
- **`[Table]` 实体类约束**：record 类型现在被支持（不再是 class-only）

## ✨ 新增功能

- **record 实体支持**：`[Table] public record User { [Key] public long Id { get; set; } }` 现在完全可生成。
  - `get; set` 属性 → 真实生成（RowFactory/CommandFactory/Migration/Registry 全输出）
  - 位置参数 `record User(string Name)` → PALORM015 Error（缺无参构造，有定位诊断）
  - `[Key]` init-only → PALORM022 Error（防 CS8852，有定位诊断）
  - 锁定测试 ×3（GeneratorPhase2Tests）
- **隔离级别全链透传**：`WithIsolationLevel()` 现在在所有自开事务路径生效
  - `ToPageAsync`（此前仅单条 CRUD 生效）
  - `BulkInsertAsync`（PG COPY / MySQL BulkCopy / MySQL 多值 fallback）
  - `BulkDeleteAsync` / `BulkUpdateAsync` / `BulkUpdateBatchAsync` / `BulkMergeAsync` / `SeedAsync`
- **`CrudColumns.Update` 列集**：批量 UPDATE 不再反解 SQL 文本，直接消费编译期元数据
- **`SqlTemplate` 生成物 `global using System`**：限定引用（`{Math.PI}`）在无 ImplicitUsings 项目不再 CS0246

## 🐛 修复

| 问题 | 影响 | 修复 |
|------|------|------|
| MySQL Upsert 自增主键不回填 | `entity.Id` 恒为 0 | LAST_INSERT_ID 死分支 + 补 SELECT 后缀 |
| Raw 子句分页 Total 虚高 | 分页统计错误 | BuildCountSql 补 Raw 消费 |
| PG `WhereJson<T>(bool)` 恒空 | 查询静默返回空结果 | bool 特判小写 "true" |
| Audit `logParameters=false` 仍泄露 PII | PG DETAIL 参数值入日志 | else 分支不再传 exception 实例 |
| 连接池参数被静默覆盖 | 用户 `Max Pool Size=500` 被改为默认 100 | 仅默认值时覆盖策略 |
| Bulk 空列表+未注册类型静默 0 | 同一非法输入两种结果 | 六方法统一前置检查 |
| PgListener 首连挂起 | LISTEN 瞬态失败后 `StartAsync` 永久阻塞 | TrySetResult 锚定 startupPhase |
| ToPageAsync 缓存键污染 | 页截断结果写入用户缓存致同键 ToListAsync 丢行 | 克隆后清 `_cacheKey` |
| UPDATE + Take/Skip 扩大范围 | `.Set().Take(5)` 静默丢 LIMIT 更新全表 | 构建期显式拒绝 |
| PALORM024 谓词误报 | 纯 `[IgnoreOnInsert]` 实体被 Error 阻断 | 分析器谓词对齐生成器真源 |
| PALORM025 Nullable 时间误报 | `DateTime?` 属性被 Error 阻断 | UnwrapNullable 判定 |
| PALORM031/032 推断式调用漏报 | `BulkUpdateBatchAsync(list)` 不报 | 语义层 TypeArguments |
| `WithTransaction` 已释放事务误导报错 | 报"不属于主连接"而非"已释放" | 先查 Connection null |

## 📦 依赖升级

| 包 | 旧版本 | 新版本 |
|---|--------|--------|
| Microsoft.Data.Sqlite.Core | 11.0.0-preview.6 | 11.0.0-preview.7 |
| MySqlConnector | 2.6.1 | 2.6.2 |
| SQLite3MC.PCLRaw.bundle | 2.3.6 | 2.4.0 |
| Microsoft.CodeAnalysis.Analyzers | 5.3.0 | 5.6.0 |
| Microsoft.SourceLink.GitHub | 8.0.0 | 10.0.400 |
| TUnit / TUnit.Assertions | 1.61.38 | 1.65.0 |
| SonarAnalyzer.CSharp | 10.30 | 10.32 |

## 🧪 验证

- **518 个测试全绿**：Core 195 + SourceGen 144 + Integration 179（含 PG/MySQL 真库回归）
- **Native AOT** 三平台 publish 零错误 + 原生二进制运行通过
- **快照基线** 13 份一致（源生成器输出锁定）
- **每批修复经下一轮独立地毯评审验证**（连续 4 轮零 P0-P2 终证）

## 🔧 开发基础设施

- **敏感信息三层防护**：.gitignore 20+ 模式 → pre-commit hook 9 类检测 → CI gitleaks 全历史扫描
- **Release 自动化**：从 CHANGELOG 自动提取版本段 + 组装标准 Release Body
- **CHANGELOG 规范化**：全部版本统一 emoji 段头七段结构


## [5.1.0] — Auto Tagging Interceptor（SourceGen 自动 SQL 源码定位）

> 基于 `docs/v5.1-auto-tagging-design.md` + ADR-F。本版本引入 **opt-in 的自动 Query Tagging**，
> 用户零代码改动即可让 SQL 自动带源码定位注释（`/* 相对路径:行号 方法名 */`）。

### ✨ 新增

- **Auto Tagging Interceptor**（opt-in）：消费侧 csproj 设 `<PalORMAutoTagging>true</PalORMAutoTagging>` 启用。
  源生成器在编译期检测 `QueryBuilderExtensions` 的 6 个终态方法调用（`ToListAsync`/`FirstAsync`/
  `FirstOrDefaultAsync`/`SingleAsync`/`SingleOrDefaultAsync`/`ExecuteNonQueryAsync`），
  为每个调用点生成 `[InterceptsLocation]` 拦截方法，自动注入 `builder.Tag(...)` 后调用原方法。
  - SQL 日志自动含 `/* Controllers/UserController.cs:42 GetUserList */` 样式注释
  - 路径规范化：绝对路径 → 相对工作目录（避免泄露编译机目录结构到 DB 日志）
  - AOT 全兼容：net11 NativeAOT publish 0 警告实测通过
- **buildTransitive targets**：NuGet 包消费侧只需一行 `<PalORMAutoTagging>true</PalORMAutoTagging>`，
  targets 自动注入 `Features`/`InterceptorsNamespaces`/`CompilerVisibleProperty`
- **ADR-F**：`docs/adr/ADR-F-auto-tagging-interceptor.md`（决策记录 + 6 项技术约束）
- **B29 教训**：`.ai/lessons.md` AC 章节（Interceptor 实施工程化缺陷 + PoC 驱动 SOP）

### 🔧 技术约束（详见 ADR-F）

| 约束 | 说明 |
|------|------|
| `GetInterceptableLocation` 是扩展方法 | 在 `CSharpExtensions` 类，非 `SemanticModel` 实例方法 |
| `InterceptsLocationAttribute` 命名空间 | 编译器硬编码要求 `System.Runtime.CompilerServices` |
| MSBuild Property 传递 | 需 `<CompilerVisibleProperty>` 显式声明 |
| `TagWithCaller` 不能直接复用 | Caller* 在拦截器中返回拦截器自身位置，需用 `Tag(string)` + 编译期常量 |

### 🧪 测试

- 新增 4 个 AutoTagging 单元测试（开关关闭零生成 + 开关开启生成拦截器 + 签名匹配 + 路径规范化）
- `test/PalORM.AotTest.MySql` 扩展：启用 PalORMAutoTagging + 2 个拦截调用点
- 125 个 SourceGen 测试全部通过（零回归）

### 📚 参考

- 设计文档：`docs/v5.1-auto-tagging-design.md`（含"实施差异说明"章节，B9 教训）
- EF Core 参考实现：[Thirty25 博客](https://thirty25.blog/blog/2025/04/ef-core-source-gen-interceptors)

## [5.0.0] — 驱动现代化 + 调优（包升级 + 连接串/PRAGMA 性能调优）

> 基于 v5.0-roadmap.md 的 5 阶段方案。本版本聚焦**驱动层现代化 + 默认调优**，
> 不引入新公共 API。架构级改造（DbDataSource 单例化 / MySqlBulkCopy / DbBatch）
> 与功能增值（阶段 5）作为独立后续工作，不在 v5.0 主线。

### 💔 破坏性变更（测试源码层，公共 API 零破坏）

仅影响**测试代码**（src/ 公共 API 面零改动）：
- TUnit 0.19.24→1.61.15 引入 4 类测试 API 适配（10 处修复）：
  - `Assert.ThrowsAsync<T>(Task)` 重载移除→改用 `() => task`
  - `Assert.ThrowsAsync<T>` 返回类型可空化（`TException?`）→访问 `.Message` 加 `!`
  - `HasCount()` 已弃用→改用 `Count().IsEqualTo(n)`
  - `WithMessage(string)` 新增 `StringComparison` 重载→加 `Ordinal`

### 📦 依赖升级

| 包 | v4.6 | v5.0 | 备注 |
|---|:---:|:---:|------|
| TUnit | 0.19.24 | **1.61.15** | 测试框架现代化 |
| TUnit.Assertions | 0.7.9 | **1.61.15** | 与 TUnit 元包版本锁定 |
| Microsoft.NET.Test.Sdk | 17.13.0 | **删除** | TUnit 1.x 走 MTP 模式，不需 VSTest 桥接 |
| Npgsql | 9.0.3 | **10.0.3** | RowFactory 兼容（GetDateTime 仍返回 DateTime） |
| MySqlConnector | 2.4.0 | **2.6.1** | 含安全修复 GHSA-473q-m89c-ghf8 |
| BenchmarkDotNet | 0.14.0 | **0.15.8** | 基准工具升级 |
| Dapper | 2.1.66 | **2.1.79** | 基准对照升级 |
| RepoDb | 1.13.1 | **1.15.1** | 基准对照升级 |
| RepoDb.Sqlite.Microsoft | 1.13.1 | **1.15.0** | 基准对照升级 |

**保持不动**（有约束）：Microsoft.CodeAnalysis.Analyzers 5.3.0（5.6.0 不在 NuGet.Config 配置的 dotnet-tools 源，触发 NU1103）；Microsoft.Data.Sqlite.Core 11.0-preview.6（等 net11 GA）。

### ⚡ 性能调优

**PG 连接串调优**（PostgreSqlProvider.CreateConnection，用户显式值优先）：
- MaxAutoPrepare 0→100（自动预编译，查询延迟 -30~50%）
- AutoPrepareMinUsages 5→2（第 2 次起 Prepare）
- NoResetOnClose false→true（归还连接跳过 DISCARD ALL，+30% localhost 吞吐）
- ReadBufferSize/WriteBufferSize 8192→16384（大结果集/大值写入吞吐）
- Enlist true→false（跳过 TransactionScope 检查）

**MySQL 连接串调优**（MySqlProvider.CreateConnection，用户显式值优先）：
- AutoEnlist true→false / ConnectionReset true→false（归还池更快）
- CancellationTimeout 2→5（防连接泄漏）
- AllowLoadLocalInfile false→true（MySqlBulkCopy 前提，为后续阶段铺路）
- ServerRedirectionMode Disabled→Preferred（Azure MySQL 直连）

**SQLite PRAGMA 调优**（SqliteProvider.InitializeConnectionAsync）：
- synchronous=NORMAL（WAL 下安全，减少 fsync）
- cache_size=-65536（64MB 页缓存，默认 2MB）
- temp_store=MEMORY / wal_autocheckpoint=1000
- mmap_size=268435456（256MB，**仅文件数据库**，:memory: 跳过）

**global.json**：rollForward `disable`→`latestMinor`（允许 SDK 补丁版本前滚）。

### ✨ 新增：MySQL BulkInsert 能力检测分流

`MySqlProvider.BulkInsertAsync` 改为 **local_infile 能力检测** 分流（替代原 2000 行阈值）：
- **local_infile=ON**（服务端）→ 走 `MySqlBulkCopy`（LOAD DATA LOCAL INFILE 协议，~4.84x）
- **local_infile=OFF** 或非 MySqlConnection → 走多值 INSERT

**无阈值**：不再用行数阈值（2000 是伪精确），改为环境能力检测——行为可预测。与 PG COPY 永远走最优协议对齐。检测开销：每次 BulkInsert 额外 1 次 SHOW VARIABLES RTT（<1ms，批量场景占比可忽略）。

**部署约束**（已文档化）：客户端连接串默认追加 `AllowLoadLocalInfile=true`（v5.0 阶段 3.2）；服务端需 `local_infile=ON`（MySQL 默认 OFF，需 `SET GLOBAL local_infile=ON` 或 my.cnf）。

**DataTable 设计**：包含目标表全部列（含 AUTO_INCREMENT 主键），主键列填 NULL 让 MySQL 自增。

### 📐 调优判断策略

"用户未显式设置"的判据：属性当前值等于 ADO.NET 默认值。该判据在罕见场景（用户显式
设成默认值）下会把用户意图当作默认覆盖，但调优参数主动设成低性能默认值的实际场景极少，
收益（透明调优）大于风险。用户可通过显式设置非默认值避免被覆盖。

### ❌ 不做项（v5.0 排除 + 理由）

| 项 | 理由 |
|------|------|
| 阶段 3.4 NpgsqlParameter\<T\> 零装箱 | **已实测决策不做**——装箱占比 ~24.5% 但 PG COPY/MySQL BulkCopy 已无装箱；SQLite 无泛型参数 API。详见 `docs/boxing-benchmark-design.md` |
| 阶段 3.6 SQLite Pooling/Cache 解除限制 | 强制 Pooling 有测试隔离风险；用户可在连接串显式配置 Pooling=true / Cache=Shared |
| 阶段 4.1 DbDataSource 单例化 | **已批准 E1（不做）**——5 条 ORM 实践调研 + EF Core #3086 反面证据 + Npgsql 官方 "discouraged 非 deprecated"。详见 `docs/adr/ADR-E-dbdatasource-单例化取舍.md` |
| 阶段 4.3a BulkMerge 多值 UPSERT | 分流有害（语义偏差）+ RETURNING 多行回填复杂 + 用户无需求。BulkUpdateBatchAsync（4.3b）已完成 |
| 阶段 5.1 单操作 timeout/retry override | API 一致性困境（20+ 方法）+ 替代方案充分（多 DataSession / CancellationToken）|
| 阶段 5.6 ASP.NET Core 集成包 | 设计哲学冲突（无 DI）+ 替代方案充分（单例 DbOptions + 工厂模式）|
| 阶段 5.7 MySQL VECTOR 映射 | Innovation 版 + MySqlVector\<T\> API 不完整 + MySQL 8.4 测试环境不支持 |
| Microsoft.CodeAnalysis.Analyzers 5.6.0 | NuGet.Config 约束 Microsoft.CodeAnalysis.* 只从 dotnet-tools 源，该源无 5.6.0 stable |

### ✨ 功能增值（阶段 5 第一梯队）

**5.2 SessionSetupSql**（`DbOptions.SessionSetupSql` + `DbOptions.ReadSessionSetupSql`）：
- 主连接 + 读副本分别配置会话级 SQL（如 `SET TIME ZONE` / `SET search_path` / `SET statement_timeout`）
- 多条 SQL 分号分隔一次 `ExecuteNonQueryAsync` 执行（三方言均支持多语句）
- `IsNullOrWhiteSpace` 判断 null/空白等价未设置（向后兼容）
- 读连接初始化器：无 ReadSessionSetupSql 时用 static 委托（零闭包分配）

**5.4 AuditInterceptor**（`src/PalORM.Core/AuditInterceptor.cs`）：
- 实现 `IQueryInterceptor` 三段式：OnBefore/OnAfter/OnError
- Priority=200（让用户业务拦截器优先）
- `logParameters` 默认 false（脱敏，避免凭据/PII 写入日志）
- `ILogger.IsEnabled` 短路优化（无订阅者零开销）
- 覆盖面继承 IQueryInterceptor：仅实体 SELECT + QueryBuilder UPDATE；INSERT/DELETE/Bulk/存储过程不经过

**5.5b AdvisoryXactLock**（`src/PalORM.PostgreSql/AdvisoryLockExtensions.cs`）：
- 4 个扩展方法：`AcquireXactLockAsync(long)` / `AcquireXactLockAsync(int,int)` / `TryAcquireXactLockAsync(long)` / `TryAcquireXactLockAsync(int,int)`
- 事务级锁（`pg_advisory_xact_lock` / `pg_try_advisory_xact_lock`），事务结束自动释放
- 单 bigint key 和双 int key 是独立锁空间（不冲突）
- 用法：`await db.WithTransaction(async ct => await db.AcquireXactLockAsync(key, ct))`

**5.5 ForUpdate**（`QueryBuilder.ForUpdate(skipLocked)`）：v5.0 前已实现，本次确认存在。

### ✨ Scaffold 三 Provider 支持

`tools/PalORM.Scaffold` 从 SQLite-only 扩展为三 Provider：
- **新增 ISchemaProvider 抽象**（`tools/PalORM.Scaffold/ISchemaProvider.cs`）：屏蔽三方言元数据差异
- **三 Provider 实现**：
  - `SqliteSchemaProvider`：sqlite_master + PRAGMA table_info
  - `PostgreSqlSchemaProvider`：information_schema + JOIN tables/key_column_usage
  - `MySqlSchemaProvider`：information_schema.COLUMNS（DATABASE() 当前库过滤）
- **TypeMapper**（`tools/PalORM.Scaffold/TypeMapper.cs`）：DB 类型 → C# 类型，按方言分支
  - SQLite 按亲和性（INTEGER/REAL/TEXT/BLOB/NUMERIC）
  - PG/MySQL 按精确类型名 + 长度后缀裁剪（如 `varchar(255)` → `varchar`）
  - 覆盖 40+ 类型（含 uuid/jsonb/bytea/tinyint/DateOnly/TimeOnly 等）
- **EntityGenerator**：与 Provider 解耦，从 SchemaTable DTO 生成 C# 实体类
  - snake_case → PascalCase 转换（表名 + 列名）
  - 列名与属性名不同时加 `[Column("原名")]`
  - 自增 PK 加 `[Key(AutoIncrement = true)]`
  - 引用类型属性加 `= default!`（避免 nullable 警告）
  - 值类型可空列加 `?` 后缀
- **CLI 扩展**：`--dialect sqlite|pg|mysql` + `--namespace NS` + `--output DIR`
  - 方言别名：pg/postgres/postgresql、mysql/my、sqlite/sql
  - 兼容旧位置参数（args[1] 当 namespace）

**真实集成验证**（三 Provider 连真实库 scaffold）：
- SQLite：建表 → 生成 `UserOrders` 实体（含可空列 + 自增 PK）
- PostgreSQL：连 PG 18.4，生成 `AllTypesEntities`（uuid/bool/DateOnly/TimeOnly 类型完整映射）
- MySQL：连 MySQL 8.4.10，生成 `AllTypesEntities`（datetime/decimal/char 类型映射）

### ✨ 批量 UPDATE 单语句化

**新增** `DataSession.BulkUpdateBatchAsync<T>`（方案 Y 严格版）：
- 单次 RTT 完成 N 行 UPDATE（PG: UPDATE FROM VALUES 4x 提速；MySQL/SQLite: CASE WHEN）
- 永远走批量，无内部阈值，N=1 也走批量（用户显式选择）
- 带 `[ConcurrencyCheck]` 的实体调用抛 `NotSupportedException`（批量无法表达每行 version 匹配）
- 参数上限自动分批（PG/MySQL 65535，SQLite 999），物理约束非性能阈值
- 租户过滤自动追加 `AND tenant_id = @p`（与 BulkUpdateAsync 对齐）

**新增 BatchUpdateSqlBuilder**（`src/PalORM.Core/BatchUpdateSqlBuilder.cs`）：静态 SQL 构造器，与 DataSession 解耦降低认知复杂度。参数顺序对应 BindUpdate 输出：`[setCol0, setCol1, ..., pk]`（SET 列先，PK 在末尾）。

**与 BulkUpdateAsync 的语义差异**：
- BulkUpdateAsync 逐条执行 + 乐观锁检查（每行 affectedRows==1 否则 ConcurrencyConflictException）
- BulkUpdateBatchAsync 单语句批量 + 不区分"行不存在"与"并发修改"（返回受影响总行数）

### ✨ 诊断规则完整化（PALORM001-040）

**扩充 13 条新规则**（PALORM023-027, 031-033, 034-037, 040）：

- **P0 防运行时崩溃**（8 条）：
  - PALORM023/024：实体无可插入/可更新列（运行期 `throw`）
  - PALORM025：`[Timestamp]` 标在非时间类型（NOT NULL 无 DEFAULT 每次插入失败）
  - PALORM026：`[NotMapped]` 与映射特性互斥（避免 PALORM001 误报）
  - PALORM027：`[Converter]` 与 `[OwnedJson]` 互斥（消息比 PALORM015 更精准）
  - PALORM031：`BulkUpdateBatchAsync<T>` 对 `[ConcurrencyCheck]` 实体调用（必崩）
  - PALORM032：`Include/Join` 引用未注册实体（运行期 throw）
  - PALORM033：`Select(projection).ToListAsync()` 调用链（必崩）

- **P1 防静默错误**（5 条）——防止不 throw 但数据错/丢失/安全绕过：
  - PALORM034：`[Key]` 非默认初值让 SaveAsync 永远走 Update（数据静默丢失）
  - PALORM035：`[ConcurrencyCheck]+[IgnoreOnInsert]` 让乐观锁基线为 0（安全绕过）
  - PALORM036：`#nullable disable` 下引用类型不生成 IsDBNull 守卫（NULL 读取崩溃）
  - PALORM037：`[Required]` + 可空注解矛盾（DDL/读取行为不一致）
  - PALORM040：`[TenantAware]` 租户列可空（跨租户数据可见，多租户安全漏洞）

**修复 7 项现有规则缺陷**：

- F1：PALORM005 N+1 检测遗漏 Bulk/Save/Get 方法（功能遗漏）
- F2：PALORM002 消息"does not match table schema"语义错位（实际是"建议加 [Column]"）
- F3：PALORM017 对每个 `[ForeignKey]` 无条件报 Warning（过度报告，FK 在 Include 中有效）
- F4：PALORM015 消息"writable mapped properties"含义模糊（修订为原因清单）
- F5：PALORM012 类型限制未说明"emitter 用 ++ 自增"理由
- F6：PALORM003 跨程序集局限未在消息中提示可降级
- F7：PALORM010 无正例测试（补 `DoesNotReport`）

**精准化 1 条消息**：

- F8：PALORM009 补"partial sealed class"要求说明（STJ 源生成器约束）

### 🧪 验证

- dotnet build：0 错误
- Core.Tests: 174/174 通过
- SourceGen.Tests: 121/121 通过（原 104 + 新增 17 条诊断规则测试）
- Integration.Tests: 173/173 通过
- 总计：468/468 全部通过
- 环境：PG 18.4 + MySQL 8.4.10（MySQL local_infile=ON 部署约束）

---

## [4.6.0] — 极致性能（25+ 项分配优化）

> 基于 v4.0 实施后的深度性能审计与基准驱动迭代优化。
> 本版本为 **non-breaking**——公共 API 无破坏性变更。

### ⚡ 性能成果（vs v4.0 实测）

| 操作 | v4.0 分配 | v4.6 分配 | 改善 |
|------|:---------:|:---------:|:----:|
| GetByKey | 4.65 KB | **3.98 KB** | **-14%** |
| Insert | 5.16 KB | **4.97 KB** | **-4%** |
| Update | 8.31 KB | **7.44 KB** | **-10%** |
| BulkInsert 10K | 10.66 MB | **4.97 MB** | **-53%** |

BulkInsert 分配已优于 Dapper 62%（4.97MB vs 12.97MB）。

### v4.1：性能优化（IRowFactory 委托化 + Converter 单例 + 快照合并）

详见 v3.1/v4.0 CHANGELOG 条目。核心：IRowFactory → Func 委托、Converter static readonly 单例、CRUD Volatile.Read 合并。

### v4.2：极致降内存第一批（8 项）

- FormattableSqlFormatter 删除丢弃的 CompositeFormat.Parse + 改用 ValueStringBuilder
- QueryAsync 结果 List 起步容量 16
- InsertWithLastInsertId 预构建为 CommandSqlSet const
- QueryBuilder _clauses/_parameters 初始容量预分配 4/8
- 参数名预缓存 ParameterNameCache（@p0..@p1023 零分配索引取用）
- GetParametersForKinds 去 LINQ Contains + AsReadOnly
- 软删/租户过滤 SQL 片段 per-(Type,Dialect) 缓存
- From\<T\> 读连接工厂闭包提取为实例字段

### v4.3：ParameterNameCache public + probe 缓存跳过

- ParameterNameCache 提为 public，SourceGen binder emit 改用 GetName
- ProbeBinderAsync 结果缓存（InsertBinderValidated=true 跳过 probe）
- BulkInsert 10K 省 30K 次字符串插值

### v4.4：极致降内存第二批（8 项）

- BuildSql TrimEnd 先裁剪再 ToString（省 1 次 string 分配）
- CacheStore OTel counter 预构造 hit/miss KVP
- GridReader/StoredProcBuilder list 起步容量 16
- QueryClauseKinds 提为非泛型 static readonly（省临时数组）
- AppendSelectColumns sourceName quote 提循环外
- byte[] 列 GetValue → GetFieldValue<byte[]>
- BuildLimitClause 直接写 ValueStringBuilder

### v4.5：SessionOperationState TCS 延迟创建 + Exit 不写 AsyncLocal

- Enter 不创建 TCS，仅设 _isActive=true（省 88B/操作）
- Exit 不写 _currentOperationOwner.Value=null（省 ~300B EC 拷贝）
- Dispose/WaitForActive 条件创建 TCS

### v4.6：极致降内存第三批（6 项）

- owner=new object()→this + EC 相等短路（省 324B/操作）
- ExitTransactionFlow 不清 AsyncLocal（省 300B/事务收口）
- GetAsync 完整 SELECT SQL 缓存（省 120B/GetByKey）
- FormattableSqlFormatter 用 ParameterNameCache（省 32B/占位符）
- BulkInsert SqliteParameter 复用：新增 BindInsertValuesToBatch 旁路 binder，满批预分配参数池跨批复用（省 ~1.76MB/10K行）
- HasClause 位掩码 O(n)→O(1)（省 Predicate 委托分配/链式调用）

### 🧪 验证
- Core 161/161 + SourceGen 104/104 + Integration 160/160 = **425/425**
- 技术债扫描 12/12 + 门禁 G1-G29 全绿

---

## [4.0.0] — 性能与一致性收口（CommandFactory 单例 + Volatile 合并 + API 治理）

> 基于 v3.1 实施后的代码审查与 4 路并行深度调研产出，详见 `docs/v4.0-improvement-plan.md`。
> 本版本为 **non-breaking**——公共 API 无破坏性变更，仅 `DiffAsync` 标 `[Obsolete]`。

### ⚡ 核心优化

#### 优化 A：CommandFactory Converter 单例对齐（v3.1 最大遗留不对称）
- **问题**：v3.1 RowFactoryEmitter 已用 `static readonly _conv_<prop>` 单例，CommandFactoryEmitter 仍每次 `new Converter()`——同一项目内 emit 模式不对称
- **改动**：`CommandFactoryEmitter` 类级生成 `private static readonly IValueConverter<TClr,TProv> _conv_<prop> = new Converter();`
- **迁移路径**：BindInsert / BindUpdate / BindUpsert / BindDelete 全部从 `((IConverter)new Converter()).ToProvider(...)` 改为 `_conv_<prop>.ToProvider(...)`
- **收益**：百万行 BulkInsert 含 2 Converter 列省 ~200 万次 Gen0 分配
- **代码清理**：删除未使用的 `GetConverterInterfaceType` 方法

#### 优化 B：CRUD 路径 Volatile.Read 合并
- **问题**：v3.1 优化 3a 让 `From<T>()` 用了 `CurrentState` 单次快照，但 CRUD 路径（GetAsync / GetAllAsync / BulkDeleteAsync）未对齐
- **改动**：三处 `PalORM_Runtime.RowFactories/TableNames/ColumnNames` 独立访问 → 单次 `PalORM_Runtime.CurrentState` 快照
- **收益**：每次查询省 ~2 次内存屏障（fence）

#### 优化 D：List Capacity 起步优化
- **问题**：`ExecuteQueryAsync` 和 `GetAllAsync` 默认 `List<T>()` (=0)，10K 行场景扩容 14 次
- **改动**：默认 `new List<T>(16)` 起步
- **收益**：10K 行场景扩容次数从 14 降至 10

### 💔 API 治理（Breaking）

- **`DiffAsync<T>`** 标 `[Obsolete]`——本质是 `ValidateSchemaAsync<T>` 的字符串前缀薄包装
- **`GetRawConnection`** XML doc 强化「⚠️ 危险操作」警示（不重命名，避免破坏性）
- **`[Column].Length/Precision/Scale/TypeName/StoreAs`**：保留——已有 PALORM017 告警 + ITM-549 文档完整说明
- **`NamingConvention`**：保留——有实际 `ApplyNaming` 用途（自定义 SQL 场景归一标识符）
- **`WithMetrics(name)`**：保留——已有文档说明「名称仅保留 API 兼容」
- **`IRowFactory<T>`**：保留——v3.1 已决定作为公共契约

### 📚 AOT 文档补录

- **`docs/AOT部署指南.md`** 新增「聚合/标量查询 AOT 注意事项」小节
- 澄清 `Convert.ChangeType` 经评估确认 AOT 安全（走 IConvertible 接口分发）
- 非 IConvertible 类型（Guid/枚举/DateOnly）在 JIT 与 AOT 下行为一致

### 评估后跳过的方案项（B9 教训应用）

| 方案项 | 跳过原因 |
|--------|---------|
| 优化 C（QueryBuilder O(N²) 拷贝消除） | 构建时间占 QueryAll 4.73ms 的 0.02%，非瓶颈；重构将重新引入 QUERY-001 |
| 预构建 SQL（QuotedColumnList） | emit 三方言版本复杂度高，9-30KB 额外生成代码换取 ~150ns/查询 |
| 源生成器 emit 工程化（S2/S3/S4/S5） | S2 跨版本不稳定；S3 破坏快照基线；S4 实际仍被 MigrateAsync 使用；S5 增加状态参数 |
| AddRange 参数批处理 | ADO.NET Provider 行为不一致，风险高于收益 |
| Tracing/Metrics 短路 | 已是 const string + intern，零分配 |

### 🧪 验证

- 构建：0 警告 0 错误
- Core Tests：156/156 全绿
- SourceGen Tests：104/104 全绿（含快照重生成）
- 技术债扫描：12/12 通过

---

## [3.1.0] — 性能优化（IRowFactory 委托化 + Converter 单例 + 快照合并）

> 基于 v3.0.0 真实场景基准数据的深度优化，详见 `docs/v3.1-performance-plan.md`。

### ⚡ 性能成果（vs v3.0.0）

| 操作 | v3.0.0 | v3.1 | vs ADO.NET | 改善 |
|------|:---:|:---:|:---:|:---:|
| QueryAll 10K | 6.9ms (177%) | **5.35ms (132%)** | 4.05ms (100%) | -22% 时间 |
| GetByKey | 65μs (232%) | **26μs (141%)** | 18.4μs (100%) | **-60% 时间** |

### 优化 1：IRowFactory&lt;T&gt; → Func&lt;DbDataReader, T&gt; 委托（核心）
- **源生成器 emit 重写**：`sealed class : IRowFactory<T>` → `internal static class + static readonly Func<DbDataReader, T> Read` 委托字段
- **注册方式变更**：`RowFactories[type] = RowFactory_X.Instance` → `RowFactories[type] = RowFactory_X.Read`（委托装箱为 object）
- **所有调用点迁移**：QueryBuilder._factory、DataSession.Crud/Query、GridReader、StoredProcBuilder 共 9 处 `(IRowFactory<T>)factory).Read(reader)` → `((Func<DbDataReader, T>)factory)(reader)`
- **原理**：接口虚分发（vtable 查找 + 间接跳转）→ 委托直接 invoke（.NET 8+ JIT 对 static delegate invoke 有更好内联支持）

### 优化 2：Converter 单例缓存
- **RowFactoryEmitter emit**：每个 `[Converter]` 列从"每次 Read `new Converter()`"改为类级 `private static readonly IValueConverter<TClr,TProv> _conv_<prop> = new Converter();`
- **收益**：带 Converter 的实体每行每列省一次 Gen0 分配 + GC 压力
- **NRT 抚慰**：lambda 内 `_conv_X!.FromProvider(...)` 加 `!` 告知分析器字段已完成初始化

### 优化 3：state 快照合并 + Stopwatch 延迟 + 拦截器空跳过
- **3a：合并 Volatile.Read**：`From<T>()` 内 3 次 `PalORM_Runtime.RowFactories/TableNames/ColumnNames` 各自 `Volatile.Read` 合并为单次 `PalORM_Runtime.CurrentState` 快照（属性公开为 `internal static`）
- **3b：Stopwatch 延迟创建**：`ExecuteQueryAsync` / `ExecuteNonQueryAsync` 中 `Stopwatch.StartNew()` 改为仅在 Tracing/Metrics/拦截器任一启用时分配；热路径默认配置省一次 StartNew + Stop
- **3c：拦截器空列表跳过**：`foreach (interceptor) OnBefore/OnAfter/OnError` 加 `if (interceptors.Count == 0) return` 守卫；默认会话无拦截器时省迭代开销
- **抽取辅助方法**：`NotifyInterceptorsOnBefore/OnAfter` 共享于 SELECT/UPDATE 管线，降低认知复杂度

### 📦 文件变更
- `src/PalORM.Core/IRowFactory.cs` — 接口保留（向后兼容），XML 注释更新说明迁移
- `src/PalORM.Core/PalORM_Runtime.cs` — `RuntimeRegistryState` 从 private → internal，新增 `CurrentState` 属性
- `src/PalORM.Core/QueryBuilder.cs` — `_factory` 字段 + `QueryBuilderServices.Factory` 类型 `IRowFactory<T>` → `Func<DbDataReader, T>`
- `src/PalORM.Core/QueryBuilderExtensions.cs` — Stopwatch 延迟 + 拦截器辅助方法 + 调用点迁移
- `src/PalORM.Core/DataSession.Crud.cs` — `From<T>()` 快照合并 + 4 处 cast 迁移
- `src/PalORM.Core/DataSession.Query.cs` — 2 处 cast 迁移
- `src/PalORM.Core/GridReader.cs` — 2 处 cast 迁移
- `src/PalORM.Core/StoredProcBuilder.cs` — 1 处 cast 迁移
- `src/PalORM.SourceGen/RowFactoryEmitter.cs` — emit 重写
- `src/PalORM.SourceGen/RegistryEmitter.cs` — 注册值从 Instance → Read

### 🧪 验证
- Core Tests: 156/156 全绿
- SourceGen Tests: 104/104 全绿（5 个快照基线重生成 + 评审通过）
- SQLite Integration Tests: 149/149 全绿（7 个 PG/MySQL 环境变量失败与本次无关）
- 技术债扫描: 12/12 全通过
- 基准对比: QueryAll 177%→132%、GetByKey 232%→141%

## [3.0.0] — Breaking Changes（架构精炼 + Breaking API 移除 + 质量增值）

### 💔 Breaking Changes
- **移除 DataSession.ForRead() / ForWrite()**：请使用 `From<T>().ForRead()` / `From<T>().ForWrite()`
- **移除 CrudMetadata 旧 9 参 ctor**：请使用聚合 ctor（CrudBindings + CrudColumns）
- **移除 QueryBuilder.ThenInclude&lt;TGrandChild&gt;(单参)**：请使用双参 `ThenInclude<TGrandChild, TParent>(grandChildKey, parentKey)`

### ✨ 架构精炼
- **删除 8 个 Obsolete 公共 API**：MinimumLogLevel / ParameterPrefix / CreateConnection 单参 / GetLimitOffsetClause / LogQuery / RecordQueryStart / RecordQueryDuration / QueryBuilder 14 参 ctor
- **合并 TypeMapperEmitter → RowFactoryEmitter**：DateTimeOffset 读取直接内联 `GetFieldValue<T>`
- **PalORM_Runtime 拆 3 文件**：EntityFeatures.cs / SqlSets.cs / CrudMetadata.cs
- **Resilience 拆 CircuitBreaker + Exceptions**：熔断状态机独立为 `internal sealed class CircuitBreaker`
- **DataSession God Object 拆 4 partial**：Crud.cs / Query.cs / Transactions.cs / Schema.cs（1597→6 文件）
- **PgNotificationListener partial 拆分**：NpgsqlNotificationConnection.cs + PgNotificationEventArgs.cs
- **抽取 BulkOperationFramework**：消除 MultiValueBulkInsert/PostgreSqlProvider 间的 probe+cleanup 重复
- **ColumnModel 瘦身**：删除 4 个恒 null 预留字段（Length/Precision/Scale/DefaultExpression）
- **EquatableArray 独立文件**：从 TableModel.cs 提取

### 🧪 质量增值
- **集成 SonarAnalyzer.CSharp 10.29.0**：CI 守护层——P0 安全 + P1 设计规则全部为 error
- **BulkOperationFramework**：三 Provider 共享的 probe + cleanup 骨架
- **测试配置双层覆盖**：appsettings.test.json + .env.test + TestEnvironment 读取器
- **测试 helper 集中化**：TestInterceptors.cs（CountingTestInterceptor + CallbackTestInterceptor + OrderedInterceptor）
- **测试方法拆行**：AdvancedTests + QueryTests + FinalTests + MultiEntityTests 单行→多行
- **PALORM006/007 占位诊断删除**：零报告描述符移除
- **P1-2 规则升级为 error**：S3776/S107/S927/S2681/S125/S1066/S1994/S2189

### 📚 AI 系统
- **`.ai/lessons.md` v6.0**：14 个缺陷 + SOP + 决策矩阵 + 技术债扫描 SOP（自包含手册）
- **PR 模板**：编译/测试/Sonar/三方一致/精炼守护/反模式预防 6 类清单
- **`docs/编码规范.md` 第 18 节**：SonarAnalyzer 守护层规则配置

## [2.0.1] — 2026-07-15
- 初始发布
- Core + 3 Provider（SQLite/PostgreSQL/MySQL）+ SourceGen + Testing
- 面向严格 Native AOT（IsAotCompatible + IsTrimmable）
- 源生成器：RowFactory / CommandFactory / Migration / Registry / SqlFile / SqlTemplate
- 编译期诊断：PALORM001-040（33 条，含 P0 防崩溃 + P1 防静默错误 + 调用级 API 误用）
- 三方言支持：SQLite / PostgreSQL / MySQL
- 弹性执行器：重试 + 退避 + 超时 + 熔断
- 批量操作：MultiValue INSERT / PG Binary COPY
- 查询 DSL：Where/OrderBy/Take/Skip/Include/Join/CTE/Window/Cache
- 软删除 + 多租户 + 乐观锁
- PG NOTIFY/LISTEN
