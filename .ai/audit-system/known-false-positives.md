# 已知误判模式（审计系统知识库）

> 来源：跨项目经验积累 + PalORM 项目持续积累。每次 `/audit` 阶段 2 执行前自动加载。
> 维护：发现新的误判模式后追加到本文。标注来源项目。

---

## 通用模式（跨项目适用）

### 模式 1：lock 块只读前半段 → "并发竞态"

**来源**：跨项目

**反例**：评审建议某方法存在并发竞态。实际完整状态转换已在 `lock (_lock)` 内完成，评审只读了 lock 块的前半段。

**如何避免**：读完整 lock 块体。确认被保护的所有修改是否都在同一 lock 内。禁止基于 lock 块前半段下并发结论。

---

### 模式 2：grep 计数差异 → 直接当语义结论

**来源**：跨项目

**反例**：grep 显示 N 处 catch(Exception) 但只有 M 处 when(is not OCE)，初判"异常过滤不一致"。实际其余使用了"前置 catch(OCE){throw;}" 模式。

**如何避免**：grep 做定位不做计数判断。数字差异 ≠ 语义差异。逐行读每个 catch 的前一行，确认保护策略。

---

### 模式 3：catch(Exception) 前已有 catch(OCE){throw;}

**来源**：跨项目

**反例**：评审建议某方法增加 OCE 过滤。实际已有 `catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)` 在前。

**如何避免**：读 catch(Exception) 的上一个 catch 块。如果已有 `catch(OperationCanceledException){throw;}`，则当前 catch 不会捕获 OCE——这是合规模式，不是遗漏。

---

### 模式 4：带 when 过滤的 catch 不触发分析器

**来源**：跨项目

**反例**：预估 N 处 catch(Exception) 需添加 [SuppressMessage]，实际 `dotnet build` 后仅少数报错。带 `when(ex is not OCE)` 的 catch 不触发分析器。

**如何避免**：任何涉及分析器触发条件的判断，必须先 `dotnet build` 验证。禁止基于"分析器应该会"的假设。

---

### 模式 5：基于过期快照的缺失判断

**来源**：跨项目

**反例**：评审声称某文件缺失。实际已存在，评审基于旧版本快照。

**如何避免**：所有"X 不存在""X 缺失"的判断，必须在当前 commit 上 grep/`ls` 验证。禁止采信记忆或旧报告。

---

### 模式 6：评分与实现质量脱节

**来源**：跨项目

**反例**：评审评分高但连接泄漏未被发现。评分与实现质量脱节。

**如何避免**：评分前先完成 6 流全量审计。若审计覆盖度未达 100%，评分需明确降级并注明"覆盖度为 X%，可能遗漏"。

---

### 模式 7：grep 表面 ✅ → 跳过深度方法读取

**来源**：跨项目

**反例**：首次审计全部 ✅，零 P0/P1。补充深度审计后发现了真实问题。

**如何避免**：grep 定位 → Read 验证 → 才可 ✅。每条流的覆盖度必须达标。若全部 ✅，必须在报告中明确声明"深度审计后确认干净"并附覆盖度证据。

---

### 模式 8：外部任务方法名/类名未交叉验证

**来源**：跨项目

**反例**：任务描述中方法名与实际源码不符。错误从任务清单持续到实施前才暴露。

**如何避免**：外部合并的任务在写入 action-items 前，逐项 grep 方法名/类名/路径在源码中是否存在。

---

## PalORM 专项模式（持续积累）

> 以下模式在 PalORM 项目开发过程中发现后追加。

### 模式 P1：源生成器输出 => 不应被直接审计

**反例**：审计发现 `PalORM.SourceGen` 生成的 `.g.cs` 文件中存在代码异味。实际这些文件是编译时生成的，不应被审计。

**如何避免**：`find src -name "*.g.cs"` 识别生成文件 → 排除出审计范围。审计只覆盖手写 `.cs` 文件。

### 模式 P2：Provider 间代码重复 ≠ DRY 违规

**反例**：审计标记 Sqlite/PostgreSql/MySql 三个 Provider 存在重复代码。实际 Provider 实现因数据库方言差异故意独立，共享会引入不必要的抽象层。

**如何避免**：区分"偶然重复"和"刻意独立"。Provider 间相似代码是各数据库方言的独立实现，不是 DRY 违规。

### 模式 P4：null! 在 ModuleInitializer 填充的 static 属性

**反例**：审计标记 `PalORM_Runtime.RowFactories = null!` 为潜在 NRE 风险。实际这些 static 属性由 `[ModuleInitializer]` 在启动时一次性填充 `FrozenDictionary`，之后不可变。`null!` 是 C# 必需的语法（static 属性必须有初始值，而实际值由初始化器设置）。

**如何避免**：检查是否有 `[ModuleInitializer]` 方法填充了这些属性。如果有，`null!` 是合规模式——不是缺陷。

### 模式 P5：struct 方法中的 lambda 捕获 this

**反例**：审计建议 lambda 改为捕获局部变量以避免装箱。实际 struct 中 lambda 无法捕获 `this`（CS1673），必须提前捕获到局部变量。这是 C# 语言约束，不是性能问题。

**如何避免**：识别 `CS1673` 编译错误对应的代码模式——struct 内 lambda 必须显式捕获所需字段到局部变量。

### 模式 P6：FrozenDictionary 静态属性为"全局可变状态"

**反例**：审计标记 `PalORM_Runtime` 的 13 个 static 属性违反"零全局可变状态"规则。实际 `FrozenDictionary` 创建后不可变，`[ModuleInitializer]` 确保在用户代码执行前完成初始化。符合"零全局可变"的精神。

**如何避免**：区分"初始化时设置一次"和"运行时可变"。`FrozenDictionary` + `ModuleInitializer` = 合规。`Dictionary` + 运行时 `Add/Remove` = 违规。

### 模式 P7：catch(DbException) 被标记为异常吞噬

**反例**：审计标记 `catch(DbException){}` 为静默异常吞噬。实际 DDL 已使用 `IF NOT EXISTS` 保证幂等，catch 为兜底安全网。DDL 如果已含 `IF NOT EXISTS`，catch 可以安全移除——但不能仅凭"catch 为空"就判定为缺陷。

**如何避免**：检查对应的 SQL/DDL 是否已有幂等保护。如果已有（如 `IF NOT EXISTS`），catch 是冗余的可以安全移除——但不能标记为"异常吞噬"。

### 模式 P8：账本"已完成"≠ 文件当前存在（仓库重建/回滚后失真）

**反例**：ITM-010 与 2026-07-17 审计报告均声称 `scripts/verify-ai-system.sh` 已创建并挂入 CI，实际该文件在当日 Git 仓库重建事故中丢失，账本未回改。反向教训同样成立：仓库重建也会让"历史已清除"的安全声明失效（AUD-001 凭据在重建历史中复活）。

**如何避免**：凡是引用具体文件/脚本的"已完成"声明，验证时必须在当前工作区 `ls`/`git ls-files` 确认存在；凡是"历史已清除"的安全声明，必须在当前 `git rev-list --all` 上重新 grep 验证。仓库重建、强制回滚、分支重置之后，所有账本状态视为未验证。

<!-- 
模板：
### 模式 P{N}：简述
**反例**：具体案例
**如何避免**：预防方法
-->
