# PalORM AI 精炼系统手册 v6.0

> **一句话定义**：AI 在 PalORM 上做任何代码变更前必读的上下文文件。
> 包含 4 个阶段（修复 / 精炼 / 增值 / 维护）的全部教训、SOP、决策矩阵、知识库。
>
> **版本历史**：v4.0（修复缺陷）→ v5.0（精炼缺陷）→ v6.0（终极整合 + 技术债扫描）

---

## I. AI 协作 6 条铁律（不可违反）

| # | 铁律 | 违反后果 |
|---|------|---------|
| 1 | **改后必跑 `dotnet build --no-incremental`** | 增量构建缓存掩盖断裂（CS1002/CS0111） |
| 2 | **批量 Edit 前必 Read** | 单行测试类 Edit 导致 CS1585 锚点漂移 |
| 3 | **SuppressMessage 必附 Justification**（禁用 ASCII 引号，用「」替代） | 编译错误 CS1003 |
| 4 | **Partial 拆分时每个文件 using 按自身依赖独立确定** | CS0246/CS0103 找不到 Stopwatch/ILogger |
| 5 | **C# 插值字符串内 `{}` `;` 不可简单 split** | Python 拆行破坏对象初始化器导致 CS1513 |
| 6 | **测试用凭据不用 Password=** | S2068 硬编码凭据（用字符数组 `new([...])`） |

---

## II. AI 系统缺陷登记（14 个，按阶段分组）

### 阶段 A：修复缺陷（v4.0，4 轮 Sonar 警告修复）

| # | 缺陷 | 核心教训 |
|---|------|---------|
| A1 | 循环触发（4 轮扫描） | 每次 Edit 后强制构建验证 |
| A2 | 抑制机制误用（3 次发现） | `[SuppressMessage]` attribute 才有效，注释无效 |
| A3 | Edit 锚点漂移（CS1585） | 单行类先多行展开再 Edit |
| A4 | 字符串引号陷阱 | Justification 内禁用 ASCII `"`，用「」 |
| A5 | 规则 ID 不精确 | 同型规则族（S2189/S1994）必须同时抑制 |
| A6 | stash pop 副作用 | 用 `git diff HEAD` 验证而非 stash |
| A7 | 子任务并行化不足 | 分类后并行 Agent 调研 |

### 阶段 B：精炼缺陷（v5.0，架构精炼 D1-R4）

| # | 缺陷 | 核心教训 |
|---|------|---------|
| B1 | Python 按行号切割不可靠 | 行号漂移——切割前必须重新 Read |
| B2 | static virtual 不能调 static abstract | CS8926 限制，三 Provider 相同方法无法上提 |
| B3 | Partial 切割后 using 丢失 | 每个文件 using 按自身依赖独立确定 |
| B4 | Partial 切割后 XML 注释断裂 | 方法注释与方法声明一起搬运 |
| B5 | 残留幽灵方法签名 | 切割后 grep 核对每个签名有完整方法体 |
| B6 | 方法被多 partial 重复提取 | 画行号范围图确认无重叠 |
| B7 | stash pop 再次确认 | 用 `git diff HEAD` 验证而非 stash |

---

## III. Sonar 抑制机制对照表

| 抑制方式 | MSBuild 编译 | SonarLint IDE | SonarQube CI |
|---------|-------------|--------------|-------------|
| `// 注释` | 无影响 | **无效** | 无影响 |
| `#pragma warning disable SXXXX` | 无影响 | 有效 | 有效 |
| `[SuppressMessage(...)]` | 无影响 | **有效** | 有效 |
| `<NoWarn>` | 有效（项目级） | 无影响 | 无影响 |
| `.editorconfig` | 有效 | 有效 | 有效 |

### 同型规则族（必须同时抑制）

| 族 | 规则 ID |
|----|---------|
| 循环变量 | S127 / S1994 / S2189 |
| 参数过多 | S107 |
| 空块 | S108 / S1186 |
| 异步 | S6966 / CA1849 |
| 命名 | IDE1006 / S101 |

### 不可抑制的 P0 规则（必须改代码）

| 规则 | 说明 |
|------|------|
| S2068 | 硬编码凭据 |
| S6966 | 同步 ADO.NET |
| S108 | 空块（无注释） |

---

## IV. 架构精炼 SOP

### 阶段 1：调研（Agent 并行）
```
1. 调研每个模块（Core/SourceGen/Provider/测试）
2. 输出文件清单 + 职责评估 + 评级（✅必要 / ⚠️可优化 / ❌冗余）
3. 按 ROI 排序：D（删除死代码）> M（合并）> R（拆分重构）
```

### 阶段 2：执行（按风险分批）
```
D 批次（删除死代码）——最低风险，每项独立提交或合并提交
M 批次（合并/上提）——低风险，验证快照基线
R 批次（拆分重构）——中高风险，每个 R 独立提交 + 独立测试
```

### 阶段 3：Partial 拆分专门流程（10 步）
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

---

## V. 精炼决策矩阵

| CC 范围 | 默认策略 | 例外 |
|--------|---------|------|
| ≤ 15 | 通过 | — |
| 16-20 | 拆分 | 异步生命周期管理（多层 try/catch/finally）→ SuppressMessage |
| 21-30 | 拆分 | 拆分会破坏并发契约（如 CTS 所有权）→ SuppressMessage |
| > 30 | 必须拆分 | 无例外 |

| 类型 | 决策标准 | 执行策略 |
|------|---------|---------|
| 死代码 | `[Obsolete]` + 零消费点 | 直接删除 + 同步测试 |
| 重复代码 | 逐字相同 ≥ 3 处 | 抽到共享 helper 或上提接口 |
| God Object | 单文件 > 800 行 + 多职责 | 按职责边界拆 partial |
| 过度抽象 | 唯一调用方是自身 | 内联或删除 |
| 过细粒度 | 单文件 < 50 行 + 单方法 | 合并到语义最近的文件 |

### 终止条件
- ✅ 无 `[Obsolete]` 公共 API（或已有明确移除计划）
- ✅ 无恒 null/false 预留字段
- ✅ 无逐字复制 ≥ 3 处的重复代码
- ✅ 无单文件 > 800 行的 God Object
- ✅ 无零消费点的死代码
- ✅ 全量构建 + 测试零回归

---

## VI. 技术债扫描 SOP（每季度执行一次）

```bash
# 12 类检查——每项零残留即通过
# 1. [Obsolete] 残留
grep -rn "\[Obsolete" src/ test/ tools/ --include="*.cs" | grep -v "obj/\|bin/"
# 2. TODO/HACK/FIXME
grep -rnE "// TODO|// HACK|// FIXME|// XXX" src/ test/ --include="*.cs" | grep -v "obj/\|bin/"
# 3. SuppressMessage 无 Justification（Python 多行扫描）
# 4. Console.WriteLine 在 src/
grep -rn "Console\." src/ --include="*.cs" | grep -v "obj/\|bin/"
# 5. 空 catch 无注释
grep -rn "catch.*{}" src/ test/ --include="*.cs" | grep -v "obj/\|bin/"
# 6. 超长行 > 180
find src/ test/ tools/ bench/ -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" -exec awk 'length($0)>180' {} \;
# 7. unused using（IDE0005）—— dotnet build 输出
# 8. 测试用例数对照 README
# 9. SuppressMessage 总数（确认全部带 Justification）
# 10. SourceGen 超长行（代码生成模板，允许）
# 11. static 可变状态（确认有线程安全机制）
# 12. 占位诊断（PALORM006/007 已删——检查是否复发）
```

---

## VII. SonarAnalyzer 规则层级（当前配置）

### P0 安全/正确性 → error
S2068 / S6966 / S5034 / S108 / S1186 / S4144

### P1 设计/可读性 → error
S3776 / S107 / S927 / S2681 / S125 / S1066 / S1994 / S2189

### P2 风格 → suggestion
IDE0007 / IDE0022 / IDE0005 / IDE0060 / IDE1006

### 项目级豁免（设计本意）
S101（PalORM 品牌名）/ S1133（Obsolete 已标注）/ S2077（FormattableString 自动参数化）/ S3236（CallerArgumentExpression）/ S6667（LoggerMessage 源生成器）

### 测试项目降级
S2094（空类占位实体）/ S1481（未用变量）/ S6444（Task.Run 无 timeout）

---

## VIII. 结构性反模式登记（RP-1 ~ RP-14）

| # | 反模式 | 状态 | 案例 |
|---|--------|------|------|
| RP-1 | 巨型 Lambda | 已根治 | PalORMAnalyzer CC 153→12 方法 |
| RP-2 | 双路径内联 | 部分根治（v3.0 IDialectStrategy） | Insert/Upsert 双路径 |
| RP-3 | 硬编码凭据 | 已根治 | CoreTests 字符数组构造 |
| RP-4 | 同步 ADO.NET | 已根治 | Scaffold async 改造 |
| RP-5 | 空块陷阱 | 已根治 | Assert.ThrowsAsync 替代 |
| RP-6 | Edit 锚点漂移 | 已登记 | QueryTests CS1585 |
| RP-7 | 抑制机制误用 | 已根治 | SuppressMessage attribute |
| RP-8 | 字符串引号陷阱 | 已登记 | Justification 用「」 |
| RP-9 | 规则 ID 不精确 | 已登记 | S2189/S1994 同型族 |
| RP-10 | 参数膨胀 | 已根治 | QueryBuilderContext 聚合 |
| RP-11 | 属性遮蔽 | 已根治 | PalORM_Runtime _ 前缀 |
| RP-12 | 嵌套三元 | 已根治 | switch/List+Join 替代 |
| RP-13 | 硬编码 fallback | 已根治 | TestEnvironment 显式失败 |
| RP-14 | Bulk 重复 | 已根治 | BulkOperationFramework 抽取 |

---

## IX. 关键案例引用

| 反模式/缺陷 | 案例 commit | 文件 |
|-------------|-----------|------|
| RP-1 巨型 Lambda | `bad26c8` | PalORMAnalyzer.cs |
| RP-2 双路径内联 | `af775e0` | DataSession.InsertCoreAsync |
| RP-3 硬编码凭据 | `438a44b` | CoreTests.cs |
| RP-5 空块陷阱 | `438a44b` | AdvancedTests.cs |
| RP-6 Edit 锚点 | `a8dc99f` | QueryTests.cs |
| RP-10 参数膨胀 | `6057845` | QueryBuilder.cs |
| RP-11 属性遮蔽 | `07f8847` | PalORM_Runtime.cs |
| RP-12 嵌套三元 | `a308144` | RegistryEmitter.cs |
| RP-14 Bulk 重复 | `3f77dcf` | BulkOperationFramework.cs |
| B1 Python 切割 | `11f28f6` | DataSession partial 拆分 |
| B2 static virtual | `fa92996` | IDbProvider.CS8926 |
| 占位诊断删除 | `8781357` | PalORMAnalyzer PALORM006/007 |

---

## X. PR 检查清单

### 编译验证
- [ ] `dotnet build -c Debug` 0 警告 0 错误
- [ ] `dotnet build -c Release --no-incremental -warnaserror` 通过
- [ ] AOT 项目单独验证

### 测试验证
- [ ] `PalORM.Core.Tests` 全绿（156+ 用例）
- [ ] `PalORM.SourceGen.Tests` 全绿（105+ 用例，含快照比对）
- [ ] `PalORM.Integration.Tests` SQLite 部分全绿（149+ 用例）
- [ ] 如改 emit 模板，`PALORM_UPDATE_SNAPSHOTS=1` 重生成并评审 diff

### SonarAnalyzer 守护
- [ ] 未引入新的 P0 错误（S2068/S6966/S5034/S108/S1186/S4144）
- [ ] 未引入新的 P1 错误（S3776/S107/S927/S2681/S125/S1066/S1994/S2189）
- [ ] 新增 `[SuppressMessage]` 必附 Justification

### 三方一致
- [ ] 公共 API 变更已同步文档
- [ ] 计数/规则数变更已同步文档
- [ ] 命名/路径变更已 grep 全仓库零残留

### 架构精炼守护
- [ ] 未引入 `[Obsolete]` 而无明确移除版本
- [ ] 未引入恒 null/false 预留字段
- [ ] 未引入逐字复制 ≥ 3 处的重复代码
- [ ] 未引入单文件 > 800 行的 God Object
- [ ] 未引入零消费点的死代码

### 反模式预防
- [ ] lambda 体 ≤ 10 行
- [ ] 参数 ≤ 7
- [ ] 无硬编码凭据
- [ ] 无同步 ADO.NET 调用
- [ ] 无空块 / 空 catch
