# PalORM 全面评审整改任务账本

> 基线：`dev@68eafb68b0f7`。任务只有在实现、对应测试和验收命令均通过后才能标记完成。`AUD-001 / G9` 按既定决定仅记录、不处理，因此本轮不能标记为 100% 完成。

## 状态

| ID | 级别 | 任务 | 状态 | 验收证据 |
|---|---|---|---|---|
| REV-001 | P1 | 建立失败基线与任务账本 | 已完成 | Core 59/59；SourceGen 基线 27/27；无外部服务 106/106；G9 失败 5 项 |
| DEP-001 | P1 | 移除未使用且存在漏洞的 OpenTelemetry.Api，升级 SQLite 安全包 | 已完成 | 漏洞扫描零发现；严格构建 0 warning / 0 error；SQLite Native AOT 原生运行通过 |
| BUILD-001 | P2 | 固定仓库和 CI 的 .NET SDK | 已完成 | `dotnet --version` 精确匹配 `global.json`；CI 与质量脚本统一读取该文件 |
| PKG-001 | P1 | 修复 PalORM.Testing Provider 包依赖 | 已完成 | 六个正式包成功 pack；Testing nuspec 含四个 PalORM 依赖；独立消费者 0 warning / 0 error |
| PKG-002 | P2 | 非发布项目显式 `IsPackable=false` | 已完成 | benchmark、Scaffold、AOT host/model/consumer 和测试项目共十项 evaluated 值均为 false |
| GEN-001 | P1 | 统一 SourceGen INSERT 列 predicate | 已完成 | SQL、BindInsert、BindInsertValues、InsertColumns 共用 `IsInsertable`；Computed/Timestamp 测试通过 |
| GEN-002 | P1 | 生成代码不依赖 ImplicitUsings | 已完成 | Roslyn driver 与打包 analyzer 消费者均在禁用 implicit usings 下严格编译通过 |
| GEN-003 | P1 | 生成辅助类型使用完整实体身份 | 已完成 | helper suffix 使用 fully-qualified identity + 稳定 hash；跨 namespace 同名实体编译通过 |
| GEN-004 | P1 | 支持全局 namespace，诊断泛型/嵌套实体 | 已完成 | 全局实体编译通过；泛型和嵌套实体各报告一个 PALORM015 且不生成级联错误 |
| GEN-005 | P1 | 统一计算列与编译期值转换契约 | 已完成 | 迁移生成 STORED 计算列；PALORM016 拒绝未知类型、nullable Provider、OwnedJson/Converter 冲突和无 DDL 契约的 Provider 类型；Converter 主键与 Upsert 返回形状经 SQLite 往返验证；SourceGen 51/51、无外部服务集成 118/118 |
| GEN-006 | P1 | 三方言 SQL/DDL、标识符引用与实体列序 | 已完成 | 编译期生成三方言 SQL/DDL；SQLite 保留字 CRUD 通过；实体查询按模型列序投影；MySQL 非数值键不进入 `LAST_INSERT_ID(expr)`；attribute 标识符经 Roslyn 字面量编码；Core 60/60 |
| DATA-001 | P1 | BulkDelete 遵守 SoftDelete 语义 | 已完成 | 500/批；表名与主键来自生成元数据；Converter 主键走 BindDelete；SoftDelete 更新 deleted_at；501 键外部事务回滚通过 |
| DATA-002 | P1 | 三 Provider BulkInsert 使用生成的 InsertColumns | 已完成 | SQLite/MySQL/PG 共用 CrudMetadata.InsertColumns/BindInsert 并预检参数形状；IgnoreOnInsert 数据库默认值通过；零列明确失败 |
| SQL-001 | P2 | 统一 FormattableString SQL formatter | 已完成 | DataSession、QueryBuilder、QueryMultiple 共用 BCL 语法验证与单次扫描；alignment、format specifier、escaped braces、重复索引、多位索引和非法格式已覆盖 |
| LIFE-001 | P2 | 三 Provider 保留批量写主异常 | 已完成 | SQLite/MySQL 真实 Provider 矩阵覆盖 probe 无主异常、row/main 双清理、执行+rollback+transaction cleanup；异常按资源独立附加；PG probe/rowCommand 同步保护 |
| GRID-001 | P2 | GridReader 单消费者和幂等释放 | 已完成 | 单活动读取；Dispose 等待读取并拒绝新读；reader/command/connection/session lease 全部尝试；重复 Dispose 共享 Task 和异常 |
| TX-001 | P2 | DataSession 并发操作和事务串扰 fail-fast | 已完成 | DataSession、QueryBuilder、Bulk、StoredProc 共用 operation state；重叠操作与派生 child flow fail-fast；事务拒绝 sibling/nested；Dispose 等待事务 scope；主异常保留 |
| QUERY-001 | P1 | QueryBuilder struct 分支形成隔离快照 | 已完成（二次修复） | ~~EnsureWritable 写时复制~~ 2026-07-17 评审（ITM-101）证实一次性 `_writable` 标志随 struct 复制被拷贝，场景 B（复制已写入 builder）与场景 C（SoftDelete/TenantAware 实体出生即可写）仍污染。二次修复：AddClause 无条件写时复制；补 4 个场景 B/C 用例并反向验证（撤回修复 → 用例确定性失败）；Core 124/124 |
| GATE-001 | P1 | G12 改为 Roslyn 语法级门禁 | 已完成 | gate-check.sh G12 PASS；clean/fail/recover 夹具完备 |
| DOC-001 | P1 | G9、Core 依赖和门禁文档恢复真实状态 | 已完成 | G9 标为 FAIL/AUD-001；Core 依赖更新为 BCL-only |
| DOC-002 | P2 | 规则、API 和测试计数建立机械校验 | 已完成 | doc-consistency-check.sh 8 项交叉验证；test-quality-scripts.sh 含故障恢复夹具 |
| DOC-003 | P2 | 收敛跨 Provider 时间精度与 offset 语义 | 已完成 | PG/MySQL 微秒级精度声明修正；STD-PROV-PG-001 从 Ticks 完全一致改为微秒级往返一致 |
| PERF-001 | P2 | 修复 Benchmark 和固定时间阈值 | 已完成 | Benchmark 修复 GlobalSetup+Shared Cache；产出查询 ~1.11× Raw ADO |
| AOT-001 | P1 | 扩展三 Provider 与 NuGet consumer AOT 矩阵 | 已完成（真库收口） | 三 Provider 原生运行全部通过：SQLite 本机 + PG/MySQL 本机 Docker（CI 同配置 postgres:17/mysql:8.4 容器）实测 PASSED（2026-07-18）；7 项真库集成用例 146/146 全绿；远端 CI 待 push 触发 |
| AUD-001 | P0 | 凭据安全事件 | 未处理 | 用户决定仅记录；G9 必须继续阻断 |
| API-001 | P3 | `PgNotificationListener.StopAsync()` 无 CancellationToken 参数 | 已记录·待 3.0 | G25 门禁豁免在案：2.0.1 已发布签名，追加可选参数属 binary-breaking（同 2.0.0 变更先例）；3.0 对齐 `IHostedService.StopAsync(CancellationToken)` 惯例时移除豁免 |
| SEC-001 | P2 | MigrationEmitter legacy BuildCreateTable 标识符未引用 | 已完成 | 审计 2026-07-17 SEC-01：legacy 重载（注册表 CreateTableSql 回退路径）表名/列名补 QuoteIdentifier（SQLite/PG 双引号风格），与方言重载对齐；5 个受影响断言同步；SourceGen 73/73；反向验证撤回修复 → CreateTableSql_IsGenerated 确定性失败 |
| ERR-001 | P3 | FormattableSqlFormatter 泛型 FormatException 缺索引值 | 已完成 | 审计 2026-07-17 ERR-01：异常消息补实际越界索引与 ArgumentCount；既有 Throws<FormatException> 断言不受影响 |
| ERR-002 | P3 | PgNotificationListener 无 OnError 订阅者时静默终止 | 已完成 | 审计 2026-07-17 ERR-02：新增可选 `Logger` 属性 + LoggerMessage 源生成兜底（CA1848 合规）；RaiseError 无订阅者时经 Logger 留痕；新用例 BackgroundFailure_WithoutOnErrorSubscriber_LogsTermination 反向验证（静默化 → 确定性超时失败）；Core 143/143 |
| DOC-004 | P3 | Core 依赖口径与 Logging 抽象失真（审计 ARCH-01） | 已完成 | 架构设计.md 依赖节 + README 最小依赖行明确"BCL + ADO.NET + 共享框架日志抽象（M.E.Logging.Abstractions，零第三方传递、AOT 安全）"，与 DataSession/DbOptions 实际引用一致 |

> **AUD-001 追加（2026-07-17 晚）**：7-17 的 Git 仓库重建（f0df771）把含真实凭据的旧版 `scripts/set-test-env.sh` 重新带入了全部历史提交——此前审计声称的"历史已重写清除"在重建仓库中不再成立。本轮已完成仓库侧再整改：脚本改为从未跟踪的 `.env.test` 加载且不回显值，新增 `scripts/.env.test.example` 占位模板，`.gitignore` 排除 `.env.test`，G9 转绿并经故障注入负向验证。**历史提交中的凭据仍在**（8 个提交全部含旧脚本），需要用户决策：历史重写（无 remote，影响面为本地 8 提交）+ 数据库侧凭据轮换（测试环境 PG/MySQL 账号）。

> **配置体系演进（2026-07-19）**：凭据卫生从单一 `.env.test` 升级为双层覆盖：仓库根 `appsettings.test.json`（git 跟踪的模板，含 `${VAR}` 占位符）+ `.env.test.example`（从 `scripts/` 迁到仓库根）+ `.env.test`（本地覆盖）。新增 `src/PalORM.Testing/TestEnvironment.cs` 作为统一读取器，AOT 安全（STJ 源生成）。旧的 `scripts/.env.test.example` 已删除。

## DEP-001 基线与反向验证

- S1：`OpenTelemetry.Api 1.11.0` 触发两个 NU1902；`SQLitePCLRaw.lib.e_sqlite3 2.1.10` 触发一个 NU1903，严格测试还原失败。
- S2：删除未使用的 OpenTelemetry 包后两个 NU1902 消失；`Microsoft.Data.Sqlite 10.0.10` 仍传递有漏洞的 SQLitePCLRaw，拒绝采用。稳定包不能依赖 preview，因此最终采用 `Microsoft.Data.Sqlite.Core 10.0.10 + SQLite3MC.PCLRaw.bundle 2.3.6`，漏洞扫描零发现，Core 59/59，独立 Native AOT 程序运行 SQLite 3.53.3。
- S3：恢复 OpenTelemetry 1.11.0 后两个 NU1902 重现；恢复 Microsoft.Data.Sqlite 9.0.0 后 NU1903 重现。随后重新应用稳定安全组合。

## 阶段 2 基线与反向验证

- S1：SourceGen 31/31；计算列迁移 DDL 缺少表达式，Converter 未生成对称的 `ToProvider` / `FromProvider`，无 Converter 的 Ulid 被静默映射为 TEXT。
- S2：统一实体身份和 INSERT predicate；迁移生成 `GENERATED ALWAYS AS (...) STORED` 并以 Roslyn 字面量编码嵌入；Converter Provider 类型驱动 DDL、写入、读取和主键 binder；PALORM015 拒绝无法构造或写入的实体；PALORM016 拒绝未知类型、nullable Provider、OwnedJson/Converter 冲突、不可访问 Converter 和无 DDL 契约的 Provider 类型；显式接口 Converter 通过接口契约调用；Upsert 显式返回模型列顺序；包消费者运行生成注册表。终审追加修复三方言 SQL/DDL、Provider 标识符引用、实体查询固定列序和 MySQL 非数值主键 Upsert。
- S3：撤掉计算列 DDL、`ToProvider`、`FromProvider`、主键生成 binder、精确类型白名单、Computed 字面量编码、Upsert 显式返回列、Converter 接口调用、NotMapped 分析过滤和特殊标量 DDL 映射后，对应用例均退化；移除打包 SourceGen 引用后 POCO 可构建但注册表运行检查退出 1。终审追加项逐项撤回后也确定性退化：实体查询回到 ordinal 0 错读、非数值主键重新进入 `SetIdDelegates`、保留字 DDL 在迁移时报语法错误、保留字 CRUD 在 Insert 时报语法错误；撤回 Roslyn 字面量编码后嵌入引号和反斜杠的 consumer 重现 CS1002、CS1519、CS1009。

## 阶段 3 基线与反向验证

- S1：新增批量用例后 2/2 失败：SoftDelete 实体被物理删除；SQLite BulkInsert 将 `[Id, name]` 参数绑定到猜测列 `[name, created_by]`，导致主键为空并覆盖数据库默认列。零列与 key-only 用例进一步复现伪空列、非法空 `UPDATE SET` 和非法空 Upsert。
- S2：BulkDelete 改用 `TableNames`、`PkColumns`、`BindDelete` 和 `EntityFeatures`，每 500 键一批并复用单一事务；三 Provider BulkInsert 统一使用 `CrudMetadata.InsertColumns` / `BindInsert` 并在执行前校验列数与参数数；空数组真实注册，零插入列和零更新列明确失败，key-only Save 使用幂等冲突分支。Core 60/60、SourceGen 51/51、无外部服务集成 118/118；三 Provider Native AOT publish 通过，SQLite 原生批量路径运行通过。
- S3：撤回实体特性判断后 SoftDelete 再次物理删除；撤回 InsertColumns 后重现 3 列/2 参数错配；撤回 key-only `DO NOTHING` 后重现 SQLite 空 SET 语法错误；恢复伪空列后重现 1 列/0 参数错配。四项均恢复正式实现。

## 阶段 4 基线与反向验证

- S1：Core 60/60、无外部服务集成 118/118。新增用例后，非法 alignment 与孤立右花括号未在执行前拒绝；DataSession 将 `{0:N1}` 原样发送给 SQLite 并报 `unrecognized token`；SQLite/MySQL 在执行、command dispose、rollback、transaction dispose 同时失败时返回清理异常而非原始执行异常。
- S2：新增统一 `FormattableSqlFormatter`，先由 BCL 验证复合格式语法，再单次扫描映射参数名；DataSession、QueryBuilder、QueryMultiple 共用入口，参数保持原始对象。三 Provider 的 probe/row/main command 释放使用主异常保留模式；SQLite/MySQL rollback 与 transaction dispose 失败附加到 `Exception.Data`。Core 71/71、SourceGen 51/51、无外部服务集成 119/119。
- S3：撤回 BCL 语法验证后 3/65 失败；撤回 DataSession 统一入口后 SQLite 重新报告 `unrecognized token`；撤回 SQLite 主 command 释放保护后仅 SQLite 故障矩阵退化；保持调用图不变并撤回 transaction dispose 保护后仅 SQLite 故障矩阵退化。四项均恢复正式实现。

## 阶段 5 基线与反向验证

- S1：阶段开始时 Core 71/71、无外部服务集成 119/119。GridReader 新增用例先得到 72/74，复现 Dispose 未等待读取和重复 Dispose 未共享异常；DataSession 新增用例得到 77/81，复现同会话重叠操作、事务 sibling flow、嵌套事务和 Dispose 竞态。
- S2：新增共享 `SessionOperationState`，直接 CRUD、QueryBuilder、Bulk、StoredProc 与 GridReader 共用单消费者门禁和事务状态；GridReader 持有 lease 到释放。`WithTransaction` 使用逻辑 owner 和 flow 完成信号，拒绝 sibling/nested，允许 callback 内顺序操作；Dispose 等待活动命令、GridReader 和事务 flow。事务发布、UseTransaction、执行时解析与 Dispose 状态切换在同一把锁内协调；Bulk 内部事务绑定 operation capability；缓存命中也进入 operation gate；StoredProc 加入当前事务；函数式事务在 commit/rollback 前自动收口遗留 GridReader；事务 rollback/resource/dispose 清理异常不覆盖 action/commit 主异常。Core 111/111、SourceGen 51/51、无外部服务集成 119/119。
- S3：撤回 operation fail-fast 后同会话、QueryBuilder、child flow 和 GridReader 路径退化；撤回事务 flow 等待后 Dispose 提前关闭连接；撤回 GridReader active-read 等待后释放提前完成；撤回 disposal task 共享后第二次 Dispose 丢失首次异常；撤回事务 cleanup 保护后 action 主异常被覆盖；撤回事务发布协调后事务建立与 Dispose 竞态返回失效事务；撤回缓存 gate 后已释放会话仍返回缓存；撤回事务 callback 在 Disposing 的续行许可后后续操作失败；撤回 Bulk operation-owned 事务许可后批量操作被 Dispose 中断；撤回 UseTransaction 状态门禁后已释放会话仍可改事务；撤回 StoredProc 事务解析后命令脱离当前事务；撤回 QueryBuilder 执行时事务解析后事务前创建的 builder 不加入后续事务；撤回 transaction flow 资源登记后遗留 GridReader 在 commit gate 失败。十三项均恢复正式实现。

## 未完成阻断

1. `AUD-001 / G9` 保持 P0 未完成；不修改 `scripts/set-test-env.sh`，不轮换或输出凭据。
2. PostgreSQL/MySQL Native AOT 服务容器运行没有远端执行证据，继续标记待 CI。
3. 当前检出没有 Git remote/upstream，GitHub CLI 未认证，不能触发远端 CI。
