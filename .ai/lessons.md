# PalORM AI 规范化系统 v7.0

> **一句话定义**：AI 在 PalORM 上做任何代码变更前必读的入口文件。
> 本文件是唯一的 AI 规范真源——其他文件（`.editorconfig` / `docs/编码规范.md` / `PR 模板`）引用本文件的规则编号。
>
> **版本历史**：v4.0（修复缺陷）→ v5.0（精炼缺陷）→ v6.0（终极整合）→ v7.0（规范化整合）

---

## I. AI 协作 6 条铁律（不可违反）

| # | 铁律 | 违反后果 |
|---|------|---------|
| 1 | **同类改动批量完成后一次构建**（不是每次 Edit 后都跑 `--no-incremental`） | 增量缓存掩盖断裂或过度等待 |
| 2 | **批量 Edit 前必 Read** | 单行类 Edit 导致 CS1585 |
| 3 | **SuppressMessage 必附 Justification**（用「」替代 ASCII `"`） | CS1003 编译错误 |
| 4 | **Partial 拆分每个文件 using 独立确定** | CS0246/CS0103 |
| 5 | **C# 插值字符串不可简单 split 拆行** | CS1513 对象初始化器断裂 |
| 6 | **测试凭据不用 Password=** | S2068 硬编码凭据 |

### 构建验证时机

| 时机 | 命令 | 用途 |
|------|------|------|
| 同类改动内（如 D 批次 5 处删除）| 不构建 | 继续批量改动 |
| 跨类别切换（D→M 或 M→R）| `dotnet build` | 确认上批无误 |
| 快照类改动（SourceGen emit）| 先 `PALORM_UPDATE_SNAPSHOTS=1 dotnet run` 前置确认基线 | 避免事后快照偏离 |
| 最终提交前 | `dotnet build --no-incremental` | 全量重建 |
| 技术债扫描 | `bash scripts/tech-debt-scan.sh` | 12 类检查一键执行 |

---

## II. AI 缺陷登记（21 个）

### 阶段 A：修复缺陷（Sonar 4 轮）

| # | 缺陷 | 教训 |
|---|------|------|
| A1 | 循环触发 | 每次 Edit 后构建验证 |
| A2 | 抑制机制误用 | `[SuppressMessage]` 才有效 |
| A3 | Edit 锚点漂移 | 先多行展开再 Edit |
| A4 | 字符串引号陷阱 | 用「」替代 ASCII `"` |
| A5 | 规则 ID 不精确 | 同型族同时抑制 |
| A6 | stash pop 副作用 | 用 `git diff HEAD` 验证 |
| A7 | 并行化不足 | Agent 并行调研 |

### 阶段 B：精炼缺陷（D1-R4）

| # | 缺陷 | 教训 |
|---|------|------|
| B1 | Python 行号切割 | 切割前必须重新 Read |
| B2 | static virtual 调用限制 | CS8926——三 Provider 相同方法无法上提 |
| B3 | Partial using 丢失 | 按自身依赖独立确定 |
| B4 | Partial XML 注释断裂 | 方法注释与方法声明一起搬运 |
| B5 | 幽灵方法签名 | 切割后 grep 核对完整性 |
| B6 | 方法重复提取 | 画行号范围图 |
| B7 | stash pop 再次 | `git diff HEAD` |
| B8 | 源生成器 emit 改动后 obj 缓存陷阱（v3.1） | emit 模板改动后必须清 `obj/` + `bin/`，否则增量构建复用旧 emit 导致运行时 cast 失败 NRE。详见 X 章 `9a5c5a7`。SOP：① 改 RowFactoryEmitter/CommandFactoryEmitter/RegistryEmitter/MigrationEmitter 任一 emit 模板 → ② `rm -rf src/*/obj src/*/bin test/*/obj test/*/bin` → ③ 全量 `dotnet build --no-incremental` |
| B9 | 方案文字优化项被合理简化（v3.1 审查发现） | 优化方案文字（plan 文档）与实施代码会有偏差：核心性能路径必须严格对齐，辅助/锦上添花的优化允许基于测量数据（S3 反向验证）取舍。**陷阱**：方案文字本身可能存在自相矛盾（如「保留接口 + 标 Obsolete」），实施时不应照搬。**SOP**：① 实施后做代码审查对比方案文字 → ② 列出偏差 → ③ 每项偏差给出 S3 依据（数据/IL 等价/JIT 内联） → ④ 在方案文档补「实施差异说明」章节承认偏差。详见 `docs/v3.1-performance-plan.md` 实施差异 1/2/3。 |
| B10 | 方案调研可能基于不完整数据（v4.0 实施时发现） | v4.0 方案基于 4 路并行 Agent 调研，但实施时发现多个高风险项被误判为瓶颈：① 优化 C（QueryBuilder O(N²)）实际只占 QueryAll 4.73ms 的 0.02%；② 源生成器 emit 工程化 4 子项均有现有机制覆盖；③ Convert.ChangeType 在 AOT 下经评估确认安全（走 IConvertible 接口，不依赖反射）。**陷阱**：调研 Agent 可能从代码模式推断出"理论瓶颈"，但缺乏运行时数据验证。**SOP**：① 实施前先用 benchmark 验证方案声称的瓶颈是否属实 → ② 评估复杂度/收益比，YAGNI 跳过理论瓶颈 → ③ 在方案文档补「评估后跳过」章节说明理由。详见 `docs/v4.0-improvement-plan.md` 与 `CHANGELOG.md` v4.0.0 「评估后跳过的方案项」表。 |
| B11 | review Agent 推理不验证代码就下结论（v4.0 全量评审） | review R1 声称 BulkMerge 默认键实体 operationOwner 坍缩为 null→EnterOperation 失败。实际代码 `operationOwner ?? operation.Owner` 中 operationOwner 参数（非 null）优先生效——推理链断裂但未读代码验证。R6 声称 PG 类型校验在 ProbeBinder 之后，实际已在之前。**陷阱**：并行 review 子代理对复杂调用链的跨方法推理易出错。**SOP**：review 报告的 P0/P1 发现必须附带探针验证（写测试复现或 grep 确认），不能仅凭推理定级。 |
| B12 | Edit 替换 emit 代码时误删相邻行（v4.0 R11 修复） | 修 MigrationEmitter nullable 条件时，Edit 的 old_string 含了 defaultClause/primaryKey/columns.Add 三行——new_string 只保留了 nullable 行，导致 emit 生成的 DDL 列为空。**陷阱**：Edit 工具按精确匹配替换，old_string 越大越容易误删相邻代码。**SOP**：emit 代码 Edit 时 old_string 只含目标行 ± 1 行上下文，不贪多。改后必须 `dotnet build` + 快照比对验证 emit 产物完整。 |
| B13 | 门禁脚本正则匹配注释内容（v4.0 G24 修复） | G24 perl 正则 `\bawait\s+` 统计 await 数量，但 GridReader.cs 注释中的 "await 会导致...await using 卡死" 被误匹配，导致 await=12 > ConfigureAwait=11 误报。**陷阱**：正则不区分代码与注释/XML doc。**SOP**：门禁脚本的正则统计前必须先剥离 `///` XML doc 和 `//` 行注释（`s{^\s*///.*$}{}gms; s{//.*$}{}gm;`）。 |
| B14 | 测试计数口径冲突——声明数 vs 通过数（v4.0 badge/D10） | README badge 写 427（源码 [Test] 声明总数），tech-debt #9 检查 badge == dotnet test 通过数（419，8 个外部 DB 失败）。D10 检查文档计数 == 源码标注数。三者口径不一致导致连环 FAIL。**陷阱**：同一数字（"测试数"）在不同检查中有不同口径。**SOP**：全仓库统一口径 = "CI 全环境实际通过数"（419）。外部 DB 环境依赖测试在文档中标注"需外部 DB"但不计入 badge 总数。D10 对 Integration.Tests 允许文档 ≤ 源码标注。 |

---

## III. SonarAnalyzer 规则层级

> 真源在 `.editorconfig`；本表是快速参考。

### P0 安全/正确性 → error（编译阻断）

| 规则 | 说明 |
|------|------|
| S2068 | 硬编码凭据 |
| S6966 | 同步 ADO.NET（改 await） |
| S5034 | ValueTask 双消费 |
| S108 | 空块 |
| S1186 | 空方法 |
| S4144 | 方法同体 |

### P1 设计/可读性 → error

| 规则 | 说明 |
|------|------|
| S3776 | 认知复杂度 > 15 |
| S107 | 参数 > 7 |
| S927 | 参数名匹配接口 |
| S2681 | 单行 if/foreach |
| S125 | 注释代码 |
| S1066 | 合并 if |
| S1994 | for stop 变量 |
| S2189 | incrementer 不被测试 |

### P2 风格 → suggestion

| 规则 | 说明 |
|------|------|
| IDE0007 | var 替代 |
| IDE0022 | 表达式主体 |
| IDE0005 | unused using |
| IDE0060 | 未使用参数 |
| IDE1006 | 命名前缀 _ |

### 豁免（设计本意）
S101 / S1133 / S2077 / S3236 / S6667

### 测试降级
S2094 / S1481 / S6444

### 同型规则族（必须同时抑制）
S127/S1994/S2189（循环变量）/ S108/S1186（空块）

### 不可抑制 P0（必须改代码）
S2068（凭据）/ S6966（await）/ S108（空块无注释）

---

## IV. 规范化 SOP（4 阶段）

### 阶段 1：调研
Agent 并行调研 → 文件清单 + 职责评估 + 评级 → ROI 排序

### 阶段 2：执行
D 删除（低风险）→ M 合并（低）→ R 拆分（中高，每个独立提交+测试）

### 阶段 3：Partial 拆分（10 步）
读文件 → grep 签名 → 画范围图 → Python 切割 → 删原段 → 独立 using → 补 XML → 查悬空 → 构建 → 测试

### 阶段 4：验证
`--no-incremental` 构建 → 全量测试 → 快照更新 → grep 残留

---

## V. 精炼决策矩阵

| CC | 策略 | 例外 |
|----|------|------|
| ≤15 | 通过 | — |
| 16-20 | 拆分 | 异步生命周期 → Suppress |
| 21-30 | 拆分 | 并发契约 → Suppress |
| >30 | 必拆 | 无例外 |

| 类型 | 标准 | 策略 |
|------|------|------|
| 死代码 | Obsolete + 零消费 | 删除 |
| 重复 | 逐字 ≥ 3 处 | 抽 helper |
| God Object | > 800 行 | 拆 partial |
| 过度抽象 | 唯一调用方是自身 | 内联 |
| 过细 | < 50 行 + 单方法 | 合并 |

### 终止条件
无 Obsolete / 无预留 null / 无重复 ≥ 3 / 无 God Object / 无死代码 / 构建测试零回归

---

## VI. 技术债扫描 SOP（每季度）

12 类检查（每项零残留）：
1. `[Obsolete]` 残留
2. TODO/HACK/FIXME
3. SuppressMessage 无 Justification
4. Console.WriteLine 在 src/
5. 空 catch 无注释
6. 超长行 > 180（src/）
7. unused using（IDE0005）
8. 测试用例数对照 README
9. SuppressMessage 总数
10. SourceGen 超长行（允许）
11. static 可变状态（确认线程安全）
12. 占位诊断（PALORM006/007 已删）

---

## VII. 反模式登记（RP-1 ~ RP-14）

| # | 反模式 | 状态 |
|---|--------|------|
| RP-1 | 巨型 Lambda | 已根治 |
| RP-2 | 双路径内联 | 部分（v3.0 IDialectStrategy） |
| RP-3 | 硬编码凭据 | 已根治 |
| RP-4 | 同步 ADO.NET | 已根治 |
| RP-5 | 空块陷阱 | 已根治 |
| RP-6 | Edit 锚点漂移 | 已登记 |
| RP-7 | 抑制机制误用 | 已根治 |
| RP-8 | 字符串引号陷阱 | 已登记 |
| RP-9 | 规则 ID 不精确 | 已登记 |
| RP-10 | 参数膨胀 | 已根治 |
| RP-11 | 属性遮蔽 | 已根治 |
| RP-12 | 嵌套三元 | 已根治 |
| RP-13 | 硬编码 fallback | 已根治 |
| RP-14 | Bulk 重复 | 已根治 |

---

## VIII. AI 启动检查清单

> AI 会话开始时自动加载本节，确认当前项目状态。

```
1. git status——工作树清洁？
2. dotnet build -c Debug——0 警告 0 错误？
3. SonarAnalyzer P0+P1 规则——全 error？
4. .editorconfig——39 条规则配置完整？
5. .ai/lessons.md——已读最新版？
6. PR 模板——已更新最新检查项？
7. CHANGELOG——当前版本号（4.0.0）？
8. 测试用例数——README badge 与实际一致？
9. 【如改动 src/PalORM.SourceGen/*.Emitter.cs】obj 缓存陷阱——改 emit 模板后必须清 obj/bin 再全量构建（见 II.B8）
10. bash scripts/test-gate.sh——T1-T10 测试规范门禁通过？
```

---

## IX. 交叉引用

| 文件 | 用途 | 与本文件的关系 |
|------|------|--------------|
| `.editorconfig` | Sonar 规则严重性配置 | **规则真源**——本文件 III 章引用 |
| `docs/编码规范.md` 第 18 节 | 规范化规则文本 | 本文件 III/V 章的文档化 |
| `.github/PULL_REQUEST_TEMPLATE.md` | PR 检查清单 | 本文件 VIII 章的流程化 |
| `.ai/gate/prompt.md` | 门禁系统 | G1-G28 对应本文件 P0+P1 规则 |
| `.ai/refine/prompt.md` | 精炼系统 | 24 项操作矩阵对应本文件 V 章 |
| `.ai/review/prompt.md` | 审计系统 | 误判库对应本文件 II 章 |
| `CONTRIBUTING.md` | 贡献指南 | 引用本文件作为代码规范依据 |
| `CHANGELOG.md` | 变更日志 | 版本变更时同步本文件版本号 |

---

## X. 案例引用

| 反模式/缺陷 | commit | 文件 |
|-------------|--------|------|
| RP-1 巨型 Lambda | `bad26c8` | PalORMAnalyzer.cs |
| RP-10 参数膨胀 | `6057845` | QueryBuilder.cs |
| RP-11 属性遮蔽 | `07f8847` | PalORM_Runtime.cs |
| RP-14 Bulk 重复 | `3f77dcf` | BulkOperationFramework.cs |
| B1 Python 切割 | `11f28f6` | DataSession partial |
| B2 static virtual | `fa92996` | IDbProvider CS8926 |
| B8 obj 缓存陷阱 | `9a5c5a7` | RowFactoryEmitter emit 改动——Func 委托迁移 |
| B9 方案合理简化 | `e58f414` / `本会话` | v3.1-performance-plan.md 实施差异说明 + 第二次基准复现 |
| B10 方案调研误判 | `本会话 v4.0` | v4.0-improvement-plan.md + CHANGELOG v4.0.0 评估后跳过表 |
| B11 review 推理不验证 | `860b09d` | review R1/R6 误判→补测试验证代码正确 |
| B12 Edit 误删 emit 行 | `a4a9c7c` | MigrationEmitter R11 修复 defaultClause/primaryKey 误删 |
| B13 门禁正则匹配注释 | `860b09d` | G24 perl await 统计含注释→剥离注释后统计 |
| B14 测试计数口径冲突 | `5788eeb` | badge 419/427/D10 三口径统一 |
| 占位诊断删除 | `8781357` | PalORMAnalyzer PALORM006/007 |
| 规范化 | `3450e18` | CHANGELOG + CONTRIBUTING |
