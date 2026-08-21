namespace PalORM.Benchmarks;

// ─── 基准实体（属性名 = 列名，不加 [Column] 别名——让 Dapper 和 PalORM 都按属性名映射）───

/// <summary>主基准实体：4 列（long/string/decimal/long），覆盖常见 CRUD 类型组合。</summary>
[Table("bench_orders")]
public sealed partial class BenchOrder
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("status")] public string status { get; set; } = "";
    [Column("total")] public decimal total { get; set; }
    [Column("created_at")] public long created_at { get; set; }
}

/// <summary>乐观锁基准实体：[ConcurrencyCheck] version 列。</summary>
[Table("bench_versioned")]
public sealed partial class BenchVersioned
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("version")] [ConcurrencyCheck] public long version { get; set; }
}

/// <summary>软删除基准实体：[SoftDelete] + deleted_at 列。</summary>
[SoftDelete]
[Table("bench_soft")]
public sealed partial class BenchSoft
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("deleted_at")] public string? deleted_at { get; set; }
}

/// <summary>v5.0 GC 装箱基准实体：4 个值类型列（long/int/decimal/bool）+ 1 引用类型（string）。
/// 每行装箱精确值：long 32B + int 24B + decimal 48B + bool 24B = 128B（含对象头 + 对齐填充）。</summary>
[Table("boxing_test")]
public sealed partial class BoxingTestEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("value")] public int Value { get; set; }
    [Column("price")] public decimal Price { get; set; }
    [Column("active")] public bool Active { get; set; }
}

/// <summary>二进制基准实体：payload 走原生 BLOB（byte[] 白名单放行后）。</summary>
[Table("bench_binary")]
public sealed partial class BenchBinary
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    // CA1819 误报：ORM 实体列需要可变数组读写
#pragma warning disable CA1819
    [Column("payload")] public byte[] payload { get; set; } = [];
#pragma warning restore CA1819
}

/// <summary>Base64 文本对照实体：旧行为形态（TEXT 列 + 手工编解码），用于量化原生 BLOB 收益。</summary>
[Table("bench_binary_text")]
public sealed partial class BenchBinaryText
{
    [Key] [Column("id")] public long id { get; set; }
    [Column("name")] public string name { get; set; } = "";
    [Column("payload")] public string payload { get; set; } = "";
}
