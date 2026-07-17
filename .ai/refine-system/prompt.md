# PalORM 代码精炼系统（v1.1 — PalORM 适配版）

> `/refine` → **保持功能·更优实现**。不是修复缺陷，不是重构架构。
> 核心：同样语义，更少代码·更高性能·更新特性。每项操作有量化效果预估。
> 基于 refine-system 框架，适配 PalORM 插件-Provider 架构。
> **精炼约束**：参照 [`docs/编码规范.md`](../../docs/编码规范.md) — 违反规范的代码不精炼，先走 `/audit`

---

## 定位边界

| | `/audit` | `/refine` | 边界 |
|------|:--:|:--:|------|
| null 检查缺失 | ✅ 发现 | ❌ | 审计职责——找缺陷 |
| catch 无 OCE | ✅ 发现 | ❌ | 审计职责——找缺陷 |
| `new List<T>()`→`[]` | ❌ | ✅ 精炼 | 精炼职责——现代化 |
| 集合无预分配 | ❌ | ✅ 精炼 | 精炼职责——提性能 |
| 架构违规 | ✅ 发现 | ❌ | 门禁职责 |
| 文件该不该删 | ❌ | ✅ 精炼 | 精炼职责——去冗余 |

**精炼不涉及的**：修复缺陷（/audit）、架构决策评估（门禁已有）、跨层合并（破坏插件架构）。

---

## 操作矩阵（24 项·三类·各有量化预估）

### 一类：减法（去冗余·8 项·全部 🟢 零风险）

| ID | 检测 | 量化效果 | 示例 |
|:--:|------|:--:|------|
| A1 | 0引用类型/方法 | 减 N 行 | `grep -rn "类名" src/` → 0引用→删除 |
| A2 | 重复实现 | 减 M 行 | DbOptions.ToSnakeCase vs NamingHelper.ToSnakeCase |
| A3 | ≤10行标记接口/常量类 | 减 N 文件 | 合并至唯一使用方 |
| A4 | 死 catch 块 | 减 K 行 | `catch(DbException){}` DDL已含IF NOT EXISTS→移除 |
| A5 | 冗余 using | 减 L 行 | IDE0005 检测 |
| A6 | 参数丢弃 SuppressMessage | 消除注解 | WithPool 参数未传递→修复后 Suppress 自动消除 |
| A7 | 未使用内建类 | 减文件 | Interceptors.cs TracingInterceptor/MetricsInterceptor |
| A8 | 回退兼容路径 | 减路径 | CrudMetadatas 旧字典回退→补齐手写注册表后删除 |

### 二类：现代化（平台特性·10 项·各有效果量化）

| ID | 旧 → 新 | 代码减少 | 性能提升 | 风险 |
|:--:|---------|:--:|:--:|:--:|
| M1 | `new List<T>()`→`[]` | -8字符 | 可能零分配 | 🟢 |
| M2 | 构造注入 5 行→主构造函数 1 行 | **-4行/类** | — | 🔴 |
| M3 | `{get;}`+校验→`required` | -3行/属性 | — | 🔴 |
| M4 | 类型判断链→`switch`表达式 | -3行 | 编译期穷尽检查 | 🟡 |
| M5 | `string.Format`→`$"{x}"`插值 | -10字符 | 编译期优化 | 🟢 |
| M6 | `new ArgumentNullException`→`ThrowIfNull(x)` | **-1行** | — | 🟡 |
| M7 | `ref struct`枚举器 | — | 零分配 | 🟡 |
| M8 | `struct`→`readonly record struct` | — | 编译器Equals | 🔴 |
| M9 | `lock()`→`Lock`(.NET9+) | — | 更好并发 | 🟡 |
| M10 | `params T[]`→`params Span<T>` | — | 零分配varargs | 🟡 |

### 三类：优化（性能提升·9 项·不改语义）

| ID | 检测 | 量化效果 | 风险 |
|:--:|------|:--:|:--:|
| O1 | `Dictionary<K,V>`→`FrozenDictionary`(只读) | O(n)→O(1)查找·零GC | 🟡 |
| O2 | 集合 `new()`→`new(N)` 预分配 | 减少扩容次数 | 🟢 |
| O3 | `.ToArray()/.ToList()`→`Span<T>` | 零分配 | 🟡 |
| O4 | `foreach`+`yield`→`ref struct` 枚举器 | 零分配·零装箱 | 🟡 |
| O5 | LINQ链→手写循环(热路径) | 减少委托分配 | 🟡 |
| O6 | 字符串`+=`循环→`StringBuilder`或插值 | 减少碎片 | 🟡 |
| O7 | `ValueTask`多重await→单次await | 消除潜在bug | 🔴 |
| O8 | 泛型`object`参数→泛型约束 | 消除装箱 | 🟡 |
| O9 | `Enum.ToString()`→`nameof`或switch | 减少分配 | 🟡 |

---

## 精炼流程（4 阶段）

### 阶段 0：一键扫描
```bash
bash scripts/refine-scan.sh  # 机械输出 17 项命中量级；A1/A2/A3/A7/M2/M7/O2/O7 共 7 项标注 [人工]，需 Roslyn/语义分析
```

### 阶段 1：分类排序
按量化效果排序——优先处理"减代码多 + 性能提升大 + 风险低"的组合：

| 优先级 | 条件 | 典型操作 |
|:------:|------|---------|
| P0 | 🟢风险 + 减≥3行/类 | A1-A5·M1·M5·O2（立即做） |
| P1 | 🟡风险 + 减≥1行 或 提性能显著 | M6·M7·M9·M10·O1·O3·O4·O8·O9 |
| P2 | 🔴风险 或 减代码有限 | M2·M3·M8·O5·O6·O7（评估后做） |

### 阶段 2：逐项执行
P0→P1→P2 顺序。每项执行后 `dotnet build` 验证。每批完成后 `dotnet test` 验证。

### 阶段 3：产出
`docs/review/refine-{date}.md` — 执行清单 + 每项量化效果 + 精炼前后对比

---

## PalORM 专项约束（v1.2 实践修订版）

| 约束 | 说明 |
|------|------|
| **Core 层可精炼** | ~~Core 层不精炼~~ → **Core 层可精炼**。本项目已实践: QueryBuilder class→struct、删除死代码(SqlFileLoader/NamingHelper)、CrudMetadata合并、ValueStringBuilder。约束修正为: Core 层精炼后必须通过全量测试 + AOT 验证 |
| AOT 优先 | 任何引入反射或动态代码的精炼操作直接禁止 |
| 插件架构不改 | 精炼不跨 Provider 移动文件——那是重构，不是精炼 |
| 源生成器代码不精炼 | `*.g.cs` 生成文件跳过所有精炼检查 |
| Provider 独立性保持 | 不在 Provider 间引入共享抽象层来消除重复代码 |
| **struct 迁移需验证** | class→struct 迁移必须: dotnet build + dotnet test + dotnet publish -p:PublishAot=true |

---

## 精炼效果追踪

每个操作执行后记录：
```
操作: M2 (主构造函数)
涉及文件: src/PalORM.Sqlite/SqliteTypeMapper.cs
代码变化: -5行 (+1主构造函数, -6旧构造注入)
性能变化: 无
风险: 通过测试 ✅
```

## 安全红线

| 禁止 | 原因 |
|------|------|
| 改变 public API 签名 | 精炼≠重构·零行为变更 |
| 🔴 项在全量测试前提交 | 结构性变更需验证 |
| 性能优化降低可读性 | 可读性 > 微优化 |
| 跨 Provider 移动文件 | 破坏插件架构独立性 |
| Core 层精炼跳过验证 | Core 层可精炼（见上方专项约束），但必须全量测试 + AOT 验证后才可提交 |
