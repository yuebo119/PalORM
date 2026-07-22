# PalORM 脚本索引

> 18 个脚本按职责分类索引。所有脚本均需 `bash scripts/xxx.sh` 执行。

## 提交前必检

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `tech-debt-scan.sh` | 12 项技术债扫描（版本号/测试数/Obsolete/TODO） | ✅ ci.yml gate |
| `gate-check.sh` | 仓库门禁（跟踪文件检查/编码规范） | ✅ ci.yml gate |
| `stub-check.sh` | Stub 方法门禁（检测 `throw new NotImplemented`） | ✅ ci.yml gate |
| `assertion-strength-check.sh` | 断言强度门禁（检测 `Assert.Pass()` 无断言测试） | ✅ ci.yml gate |
| `test-quality-scripts.sh` | 脚本质量自检 | ✅ ci.yml gate |
| `doc-consistency-check.sh` | 文档一致性检查 | ✅ ci.yml gate |

## 性能基准

| 脚本 | 用途 | 耗时 |
|------|------|:---:|
| `run-benchmarks.sh` | 基准运行器（sqlite/pg/mysql/scale/build/speed/all） | 5-30min |
| `run-mutation-tests.sh` | 变异测试（Stryker.NET，验证测试有效性） | 10-30min |

## 测试环境

| 脚本 | 用途 |
|------|------|
| `set-test-env.sh` | 从 `.env.test` 加载 PG/MySQL 连接串（`source` 方式调用） |

## AI 系统验证

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `verify-ai-system.sh` | AI 规范系统一致性检查 | ✅ ci.yml gate |
| `verify-action-items.sh` | 评审行动项验证 | 手动 |
| `verify-phase.sh` | 阶段完成度验证 | 手动 |
| `install-ai-system.sh` | AI 系统安装到新项目 | 手动 |

## 代码审查

| 脚本 | 用途 |
|------|------|
| `review-scope.sh` | 审查范围扫描（确认无越界改动） |
| `review-snapshot.sh` | 快照审查辅助 |
| `refine-scan.sh` | 精炼矩阵扫描 |
| `probe-template.sh` | 模板探测 |
| `test-package-contract.sh` | NuGet 包契约验证 | ✅ ci.yml aot |
