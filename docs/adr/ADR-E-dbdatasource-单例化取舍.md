# ADR-E：DbDataSource 单例化取舍

> 状态：**已批准 E1（不做）**（2026-07-25 用户裁决）· 来源：v5.0-roadmap 阶段 4.1

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

## 主流 ORM 实践调研（2026-07-25 补充）

### 各 ORM 是否强制 DataSource 单例

| ORM | 是否强制 | 模式 | 来源 |
|-----|:---:|------|------|
| **EF Core** | ✓ 强制 | DI 容器注册（NpgsqlDataSource 9.0 前为 Singleton，9.0 改 Scoped） | [npgsql/efcore.pg#3086](https://github.com/npgsql/efcore.pg/issues/3086) |
| **Dapper** | ✗ 不强制 | 推荐用 `DbConnection` + `using`（连接串驱动），另提供基于 DataSource 的扩展方法 | Dapper 官方仓库 |
| **Linq2Db** | ✗ 不强制 | `DataOptions` 同时支持连接串和 DataSource，每个 DataContext 独立 | [linq2db GitHub](https://github.com/linq2db/linq2db) |
| **RepoDB** | ✗ 不用 | 静态 `DbConnection` 创建方法，无 DataSource 概念 | RepoDB 文档 |
| **PetaPoco** | ✗ 不用 | 连接串直接传构造函数 | PetaPoco 文档 |

**结论**：只有 EF Core 强制用——因为它重度依赖 DI 容器（AddDbContext + DI 注册），DataSource 在 DI 里顺理成章。**纯微型 ORM 都不强求**，连接串模式仍是主流。PalORM 是无 DI 设计（`DbOptions.cs:3` 注释明确"为什么不是 DI"），属微型 ORM 阵营。

### EF Core 的 Singleton → Scoped 教训（重要反面证据）

[npgsql/efcore.pg#3086](https://github.com/npgsql/efcore.pg/issues/3086) 揭示了 DataSource Singleton 在 EF Core 里的真实痛点：
1. **多租户场景失效**：单例让 EF Core 无法判断"是否需要 new service provider"
2. **测试场景污染**：测试需同一容器配合多 DataSource，Singleton 阻碍
3. **触发"many service providers"警告**

**EF Core 9.0 修复**：把 NpgsqlDataSource 从 Singleton 改 Scoped。

**对 PalORM 的警示**：如果引入 DataSource Singleton，等于把 EF Core 8.0 之前的痛点重新踩一遍，且没有 DI 容器来缓冲。

### Npgsql 官方定位（事实）

来自 [Npgsql 文档](https://www.npgsql.org/doc/basic-usage.html)：
> "Direct instantiation of connection is **still supported**, but is **discouraged**... when using Npgsql 7.0"

官方"不推荐"**不是"强制"**。关键事实：
- DataSource 实质 = "连接池的对象化封装"（官方原文"usually correspond to a connection pool inside Npgsql"）
- **多 NpgsqlConnection 同连接串 ≠ 多池**：Npgsql 内部按连接串哈希共享池
- `new NpgsqlConnection(cs)` 路径在 Npgsql 10 仍完全支持，无 deprecation 标记

### DataSource 真正必须的场景 vs PalORM 覆盖度

| 场景 | 是否必须 DataSource | PalORM 现状 | 是否需要做 |
|------|:---:|------|:---:|
| Azure PG 托管 + 密码自动轮换 | ✓ 必须 | ✗ 未覆盖（用户连静态密码） | 推迟（用户无需求） |
| 多租户动态切换 DataSource | ✓ 必须 | ✗ 未覆盖（一进程一 Provider） | 推迟（设计哲学冲突） |
| 复杂类型映射/自定义类型解析器 | ✓ 必须 | ✗ 未覆盖（用源生成器） | 推迟（架构不同） |
| Native AOT + 极致裁剪 | SlimBuilder 更优 | ✓ 已 AOT 兼容 | 已覆盖 |
| 简单单库 CRUD（PalORM 主战场） | ✗ 不必须 | ✓ 已覆盖 | 已覆盖 |

### 优缺点对比

**优点（如果做 4.1）**：
- 与 Npgsql 7+ 官方推荐对齐（弱——"discouraged"非"deprecated"）
- 为密码轮换/多租户铺路（中——PalORM 当前无此需求）
- NpgsqlSlimDataSourceBuilder AOT 优化（弱——PalORM 已 AOT 兼容）
- DataSource 直接执行命令省 Open/Close（弱——DataSession 已封装）

**缺点（风险）**：
- **生命周期复杂**：DataSession using-scoped vs DataSource 单例所有者不明（高，EF Core #3086 已踩坑）
- **违反项目"零全局可变状态"原则**（高，DbOptions.cs:3 注释）
- **引入 DI 依赖**才能管理生命周期（高，PalORM 设计哲学冲突）
- **多租户场景 Singleton 阻碍**（高，EF Core 9.0 改 Scoped 即为此）
- **多连接串场景内存膨胀**（中）
- **API 行为变化**：CreateAsync 语义变化（中）

### 综合判断

DbDataSource 单例化对 PalORM 是"伪优化"：
1. **只有 EF Core 强制用**——因为它重度依赖 DI
2. **其他微型 ORM 都不强求**——连接串模式是微型 ORM 主流
3. **EF Core 自己从 Singleton 改 Scoped**——证明 Singleton 模式有真实痛点
4. **PalORM 无 DI 设计**——引入 Singleton 等于把 EF Core 8.0 的坑重新踩一遍
5. **PalORM 核心场景不需要 DataSource 独有能力**——密码轮换/多租户/复杂类型映射都不是 PalORM 目标场景

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

## 决策记录（2026-07-25 · 已批准）

按用户批准的 **E1（维持现状，不做）** 落地：

- **决策依据**：5 条主流 ORM 实践调研 + EF Core #3086 反面证据 + Npgsql 官方 "discouraged 但非 deprecated" 事实定位
- **当前覆盖度**：v5.0 阶段 3.1/3.2 已通过连接串层调优拿到 DataSource 的核心收益（MaxAutoPrepare / NoResetOnClose / 池化）；PalORM 已 AOT 兼容
- **遗留场景**：Azure PG 密码轮换 / 多租户动态切换 / 复杂类型映射 → 当前非 PalORM 目标，未来如需要走 E3（可选钩子）而非 E2（强制改造）
- **触发 revisit 的条件**：Npgsql 11 强制要求 DataSource / 用户提出明确的密码轮换或多租户需求 / EF Core #3086 类似痛点在 PalORM 实际出现
