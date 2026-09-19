# ADR-L：多租户查询缓存的结构性隔离

- 状态：已采纳（2026-09-19，取代 ADR-C 中"隔离责任完全在调用方 key 约定"的裁决）
- 决策人：仓库所有者（审计 2026-09-19 开放问题论证后拍板）
- 背景：审计 S3（多租户缓存默认不安全）+ 论证中发现的手动 key 前缀方案自身的 IgnoreFilters 盲区

## 语境

`WithCache(cacheKey)` 的缓存键由调用方裸提供；未注入 `DbOptions.QueryCache` 时各会话共享
进程级默认实例（`CacheStore.Default`，容量 1024）。多租户应用四条件与（多租户 ∧ WithCache ∧
key 无租户维度 ∧ 未注入 per-tenant 缓存）全中时，租户 A 的查询结果静默供给租户 B——无异常
无日志。ADR-C 原裁决"隔离责任在调用方 key 约定"，配套指引是手动前缀
（`$"products:{tenantId}"`）。

**推翻原裁决的两个证据**：

1. 默认值站在不安全侧。库自身的原则是"危险默认必须响亮"（已释放事务显式抛
   `QueryBuilder.GetActiveTransaction`，而非静默回退无事务执行）——缓存隔离靠人读文档，
   与该原则不同构。
2. 手动前缀方案自身有洞：`IgnoreFilters()` 的全量查询（运维/报表读到全部租户数据）与租户
   过滤查询若 key 相撞，照样串——原指引没有覆盖这个分支。

## 决策

实际缓存 key 由框架自动前缀化，**与租户过滤注入同点冻结**（`From<T>()` 内，DefaultFilter
子句上链的同一时刻快照 `_tenantId` 与 `_ignoreFilters`）：

| 会话/实体状态 | 作用域前缀 | 说明 |
|---|---|---|
| 多租户会话 + TenantAware 实体 + 未 IgnoreFilters | `__t:{tenantId}:` | 每租户独立命名空间 |
| 多租户会话 + IgnoreFilters（或非 TenantAware 实体） | `__all__:` | 全量数据独立命名空间，与租户命名空间互不可见 |
| 单租户会话（未 `WithTenant`） | 无前缀 | key 原样，行为零变化 |

同点冻结的意义：查询的租户可见性在 `From` 时定型（DefaultFilter 已上链、后续变更受
`SessionOperationState` 门禁约束），key 作用域同拍快照则二者**永不漂移**——不存在
"过滤按新租户、缓存按旧租户"的窗口。

调用方仍按传入的 userKey 推理缓存；前缀是框架内部命名空间（缓存诊断工具中可见）。

## 后果

- 正面：跨租户命中结构性不可能；IgnoreFilters 盲区一并关闭；单租户零影响（S3 测试
  `TenantScope_SingleTenant_KeyRemainsUnprefixed` 锁定）。
- 负面：进程级默认实例的 1024 容量被多租户摊薄（每租户可视为独立逻辑缓存）——需要按租户
  控制容量/TTL 的应用仍应注入 per-tenant `QueryCache`（推荐路径不变）；key 与传入值不完全
  一致（诊断时需知前缀存在，已在 WithCache doc 说明）。
- 兼容性：单租户应用行为不变；多租户应用中**原本依赖错误共享**的行为（跨租户命中）消失——
  这正是要消灭的缺陷，不视为破坏。
- 测试：`QueryCacheInjectionTests` 三用例（共享缓存同 key 互不可见 / IgnoreFilters 走
  __all__ / 单租户原样），S3 反向验证（撤前缀化 → 串租用例确定性回红）已闭环。

## 与其它文档的关系

- ADR-C 的"隔离责任在调用方 key 约定"由本 ADR 取代；ADR-C 其余内容（CacheStore 全局状态
  的容量治理）不受影响。
- `docs/静态缓存清单.md` 的 BoundedQueryCache 条目已注明 key 经本 ADR 前缀化。
