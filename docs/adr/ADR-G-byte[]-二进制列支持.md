# ADR-G：byte[] 二进制列原生支持

> 状态：已实施（2026-08-21/22 批准并实施）· 来源：消费方 payload 字段诉求 + 驱动层 PoC 实证

## 背景

PALORM016 白名单长期拒绝 `byte[]`：`IsSupportedProviderType` 对所有 `IArrayTypeSymbol` 返回 false。
v4.4 曾在 `RowFactoryEmitter` 预铺 `GetFieldValue<byte[]>` 读取分支，但实体管道（`CanGenerateEntity`
全属性校验）使其不可达。消费方以 `[Converter]` 出口 `string`（Base64 TEXT）绕行，付出 33% 存储
膨胀 + 编解码 CPU + 64KB 档位跨越 LOH 阈值（85000 字节）的 GC 代价。

驱动层 PoC（MySQL 8.4.11 + PG 18.4 + MySqlConnector 2.6.2 + Npgsql 10.0.3）实证：
- 三驱动全部写入路径（参数化 INSERT / MySQL LOAD DATA BulkCopy / PG Binary COPY）对 byte[]
  逐字节往返一致，含 0x00 字节与 1MB 载荷——瓶颈从来不是驱动，是框架 DDL 契约缺口；
- 天真放行（只开白名单）后果：DDL 兜底 TEXT，PG 静默变形为 hex 文本（`'\x0001ff'`）、
  MySQL strict mode 报 1366——这是白名单"可证明才放行"原则要杜绝的事故类别。

## 选项

| 选项 | 内容 | 工作量 | 风险 |
|------|------|--------|------|
| G1 维持拒绝 | 消费方继续 Base64 Converter | 零 | 33% 膨胀 + 编解码成本永久化；Scaffold 的 bytea→byte[] 反向工程"生成即不可用" |
| G2 整类放行数组 | 删除 IArrayTypeSymbol 拒绝 | 小 | int[]/string[] 漏入，回到"运行时才炸"；TEXT 兜底事故面扩大 |
| G3 收窄放行 byte[] + 四环节契约 | 白名单仅放行 Byte 元素一维数组 + GetBinaryDbType 三方言 + 测试级联 | 中 | 需同步快照/文档/反例测试 |
| G4 捆绑 TimeSpan | 同轮放行两个同构缺口类型 | 中+ | TimeSpan 的 DDL 方言分歧（PG interval vs MySQL TIME）与 byte[] 不同构，需独立设计 |

## 决策

**G3**。要点：

1. **白名单判据**：`IArrayTypeSymbol` 且 `ElementType.SpecialType == System_Byte && Rank == 1`——
   `byte[][]`、多维数组、其他元素类型数组仍被 PALORM016 拒绝。
2. **DDL 映射**（`MigrationEmitter.GetBinaryDbType`）：PG `BYTEA` / SQLite `BLOB` /
   MySQL 数据列 `LONGBLOB`、主键与索引列 `VARBINARY(255)`。
   - LONGBLOB 而非 MEDIUMBLOB：避免 16MB 静默截断面，对齐 Pomelo/EF Core 惯例；
   - VARBINARY(255)：MySQL 对 BLOB 建索引必须前缀长度（错误 1170，ITM-566 同型约束）。
3. **参数绑定显式化**：生成 binder 对 byte[] provider 列发射 `DbType.Binary`——
   PG COPY 经 `NpgsqlParameter.DbType → NpgsqlDbType.Bytea` 显式分派，不依赖驱动运行时推断。
4. **TimeSpan 不捆绑**（G4 拒绝）：其 DDL 需在 PG interval / MySQL TIME（可超 24h 时长语义）/
   SQLite TEXT 间做语义决策，独立 ADR 另议。

## 后果

- 消费方 `byte[]` 属性声明即得三方言一致 CRUD；CA1819 为 ORM 列误报，定点 pragma（PALORM016
  消息已内置提示）；
- 既有 Base64 TEXT 存量不受影响（Converter 路径保留），迁移为应用侧一次性脚本（decode 回填）；
- 明确放弃：BLOB 档位配置（无消费者的投机抽象）、流式读取（全行业通病，正解是对象存储）、
  PG lo 大对象协议（PG 独有，破坏三方言统一）；
- 锁定测试：`ByteArrayColumns_GenerateCrudAndBlobDdl`、`UnsupportedPortableProviderTypes`
  （int[]/byte[,] 反例）、AllTypes/BulkBinary/ExtBulk 真库往返、Scaffold 反向工程端到端、
  AotTest 原生二进制含 0x00 等值参数化与物化往返。
