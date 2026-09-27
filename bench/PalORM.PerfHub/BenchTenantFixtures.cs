using PalORM;
using System.Text.Json.Serialization;

namespace PalORM.PerfHub;

/// <summary>租户会话夹具实体——<c>[TenantAware]</c> + <c>[SoftDelete]</c> + <c>[OwnedJson]</c>
/// 三特性组合路径的测量载体（覆盖面补齐批次新增）。
/// <para>列名 <c>tenant_id</c>/<c>deleted_at</c> 是产品默认过滤的硬编码契约
/// （DataSession.GetDefaultFilterForms 与 SourceGen PALORM014/018/040 诊断），不是可自由
/// 命名的列——用别的列名编译期即报错。</para>
/// <para>OwnedJson 范式照 test/PalORM.AotTest.Pg/Program.cs 的 AotPgJsonEntity：
/// 对象载荷必须指定 STJ 源生成上下文。</para></summary>
[Table("bench_tenant")]
[SoftDelete]
[TenantAware]
internal sealed partial class BenchTenantPost
{
    /// <inheritdoc/>
    [Key(AutoIncrement = false)]
    [Column("Id")]
    public int Id { get; set; }

    /// <summary>租户列——[TenantAware] 契约列（产品过滤 SQL 引用 tenant_id）。
    /// [Required] 是 PALORM040 的强制项：租户隔离完全靠该列值承载，可空列让跨租户数据可见。</summary>
    [Column("tenant_id")]
    [Required]
    public long TenantId { get; set; }

    /// <summary>可变长文本列（可空）。</summary>
    [Column("Text")]
    public string? Text { get; set; }

    /// <summary>时间列——三方言 TIMESTAMP / DATETIME(6) / TEXT 同构。</summary>
    [Column("CreationDate")]
    public DateTime CreationDate { get; set; }

    /// <summary>整型过滤列——TenantCountWhere 范围条件的右值面。</summary>
    [Column("Value")]
    public int Value { get; set; }

    /// <summary>软删列——[SoftDelete] 契约列（产品过滤 SQL 引用 deleted_at IS NULL）。</summary>
    [Column("deleted_at")]
    public string? DeletedAt { get; set; }

    /// <summary>OwnedJson 列——STJ 源生成上下文序列化（AOT 友好形态）。</summary>
    [Column("Payload")]
    [OwnedJson(typeof(BenchTenantJsonContext))]
    public BenchTenantPayload? Payload { get; set; }
}

/// <summary>OwnedJson 载荷——两个确定性字段（A = 行号，B = 行号前缀文本）。</summary>
internal sealed class BenchTenantPayload
{
    public int A { get; set; }
    public string? B { get; set; }
}

/// <summary>BenchTenantPayload 的 STJ 源生成上下文——OwnedJson emit 的序列化载体。</summary>
[JsonSerializable(typeof(BenchTenantPayload), TypeInfoPropertyName = "BenchTenantPayloadInfo")]
internal sealed partial class BenchTenantJsonContext : JsonSerializerContext;
