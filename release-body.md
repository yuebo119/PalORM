# PalORM v5.2.0

**[与 v5.1.0 对比](https://github.com/yuebo119/PalORM/compare/v5.1.0...v5.2.0)** |
[NuGet](https://www.nuget.org/packages/PalORM.Core) |
[完整变更日志](https://github.com/yuebo119/PalORM/blob/main/CHANGELOG.md)

## 📦 发布包

| 包名 | 版本 |
|------|------|
| PalORM.Core | 5.2.0 |
| PalORM.Sqlite | 5.2.0 |
| PalORM.MySql | 5.2.0 |
| PalORM.PostgreSql | 5.2.0 |
| PalORM.SourceGen | 5.2.0 |

## 📥 安装

```bash
# 按需安装 Provider（Core 是必需的）
dotnet add package PalORM.Core --version 5.2.0

# SQLite（零配置即装即用）
dotnet add package PalORM.Sqlite --version 5.2.0

# MySQL / PostgreSQL
dotnet add package PalORM.MySql --version 5.2.0
dotnet add package PalORM.PostgreSql --version 5.2.0
```

## 🚀 30 秒上手

```csharp
// 1. 定义实体（record 也可）
[Table("users")]
public sealed partial class User
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

// 2. 连接 + 操作（编译期生成全部 SQL，零反射零装箱）
await using var db = await DataSession<SqliteProvider>.CreateAsync(
    new DbOptions { ConnectionString = "Data Source=:memory:" });
await db.MigrateAsync();
await db.InsertAsync(new User { Name = "Alice" });
var user = await db.GetAsync<User>(1);
```

## 📋 变更明细

> 本版本包含 14 轮自动化 AI 评审-修复迭代（56→0 缺陷收敛），全部修复均经独立验证轮确认。

### ✨ 新增

- **record 实体支持**：`[Table] record` 声明现在完全可生成（get;set 属性真生成、
  位置参数/init-only 键有 Error 级定位诊断）——三支分形态全覆盖
- **隔离级别全链透传**：`WithIsolationLevel()` 现在在所有自开事务路径生效
  （ToPageAsync/BulkInsert/BulkCopy/fallback——此前仅单条 CRUD 生效）
- **PALORM015/022/024/025/031/032/033/034/036** 诊断规则修复与增强
  （推断式泛型调用/Nullable 时间/init-only 并发令牌/分析器谓词对齐真源）

### 🐛 修复

- **MySQL Upsert 主键回填**（ITM-608）：LAST_INSERT_ID 死分支 + 缺 SELECT 后缀——v4.1 预构建引入的回归
- **Raw 子句分页 Total 虚高**（ITM-609）：BuildCountSql 补 Raw 消费
- **PG bool JSON 恒不匹配**（ITM-610）：Convert.ToString(bool) 产 "True" 与 jsonb 小写 "true" 恒不等
- **Audit 脱敏承诺击穿**（ITM-611）：logParameters=false 仍渲染 exception.ToString()（含 PG DETAIL PII）
- **连接池参数静默覆盖**（ITM-612）：用户连接串显式 Max Pool Size 被 DbOptions 默认值改写
- **Bulk 家族空列表+未注册类型**（ITM-637 六侧收口）：六方法统一"一致抛"语义
- **PgListener 启动挂起**（S1）：首连 LISTEN 瞬态失败→重连成功路径 TrySetResult 修复
- **ToPageAsync 缓存键污染**（S-A）：页截断结果写入用户缓存键致同键 ToListAsync 静默丢行
- **Take/Skip UPDATE 扩大范围**（D-1）：BuildUpdateSql 守卫族补 Take/Skip（静默丢 LIMIT）
- **PALORM024 谓词锚错真源**（D-A）：分析器与生成器 IsUpdatableColumn 对齐

### 📦 依赖

- Microsoft.Data.Sqlite.Core 11.0.0-preview.6 → preview.7
- MySqlConnector 2.6.1 → 2.6.2
- SQLite3MC.PCLRaw.bundle 2.3.6 → 2.4.0
- SonarAnalyzer.CSharp 10.30 → 10.32
- Microsoft.CodeAnalysis.Analyzers 5.3.0 → 5.6.0
- Microsoft.SourceLink.GitHub 8.0.0 → 10.0.400
- TUnit / TUnit.Assertions 1.61.38 → 1.65.0

### 🧪 测试

- 507 个测试全绿（Core 191 + SourceGen 144 + Integration 179 含 PG/MySQL 真库）
- Native AOT 三平台 publish + 原生运行通过
- 快照基线 13 份一致
- 14 轮独立评审验证（B37 收敛纪律）

### 📚 参考

- [ADR-G/H/I](docs/adr/)：LocalInfile 安全策略 / IRowFactory 处置 / legacy 移除窗口
- [AI 质量系统](.ai/README.md)：V1-V15 防线 + B30-B37 教训库

---

| 资源 | 链接 |
|------|------|
| 📖 文档 | [README](https://github.com/yuebo119/PalORM#readme) |
| 📋 全部变更 | [CHANGELOG.md](https://github.com/yuebo119/PalORM/blob/main/CHANGELOG.md) |
| 🏗️ 架构决策 | [ADR 目录](https://github.com/yuebo119/PalORM/tree/main/docs/adr) |
| 🐛 报告问题 | [Issues](https://github.com/yuebo119/PalORM/issues) |