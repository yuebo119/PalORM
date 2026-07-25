# PalORM PR 检查清单

## 编译验证
- [ ] `dotnet build -c Debug` 0 警告 0 错误
- [ ] 全量重建（`--no-incremental`）通过
- [ ] AOT 项目（`AotTest.Pg` / `AotTest.MySql`）单独验证
- [ ] CI 干净环境 `dotnet restore` 通过（NuGet.Config packageSourceMapping 约束的包在 CI 源可用）

## 测试验证
- [ ] `PalORM.Core.Tests` 全绿（173+ 用例）
- [ ] `PalORM.SourceGen.Tests` 全绿（104+ 用例，含快照比对）
- [ ] `PalORM.Integration.Tests` 全绿（171+ 用例，含 PG/MySQL 真实 DB）
- [ ] 如改 emit 模板，`PALORM_UPDATE_SNAPSHOTS=1` 重生成并评审 diff
- [ ] 远程 DB 测试用 `PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION`（.env.test），不用基准变量

## SonarAnalyzer 守护
- [ ] 未引入新的 P0 错误（S2068 凭据 / S6966 await / S5034 ValueTask / S108 空块）
- [ ] 未引入新的 P1 警告（S3776 复杂度 / S107 参数 / S927 接口名）
- [ ] 新增 `[SuppressMessage]` 必附 Justification（说明拆分不适用原因）

## 三方一致
- [ ] 公共 API 变更已同步文档（`docs/API参考.md` / `README.md`）
- [ ] 包版本号变更已同步所有 Provider csproj + CHANGELOG + 架构设计.md
- [ ] 连接串调优参数变更已同步 CHANGELOG + Provider XML 注释
- [ ] 方言行为差异（如 SQLite BulkUpdateBatch 回退）已同步 BENCHMARKS.md + CHANGELOG
- [ ] 计数/规则数变更已同步文档
- [ ] 命名/路径变更已 grep 全仓库零残留

## 反模式预防
- [ ] 未引入巨型 Lambda（lambda 体 <= 10 行）
- [ ] 未引入参数 > 7（除非有 Obsolete 兼容包袱）
- [ ] 未引入硬编码凭据（Password=xxx）
- [ ] 未引入同步 ADO.NET 调用
- [ ] 未引入空块 / 空 catch（用 `Assert.ThrowsAsync` 或 `=> _ = (...)`）

## 架构精炼守护（v5.0 新增）
- [ ] 未引入 `[Obsolete]` 而无明确移除版本的公共 API
- [ ] 未引入恒 null/false 预留字段（TableModel/DbOptions 等）
- [ ] 未引入逐字复制 ≥ 3 处的重复代码（抽到 helper 或上提接口）
- [ ] 未引入单文件 > 800 行的 God Object（考虑 partial 拆分）
- [ ] 未引入零消费点的死代码（grep 全仓库验证）
- [ ] partial 拆分时每个文件 using 列表按自身依赖独立确定
- [ ] partial 拆分时检查首方法 XML 注释（CS1591）和末尾悬空注释（CS1587）

## v5.0 方言与驱动守护（2026-07-25 新增）
- [ ] 跨方言 API 验证了三方言行为一致或明确标注差异（SQLite/PG/MySQL）
- [ ] 阈值分流改为能力检测（如 local_infile=ON 而非行数阈值）
- [ ] 第三方库升级前查 release notes（不凭印象）——破坏性变更需手工适配
- [ ] SourceGen 改动保持 netstandard2.0 + EnforceExtendedAnalyzerRules
- [ ] NuGet.Config packageSourceMapping 约束的包在 nuget.org 也可还原（CI 兼容）
