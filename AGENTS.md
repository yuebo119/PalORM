# PalORM 项目级 AI 规范

> 本文件被 ZCode 自动加载（项目根目录 AGENTS.md 约定）。
> 与全局 `~/.zcode/AGENTS.md` 的分工：全局管行为准则（Karpathy 准则），项目级管项目规范。

## 启动清单（每次会话开始确认）

```
1. git status——工作树清洁？
2. dotnet build -c Debug——0 警告 0 错误？
3. 技术债扫描——bash .ai/scripts/tech-debt-scan.sh 12/12 通过？（本地工具，不入仓库）
4. .ai/lessons.md——已读最新版？（本地工具，不入仓库）
```

## 规范真源（按优先级）

| 优先级 | 文件 | 用途 |
|-------|------|------|
| **最高** | `.editorconfig` | SonarAnalyzer 38 条规则（P0+P1 error，编译期阻断） |
| **高** | `.ai/lessons.md` | 规范系统手册 v7.0（22 缺陷，本地工具，不入仓库） |
| **高** | `.ai/test/prompt.md` | 测试规范系统 v1.0（14 铁律 + 16 缺陷，本地工具，不入仓库） |
| **中** | `docs/编码规范.md` §18 | SonarAnalyzer 守护层规则文档化 |
| **参考** | `.github/PULL_REQUEST_TEMPLATE.md` | PR 检查清单 6 类 |

## 四系统（按需触发）

| 命令 | 系统 | 用途 |
|------|------|------|
| `/review` | 审计+评审 | 地毯式逐行代码审查（5 档位） |
| `/gate` | 门禁 | G1-G32 规范合规检查（编译前阻断） |
| `/refine` | 精炼 | 24+3 项操作矩阵（更优实现） |
| `/test` | 测试规范 | T1-T12 测试铁律 + 覆盖矩阵 + 基准配置规范 |

### v4.0 会话经验沉淀（2026-07-23）

以下经验基于 v4.0 性能优化 + 测试体系构建 + 四系统全链路执行实测验证，已融入四系统：

**性能优化经验（融入 /review + /refine）**：
- **方案调研可能误判瓶颈**：4 路并行 Agent 调研发现"QueryBuilder O(N²) 拷贝"，实施时实测占 QueryAll 0.02%——微秒级非瓶颈。SOP：实施前用 benchmark 验证方案声称的瓶颈是否属实（B10）。
- **review Agent 推理不验证代码**：R1 声称 BulkMerge operationOwner 坍缩，实际 `operationOwner ??` 参数优先生效。SOP：P0/P1 定级前必须写复现测试或 grep 确认（B11）。
- **方案文字与实施代码偏差**：v3.1 方案写"保留接口 + 标 Obsolete"自相矛盾。核心路径严格对齐，辅助优化允许 S3 反向验证取舍（B9）。

**测试体系经验（融入 /test T13/T14）**：
- **SQL 断言必须先读生成代码**：LimitOffset 测试假设 `Take(5)` 无 `Skip` 不含 OFFSET，实际生成 `OFFSET 0`。不看代码的 SQL 断言 = 猜测（B15）。
- **优先行为断言避免裸 IsNotNull**：弱断言从 30 削减到 11——RuntimeFields/found/loaded/factory 改行为断言。例外：try-catch 异常/异步通知/static abstract 委托（T14）。
- **测试计数口径统一**：badge 427（声明数）vs 通过 419（实际）vs 标注 167（源码 `[Test]`）三者冲突。全仓库统一为 CI 通过数（B14）。

**工具纪律经验（融入 /gate + /refine）**：
- **Edit 替换 emit 代码误删相邻行**：MigrationEmitter R11 修复时 old_string 含了 defaultClause/primaryKey——new_string 丢失。SOP：old_string 只含目标行 ± 1 行（B12）。
- **门禁正则匹配注释内容**：G24 perl `await` 统计含 GridReader 注释中的 "await 会导致"。SOP：正则统计前剥离 `///` 和 `//`（B13）。
- **emit 变更必须清 obj/bin**：RowFactoryEmitter/CommandFactoryEmitter 改动后增量构建复用旧 emit 导致运行时 NRE。SOP：`rm -rf src/*/obj src/*/bin`（B8）。

### v5.0 会话经验沉淀（2026-07-25）

以下经验基于 v5.0 大型重构（40+ 次提交）实测验证，已融入四系统：

**门禁系统新增（G29-G30）**：
- **G29 方言感知验证**：跨方言 API（BulkUpdateBatchAsync / 连接串调优）必须验证三方言行为一致或明确标注差异。SQLite CASE WHEN 比 PG FROM VALUES 慢 6.4x——方言回退是正确解。
- **G30 NuGet.Config 源约束 CI 验证**：packageSourceMapping 限制的包必须在 CI 干净环境验证可还原。dotnet-tools 源不保证有 stable 版本（5.6.0 只在 nuget.org）。

**精炼系统新增（O25-O27）**：
- **O25 阈值检测改为能力检测**：MySQL BulkInsert 从 `count >= 2000`（伪精确）改为 `local_infile == ON`（环境能力）。阈值是隐式分流，能力检测是显式选择。
- **O26 方案 Y 双方法优于三模式配置**：BulkUpdate（逐条+乐观锁）vs BulkUpdateBatchAsync（批量无锁）——两种正确语义用双方法分离，不用阈值/模式开关。API 即文档。
- **O27 SourceGen WithComparer 增量缓存**：sealed record + EquatableArray 已有值相等，但 Roslyn 默认用 ReferenceEqualityComparer。显式 `.WithComparer(EqualityComparer<T>.Default)` 提高缓存命中率。

**测试系统新增（T11-T12）**：
- **T11 远程 DB 环境变量命名分离**：测试用 `PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION`（.env.test），基准用 `PALORM_BENCH_PG` / `PALORM_BENCH_MYSQL`。两套不混用——测试需 SET GLOBAL local_infile=ON，基准不应有副作用。
- **T12 三方一致验证扩展**：代码-文档-注释同步从"公共 API 签名"扩展到"包版本号+连接串参数+PRAGMA+方言行为差异"。v5.0 连接串调优参数（MaxAutoPrepare=100 等）必须在 CHANGELOG + 架构设计.md 同步。

**审计系统经验（融入 /review 现有流程）**：
- **调研先于实施**：第三方库升级前必须查 release notes（不能凭印象）。TUnit 0.19→1.61 调研说"零改动"，实际有 4 类破坏（ThrowsAsync 签名/返回类型/弃用/分析器规则）。
- **ADR 用于重大架构决策**：DbDataSource 单例化用 ADR-E 记录，含 5 个 ORM 实践对比 + EF Core #3086 反面证据 + 触发 revisit 条件。决策不是"不做"，是"有条件不做"。
- **基准验证不做项的可行性**：3.4 NpgsqlParameter<T> 零装箱不做——用 BenchmarkDotNet + 手写微基准实测装箱占比 ~24.5%，但 PG COPY/MySQL BulkCopy 已无装箱，实际收益极小。用数据决策，不用直觉。

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
- **贡献指南**：`CONTRIBUTING.md`
- **变更日志**：`CHANGELOG.md`
