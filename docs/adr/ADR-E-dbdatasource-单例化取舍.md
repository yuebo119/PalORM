# ADR-E：DbDataSource 单例化取舍

> 状态：**待用户决策**（2026-07-25 起草）· 来源：v5.0-roadmap 阶段 4.1

## 背景

v5.0-roadmap 阶段 4.1 提议把当前 `new NpgsqlConnection(cs)` / `new MySqlConnection(cs)` 路径改造为 `DbDataSource` 单例模式：
- `IDbProvider` 新增 `static virtual DbDataSource? CreateDataSource(DbOptions options) => null;`
- PG 返回 `NpgsqlSlimDataSourceBuilder(cs).Build()`（AOT 友好）
- MySQL 返回 `MySqlDataSourceBuilder(cs).Build()`
- SQLite 返回 `null`（fallback 到 `CreateConnection`）
- `DataSession.CreateAsync` 优先 `DataSource.OpenConnectionAsync`，fallback `CreateConnection`
- DataSource 是**应用级单例**（DataSession.DisposeAsync 不释放 DataSource）

## 现状（v5.0 阶段 3.1/3.2 实施后）

阶段 3.1/3.2 已通过 ConnectionStringBuilder 默认值检测覆盖，把核心调优参数写进连接串：
- PG：MaxAutoPrepare=100、AutoPrepareMinUsages=2、NoResetOnClose=true、ReadBufferSize/WriteBufferSize=16384、Enlist=false
- MySQL：AutoEnlist=false、ConnectionReset=false、CancellationTimeout=5、AllowLoadLocalInfile=true、ServerRedirectionMode=Preferred

`new NpgsqlConnection(cs)` 在 Npgsql 9+ 走连接池（默认 Pooling=true），与 NpgsqlDataSource 共享同一连接池基础设施；`MaxAutoPrepare` 跨连接复用 prepared statement 的能力已在连接串层支持，不依赖 DataSource。

## 选项

| 选项 | 机制 | 优点 | 缺点 |
|------|------|------|------|
| **E1 维持现状**（不做 4.1） | 继续用 `new NpgsqlConnection(cs)` + 连接串层池化 | 零架构改动；无静态可变状态；AOT 已兼容（IsAotCompatible=true 编译通过） | 失去 NpgsqlSlimDataSourceBuilder 的额外 AOT 收益（边际）；失去 DataSource 统一管理面 |
| **E2 完整实施 4.1**（roadmap 方案） | IDbProvider 加 CreateDataSource；Provider 静态字段缓存 DataSource；DataSession 优先走 DataSource | 统一连接管理面；NpgsqlSlimDataSourceBuilder 最小化类型加载（理论 AOT 优化）；MySqlDataSource 为未来特性（如 ServerRedirection 长连接）铺路 | **生命周期复杂度**（DataSession using-scoped vs DataSource 应用级单例）；**静态可变状态**（违反项目"零全局可变状态"原则，需锁保护首次创建）；**API 行为变化**（DataSession.CreateAsync 语义变化）；**多连接串场景需 Dictionary 缓存**（每个唯一 cs 一个 DataSource，内存占用上升） |
| **E3 仅文档化 DataSource 路径，不强制** | 维持 CreateConnection 为默认；IDbProvider 加可选 CreateDataSource 钩子，用户按需启用 | 兼容现状；为未来留扩展点；零强制风险 | API 面扩大；两套路径并存增加维护成本 |

## 关键技术判断

### 1. NpgsqlSlimDataSourceBuilder 的 AOT 收益是否实质

[事实] Npgsql 9+ 全面支持 Native AOT，`new NpgsqlConnection(cs)` 路径已经 AOT 兼容。
[事实] PalORM 当前 `IsAotCompatible=true` 编译 0 警告（v5.0 验证）。
[推断] NpgsqlSlimDataSourceBuilder 的 AOT 优势主要针对**新项目首次配置**（避免反射发现类型映射）；对已 AOT 兼容的现有项目，迁移收益是边际的。
[未验证] Npgsql 10 在 `new NpgsqlConnection(cs)` vs `NpgsqlSlimDataSourceBuilder` 路径上的 native AOT 发布 binary size / trim 警告差异，需 BenchmarkDotNet + PublishAot 实测对比。

### 2. DataSource 单例的生命周期管理

[事实] DataSession 是 using-scoped（用完即弃），CreateAsync 打开连接、DisposeAsync 释放连接。
[事实] NpgsqlDataSource / MySqlDataSource 实现 IDisposable，应用关闭时需 Dispose。
[推断] 若 Provider 用静态字段缓存 DataSource：
- 谁负责 Dispose？静态字段无明确所有者
- 应用关闭时 DataSource 的 Dispose 时机不可控（进程退出时 finalizer 顺序不定）
- 多个 DataSession 共享同一 DataSource，错误隔离边界模糊

### 3. 静态可变状态违反项目原则

[事实] PalORM.DbOptions 注释明确："record 类型+init-only 属性"是"零全局可变状态"原则的体现。
[事实] PalORM_Runtime 用 `Volatile.Read + Lock + 不可变快照交换` 模式管理静态注册表，避免简单静态可变字段。
[推断] Provider 静态字段缓存 DataSource 需复刻 PalORM_Runtime 的模式（锁保护首次创建），增加代码复杂度。否则引入竞态（多线程首次 CreateAsync 同时初始化 DataSource）。

### 4. 当前架构已覆盖的核心收益

[事实] 阶段 3.1 连接串层 MaxAutoPrepare=100 已实现"跨连接 prepared statement 复用"——这是 NpgsqlDataSource 的核心收益之一。
[事实] 阶段 3.1 NoResetOnClose=true 已实现"归还池跳过 DISCARD ALL"——性能优化。
[推断] 阶段 3.1/3.2 完成后，DataSource 的剩余收益主要是"API 统一面"和"未来特性铺路"，而非实质性能提升。

## 推荐

**E1（维持现状）**：v5.0 阶段 3.1/3.2 已通过连接串层调优拿到 DataSource 的核心收益（连接池、MaxAutoPrepare、NoResetOnClose）。剩余收益（SlimBuilder AOT 边际优化、API 统一面）不足以抵消引入的风险（生命周期复杂度、静态可变状态、API 行为变化）。

如果未来确实需要 DataSource 路径（如 Npgsql 11 强制要求、或新特性只在 DataSource 上提供），再走 E3（可选钩子）而非 E2（强制改造）。

## 待用户决策

1. **采纳 E1（不做）还是 E2（完整实施）还是 E3（可选钩子）**？
2. 若 E2：DataSource 生命周期管理方案——静态字段+锁（项目原则妥协）还是依赖注入容器（引入 DI 依赖）？
3. 若 E1：是否需要后续 BenchmarkDotNet 实测对比 `new NpgsqlConnection` vs `NpgsqlSlimDataSourceBuilder` 的 AOT binary size 差异，作为未来 revisit 的依据？

## 参考

- Npgsql 10 DataSource 文档：https://www.npgsql.org/doc/api/Npgsql.NpgsqlDataSource.html
- NpgsqlSlimDataSourceBuilder 源码：https://github.com/npgsql/npgsql/blob/main/src/Npgsql/NpgsqlSlimDataSourceBuilder.cs
- MySqlDataSourceBuilder 文档：https://mysqlconnector.net/api/mysqlconnector.mysqldatasourcebuilder/
- v5.0-roadmap 阶段 4.1：`docs/v5.0-roadmap.md` 第 104-124 行
- v5.0 阶段 3.1/3.2 实施：`src/PalORM.PostgreSql/PostgreSqlProvider.cs:18-58`、`src/PalORM.MySql/MySqlProvider.cs:18-59`
