# ADR-G：OwnedJson 读路径方言条件 Span 化

> 状态：**已决策：维持 G1（不做）**（2026-09-27）· 来源：step7 任务清单 T14 · 关联：发布回滚 0d15da5（run 36223824623）

## 背景

v5.7.0 曾把对象型 OwnedJson 的读路径从 `GetString` + `JsonSerializer.Deserialize(string)` 改为 `GetFieldValue<byte[]>` + `Deserialize(ReadOnlySpan<byte>)`（消每行 UTF-16 JSON string 分配与双重转码），三方言统一 emit。发布 run 36223824623 被 CI MySQL Native AOT job 抓住：**MySqlConnector 对 TEXT 列抛 `InvalidCastException`**（Microsoft.Data.Sqlite 的 TEXT 列返回 byte[] 成立），回滚为 GetString 通用形态（0d15da5），并补 PG/MySQL OwnedJson round trip 测试钉死三方言行为。

## 现状

RowFactory 是**方言无关**生成物：emitter 拿不到（也不拿）实体注册在哪个 Provider，无法按方言条件 emit。当前所有方言的对象型 OwnedJson 读路径 = `r.GetString(ordinal)` + 反序列化（RowFactoryEmitter.cs:142）。PG 侧 jsonb 列经 Npgsql 的 `GetFieldValue<byte[]>` 理论上可行（探针四已证 Npgsql 的 byte[] 路径与 DBNull+显式 DbType 行为健康）。

## 选项

| 选项 | 机制 | 优点 | 缺点 |
|------|------|------|
| G1 维持现状 | GetString 通用形态 | 三方言一致、零新面 | PG 用户继续付每行 UTF-16 转码 |
| G2 SourceGen 方言感知化 | emitter 按实体注册的 Provider 分支 emit（需要方言信息进入 TableModel） | 根治；其他方言差异优化也能受益 | 生成器核心模型变更；方言信息从注册表（编译期常量）传递，需验证不破坏增量管线 |
| G3 PG-only 运行时旁路 | RowFactory 保持 GetString，但在 PG 的读管线入口对含 OwnedJson 列的实体换 typed reader | 不动生成器 | 读路径分叉成两条，物化一致性风险（v5.7 回滚的同款风险换位置再犯） |
| G4 驱动能力探测 | 运行时试 `GetFieldValue<byte[]>` 失败回退 GetString | 无需编译期方言 | 每列一次的探测成本；失败态在中间行出现则语义分裂（部分 byte[]/部分 string） |

## 推荐

**G2（SourceGen 方言感知化）**，但列为待决策而非直接实施：收益面（仅对象型 OwnedJson 列 + 仅 PG）需要**先补 OwnedJson 进 PerfHub 夹具**才能测量（0d15da5 的教训：无基准覆盖的优化无法证伪也无法验收）。成本面是生成器核心模型变更，需单独立项与三方言矩阵实测（MySQL TEXT 抛异常的驱动行为差异已实证过一次）。

## 待用户决策

1. 是否批准 G2 方向（SourceGen 方言感知化）？若批准，先立"OwnedJson PerfHub 夹具"子任务再动 emitter。
2. 维持 G1 也是可接受决策：jsonb 读路径的 UTF-16 转码成本未被测量，可能本就低于直觉（Npgsql 的 GetString 内部也是 byte[]→string 单次转码）。

## 决策（2026-09-27：维持 G1）

**裁决依据来自 T16 新夹具族的实测**（`bench/PalORM.PerfHub` 的 `OwnedJsonQuery`/`TenantGetAll`
两操作，PG 20000 档，三臂）：

| 形态 | PalORM | 裸 ADO | Dapper |
|---|---|---|---|
| TenantGetAll（5000 行含 OwnedJson 反序列化，Span 化的目标形态） | **7.57ms / 1030KB**（时延与分配双优） | 9.86ms / 1035KB | 7.92ms / 1717KB |
| OwnedJsonQuery（LIMIT 50，小结果集） | 0.46ms（+21% vs ADO） | **0.38ms** | 0.47ms |

- Span 化要消除的“每行 UTF-16 string 分配 + 双重转码”只在**大结果集**显著——而那里 PalORM
  已经时延分配双优，收益是“领先者再快一点”的上界。
- 小结果集的 +21% 差距是 RTT 主导的固定开销，Span 化（物化侧优化）治不了。
- G2 成本侧：SourceGen 方言感知化 = 生成器核心模型变更 + MySqlConnector TEXT 列
  `InvalidCastException` 前科（0d15da5）需三方言矩阵重验。收益侧证据走弱后，投入产出不成立。

**重评触发条件**（满足其一重开本 ADR）：① 出现“大 OwnedJson 结果集上 PalORM 时延或分配
输给 Dapper/ADO”的夹具读数；② SourceGen 方言感知化因其他需求先行落地（成本已被摊销）。
