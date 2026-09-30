# 配方：pgvector 向量搜索（全文本边界 Raw 路线 v2）

> 状态：**配方文档 v2（2026-09-30 论证轮重写）** · 前置裁决：向量决策门（2026-09-28，见
> `docs/v6.0-vector-search-design.md` 刷新记录与同日勘误）——向量不进产品面，本配方是门结论
> "Raw 手写配方文档化"的交付物。
> **v2 与 v1 的差别**：v1 的实体含 vector 列 + InsertAsync 写入形态经官方文档查证**确定不可行**
> （两大铁证见 §六）；v2 重构为"实体不含 vector 列 + SQL 边界全文本 cast"，把 v1 的两处运行期
> 必炸点变为设计上不可能。所有 DDL/SQL 形态仍**未经本仓真库实证**（本地 PG 无 pgvector 为门探针
> 实锤），首用必须过 §五核对清单。

## 〇、方案空间与选型论证（为什么是这个形态）

| 候选 | 结论 | 一句话依据 |
|------|------|-----------|
| A. 实体含 vector 列（v1 形态） | **否** | InsertAsync/UpdateAsync 生成 SQL 无 cast 注入点，text 参数直写 vector 列撞 explicit-only cast（§六·铁证1）；QueryAsync 生成的 SELECT 列集含 embedding，裸读 vector 列 Npgsql 端失败（§六·铁证2）——写读双路全断 |
| B. **实体不含 vector 列 + Raw 补列 + 全文本边界**（本文） | **采纳** | ORM CRUD 只碰标量列（零 cast 需求）；vector 列由 Raw DDL 补建、写入走 `::vector` 显式 cast、读出走 `::text`——Npgsql 全程只见 text，两大铁证被形态性绕开 |
| C. Converter 产品化（门结论原推断） | **降级** | Converter 只改变 .NET 侧类型映射，生成的 INSERT 参数仍是 text 直写（无 cast 点）——铁证1 原样命中；可行形态实为"生成器对 vector TypeName 列注入 cast"，属生成器专门迭代（已订正入 vector-design.md 勘误） |
| D. 混合双栈（PalORM 管标量表 + 原生 Npgsql+Pgvector.Npgsql 管向量表） | **否** | Pgvector.Npgsql 注册必须经 NpgsqlDataSourceBuilder（ADR-E 已裁决不做单例化）；且引入第二套连接管理 |
| E. 产品化 vector 列支持 | **否（维持门裁决）** | 零真实用户请求（B28 四问之④）；形态 C 的教训表明正确实现是生成器 cast 注入，成本高于预期，等需求触发 |

## 一、前置条件（服务器侧，非连接配置）

1. PG 服务器安装 pgvector 扩展包（apt/yum 装包后 `CREATE EXTENSION vector;`）——本仓探针实锤
   扩展包缺失时无从启用（`pg_available_extensions` 零行）。
2. 不需要 Pgvector.Npgsql 包；不调用 `EnableUnmappedTypes()`（标注 `RequiresDynamicCode`，AOT 禁用）。

## 二、表结构：实体只声明标量列，vector 列 Raw 补建

```csharp
// 实体刻意不声明 embedding——ORM 生成的 INSERT/SELECT/UPDATE 全部只含标量列，
// InsertAsync/UpdateAsync/QueryAsync 原样可用，与 vector 的 cast 语义零交集
[Table("docs")]
public partial class Doc
{
    [Key] public long Id { get; set; }
    [Column("content")] public string Content { get; set; } = "";
}
```

```sql
-- MigrateAsync 建出标量表后，Raw 补列（PG 支持 ADD COLUMN IF NOT EXISTS，幂等）
ALTER TABLE "docs" ADD COLUMN IF NOT EXISTS "embedding" vector(3);
-- HNSW 索引（可选，数据量 >1k 才有意义；运算符类必须与查询距离运算符匹配，见 §四）
CREATE INDEX IF NOT EXISTS "ix_docs_embedding"
  ON "docs" USING hnsw ("embedding" vector_cosine_ops);
```

执行点：`MigrateAsync()` 之后 `ExecuteAsync` 这两条（幂等，每次会话启动跑无副作用）。

## 三、写入：显式 ::vector（Raw，不用 InsertAsync）

```csharp
// 铁证1：string→用户类型的自动 cast 是 explicit-only，裸参数必败。
// {@vec} 被 PalORM 参数化为 @p0（text），PG 端 ::vector 显式 cast 完成转换
await db.ExecuteAsync(
    $"INSERT INTO \"docs\" (\"content\", \"embedding\") VALUES ({@content}, {@vec}::vector)");
```

**向量文本的生成与早校验**（消灭"运行期才炸"的维度/格式错误）：

```csharp
internal static class VectorText
{
    public static string Of(ReadOnlySpan<float> values, int expectedDim)
    {
        if (values.Length != expectedDim)
            throw new ArgumentException($"向量维度 {values.Length} ≠ 列定义 {expectedDim}");
        var sb = new System.Text.StringBuilder(values.Length * 8).Append('[');
        for (var i = 0; i < values.Length; i++)
            sb.Append(values[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture))
              .Append(i < values.Length - 1 ? ',' : ']');
        return sb.ToString();   // 形如 "[0.1,0.2,0.3]"，恒定 InvariantCulture 防区域小数点
    }
}
// 用法：var vec = VectorText.Of(stackalloc float[] { 0.1f, 0.2f, 0.3f }, expectedDim: 3);
```

## 四、检索：KNN Raw + [Projection] 物化 + 读侧永不裸读 vector 列

```csharp
[Projection]
public sealed partial class DocHit
{
    public long Id { get; set; }
    public string Content { get; set; } = "";
    public double Dist { get; set; }     // 距离是标量，Npgsql 原生认得
}

// 距离运算符 ↔ HNSW 运算符类必须成对（错配不报错、静默全表扫）：
//   <->（L2）↔ vector_l2_ops · <=>（余弦）↔ vector_cosine_ops · <#>（内积）↔ vector_ip_ops
var hits = await db.QueryAsync<DocHit>(
    $"""
    SELECT "Id", "Content", "embedding" <=> {@vec}::vector AS "Dist"
    FROM "docs"
    WHERE "DeletedAt" IS NULL          -- 默认过滤必须手写（Raw SQL 不经过软删/租户管线，漏写 = 数据泄漏面）
      ORDER BY "embedding" <=> {@vec}::vector
    LIMIT {@topK}
    """);
```

**需要回看向量本体时**（极少见），显式 `::text` 转出后自行解析：

```csharp
// SELECT "embedding"::text AS "Embedding" ... —— Npgsql 收到的是 text 列，确定性返回 string
```

## 五、首用核对清单（未实证项逐条验证，全过才算配方成立）

| # | 项 | 验证方式 | 依据状态 |
|---|----|---------|---------|
| 1 | `ALTER ... ADD COLUMN IF NOT EXISTS ... vector(3)` 合法 | PG 15+ 语法，`\d docs` 看列型 | 文档形态，未实测 |
| 2 | `@p0::vector` 参数化 cast（text 参数 + 显式 cast）写入成功 | 插一行 `SELECT embedding::text` 核值 | 铁证1 推出的必用形态，未实测 |
| 3 | HNSW 索引建立 + 运算符类匹配走索引 | `EXPLAIN ANALYZE` 确认 Index Scan（错配是静默全表扫） | 强制步骤 |
| 4 | `[Projection]` 物化 KNN 结果集（含 double 距离列） | 本配方查询原样跑 | R1 既有能力，低风险 |
| 5 | 事务/COPY 等路径对 vector 列的兼容性 | 复用 Integration 夹具形态跑一轮 | 未测 |
| 6 | PalORM 生成 SQL 与 Raw 补列共存（实体迁移幂等 + ADD COLUMN IF NOT EXISTS 幂等） | 连续两次会话启动 | 形态设计目标，未实测 |

## 六、两大铁证（本配方形态的推导依据，2026-09-30 官方文档查证）

1. **PG `CREATE CAST` 文档**：自动 I/O cast 中，"到 string 类型"是 assignment 级（vector→text 直读
   理论可转），但"**从 string 类型**"（text→vector）是 **explicit-only**——赋值上下文（INSERT VALUES）
   不接受，故任何无 cast 点的生成 SQL 写入 vector 列必败（`42883`/类型不匹配族）。
2. **Npgsql**：官方类型映射表无 vector（tsvector 是全文检索向量，非 pgvector）；`EnableUnmappedTypes()`
   标注 `[RequiresDynamicCode]`，与 NativeAOT 不兼容——裸读 vector 列在 Npgsql 端失败且无 AOT 安全
   的开启项。**结论：SQL 边界全文本（写 ::vector / 读 ::text）是 AOT 安全的唯一形态。**

## 七、升级路径

若真实用户请求出现且核对清单全过，产品化正确形态是**生成器 cast 注入**（对 `TypeName` 为 vector 的
列，INSERT 参数后追加 `::vector`、SELECT 侧包装 `::text`）——不是门结论原推断的 Converter（已勘误，
见 vector-design.md）。本配方即该生成器迭代的实现规格与验证场。
