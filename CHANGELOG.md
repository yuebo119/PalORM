# PalORM 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 规范。

## [3.1.0] — 性能优化（IRowFactory 委托化 + Converter 单例 + 快照合并）

> 基于 v3.0.0 真实场景基准数据的深度优化，详见 `docs/v3.1-performance-plan.md`。

### 性能成果（vs v3.0.0）

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

### 文件变更
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

### 验证
- Core Tests: 156/156 全绿
- SourceGen Tests: 104/104 全绿（5 个快照基线重生成 + 评审通过）
- SQLite Integration Tests: 149/149 全绿（7 个 PG/MySQL 环境变量失败与本次无关）
- 技术债扫描: 12/12 全通过
- 基准对比: QueryAll 177%→132%、GetByKey 232%→141%

## [3.0.0] — Breaking Changes（架构精炼 + Breaking API 移除 + 质量增值）

### Breaking Changes
- **移除 DataSession.ForRead() / ForWrite()**：请使用 `From<T>().ForRead()` / `From<T>().ForWrite()`
- **移除 CrudMetadata 旧 9 参 ctor**：请使用聚合 ctor（CrudBindings + CrudColumns）
- **移除 QueryBuilder.ThenInclude&lt;TGrandChild&gt;(单参)**：请使用双参 `ThenInclude<TGrandChild, TParent>(grandChildKey, parentKey)`

### 架构精炼
- **删除 8 个 Obsolete 公共 API**：MinimumLogLevel / ParameterPrefix / CreateConnection 单参 / GetLimitOffsetClause / LogQuery / RecordQueryStart / RecordQueryDuration / QueryBuilder 14 参 ctor
- **合并 TypeMapperEmitter → RowFactoryEmitter**：DateTimeOffset 读取直接内联 `GetFieldValue<T>`
- **PalORM_Runtime 拆 3 文件**：EntityFeatures.cs / SqlSets.cs / CrudMetadata.cs
- **Resilience 拆 CircuitBreaker + Exceptions**：熔断状态机独立为 `internal sealed class CircuitBreaker`
- **DataSession God Object 拆 4 partial**：Crud.cs / Query.cs / Transactions.cs / Schema.cs（1597→6 文件）
- **PgNotificationListener partial 拆分**：NpgsqlNotificationConnection.cs + PgNotificationEventArgs.cs
- **抽取 BulkOperationFramework**：消除 MultiValueBulkInsert/PostgreSqlProvider 间的 probe+cleanup 重复
- **ColumnModel 瘦身**：删除 4 个恒 null 预留字段（Length/Precision/Scale/DefaultExpression）
- **EquatableArray 独立文件**：从 TableModel.cs 提取

### 质量增值
- **集成 SonarAnalyzer.CSharp 10.29.0**：CI 守护层——P0 安全 + P1 设计规则全部为 error
- **BulkOperationFramework**：三 Provider 共享的 probe + cleanup 骨架
- **测试配置双层覆盖**：appsettings.test.json + .env.test + TestEnvironment 读取器
- **测试 helper 集中化**：TestInterceptors.cs（CountingTestInterceptor + CallbackTestInterceptor + OrderedInterceptor）
- **测试方法拆行**：AdvancedTests + QueryTests + FinalTests + MultiEntityTests 单行→多行
- **PALORM006/007 占位诊断删除**：零报告描述符移除
- **P1-2 规则升级为 error**：S3776/S107/S927/S2681/S125/S1066/S1994/S2189

### AI 系统
- **`.ai/lessons.md` v6.0**：14 个缺陷 + SOP + 决策矩阵 + 技术债扫描 SOP（自包含手册）
- **PR 模板**：编译/测试/Sonar/三方一致/精炼守护/反模式预防 6 类清单
- **`docs/编码规范.md` 第 18 节**：SonarAnalyzer 守护层规则配置

## [2.0.1] — 2026-07-15
- 初始发布
- Core + 3 Provider（SQLite/PostgreSQL/MySQL）+ SourceGen + Testing
- 面向严格 Native AOT（IsAotCompatible + IsTrimmable）
- 源生成器：RowFactory / CommandFactory / Migration / Registry / SqlFile / SqlTemplate
- 编译期诊断：PALORM001-022
- 三方言支持：SQLite / PostgreSQL / MySQL
- 弹性执行器：重试 + 退避 + 超时 + 熔断
- 批量操作：MultiValue INSERT / PG Binary COPY
- 查询 DSL：Where/OrderBy/Take/Skip/Include/Join/CTE/Window/Cache
- 软删除 + 多租户 + 乐观锁
- PG NOTIFY/LISTEN
