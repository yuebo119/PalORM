# PalORM 脚本索引

> 通用脚本按职责分类索引。AI 系统脚本已移至 `.ai/scripts/`（本地工具，不入仓库）。

## 质量门禁（CI gate job 与 pre-commit 共用）

> 2026-10-18 审计：下列五项此前只在 pre-commit（或仅手动）跑，CI 无触发点，已接入
> `.github/workflows/verify.yml` 的 gate job。各脚本退出码契约统一为 0=通过 / 1=有 FAIL / 2=用法错。

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `gate-check.cs` | G1-G33 规范门禁（G10/G15/G16 为警告级） | ✅ pre-commit + verify.yml gate |
| `doc-consistency-check.cs` | D4/D10-D14 文档口径（`--fix` 同步 [Test] 计数；D13 G 级对账 / D14 T 级严重度对账） | ✅ pre-commit + verify.yml gate |
| `tech-debt-scan.cs` | 技术债 15 类（`--ci` 跳过领先远端与 tag 对账；#15 脚本索引完整性） | ✅ verify.yml gate |
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
| `verify-ai-system.cs` | `.ai` 系统一致性校验 V1-V27（提示词引用可解析、口径数字对账、防线能失败、提交锚验存、活文档完整性）；`--fast` 跳过副作用项，`--public` 仅跑主仓可见子集 | pre-commit + 手工全量 |

## 四系统工具（按需调用，非门禁）

| 脚本 | 用途 | 触发 |
|------|------|:---:|
| `refine-scan.cs` | 精炼扫描：27 项操作矩阵的机械命中数（可 grep 的 17 项出量级，7 项需 Roslyn 标 [人工]） | `/refine` 阶段 0 |
| `review-scope.cs` | review 范围清单与覆盖度账本（分片 + 逐文件勾销） | `/review` 执行门禁 |
| `review-snapshot.cs` | 仓库基线快照（可整段贴进评审报告；`--no-build` 跳过构建） | `/review` 执行门禁 |
| `sibling-map.cs` | 三方言姊妹族枚举：轴 A 机械生成同名方法族，轴 B 语义种子表真源在 `.ai/review/sibling-map.md` | 修复 brief 前评估联动面 |
| `swing-analysis.cs` | 全键比值摆动分布（选择门禁「宽散布」阈值时重跑） | 门禁阈值调参 |
| `probe-template.cs` | 探针骨架生成器（约 30 秒出最小工程，review probe-first 基建） | P0/P1 定级前的探针实证 |
| `verify-action-items.cs` | 行动项账本中「文件路径引用」的真实性校验（只核可机械判真的路径，标识符跳过） | `/review` 事后验证回路 |
| `verify-phase.cs` | 阶段完成验证（S1 构建 / S2 空壳 / S3 测试 / S4 阶段特定） | 阶段收尾 |
| `probe-pgvector.cs` | pgvector 向量门核对清单（在有扩展的 PG 库上验证 Raw 配方六项形态；无扩展时退出 2） | 向量场景变更 |

## 发布

| 脚本 | 用途 | 调用时机 |
|------|------|:---:|
| `pre-release-check.cs` | 发布前预检：模拟 CI 消费者路径（v6.1.0 发布事故后固化） | 打 tag 前 |
| `release-version-scan.cs` | 升版本时旧版本号字面量残留扫描（版本字面量多处同步的机械兜底） | 升版本提交前 |

## 性能基准

> 性能链统一入口已迁移为 `tools/PalORM.PerfCli`（脚本 C# 化整改方案 Phase 4）：
> `dotnet run --project tools/PalORM.PerfCli -- <smoke|full|compare|gate|report|index|bench-matrix|full-perf|dappersuite>`。

| 命令 | 用途 | 耗时 |
|------|------|:---:|
| `PerfCli bench-matrix` | 基准运行器（sqlite/pg/mysql/scale/build/speed/all） | 5-30min |
| `compare-perf-batches.cs` | 跨批次中位值对照：`--date yyyyMMdd` 选日、`--items csv` 选项（`方言/操作/行数`、`方言/操作`、`操作` 三形态）、`--list` 列当日全部项；缺省按摆动 `--swing`（默认 1.30×）自动挑动过的项。单位按项目口径（µs 一位/三位，≥1000 µs 转 ms） | 秒 |
| `scripts/run-mutation-tests.cs` | 变异测试（Stryker.NET，验证测试有效性；CI 每周六自动跑 mutation-tests.yml，Core + SourceGen 双配置；`dotnet run --file` 调用） | 10-30min/项目 |

## 测试环境

> 原 `set-test-env.sh` 已淘汰：`.env.test` 的加载内置于 PerfCli 的 env loader
> （进程内读取后由子进程继承，不回显值）；跑测试由 `TestEnvironment` 自动补变量。

## 包验证

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `test-package-contract.cs` | NuGet 包契约验证（`dotnet run --file` 调用） | ✅ ci.yml aot |
