# PalORM 项目级 AI 规范

> 本文件被 ZCode 自动加载（项目根目录 AGENTS.md 约定）。
> 与全局 `~/.zcode/AGENTS.md` 的分工：全局管行为准则（Karpathy 准则），项目级管项目规范。

## AI 启动清单（每次会话开始确认）

```
1. git status——工作树清洁？
2. dotnet build -c Debug——0 警告 0 错误？
3. 技术债扫描——bash scripts/tech-debt-scan.sh 12/12 通过？
4. .ai/lessons.md——已读最新版？
```

## 规范真源（按优先级）

| 优先级 | 文件 | 用途 |
|-------|------|------|
| **最高** | `.editorconfig` | SonarAnalyzer 38 条规则（P0+P1 error，编译期阻断） |
| **高** | `.ai/lessons.md` | AI 规范系统手册 v7.0（6 铁律 + 22 缺陷 + SOP + 决策矩阵） |
| **高** | `.ai/test/prompt.md` | 测试规范系统 v1.0（14 铁律 + 16 缺陷 + 覆盖矩阵 + 基准配置） |
| **中** | `docs/编码规范.md` §18 | SonarAnalyzer 守护层规则文档化 |
| **参考** | `.github/PULL_REQUEST_TEMPLATE.md` | PR 检查清单 6 类 |

## 四系统（按需触发）

| 命令 | 系统 | 用途 |
|------|------|------|
| `/review` | 审计+评审 | 地毯式逐行代码审查（5 档位） |
| `/gate` | 门禁 | G1-G28 规范合规检查（编译前阻断） |
| `/refine` | 精炼 | 24 项操作矩阵（更优实现） |
| `/test` | 测试规范 | T1-T10 测试铁律 + 覆盖矩阵 + 基准配置规范 |

详见 `.ai/README.md`。

## 构建验证时机

| 时机 | 命令 |
|------|------|
| 同类改动内 | 不构建 |
| 跨类别切换 | `dotnet build` |
| 快照类改动 | 先 `PALORM_UPDATE_SNAPSHOTS=1 dotnet run` 确认基线 |
| 最终提交前 | `dotnet build --no-incremental` |
| 技术债扫描 | `bash scripts/tech-debt-scan.sh` |

## 详见

- **完整规范手册**：`.ai/lessons.md`
- **四系统导航**：`.ai/README.md`
- **贡献指南**：`CONTRIBUTING.md`
- **变更日志**：`CHANGELOG.md`
