# PalORM Native AOT 部署指南

> 严格 Native AOT 验收要求发布后运行真实数据库路径。SQLite 已有本机原生运行证据；PostgreSQL/MySQL 在 CI 服务容器实际运行前保持“待验证”。任何 IL/AOT 警告均不得抑制。

## 快速开始

```bash
dotnet publish test/PalORM.AotTest -c Release -r win-x64 \
  --self-contained true -p:PublishAot=true -p:PublishTrimmed=true \
  -p:JsonSerializerIsReflectionEnabledByDefault=false -o artifacts/aot/sqlite
./artifacts/aot/sqlite/PalORM.AotTest.exe
```

发布后得到原生二进制，不含 JIT、IL 或运行时反射。二进制大小取决于 SDK、RID、Provider 和链接器版本，必须以实际发布产物为准。

## 前提条件

1. 本仓库使用 `global.json` 固定的 **.NET 11 preview SDK**；消费者使用与目标 `net11.0` 兼容的 SDK
2. 项目文件中 `IsAotCompatible=true`
3. 实体类是顶级非泛型 `partial class`；可使用 `public` 或 `internal` 可见性

## 三 Provider AOT 状态

| Provider | AOT 状态 | NuGet 包 | 原生依赖 |
|----------|---------|----------|---------|
| **SQLite** | ✅ 本机原生运行通过 | `Microsoft.Data.Sqlite.Core` | `e_sqlite3mc`（随发布目录部署） |
| **PostgreSQL** | ⚠️ 原生 publish 通过，CI 运行待验证 | `Npgsql` | 无 (纯托管) |
| **MySQL** | ⚠️ 原生 publish 通过，CI 运行待验证 | `MySqlConnector` | 无 (纯托管) |

### SQLite — 推荐 AOT 首选

当前固定的 `Microsoft.Data.Sqlite.Core 10.0.10` 搭配 `SQLite3MC.PCLRaw.bundle 2.3.6`。Bundle 为目标 RID 提供 `e_sqlite3mc` 原生库，发布时该原生资产与应用一起部署；复制产物时必须保留完整发布目录。ADO.NET 层和原生 SQLite 安全更新可独立升级，未配置密码时保持普通 SQLite 行为。

验证：
```bash
dotnet publish test/PalORM.AotTest -c Release -r win-x64 \
  --self-contained true -p:PublishAot=true -p:PublishTrimmed=true \
  -p:JsonSerializerIsReflectionEnabledByDefault=false -o artifacts/aot/sqlite
./artifacts/aot/sqlite/PalORM.AotTest.exe
# 预期：PalORM AOT verification PASSED
```

### PostgreSQL — Npgsql AOT

`Npgsql` 是纯托管 ADO.NET Provider，不依赖 `libpq`。PalORM 不启用 Npgsql 运行时 JSON 类型映射；OwnedJson 只走 PalORM Source Generator 与 STJ `JsonTypeInfo<T>`。任何 Npgsql 路径产生的 IL/AOT 警告都阻断验收；不得通过抑制继续发布。最终状态以 CI 服务容器中的原生二进制 CRUD、并发、OwnedJson、批量插入和批量软删除运行结果为准。

### MySQL — MySqlConnector AOT

`MySqlConnector` 纯托管实现，无原生依赖。理论上 AOT 兼容性最好，但未在生产中大规模验证。

⚠️ MySqlConnector 的连接池和 SSL/TLS 路径需要 AOT 链路验证。

## AOT 限制（PalORM 通用）

PalORM 运行时路径按严格 Native AOT 约束实现；以下机制不依赖运行时反射或动态代码，最终兼容性以原生发布和数据库运行矩阵为准：

| 特性 | JIT 实现 | AOT 实现 | 状态 |
|------|---------|---------|------|
| 实体物化 (RowFactory) | IIncrementalGenerator 生成 | ✅ 同 | 编译器保证 |
| SQL 生成 (CommandFactory) | IIncrementalGenerator 生成 | ✅ 同 | 编译器保证 |
| 参数绑定 (BindInsert/BindUpdate) | IIncrementalGenerator 生成 | ✅ 同 | 编译器保证 |
| 类型转换 (TypeMapper) | 编译时 Map 函数 | ✅ 同 | 编译器保证 |
| ModuleInitializer 注册 | ModuleInitializer | ✅ 同 | 运行时启动 |
| FrozenDictionary 查找 | FrozenDictionary | ✅ 同 | BCL 保证 |
| JSON (OwnedJson) | `string` 原始 JSON；对象使用 STJ 源生成 | ✅ 同 | 对象需 `[OwnedJson(typeof(TContext))]` + `[JsonSerializable]`，实体与上下文均为非泛型顶级类型，仅调用 `JsonTypeInfo<T>` overload |
| 连接池 (WithPool) | PG/MySQL 连接串 builder | ✅ 同 | DbOptions 覆盖连接串内同名池参数；SQLite 自定义池参数明确失败 |
| 重试 (Resilience) | Provider 强类型瞬时异常判定 + 循环 | ✅ 同 | 不使用反射；确定性异常不重试 |

## 验证清单

AOT 部署前逐项确认：

- [ ] 对目标 AOT 宿主执行带项目路径、RID、`PublishAot=true`、`PublishTrimmed=true` 和关闭 STJ 反射的 publish，且无 IL/AOT 警告
- [ ] 原生二进制可启动并连接数据库（`PalORM AOT verification PASSED`）
- [ ] CRUD 与批量操作通过（Insert / Query / Update / Delete / BulkInsert / BulkDelete）
- [ ] 带 alignment 或 format specifier 的 FormattableString 仍按原对象绑定参数
- [ ] CTE / Window / StoredProc 复杂查询通过
- [ ] OwnedJson 序列化/反序列化通过
- [ ] PostgreSQL/MySQL 连接池参数已应用；SQLite 未配置自定义池参数
- [ ] QueryBuilder 读写分离 (ForRead/ForWrite) 在执行时租用读连接，事务与写操作走主连接
- [ ] DataSession 单消费者门禁覆盖直接 CRUD、QueryBuilder、Bulk、StoredProc 与 GridReader；重叠操作 fail-fast
- [ ] Dispose 等待活动命令、GridReader 和 WithTransaction scope；事务 callback 仅执行顺序数据库操作

## 裁剪与 AOT 警告处理

任何 IL2xxx、IL3xxx、裁剪或 AOT 警告都视为发布阻断。不得使用 `NoWarn` 或无范围 `#pragma warning disable` 隐藏诊断；应定位实际执行路径并改为源生成元数据。仅 `PublishAot=true` 成功不代表兼容，发布后的原生二进制还必须执行真实 CRUD、OwnedJson、并发和批量路径。

## 已知不兼容项

以下 PalORM API 在 AOT 下**不可用**：

| API | 原因 | 替代方案 |
|-----|------|---------|
| `QueryAsync<T>(string rawSql)` 未使用源生成 | 裸 SQL 需要运行时类型映射 | 使用 `FormattableString` 重载 |
| `ExecuteAsync(string rawSql)` 无类型参数 | 同上 | 使用 `FormattableString` 重载 |

### 聚合/标量查询的 AOT 注意事项（v4.0 补录）

`MaxAsync<T, TValue>` / `MinAsync<T, TValue>` / `ScalarAsync<T>` 通过 <xref:System.Convert.ChangeType%2A> 转换 DB 返回值。
`Convert.ChangeType(object, Type, IFormatProvider)` 本身 AOT 安全（走 IConvertible 接口分发，不依赖反射），
但 **TValue 必须是实现 IConvertible 的基元类型**：`int`/`long`/`decimal`/`double`/`float`/`string`/`DateTime` 等。

非 IConvertible 类型在 **JIT 与 AOT 下行为一致**——均抛 `InvalidCastException`：
- `Guid` / `DateOnly` / `TimeOnly`
- 枚举类型
- 自定义 struct（除非显式实现 IConvertible）

如需聚合这些类型，建议先 `MaxAsync<T, long>` 取基础类型，再在应用层显式构造目标类型。

## CI 集成

仓库的 `.github/workflows/ci.yml` 包含四条独立原生验证路径：

- SQLite Native AOT 发布与原生运行
- PostgreSQL 17 服务容器中的 Native AOT 发布与原生运行
- MySQL 8.4 服务容器中的 Native AOT 发布与原生运行
- 本地打包 SourceGen analyzer 后的 NuGet consumer Native AOT 发布与原生运行

PostgreSQL/MySQL job 未在 GitHub Actions 实际成功前，文档状态保持“CI 运行待验证”。
