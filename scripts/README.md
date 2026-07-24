# PalORM 脚本索引

> 通用脚本按职责分类索引。AI 系统脚本已移至 `.ai/scripts/`（本地工具，不入仓库）。

## 提交前必检

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `stub-check.sh` | Stub 方法门禁（检测 `throw new NotImplemented`） | ✅ ci.yml gate |
| `test-quality-scripts.sh` | 脚本质量自检 | ✅ ci.yml gate |

## 性能基准

| 脚本 | 用途 | 耗时 |
|------|------|:---:|
| `run-benchmarks.sh` | 基准运行器（sqlite/pg/mysql/scale/build/speed/all） | 5-30min |
| `run-mutation-tests.sh` | 变异测试（Stryker.NET，验证测试有效性） | 10-30min |

## 测试环境

| 脚本 | 用途 |
|------|------|
| `set-test-env.sh` | 从 `.env.test` 加载 PG/MySQL 连接串（`source` 方式调用） |

## 包验证

| 脚本 | 用途 | CI 调用 |
|------|------|:---:|
| `test-package-contract.sh` | NuGet 包契约验证 | ✅ ci.yml aot |
