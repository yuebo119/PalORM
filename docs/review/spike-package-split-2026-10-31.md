# Spike：可选包拆分评估（PalORM.Caching / PalORM.Observability）

- 日期：2026-10-31
- 结论：7.0 再评估；当前保持单包 Core
- 定位：7.0 版本规划输入，不产生代码变更

## 候选拆分

| 包 | 内容 | 依赖 | 拆分收益 | 拆分成本 |
|----|------|------|----------|----------|
| `PalORM.Caching` | `IQueryCache`、`BoundedQueryCache`、`CacheStore`、查询缓存接线 | BCL | 不需要缓存的用户少一个类型面 | 公共 API 迁移、`DbOptions.QueryCache` 类型引用、版本兼容 |
| `PalORM.Observability` | `PalORMMetrics`、`AuditInterceptor`、`QueryObservation`、Activity/Meter | `Microsoft.Extensions.Logging.Abstractions` | 不观测的用户少一个依赖面 | 拦截器接口与 `DbOptions` 接线、AOT 与 trimming 验证 |

## 评估

1. 当前 Core 只依赖 BCL 与 `Microsoft.Extensions.Logging.Abstractions`，包体与依赖面已经很小；拆分的边际收益有限。
2. `DbOptions` 直接引用 `IQueryCache` 与 `ILoggerFactory`，拆包会把这些类型移到新包，`DbOptions` 需要跨包引用，形成新的包间依赖。
3. `AuditInterceptor` 与 `IQueryInterceptor` 位于 Core，拆分需要把接口留在 Core、实现移到 Observability，公共 API 面变化较大。
4. 拆分后需要独立版本号、独立发布流水线、独立 AOT 验证，维护成本上升。

## 建议

7.0 之前保持单包。若收到“不想引入缓存/观测代码”的实际诉求，先评估 trimming 是否能裁掉未使用类型；确认无法满足后再拆包，并按 SemVer MAJOR 处理公共 API 迁移。
