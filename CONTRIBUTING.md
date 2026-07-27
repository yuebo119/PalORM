# 贡献指南

## 开发环境

- .NET 11 SDK (Preview)
- Git
- 本地 SQLite（内置，无需安装）
- 可选：本地 PostgreSQL 17 / MySQL 8.4（集成测试需要）

## 快速开始

```bash
git clone <repo-url>
cd Pal.ORM

# 构建全部项目
dotnet build PalORM.slnx -c Debug

# 运行 SQLite 测试（无需外部数据库）
dotnet test test/PalORM.Core.Tests -c Debug
dotnet test test/PalORM.Integration.Tests -c Debug

# 可选：设置外部数据库连接串
cp .env.test.example .env.test
# 编辑 .env.test 填入本地 PG/MySQL 凭据
source scripts/set-test-env.sh
```

## 代码规范

### 编译纪律（强制）
- `TreatWarningsAsErrors=true`——构建零警告零错误
- `AnalysisLevel=latest-all`——所有分析器规则启用
- `GenerateDocumentationFile=true`——src/ 公共 API 必须有 XML 注释
- SonarAnalyzer.CSharp P0 + P1 规则为 error

### 命名约定
- private 字段：`_camelCase`（.editorconfig 强制）
- 文件范围 namespace（C# 10+）
- `var` 优先（IDE0007 suggestion）

### 测试纪律
- 测试方法命名：`Method_Scenario_Expectation`
- 测试期望异常用 `Assert.ThrowsAsync<T>`，不用 `try {} catch {}`
- 测试用凭据不用 `Password=xxx`——用字符数组构造或环境变量
- 单行测试方法拆为多行（≤180 字符/行）

详见 `docs/编码规范.md`。

## PR 流程

1. 创建分支：`git checkout -b feature/xxx` 或 `fix/xxx`（从 dev 创建，详见 `docs/发布规范.md` §3.1）
2. 实现变更 + 测试
3. 验证 `dotnet build -c Release --no-incremental -warnaserror` 通过
4. 运行测试
5. 检查 `.github/PULL_REQUEST_TEMPLATE.md` 清单
6. 提交 PR

## 版本发布

发布到 NuGet 的完整流程（含版本号管理、tag 触发、回滚等）详见 [`docs/发布规范.md`](docs/发布规范.md)。

**简版流程**：
1. 改 `Directory.Build.props` 的 `<Version>`（唯一版本源，禁止 csproj 硬编码）
2. 同步改 README badge 版本号
3. 走 feature → dev → main PR 流程
4. main 合并后打 tag：`git tag v5.0.1 && git push origin v5.0.1`
5. release.yml 自动触发：test → pack 5 个包 → push → GitHub Release

## 项目结构

```
src/
  PalORM.Core/          — 运行时核心（DataSession / QueryBuilder / Resilience / etc.）
  PalORM.SourceGen/     — IIncrementalGenerator + DiagnosticAnalyzer
  PalORM.PostgreSql/    — Npgsql Provider + NOTIFY/LISTEN
  PalORM.MySql/         — MySqlConnector Provider
  PalORM.Sqlite/        — Microsoft.Data.Sqlite Provider
  PalORM.Testing/       — 测试夹具（TestDb + TestEnvironment）
test/
  PalORM.Core.Tests/        — 单元测试
  PalORM.SourceGen.Tests/  — 源生成器快照测试
  PalORM.Integration.Tests/— 集成测试（三方言）
  PalORM.AotTest*/          — Native AOT 冒烟测试
tools/
  PalORM.Scaffold/      — SQLite schema → C# 实体生成器
docs/                   — 文档（架构/API/规范/ADR/踩坑）
scripts/                — 通用脚本（基准/测试环境/包验证）
.ai/scripts/            — AI 质量脚本（本地工具，不入仓库）
```

## 许可证

AGPL v3 — 见 [LICENSE](LICENSE)
