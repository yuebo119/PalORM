# PalORM PR 检查清单

## 编译验证
- [ ] `dotnet build -c Debug` 0 警告 0 错误
- [ ] 全量重建（`--no-incremental`）通过
- [ ] AOT 项目（`AotTest.Pg` / `AotTest.MySql`）单独验证

## 测试验证
- [ ] `PalORM.Core.Tests` 全绿（156+ 用例）
- [ ] `PalORM.SourceGen.Tests` 全绿（105+ 用例，含快照比对）
- [ ] `PalORM.Integration.Tests` SQLite 部分全绿（149+ 用例）
- [ ] 如改 emit 模板，`PALORM_UPDATE_SNAPSHOTS=1` 重生成并评审 diff

## SonarAnalyzer 守护
- [ ] 未引入新的 P0 错误（S2068 凭据 / S6966 await / S5034 ValueTask / S108 空块）
- [ ] 未引入新的 P1 警告（S3776 复杂度 / S107 参数 / S927 接口名）
- [ ] 新增 `[SuppressMessage]` 必附 Justification（说明拆分不适用原因）

## 三方一致
- [ ] 公共 API 变更已同步文档（`docs/API参考.md` / `README.md`）
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
