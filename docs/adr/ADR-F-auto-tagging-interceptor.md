# ADR-F：Auto Tagging Interceptor

> 状态：**已批准**（2026-07-28 实施完成）· 来源：v5.1 Auto Tagging 特性

## 背景

PalORM 已有手动 Tagging 能力（`QueryBuilder.TagWithCaller()`），但需用户**每次查询显式调用**，80% 场景会忘记。生产排障时 SQL 日志无法定位源码调用点。

EF Core 社区 2025 年用 **SourceGen Interceptors**（.NET 8+ 特性）实现自动 Query Tagging，用户零代码改动。PalORM 沿此路径实施。

## 决策

**批准实施** Auto Tagging Interceptor，opt-in 模式（默认关闭）。

### 设计要点

1. **opt-in 开关**：消费侧 csproj 设 `<PalORMAutoTagging>true</PalORMAutoTagging>` 启用。默认关闭——不影响现有行为
2. **6 个终态方法**：`ToListAsync` / `FirstAsync` / `FirstOrDefaultAsync` / `SingleAsync` / `SingleOrDefaultAsync` / `ExecuteNonQueryAsync`
3. **路径规范化**：编译机绝对路径 → 相对工作目录（避免泄露目录结构到 DB 日志）；fallback 仅文件名
4. **AOT 全兼容**：Interceptors 是编译期机制，运行时零反射；net11 AOT publish 0 警告实测通过

### 关键技术约束（实施过程发现）

| 约束 | 说明 |
|------|------|
| `GetInterceptableLocation` 是扩展方法 | 在 `CSharpExtensions` 类，非 `SemanticModel` 实例方法——首次 PoC 用错 API 位置 |
| `InterceptsLocationAttribute` 命名空间 | 编译器硬编码要求 `System.Runtime.CompilerServices`（不能放自定义命名空间） |
| 拦截器类型命名空间 | 需在 `InterceptorsNamespaces` 声明（PalORM 用 `PalORM.Generated`） |
| MSBuild Property 传递 | 需 `<CompilerVisibleProperty>` 显式声明，否则源生成器读不到 |
| block namespace（非 file-scoped） | 文件含两个 namespace 声明时不能用 file-scoped（CS8954） |

## 验证

- ✅ `PalORMAutoTagging=true` 项目，6 方法调用自动生成 `/* 相对路径:行号 方法名 */` SQL 注释
- ✅ 不设置开关（默认关闭）时零行为变化、零生成物
- ✅ `test/PalORM.AotTest.MySql` 启用后 `dotnet publish -p:PublishAot=true` 0 警告
- ✅ SQL 注释路径是**相对路径**（非编译机绝对路径）
- ✅ 125 个 SourceGen 测试全部通过（4 个新增 AutoTagging 测试 + 121 个既有测试零回归）

## 参考

- 设计文档：`docs/v5.1-auto-tagging-design.md`
- EF Core 参考实现：[Thirty25 博客](https://thirty25.blog/blog/2025/04/ef-core-source-gen-interceptors)
- Roslyn Interceptors 官方文档：[dotnet/roslyn interceptors.md](https://github.com/dotnet/roslyn/blob/main/docs/features/interceptors.md)
- B28 教训：`.ai/lessons.md` AB 章节（特性推荐四问 SOP）
