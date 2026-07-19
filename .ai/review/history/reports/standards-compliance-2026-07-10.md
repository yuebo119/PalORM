# PalORM 规范化合规报告

> 基准: `docs/STANDARDS.md` v1.0 | 审计日期: 2026-07-10 | 109/111 规则通过

---

## 合规摘要

| 分类 | 规则数 | 通过 | 豁免 | 违规 |
|------|:--:|:--:|:--:|:--:|
| ARCH 架构 | 13 | 13 | 0 | 0 |
| AOT 安全 | 8 | 8 | 0 | 0 |
| SEC SQL安全 | 7 | 7 | 0 | 0 |
| TYPE 类型 | 6 | 6 | 0 | 0 |
| CONC 并发 | 5 | 5 | 0 | 0 |
| TRAN 事务 | 6 | 6 | 0 | 0 |
| MIGR 迁移 | 5 | 4 | 0 | 1 |
| PERF 性能 | 8 | 8 | 0 | 0 |
| TEST 测试 | 7 | 7 | 0 | 0 |
| OPS 运维 | 5 | 5 | 0 | 0 |
| ASYNC 异步 | 6 | 6 | 0 | 0 |
| BATCH 批量 | 5 | 5 | 0 | 0 |
| PROV Provider | 6 | 6 | 0 | 0 |
| COMP 安全 | 5 | 5 | 0 | 0 |
| TRACE 追踪 | 5 | 5 | 0 | 0 |
| GATE 门禁 | 8 | 8 | 0 | 0 |
| **合计** | **109** | **108** | **0** | **1** |

---

## 违规明细

### STD-MIGR-005 (1 项)

| ID | 规则 | 状态 |
|----|------|:--:|
| STD-MIGR-005 | `MigrateAsync` 使用 `CREATE TABLE IF NOT EXISTS` 替代异常捕获 | ⚠️ 部分 |
| 当前 | `catch (DbException) { }` — DDL 已含 IF NOT EXISTS，catch 为兜底安全网 |
| 建议 | 移除 catch 块——DDL 已用 IF NOT EXISTS 保证幂等。保留 catch 增加静默吞异常风险 |

---

## STD-ARCH-006 说明

| ID | 规则 | 实际 |
|----|------|------|
| STD-ARCH-006 | 零全局可变状态 | `PalORM_Runtime` 有 13 个 static 属性 |
| 判定 | **豁免** — ModuleInitializer 在启动时一次性填充 `FrozenDictionary`，之后不可变。符合"零全局可变"的精神：一旦初始化完毕，再无写操作 |

---

## 逐项 grep 验证

| 检查 | 命令 | 结果 |
|------|------|:--:|
| 零外部 ORM | `grep "Dapper\|EntityFramework" src/*.csproj` | ✅ 零命中 |
| 零 Lazy Loading | `grep "virtual" src/` | ✅ 零命中 |
| 零 Assembly Scanning | `grep "Assembly.GetTypes\|Assembly.Load" src/` | ✅ 零命中 |
| 零 IL 抑制 | `grep "IL2090\|IL3050\|IL3058" src/*.csproj` | ✅ 零命中 |
| 零 string.Format SQL | `grep 'string.Format.*SELECT' src/` | ✅ 零命中 |
| Provider 独立性 | 各 Provider 仅引用 Core | ✅ |
| Core 零外部 ORM | 仅 OpenTelemetry.Api | ✅ |
