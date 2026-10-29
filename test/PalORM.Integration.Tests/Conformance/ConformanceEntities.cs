using System.Text.Json.Serialization;

namespace PalORM.Integration.Tests.Conformance;

// 一致性套件的测试实体。形状编号与 docs/一致性测试方案-2026-10-29.md §4.6 对应。
// 全部表名带 cf_ 前缀：MigrateAsync 遍历全 Registry 建表，任何 DDL 错误都会炸掉
// Integration 项目所有测试的迁移，因此每个形状加进来后必须跑一次现有套件确认不破坏。

/// <summary>S01 纯标量：16 种映射类型（PALORM016 白名单）。</summary>
[Table("cf_scalar")]
public partial class CfScalarEntity
{
    [Key] public long Id { get; set; }
    [Column("v_int")] public int VInt { get; set; }
    [Column("v_short")] public short VShort { get; set; }
    [Column("v_byte")] public byte VByte { get; set; }
    [Column("v_string")] [Required] public string VString { get; set; } = "";
    [Column("v_char")] public char VChar { get; set; }
    [Column("v_bool")] public bool VBool { get; set; }
    [Column("v_decimal")] public decimal VDecimal { get; set; }
    [Column("v_double")] public double VDouble { get; set; }
    [Column("v_float")] public float VFloat { get; set; }
    [Column("v_datetime")] public DateTime VDateTime { get; set; }
    [Column("v_guid")] public Guid VGuid { get; set; }
    [Column("v_dto")] public DateTimeOffset VDto { get; set; }
    [Column("v_dateonly")] public DateOnly VDateOnly { get; set; }
    [Column("v_timeonly")] public TimeOnly VTimeOnly { get; set; }
    // CA1819 与既有实体同口径：ORM 列需要可变数组读写
#pragma warning disable CA1819
    [Column("v_bytes")] public byte[] VBytes { get; set; } = [];
#pragma warning restore CA1819
    [Column("v_nullable_int")] public int? VNullableInt { get; set; }
    [Column("v_nullable_timeonly")] public TimeOnly? VNullableTimeOnly { get; set; }
#pragma warning disable CA1819
    [Column("v_nullable_bytes")] public byte[]? VNullableBytes { get; set; }
#pragma warning restore CA1819
}

/// <summary>S02 软删：deleted_at 过滤。L4 默认过滤不变式的主形状。</summary>
[SoftDelete]
[Table("cf_soft_delete")]
public partial class CfSoftDeleteEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}

/// <summary>S03 租户：tenant_id 过滤与 SET 排除。</summary>
[TenantAware]
[Table("cf_tenant")]
public partial class CfTenantEntity
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("value")] public string Value { get; set; } = "";
}

/// <summary>S04 并发令牌：版本化更新与冲突检测。</summary>
[Table("cf_versioned")]
public partial class CfVersionedEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("version")] [ConcurrencyCheck] public long Version { get; set; }
}

/// <summary>S12 复合索引：DDL 与索引名长度。</summary>
[Table("cf_composite_index")]
[Index("ix_cf_composite_index_cat_price", "category", "price")]
public partial class CfCompositeIndexEntity
{
    [Key] public long Id { get; set; }
    [Column("category")] public string Category { get; set; } = "";
    [Column("price")] public decimal Price { get; set; }
    [Column("sku")] [Unique] public string Sku { get; set; } = "";
}

/// <summary>S14 计算列与默认值：GENERATED ALWAYS AS 与 DEFAULT 互斥面。
/// 表达式选纯算术，三方言语法一致（字符串拼接在 MySQL 需 CONCAT，会破坏跨方言建表）。</summary>
[Table("cf_computed")]
public partial class CfComputedEntity
{
    [Key] public long Id { get; set; }
    [Column("base_value")] public int BaseValue { get; set; }
    [Column("dflt_value")] [DefaultValue("7")] public int DfltValue { get; set; }
    [Column("calc_value")] [Computed("base_value + 1")] public int CalcValue { get; set; }
}

// ── S05 转换器与 S16 组合形状共用 ─────────────────────────

/// <summary>枚举存储形态（S08 字符串、S09 Int32、S10 Int64）。</summary>
public enum CfOrderStatus
{
    Pending = 0,
    Active = 1,
    Closed = 2
}

/// <summary>可逆转换器：往返后等于原值，便于断言两侧一致。
/// 若用有损转换（如统一大写），往返不等于输入，属于转换器语义本身，会掩盖映射缺陷。</summary>
public sealed class CfReversingStringConverter : IValueConverter<string, string>
{
    public string ToProvider(string value) => Reverse(value);

    public string FromProvider(string value) => Reverse(value);

    private static string Reverse(string value) => string.Concat(Enumerable.Reverse(value));
}

/// <summary>S05 转换器：IValueConverter 读写双路径。</summary>
[Table("cf_converter")]
public partial class CfConverterEntity
{
    [Key] public long Id { get; set; }
    [Column("code")] [Converter(typeof(CfReversingStringConverter))] public string Code { get; set; } = "";
}

/// <summary>S06 OwnedJson 对象的负载类型。</summary>
public sealed class CfJsonPayload
{
    public string Key { get; set; } = "";
    public int Count { get; set; }
}

[JsonSerializable(typeof(CfJsonPayload), TypeInfoPropertyName = "CfJsonPayloadInfo")]
internal sealed partial class CfJsonContext : JsonSerializerContext;

/// <summary>S06 OwnedJson 对象：STJ 源生成上下文序列化。</summary>
[Table("cf_owned_json_object")]
public partial class CfOwnedJsonObjectEntity
{
    [Key] public long Id { get; set; }
    [Column("payload")]
    [OwnedJson(typeof(CfJsonContext))]
    public CfJsonPayload Payload { get; set; } = new();
}

/// <summary>S07 OwnedJson 字符串：原始 JSON 列。</summary>
[Table("cf_owned_json_string")]
public partial class CfOwnedJsonStringEntity
{
    [Key] public long Id { get; set; }
    [Column("raw")] [OwnedJson] public string Raw { get; set; } = "{}";
}

/// <summary>S08 枚举 AsString：成员名存储与解析。</summary>
[Table("cf_enum_string")]
public partial class CfEnumStringEntity
{
    [Key] public long Id { get; set; }
    [Column("status", StoreAs = StoreAs.AsString)] public CfOrderStatus Status { get; set; }
}

/// <summary>S09 枚举 AsInt32：整数存储。</summary>
[Table("cf_enum_int32")]
public partial class CfEnumInt32Entity
{
    [Key] public long Id { get; set; }
    [Column("status", StoreAs = StoreAs.AsInt32)] public CfOrderStatus Status { get; set; }
}

/// <summary>S10 枚举 AsInt64：长整数存储。</summary>
[Table("cf_enum_int64")]
public partial class CfEnumInt64Entity
{
    [Key] public long Id { get; set; }
    [Column("status", StoreAs = StoreAs.AsInt64)] public CfOrderStatus Status { get; set; }
}

/// <summary>S11 继承的基类：列经基类链收集（源生成器口径 ITM-502/587）。</summary>
public abstract class CfAuditBase
{
    [Column("created_by")] public string CreatedBy { get; set; } = "";
}

/// <summary>S11 继承：基类列映射与声明序。</summary>
[Table("cf_inherited")]
public partial class CfInheritedEntity : CfAuditBase
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

/// <summary>S13 外键的父表。</summary>
[Table("cf_fk_parent")]
public partial class CfForeignKeyParent
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

/// <summary>S13 外键：表级 FK 与 OnDelete。
/// 被引用列名须与被引用实体的实际映射列名逐字对齐（PG 引号标识符大小写敏感）。</summary>
[Table("cf_fk_child")]
public partial class CfForeignKeyChild
{
    [Key] public long Id { get; set; }
    [Column("parent_id")]
    [ForeignKey("cf_fk_parent", "Id", OnDelete = DeleteAction.Cascade)]
    public long ParentId { get; set; }
}

/// <summary>S15 组合：软删加租户加并发令牌。三个过滤面与 SET 排除的交互。</summary>
[SoftDelete]
[TenantAware]
[Table("cf_combo_filter")]
public partial class CfComboFilterEntity
{
    [Key] public long Id { get; set; }
    [Column("tenant_id")] public long TenantId { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("version")] [ConcurrencyCheck] public long Version { get; set; }
    [Column("deleted_at")] public string? DeletedAt { get; set; }
}

/// <summary>S16 组合：转换器加 OwnedJson 加枚举 AsString，三条类型转换路径的交互。</summary>
[Table("cf_combo_types")]
public partial class CfComboTypesEntity
{
    [Key] public long Id { get; set; }
    [Column("code")] [Converter(typeof(CfReversingStringConverter))] public string Code { get; set; } = "";
    [Column("payload")]
    [OwnedJson(typeof(CfJsonContext))]
    public CfJsonPayload Payload { get; set; } = new();
    [Column("status", StoreAs = StoreAs.AsString)] public CfOrderStatus Status { get; set; }
}

/// <summary>时间矩阵探针实体：三方言的列类型与 DateTimeKind 往返实测（方案 §4.7 阶段 0a 第 4 项）。</summary>
[Table("cf_time_matrix")]
public partial class CfTimeMatrixEntity
{
    [Key] public long Id { get; set; }
    [Column("dt")] public DateTime Dt { get; set; }
    [Column("dto")] public DateTimeOffset Dto { get; set; }
    [Column("date_only")] public DateOnly DateOnly { get; set; }
    [Column("time_only")] public TimeOnly TimeOnly { get; set; }
}

/// <summary>批量族专用实体：无默认过滤、无并发令牌、显式主键。
/// <para><b>为什么无并发令牌</b>：<c>BulkUpdateBatchAsync</c> 对带 <c>[ConcurrencyCheck]</c> 的实体
/// 在 PG/MySQL 上抛 <c>NotSupportedException</c>（分析器 PALORM031 在编译期就拦下），SQLite 才回退逐行。</para>
/// <para><b>为什么显式主键</b>：实测 <c>BulkInsertAsync</c> 返回影响行数、不回填自增 Id（实体 Id 保持 0），
/// 按 Id 匹配的 BulkUpdate/BulkDelete 会静默 0 行受影响。批量对照必须让主键由测试赋值。</para></summary>
[Table("cf_bulk_plain")]
public partial class CfBulkPlainEntity
{
    [Key(AutoIncrement = false)] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("amount")] public long Amount { get; set; }
}