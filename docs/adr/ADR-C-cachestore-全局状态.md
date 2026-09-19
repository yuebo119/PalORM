# ADR-C：CacheStore 进程级静态缓存与"零全局可变状态"冲突

> **2026-09-19 修订（ADR-L）**：本 ADR 推荐段"`WithTenant` 场景文档要求 key 含租户段或注入
> 独立实例"的<b>调用方 key 约定</b>部分已由 [ADR-L](ADR-L-多租户缓存结构性隔离.md) 取代——
> 实际缓存 key 现由框架按租户作用域自动前缀化（`__t:{tenantId}:` / `__all__:`），跨租户命中
> 结构性不可能；per-tenant 实例注入仍是容量/TTL 控制的推荐路径。本 ADR 其余内容
> （IQueryCache 抽象、容量治理）不受影响。

> 状态：已实施（2026-07-17 批准并实施）· 来源：评审 ITM-115/152（ARCH-07/CONC-03）

## 背景

`CacheStore` 是 `public static` 的进程级 `ConcurrentDictionary`，与架构设计"明确不做"清单中的"全局可变状态｜测试间污染"直接冲突。`WithCache(key)` 写入的缓存跨会话、跨 Provider、跨租户共享，仅靠调用方 key 约定隔离；无容量上限与淘汰机制。

## 现状（2026-07-17 已实施的缓解）

- 存/取双向快照副本：缓存内实例不再与调用方共享（ITM-102，P0 已修）；
- 过期条目读取时 `TryRemove`（消除同 key 过期驻留）；
- 仍未解决：跨租户共享 key 空间、无容量上限（不同 key 无界增长）、`Clear()` 全局副作用、测试并行污染。

## 选项

| 选项 | 机制 | 优点 | 缺点 |
|------|------|------|------|
| C1 会话级缓存 | 缓存字典挂在 DataSession 实例 | 生命周期清晰、租户天然隔离、测试零污染 | 会话按设计短命（using-scoped），缓存命中率归零——等于删除功能 |
| C2 DbOptions 注入缓存实例 | `DbOptions.QueryCache: IQueryCache`，默认进程级单例，可注入独立实例 | 保留跨会话命中；测试/多租户可注入隔离实例；容量策略可配置 | binary-breaking 视签名设计而定；需定义 IQueryCache 抽象 |
| C3 维持静态 + 上限 | 加 maxEntries + LRU/随机淘汰 | 改动最小 | 全局共享与测试污染依旧；与"不做"清单持续冲突 |
| C4 删除 WithCache | 交给应用层（HybridCache/IMemoryCache） | 彻底消除冲突；BCL 已有成熟方案 | binary-breaking（3.0）；损失一行式易用性 |

## 推荐

**C2**：`IQueryCache` 抽象 + 默认实现带容量上限（如 1024 条目 + 过期扫描），`WithTenant` 场景文档要求 key 含租户段或注入独立实例。3.0 可再评估是否降级为 HybridCache 适配层（C4 方向）。架构设计"不做"清单同步改写为"默认进程级缓存实例，可注入隔离"。

## 待用户决策

- 采纳 C2 还是 C3（最小改动）/C4（删除）？
- 若 C2：默认容量上限取多少；`Clear()` 是否保留为静态入口？


## 实施记录（2026-07-17 · 已实施）

按用户批准的推荐方案落地（**编号勘误（独立审计 DOC6）：本段原写 "C3 IQueryCache 抽象注入"——
选项表中该设计是 C2（C3 是"维持静态+上限"）。实际采纳内容即上表 **C2**，此处按 C2 理解）：**C2 IQueryCache 抽象注入 + 默认容量上限**

- 新增 `IQueryCache` 接口（TryGet/Set/Clear）与 `BoundedQueryCache` 默认实现：TTL + 容量上限（默认 1024 条）；容量满时先剔除过期条目、仍满则拒绝新写入（缓存未命中正确性中性，拒绝优于无界增长或 LRU 锁开销）。
- `DbOptions.QueryCache` 注入点：未设置时回退进程级共享默认实例（`CacheStore.Default`），注入独立实例即得会话/租户级隔离。
- `CacheStore` 降为兼容外观（委托默认实例），既有 `CacheStore.Clear()` 调用方不破坏。
- 测试：`QueryCacheInjectionTests` 5 用例——注入实例生效且默认实例不受污染、双会话独立缓存同 key 隔离、容量满拒绝写入、过期剔除后接受、已有 key 更新绕过容量检查。
