# PalORM 项目级 AI 规范

> 本文件被 ZCode 自动加载（项目根目录 AGENTS.md 约定）。
> 与全局 `~/.zcode/AGENTS.md` 的分工：全局管行为准则（Karpathy 准则），项目级管项目规范。

## 启动清单（每次会话开始确认）

```
1. git status——工作树清洁？
2. dotnet build -c Debug——0 警告 0 错误？
3. 技术债扫描——bash .ai/scripts/tech-debt-scan.sh（本地工具，不入仓库）
4. .ai/lessons.md——已读最新版？（本地工具，不入仓库）
```

## 规范真源（按优先级）

| 优先级 | 文件 | 用途 |
|-------|------|------|
| **最高** | `.editorconfig` | SonarAnalyzer 39 条规则（P0+P1 error，编译期阻断；口径=全部 dotnet_diagnostic 严重性条目含 *.g.cs 段） |
| **高** | `.ai/lessons.md` | 规范系统手册 v7.0（44 缺陷：A1-A7 + B1-B37，本地工具，不入仓库） |
| **高** | `.ai/test/prompt.md` | 测试规范系统 v1.0（14 铁律 + 17 缺陷，本地工具，不入仓库） |
| **高** | `docs/发布规范.md` | NuGet 发布流程 SOP（v5.0.0 实测，含 8 条实践教训） |
| **中** | `docs/编码规范.md` §18 | SonarAnalyzer 守护层规则文档化 |
| **参考** | `.github/PULL_REQUEST_TEMPLATE.md` | PR 检查清单 |

## 四系统（按需触发）

| 命令 | 系统 | 用途 |
|------|------|------|
| `/review` | 审计+评审 | 地毯式逐行代码审查（5 档位） |
| `/gate` | 门禁 | G1-G33 规范合规检查（编译前阻断） |
| `/refine` | 精炼 | 24+3 项操作矩阵（更优实现） |
| `/test` | 测试规范 | T1-T14 测试铁律 + 覆盖矩阵 + 基准配置规范 |

### 项目专属经验（v4.0 + v5.0 实测，编号与四系统脚本对齐）

> 通用教训已在全局 `~/.zcode/AGENTS.md` 的"大型重构实践教训"章节。以下仅保留 PalORM 专属的规则编号和缺陷编号。

**门禁 G31-G33**：
- G31 方言感知验证（跨方言 API 需 SqlDialect 分支或回退）
- G32 NuGet.Config packageSourceMapping CI 兼容（nuget.org 也能提供约束包）
- G33 工作树脏检查（跨任务切换前 git status 清洁）— 已提升到全局

**精炼 O25-O27**：
- O25 阈值改能力检测（local_infile 替代行数阈值）
- O26 方案 Y 双方法（BulkUpdate vs BulkUpdateBatchAsync）
- O27 SourceGen WithComparer（sealed record 增量缓存）

**测试 T11-T14**（编号冲突注意）：
- T11 计数口径统一（badge / 文档 / tech-debt #9 一致）
- T12 断言基线提升需标注理由
- T13 DryRun/SQL 断言必须先读生成代码
- T14 优先行为断言，避免裸 IsNotNull

**v5.0 期望但未入 test/prompt.md 的规则**（编号冲突）：
- 原 AGENTS.md 声称的"T11 环境变量命名分离 / T12 三方一致扩展 / T13 被测代码完整性"与 test/prompt.md 的 T11-T14 完全冲突
- 环境变量命名分离 → 全局 `~/.zcode/AGENTS.md` 的 v5.0 大型重构教训章节已覆盖
- 三方一致扩展 → 全局"三方一致"铁律已覆盖（包版本号+连接串参数）
- 被测代码完整性 → 已提升到全局

**过程纪律 B8-B37**（r11 审计补 B28/29 空洞、r5/r10 沉淀 B30-B37——真源在 .ai/lessons.md）（PalORM 专属缺陷编号，见 .ai/lessons.md）：
- B8 emit 变更必须清 obj/bin（增量构建复用旧 emit 导致 NRE）
- B9 方案文字与实施代码偏差（核心路径严格对齐）
- B10 方案调研可能误判瓶颈（benchmark 验证声称的瓶颈）
- B11 review 子代理推理不验证代码（P0/P1 定级前写复现测试）
- B12 Edit 替换误删相邻行（old_string 只含目标行 ±1 行）
- B13 门禁正则匹配注释内容（统计前剥离 /// 和 //）
- B14 测试计数口径统一（全仓库统一为 CI 通过数）
- B15 SQL 断言必须先读生成代码（不看代码的断言 = 猜测）
- B16-B20 v5.0 会话缺陷（TUnit 零改动误判/阈值伪精确/CASE WHEN 方言慢/NuGet.Config CI/SourceGen WithComparer）
- B21-B23 过程纪律缺陷（核实 summary / 核实 diff / 核实 API）— R0 的底层缺陷登记
- B24-B27 诊断规则工程化缺陷（RS1032 消息格式/XML doc 转义/防静默错误价值分层/SyntaxNodeAction 变量流局限）— PALORM001-040 完整化会话
- B28 特性推荐偏置缺陷（参照系偏置：用 EF Core 全功能面衡量 micro-ORM）— 「特性推荐四问」SOP：推荐前必过(1) ORM 职责吗？(2) AOT 可行吗？(3) 客户自己做更好吗？(4) 真必需吗？且必查 docs/adr/ 既有 ADR + .ai/lessons.md B 系列 + grep 目标 API 已存在性
- B29 Interceptor 实施工程化缺陷（API 名称/位置推断错误）— PoC 驱动开发 SOP：实施前先写最小 PoC 验证关键 API 可用性（1 天止损），API 以编译错误信息为准不以记忆为准

**R0 审查前置三核实**（已提升到全局）：核实 summary / 核实 diff / 核实 API

详见 `.ai/README.md`。

## 构建验证时机

| 时机 | 命令 |
|------|------|
| 同类改动内 | 不构建 |
| 跨类别切换 | `dotnet build` |
| 快照类改动 | 先 `PALORM_UPDATE_SNAPSHOTS=1 dotnet run` 确认基线 |
| 最终提交前 | `dotnet build --no-incremental` |
| 技术债扫描 | `bash .ai/scripts/tech-debt-scan.sh`（本地工具） |

## 详见

- **完整规范手册**：`.ai/lessons.md`
- **四系统导航**：`.ai/README.md`
- **发布流程**：`docs/发布规范.md`
- **贡献指南**：`CONTRIBUTING.md`
- **变更日志**：`CHANGELOG.md`
