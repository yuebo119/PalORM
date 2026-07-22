# PalORM 测试规范系统（v1.0）

> `/test` → 测试规范化约束。不是 review（找缺陷）、不是 gate（规范合规）、不是 refine（求更优）。
> **宗旨：让每一行测试代码都能被信任**——测试本身的质量决定生产的质量。
> 测试系统回答一个问题：**这份变更的测试是否充分且规范？**
> **权威来源**：[`docs/测试规范.md`](../../docs/测试规范.md) · [`docs/测试体系规范.md`](../../docs/测试体系规范.md)

---

## 定位边界

| 系统 | 问题 | 产出 |
|------|------|------|
| `/review` | 代码有什么缺陷？ | 缺陷清单+四指标+热点 |
| `/gate` | 遵守编码规范了吗？ | 通过/失败+违规清单 |
| `/refine` | 如何更优实现？ | 现代化+性能优化方案 |
| **`/test`** | **测试是否充分且规范？** | **测试覆盖矩阵+违规清单+缺失清单** |

裁决顺序：**gate 阻断 > test 违规 > review 缺陷 > refine 优化**。

---

## T1-T10：测试铁律（不可违反）

| 编号 | 铁律 | 违反后果 |
|------|------|---------|
| T1 | **新增/修改的生产代码必须有对应测试**——无测试的 PR 拒绝合入 | 阻断 |
| T2 | **测试必须验证行为，不验证实现**——断言公共 API 的输入输出，不断言私有字段/方法调用顺序 | 警告 |
| T3 | **每个测试方法只测一件事**——一个 `[Test]` 方法只含一个核心断言链（setup→act→assert） | 警告 |
| T4 | **测试命名遵循 `Method_Scenario_ExpectedResult`**——或 `Provider_Operation_Behavior`（集成测试） | 警告 |
| T5 | **测试间无状态依赖**——每个测试独立 setup + cleanup，不依赖执行顺序 | 阻断 |
| T6 | **外部资源测试必须 try/finally 清理**——PG/MySQL 表创建后必须在 finally 中 DROP | 阻断 |
| T7 | **emit 变更必须清 obj/bin + 重生成快照**——`PALORM_UPDATE_SNAPSHOTS=1` 产生的 diff 未经评审不得提交 | 阻断 |
| T8 | **基准配置必须标注场景理由**——每个 `[SimpleJob]` 必须有注释说明为何选此配置 | 警告 |
| T9 | **BenchmarkCategory 不得混用同义词**——同一概念全局用一个标签（如全用 `BulkInsert` 不混用 `Bulk`） | 警告 |
| T10 | **基准 Median 的 Error/Mean > 15% 视为统计无效**——必须提升配置或标注环境噪声 | 警告 |

---

## 缺陷登记（T-DEF-1 ~ T-DEF-N）

> 测试系统的缺陷登记——从历次测试规范化审计中沉淀。只增不删。

| # | 缺陷 | 教训 | 来源 |
|---|------|------|------|
| T-DEF-1 | 门禁脚本缺 `set -e` | `gate-check.sh` 等中间命令失败不退出，破坏性 PR 误过 CI。所有 bash 脚本必须 `set -euo pipefail` | P0 审计 2026-07-22 |
| T-DEF-2 | PG/MySQL 测试无 finally 清理 | `MySqlTests.Execute_DDL_Works` 单行式末尾 DROP 在断言失败时不执行，表残留。所有外部 DB 测试必须 try/finally | P0 审计 2026-07-22 |
| T-DEF-3 | BenchmarkCategory 混用 | `Bulk` vs `BulkInsert` 混用导致 `--filter` 语义不一致。统一标签命名 | P1 审计 2026-07-22 |
| T-DEF-4 | CI 无 timeout-minutes | 默认 360 分钟，DB 挂起长时间占用 runner。每个 job 必须显式设置 | P0 审计 2026-07-22 |
| T-DEF-5 | perf-gate SDK 与 ci.yml 不一致 | `dotnet-version` vs `global-json-file` 可能漂移到不同 patch。统一用 `global-json-file` | P1 审计 2026-07-22 |
| T-DEF-6 | 文件名与类名不匹配 | `V11Tests.cs` 内含 `OwnedJsonTests` 类。文件名必须对齐主类名 | P1 审计 2026-07-22 |
| T-DEF-7 | 测试脚本无索引 | 18 个脚本对新人可发现性差。必须有 `scripts/README.md` 分类索引 | P1 审计 2026-07-22 |

---

## 新增代码的测试要求矩阵

| 代码变更类型 | 必须的测试 | 测试项目 | 门禁检查 |
|-------------|-----------|---------|---------|
| `src/PalORM.Core/*.cs` 新增/修改公共方法 | ≥1 正向 + ≥1 边界/异常 | `test/PalORM.Core.Tests` | T1 |
| `src/PalORM.SourceGen/*Emitter.cs` emit 模板变更 | 快照重生成 + 内容断言 | `test/PalORM.SourceGen.Tests` | T7 |
| `src/PalORM.SourceGen/PalORMAnalyzer.cs` 新诊断 | ≥1 触发 + ≥1 不触发 | `test/PalORM.SourceGen.Tests` | T1 |
| 新 Provider 适配器 | ≥1 DryRun + ≥1 真库冒烟 | `test/PalORM.Integration.Tests` | T1+T6 |
| 性能敏感路径修改 | 对应 `[Benchmark]` | `bench/PalORM.Benchmarks` | T1 |
| `IDbProvider` 新方言分支 | 方言差异断言 | `test/PalORM.Integration.Tests/DialectDifferenceTests.cs` | T1 |

---

## 测试命名规范

```csharp
// 单元测试
[Test]
public async Task GetByKey_ReturnsNull_WhenNotFound() { ... }

// 集成测试（含 Provider 前缀）
[Test]
public async Task Sqlite_BulkInsert_RoundTripsAllRows() { ... }

// 诊断测试（含 PALORM 编号）
[Test]
public async Task PALORM014_Fires_WhenMultipleKeys() { ... }

// 基准（无特殊命名要求，方法名描述操作即可）
[Benchmark]
public async Task<List<BenchOrder>> PalORM_QueryAll() { ... }
```

---

## BenchmarkDotNet 配置规范

| 层级 | 配置 | 适用场景 | 注释要求 |
|------|------|---------|---------|
| 快速验证 | `launchCount=1, warmupCount=3, iterationCount=5` | 开发迭代 | `// 快速验证配置` |
| 标准基准 | `launchCount=3, warmupCount=5, iterationCount=10` | 正式报告 | `// 标准基准配置——统计可信度中` |
| 严格基准 | `launchCount=5, warmupCount=10, iterationCount=15` | 发版基线、CI 门禁 | `// 严格基准配置——nanosecond 级或 CI 门禁` |

**每个 `[SimpleJob]` 必须有注释说明选择理由（T8）。**

---

## 机械防线（test 系统下沉产物）

| 防线 | 守护对象 | 对应铁律 |
|------|---------|---------|
| `scripts/test-gate.sh` | T1/T3/T4/T8/T9 机械检查 | 门禁脚本 |
| `scripts/tech-debt-scan.sh` #9 | 测试用例数与 README badge 一致 | T1 |
| `scripts/tech-debt-scan.sh` #10 | csproj 版本号一致 | — |
| BenchmarkDotNet `[MemoryDiagnoser]` | 全基准类覆盖 | — |
| SnapshotTests | emit 产物基线 | T7 |
| `PALORM_UPDATE_SNAPSHOTS=1` | 快照变更强制评审 | T7 |

---

## /test 执行流程

```
1. 扫描变更范围（git diff --name-only）
2. 识别变更类型（Core/SourceGen/Provider/Benchmark/Test）
3. 按要求矩阵检查测试覆盖
4. 运行 scripts/test-gate.sh（机械检查）
5. 产出报告：
   - 测试覆盖矩阵（✅/❌/⚠）
   - T1-T10 铁律违规清单
   - 缺失测试建议
   - 统计有效性评估（基准）
```

---

## 维护规则

1. **缺陷只增不删**：新发现的测试缺陷追加为 `T-DEF-{N+1}`。
2. **铁律变更需评审**：新增/修改 T 编号铁律必须在 PR 中说明依据。
3. **门禁脚本同步**：改 `test-gate.sh` 必须同步本文的机械防线表。
4. **与 gate 系统不重复**：gate 系统已覆盖的编码规范检查（G1-G28），test 系统只关注测试维度。
