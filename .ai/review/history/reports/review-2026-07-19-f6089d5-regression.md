# PalORM 全量 Review 报告：f6089d5（review v3.1 · 全量档 · 整改回归验证轮）

> 日期：2026-07-19 · 基线：`f6089d5`（dev，工作树干净）
> 前序基线：`835591f`（全量 review v3.1 第 10 轮——18 项 P1/P2/P3 行动项）
> 方法：engine v2.3 **整改回归验证轮**——`835591f→f6089d5` 共 4 提交 27 文件 710 行变更（12 src + 1 新 src + 9 测试 + 5 文档），本轮目标：**确认整改无回归 + 无新缺陷 + 暴露半完成项**。
> 范围策略：因 src 改动集中在 12 文件（不是全 43 文件并行），改为 **2 子代理 + 主线程跨片不变式**：子代理 A 逐行 13 改动 src 文件，子代理 B 复查 6 处测试代码同型修复正确性 + 全 test/ 残留扫描，主线程做跨文件契约核查 + 统一定级 + 探针实证 + 定稿门三问。
> 结论：**REQUEST_CHANGES** — 0 P0 + 0 P1 + 3 P2 + 7 P3。13 改动 src 文件逐行覆盖率 100%；3 条**整改半完成项**确认（ITM-603 命令路径未覆盖 / ITM-596 消息被遮蔽 / ITM-601 PALORM013 漏改）；其余 7 条为存量边界登记或设计取舍。

---

## 段 1：范围与方法

**评审范围**：835591f→f6089d5 的全部改动 src/ 文件（12 改 + 1 新）+ 测试代码同型修复正确性（9 处）+ 全 test/ 残留扫描。

**改动 src 文件清单**（逐行勾销）：

- [x] src/PalORM.Core/DataSession.cs (+23 -6 行)
- [x] src/PalORM.Core/IdentifierSafety.cs (新 28 行)
- [x] src/PalORM.Core/PalORMMetrics.cs (+3 -3 行)
- [x] src/PalORM.Core/Resilience.cs (+5 行)
- [x] src/PalORM.Core/SessionOperationState.cs (+8 行)
- [x] src/PalORM.Core/StoredProcBuilder.cs (+9 -1 行)
- [x] src/PalORM.Core/TransactionCleanup.cs (+7 行)
- [x] src/PalORM.MySql/MySqlProvider.cs (+2 -3 行)
- [x] src/PalORM.PostgreSql/PostgreSqlProvider.cs (+2 -3 行)
- [x] src/PalORM.SourceGen/PalORMAnalyzer.cs (+38 -13 行——最大改动)
- [x] src/PalORM.SourceGen/TableModel.cs (+8 -1 行)
- [x] src/PalORM.Sqlite/SqliteProvider.cs (+3 -3 行)

**测试代码同型修复正确性清单**（子代理 B 已 PASS）：

- [x] test/PalORM.SourceGen.Tests/SourceGenTests.cs:ExternalUser — `[Key(AutoIncrement=false)]` 正确
- [x] test/PalORM.Core.Tests/BulkCleanupTests.cs:BulkCleanupEntity — 正确
- [x] test/PalORM.Integration.Tests/BulkDataTests.cs:BulkInsertDefaultEntity — 正确
- [x] test/PalORM.Integration.Tests/BulkDataTests.cs:BulkKeyOnlyEntity — 正确
- [x] test/PalORM.Integration.Tests/MultiEntityTests.cs:ConvertedKeyEntity — BinaryIdConverter × AutoIncrement=false 兼容
- [x] test/PalORM.Integration.Tests/MultiEntityTests.cs:ConvertedSoftDeleteEntity — 三属性共存正确
- [x] test/PalORM.Integration.Tests/MultiEntityTests.cs:UpsertShapeEntity — 正确
- [x] test/PalORM.AotTest/Program.cs:AotBulkEntity — DDL `Id TEXT PRIMARY KEY` 与 AutoIncrement=false 对齐
- [x] test/PalORM.AotTest.MySql/Program.cs:AotMySqlBulkEntity — DDL `VARCHAR(255) PRIMARY KEY` 对齐
- [x] test/PalORM.AotTest.Pg/Program.cs:AotPgBulkEntity — DDL `"Id" TEXT PRIMARY KEY` 对齐

**明确不评审**：
- `*.g.cs` 源生成器生成文件（P1 误判模式）
- 未改动的 31 个 src/ 文件（不在本轮回归范围；上一轮 835591f 已地毯）
- `docs/`、`.ai/`、`scripts/` 非业务代码

**抽样策略**：改动 src 文件全量逐行；测试代码逐个 Read 验证 [Key] 标注 + 测试语义。

**盲区声明**：
- 真库（PG/MySQL）行为未在本机验证——本轮基线本机仅 SQLite 可达
- 性能（编译时间、运行时分配）未做基准测量
- GeneratorPhase2Tests 与 SnapshotTests 中的内联实体片段（S14/S16/S18）走源生成器宿主（不挂 analyzer），PALORM022 不触发——子代理 B 已确认无需同型修复

---

## 段 2：评审基线

```
基线 commit：f6089d5（dev，工作树干净）
基线类型：评审收口（835591f 轮 18 项 P1/P2/P3 全量整改完成）
前序基线：835591f（全量 review v3.1 第 10 轮）

─── 835591f → f6089d5 提交序列 ───
bd29f1b 修复：ITM-587/588/601 P1 三项——PalORMAnalyzer 基类链口径对齐
b6a0966 修复：ITM-589/590 P2 两项——PALORM022 扩展 + 程序集表名跨类型缓存
c05a637 修复：ITM-591~604 P3 十三项——防御检查 + Obsolete 口径 + 文档 + 控制字符守卫
f6089d5 评审收口：835591f 轮 18 项行动项账实同步

─── 改动统计 ───
27 文件 710 行变更（+660 -51）
  src/: 12 改 + 1 新 = 13 文件 / +135 -31 行
  test/: 9 文件 / +200 -16 行（含 168 行新增防复发测试）
  docs/README: 5 文件 / +11 -4 行（三方一致同步）
  .ai/: 2 文件（报告 + 行动项 + metrics）

─── 构建状态 ───
    0 个警告
    0 个错误
构建退出码：0

─── 门禁状态（gate-check.sh）───
通过：29  警告：0  失败：0  总计：29（G1-G29 全绿）
═══════ 快照完成 ═══════
```

---

## 段 3：准确性分析

### 3.1 整改半完成项（3 条 P2——本轮核心发现）

| ID | 发现 | 位置 | 可信度 | 信息源 | 已对照误判模式 |
|:--:|------|------|:--:|--------|:--:|
| ITM-605 | **ITM-603 RetryBackoff 负值守卫只加在 DataSession.CreateAsync（连接重试路径），未加在 ResilienceExecutor.ExecuteAsync（命令重试路径）**——两路径独立，命令重试时 `Task.Delay(_backoff(attempt))` 仍抛 `ArgumentOutOfRangeException("delay")` 不指向 RetryBackoff 配置 | `src/PalORM.Core/Resilience.cs:75`（对照 `DataSession.cs:78-87`） | ✅ | Read 源码 + 双路径对比 | 已排除 5（当前 commit 验证） |
| ITM-606 | **ITM-596 "Cannot use a disposed transaction" 消息被 DataSession.UseTransaction 的 ReferenceEquals 检查遮蔽**——已 dispose 事务的 `tran.Connection == null`，先做 `ReferenceEquals(null, _conn)` 抛"事务必须属于当前 DataSession 的主连接"（误导），SessionOperationState.UseTransaction 中更精确的 disposed 消息永远不触发 | `src/PalORM.Core/DataSession.cs:580`（对照 `SessionOperationState.cs:276-280`） | ✅ | Read 源码 + 顺序分析 | 已排除 5 |
| ITM-607 | **ITM-601 基类链统一整改漏改 PALORM013（并发令牌计数）**——PalORMAnalyzer.cs:265 仍用 `type.GetMembers().OfType<IPropertySymbol>()` 不走基类链，与 TableModel.GetMappableProperties（基类链）口径不一致。派生类继承 `AuditBase.Version`（基类 [ConcurrencyCheck]）+ 自身 `RowVer` 时，分析器只看到 1 个令牌不报 PALORM013，生成器实际收集 2 列，BindUpdate 走单令牌假设冲突 | `src/PalORM.SourceGen/PalORMAnalyzer.cs:265`（对照 TableModel.cs:77） | ✅ | Read 源码 + 同根因类对比 | 已排除 P9 |

### 3.2 存量/设计取舍登记（7 条 P3）

| ID | 发现 | 位置 | 可信度 | 信息源 |
|:--:|------|------|:--:|--------|
| ITM-608 | IdentifierSafety 只拒 C0（U+0000-U+001F）+ DEL（U+007F），未拒 C1（U+0080-U+009F）——C1 在多字节 UTF-8 序列下驱动 C 层解析行为同样不稳，但当前调用点全为编译期常量，威胁面接近 0 | `src/PalORM.Core/IdentifierSafety.cs:19` | ⚠ | Read 源码 + Unicode 标准核对 |
| ITM-609 | IdentifierSafety public 跨程序集无 `[EditorBrowsable(Never)]` 治理——外部 IDE IntelliSense 会看到稳定类型，可能误用直接调用而非经 Provider | `src/PalORM.Core/IdentifierSafety.cs:9` | ⚠ | Read 源码 + API 表面分析 |
| ITM-610 | StoredProcBuilder._executed 非原子（裸 bool）——同实例并发调 QueryAsync 有竞态，两线程都见 false → 都通过 → 都 Add 参数，provider 行为未定义。但实际使用契约是"一个 builder 一次异步链"，并发复用不符契约 | `src/PalORM.Core/StoredProcBuilder.cs:19` | ⚠ | Read 源码 + 契约分析 |
| ITM-611 | TransactionCleanup.DisposeTransactionPreservingAsync 成功路径静默吞 DisposeAsync 异常（ITM-595 已自陈）——失败可观测性为零，调用方收到成功但底层连接状态可能不一致 | `src/PalORM.Core/TransactionCleanup.cs:34` | ⚠ | Read 源码 + ITM-595 注释 |
| ITM-612 | CompilationStartAction 闭包内 `assemblyTables ??=` 在 EnableConcurrentExecution 下可能重复计算（两线程都见 null）——非正确性缺陷，仅大型项目首次编译可感知的 CPU 浪费 | `src/PalORM.SourceGen/PalORMAnalyzer.cs:189-382` | ⚠ | Read 源码 + Roslyn 并发模型 |
| ITM-613 | Core 与 SourceGen 各自定义 `DeleteAction` 枚举（Core:82/SourceGen:248），数值靠注释约定对齐——无编译期约束，未来任一侧改顺序即漂移。当前 FK 不生成 DDL（ITM-525），运行时无影响 | `src/PalORM.SourceGen/TableModel.cs:248` | ⚠ | Read 源码 + 双枚举对比 |
| ITM-614 | PALORM022 reason 链按 init-only → nullable → AutoIncrement 三分支首中即报——同时具备多问题的主键只报第一条，用户需多次编译。复合多问题主键在实际代码极罕见 | `src/PalORM.SourceGen/PalORMAnalyzer.cs:170-182` | ⚠ | Read 源码 + reason 链分析 |

### 3.3 子代理 B 测试同型修复正确性结论（PASS）

子代理 B 完整 Read 9 处测试代码 + 全 test/ 残留扫描（10 候选位置）：
- **6 处 P3 同型修复 + 3 处 AotTest**：全部正确应用 `[Key(AutoIncrement=false)]`
- **BinaryIdConverter × AutoIncrement=false**：兼容（TableModel 先归一 provider-type string，再判 isAutoIncrement=false）
- **三方言 AotTest DDL**：与 AutoIncrement=false 严格对齐（无 AUTOINCREMENT/AUTO_INCREMENT/BIGSERIAL）
- **测试语义保真**：无任何用例依赖自增回填
- **残留同型漏修**：0（3 处 GeneratorPhase2Tests/SnapshotTests 的 [Key] 无参属于源生成器宿主测试不挂 analyzer，无需修）

### 3.4 主线程跨片不变式核查（全闭环）

| 不变式 | 核查结果 |
|--------|---------|
| IdentifierSafety 三方言全部接入 | ✅ SQLite/PostgreSql/MySql 三 Provider 均引用，旧 NUL 检查 0 残留 |
| PalORMAnalyzer EnumerateMappedProperties 口径 | ⚠ PALORM001/002/014/018/021 已统一（6 处），**PALORM013 漏改**（见 ITM-607） |
| CompilationStartAction 注册结构 | ✅ PALORM002-004 块整体迁入，闭包内 assemblyTables 缓存正确 |
| PALORM022 reason 链 autoIncrementEnabled 判定 | ✅ 与 TableModel.isAutoIncrement 完全一致（`is not false`） |
| TableModel.OnDelete 从命名参数提取 | ✅ 默认 NoAction=0，named arg 存在则取值——但与 Core enum 数值约定无强约束（见 ITM-613） |

### 3.5 反证证伪

子代理 A 的 10 条疑点中：
- **3 条确认为真缺陷**（D-005/D-006/D-008 → ITM-605/606/607）
- **7 条降级为 P3**（D-001/D-002/D-003/D-004/D-007/D-009/D-010 → ITM-608~614）

子代理 B 的 S1-S18 全部闭合，0 条新疑点（PASS）。

---

## 段 4：优先级判定

| ID | 危害 | 复杂度 | 优先级 | 理由 |
|:--:|:--:|:--:|:--:|------|
| ITM-605 | 中（命令重试配置错误消息不指向 RetryBackoff，调用方误判为框架 bug） | 易（Resilience.cs:75 前加 5 行守卫） | **P2** | ITM-603 修了一半，需补全命令路径 |
| ITM-606 | 中（错误消息误导，调用方查 `_conn == tx.Connection` 找不到原因） | 易（交换 DataSession.UseTransaction 两段顺序） | **P2** | ITM-596 精确消息被遮蔽，需调整检查顺序 |
| ITM-607 | 中（[ConcurrencyCheck] 在基类时静默不报多令牌，生成器与 BindUpdate 冲突） | 易（改 `type.GetMembers()` → `EnumerateMappedProperties(type)`，~2 行） | **P2** | 与 ITM-587/588/601 同根因类（基类链口径漂移），漏改项 |
| ITM-608 | 低（C1 控制字符威胁面接近 0，调用点全编译期常量） | 中（边界扩展 + 单测） | **P3** | 防御性增强 |
| ITM-609 | 低（public API 表面治理） | 易（加 `[EditorBrowsable(Never)]`） | **P3** | API 卫生 |
| ITM-610 | 低（并发复用不符契约，实际不会发生） | 中（改 int + Interlocked 或 XMLDOC 显式） | **P3** | 文档化契约 |
| ITM-611 | 低（成功路径 Dispose 失败罕见） | 中（需引入 logger 字段） | **P3** | ITM-595 已登记，可选增强 |
| ITM-612 | 低（大型项目首次编译 CPU 浪费，非正确性） | 易（Interlocked.CompareExchange） | **P3** | 性能优化 |
| ITM-613 | 低（当前 FK 不生成 DDL，运行时无影响） | 中（强约束机制需设计） | **P3** | 防御登记，3.0 启用 FK DDL 前处理 |
| ITM-614 | 低（复合多问题主键罕见） | 中（reason 聚合多原因） | **P3** | UX 改进 |

**总计**：0 P0 + 0 P1 + 3 P2 + 7 P3

---

## 段 5：方法论自省

**工具选择**：
- 结构发现：2 子代理并行（A=改动 src 逐行 / B=测试同型修复正确性）
- 跨片不变式：主线程 grep + Read（IdentifierSafety 接入 / EnumerateMappedProperties 口径 / CompilationStartAction 结构）
- 探针实证：本轮无新增探针——3 条 P2 均为 Read 源码 + 路径对比直接确认

**覆盖度证据**：
- 子代理 A 覆盖度自报 13 文件 100% 勾销
- 子代理 B 覆盖度自报 9 测试代码全 Read + 全 test/ 残留扫描 10 候选全判定
- 主线程跨片核查 5 项不变式

**反证率较高**：子代理 A 10 条疑点中 3 条确认 + 7 条降级——本轮"半完成项"主题使子代理更敏感于"修了一半"的模式，主线程反证确认了 3 条真缺陷。

**回归风险评估**：
- 18 项整改中 **15 项完整闭环**（无回归）
- **3 项半完成**（ITM-603/596/601 → ITM-605/606/607）——需本轮行动项收口
- 测试代码同型修复 9 处全部正确（PASS）

**本轮关键教训**：整改时"同根因类"应一次性扫描所有同型代码路径，而非仅修报告点名的特定位置。ITM-601 整改时 PALORM013 同型代码（PalORMAnalyzer.cs:265）被遗漏——若当时 grep `type.GetMembers().OfType<IPropertySymbol>` 全文，会发现还有 1 处需统一。

---

## 段 6：指标（引用 metrics.md）

本轮指标将追加至 `.ai/review/metrics.md`：

- 发现总数：10（3 P2 + 7 P3）
- 探针数：0（3 P2 均为 Read 源码直接确认）
- 证伪数：0
- 下沉数：1（ITM-607 同根因类——基类链判定口径漂移，ITM-587/588/601/607 已 4 例，应下沉为 grep 机械防线：`grep "type.GetMembers().OfType<IPropertySymbol>" src/PalORM.SourceGen/PalORMAnalyzer.cs` 应 0 残留）

**与上轮对比**：
- 835591f（前序）：0 P0 + 2 P1 + 3 P2 + 13 P3，0 新增逃逸，0 复发
- f6089d5（本轮）：0 P0 + 0 P1 + 3 P2 + 7 P3，0 新增逃逸，**0 复发但发现 3 条半完成项**

**复发根因类登记**：基类链判定口径漂移（ITM-587/588/601 + **ITM-607**）已是第 4 例，建议下沉为 grep 机械防线。

---

## 段 7：整改回归验证结论

本轮基线 `f6089d5` 是 `835591f` 轮 18 项整改完成后的下一轮。整改改动 27 文件 710 行：

| 上轮 ITM | 本轮复查结果 |
|---------|------------|
| ITM-587/588/601（基类链统一） | ⚠️ **部分回归**——PALORM013 同型漏改（ITM-607） |
| ITM-589（PALORM022 扩展） | ✅ 完整闭环 + 6 处测试同型修复全部正确 |
| ITM-590（CompilationStartAction 缓存） | ✅ 完整闭环（ITM-612 仅并发性能优化登记） |
| ITM-591/604（Obsolete 口径） | ✅ 完整闭环 |
| ITM-592（StoredProcBuilder._executed） | ✅ 完整闭环（ITM-610 仅并发契约文档化） |
| ITM-593（IdentifierSafety 控制字符） | ✅ 完整闭环（ITM-608/609 仅边界扩展登记） |
| **ITM-596（UseTransaction Connection null）** | ⚠️ **半完成**——精确消息被 ReferenceEquals 遮蔽（ITM-606） |
| ITM-598/599/600/602/595（文档注释） | ✅ 完整闭环 |
| **ITM-603（RetryBackoff 负值）** | ⚠️ **半完成**——只覆盖 CreateAsync，未覆盖 ResilienceExecutor（ITM-605） |

**18 项整改 → 15 项完整闭环 + 3 项半完成**。半完成项需本轮 P2 行动项收口。

---

## 段 8：本轮指标记录

```markdown
| 2026-07-19 | f6089d5 | 全量 review（v2.3 引擎·整改回归验证轮） | 0 | 0 | 3 | 7 | 0 新增 | 0 | 0 | 待收口（3 半完成项：ITM-605 命令路径 RetryBackoff / ITM-606 UseTransaction 顺序 / ITM-607 PALORM013 基类链漏改） |
```

**指标解读**：
- **0 P0/P1**：连续 5 轮无 P0/P1，整改质量稳定
- **3 P2**：均为上轮整改的半完成项（非新缺陷）——反映"同根因类一次性扫描"的整改纪律需强化
- **0 复发**：基类链判定口径漂移根因类新增 ITM-607（第 4 例），但属"同根因漏改"非"根因类复发"
- **0 证伪**：3 P2 均为 Read 源码 + 路径对比直接确认，无需探针

---

## 结论

**REQUEST_CHANGES** — 3 项 P2 半完成项需本迭代内修复（ITM-605/606/607），7 项 P3 为存量边界登记或设计取舍。

**本轮关键发现**：
1. **整改半完成项 3 条**（ITM-605/606/607）——上轮 18 项整改中 15 项完整闭环，3 项漏改/遮蔽。修复成本均低（~2-5 行/项）。
2. **基类链判定口径漂移根因类第 4 例**（ITM-607）——建议下沉 grep 机械防线：`grep "type.GetMembers().OfType<IPropertySymbol>" src/PalORM.SourceGen/PalORMAnalyzer.cs` 应 0 残留。
3. **测试代码同型修复 9 处全部正确**（子代理 B PASS 判定，0 残留漏修）。
4. **0 新增逃逸 / 0 复发**——18 项整改未引入新缺陷，仅暴露 3 条半完成项。

**后续建议**：
- 立即修复 ITM-605/606/607（~10 行总改动）
- 下沉 grep 机械防线到 gate-check.sh：扫描 PalORMAnalyzer 内 `type.GetMembers().OfType<IPropertySymbol>` 残留
- 强化整改纪律：同根因类（如基类链判定）整改时必须 grep 全文所有同型代码路径，不只修报告点名位置

报告完毕。
