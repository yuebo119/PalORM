# 配方：pgvector 向量搜索（Raw 手写 SQL 路线）

> 状态：**配方文档（门结论 B 分支交付物，2026-09-30）** · 前置裁决：`docs/adr/` 向量决策门（2026-09-28，见 `docs/v6.0-vector-search-design.md` 刷新记录）——向量不进产品面，本配方按门结论"Raw 手写配方文档化"交付。
> **诚实声明**：本仓探针实锤本地 PG 18.4 无 pgvector 扩展（`pg_available_extensions` 零行），**本文 DDL/SQL 形态基于 pgvector 官方文档，未经本仓真库实证**；首次使用时按 §四核对清单验证。

## 一、前置条件（服务器侧，非连接配置）

1. PG 服务器安装 pgvector 扩展包（apt/yum 装包后 `CREATE EXTENSION vector;`）——本仓探针证实扩展包缺失时无从启用。
2. Npgsql 驱动**不需要** Pgvector.Npgsql 包——本配方全程把向量当字符串字面量进出（门结论：方案 A 用户预初始化 NpgsqlDataSourceBuilder 架构性不可行，ADR-E 已裁决不做 DbDataSource 单例化）。

## 二、列定义（[Column(TypeName=...)] 直通，R3 既有能力）

```csharp
[Table("docs")]
public partial class Doc
{
    [Key] public long Id { get; set; }
    [Column("content")] public string Content { get; set; } = "";
    // TypeName 直通进 DDL——vector(3) 是 pgvector 类型，维度必须与写入向量一致
    [Column("embedding", TypeName = "vector(3)")]
    public string Embedding { get; set; } = "";   // 文本形态 "[1,2,3]"
}
```

- 写侧：`Embedding = "[0.1,0.2,0.3]"`——pgvector 接受文本输入隐式 cast 到 vector。
- 读侧：`Embedding` 回读为字符串，解析由用户代码负责（`[1,2,3]` 切分 float）。
- AOT：全程字符串直通，零反射、零运行时代码生成，天然 AOT 安全。

## 三、KNN 检索（Raw SQL，参数化）

```csharp
// 距离运算符三选一：<->（L2）/ <=>（余弦）/ <#>（内积负数）。
// 物化目标用 [Projection] sealed class（v6.0 R1 能力，ordinal 契约同 ADR-A；元组不是合法物化目标）
[Projection]
public sealed partial class DocHit
{
    public long Id { get; set; }
    public string Content { get; set; } = "";
    public double Dist { get; set; }
}

var rows = await db.QueryAsync<DocHit>(
    $"""
    SELECT "Id", "Content", "Embedding" <=> {@queryVector}::vector AS "Dist"
    FROM "docs"
    ORDER BY "Embedding" <=> {@queryVector}::vector
    LIMIT {@topK}
    """);
// queryVector 形态："[0.1,0.2,0.3]"（与列定义同维度）
```

**HNSW 索引**（性能路径，手写 DDL）：

```sql
CREATE INDEX IF NOT EXISTS ix_docs_embedding
  ON docs USING hnsw (embedding vector_cosine_ops);
-- 运算符类须与查询距离运算符匹配：<-> 用 vector_l2_ops，<=> 用 vector_cosine_ops，<#> 用 vector_ip_ops
```

## 四、首次使用核对清单（未实证项逐条验证）

| # | 项 | 验证方式 |
|---|----|---------|
| 1 | `[Column(TypeName = "vector(3)")]` 直通产出合法列定义 | 建表 + `\d docs` 看列型 |
| 2 | 文本形态 INSERT 参数化隐式 cast（非显式 `::vector` 时） | 插一行 `SELECT embedding FROM docs` 核值 |
| 3 | `NpgsqlDbType` 推断对 string 参数 + `::vector` 显式 cast 的兼容性 | §三查询原样跑通 |
| 4 | HNSW 索引建立时间与召回（数据量 >1k 才有意义） | `EXPLAIN ANALYZE` 对比有无索引 |
| 5 | SET NULL/事务/COPY 等路径对 vector 列的兼容性 | 复用现有 Integration 夹具形态跑一轮 |

## 五、升级路径

若真实用户请求出现且配方验证通过，产品化走 **[Converter] 字符串路径**（门结论推断的可行路径：`IValueConverter<float[], string>` 双向转换 + 既有 Converter 基础设施，AOT 天然安全）——届时本文档作为实现规格输入。
