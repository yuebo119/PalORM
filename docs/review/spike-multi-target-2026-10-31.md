# Spike：多目标框架可行性（net10.0）

- 日期：2026-10-31
- 结论：net10.0 可行；Core 构建 0 警告 0 错误，Core 测试 700/700 通过
- 定位：验证性 spike，不合并主分支

## 方法

在独立 worktree `C:\ai\claude\PalORM-net10-spike`（基于 `ed8d9a1`）上做最小改动，使用系统 .NET SDK 10.0.403：

| 改动 | 内容 |
|------|------|
| 目标框架 | `Directory.Build.props` 的 `TargetFramework` 改为 `net10.0` |
| runtime-async | 删除 `<Features>runtime-async=on</Features>` |
| .NET 11 API | `Type.GetNullableUnderlyingType()` 两处改为 `Nullable.GetUnderlyingType(typeof(T))` |
| 日志抽象 | Core 增加 `Microsoft.Extensions.Logging.Abstractions` 10.0.13 包引用 |
| 测试兼容 | 三个测试文件把 `StartsWith/EndsWith/Contains/IndexOf/LastIndexOf` 的 `char + StringComparison` 重载改为 string 重载；测试项目 NoWarn CA1865/CA1847 |

## 结果

| 项目 | 结果 |
|------|------|
| `PalORM.Core` net10.0 | 构建成功，0 警告 0 错误 |
| `PalORM.Core.Tests` net10.0 | 构建成功，0 警告 0 错误 |
| Core 测试 | 700/700 通过，0 失败 |

## 结论与估算

1. Core 运行时对 net10 的依赖面很小：只有 `Type.GetNullableUnderlyingType` 与 `runtime-async` 两个开关，`Lock`、`FrozenDictionary`、`DbBatch`、`GeneratedRegex` 在 net10 均可用。
2. 主要成本在测试代码的 .NET 11 string 重载与新分析器规则（CA1865/CA1847），属于机械修改，不是架构阻塞。
3. net10 多目标估算 1 至 2 天；net8 多目标需要额外处理 `System.Threading.Lock`（net9 起可用）、日志包与更多测试 API，估算 1 周。
4. 建议保持 net11 为发布主目标；net10 可作为第一个兼容目标，在独立分支验证后按需发布。
