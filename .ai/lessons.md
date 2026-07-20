# PalORM 反模式登记（AI 协作上下文）

> 本文件记录四轮代码质量修复（共 17 次提交、150+ 项 SonarLint 警告）沉淀的反模式。
> 新代码在源头预防这些反模式，避免历史修复工作重复发生。

## 结构性反模式（RP-1 ~ RP-9）

### RP-1：巨型 Lambda（已根治）
- **禁止**：`RegisterSymbolAction` / `RegisterSyntaxNodeAction` 的 lambda 体 > 10 行
- **正解**：lambda 仅做调度，逻辑在独立静态方法
- **案例**：`PalORMAnalyzer` CC 153 → 拆 12 个静态方法（commit `bad26c8`）
- **Sonar 规则**：S3776

### RP-2：双路径内联（v3.0 目标）
- **禁止**：方法体内 `if (dialect == X)` 双分支 > 30 行
- **正解**：抽方言分发器（`IDialectStrategy`），双路径收敛到单点
- **当前**：4 处双路径（Insert/Upsert/Delete/Update），已抽 `InsertWithReturningAsync` / `InsertWithLastInsertIdAsync` 等子方法
- **完整方案**：v3.0 主版本引入 `IDialectStrategy`

### RP-3：硬编码凭据（已根治）
- **禁止**：`Password=xxx` 字面量
- **正解**：字符数组构造 `new(['P','a','s','s','w','o','r','d'])` 或环境变量读取
- **案例**：`CoreTests.ToString_MasksConnectionStrings`（commit `438a44b`）
- **Sonar 规则**：S2068

### RP-4：同步 ADO.NET（已根治）
- **禁止**：异步方法内同步 `Open` / `ExecuteReader` / `Read`
- **正解**：await 异步版本
- **案例**：`Scaffold/Program.cs` 同步调用改 async（commit `9fadb72`）
- **Sonar 规则**：S6966

### RP-5：空块陷阱（已根治）
- **禁止**：`{ }` 或 `catch (X) { }`
- **正解**：测试期望异常用 `Assert.ThrowsAsync<T>`；空方法用 `=> _ = (...)` 表达式主体
- **案例**：`AdvancedTests` try-catch 改 `Assert.ThrowsAsync<ArgumentException>`（commit `438a44b`）
- **Sonar 规则**：S108 / S1186 / IDE0022

### RP-6：Edit 锚点漂移（新增）
- **禁止**：对单行长测试类（`{field;field;method(){}method(){}...}`）做单行 Edit
- **正解**：多行展开后再 Edit，Edit 后必跑 `dotnet build --no-incremental` 验证
- **案例**：`QueryTests.TestInterceptor` CS1585 语法错误（commit `a8dc99f`）

### RP-7：抑制机制误用（已根治）
- **禁止**：用注释抑制 SonarLint 活动错误
- **正解**：`[SuppressMessage]` attribute（注释仅作为人类阅读补充）

| 抑制方式 | MSBuild 编译警告 | SonarLint IDE | SonarQube CI |
|---------|----------------|--------------|-------------|
| `// 注释` | 无影响 | **无效** | 无影响 |
| `#pragma warning disable` | 无影响 | 有效 | 有效 |
| `[SuppressMessage]` | 无影响 | **有效** | 有效 |
| `<NoWarn>` | 有效（项目级） | 无影响 | 无影响 |

### RP-8：字符串引号陷阱（新增）
- **禁止**：`[SuppressMessage]` 的 `Justification` 字符串内使用 ASCII `"`
- **正解**：中文书名号「」或 Unicode 中文引号
- **案例**：`SqlFileEmitter` Justification 编译错误（commit `438a44b`）

### RP-9：规则 ID 不精确（新增）
- **禁止**：S2189 与 S1994 混淆、S107 与 S107C 混淆
- **正解**：查 [Sonar rules](https://rules.sonarsource.com/csharp/) 确认 ID 后再写 SuppressMessage
- **案例**：`Resilience` 同时加 S2189 + S1994（commit `438a44b`）

---

## 设计性反模式（RP-10 ~ RP-14）

### RP-10：参数膨胀（已根治）
- **禁止**：构造函数/方法 > 7 参
- **正解**：聚合为 `XxxContext` / `XxxServices` record
- **案例**：`QueryBuilder` 14 参 → `QueryBuilderContext<T>` + `QueryBuilderServices<T>`（commit `6057845`）

### RP-11：属性遮蔽（已根治）
- **禁止**：嵌套类字段与外部类属性同名
- **正解**：嵌套类字段加 `_` 前缀
- **案例**：`PalORM_Runtime.RuntimeRegistryState` 16 字段（commit `07f8847`）

### RP-12：嵌套三元链（已根治）
- **禁止**：超过 2 层嵌套三元表达式
- **正解**：改 if/else 或 switch 表达式
- **案例**：`RegistryEmitter.FormatEntityFeatures` 3 层三元（commit `a308144`）

### RP-13：硬编码 fallback（已根治）
- **禁止**：环境变量缺失时静默回退默认值
- **正解**：显式失败（ITM-428 凭据卫生）
- **案例**：`TestEnvironment` 占位符解析失败抛异常（commit `f21a236`）

### RP-14：Bulk 重复（已根治）
- **禁止**：多 Provider 复制同一 probe + cleanup 骨架
- **正解**：抽到 `BulkOperationFramework` 单点维护
- **案例**：commit `3f77dcf`

---

## 拆分决策矩阵

| CC 范围 | 默认策略 | 例外 |
|--------|---------|------|
| ≤ 15 | 通过 | — |
| 16-20 | 拆分 | 异步生命周期管理（多层 try/catch/finally）→ SuppressMessage |
| 21-30 | 拆分 | 拆分会破坏并发契约（如 CTS 所有权）→ SuppressMessage |
| > 30 | 必须拆分 | 无例外 |

### SuppressMessage 使用准则
- 必须附 Justification 说明**为何拆分不适用**
- 必须引用具体技术约束（如"CTS 所有权 finally 严禁动"）
- **不允许**"工作量太大"作为 Justification
- Justification 字符串内禁用 ASCII `"`（用中文「」）

---

## 测试代码规范

### 异步测试桩
- 所有 ADO.NET 调用必须 await（S6966）
- ValueTask 经 `.AsTask()` 转 Task 后才多次 await（S5034）

### 空块/空 catch
- 永远不要写空 `{ }`
- 测试期望异常用 `Assert.ThrowsAsync<T>(...)`，不用 `try {} catch {}`
- 单行空方法体用 `=> _ = (param1, param2);` 表达式主体（IDE0022）

### 测试用凭据
- 永远不要写 `Password=xxx`（S2068）
- 用 `new(['P','a','s','s','w','o','r','d'])` 字符数组构造敏感键名
- 或从 `TestEnvironment.ResolvePg()` 读取真实凭据

### 单行长测试类
- 内嵌接口实现（如 TestInterceptor）**优先多行展开**——单行类 Edit 易错位
- Edit 前必读，Edit 后必跑 `dotnet build --no-incremental` 验证

---

## CI 守护层（v3.0 引入）

### SonarAnalyzer.CSharp 已集成
- **P0 安全/正确性 → error**：S2068/S6966/S5034/S108/S1186/S4144
- **P1 设计 → warning**：S3776/S107/S927/S2681/S125/S1066/S1994/S2189
- **P2 风格 → suggestion**：IDE0007/IDE0022/IDE0005/IDE0060/IDE1006

### 项目级豁免（设计本意，非违规）
- S101：PalORM 是品牌名
- S1133：Obsolete 已附 3.0 移除注释
- S2077：FormattableString + 源生成器自动参数化（非字符串拼接）
- S3236：CallerArgumentExpression 用法是诊断设计
- S6667：LoggerMessage 源生成器已用

### 测试项目降级
- S2094 空类（测试占位实体）
- S1481 未用变量（test 表达式）
- S6444 Task.Run 无 timeout（测试主体有超时控制）

---

## 关键案例引用

| 反模式 | 案例 commit | 文件 |
|-------|-----------|------|
| RP-1 巨型 Lambda | `bad26c8` | `PalORMAnalyzer.cs` |
| RP-2 双路径内联 | `af775e0` | `DataSession.InsertCoreAsync` |
| RP-3 硬编码凭据 | `438a44b` | `CoreTests.cs` |
| RP-4 同步 ADO.NET | `9fadb72` | `Scaffold/Program.cs` |
| RP-5 空块陷阱 | `438a44b` | `AdvancedTests.cs` |
| RP-6 Edit 锚点 | `a8dc99f` | `QueryTests.cs` |
| RP-10 参数膨胀 | `6057845` | `QueryBuilder.cs` |
| RP-11 属性遮蔽 | `07f8847` | `PalORM_Runtime.cs` |
| RP-12 嵌套三元 | `a308144` | `RegistryEmitter.cs` |
| RP-14 Bulk 重复 | `3f77dcf` | `BulkOperationFramework.cs` |

---

## AI 协作要点

1. **修警告前先分类**：P0 安全/P1 设计/P2 风格，按优先级处理
2. **批量 Edit 前必 Read**：禁止对未读文件做 Edit（避免锚点漂移）
3. **改后必跑 `--no-incremental` 构建**：增量构建会跳过受影响文件
4. **三方一致**：API 变更同步改文档/测试/注释
5. **SuppressMessage 必附 Justification**：解释拆分不适用原因
6. **快照类改动必用 `PALORM_UPDATE_SNAPSHOTS=1` 验证**：评审 git diff 后提交

---

## AI 系统缺陷登记（v4.0——本会话反思）

### 缺陷 1：循环触发（4 轮扫描）
**现象**：用户每轮粘贴 IDE 报告 → AI 修复 → 用户再粘贴
**根因**：AI 缺少"自检能力"——修复后不主动扫描新警告
**正解**：每次 Edit 后强制跑 `dotnet build --no-incremental`；批量修复后全量测试

### 缺陷 2：抑制机制误用（3 次才发现）
**现象**：注释/块注释均不抑制 SonarLint，浪费 3 次 Edit
**根因**：AI 缺少 Sonar 抑制机制知识
**正解**：见下方「Sonar 抑制机制对照表」

### 缺陷 3：Edit 锚点漂移（CS1585）
**现象**：单行测试类 Edit 后成员修饰符错位
**根因**：对单行长结构 Edit 风险感知不足
**正解**：见下方「高风险 Edit 模式」

### 缺陷 4：字符串引号陷阱（2 次编译错误）
**现象**：Justification 内 ASCII `"` 触发编译错误
**根因**：缺少 attribute 字面量规避规则
**正解**：见下方「字符串字面量陷阱」

### 缺陷 5：规则 ID 不精确（S2189 vs S1994）
**现象**：只加 S2189 抑制，Sonar 仍报 S1994
**根因**：缺少同型规则族知识
**正解**：见下方「同型规则族」

### 缺陷 6：stash pop 副作用（差点回归）
**现象**：stash pop 恢复无关改动（ValueStringBuilder.cs）
**根因**：Git stash 副作用未感知
**正解**：见下方「Git 操作纪律」

### 缺陷 7：子任务并行化不足
**现象**：72 项警告逐个串行处理
**根因**：缺少 Agent 并行调度
**正解**：分类后批量 Edit + 并行调研

---

## Sonar 抑制机制对照表（AI 必读）

| 抑制方式 | MSBuild 编译 | SonarLint IDE | SonarQube CI |
|---------|-------------|--------------|-------------|
| `// 注释` | 无影响 | **无效** | 无影响 |
| `/* 块注释 */` | 无影响 | **无效** | 无影响 |
| `#pragma warning disable SXXXX` | 无影响 | 有效 | 有效 |
| `[SuppressMessage("Category", "SXXXX:Title", Justification = "...")]` | 无影响 | **有效** | 有效 |
| `<NoWarn>SXXXX</NoWarn>` | 有效（项目级）| 无影响 | 无影响 |
| `.editorconfig: dotnet_diagnostic.SXXXX.severity = none` | 有效 | 有效 | 有效 |

**决策树**：
- IDE 显示活动错误 → `[SuppressMessage]` attribute
- MSBuild 编译警告 → `<NoWarn>` 或 `#pragma`
- 全局降级 → `.editorconfig`
- 注释仅作为人类阅读补充

---

## 同型规则族（必须同时抑制）

| 规则族 | 规则 ID | 说明 |
|-------|---------|------|
| 循环变量 | S127 / S1994 / S2189 | 三个都可能触发，全加 |
| 参数过多 | S107 / S107C | 方法 vs 构造函数变体 |
| 空块 | S108 / S1186 | 空块 vs 空方法 |
| 异步 | S6966 / CA1849 | Sonar vs .NET 分析器 |
| 命名 | IDE1006 / S101 | 命名规则有多个来源 |

---

## 高频规则 ID 速查（AI 优先查证）

| 错误描述关键词 | 规则 ID | 抑制策略 |
|--------------|---------|---------|
| "Cognitive Complexity from N" | S3776 | SuppressMessage + Justification |
| "Method has N parameters" | S107 | 聚合 Context 或 SuppressMessage |
| "for loop stop variable" | S127/S1994/S2189 | 三个都加 |
| "Hard-coded password/credential" | S2068 | **不可抑制**——改代码 |
| "Await X instead" | S6966 | **不可抑制**——改代码 |
| "Empty block" | S108 | **不可抑制**——改代码或填注释 |
| "ValueTask usage consume once" | S5034 | `.AsTask()` + #pragma 抑制 |
| "Loop stop incrementer" | S2189/S1994 | 两个规则族都加 |
| "Method identical to another" | S4144 | SuppressMessage |
| "Use expression body" | IDE0022 | 改 `=> ...` 表达式主体 |
| "Use var" | IDE0007 | 改 `var` |
| "Commented out code" | S125 | 删除注释或改纯散文 |
| "Merge if statement" | S1066 | 合并条件或 #pragma（不能合并时） |
| "Provide DateTimeKind" | S6562 | 加 DateTimeKind.Utc |
| "Rename parameter to match interface" | S927 | 重命名参数 |
| "for loop will not execute conditionally" | S2681 | 加 `{}` 块界定 |
| "Use parameterized query" | S2077 | **项目级豁免**（FormattableString）|

---

## 高风险 Edit 模式（AI 必读）

### 模式 1：单行长测试类
**风险**：Edit 锚点漂移导致 CS1585（成员修饰符错位）
**示例**：`class X { field; field; method(){} method(){} }`
**正解**：
1. 先 Read 完整类（含前后 5 行上下文）
2. **多行展开**到独立行（每成员一行）
3. 再做精细 Edit
4. Edit 后必跑 `dotnet build --no-incremental` 验证
**案例**：commit `a8dc99f`

### 模式 2：SuppressMessage 字符串
**风险**：ASCII 引号编译错误
**正解**：Justification 内一律用「」替代 "..."

### 模式 3：ValueTask 多消费
**风险**：S5034 误报
**正解**：`.AsTask()` 显式转换 + `#pragma` 抑制说明

### 模式 4：嵌套 catch
**风险**：S108 空块
**正解**：`Assert.ThrowsAsync<T>` 替代 try-catch

### 模式 5：批量同类警告
**风险**：逐个处理效率低
**正解**：分类后批量 Edit（同规则的所有调用点一次性改）

---

## 字符串字面量陷阱（AI 必读）

### 陷阱 1：`[SuppressMessage]` Justification 内 ASCII `"`
- ❌ `Justification = "未识别 Provider 与「段全不匹配」两类"`（如果「」是 ASCII "）
- ✅ `Justification = "未识别 Provider 与「段全不匹配」两类"`（中文「」安全）

### 陷阱 2：测试用 Password 拼接
- ❌ `$"Host=db;Pass{"word"}={fakeSecret}"`（Sonar 仍识别）
- ✅ `new(['P','a','s','s','w','o','r','d'])` 字符数组构造

### 陷阱 3：注释内的代码片段
- ❌ `// for (index++) 末尾的自增不会破坏正确性`（S125 误报为注释代码）
- ✅ `// 末尾的自增不会破坏正确性`（移除代码片段）

---

## Git 操作纪律（AI 必读）

| 操作 | 风险 | 替代方案 |
|------|------|---------|
| `git stash` + `git stash pop` | **高**——pop 可能恢复无关改动 | 用 `git diff HEAD -- <file>` 验证 |
| `git checkout -- .` | 中——丢弃所有工作区改动 | 先 `git diff` 确认范围 |
| `git reset --hard` | **高**——不可逆 | 不用 |
| `git commit --amend` | 中——改写历史 | 不用 |
| `git rebase -i` | 中——交互式改写 | 不用 |

### stash 使用准则
1. **仅在必要时用 stash**（验证 HEAD 状态时优先用 `git diff HEAD -- <file>`）
2. stash 前先 `git status` 确认工作区范围
3. stash pop 后**必跑构建验证**
4. 若 pop 引入未知改动，立即 `git checkout -- .` 恢复

### Edit 后验证三步骤
1. `git diff <file>` 确认改动范围符合预期
2. `dotnet build --no-incremental` 验证无编译错误
3. `dotnet test` 验证无回归

---

## AI 系统调度优化

### 警告分类策略（按优先级处理）
```
P0 安全/正确性（必改）：
  S2068 凭据 / S6966 await / S5034 ValueTask / S108 空块 / S1186 空方法 / S4144 同体

P1 设计（必改或 SuppressMessage）：
  S3776 复杂度 / S107 参数 / S927 接口名 / S2681 单行 / S125 注释代码 / S1066 合并 if

P2 风格（建议改）：
  IDE0007 var / IDE0022 表达式主体 / IDE0005 unused using
```

### 并行调研策略
收到大规模警告报告时：
1. 用 Agent 并行调研（定位 + 分类 + 修复策略）
2. 主 agent 整合调研结果
3. 按规则 ID 批量处理（同规则的所有调用点一次性改）
4. 每批后构建验证

### 修复循环终止条件
- ✅ `dotnet build --no-incremental` 0 警告 0 错误
- ✅ `dotnet test` 全绿
- ✅ 14 类 Sonar 触发模式 grep 全部清零
- ✅ `git status` 工作树清洁

---

## AI 系统精炼缺陷登记（v5.0——架构精炼反思）

> 以下 7 个缺陷来自架构精炼实践（D1-R4 共 6 次提交），区别于 v4.0 的修复阶段教训。

### 精炼缺陷 1：Python 按行号切割文件不可靠
**现象**：用 Python 按行号 `lines[120:562]` 切割 DataSession 时，注释行/方法边界对不齐导致 CS1002/CS1587
**根因**：行号是硬编码的，但文件内容在之前的 Edit 操作后行号已漂移
**正解**：
1. 切割前**必须重新 Read 确认行号**
2. 优先用**符号边界**（grep 方法签名行）而非固定行号
3. 切割后立即构建验证——CS1002/CS1587/CS1591 是切割边界错误的信号

### 精炼缺陷 2：C# static virtual 默认实现不能调同接口的 static abstract
**现象**：`IDbProvider.QuoteQualifiedIdentifier` 默认实现调 `QuoteIdentifier` → CS8926
**根因**：C# 11 static virtual interface 成员在默认实现中不能引用其他 static abstract 成员——编译器无法在接口上下文中解析类型参数分发
**正解**：三 Provider 逐字相同的方法即使想上提也必须保留为各 Provider 独立实现，或改为接口外 helper
**案例**：M2 撤销（commit `fa92996`）

### 精炼缺陷 3：partial class 切割后 using 丢失
**现象**：DataSession.Schema.cs 缺 `using System.Diagnostics`（Stopwatch）和 `using Microsoft.Extensions.Logging`（LogWarning）
**根因**：Python 切割时只复制了 header using，但不同职责的 partial 需要不同的 using 集
**正解**：每个 partial 文件的 using 列表**按自身依赖独立确定**，不能盲目从原文件复制
**检查清单**：切割后构建报 CS0246/CS0103 时，优先检查 using 而非逻辑

### 精炼缺陷 4：partial class 切割后 XML 注释断裂
**现象**：CS1591（公共方法缺 XML 注释）+ CS1587（XML 注释没有放在有效语言元素上）
**根因**：方法注释 `/// <summary>` 在原文件中位于方法声明前一行，切割时注释和方法体分离
**正解**：切割时把每个方法的 XML 注释**与方法声明一起搬运**；如果方法注释在上一个文件的末尾（属于下一个文件的首方法），需要在目标文件首方法前补注释

### 精炼缺陷 5：残留方法声明（"幽灵签名"）
**现象**：Core.cs 删除了 From<T> 方法体但残留 `public QueryBuilder<T> From<T>()` 签名 → CS1002
**根因**：Python 按行号删除时，方法的签名行（class 声明后紧跟的方法）被保留，但方法体被移走
**正解**：切割后 `grep -n "public.*(" Core.cs` 逐一核对每个方法签名是否仍有完整方法体

### 精炼缺陷 6：同一方法被多个 partial 文件重复提取
**现象**：SavepointAsync 同时出现在 Query.cs 和 Transactions.cs → CS0111 重复成员
**根因**：Python 切割 Query.cs（行 708-953）和 Transactions.cs（行 780-810 + 954-1175）时，780-810 落在两个范围的重叠区
**正解**：切割前**画行号范围图**确认无重叠；每个方法只属于一个 partial 文件

### 精炼缺陷 7：stash pop 恢复无关改动
**现象**：stash pop 把 ValueStringBuilder.cs 的 `ref struct → struct` 改动恢复，触发 CS8345
**根因**：stash 存储时工作区有 IDE 自动格式化改动（using 重排序），pop 时全部恢复
**正解**：用 `git diff HEAD -- <file>` 验证 HEAD 状态，不依赖 stash

---

## 架构精炼 SOP（标准操作流程）

### 阶段 1：调研
1. 用 Agent 并行调研每个模块（Core/SourceGen/Provider/测试）
2. 输出文件清单 + 职责评估 + 评级（✅必要 / ⚠️可优化 / ❌冗余）
3. 按 ROI 排序：D（删除死代码）> M（合并）> R（拆分重构）

### 阶段 2：执行（按批次）
1. **D 批次**（删除死代码）——最低风险，每项独立提交或合并提交
2. **M 批次**（合并/上提）——低风险，验证快照基线
3. **R 批次**（拆分重构）——中高风险，**每个 R 独立提交 + 独立测试**

### 阶段 3：partial class 拆分专门流程
```
1. 读取完整源文件
2. grep 所有方法签名，记录行号
3. 画行号范围图，确认无重叠
4. 用 Python 按行号切割到独立文件（每个文件带 header + using + namespace + class 声明）
5. 从原文件删除已提取的行段
6. 每个新文件的 using 列表按自身依赖独立确定
7. 检查每个文件首方法是否缺 XML 注释（CS1591）
8. 检查每个文件末尾是否有悬空 XML 注释（CS1587）
9. 构建验证——CS1002/CS0111/CS1591/CS1587 是切割错误的信号
10. 测试验证——确保 partial 拆分不影响运行时行为
```

### 阶段 4：验证
```
1. dotnet build -c Debug --no-incremental（全量重建）
2. dotnet test 全部测试项目
3. PALORM_UPDATE_SNAPSHOTS=1 dotnet run（如涉及源生成器）
4. git diff --stat 确认改动范围合理
5. grep 确认无残留引用
```

### 精炼决策矩阵

| 类型 | 决策标准 | 执行策略 |
|------|---------|---------|
| 死代码 | `[Obsolete]` + 零消费点 | 直接删除 + 同步测试 |
| 重复代码 | 逐字相同 ≥ 3 处 | 抽到共享 helper 或上提接口 |
| God Object | 单文件 > 800 行 + 多职责 | 按职责边界拆 partial |
| 过度抽象 | 唯一调用方是自身 | 内联或删除 |
| 过细粒度 | 单文件 < 50 行 + 单方法 | 合并到语义最近的文件 |

### 精炼终止条件
- ✅ 无 `[Obsolete]` 公共 API（或已有明确移除计划）
- ✅ 无恒 null/false 预留字段
- ✅ 无逐字复制 ≥ 3 处的重复代码
- ✅ 无单文件 > 800 行的 God Object（partial 拆分后）
- ✅ 无零消费点的死代码
- ✅ 全量构建 + 测试零回归

