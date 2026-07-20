# PalORM AI 规范化系统 v7.0

> **一句话定义**：AI 在 PalORM 上做任何代码变更前必读的入口文件。
> 本文件是唯一的 AI 规范真源——其他文件（`.editorconfig` / `docs/编码规范.md` / `PR 模板`）引用本文件的规则编号。
>
> **版本历史**：v4.0（修复缺陷）→ v5.0（精炼缺陷）→ v6.0（终极整合）→ v7.0（规范化整合）

---

## I. AI 协作 6 条铁律（不可违反）

| # | 铁律 | 违反后果 |
|---|------|---------|
| 1 | **改后必跑 `dotnet build --no-incremental`** | 增量缓存掩盖断裂 |
| 2 | **批量 Edit 前必 Read** | 单行类 Edit 导致 CS1585 |
| 3 | **SuppressMessage 必附 Justification**（用「」替代 ASCII `"`） | CS1003 编译错误 |
| 4 | **Partial 拆分每个文件 using 独立确定** | CS0246/CS0103 |
| 5 | **C# 插值字符串不可简单 split 拆行** | CS1513 对象初始化器断裂 |
| 6 | **测试凭据不用 Password=** | S2068 硬编码凭据 |

---

## II. AI 缺陷登记（14 个）

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
7. CHANGELOG——当前版本号（2.1.0）？
8. 测试用例数——README badge 与实际一致？
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
| 占位诊断删除 | `8781357` | PalORMAnalyzer PALORM006/007 |
| 规范化 | `3450e18` | CHANGELOG + CONTRIBUTING |
