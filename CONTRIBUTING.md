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

# 可选但推荐：BenchmarkDotNet 本地 fork（仅基准项目需要）。
# bench/PalORM.Benchmarks 以 ProjectReference 引用 bench/BenchmarkDotNet（gitignore 排除，
# 不入仓库——上游 master 已含 net11 支持）。不跑基准/性能门禁可跳过本步，
# 但跳过时请用下方 PalORM.dev.slnf 构建（见"构建"两路径说明）。
git clone --depth 1 https://github.com/dotnet/BenchmarkDotNet.git bench/BenchmarkDotNet

# 安装本地提交防线（敏感信息拦截 + stub 门禁）——一次性，必须执行
git config core.hooksPath .githooks

# 构建——两条路径任选其一：
# ① 全量（含基准项目，需先克隆 BDN fork，见上）：
dotnet build PalORM.slnx -c Debug
# ② 核心开发（CI 同款，不含 bench，全新克隆零额外依赖即可成功）：
dotnet build PalORM.ci.slnf -c Debug

# 运行 SQLite 测试（无需外部数据库）
dotnet run --project test/PalORM.Core.Tests -c Debug
dotnet run --project test/PalORM.Integration.Tests -c Debug

# 可选：设置外部数据库连接串
cp .env.test.example .env.test
# 编辑 .env.test 填入本地 PG/MySQL 凭据——无需再手动 source，
# TestEnvironment 会在解析连接串时自动补入其中缺失的 PALORM_* 变量
```

> **注意**：未配置 `.env.test`（且未设置 `PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION`）时
> 外部数据库集成测试会显式失败（fail-fast 设计），单元测试与 SQLite 集成测试不受影响。
>
> **优先级**：显式环境变量 > `.env.test` > 报错。已设置的环境变量恒不被文件覆盖，
> 故 CI 注入 secret 的路径完全不读该文件。
>
> `source scripts/set-test-env.sh` 仍可用（例如想在 shell 里跑 `psql`），但已不是跑测试的必需步骤。

## 代码规范

### 编译纪律（强制）
- `TreatWarningsAsErrors=true`——构建零警告零错误
- 分析器分层（独立审计 DOC2 修正，对齐各 csproj 实际值）：Directory.Build.props 设
  `latest-all`；**PalORM.Core 当前放宽为 `latest-minimum`**（Phase 1 的历史 TODO，
  拉回 `latest-all` 分批节奏见整改账本 M1-6）；**PalORM.SourceGen 因 netstandard2.0 +
  Roslyn 分析器宿主不兼容 SonarAnalyzer 而整体 `none`**（csproj 注释有据）——其余项目
  继承 `latest-all`
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
