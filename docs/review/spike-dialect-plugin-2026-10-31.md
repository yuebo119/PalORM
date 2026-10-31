# Spike：第四 Provider 与方言插件评估

- 日期：2026-10-31
- 结论：三方言保持封闭集；方言插件暂不实施
- 定位：设计评估，不产生代码变更

## 现状

新增一个方言需要同时改动：

| 层 | 改动点 |
|----|--------|
| Core | `SqlDialect` 枚举、`CommandSqlByDialect`、`SqlSets`、`DataSession_Bulk` 的方言分支、`BatchUpdateSqlBuilder` |
| SourceGen | `SqlGenerationDialect`、`CommandFactoryEmitter`、`MigrationEmitter`、`RegistryEmitter` 的方言门控 |
| Provider | `IDbProvider` 实现、批量写入策略、参数/数组能力、连接串调优 |
| 测试 | 三方言快照、契约测试、真库集成、AOT 矩阵 |

## 成本估算

| 路径 | 估算 | 说明 |
|------|------|------|
| 新增同构方言（如另一个 PostgreSQL 兼容库） | 3 至 5 天 | 主要是连接与参数适配，SQL 生成可复用 |
| 新增异构方言（如 Oracle/SQL Server） | 2 至 4 周 | DDL、分页、批量、类型映射、数组/参数上限、测试矩阵 |
| 方言描述符插件化 | 4 至 6 周 | 需要把枚举分支改为能力描述符，生成器与运行时同步改造 |

## 风险

1. 方言插件会把编译期生成的 SQL 变成运行时可配置的形态，削弱 AOT 与编译期校验优势。
2. 生成器需要为每个方言发射 SQL，插件化后生成物体积与快照测试面显著增加。
3. `IDbProvider` 的 static abstract 设计依赖编译期闭合，插件化需要改为实例化或源生成注册，影响热路径分发。

## 建议

保持三方言封闭集，并在 README/架构文档明确“非目标”。如果未来确有第四方言需求，优先按“同构方言适配”处理，不引入运行时插件；异构方言按独立分支评估，不进入主包。
