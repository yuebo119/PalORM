# PalORM 门禁系统（v1.1 — PalORM 适配版）

> `/gate` → 规范合规检查。不是 review（找缺陷）、不是精炼（改进代码）。
> **宗旨：质量为先**——门禁是 review 地毯式逐行的前置与兜底，不是替代；
> 门禁全绿只说明"已知机械形态零违规"，未知形态由 review 逐行负责。
> 门禁回答一个问题：**这份代码是否遵守了 PalORM 的约束规则？**
> 秒级完成·二进制结果(通过/失败)·可集成到 pre-commit。
> **权威规范来源**：[`docs/编码规范.md`](../../docs/编码规范.md) — 167 条 STD 规则 × 17 类，源自 [`docs/踩坑目录.md`](../../docs/踩坑目录.md) 302 项陷阱

---

## 定位边界

| 系统 | 问题 | 产出 |
|------|------|------|
| `/review` | 代码有什么缺陷？ | 缺陷清单+四指标+热点（原 /audit 与 /review 已统一） |
| `/refine` | 如何更优实现？ | 现代化+性能优化方案 |
| **`/gate`** | **遵守规范了吗？** | **通过/失败+违规清单** |

---

## 约束层次

| 层 | 已有守护 | `/gate` 新增检查 |
|----|---------|----------------|
| 编译期 | TreatWarningsAsErrors · IsAotCompatible · STJ源生成关闭反射 | — |
| 架构 | Directory.Build.props 统一配置 · 中央包管理 | — |
| **门禁（新增）** | **—** | **G1-G28：由 scripts/gate-check.sh 机械执行** |

---

## G1-G28 检查项（与 scripts/gate-check.sh 一一对应）

> 本表描述脚本已实现的检查。编号、名称、判定级别（阻断/警告）与脚本保持一致；修改脚本时必须同步本表和 `docs/编码规范.md` 门禁清单章节。

| ID | 约束 | 对应规范 | 级别 |
|:--:|------|------|:--:|
| G1 | 具体异常类型必须 `sealed`（`PalORMException` 基类与 abstract 除外） | STD-ARCH | 阻断 |
| G2 | Core 零外部 ORM 依赖（Dapper/EFCore/NHibernate/Newtonsoft） | STD-ARCH-002, STD-ARCH-011 | 阻断 |
| G3 | 运行时零 MakeGenericType / MakeGenericMethod | STD-AOT-003 | 阻断 |
| G4 | 运行时零反射发现（Type.GetType/.GetMethod/.GetProperty…） | STD-AOT-002 | 阻断 |
| G5 | 运行时零 Expression.Compile() | STD-AOT-004 | 阻断 |
| G6 | 运行时零 Activator.CreateInstance | STD-AOT-005 | 阻断 |
| G7 | 运行时零 dynamic / DynamicParameters | STD-AOT-007 | 阻断 |
| G8 | 零 string.Format 拼接 SQL | STD-SEC-001 | 阻断 |
| G9 | 受跟踪文件零硬编码连接凭据（全仓库，排除 docs 与明显占位符） | STD-SEC-005 | 阻断 |
| G10 | DataSession 生命周期需人工复核 | STD-ARCH-003 | 警告 |
| G11 | 零 virtual 导航属性 | STD-ARCH-004 | 阻断 |
| G12 | 禁止公开 static 可写状态（跨行扫描，故障夹具验证） | STD-ARCH-006 | 阻断 |
| G13 | Provider 不跨引用其他 Provider | STD-ARCH-010 | 阻断 |
| G14 | SourceGen 不引用运行时 Provider | STD-ARCH | 阻断 |
| G15 | 实体优先使用 DateTimeOffset | STD-TYPE-002 | 警告 |
| G16 | 级联删除必须显式启用（默认 NO ACTION） | STD-TYPE-004 | 警告 |
| G17 | 禁止 async void | STD-ASYNC-006 | 阻断 |
| G18 | 禁止 TransactionScope | STD-ASYNC-008 | 阻断 |
| G19 | 运行时项目 IsAotCompatible=true（msbuild 评估值，SourceGen 豁免） | STD-AOT-001 | 阻断 |
| G20 | 禁止同步阻塞异步操作（.Result / .Wait()） | STD-ASYNC | 阻断 |
| G21 | 生成器不得输出 blanket pragma | STD-AOT | 阻断 |
| G22 | 禁止 NoWarn/pragma 抑制 IL2xxx/IL3xxx 裁剪与 AOT 警告 | STD-AOT-009 | 阻断 |
| G23 | AOT 测试项目不得手工编译 `*.g.cs` 生成文件 | 本系统 | 阻断 |
| G24 | 库代码每个 await 必须 `ConfigureAwait(false)`（跨行 perl 统计；`await using`/`await foreach`/`Task.Yield()` 豁免） | STD-ASYNC | 阻断 |
| G25 | 公共 async API 必须带 `CancellationToken` 参数（Dispose 系豁免；`StopAsync` 豁免在案见账本 API-001） | STD-ASYNC | 阻断 |
| G26 | QueryBuilder 保持 struct 声明（class 化即高 QPS 堆分配回退，写时复制语义失效；原候补 C1 脚本化） | STD-PERF | 阻断 |
| G27 | src/ 全域 CS1591 强制不回退（全局 NoWarn 不得重新纳入 CS1591；测试/AotModels/Benchmarks 条件豁免在案；原候补 C3 的守卫） | STD-ARCH | 阻断 |
| G28 | 禁止裸 `(int)…CommandTimeout.TotalSeconds` 截断（亚秒塌缩为 0=ADO 无限等待；须走 CommandTimeoutSeconds 向上取整，ITM-501 下沉） | STD-ASYNC | 阻断 |

### 候补检查（未脚本化，需 AI 人工核验）

| 项 | 约束 | 检查方式 |
|:--:|------|---------|
| C2 | 三 Provider 实现完整 IDbProvider 成员 | static abstract 编译期强制；virtual 成员覆盖 grep 核验 |

> C1 已下沉为 G26、C3 已下沉为 G27（2026-07-19，均经负向探针验证）。

---

## PalORM 专项：插件架构依赖方向检查

> 以下为 G2/G13/G14 的具体检查命令，已包含在上述 G1-G25 表中。

```
✅ 允许:
  PalORM.Core → (仅 BCL + ADO.NET)
  PalORM.Sqlite/PostgreSql/MySql → PalORM.Core
  PalORM.SourceGen → PalORM.Core（仅分析器引用）
  PalORM.Testing → PalORM.Core + 相关Provider

❌ 禁止:
  PalORM.Core → 任何第三方 ORM 库
  ProviderA → ProviderB（跨Provider引用）
  PalORM.SourceGen → 运行时库
  PalORM.Core → Providers（循环依赖）
```

检查命令：
```bash
# 禁止：Core 引用第三方 ORM
grep -rn "using Dapper\|using EntityFramework\|using NHibernate" src/PalORM.Core/ --include="*.cs"

# 禁止：Provider 跨引用
grep -rn "using PalORM\.\(Sqlite\|PostgreSql\|MySql\)" src/PalORM.Sqlite/ --include="*.cs"
grep -rn "using PalORM\.\(Sqlite\|PostgreSql\|MySql\)" src/PalORM.PostgreSql/ --include="*.cs"
grep -rn "using PalORM\.\(Sqlite\|PostgreSql\|MySql\)" src/PalORM.MySql/ --include="*.cs"

# 禁止：SourceGen 引用运行时
grep -rn "using PalORM\.\(Sqlite\|PostgreSql\|MySql\|Testing\)" src/PalORM.SourceGen/ --include="*.cs"
```

---

## 集成

```bash
# 手动触发
bash scripts/gate-check.sh

# AI 触发
/gate → 加载本提示词 → 执行 bash scripts/gate-check.sh → 输出通过/失败
```

---

## 结果格式

以 `scripts/gate-check.sh` 实际输出为准（PASS/WARN/FAIL 每行一项 + 末尾统计）：

```
═══════ PalORM 门禁扫描 ═══════
时间：YYYY-MM-DD HH:MM:SS
规范：docs/编码规范.md

PASS G1: 具体异常类型 sealed
PASS G2: Core 零外部 ORM 依赖
...
PASS G27: src/ 全域 CS1591 强制不回退

通过：N  警告：N  失败：N  总计：28
═══════ 扫描完成 ═══════
```

退出码：任一阻断级检查失败 → exit 1（警告级不阻断）。
