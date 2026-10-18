# PalORM 脚本索引

> 通用脚本按职责分类索引。AI 系统脚本已移至 `.ai/scripts/`（本地工具，不入仓库）。

## 质量门禁（CI gate job 与 pre-commit 共用）

> 2026-10-18 审计：下列五项此前只在 pre-commit（或仅手动）跑，CI 无触发点，已接入
> `.github/workflows/verify.yml` 的 gate job。各脚本退出码契约统一为 0=通过 / 1=有 FAIL / 2=用法错。

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `gate-check.cs` | G1-G33 规范门禁（G10/G15/G16 为警告级） | ✅ pre-commit + verify.yml gate |
| `doc-consistency-check.cs` | D4/D10-D13 文档口径（`--fix` 同步 [Test] 计数） | ✅ pre-commit + verify.yml gate |
| `tech-debt-scan.cs` | 技术债 14 类（`--ci` 跳过领先远端与 tag 对账） | ✅ verify.yml gate |
| `test-gate.cs` | 测试规范 T4/T6/T8/T9/T11/T12 + T-DEF-1/4（`--ci` 语义保留） | ✅ verify.yml gate |
| `assertion-strength-check.cs` | 弱断言棘轮（基线见 `bench/baselines/assertion-baseline.json`，当前 0） | ✅ verify.yml gate |
| `gate-param-collection-reuse.cs` | R-UNNESTB 参数集合复用理由码门禁 | ✅ pre-commit + verify.yml gate |
| `assert-test-counts.cs` | 测试执行数地板断言（地板真源 `bench/baselines/test-counts.json`） | ✅ verify.yml unit/integration |
| `assert-coverage.cs` | 覆盖率地板断言（同上文件 `coverage` 段） | ✅ verify.yml unit |

## 提交前必检

> 本地防线经 `.githooks/pre-commit` 薄包装调用（转发仓库脚本，更新即时生效）。
> 安装：`git config core.hooksPath .githooks`（一次性，见 CONTRIBUTING.md）。

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `secret-guard.cs` | 敏感信息拦截（40 类；默认 staged 模式，`--range BASE..HEAD` 供 CI 扫差异集；file-based app，`dotnet run --file` 调用） | ✅ pre-commit + ci.yml security |
| `stub-check.cs` | Stub 方法门禁（检测 `=> this` 表达式体与 `NotImplementedException` 空壳） | ✅ pre-commit + ci.yml gate |
| `precommit-build-check.cs` | 受影响项目构建（编译面变更即全量 `PalORM.ci.slnf` Debug 增量；非编译文件跳过） | pre-commit 专属 |
| `test-quality-scripts.cs` | 脚本质量自检（CI 模式自动跳过 `.ai` 段，仍回归 stub/SDK/secret-guard/语言门禁四段） | ✅ pre-commit 语义 + ci.yml gate |

## 知识层与运维

| 脚本 | 用途 | 调用时机 |
|------|------|:---:|
| `backup-ai-system.cs` | `.ai` 知识层异地备份（69 个知识文件约 1 MB，排除约 600 MB 可再生实验产物；写 MANIFEST + SHA256 回读校验；`--verify` 只校验） | 手工 / 阶段收尾 |

## 性能基准

> 性能链统一入口已迁移为 `tools/PalORM.PerfCli`（脚本 C# 化整改方案 Phase 4）：
> `dotnet run --project tools/PalORM.PerfCli -- <smoke|full|compare|gate|report|index|bench-matrix|full-perf|dappersuite>`。

| 命令 | 用途 | 耗时 |
|------|------|:---:|
| `PerfCli bench-matrix` | 基准运行器（sqlite/pg/mysql/scale/build/speed/all） | 5-30min |
| `scripts/run-mutation-tests.cs` | 变异测试（Stryker.NET，验证测试有效性；CI 每周六自动跑 mutation-tests.yml，Core + SourceGen 双配置；`dotnet run --file` 调用） | 10-30min/项目 |

## 测试环境

> 原 `set-test-env.sh` 已淘汰：`.env.test` 的加载内置于 PerfCli 的 env loader
> （进程内读取后由子进程继承，不回显值）；跑测试由 `TestEnvironment` 自动补变量。

## 包验证

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `test-package-contract.cs` | NuGet 包契约验证（`dotnet run --file` 调用） | ✅ ci.yml aot |
