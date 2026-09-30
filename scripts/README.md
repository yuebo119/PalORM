# PalORM 脚本索引

> 通用脚本按职责分类索引。AI 系统脚本已移至 `.ai/scripts/`（本地工具，不入仓库）。

## 提交前必检

> 本地防线经 `.githooks/pre-commit` 薄包装调用（转发仓库脚本，更新即时生效）。
> 安装：`git config core.hooksPath .githooks`（一次性，见 CONTRIBUTING.md）。

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `secret-guard.cs` | 敏感信息拦截（40 类；默认 staged 模式，`--range BASE..HEAD` 供 CI 扫差异集；file-based app，`dotnet run --file` 调用） | ✅ pre-commit + ci.yml security |
| `stub-check.cs` | Stub 方法门禁（检测 `=> this` 表达式体与 `NotImplementedException` 空壳） | ✅ pre-commit + ci.yml gate |
| `test-quality-scripts.cs` | 脚本质量自检（CI 模式自动跳过 `.ai` 段，仍回归 stub/SDK/secret-guard/语言门禁四段） | ✅ pre-commit 语义 + ci.yml gate |

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
