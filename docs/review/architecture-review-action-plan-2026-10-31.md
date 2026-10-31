# PalORM 架构评审整改方案与任务清单

> 来源：2026-10-31 全量代码评审（生产代码 86 个 .cs 逐文件通读，测试基建与架构不变量测试抽样通读）
> 目标：把评审发现转成可执行、可验收、可回滚的任务，优先修正确性缺口，再补健壮性与维护性
> 约束：本项目遵循 SemVer 2.0.0；破坏性变更进 MAJOR；不得绕过 pre-commit 与 CI 门禁；推送需用户明确授权

---

## 0. 执行原则

1. 先修正确性，再补健壮性，最后做结构性优化。P1 不跨版本拖延。
2. 每个任务必须有锁定测试：修复前红，修复后绿。无测试的修复不算完成。
3. 公共 API 变更按 SemVer 分级：PATCH 修缺陷，MINOR 加能力，MAJOR 才允许删除或改签名。
4. 性能相关改动必须用 PerfHub/BDD 或 BDN 复测，遵守仓库的 5% 线与性能账本。
5. 写入共享 checkout 的任务串行执行；需要并行时用独立 worktree。
6. 本地提交按 `类型：描述`；推送、发版、合并 PR 需用户明确授权。
7. 文档与代码同提交；旧事实值 grep 零残留才算完成。

---

## 1. 阶段总览

| 阶段 | 时间盒 | 目标 | 出口条件 |
|------|--------|------|----------|
| Phase 0 | 1 至 2 天 | 修 P1 正确性缺口 | T0.1 至 T0.5 全部通过，CI 绿 |
| Phase 1 | 1 周 | 补健壮性与测试隔离 | T1.1 至 T1.5 通过，回归测试全绿 |
| Phase 2 | 2 至 4 周 | 降长期维护成本 | T2.1 至 T2.7 通过，分析器与文档门禁绿 |
| Phase 3 | 按需 | 兼容性与结构性演进 | 每个 spike 有结论，不默认合并 |

---

## 2. 决策门

以下四项需要用户或产品裁决，裁决前不进入实现。

### D1 拦截器覆盖面

| 选项 | 内容 | 成本 | 风险 | 建议 |
|------|------|------|------|------|
| A | 扩展 `IQueryInterceptor` 到 Insert/Delete/Save/Bulk | 中 | 写路径热路径增加通知成本 | 不立即做 |
| B | 新增 `ICommandInterceptor`，在 ADO.NET 命令层统一拦截 | 高 | 新抽象、性能与语义设计复杂 | 7.0 评估 |
| C | 保持现状，改名或文档化边界 | 低 | 使用者预期落差 | 现在做 |

建议：先做 C。把 `IQueryInterceptor` 文档改为“读查询与部分写入口拦截器”，在 README 增加覆盖矩阵；同时把 B 列为 7.0 候选。

### D2 迁移原子性

| 选项 | 内容 | 成本 | 风险 | 建议 |
|------|------|------|------|------|
| A | PG/SQLite 用事务 DDL，MySQL 返回结果对象 | 中 | DDL 事务持锁时间变长 | 推荐 |
| B | 只加 `MigrationResult`，不改事务 | 低 | 半成品 schema 仍存在 | 最低要求 |
| C | 只文档化 | 极低 | 运维风险不降 | 不推荐 |

建议：A。新增 `MigrateWithResultAsync`，保留 `MigrateAsync` 签名；PG/SQLite 在事务内执行建表与索引，MySQL 逐条执行并记录失败。

### D3 .NET 目标框架

| 选项 | 内容 | 成本 | 风险 | 建议 |
|------|------|------|------|------|
| A | 保持 net11.0 单目标 | 低 | 采用面受限 | 当前 |
| B | 增加 net10.0 多目标 | 中 | 条件编译、双份 CI | Phase 3 spike |
| C | 增加 net8.0 LTS 多目标 | 高 | `Lock`、`runtime-async`、包引用都要条件化 | Phase 3 评估 |

建议：A 保持，Phase 3 做 B 的 spike。README 补“为什么只支持 .NET 11”。

### D4 公共 API 收敛

| 选项 | 内容 | 成本 | 风险 | 建议 |
|------|------|------|------|------|
| A | 立即把 Provider 内部类型改 internal | 低 | 外部使用者破坏 | 不推荐 |
| B | 先加 `[Obsolete]` 与 `EditorBrowsable(Never)`，7.0 改 internal | 低 | 过渡期 API 面仍大 | 推荐 |
| C | 保持 public | 零 | 公共契约负担 | 不推荐 |

建议：B。

---

## 3. Phase 0 任务清单

### T0.1 方言格式化遗漏

| 项 | 内容 |
|----|------|
| 优先级 | P1 |
| 文件 | `src/PalORM.Core/SessionBatch.cs`、`src/PalORM.Core/QueryBuilderExtensions.cs`、`src/PalORM.Core/QueryBuilder.cs` |
| 改动 | `SessionBatch.Append` 传 `TProvider.Dialect`；`QueryMultipleAsync` 传 `builder._dialect`；删除 `FormatFormattableSql` 的默认方言参数，强制调用方显式传入 |
| 测试 | 新增 `FormattableSqlFormatter` 的 MySQL `#` 注释与反斜杠转义用例；新增 `SessionBatch` 与 `QueryMultipleAsync` 的方言参数编译级约束（删除默认参数后编译即验证） |
| 验收 | `git grep "FormatFormattableSql(sql, 0)"` 只返回带方言的调用；相关测试通过 |
| 验证命令 | `dotnet build PalORM.ci.slnf -c Debug`；`dotnet run --project test/PalORM.Core.Tests -c Release` |
| 依赖 | 无 |
| 预估 | 0.5 天 |
| 风险 | 删除默认参数是内部 API 变更，需同步所有调用点；调用点少，风险低 |

### T0.2 Scaffold 主键生成

| 项 | 内容 |
|----|------|
| 优先级 | P1（工具） |
| 文件 | `tools/PalORM.Scaffold/EntityGenerator.cs` |
| 改动 | 拆分 `[Key]` 与 `[Column]` 的 `if`；非自增主键生成 `[Key(AutoIncrement = false)]`；主键列名与属性名不同时同时生成 `[Column]` |
| 测试 | 新增 golden 用例：string 主键、Guid 主键、应用赋值 long 主键、snake_case 主键；生成代码用临时项目编译一次 |
| 验收 | 四类主键生成代码可编译；`[Key(AutoIncrement = false)]` 与 `[Column]` 同时存在 |
| 验证命令 | 生成到临时目录后 `dotnet build` 临时项目；Scaffold 自身的格式检查 |
| 依赖 | 无 |
| 预估 | 0.5 天 |
| 风险 | 临时项目编译较重；可先做字符串断言，编译冒烟放 CI 脚本 |

### T0.3 `Raw` 会话标脏

| 项 | 内容 |
|----|------|
| 优先级 | P1 |
| 文件 | `src/PalORM.Core/QueryBuilder.cs`、`src/PalORM.Core/QueryBuilderServices`、`src/PalORM.Core/DataSession.Crud.cs`、`src/PalORM.Core/DataSession.cs` |
| 改动 | 新增会话级回调 `MarkRawSqlStateUnknown`，由 `Raw` 调用，同时设置 `_connectionStateMayBeDirty` 与 Provider 钩子；`Set` 保持现有 Provider 钩子行为 |
| 测试 | 新增内部测试钩子或自定义 Provider，断言 `Raw` 后会话脏标记为 1；池化租出路径执行复位 |
| 验收 | 池化 PG/MySQL 会话执行 `Raw` 后，下一租期触发 `ResetConnectionStateAsync` |
| 验证命令 | Core 单测；PG/MySQL 集成测试可选 |
| 依赖 | 无 |
| 预估 | 0.5 天 |
| 风险 | `Raw` 可能被用于只读片段，标脏会带来一次复位成本；保守方向可接受 |

### T0.4 拦截器覆盖决策与文档

| 项 | 内容 |
|----|------|
| 优先级 | P1（决策） |
| 文件 | `docs/adr/` 新增 ADR；`src/PalORM.Core/IQueryInterceptor.cs`；`README.md` |
| 改动 | 按 D1 裁决；推荐 C，改名或文档化边界，增加覆盖矩阵 |
| 测试 | 文档门禁；无代码测试 |
| 验收 | README 与 XML 文档明确列出覆盖与不覆盖的入口 |
| 验证命令 | `dotnet run --file scripts/doc-consistency-check.cs` |
| 依赖 | D1 |
| 预估 | 0.5 天 |
| 风险 | 文档改动需与 README 中英文同步 |

### T0.5 迁移原子性决策与最小实现

| 项 | 内容 |
|----|------|
| 优先级 | P1（决策） |
| 文件 | `src/PalORM.Core/DataSession.Schema.cs`；`docs/adr/` |
| 改动 | 按 D2 裁决；推荐 A：新增 `MigrateWithResultAsync`，PG/SQLite 事务 DDL，MySQL 记录失败 |
| 测试 | 成功迁移、重复迁移、索引失败三类用例 |
| 验收 | 失败时返回 `MigrationResult`，包含 created/skipped/failed；PG/SQLite 失败不留半成品 |
| 验证命令 | SQLite 单测；PG 集成测试；MySQL 集成测试 |
| 依赖 | D2 |
| 预估 | 2 天 |
| 风险 | DDL 事务持锁；MySQL 无法完全原子；需在文档写清方言差异 |

---

## 4. Phase 1 任务清单

### T1.1 `MigrationResult` 与事务 DDL

与 T0.5 合并执行。产出：

- `public sealed record MigrationResult(...)`
- `public ValueTask<MigrationResult> MigrateWithResultAsync(CancellationToken ct = default)`
- `public ValueTask MigrateAsync(...)` 保留，内部调用并丢弃结果
- PG/SQLite 事务包裹；MySQL 逐条执行并记录失败
- `LastMigrationSkippedIndexes` 保留，新增 `LastMigrationResult`

验收命令：

```bash
dotnet run --project test/PalORM.Core.Tests -c Release
dotnet run --project test/PalORM.Integration.Tests -c Release
```

### T1.2 静态缓存测试复位

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `src/PalORM.Core/EntityInfoCache.cs`、`src/PalORM.Core/MemberResolver.cs`、`src/PalORM.Core/PalORM_Runtime.cs` |
| 改动 | 增加注册代际计数；缓存条目携带代际，代际变化时失效；`RestoreStateForTesting` 递增代际 |
| 测试 | 注册合成类型、读取、恢复注册表、重新注册同类型不同表名，断言使用新元数据 |
| 验收 | 测试隔离用例通过；现有测试无回归 |
| 验证命令 | `dotnet run --project test/PalORM.Core.Tests -c Release` |
| 风险 | 代际检查在热路径增加一次 int 比较；可接受 |

### T1.3 探测缓存容量上限

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `src/PalORM.Core/CapabilityProbeCache.cs` |
| 改动 | 增加 `MaxEntries`（建议 256 或 512）；满员时清理过期条目或整体清空；保留负结果窗口 |
| 测试 | 写入超过上限的键，断言条目数有界；过期条目可被清理 |
| 验收 | 多租户连接串场景内存有界 |
| 验证命令 | Core 单测 |
| 风险 | 整体清空会短暂增加探测次数；探测是幂等且低频，可接受 |

### T1.4 复用命令槽释放失败处理

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `src/PalORM.Core/DataSession.Crud.cs` 五个槽的晋升 catch 分支 |
| 改动 | 旧命令 `Dispose()` 抛异常时，先把槽置空，再释放新命令并抛出 |
| 测试 | 自定义命令包装器在 Dispose 时抛异常，断言下一次操作不会拿到已释放命令 |
| 验收 | 异常路径后会话仍可继续使用或明确失败，不出现重复失败 |
| 验证命令 | Core 单测 |
| 风险 | 测试需要自定义 Provider/Command，成本中等 |

### T1.5 同步 IO 叶子统一

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `src/PalORM.Core/DataSession.Crud.cs`、`src/PalORM.Core/DataSession.Query.cs` |
| 改动 | `GetAllCoreAsync`、`QueryEntryAsync`、`ScalarEntryAsync`、`QueryAsyncEnumerable` 改用 `ExecuteReaderAsync` / `ExecuteScalarCoreAsync` 静态助手 |
| 测试 | 现有测试全绿；可加一个 Provider 计数测试，断言 SQLite 走同步叶子 |
| 验收 | SQLite 读路径不再走异步脚手架；行为不变 |
| 验证命令 | Core 单测；SQLite 集成测试 |
| 风险 | 同步打开 reader 会阻塞调用线程；SQLite 本来就是同步执行，风险低 |

### T1.6 公共 API 收敛准备

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `src/PalORM.Core/BulkOperationFramework.cs`、`MultiValueBulkInsert.cs`、`RowPreparedBulkInsert.cs`、`SqlLimits.cs`、`CrudMetadata.cs` |
| 改动 | 按 D4 推荐 B：加 `[Obsolete]` 与 `[EditorBrowsable(Never)]`；文档标注“生成代码契约”或“Provider 内部 API”；7.0 再改 internal |
| 测试 | 编译无新增警告；文档门禁 |
| 验收 | 公共 API 面有清晰标注，外部使用者不会误用 |
| 验证命令 | `dotnet build PalORM.ci.slnf -c Release -warnaserror` |
| 风险 | `[Obsolete]` 会产生警告，需评估是否影响消费者；可用 `EditorBrowsable` 加文档，不急着加 `Obsolete` |

---

## 5. Phase 2 任务清单

### T2.1 共享注解与枚举源文件

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | 新增 `src/Shared/PalORMAnnotations.cs`；`src/PalORM.Core/PalORM.Core.csproj`；`src/PalORM.SourceGen/PalORM.SourceGen.csproj` |
| 改动 | 把 `DeleteAction` 与属性名常量抽到共享文件，用 `<Compile Include="..." Link="..."/>` 同时链接进 Core 与 SourceGen；不新增包 |
| 测试 | `IdentifierConsistencyTests` 或新增测试断言枚举数值与名称一致 |
| 验收 | SourceGen 与 Core 不再各写一份 `DeleteAction` |
| 验证命令 | `dotnet build PalORM.ci.slnf -c Debug`；SourceGen 测试 |
| 风险 | 共享文件同时被两个 TFM 编译，需确保不使用 TFM 特定 API |

### T2.2 版本常量生成

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `Directory.Build.targets` 或新增 `build/VersionInfo.targets`；`src/PalORM.Core/PalORMMetrics.cs`；`src/PalORM.SourceGen/GeneratedCodeMetadata.cs` |
| 改动 | MSBuild 生成 `PalORMVersion.g.cs`，包含 `internal const string Version = "$(Version)"`；两处常量引用生成物 |
| 测试 | 文档一致性检查 D11；版本变更后自动同步 |
| 验收 | 三处版本号不再手工维护 |
| 验证命令 | 修改 `Version` 后 build，检查生成常量；`doc-consistency-check` |
| 风险 | 生成文件需加入 `.gitignore` 或提交策略；避免与 SourceGen 的 `GeneratedCodeAttribute` 冲突 |

### T2.3 时间抽象

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `src/PalORM.Core/CacheStore.cs`、`CircuitBreaker.cs`、`CapabilityProbeCache.cs`、`Resilience.cs` |
| 改动 | 引入窄接口或 `Func<long>` 时钟，默认 `Environment.TickCount64`；测试可注入假时钟 |
| 测试 | 缓存过期、熔断冷却、探测负窗口的确定性测试，不依赖真实等待 |
| 验收 | 时间敏感组件可在毫秒内完成测试 |
| 验证命令 | Core 单测 |
| 风险 | 不要在热路径全面替换；只在时间边界注入，避免虚拟调用开销 |

### T2.4 运行时正则改 `GeneratedRegex`

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `src/PalORM.Core/DataSession.cs`、`src/PalORM.Core/DataSession.PartialUpdate.cs` |
| 改动 | 两个 `Regex.Replace` 改为 `[GeneratedRegex]` partial 方法；保留超时参数 |
| 测试 | 现有租户护栏与部分更新测试；新增正则边界用例 |
| 验收 | 行为不变；AOT publish 仍 0 警告 |
| 验证命令 | Core 单测；AOT tests |
| 风险 | `GeneratedRegex` 源生成器在 net11 可用；注意 partial 方法签名与超时 |

### T2.5 分析器规则逐条启用

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `.editorconfig`；`src/PalORM.Core/PalORM.Core.csproj` |
| 改动 | 保持 `latest-minimum`，按规则逐条启用 CA1062/CA1849/CA2012/CA2213 等高价值项；逐条修复或带理由抑制 |
| 测试 | Release 构建 0 警告；CI 门禁 |
| 验收 | Core 的分析器覆盖提升，且不回到 338 条噪声 |
| 验证命令 | `dotnet build PalORM.ci.slnf -c Release --no-incremental -warnaserror` |
| 风险 | 逐条启用会产生修复工作量；分批进行，每批独立提交 |

**实施记录（2026-10-31）**：CA1062/CA1849/CA2012/CA2213 四条均已启用为 error。CA1062 修复了
`BulkOperationFramework`、`DataSession.Query`、`QueryBuilderExtensions`、`AuditInterceptor` 的公开
方法参数检查；CA2213 仅剩读连接字段，因释放由 `DisposeReadConnectionAsync` 统一执行，按字段局部
抑制并注明理由；CA2012 与 CA1849 启用后无新增违规。

### T2.6 文档漂移修复

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `docs/架构设计.md`、`README.md`、`README.en.md`、`scripts/doc-consistency-check.cs` |
| 改动 | 更新源生成器 pipeline、去掉 `IRowFactory<T>`/`TypeMapper.g.cs`；诊断计数改为 46 或核实后修正；增加诊断计数一致性检查 |
| 测试 | `doc-consistency-check` |
| 验收 | 文档与代码一致；计数门禁可失败 |
| 验证命令 | `dotnet run --file scripts/doc-consistency-check.cs` |
| 风险 | 中英文 README 同步；计数需要从代码自动提取，避免再次漂移 |

### T2.7 注释与历史决策整理

| 项 | 内容 |
|----|------|
| 优先级 | P2 |
| 文件 | `src/PalORM.Core/DataSession_Bulk.cs`、`QueryBuilder.cs`、`DataSession.cs` |
| 改动 | 定义注释规范：方法上方只留当前不变量与“为什么”；历史轮次、探针数字移到 ADR/lessons；不删除仍有约束力的警告 |
| 测试 | 文档评审；无行为变更 |
| 验收 | 热路径文件注释密度下降，关键不变量仍可检索 |
| 验证命令 | 人工评审；`dotnet build` 无变化 |
| 风险 | 注释删除可能丢失知识；先迁移到 ADR/lessons，再精简 |

---

## 6. Phase 3 任务清单

### T3.1 多目标可行性 spike

- 分支：`spike/multi-target`
- 目标：评估 `net8.0;net9.0;net10.0;net11.0`
- 重点：`Lock` 在 net8 不可用；`runtime-async` 条件化；`Type.GetNullableUnderlyingType` 回退；`Microsoft.Extensions.Logging.Abstractions` 包化；`Microsoft.Data.Sqlite.Core` 版本矩阵
- 产出：兼容性报告与工作量估算，不默认合并
- 验收：至少一个目标框架可 build + Core 测试通过

### T3.2 RegistryScope spike

- 分支：`spike/registry-scope`
- 目标：评估模块化单体下的注册表作用域
- 重点：静态缓存键、`SqlSlots<T>`、`MigrateAsync` 过滤、生成代码引用
- 产出：设计草案与风险评估，不默认合并

### T3.3 第四 Provider 方言插件评估

- 分支：`spike/dialect-plugin`
- 目标：评估把 `SqlDialect` 枚举扩展为方言描述符的成本
- 重点：生成器、DDL、批处理、数组参数、迁移
- 产出：结论文档；如果成本过高，明确“三方言封闭集”为非目标

### T3.4 可选包拆分评估

- 目标：评估 `PalORM.Caching`、`PalORM.Observability` 拆分
- 重点：公共 API 迁移、依赖、版本策略
- 产出：7.0 版本规划输入

---

## 7. 验收矩阵

| 任务 | 单元测试 | 集成测试 | 构建 | 文档 | AOT |
|------|:---:|:---:|:---:|:---:|:---:|
| T0.1 | 必须 | MySQL 可选 | 必须 | 可选 | 不涉及 |
| T0.2 | 必须 | 不涉及 | 必须 | 可选 | 不涉及 |
| T0.3 | 必须 | PG/MySQL 可选 | 必须 | 必须 | 不涉及 |
| T0.4 | 不涉及 | 不涉及 | 不涉及 | 必须 | 不涉及 |
| T0.5/T1.1 | 必须 | PG/MySQL 必须 | 必须 | 必须 | 不涉及 |
| T1.2 | 必须 | 不涉及 | 必须 | 可选 | 不涉及 |
| T1.3 | 必须 | 不涉及 | 必须 | 可选 | 不涉及 |
| T1.4 | 必须 | 不涉及 | 必须 | 可选 | 不涉及 |
| T1.5 | 必须 | SQLite 必须 | 必须 | 可选 | 不涉及 |
| T1.6 | 不涉及 | 不涉及 | 必须 | 必须 | 不涉及 |
| T2.1 | 必须 | 不涉及 | 必须 | 可选 | 必须 |
| T2.2 | 必须 | 不涉及 | 必须 | 必须 | 必须 |
| T2.3 | 必须 | 不涉及 | 必须 | 可选 | 不涉及 |
| T2.4 | 必须 | 不涉及 | 必须 | 可选 | 必须 |
| T2.5 | 必须 | 不涉及 | 必须 | 可选 | 不涉及 |
| T2.6 | 不涉及 | 不涉及 | 不涉及 | 必须 | 不涉及 |

统一验证命令：

```bash
# 1. 严格构建
dotnet build PalORM.ci.slnf -c Release --no-restore --no-incremental -warnaserror

# 2. 单元测试
dotnet run --project test/PalORM.Core.Tests -c Release
dotnet run --project test/PalORM.SourceGen.Tests -c Release

# 3. 集成测试（需要 PG/MySQL 服务）
dotnet run --project test/PalORM.Integration.Tests -c Release

# 4. 质量门禁
dotnet run --file scripts/gate-check.cs -- --allow-dirty
dotnet run --file scripts/doc-consistency-check.cs

# 5. AOT（涉及生成器或运行时改动时）
dotnet publish test/PalORM.AotTest -c Release -r win-x64
```

注意：测试项目使用 TUnit，直接 `dotnet run`，不要用 `dotnet test`。集成测试需要 `PALORM_PG_CONNECTION` 与 `PALORM_MYSQL_CONNECTION` 环境变量，或走 CI 服务容器。

---

## 8. 提交与发布策略

| 阶段 | 建议提交粒度 | 版本类型 | 说明 |
|------|--------------|----------|------|
| Phase 0 | 每任务一个提交 | PATCH | 缺陷修复，不新增公共 API |
| Phase 1 | 每任务一个提交 | MINOR | `MigrationResult` 等新增 API |
| Phase 2 | 每任务一个提交 | MINOR 或 PATCH | 内部重构、分析器、文档 |
| Phase 3 | spike 分支 | 不发布 | 结论驱动后续版本 |

提交格式：`修复：...`、`测试：...`、`文档：...`、`重构：...`、`基准：...`。涉及公共 API 的提交在正文说明兼容性与迁移方式。

发布前按 `docs/发布规范.md` 执行版本判定与全量预检；快照类改动先 `PALORM_UPDATE_SNAPSHOTS=1` 更新基线。

---

## 9. 风险与回滚

| 风险 | 触发条件 | 处置 |
|------|----------|------|
| 方言修复引入 MySQL 回归 | 集成测试失败 | 回滚 T0.1 单提交；保留默认参数直到确认 |
| Scaffold 生成代码变化 | golden 测试失败 | 回滚 T0.2；先补测试再改实现 |
| 迁移事务导致锁等待 | PG/SQLite 集成测试超时 | 缩小事务范围或改为结果对象方案 B |
| 静态缓存代际检查影响性能 | 基准分配或时延超线 | 回退代际检查，测试改用唯一类型 |
| 分析器规则启用导致大量警告 | Release 构建失败 | 分批启用，逐条抑制或修复 |
| 多目标 spike 破坏主分支 | 分支合并冲突 | spike 只在独立分支，结论不默认合并 |

---

## 10. 进度追踪

| ID | 任务 | 状态 | 负责人 | 开始 | 完成 | 备注 |
|----|------|------|--------|------|------|------|
| T0.1 | 方言格式化遗漏 | 已完成 | | | | |
| T0.2 | Scaffold 主键生成 | 已完成 | | | | |
| T0.3 | `Raw` 会话标脏 | 已完成 | | | | |
| T0.4 | 拦截器覆盖决策 | 已完成 | | | | 需 D1 |
| T0.5 | 迁移原子性 | 已完成 | | | | 需 D2 |
| T1.2 | 静态缓存复位 | 已完成 | | | | |
| T1.3 | 探测缓存上限 | 已完成 | | | | |
| T1.4 | 槽位释放失败 | 已完成 | | | | |
| T1.5 | 同步 IO 统一 | 已完成 | | | | |
| T1.6 | 公共 API 收敛准备 | 已完成 | | | | 需 D4 |
| T2.1 | 共享注解枚举 | 已完成 | | | | |
| T2.2 | 版本常量生成 | 已完成 | | | | |
| T2.3 | 时间抽象 | 已完成 | | | | |
| T2.4 | GeneratedRegex | 已完成 | | | | |
| T2.5 | 分析器规则 | 已完成 | | | | |
| T2.6 | 文档漂移修复 | 已完成 | | | | |
| T2.7 | 注释整理 | 已完成 | | | | |
| T3.1 | 多目标 spike | 已完成 | | | | 需 D3 |
| T3.2 | RegistryScope spike | 已完成 | | | | |
| T3.3 | 方言插件评估 | 已完成 | | | | |
| T3.4 | 可选包拆分 | 已完成 | | | | |

---

## 11. 完成定义

每个任务完成必须同时满足：

1. 代码已实现，无 TODO、无 `NotImplementedException`、无占位符。
2. 新增或修改的测试在修复前失败、修复后通过，测试名能说明行为。
3. Release 严格构建 0 警告 0 错误。
4. 相关测试套件全绿，集成测试涉及方言时三方言结果一致或有明确差异说明。
5. 文档与注释同步，旧事实值全仓 grep 零残留。
6. pre-commit 七段全过，未被 `--no-verify` 绕过。
7. 提交信息符合 `类型：描述`，涉及公共 API 时说明兼容性。
8. 任务清单状态更新，阻塞项写明原因与下一步。

---

## 12. 建议执行顺序

第一批，可立即开始：

1. T0.1 方言格式化。
2. T0.2 Scaffold 主键。
3. T0.3 `Raw` 标脏。
4. T1.2 静态缓存复位。

第二批，决策后开始：

1. D1 裁决，T0.4。
2. D2 裁决，T0.5/T1.1。
3. T1.3、T1.4、T1.5。
4. D4 裁决，T1.6。

第三批，维护性：

1. T2.1 至 T2.7。
2. Phase 3 spike。

---

## 13. 需要用户裁决的事项

1. D1 拦截器覆盖面：推荐 C，文档化边界，`ICommandInterceptor` 留到 7.0。
2. D2 迁移原子性：推荐 A，PG/SQLite 事务 DDL 加 `MigrationResult`。
3. D3 目标框架：推荐当前保持 net11，Phase 3 做 net10 spike。
4. D4 公共 API 收敛：推荐 B，先标注，7.0 改 internal。

裁决后即可从 T0.1 开始执行。


---

## 14. 完成记录（2026-10-31）

### 提交

- Phase 0 + Phase 1：`ed8d9a1 修复：架构评审整改 P1/P2（方言/Scaffold/Raw/迁移/缓存/同步 IO）`
- Phase 2 + Phase 3 文档：`a551171 重构：架构评审 Phase 2/3（共享枚举、版本生成、时钟注入、GeneratedRegex、文档与 spike）`
- 迁移事务竞态修复 + 共享枚举测试：`0dcf6bc 修复：迁移事务内并发建表竞态改用保存点恢复`
- ADR-U/ADR-V + CrudMetadata 可见性 + Guid 主键测试：`849e48f 重构：补 ADR-U/ADR-V 与 CrudMetadata 可见性标注`
- 共享注解契约单文件收敛：`bd936f1 重构：合并共享注解契约到 PalORMAnnotations.cs`
- CA1062/CA2213 启用 + 方案归档：`ebf9c4e 重构：启用 CA1062/CA2213 分析器并归档架构评审方案`
- 六个提交均在本地 `dev`，未推送；本方案文件归档于 `docs/review/architecture-review-action-plan-2026-10-31.md`

### 验证

| 验证 | 结果 |
|------|------|
| Core 测试 | 704/704 通过 |
| SourceGen 测试 | 261/261 通过（共享枚举 + Guid 主键测试） |
| Integration 测试 | 526/526 通过（含 PG 并发迁移竞态用例） |
| Release 全量构建 | 0 警告 0 错误 |
| 质量门禁 | 32 通过，1 跳过（--allow-dirty），0 失败 |
| 文档一致性 | 10 通过，0 失败（D10=1444，D17 诊断计数 46） |
| AI 系统校验 | 25/25 通过 |
| AOT SQLite | publish 成功，原生运行 PASSED，exit 0 |
| net10 spike | Core 构建 0/0，Core 测试 700/700 |

### 说明

- T0.4：D1 裁决为文档化边界，新增 `docs/adr/ADR-U-IQueryInterceptor覆盖边界.md`，README 中英文与 XML 文档已列覆盖矩阵。
- T0.5/T1.1：D2 裁决为事务 DDL + 结果对象，新增 `docs/adr/ADR-V-迁移原子性与方言差异.md`；PG/SQLite 事务内并发建表竞态会让 PG 进入 aborted 态（25P02），已改为批执行与逐条回退各套保存点，重名异常只回滚到保存点；索引 DDL 同策略。MySQL 保持非事务逐条路径不变。
- T1.6：按 D4 推荐 B 加 `[EditorBrowsable(Never)]` 与 XML 文档标注（含 `CrudMetadata`）；未加 `[Obsolete]`，因为 Core 与 Provider 自身使用这些类型，`CS0618` 会污染构建。7.0 改 internal 时一并处理。
- T2.1：共享契约收敛到单个 `src/Shared/PalORMAnnotations.cs`，含 `DeleteAction` 与注解短名常量；共享枚举测试锁定四个取值一致。
- T2.5：CA1062/CA1849/CA2012/CA2213 四条均启用为 error；CA1062 修复公开方法参数检查，CA2213 的读连接字段按局部抑制并注明释放路径；全解决方案构建 0 警告 0 错误。
- T2.7：已建立 ADR-T 注释分层规范与历史块迁移索引，并完成 QueryBuilder 首个示范块精简；其余历史块按索引渐进迁移，避免一次性删除造成知识丢失。
- T3.1：在独立 worktree `C:/ai/claude/PalORM-net10-spike` 完成 net10 构建与 Core 测试，不合并主分支；报告见 `docs/review/spike-multi-target-2026-10-31.md`。
- T3.2 至 T3.4：结论见 `docs/review/spike-registry-scope-2026-10-31.md`、`spike-dialect-plugin-2026-10-31.md`、`spike-package-split-2026-10-31.md`。
- T3.1：在独立 worktree `C:/ai/claude/PalORM-net10-spike` 完成 net10 构建与 Core 测试，不合并主分支；报告见 `docs/review/spike-multi-target-2026-10-31.md`。
- T3.2 至 T3.4：结论见 `docs/review/spike-registry-scope-2026-10-31.md`、`spike-dialect-plugin-2026-10-31.md`、`spike-package-split-2026-10-31.md`。
